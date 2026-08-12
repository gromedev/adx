using System.Linq;
using System.Management.Automation;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>
/// Shared machinery for the membership cmdlets (Get-ADxGroupMember, Get-ADxGroupNested):
/// resolve a GROUP from -Identity, then emit the results of a membership search.
/// <para>
/// Deliberately not derived from <see cref="ADxObjectCmdletBase"/>: that base carries the
/// -Filter/-LDAPFilter parameter sets, and RSAT's Get-ADGroupMember has neither -- inheriting
/// them would put wrong parameters on the surface with no way to hide them. The group
/// resolution here is a compact restatement of the same ladder
/// (<see cref="AdIdentityResolver"/> + the Group schema's base filter), fetching only what
/// membership queries need: the DN and the SID.
/// </para>
/// </summary>
public abstract class ADxGroupQueryCmdletBase : ADxMembershipQueryCmdletBase
{
    /// <summary>The group: DN, objectGUID (D/N), SID, or sAMAccountName.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName", "Group")]
    public object? Identity { get; set; }

    /// <summary>
    /// A resolved group and everything the membership filters need to describe it.
    /// </summary>
    /// <param name="GroupDn">The group's distinguished name.</param>
    /// <param name="PrimaryGroupRid">
    /// The group's own RID, held by <c>primaryGroupID</c> on objects whose primary group this
    /// is. Null when the SID could not be read.
    /// </param>
    /// <param name="NestedPrimaryGroupRids">
    /// The RIDs of groups nested inside it -- populated only when
    /// <see cref="NeedsNestedPrimaryGroupRids"/> is set, since only transitive enumeration
    /// needs them.
    /// </param>
    protected internal readonly record struct GroupMembershipTarget(
        string GroupDn,
        uint? PrimaryGroupRid,
        IReadOnlyList<uint> NestedPrimaryGroupRids)
    {
        /// <summary>
        /// Every RID whose <c>primaryGroupID</c> counts as transitive membership: the group's
        /// own, plus each nested group's -- deduplicated, because nesting cycles make overlap
        /// real: in an A<->B cycle the 1941 nested-group search on A matches A itself, so the
        /// target's own RID comes back in the nested set too. Duplicate OR arms would be
        /// semantically harmless, just filter noise.
        /// </summary>
        public IReadOnlyCollection<uint> AllPrimaryGroupRids
        {
            get
            {
                var all = new List<uint>(NestedPrimaryGroupRids.Count + 1);
                var seen = new HashSet<uint>();
                if (PrimaryGroupRid is { } own && seen.Add(own)) all.Add(own);
                foreach (var rid in NestedPrimaryGroupRids)
                {
                    if (seen.Add(rid)) all.Add(rid);
                }
                return all;
            }
        }
    }

    /// <summary>The membership filter for a resolved group, and the shape of the results.</summary>
    protected abstract (string Filter, AdObjectSchema OutputSchema) BuildQuery(GroupMembershipTarget target);

    /// <summary>
    /// Whether this cmdlet's query needs the primary-group RIDs of the NESTED groups as well
    /// as the target's. Only transitive member enumeration does: primary membership creates
    /// no memberOf link for rule 1941 to follow, so a user whose primary group is a nested
    /// group is reachable only by matching that nested group's RID directly.
    /// </summary>
    protected virtual bool NeedsNestedPrimaryGroupRids => false;

    protected override void ProcessRecord()
    {
        try
        {
            var group = ResolveGroup();
            if (group is null)
            {
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Cannot find a group with identity '{Identity}'."),
                    "ADxObjectNotFound", ErrorCategory.ObjectNotFound, Identity));
                return;
            }

            // The RID (last SID sub-authority) is what member objects' primaryGroupID holds.
            // A group without a readable SID still gets the memberOf arm; the missing primary
            // reconciliation is warned about, not silently dropped.
            var rid = LdapConvert.SidRid(group.GetBytes("objectSid"));
            if (rid is null)
            {
                WriteWarning(
                    $"'{group.DistinguishedName}' has no readable objectSid; members whose PRIMARY group " +
                    "this is (primaryGroupID matches) cannot be included.");
            }
            else if (IsGlobalCatalog)
            {
                // A RID is meaningful only relative to a domain SID. On a GC bind the subtree
                // search also reaches namespace-subordinate child partitions, so a
                // primaryGroupID arm would match OTHER domains' accounts with the same RID --
                // e.g. every child domain's users (RID 513) reported as members of the root's
                // Domain Users. Silent over-reporting is the one failure class this module
                // must never have; drop the arm and warn instead.
                WriteWarning(
                    $"Bound to a Global Catalog (port {EffectivePort}): primaryGroupID is a domain-relative " +
                    "RID and cannot be matched safely across the forest-wide GC namespace, so members held " +
                    "only through a primary-group link (the group's own or, with -Recursive, a nested " +
                    $"group's) are NOT included for '{group.DistinguishedName}'. Bind the group's own " +
                    "domain (port 389/636) to include them.");
                rid = null;
            }

            // The member attribute rides along on resolution for foreign-partition detection;
            // past MaxValRange that read holds only the first range block, and a cross-domain
            // member at index 1500+ would evade the warning -- complete the walk first.
            if (LdapRangeRetriever.NeedsCompletion(group))
            {
                group = LdapRangeRetriever
                    .CompleteAsync(GetConnection(), group, CancellationToken, EnqueueWarning)
                    .GetAwaiter().GetResult();
                DrainMessages();
            }
            WarnOnForeignPartitionMembers(group);

