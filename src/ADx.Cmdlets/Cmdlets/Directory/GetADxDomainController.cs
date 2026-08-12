using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxDomainController: one or all domain controllers, matching RSAT's
/// Get-ADDomainController.
/// <list type="bullet">
/// <item>no arguments -> the connected DC;</item>
/// <item><c>-Identity &lt;name|DN&gt;</c> -> that one DC;</item>
/// <item><c>-Filter *</c> -> every DC in the connected domain (any other filter is refused --
/// a client-side property filter is out of scope for v1);</item>
/// <item><c>-Discover</c> -> declared unsupported (the DC locator is the netlogon/CLDAP
/// mailslot protocol, not LDAP).</item>
/// </list>
/// IPv4Address/IPv6Address stay declared-unsupported (client-side DNS), the same gap the
/// computer preset documents.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxDomainController", DefaultParameterSetName = IdentitySet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxDomainController : ADxTopologyCmdletBase
{
    private const string IdentitySet = "Identity";
    private const string FilterSet = "Filter";

    [Parameter(ParameterSetName = IdentitySet, Position = 0,
        ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)]
    [Alias("Name", "HostName")]
    public string? Identity { get; set; }

    [Parameter(ParameterSetName = FilterSet, Mandatory = true)]
    public string? Filter { get; set; }

    /// <summary>Declared unsupported: the DC locator is not an LDAP operation.</summary>
    [Parameter]
    public SwitchParameter Discover { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            if (Discover.IsPresent)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new PSNotSupportedException(
                        "-Discover is not supported: the domain-controller locator uses the netlogon/CLDAP " +
                        "mailslot protocol, not LDAP, and ADx is LDAP-only. Name a DC with -Server, or use " +
                        "Get-ADxDomainController -Filter * to enumerate the domain's controllers."),
                    "DiscoverNotSupported", ErrorCategory.NotImplemented, null));
            }

            if (ParameterSetName == FilterSet && Filter?.Trim() != "*")
            {
                ThrowTerminatingError(new ErrorRecord(
                    new PSNotSupportedException(
                        $"Only '-Filter *' is supported in this version (got '{Filter}'). Enumerate with " +
                        "-Filter *, then filter the results in PowerShell, or target one DC with -Identity."),
                    "FilterNotSupported", ErrorCategory.NotImplemented, Filter));
            }

            var rootDse = GetConnection().RootDse;
            var domainNc = rootDse.DefaultNamingContext;
            var domainDns = AdTopology.DnsNameFromNamingContext(domainNc);
            var forestDns = AdTopology.DnsNameFromNamingContext(rootDse.RootDomainNamingContext);
            var roleHolders = ReadForestAndDomainRoleHolders(rootDse, domainNc);

            var allFacts = CollectDomainControllerFacts();

            IEnumerable<DomainControllerFacts> selected = ParameterSetName switch
            {
                // -Filter *: the connected domain's DCs, matching RSAT's domain scope.
                FilterSet => allFacts.Where(f =>
                    string.Equals(f.DomainNamingContext, domainNc, StringComparison.OrdinalIgnoreCase)),

                // -Identity given: the matching DC. Otherwise the connected DC.
                _ when !string.IsNullOrWhiteSpace(Identity) =>
                    allFacts.Where(f => MatchesIdentity(f, Identity!)),

                _ => allFacts.Where(f =>
                    string.Equals(f.HostName, rootDse.DnsHostName, StringComparison.OrdinalIgnoreCase)),
            };

            var results = selected.ToArray();

            if (results.Length == 0)
            {
                if (ParameterSetName == IdentitySet && !string.IsNullOrWhiteSpace(Identity))
                {
                    WriteError(new ErrorRecord(
                        new ItemNotFoundException($"No domain controller matches identity '{Identity}'."),
                        "ADxDomainControllerNotFound", ErrorCategory.ObjectNotFound, Identity));
                }
                return;
            }

            foreach (var fact in results.OrderBy(f => f.HostName, StringComparer.OrdinalIgnoreCase))
                WriteObject(Project(fact, roleHolders, domainDns, forestDns));
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            DrainMessages();
        }
        catch (PipelineStoppedException)
        {
            throw;
        }
        catch (Exception ex) when (WriteLdapError(ex, Server)) { }
    }

    private PSObject Project(
        DomainControllerFacts fact, IReadOnlyList<(string Role, string? Holder)> roleHolders,
        string? domainDns, string? forestDns)
    {
        var pso = new PSObject();
        pso.TypeNames.Insert(0, "ADx.DomainController");

        void Add(string name, object? value) => pso.Properties.Add(new PSNoteProperty(name, value));

        Add("Name", LdapConvert.FirstRdnValue(fact.ServerDn));
        Add("HostName", fact.HostName);
        Add("Site", fact.Site);
        Add("Domain", domainDns);
        Add("Forest", forestDns);
        Add("IsGlobalCatalog", fact.IsGlobalCatalog);
        Add("IsReadOnly", fact.IsReadOnly);
        Add("OperatingSystem", fact.OperatingSystem);
        Add("OperationMasterRoles", OperationMasterRolesFor(fact.HostName, roleHolders));
        Add("InvocationId", fact.InvocationId);
        Add("NTDSSettingsObjectDN", fact.NtdsSettingsDn);
        Add("ServerObjectDN", fact.ServerDn);
        Add("ComputerObjectDN", fact.ComputerDn);

        return pso;
    }

    private IReadOnlyList<(string Role, string? Holder)> ReadForestAndDomainRoleHolders(
        LdapRootDse rootDse, string? domainNc)
    {
        // Schema and DomainNaming are forest-wide; PDC/RID/Infrastructure are per-domain and
        // resolved against the connected domain, which is the scope -Filter * enumerates.
        return new (string, string?)[]
        {
            ("SchemaMaster", RoleOwnerToHostname(
                ReadSingleValue(rootDse.SchemaNamingContext ?? string.Empty, "fSMORoleOwner"))),
            ("DomainNamingMaster", RoleOwnerToHostname(
                ReadSingleValue($"CN=Partitions,{rootDse.ConfigurationNamingContext}", "fSMORoleOwner"))),
            ("PDCEmulator", RoleOwnerToHostname(ReadSingleValue(domainNc ?? string.Empty, "fSMORoleOwner"))),
            ("RIDMaster", RoleOwnerToHostname(
                ReadSingleValue($"CN=RID Manager$,CN=System,{domainNc}", "fSMORoleOwner"))),
            ("InfrastructureMaster", RoleOwnerToHostname(
                ReadSingleValue($"CN=Infrastructure,{domainNc}", "fSMORoleOwner"))),
        };
    }

    private static bool MatchesIdentity(DomainControllerFacts fact, string identity)
    {
        // Hostname (full or short), or any of the DC's DNs.
        if (string.Equals(fact.HostName, identity, StringComparison.OrdinalIgnoreCase)) return true;

        var shortName = fact.HostName is null ? null
            : fact.HostName.Split('.', 2)[0];
        if (string.Equals(shortName, identity, StringComparison.OrdinalIgnoreCase)) return true;

        // The server object CN is the short name too (a DC whose dNSHostName is unset).
        if (string.Equals(LdapConvert.FirstRdnValue(fact.ServerDn), identity, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(fact.ServerDn, identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fact.NtdsSettingsDn, identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fact.ComputerDn, identity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The FSMO roles a DC holds: the role names whose holder hostname equals this DC's. Pure
    /// and case-insensitive; a null/blank hostname holds nothing. Exposed for offline testing
    /// (no DC needed), the pattern the module uses for any non-trivial cmdlet logic.
    /// </summary>
    internal static string[] OperationMasterRolesFor(
        string? hostName, IReadOnlyList<(string Role, string? Holder)> roleHolders)
    {
        if (string.IsNullOrWhiteSpace(hostName)) return Array.Empty<string>();

        return roleHolders
            .Where(r => string.Equals(r.Holder, hostName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Role)
            .ToArray();
    }
}
