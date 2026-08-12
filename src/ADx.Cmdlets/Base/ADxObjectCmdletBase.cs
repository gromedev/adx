using System.Management.Automation;
using ADx.Cmdlets.Filter;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>
/// Shared machinery for the RSAT-compatible preset cmdlets (Get-ADxUser and siblings). Each
/// preset supplies its <see cref="AdObjectSchema"/>; parameter binding, filter translation,
/// identity resolution, searching and projection all live here, so a preset is ~20 lines.
/// <para>
/// Parameter-set design is a correctness feature, not taste:
/// <c>DefaultParameterSetName = "Filter"</c> with -Filter and -LDAPFilter <em>named-only</em>
/// while only -Identity is positional. If all three were positional,
/// <c>Get-ADxUser jdoe</c> would bind "jdoe" into -Filter and silently return the wrong
/// thing (a filter parse error at best, an unintended match-all at worst).
/// </para>
/// </summary>
public abstract class ADxObjectCmdletBase : ADxCmdletBase
{
    protected const string FilterSet = "Filter";
    protected const string LdapFilterSet = "LdapFilter";
    protected const string IdentitySet = "Identity";

    /// <summary>The per-type table: base class filter, defaults, identity forms.</summary>
    protected abstract AdObjectSchema ObjectSchema { get; }

