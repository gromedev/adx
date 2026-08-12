using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxDefaultDomainPasswordPolicy: the domain head's password/lockout policy, matching
/// RSAT's Get-ADDefaultDomainPasswordPolicy. One base-scope read of defaultNamingContext.
/// <para>
/// No <c>-Identity</c>/<c>-Current</c>: RSAT's domain targeting rides the netlogon DC
/// locator, which is not LDAP -- here the connected domain (chosen via <c>-Server</c>) IS
/// the target, which is the drop-in case. The four age/duration values are interval
/// attributes (stored as negative 100ns ticks); they surface as positive TimeSpans exactly
/// as RSAT emits them, via <see cref="LdapConvert.Interval(long)"/>.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxDefaultDomainPasswordPolicy")]
[OutputType(typeof(PSObject))]
public sealed class GetADxDefaultDomainPasswordPolicy : ADxTopologyCmdletBase
{
    private static readonly string[] PolicyAttributes =
    {
        "minPwdLength", "pwdHistoryLength", "maxPwdAge", "minPwdAge",
        "lockoutDuration", "lockOutObservationWindow", "lockoutThreshold",
        "pwdProperties", "objectClass", "objectGUID",
    };

    protected override void ProcessRecord()
    {
        try
        {
            var domainNc = GetConnection().RootDse.DefaultNamingContext;
            if (string.IsNullOrWhiteSpace(domainNc))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "The server did not publish a defaultNamingContext, so there is no domain " +
                        "head to read the password policy from."),
                    "NoDefaultNamingContext", ErrorCategory.InvalidOperation, Server));
            }

            var head = ReadEntry(domainNc!, PolicyAttributes);
            if (head is null)
            {
                // RootDSE advertised the NC but the read missed: something is genuinely wrong.
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"The domain head '{domainNc}' could not be read even though RootDSE " +
                        "advertises it. The account may lack read access to the domain object."),
                    "DomainHeadUnreadable", ErrorCategory.ObjectNotFound, domainNc));
                return;
            }

            var pwdProperties = head.GetInt32("pwdProperties");
            var (complexity, reversible) = pwdProperties is null
                ? ((bool?)null, (bool?)null)
                : AdTopology.DecodePwdProperties(pwdProperties.Value);

            var pso = new PSObject();
            pso.TypeNames.Insert(0, "ADx.DefaultDomainPasswordPolicy");

            // RSAT field names and types, alphabetical like its own default rendering.
            pso.Properties.Add(new PSNoteProperty("ComplexityEnabled", complexity));
            pso.Properties.Add(new PSNoteProperty("DistinguishedName", head.DistinguishedName));
            pso.Properties.Add(new PSNoteProperty("LockoutDuration",
                LdapConvert.Interval(head.GetString("lockoutDuration"))));
            pso.Properties.Add(new PSNoteProperty("LockoutObservationWindow",
                LdapConvert.Interval(head.GetString("lockOutObservationWindow"))));
            pso.Properties.Add(new PSNoteProperty("LockoutThreshold", head.GetInt32("lockoutThreshold")));
            pso.Properties.Add(new PSNoteProperty("MaxPasswordAge",
                LdapConvert.Interval(head.GetString("maxPwdAge"))));
            pso.Properties.Add(new PSNoteProperty("MinPasswordAge",
                LdapConvert.Interval(head.GetString("minPwdAge"))));
            pso.Properties.Add(new PSNoteProperty("MinPasswordLength", head.GetInt32("minPwdLength")));
            pso.Properties.Add(new PSNoteProperty("ObjectClass",
                head.GetStrings("objectClass") is { Count: > 0 } classes ? classes[^1] : null));
            pso.Properties.Add(new PSNoteProperty("ObjectGUID",
                LdapConvert.ObjectGuid(head.GetBytes("objectGUID"))));
            pso.Properties.Add(new PSNoteProperty("PasswordHistoryCount", head.GetInt32("pwdHistoryLength")));
            pso.Properties.Add(new PSNoteProperty("ReversibleEncryptionEnabled", reversible));

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
