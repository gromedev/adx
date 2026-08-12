using System.Linq;
using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxPrincipalGroupMembership: drop-in replacement for RSAT's Get-ADPrincipalGroupMembership.
/// The reverse of Get-ADxGroupMember -- given a user, computer, group or service account, return
/// the groups it belongs to.
/// <para>
/// Enumerates by searching each group's <c>member</c> attribute rather than reading the
/// principal's <c>memberOf</c> DN-by-DN, so it is immune to MaxValRange, and OR's in the
/// PRIMARY group by SID -- the one membership (Domain Users for an ordinary account) that lives
/// only in <c>primaryGroupID</c> and appears in neither <c>member</c> nor <c>memberOf</c>. That
/// is exactly what RSAT's Get-ADPrincipalGroupMembership returns and the reason this cmdlet
/// exists rather than a one-line memberOf read.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxPrincipalGroupMembership")]
[OutputType(typeof(PSObject))]
public sealed class GetADxPrincipalGroupMembership : ADxMembershipQueryCmdletBase
{
    /// <summary>The principal: DN, objectGUID (D/N), SID, or sAMAccountName.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public object? Identity { get; set; }

    // memberOf rides along on the resolution read (no extra round trip) purely to detect
    // memberships in other partitions the single-partition member search cannot return -- see
    // WarnOnForeignPartitionMemberships.
    private static readonly string[] PrincipalAttributes =
        { "distinguishedName", "objectSid", "objectClass", "primaryGroupID", "memberOf" };

