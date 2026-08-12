using System.Management.Automation;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>
/// Machinery shared by the membership cmdlets that resolve one object from <c>-Identity</c> and
/// then stream the results of a single membership search: the group-facing pair
/// (Get-ADxGroupMember / Get-ADxGroupNested, via <see cref="ADxGroupQueryCmdletBase"/>) and the
/// principal-facing Get-ADxPrincipalGroupMembership. What lives here is everything that does not
/// depend on WHICH object -Identity names: the result-shaping parameters, the paged streaming
/// projection, and the root the search must run from.
/// <para>
/// <c>-Identity</c> itself is deliberately NOT here. Its aliases differ (the group cmdlets
/// alias it "Group"; the principal cmdlet does not) and so does how it resolves, so each
/// subclass declares its own -- keeping wrong aliases off the surface with no way to hide them,
/// the same reason this hierarchy does not derive from <see cref="ADxObjectCmdletBase"/>.
/// </para>
/// </summary>
public abstract class ADxMembershipQueryCmdletBase : ADxCmdletBase
{
    /// <summary>Properties to emit for each result, beyond the type's default set.</summary>
    [Parameter]
    [Alias("Property")]
    public string[]? Properties { get; set; }

    /// <summary>Maximum results; 0 (default) is unlimited.</summary>
    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int ResultSetSize { get; set; }

    /// <summary>Entries per wire page. AD's MaxPageSize default is 1000.</summary>
    [Parameter]
    [ValidateRange(1, 1000)]
    public int ResultPageSize { get; set; } = 1000;

    /// <summary>Pass unrecognised -Properties names through verbatim instead of erroring.</summary>
    [Parameter]
    public SwitchParameter AllowUnknownProperty { get; set; }

    /// <summary>
    /// Run a membership search and stream each result as a projected object. Range-suffixed
    /// attributes are completed per-entry, and messages are drained periodically so warnings
    /// and verbose output interleave with results rather than arriving in a burst at the end.
    /// </summary>
    protected void EmitQueryResults(string filter, AdObjectSchema outputSchema)
    {
        AdRsatProjector.ValidateRequestedProperties(Properties, AllowUnknownProperty.IsPresent);
        var fetchList = AdRsatProjector.BuildFetchList(
            outputSchema, Properties, AllowUnknownProperty.IsPresent, out var fetchAll);

        var spec = new LdapSearchSpec(
            DefaultNamingContext(),
            filter,
            fetchList,
            LdapScope.Subtree,
            ResultPageSize,
            SizeLimit: 0);

        WriteVerbose($"Searching '{spec.SearchBase}' filter {spec.Filter}");

        var iterator = new LdapPageIterator(GetConnection());
        var enumerable = iterator.StreamAsync(
            spec,
            maxItems: ResultSetSize,
            onPageComplete: info => EnqueueVerbose(
                $"Page {info.PageIndex}: {info.EntriesInPage} entries ({info.TotalEmitted} total)."),
            skipFirst: 0,
            warning: EnqueueWarning,
            cancellationToken: CancellationToken);

        long emitted = 0;
        var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var entry = enumerator.Current;
                if (LdapRangeRetriever.NeedsCompletion(entry))
                {
                    entry = LdapRangeRetriever
                        .CompleteAsync(GetConnection(), entry, CancellationToken, EnqueueWarning)
                        .GetAwaiter().GetResult();
                }

                WriteObject(AdRsatProjector.Project(entry, outputSchema, Properties, fetchAll));
                emitted++;
                if (emitted % 500 == 0) DrainMessages();
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DrainMessages();
        WriteVerbose($"Returned {emitted} {outputSchema.TypeLabel}(s).");
    }

    /// <summary>
    /// Membership searches always run from the domain root: the objects sought can live in any
    /// OU, so a narrower base would silently drop results. (RSAT's Get-ADGroupMember and
    /// Get-ADPrincipalGroupMembership have no -SearchBase either, for the same reason.)
    /// </summary>
    protected string DefaultNamingContext()
    {
        var context = GetConnection().RootDse.DefaultNamingContext;
        if (!string.IsNullOrWhiteSpace(context)) return context;

        ThrowTerminatingError(new ErrorRecord(
            new InvalidOperationException(
                "The server did not publish a defaultNamingContext; membership queries need one."),
            "NoDefaultNamingContext", ErrorCategory.InvalidArgument, Server));
        return null!;
    }
}
