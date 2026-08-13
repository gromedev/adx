using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Search-ADxAccount: drop-in for RSAT's Search-ADAccount. A switch-driven account finder --
/// each criterion switch (mutually exclusive, one per call) maps to a specific LDAP filter over
/// UAC bits, accountExpires, lastLogonTimestamp or lockoutTime, scoped to users and/or
/// computers. There is no -Filter/-Identity/-Properties: the criterion IS the filter and the
/// output is a fixed slim account shape, matching RSAT.
/// <para>
/// -PasswordExpired is filtered client-side: its bit lives in the constructed
/// msDS-User-Account-Control-Computed, which AD cannot match in a search filter.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Search, "ADxAccount")]
[OutputType(typeof(PSObject))]
public sealed class SearchADxAccount : ADxCmdletBase
{
    private const string DisabledSet = "AccountDisabled";
    private const string ExpiredSet = "AccountExpired";
    private const string ExpiringSet = "AccountExpiring";
    private const string InactiveSet = "AccountInactive";
    private const string LockedOutSet = "LockedOut";
    private const string PasswordExpiredSet = "PasswordExpired";
    private const string PasswordNeverExpiresSet = "PasswordNeverExpires";

    [Parameter(Mandatory = true, ParameterSetName = DisabledSet)]
    public SwitchParameter AccountDisabled { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = ExpiredSet)]
    public SwitchParameter AccountExpired { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = ExpiringSet)]
    public SwitchParameter AccountExpiring { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = InactiveSet)]
    public SwitchParameter AccountInactive { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = LockedOutSet)]
    public SwitchParameter LockedOut { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = PasswordExpiredSet)]
    public SwitchParameter PasswordExpired { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = PasswordNeverExpiresSet)]
    public SwitchParameter PasswordNeverExpires { get; set; }

    /// <summary>An absolute cutoff for -AccountExpiring / -AccountInactive. Interpreted as local time, like RSAT.</summary>
    [Parameter(ParameterSetName = ExpiringSet)]
    [Parameter(ParameterSetName = InactiveSet)]
    public DateTime? DateTime { get; set; }

    /// <summary>A relative window for -AccountExpiring (now+span) / -AccountInactive (now-span).</summary>
    [Parameter(ParameterSetName = ExpiringSet)]
    [Parameter(ParameterSetName = InactiveSet)]
    public TimeSpan? TimeSpan { get; set; }

    [Parameter]
    public SwitchParameter UsersOnly { get; set; }

    [Parameter]
    public SwitchParameter ComputersOnly { get; set; }

    [Parameter]
    [Alias("Base", "OrganizationalUnit", "OU")]
    public string? SearchBase { get; set; }

    [Parameter]
    [ValidateSet("Base", "OneLevel", "Subtree")]
    public string SearchScope { get; set; } = "Subtree";

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int ResultSetSize { get; set; }

    [Parameter]
    [ValidateRange(1, 1000)]
    public int ResultPageSize { get; set; } = 1000;

    protected override void ProcessRecord()
    {
        try
        {
            var criterion = CriterionForSet(ParameterSetName);
            var scope = ResolveScope();
            var nowUtc = System.DateTime.UtcNow;
            var cutoffUtc = ResolveCutoff(criterion, nowUtc);

            var filter = AdAccountSearchQuery.BuildFilter(criterion, scope, nowUtc, cutoffUtc);

            // PasswordExpired's primary backing attribute (msDS-User-Account-Control-Computed) is
            // already a default column; the predicate additionally needs pwdLastSet to exclude
            // must-change accounts, which is NOT a default column, so append it (unprojected).
            var fetch = AdRsatProjector.BuildFetchList(AdObjectSchema.Account, null, false, out var fetchAll);

            var clientFiltered = criterion == AdAccountSearchQuery.Criterion.PasswordExpired;
            if (clientFiltered)
                fetch = new List<string>(fetch) { "pwdLastSet" };
            // +1 is the truncation probe; long arithmetic so [int]::MaxValue cannot wrap.
            var effectivePageSize = ResultSetSize > 0 && !clientFiltered
                ? (int)Math.Min(ResultPageSize, (long)ResultSetSize + 1)
                : ResultPageSize;

            var spec = new LdapSearchSpec(
                ResolveSearchBase(SearchBase),
                filter,
                fetch,
                Enum.TryParse<LdapScope>(SearchScope, ignoreCase: true, out var s) ? s : LdapScope.Subtree,
                effectivePageSize,
                SizeLimit: 0);

            WriteVerbose($"Searching '{spec.SearchBase}' scope {spec.Scope} filter {spec.Filter}");
            if (clientFiltered)
                WriteVerbose("-PasswordExpired is filtered per object client-side (a constructed attribute); " +
                             "the whole in-scope account population is read.");

            // Enforce -ResultSetSize on the EMITTED count so it stays correct under client-side
            // filtering; the iterator streams unbounded and we stop early.
            var iterator = new LdapPageIterator(GetConnection());
            var enumerable = iterator.StreamAsync(
                spec,
                maxItems: 0,
                onPageComplete: info => EnqueueVerbose(
                    $"Page {info.PageIndex}: {info.EntriesInPage} entries."),
                skipFirst: 0,
                warning: EnqueueWarning,
                cancellationToken: CancellationToken);

            long emitted = 0;
            var truncated = false;
            var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    var entry = enumerator.Current;
                    if (clientFiltered && !AdAccountSearchQuery.PasswordExpiredPredicate(entry))
                        continue;

                    // The stream runs unbounded (client-side filtering makes a server-side
                    // cap wrong), so the first MATCHING entry past the cap is the free
                    // truncation probe: seen, never emitted.
                    if (ResultSetSize > 0 && emitted == ResultSetSize)
                    {
                        truncated = true;
                        break;
                    }

                    WriteObject(AdRsatProjector.Project(entry, AdObjectSchema.Account, null, fetchAll));
                    emitted++;
                    if (emitted % 500 == 0) DrainMessages();
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            DrainMessages();
            if (truncated)
                WriteWarning(
                    $"More accounts match than -ResultSetSize {ResultSetSize}; the result set is truncated. " +
                    "Raise -ResultSetSize, or drop it for the full set.");
            WriteVerbose($"Returned {emitted} account(s).");
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

    private static AdAccountSearchQuery.Criterion CriterionForSet(string parameterSetName) => parameterSetName switch
    {
        DisabledSet => AdAccountSearchQuery.Criterion.AccountDisabled,
        ExpiredSet => AdAccountSearchQuery.Criterion.AccountExpired,
        ExpiringSet => AdAccountSearchQuery.Criterion.AccountExpiring,
        InactiveSet => AdAccountSearchQuery.Criterion.AccountInactive,
        LockedOutSet => AdAccountSearchQuery.Criterion.LockedOut,
        PasswordExpiredSet => AdAccountSearchQuery.Criterion.PasswordExpired,
        PasswordNeverExpiresSet => AdAccountSearchQuery.Criterion.PasswordNeverExpires,
        _ => throw new InvalidOperationException($"Unexpected parameter set '{parameterSetName}'."),
    };

    private AdAccountSearchQuery.AccountScope ResolveScope()
    {
        if (UsersOnly.IsPresent && ComputersOnly.IsPresent)
            ThrowTerminatingError(new ErrorRecord(
                new PSArgumentException("Specify at most one of -UsersOnly and -ComputersOnly."),
                "ConflictingScope", ErrorCategory.InvalidArgument, null));

        if (UsersOnly.IsPresent) return AdAccountSearchQuery.AccountScope.UsersOnly;
        if (ComputersOnly.IsPresent) return AdAccountSearchQuery.AccountScope.ComputersOnly;
        return AdAccountSearchQuery.AccountScope.AllAccounts;
    }

    /// <summary>
    /// The absolute UTC cutoff for the windowed criteria. -DateTime is interpreted in local time
    /// (matching RSAT and the projector's local-time output); an unspecified Kind is stamped
    /// Local before converting. -TimeSpan moves the cutoff forward for expiring, backward for
    /// inactive. Null for the non-windowed criteria.
    /// </summary>
    private DateTime? ResolveCutoff(AdAccountSearchQuery.Criterion criterion, DateTime nowUtc)
    {
        var windowed = criterion is AdAccountSearchQuery.Criterion.AccountExpiring
            or AdAccountSearchQuery.Criterion.AccountInactive;
        if (!windowed) return null;

        if (DateTime.HasValue == TimeSpan.HasValue)
            ThrowTerminatingError(new ErrorRecord(
                new PSArgumentException(
                    $"-{criterion} requires exactly one of -DateTime or -TimeSpan."),
                "MissingWindow", ErrorCategory.InvalidArgument, null));

        if (DateTime.HasValue)
        {
            var dt = DateTime.Value;
            if (dt.Kind == DateTimeKind.Unspecified)
                dt = System.DateTime.SpecifyKind(dt, DateTimeKind.Local);
            return dt.ToUniversalTime();
        }

        return criterion == AdAccountSearchQuery.Criterion.AccountExpiring
            ? nowUtc + TimeSpan!.Value
            : nowUtc - TimeSpan!.Value;
    }
}