    protected override void ProcessRecord()
    {
        try
        {
            var principal = ResolvePrincipal();
            if (principal is null)
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Cannot find a principal with identity '{Identity}'."),
                    "ADxObjectNotFound", ErrorCategory.ObjectNotFound, Identity));
                return;
            }

            // The primary group's SID is the principal's own account-domain SID with the RID
            // swapped for primaryGroupID. Without a readable SID the arm is dropped and the
            // primary group is warned about, not silently omitted -- Domain Users is the single
            // most common group a caller would miss.
            var primaryGroupSid = ComputePrimaryGroupSid(principal);
            if (primaryGroupSid is null)
                WriteWarning(
                    $"'{principal.DistinguishedName}' has no readable objectSid/primaryGroupID; its PRIMARY " +
                    "group (Domain Users for an ordinary account) cannot be included.");

            WarnOnForeignPartitionMemberships(principal);

            var filter = AdGroupMemberQuery.PrincipalGroups(principal.DistinguishedName, primaryGroupSid);
            EmitQueryResults(filter, AdObjectSchema.Group);
        }
        catch (AdFilterTranslationException ex)
        {
            DrainMessages();
            ThrowTerminatingError(new ErrorRecord(
                ex, "ADxFilterTranslation", ErrorCategory.InvalidArgument, Identity));
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            DrainMessages();
            WriteWarning("Search cancelled.");
        }
        catch (PipelineStoppedException)
        {
            throw;
        }
        catch (Exception ex) when (WriteLdapError(ex, Identity)) { }
    }

    /// <summary>
    /// Resolve -Identity to a security principal (DN + SID + primaryGroupID + memberOf). DN
    /// identities are a single base-scope read; everything else is a subtree search. No object
    /// class constrains a principal -- user, computer, group and service account all qualify --
    /// so the only type gate is "carries an objectSid".
    /// </summary>
    private LdapEntry? ResolvePrincipal()
    {
        var (kind, value) = AdIdentityResolver.Classify(Identity!, AdObjectSchema.SecurityPrincipal);

        if (kind == AdIdentityKind.DistinguishedName)
        {
            var entry = GetConnection()
                .ReadEntryAsync((string)value, PrincipalAttributes, CancellationToken)
                .GetAwaiter().GetResult();
            DrainMessages();
            // Any object is readable by DN; only one with a SID is a principal that can hold
            // group memberships. Reject the rest as not-found, matching RSAT.
            if (entry is null || entry.GetBytes("objectSid") is null) return null;
            return entry;
        }

        var lookup = AdIdentityResolver.BuildLookupFilter(kind, value);
        var spec = new LdapSearchSpec(
            DefaultNamingContext(),
            AdFilterEmitter.Emit(lookup),
            PrincipalAttributes,
            LdapScope.Subtree,
            // Two, so ambiguity is detectable rather than resolved by arbitrary pick.
            PageSize: 2,
            SizeLimit: 0);

        var page = GetConnection()
            .SearchPageAsync(spec, cookie: null, CancellationToken)
            .GetAwaiter().GetResult();
        DrainMessages();

        if (page.Entries.Count == 0) return null;

        if (page.Entries.Count > 1)
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"-Identity '{Identity}' matched {page.Entries.Count} or more objects (for example " +
                    $"'{page.Entries[0].DistinguishedName}' and '{page.Entries[1].DistinguishedName}'). " +
                    "Use a distinguished name, objectGUID, or SID."),
                "ADxIdentityAmbiguous", ErrorCategory.InvalidArgument, Identity));

        return page.Entries[0];
    }

    private static byte[]? ComputePrimaryGroupSid(LdapEntry principal)
    {
        var domainSid = LdapConvert.SidDomain(principal.GetBytes("objectSid"));
        var rid = principal.GetInt32("primaryGroupID");
        if (domainSid is null || rid is null) return null;
        return LdapConvert.SddlToSid($"{domainSid}-{rid}");
    }

    /// <summary>
    /// Group memberships are enumerated by a <c>member</c> search under one domain partition,
    /// which cannot return a membership stored in another partition of the forest: a
    /// cross-domain membership lives as a forward <c>member</c> link in the GROUP's partition
    /// with no <c>memberOf</c> back-link maintained on this principal locally, so a
    /// single-partition search is structurally blind to it. The one place such memberships DO
    /// surface on the principal is its own <c>memberOf</c> when read against a Global Catalog
    /// (universal groups replicate forest-wide), so <c>memberOf</c> rides along on resolution
    /// and any entry whose domain NC differs from the searched partition is named in a warning
    /// rather than silently dropped. Positive-detection only: an ordinary domain-DC query,
    /// where every memberOf entry is local, warns not at all.
    /// </summary>
    private void WarnOnForeignPartitionMemberships(LdapEntry principal)
    {
        if (!principal.TryGetRanged("memberOf", out var groups, out _, out _, out _) || groups.Count == 0)
            return;

        var searchNc = DefaultNamingContext();
        var gc = IsGlobalCatalog;

        var foreign = groups
            .Where(dn => IsGenuinelyExcluded(LdapConvert.DomainNamingContext(dn), searchNc, gc))
            .ToList();
        if (foreign.Count == 0) return;

        var sample = string.Join("; ", foreign.Take(3));
        var more = foreign.Count > 3 ? $" (and {foreign.Count - 3} more)" : string.Empty;
        WriteWarning(
            $"'{principal.DistinguishedName}' is a memberOf {foreign.Count} group(s) in other domain " +
            $"partitions of the forest: {sample}{more}. Group membership is enumerated within one " +
            $"partition ({searchNc}), so these are NOT included in the results. Query each group's own " +
            "domain, or read the principal's memberOf directly, to see them.");
    }

    /// <summary>
    /// Whether a membership in the partition <paramref name="memberNc"/> is genuinely absent
    /// from a search rooted at <paramref name="searchNc"/>. A membership in the search partition
    /// itself is never excluded. On a Global Catalog, a subtree search from the base also reaches
    /// every partition namespace-SUBORDINATE to it (a child domain sits under the forest root),
    /// so those are returned too -- only a non-subordinate partition (a parent domain, or a
    /// different tree) is truly dropped. On a plain 389/636 bind, which hosts one partition,
    /// anything outside the search NC is dropped.
    /// </summary>
    internal static bool IsGenuinelyExcluded(string? memberNc, string searchNc, bool isGlobalCatalog)
    {
        if (memberNc is null ||
            string.Equals(memberNc, searchNc, StringComparison.OrdinalIgnoreCase))
            return false;

        if (isGlobalCatalog &&
            memberNc.EndsWith("," + searchNc, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
