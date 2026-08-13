using System.DirectoryServices.Protocols;
using System.Management.Automation;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Base;

/// <summary>
/// Base for the domain/forest topology cmdlets (Get-ADxDomain, Get-ADxForest,
/// Get-ADxDomainController, Get-ADxDefaultDomainPasswordPolicy). These are not object
/// searches: each reads a handful of well-known objects -- the domain NC head, the
/// Partitions container's crossRefs, and the config-partition server/nTDSDSA objects --
/// and hand-builds one fixed-shape PSObject, the Get-ADxRootDse pattern rather than the
/// preset/projector pipeline.
/// <para>
/// Only directory-touching glue lives here (it needs <see cref="ADxCmdletBase.GetConnection"/>,
/// so no offline test can reach it); every branchy decode these helpers feed lives in
/// <see cref="AdTopology"/> where xUnit covers it without a DC.
/// </para>
/// </summary>
public abstract class ADxTopologyCmdletBase : ADxCmdletBase
{
    /// <summary>
    /// Base-scope read of one object; null when it does not exist. A null/blank DN also
    /// yields null rather than throwing -- an unresolved naming context (a non-AD server, or
    /// a role read whose target NC was absent) is a "cannot read this" answer, not a crash.
    /// </summary>
    protected LdapEntry? ReadEntry(string distinguishedName, params string[] attributes)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName)) return null;

        var entry = GetConnection()
            .ReadEntryAsync(distinguishedName, attributes, CancellationToken)
            .GetAwaiter().GetResult();
        DrainMessages();
        return entry;
    }

    /// <summary>
    /// Best-effort read: a cross-partition referral or other directory error yields null
    /// rather than aborting. In a multi-domain forest a DC's computer account lives in a
    /// partition this bind may not host, and a base read of it returns a referral (not
    /// NoSuchObject); enumerating DCs must tolerate that and leave the foreign DC's
    /// domain-partition fields null, per the documented contract, rather than failing the
    /// whole cmdlet and returning nothing -- including for the readable local domain.
    /// </summary>
    protected LdapEntry? TryReadEntry(string distinguishedName, params string[] attributes)
    {
        try
        {
            return ReadEntry(distinguishedName, attributes);
        }
        catch (DirectoryOperationException)
        {
            DrainMessages();
            return null;
        }
        catch (LdapException)
        {
            DrainMessages();
            return null;
        }
    }

    /// <summary>
    /// Search under the configuration naming context. The topology cmdlets are the first
    /// consumers of this partition in the module; result sets here are small (sites,
    /// servers, crossRefs), so this collects rather than streams.
    /// </summary>
    protected IReadOnlyList<LdapEntry> SearchConfig(
        string relativeBase, string filter, LdapScope scope, params string[] attributes)
    {
        var configNc = GetConnection().RootDse.ConfigurationNamingContext;
        if (string.IsNullOrWhiteSpace(configNc))
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    "The server did not publish a configurationNamingContext; the directory " +
                    "topology cannot be read. This server does not look like an Active Directory " +
                    "domain controller."),
                "NoConfigurationNamingContext", ErrorCategory.InvalidOperation, Server));
        }

        var searchBase = string.IsNullOrEmpty(relativeBase) ? configNc! : $"{relativeBase},{configNc}";
        return CollectSearch(searchBase, filter, scope, attributes);
    }

    /// <summary>Paged search from an arbitrary base, collected into a list.</summary>
    protected IReadOnlyList<LdapEntry> CollectSearch(
        string searchBase, string filter, LdapScope scope, params string[] attributes)
    {
        var spec = new LdapSearchSpec(searchBase, filter, attributes, scope, PageSize: 1000, SizeLimit: 0);
        var results = new List<LdapEntry>();

        var iterator = new LdapPageIterator(GetConnection());
        var enumerator = iterator.StreamAsync(spec, maxItems: 0, onPageComplete: null,
            skipFirst: 0, warning: EnqueueWarning,
            cancellationToken: CancellationToken).GetAsyncEnumerator(CancellationToken);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                results.Add(enumerator.Current);
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        DrainMessages();
        return results;
    }

    /// <summary>
    /// Resolve an <c>fSMORoleOwner</c> value (an nTDSDSA DN) to the role holder's DNS
    /// hostname via its parent server object. Null when the owner is unreadable -- the
    /// caller decides whether that is an omission or an error. A deleted role holder leaves
    /// a mangled DN (containing <c>\0ADEL:</c>), which reads as no-such-object here and so
    /// correctly comes back null rather than a ghost hostname.
    /// </summary>
    protected string? RoleOwnerToHostname(string? ntdsSettingsDn)
    {
        var serverDn = AdTopology.NtdsSettingsToServerDn(ntdsSettingsDn);
        if (serverDn is null) return null;

        return ReadEntry(serverDn, "dNSHostName")?.GetString("dNSHostName");
    }

    /// <summary>
    /// Read one attribute of one object -- the fSMO-role read, mostly. Null when the object
    /// or the attribute is absent.
    /// </summary>
    protected string? ReadSingleValue(string distinguishedName, string attribute) =>
        ReadEntry(distinguishedName, attribute)?.GetString(attribute);

    /// <summary>
    /// A domain controller, joined across its three defining objects: the config-partition
    /// nTDSDSA (GC flag, RODC flag, invocation id), its parent server object (hostname, site,
    /// serverReference), and the domain-partition computer account (OS). The computer-account
    /// fields are null for a DC in another domain whose partition this bind cannot read --
    /// honest absence, never a guessed value. IsReadOnly deliberately does NOT come from the
    /// computer account: the nTDSDSARO class check answers from forest-replicated config data,
    /// so it stays correct where the computer read fails.
    /// </summary>
    protected sealed record DomainControllerFacts(
        string? HostName,
        string? Site,
        string ServerDn,
        string NtdsSettingsDn,
        string? ComputerDn,
        string? DomainNamingContext,
        bool IsGlobalCatalog,
        bool IsReadOnly,
        Guid? InvocationId,
        string? OperatingSystem);

    /// <summary>
    /// Every domain controller in the forest, one <see cref="DomainControllerFacts"/> each.
    /// A server object with no nTDSDSA child is a demoted or non-DC machine and is excluded.
    /// The domain-partition computer read is best-effort: unreadable (foreign domain) leaves
    /// its fields null without dropping the DC.
    /// </summary>
    protected IReadOnlyList<DomainControllerFacts> CollectDomainControllerFacts()
    {
        var ntdsList = SearchConfig("CN=Sites", "(objectClass=nTDSDSA)", LdapScope.Subtree,
            "options", "invocationId", "objectClass");

        var servers = SearchConfig("CN=Sites", "(objectClass=server)", LdapScope.Subtree,
            "dNSHostName", "serverReference");
        var serverByDn = new Dictionary<string, LdapEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers) serverByDn[server.DistinguishedName] = server;

        var facts = new List<DomainControllerFacts>();
        foreach (var ntds in ntdsList)
        {
            var serverDn = AdTopology.NtdsSettingsToServerDn(ntds.DistinguishedName);
            if (serverDn is null) continue;
            serverByDn.TryGetValue(serverDn, out var server);

            var isGc = AdTopology.NtdsIsGlobalCatalog(ntds.GetInt32("options") ?? 0);
            var computerDn = server?.GetString("serverReference");

            string? operatingSystem = null;
            if (!string.IsNullOrWhiteSpace(computerDn))
            {
                // Best-effort: a foreign-domain DC's computer object is in a partition this
                // bind may not host, so a referral here must not abort the enumeration.
                var computer = TryReadEntry(computerDn!, "operatingSystem");
                if (computer is not null)
                    operatingSystem = computer.GetString("operatingSystem");
            }

            facts.Add(new DomainControllerFacts(
                HostName: server?.GetString("dNSHostName"),
                Site: AdTopology.SiteFromServerDn(serverDn),
                ServerDn: serverDn,
                NtdsSettingsDn: ntds.DistinguishedName,
                ComputerDn: computerDn,
                DomainNamingContext: LdapConvert.DomainNamingContext(computerDn),
                IsGlobalCatalog: isGc,
                IsReadOnly: AdTopology.NtdsIsReadOnly(ntds.GetStrings("objectClass")),
                InvocationId: LdapConvert.ObjectGuid(ntds.GetBytes("invocationId")),
                OperatingSystem: operatingSystem));
        }
        return facts;
    }
}
