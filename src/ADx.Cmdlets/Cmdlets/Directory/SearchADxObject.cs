using System.Diagnostics;
using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Search-ADxObject: the generic LDAP search primitive.
/// <para>
/// Deliberately one generic cmdlet taking a raw LDAP filter, rather than per-entity cmdlets.
/// The RSAT-compatible presets (Get-ADxUser, Get-ADxGroup, ...) are built on top of this
/// transport, adding a filter translator and an output projector; they do not replace it,
/// because a raw filter is the escape hatch when a preset does not cover the query.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Search, "ADxObject")]
[OutputType(typeof(PSObject))]
public sealed class SearchADxObject : ADxCmdletBase
{
    /// <summary>
    /// Raw LDAP filter, e.g. <c>(&amp;(objectCategory=person)(objectClass=user))</c>.
    /// <para>
    /// Named -LdapFilter, not -Filter, on purpose. the Graph world's -Filter is OData and RSAT's
    /// -Filter is PowerShell syntax; accepting either here and sending it as an LDAP filter
    /// would silently return the wrong set rather than failing. There is deliberately no
    /// -Filter alias.
    /// </para>
    /// </summary>
    [Parameter(Position = 0)]
    [Alias("Ldap")]
    public string LdapFilter { get; set; } = "(objectClass=*)";

    /// <summary>Search root. Defaults to the domain's defaultNamingContext.</summary>
    [Parameter]
    [Alias("Base", "OrganizationalUnit", "OU")]
    public string? SearchBase { get; set; }

    /// <summary>
    /// Attributes to return. Naming them explicitly is the single biggest performance lever
    /// in an LDAP sweep -- omitting it makes the DC serialise every populated attribute.
    /// </summary>
    [Parameter]
    [Alias("Select", "Attributes")]
    public string[]? Property { get; set; }

    [Parameter]
    [ValidateSet("Base", "OneLevel", "Subtree")]
    public string Scope { get; set; } = "Subtree";

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Top { get; set; }

    [Parameter]
    public SwitchParameter All { get; set; }

    /// <summary>
    /// Entries per page. Max 1000, which is AD's MaxPageSize default -- asking for more
    /// does not return more. (The Graph cmdlets cap at 999; this is a different server.)
    /// </summary>
    [Parameter]
    [ValidateRange(1, 1000)]
    public int PageSize { get; set; } = 1000;

    /// <summary>Emit raw attribute values with no type conversion.</summary>
    [Parameter]
    public SwitchParameter Raw { get; set; }

    protected override void ProcessRecord()
    {
        var sw = Stopwatch.StartNew();

        // -All is unlimited and overrides -Top; -Top N caps at N; neither means a single page.
        long maxItems;
        var defaultedToPageSize = false;
        if (All.IsPresent)
            maxItems = 0;
        else if (Top > 0)
            maxItems = Top;
        else
        {
            // One past the page: the probe entry is never emitted, it only proves whether
            // the single-page default actually cut the result set. Without it a query
            // matching EXACTLY one page warned "stopped at one page" over a complete set.
            maxItems = PageSize + 1;
            defaultedToPageSize = true;
        }

        try
        {
            var client = GetConnection();
            var searchBase = ResolveSearchBase(SearchBase);

            var spec = new LdapSearchSpec(
                searchBase,
                LdapFilter,
                Property ?? Array.Empty<string>(),
                Enum.TryParse<LdapScope>(Scope, ignoreCase: true, out var scope) ? scope : LdapScope.Subtree,
                PageSize,
                SizeLimit: 0);

            WriteVerbose($"Searching '{searchBase}' scope {spec.Scope} filter {LdapFilter}");

            var iterator = new LdapPageIterator(client);
            long emitted = 0;

            var enumerable = iterator.StreamAsync(
                spec,
                maxItems,
                onPageComplete: info => EnqueueVerbose(
                    $"Page {info.PageIndex}: {info.EntriesInPage} entries ({info.TotalEmitted} total)."),
                skipFirst: 0,
                cancellationToken: CancellationToken);

            // Drive the async sequence from the pipeline thread. WriteObject and
            // WriteVerbose are only legal here, which is why page callbacks buffer
            // their messages and this loop drains them.
            var truncated = false;
            var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    if (defaultedToPageSize && emitted == PageSize)
                    {
                        truncated = true;
                        break;
                    }
                    WriteObject(LdapEntryToPSObject(enumerator.Current, Raw.IsPresent));
                    emitted++;

                    if (emitted % 500 == 0) DrainMessages();
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            sw.Stop();
            DrainMessages();
            WriteVerbose($"Returned {emitted} entries in {sw.ElapsedMilliseconds} ms.");

            if (truncated)
            {
                WriteWarning(
                    $"Search stopped at {emitted} entries (one page) with more available. " +
                    "Use -All to return everything, or -Top N for an explicit limit.");
            }
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
}