            var nestedRids = NeedsNestedPrimaryGroupRids && !IsGlobalCatalog
                ? CollectNestedGroupRids(group.DistinguishedName)
                : Array.Empty<uint>();

            var (filter, outputSchema) = BuildQuery(
                new GroupMembershipTarget(group.DistinguishedName, rid, nestedRids));
            EmitQueryResults(filter, outputSchema);
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
    /// Resolve -Identity to the group entry (DN + objectSid + objectClass). DN identities are
    /// a single base-scope read with the object-class check; everything else is a subtree
    /// search ANDed with the group base filter.
    /// </summary>
    private LdapEntry? ResolveGroup()
    {
        var schema = AdObjectSchema.Group;
        var (kind, value) = AdIdentityResolver.Classify(Identity!, schema);
        // "member" rides along on the resolution read (no extra round trip) purely to detect
        // cross-partition members the memberOf search structurally cannot return -- see
        // WarnOnForeignPartitionMembers.
        var attributes = new[] { "distinguishedName", "objectSid", "objectClass", "member" };

        if (kind == AdIdentityKind.DistinguishedName)
        {
            var entry = GetConnection()
                .ReadEntryAsync((string)value, attributes, CancellationToken)
                .GetAwaiter().GetResult();
            DrainMessages();
            if (entry is null) return null;

            return schema.MatchesType(entry.GetStrings("objectClass")) ? entry : null;
        }

        var lookup = AdIdentityResolver.BuildLookupFilter(kind, value);
        var spec = new LdapSearchSpec(
            DefaultNamingContext(),
            AdFilterEmitter.Emit(new AdFilterAnd(new AdFilterNode[]
            {
                new AdFilterRaw(schema.BaseFilter!),
                lookup
            })),
            attributes,
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
                    $"-Identity '{Identity}' matched {page.Entries.Count} or more groups (for example " +
                    $"'{page.Entries[0].DistinguishedName}' and '{page.Entries[1].DistinguishedName}'). " +
                    "Use a distinguished name, objectGUID, or SID."),
                "ADxIdentityAmbiguous", ErrorCategory.InvalidArgument, Identity));

        return page.Entries[0];
    }

    /// <summary>
    /// Membership is enumerated by a <c>memberOf</c> search under one domain partition. That is
    /// immune to MaxValRange and includes primary-group members, but it is structurally blind
    /// to members in OTHER domains of the forest: a cross-partition membership is stored only
    /// as a forward <c>member</c> link in this group's partition, with no <c>memberOf</c>
    /// back-link maintained on the foreign object, so no single-partition search can reach it.
    /// The group's own <c>member</c> attribute is where those foreign DNs surface, so it is
    /// read on the resolution round trip; any member whose domain NC differs from the group's
    /// is one this cmdlet cannot return. Warn rather than drop it silently -- and point at
    /// <c>Get-ADxGroup -Properties Members</c>, which returns every member DN verbatim, foreign
    /// ones included. Positive-detection only: a group with all members local warns not at all.
    /// </summary>
    private void WarnOnForeignPartitionMembers(LdapEntry group)
    {
        if (!group.TryGetRanged("member", out var members, out _, out _, out _) || members.Count == 0)
            return;

        var groupNc = LdapConvert.DomainNamingContext(group.DistinguishedName);
        if (groupNc is null) return;

        var foreign = members
            .Where(dn => !string.Equals(
                LdapConvert.DomainNamingContext(dn), groupNc, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (foreign.Count == 0) return;

        var sample = string.Join("; ", foreign.Take(3));
        var more = foreign.Count > 3 ? $" (and {foreign.Count - 3} more)" : string.Empty;
        WriteWarning(
            $"'{group.DistinguishedName}' has {foreign.Count} member(s) in other domain " +
            $"partitions of the forest: {sample}{more}. Membership is enumerated within one " +
            $"partition ({groupNc}), so these are NOT included in the results. Use " +
            "'Get-ADxGroup -Identity <group> -Properties Members' to see every member DN, then " +
            "resolve foreign DNs against their own domain.");
    }

    /// <summary>
    /// The primary-group RIDs of every group nested inside <paramref name="groupDn"/>.
    /// <para>
    /// One extra 1941 search, and it closes the -Recursive gap that matters most in practice:
    /// Domain Users is nested in BUILTIN\Users in every default domain, and its members hold
    /// that membership through primaryGroupID alone. Without these RIDs the chain rule sees
    /// no link and "-Recursive" returns almost nobody for the most commonly audited group in
    /// the directory.
    /// </para>
    /// </summary>
    private IReadOnlyList<uint> CollectNestedGroupRids(string groupDn)
    {
        var spec = new LdapSearchSpec(
            DefaultNamingContext(),
            AdGroupMemberQuery.NestedGroups(groupDn),
            new[] { "objectSid" },
            LdapScope.Subtree,
            ResultPageSize,
            SizeLimit: 0);

        var rids = new List<uint>();
        var iterator = new LdapPageIterator(GetConnection());
        var enumerator = iterator
            .StreamAsync(spec, maxItems: 0, onPageComplete: null, skipFirst: 0,
                warning: EnqueueWarning, cancellationToken: CancellationToken)
            .GetAsyncEnumerator(CancellationToken);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                if (LdapConvert.SidRid(enumerator.Current.GetBytes("objectSid")) is { } nestedRid)
                    rids.Add(nestedRid);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DrainMessages();
        if (rids.Count > 0)
            WriteVerbose($"Including primaryGroupID for {rids.Count} nested group(s).");

        return rids;
    }
}
