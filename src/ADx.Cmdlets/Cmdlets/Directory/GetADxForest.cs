using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxForest: the connected forest's identity, FSMO roles, domains, global catalogs and
/// sites, matching RSAT's Get-ADForest. The forest is read from the configuration partition,
/// which every domain in the forest shares. No <c>-Identity</c>: <c>-Server</c> picks a DC.
/// <para>
/// Omitted in v1 (documented, not null-filled): ApplicationPartitions and
/// CrossForestReferences, which need extra config reads and trust data.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxForest")]
[OutputType(typeof(PSObject))]
public sealed class GetADxForest : ADxTopologyCmdletBase
{
    protected override void ProcessRecord()
    {
        try
        {
            var rootDse = GetConnection().RootDse;
            var configNc = rootDse.ConfigurationNamingContext;
            if (string.IsNullOrWhiteSpace(configNc))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "The server did not publish a configurationNamingContext; the forest cannot be read."),
                    "NoConfigurationNamingContext", ErrorCategory.InvalidOperation, Server));
            }

            var partitionsDn = $"CN=Partitions,{configNc}";
            var partitions = ReadEntry(partitionsDn,
                "msDS-Behavior-Version", "fSMORoleOwner", "uPNSuffixes", "msDS-SPNSuffixes");

            var crossRefs = SearchConfig("CN=Partitions", "(objectClass=crossRef)", LdapScope.OneLevel,
                "dnsRoot", "systemFlags");

            var dcFacts = CollectDomainControllerFacts();

            var pso = new PSObject();
            pso.TypeNames.Insert(0, "ADx.Forest");

            void Add(string name, object? value) => pso.Properties.Add(new PSNoteProperty(name, value));

            var rootDomain = AdTopology.DnsNameFromNamingContext(rootDse.RootDomainNamingContext);
            Add("Name", rootDomain);
            Add("RootDomain", rootDomain);
            Add("ForestMode", AdTopology.DecodeForestMode(partitions?.GetInt32("msDS-Behavior-Version") ?? 0));

            Add("SchemaMaster", RoleOwnerToHostname(
                ReadSingleValue(rootDse.SchemaNamingContext ?? string.Empty, "fSMORoleOwner")));
            Add("DomainNamingMaster", RoleOwnerToHostname(partitions?.GetString("fSMORoleOwner")));

            Add("Domains", crossRefs
                .Where(cr => AdTopology.IsDomainCrossRef(cr.GetInt32("systemFlags") ?? 0))
                .Select(cr => cr.GetString("dnsRoot"))
                .Where(d => d is not null).Select(d => d!)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray());

            Add("GlobalCatalogs", dcFacts
                .Where(f => f.IsGlobalCatalog && f.HostName is not null)
                .Select(f => f.HostName!)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray());

            Add("Sites", SearchConfig("CN=Sites", "(objectClass=site)", LdapScope.OneLevel, "name")
                .Select(s => s.GetString("name"))
                .Where(n => n is not null).Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());

            Add("UPNSuffixes", (partitions?.GetStrings("uPNSuffixes") ?? Array.Empty<string>()).ToArray());
            Add("SPNSuffixes", (partitions?.GetStrings("msDS-SPNSuffixes") ?? Array.Empty<string>()).ToArray());
            Add("PartitionsContainer", partitionsDn);

            WriteObject(pso);
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
}