    /// <summary>
    /// DN, objectGUID (D/N format), SID, or sAMAccountName. Pipeline-bindable both by value
    /// and by a DistinguishedName property, so ADx output pipes back in.
    /// </summary>
    [Parameter(ParameterSetName = IdentitySet, Mandatory = true, Position = 0,
        ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("DistinguishedName")]
    public object? Identity { get; set; }

    /// <summary>
    /// RSAT-syntax filter ("Name -like 'j*' -and Enabled -eq $true"). A ScriptBlock argument
    /// coerces to string, yielding the body without braces, so both RSAT spellings work.
    /// </summary>
    [Parameter(ParameterSetName = FilterSet, Mandatory = true)]
    public string Filter { get; set; } = string.Empty;

    /// <summary>Raw LDAP filter, for queries the translator does not cover.</summary>
    [Parameter(ParameterSetName = LdapFilterSet, Mandatory = true)]
    [Alias("Ldap")]
    public string LDAPFilter { get; set; } = string.Empty;

    /// <summary>
    /// Extra properties on top of the type's default set; "*" fetches everything the server
    /// will hand over (which excludes constructed attributes -- RSAT's "*" does too).
    /// </summary>
    [Parameter]
    [Alias("Property")]
    public string[]? Properties { get; set; }

    [Parameter]
    [Alias("Base", "OrganizationalUnit", "OU")]
    public string? SearchBase { get; set; }

    [Parameter]
    [ValidateSet("Base", "OneLevel", "Subtree")]
    public string SearchScope { get; set; } = "Subtree";

    /// <summary>
    /// Maximum objects returned; 0 (the default) is unlimited, matching RSAT's
    /// -ResultSetSize default. Search-ADxObject defaults to one page instead -- that
    /// divergence is deliberate and Pester-asserted, because a preset that silently stopped
    /// at 1000 would be a drop-in lie.
    /// </summary>
    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int ResultSetSize { get; set; }

    /// <summary>Entries per wire page. AD's MaxPageSize default is 1000.</summary>
    [Parameter]
    [ValidateRange(1, 1000)]
    public int ResultPageSize { get; set; } = 1000;

    /// <summary>
    /// Pass unrecognised names in -Filter and -Properties through verbatim instead of
    /// erroring. The escape hatch for schema extensions ADx's curated table cannot know.
    /// </summary>
    [Parameter]
    public SwitchParameter AllowUnknownProperty { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            if (ParameterSetName == IdentitySet)
                ProcessIdentity();
            else
                ProcessSearch();
        }
        catch (AdFilterTranslationException ex)
        {
            DrainMessages();
            ThrowTerminatingError(new ErrorRecord(
                ex, "ADxFilterTranslation", ErrorCategory.InvalidArgument,
                ParameterSetName == IdentitySet ? Identity : Filter));
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
        catch (Exception ex) when (WriteLdapError(ex, SearchBase ?? Server)) { }
    }

    // ---- -Filter / -LDAPFilter ----

    private void ProcessSearch()
    {
        AdRsatProjector.ValidateRequestedProperties(Properties, AllowUnknownProperty.IsPresent, ObjectSchema.AttributeOverrides);

        var ldapFilter = ParameterSetName == LdapFilterSet
            ? ComposeWithBaseFilter(new AdFilterRaw(LDAPFilter))
            : TranslateFilter();

        var fetchList = AdRsatProjector.BuildFetchList(
            ObjectSchema, Properties, AllowUnknownProperty.IsPresent, out var fetchAll);

        // Never ask the server for a bigger page than the caller can possibly consume.
        // -ResultSetSize is applied client-side, so without this a "-Filter * -ResultSetSize 1"
        // fetches a full 1000-entry page, emits one, and discards 999 -- measured at 82ms
        // against 31ms once the page is capped, on the same query returning the same object.
        // Only ever shrinks the page, so bulk enumeration (ResultSetSize 0 = unlimited) is
        // untouched.
        var effectivePageSize = ResultSetSize > 0
            ? Math.Min(ResultPageSize, ResultSetSize)
            : ResultPageSize;

        var spec = new LdapSearchSpec(
            ResolveEffectiveSearchBase(),
            ldapFilter,
            fetchList,
            Enum.TryParse<LdapScope>(SearchScope, ignoreCase: true, out var scope) ? scope : LdapScope.Subtree,
            effectivePageSize,
            SizeLimit: 0);

        WriteVerbose($"Searching '{spec.SearchBase}' scope {spec.Scope} filter {spec.Filter}");

        var iterator = new LdapPageIterator(GetConnection());
        var enumerable = iterator.StreamAsync(
            spec,
            maxItems: ResultSetSize,
            onPageComplete: info => EnqueueVerbose(
                $"Page {info.PageIndex}: {info.EntriesInPage} entries ({info.TotalEmitted} total)."),
            skipFirst: 0,
            cancellationToken: CancellationToken);

        long emitted = 0;
        var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                WriteObject(AdRsatProjector.Project(
                    CompleteRangedAttributes(enumerator.Current), ObjectSchema, Properties, fetchAll));
                emitted++;
                if (emitted % 500 == 0) DrainMessages();
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DrainMessages();
        WriteVerbose($"Returned {emitted} {ObjectSchema.TypeLabel}(s).");
    }

    private string TranslateFilter()
    {
        // SessionState.PSVariable.Get, NOT GetVariableValue: the latter returns null for both
        // "undefined" and "defined as $null", which would make a typo'd variable silently
        // filter as $null. The translator turns not-found into a terminating error.
        var node = AdFilterTranslator.Translate(
            Filter,
            name =>
            {
                var variable = SessionState.PSVariable.Get(name);
                return variable is null ? (false, null) : (true, variable.Value);
            },
            AllowUnknownProperty.IsPresent);

        return ComposeWithBaseFilter(node);
    }

    /// <summary>
    /// AND the preset's base object-class filter with the user's constraint. A null node is
    /// the translated bare "*": no user constraint, base filter alone (or match-everything
    /// for the untyped preset).
    /// </summary>
    private string ComposeWithBaseFilter(AdFilterNode? userNode)
    {
        var baseNode = ObjectSchema.BaseFilter is null ? null : new AdFilterRaw(ObjectSchema.BaseFilter);

        var combined = (baseNode, userNode) switch
        {
            (null, null) => (AdFilterNode)new AdFilterRaw("(objectClass=*)"),
            (not null, null) => baseNode,
            (null, not null) => userNode,
            (not null, not null) => new AdFilterAnd(new[] { (AdFilterNode)baseNode, userNode })
        };

        return AdFilterEmitter.Emit(combined);
    }

    /// <summary>
    /// The search base: the caller's -SearchBase if given; otherwise the schema's default
    /// container relative to the domain head (for types confined to one container, e.g. PSOs);
    /// otherwise the domain's defaultNamingContext (via the base <see cref="ADxCmdletBase.ResolveSearchBase"/>).
    /// </summary>
    private string ResolveEffectiveSearchBase()
    {
        if (!string.IsNullOrWhiteSpace(SearchBase)) return SearchBase!.Trim();

        if (ObjectSchema.DefaultContainerRelativeDn is { } relative)
        {
            var namingContext = GetConnection().RootDse.DefaultNamingContext;
            if (!string.IsNullOrWhiteSpace(namingContext)) return $"{relative},{namingContext}";
        }

        return ResolveSearchBase(SearchBase);
    }

    // ---- -Identity ----

    private void ProcessIdentity()
    {
        AdRsatProjector.ValidateRequestedProperties(Properties, AllowUnknownProperty.IsPresent, ObjectSchema.AttributeOverrides);

        var (kind, value) = AdIdentityResolver.Classify(Identity!, ObjectSchema);
        var fetchList = AdRsatProjector.BuildFetchList(
            ObjectSchema, Properties, AllowUnknownProperty.IsPresent, out var fetchAll);

        var entry = kind == AdIdentityKind.DistinguishedName
            ? ResolveByDn((string)value, fetchList)
            : ResolveBySearch(kind, value, fetchList);

        if (entry is null)
        {
            WriteError(new ErrorRecord(
                new ItemNotFoundException(
                    $"Cannot find a {ObjectSchema.TypeLabel} with identity '{Identity}'" +
                    (SearchBase is null ? "." : $" under '{SearchBase}'.")),
                "ADxObjectNotFound", ErrorCategory.ObjectNotFound, Identity));
            return;
        }

        var projected = AdRsatProjector.Project(
            CompleteRangedAttributes(entry), ObjectSchema, Properties, fetchAll);
        DrainMessages();
        WriteObject(projected);
    }

    /// <summary>
    /// Complete any range-limited multi-valued attributes before projection. RSAT returns
    /// complete Members/MemberOf collections, so the presets do too; the follow-up reads
    /// only happen for entries that actually came back ranged (groups past MaxValRange).
    /// Search-ADxObject deliberately keeps its flag-don't-fetch contract instead.
    /// </summary>
    protected LdapEntry CompleteRangedAttributes(LdapEntry entry) =>
        LdapRangeRetriever.NeedsCompletion(entry)
            ? LdapRangeRetriever.CompleteAsync(GetConnection(), entry, CancellationToken, EnqueueWarning)
                .GetAwaiter().GetResult()
            : entry;

    /// <summary>
    /// The DN fast path: a base-scope read, one round trip, no paging, no subtree walk --
    /// only reachable from C#, and the single biggest per-object win over piping through a
    /// generic search. Object class is verified afterwards because a base read applies no
    /// filter: without the check, a group DN handed to Get-ADxUser would return the group.
    /// </summary>
    private LdapEntry? ResolveByDn(string distinguishedName, IReadOnlyList<string> fetchList)
    {
        var attributes = EnsureObjectClassFetched(fetchList);

        var entry = GetConnection()
            .ReadEntryAsync(distinguishedName, attributes, CancellationToken)
            .GetAwaiter().GetResult();
        DrainMessages();

        if (entry is null) return null;

        // Exists, but is not this type: ObjectNotFound, same as RSAT. MatchesType tests
        // membership in the objectClass chain (minus the disqualifiers), which is what the
        // wire filter does -- an inetOrgPerson IS a user, and requiring it to be the chain's
        // last element made this path reject objects -Filter returns.
        return ObjectSchema.MatchesType(entry.GetStrings("objectClass")) ? entry : null;
    }

    private LdapEntry? ResolveBySearch(AdIdentityKind kind, object value, IReadOnlyList<string> fetchList)
    {
        var entry = RunIdentitySearch(AdIdentityResolver.BuildLookupFilter(kind, value), fetchList);

        // Computers: 'WS01' misses because the stored sAMAccountName is 'WS01$'. Retry with
        // the suffix rather than making every caller remember it.
        if (entry is null &&
            kind == AdIdentityKind.SamAccountName &&
            ObjectSchema.IdentitySamTriesDollarSuffix &&
            value is string sam && !sam.EndsWith('$'))
        {
            entry = RunIdentitySearch(
                AdIdentityResolver.BuildLookupFilter(kind, sam + "$"), fetchList);
        }

        return entry;
    }

    private LdapEntry? RunIdentitySearch(AdFilterNode lookup, IReadOnlyList<string> fetchList)
    {
        var spec = new LdapSearchSpec(
            ResolveEffectiveSearchBase(),
            ComposeWithBaseFilter(lookup),
            EnsureObjectClassFetched(fetchList),
            LdapScope.Subtree,
            // Two, so ambiguity is detectable: one row cannot distinguish "the match" from
            // "a match".
            PageSize: 2,
            SizeLimit: 0);

        var page = GetConnection()
            .SearchPageAsync(spec, cookie: null, CancellationToken)
            .GetAwaiter().GetResult();
        DrainMessages();

        if (page.Entries.Count == 0) return null;

        // An identity that matches more than one object has no right answer, and picking the
        // first is the silent-wrong-result failure this design exists to prevent. Reachable
        // against a Global Catalog (port 3268), where sAMAccountName is not forest-unique.
        // RSAT throws here too.
        if (page.Entries.Count > 1)
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"-Identity '{Identity}' matched {page.Entries.Count} or more {ObjectSchema.TypeLabel}s " +
                    $"(for example '{page.Entries[0].DistinguishedName}' and " +
                    $"'{page.Entries[1].DistinguishedName}'). Use a distinguished name, objectGUID, or SID, " +
                    "or narrow the search with -SearchBase."),
                "ADxIdentityAmbiguous", ErrorCategory.InvalidArgument, Identity));

        return page.Entries[0];
    }

    /// <summary>
    /// Identity resolution always needs objectClass (for the type check and the ObjectClass
    /// default), and "*" already includes it.
    /// </summary>
    private static IReadOnlyList<string> EnsureObjectClassFetched(IReadOnlyList<string> fetchList)
    {
        if (fetchList.Contains("*") ||
            fetchList.Contains("objectClass", StringComparer.OrdinalIgnoreCase))
            return fetchList;

        var extended = new List<string>(fetchList) { "objectClass" };
        return extended;
    }
}
