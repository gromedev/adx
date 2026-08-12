using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxDomain: the connected domain's identity, FSMO roles, well-known containers and
/// directory servers, matching RSAT's Get-ADDomain. No <c>-Identity</c>: <c>-Server</c>
/// selects the domain (RSAT's domain targeting is the netlogon DC locator, not LDAP).
/// <para>
/// Honest subset: every emitted property is produced from a real read. RSAT properties that
/// need machinery ADx does not have (LastLogonReplicationInterval semantics, subordinate
/// references, a trust walk) are omitted rather than emitted as null -- absence is visible,
/// a null value is a lie. See the module help for the exact omissions.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxDomain")]
[OutputType(typeof(PSObject))]
public sealed class GetADxDomain : ADxTopologyCmdletBase
{
    protected override void ProcessRecord()
    {
        try
        {
            var rootDse = GetConnection().RootDse;
            var domainNc = rootDse.DefaultNamingContext;
            if (string.IsNullOrWhiteSpace(domainNc))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "The server did not publish a defaultNamingContext; there is no domain to read."),
                    "NoDefaultNamingContext", ErrorCategory.InvalidOperation, Server));
            }

            var head = ReadEntry(domainNc!,
                "objectSid", "objectGUID", "gPLink", "wellKnownObjects", "fSMORoleOwner",
                "managedBy", "msDS-Behavior-Version", "msDS-AllowedDNSSuffixes", "name",
                "objectClass", "distinguishedName");
            if (head is null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException($"The domain head '{domainNc}' could not be read."),
                    "DomainHeadUnreadable", ErrorCategory.ObjectNotFound, domainNc));
                return;
            }

            // All domain crossRefs once, then pick this domain's and its children.
            var crossRefs = SearchConfig("CN=Partitions", "(objectClass=crossRef)", LdapScope.OneLevel,
                "nCName", "dnsRoot", "nETBIOSName", "trustParent", "systemFlags", "distinguishedName");
            var thisCrossRef = crossRefs.FirstOrDefault(cr =>
                string.Equals(cr.GetString("nCName"), domainNc, StringComparison.OrdinalIgnoreCase));

            var wellKnown = AdTopology.ParseWellKnownObjects(head.GetStrings("wellKnownObjects"));
            var dcFacts = CollectDomainControllerFacts()
                .Where(f => string.Equals(f.DomainNamingContext, domainNc, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var pso = new PSObject();
            pso.TypeNames.Insert(0, "ADx.Domain");

            void Add(string name, object? value) => pso.Properties.Add(new PSNoteProperty(name, value));

            Add("DistinguishedName", head.DistinguishedName);
            Add("Name", head.GetString("name"));
            Add("ObjectClass", head.GetStrings("objectClass") is { Count: > 0 } c ? c[^1] : null);
            Add("ObjectGUID", LdapConvert.ObjectGuid(head.GetBytes("objectGUID")));
            Add("DNSRoot", thisCrossRef?.GetString("dnsRoot") ?? AdTopology.DnsNameFromNamingContext(domainNc));
            Add("NetBIOSName", thisCrossRef?.GetString("nETBIOSName"));
            Add("DomainMode", AdTopology.DecodeDomainMode(head.GetInt32("msDS-Behavior-Version") ?? 0));
            Add("DomainSID", ADxSecurityIdentifier.FromBinary(head.GetBytes("objectSid")));
            Add("Forest", AdTopology.DnsNameFromNamingContext(rootDse.RootDomainNamingContext));
            Add("ParentDomain", ParentDomainDns(thisCrossRef, crossRefs));
            Add("ChildDomains", ChildDomainDnsRoots(thisCrossRef, crossRefs));

            Add("PDCEmulator", RoleOwnerToHostname(head.GetString("fSMORoleOwner")));
            Add("RIDMaster", RoleOwnerToHostname(
                ReadSingleValue($"CN=RID Manager$,CN=System,{domainNc}", "fSMORoleOwner")));
            Add("InfrastructureMaster", RoleOwnerToHostname(
                ReadSingleValue($"CN=Infrastructure,{domainNc}", "fSMORoleOwner")));

            Add("ReplicaDirectoryServers", dcFacts
                .Where(f => !f.IsReadOnly && f.HostName is not null)
                .Select(f => f.HostName!).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray());
            Add("ReadOnlyReplicaDirectoryServers", dcFacts
                .Where(f => f.IsReadOnly && f.HostName is not null)
                .Select(f => f.HostName!).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray());

            // Well-known containers, in the order Get-ADDomain lists them.
            foreach (var (property, guid) in AdTopology.WellKnownContainerGuids)
                Add(property, wellKnown.GetValueOrDefault(guid));

            Add("LinkedGroupPolicyObjects", AdTopology.ParseGpLink(head.GetString("gPLink")).ToArray());
            Add("ManagedBy", head.GetString("managedBy"));
            Add("AllowedDNSSuffixes", head.GetStrings("msDS-AllowedDNSSuffixes").ToArray());

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

    private static string? ParentDomainDns(LdapEntry? thisCrossRef, IReadOnlyList<LdapEntry> crossRefs)
    {
        var parentDn = thisCrossRef?.GetString("trustParent");
        if (string.IsNullOrWhiteSpace(parentDn)) return null; // forest root domain

        return crossRefs
            .FirstOrDefault(cr => string.Equals(cr.DistinguishedName, parentDn, StringComparison.OrdinalIgnoreCase))
            ?.GetString("dnsRoot");
    }

    private static string[] ChildDomainDnsRoots(LdapEntry? thisCrossRef, IReadOnlyList<LdapEntry> crossRefs)
    {
        if (thisCrossRef is null) return Array.Empty<string>();

        return crossRefs
            .Where(cr => string.Equals(cr.GetString("trustParent"), thisCrossRef.DistinguishedName,
                StringComparison.OrdinalIgnoreCase))
            .Select(cr => cr.GetString("dnsRoot"))
            .Where(dns => dns is not null)
            .Select(dns => dns!)
            .OrderBy(dns => dns, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
