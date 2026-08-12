using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxRootDse: read the directory's RootDSE.
/// <para>
/// The first thing to run against an unfamiliar environment: it answers "can this host
/// reach a domain controller, which one, and what does it support" in a single round trip,
/// which makes every later failure diagnosable. It is also the portable replacement for
/// the Windows-only <c>ActiveDirectory.Domain.GetCurrentDomain()</c>.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxRootDse")]
[OutputType(typeof(LdapRootDse))]
public sealed class GetADxRootDse : ADxCmdletBase
{
    /// <summary>List every control OID the server advertises, not just the known ones.</summary>
    [Parameter]
    public SwitchParameter IncludeSupportedControls { get; set; }

    protected override void ProcessRecord()
    {
        try
        {
            var client = GetConnection();
            var rootDse = client.RootDse;

            var pso = new PSObject();
            pso.TypeNames.Insert(0, "ADx.RootDse");

            pso.Properties.Add(new PSNoteProperty("Server", client.ConnectedServer));
            pso.Properties.Add(new PSNoteProperty("DnsHostName", rootDse.DnsHostName));
            pso.Properties.Add(new PSNoteProperty("ServerName", rootDse.ServerName));
            pso.Properties.Add(new PSNoteProperty("DefaultNamingContext", rootDse.DefaultNamingContext));
            pso.Properties.Add(new PSNoteProperty("ConfigurationNamingContext", rootDse.ConfigurationNamingContext));
            pso.Properties.Add(new PSNoteProperty("SchemaNamingContext", rootDse.SchemaNamingContext));
            pso.Properties.Add(new PSNoteProperty("RootDomainNamingContext", rootDse.RootDomainNamingContext));
            pso.Properties.Add(new PSNoteProperty("HighestCommittedUsn", rootDse.HighestCommittedUsn));
            pso.Properties.Add(new PSNoteProperty("DomainControllerFunctionality", rootDse.DomainControllerFunctionality));
            pso.Properties.Add(new PSNoteProperty("IsActiveDirectory", rootDse.IsActiveDirectory));
            pso.Properties.Add(new PSNoteProperty("SupportsPagedResults", rootDse.SupportsPagedResults));
            pso.Properties.Add(new PSNoteProperty("SupportsDirSync", rootDse.SupportsDirSync));
            pso.Properties.Add(new PSNoteProperty("SupportsDomainScope", rootDse.SupportsDomainScope));
            pso.Properties.Add(new PSNoteProperty("SupportedControlCount", rootDse.SupportedControls.Count));

            if (IncludeSupportedControls.IsPresent)
            {
                pso.Properties.Add(new PSNoteProperty(
                    "SupportedControls", rootDse.SupportedControls.OrderBy(c => c).ToArray()));
            }

            // Paged results is not optional for this module: without it every collection
            // silently truncates at the server's MaxPageSize.
            if (!rootDse.SupportsPagedResults && rootDse.IsActiveDirectory)
            {
                WriteWarning(
                    "The server does not advertise paged results (1.2.840.113556.1.4.319). " +
                    "Large searches will be truncated at the server's page limit.");
            }

            WriteObject(pso);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            DrainMessages();
        }
        catch (Exception ex) when (WriteLdapError(ex, Server)) { }
    }
}
