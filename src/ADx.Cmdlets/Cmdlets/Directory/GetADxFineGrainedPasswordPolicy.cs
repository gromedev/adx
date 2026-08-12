using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxFineGrainedPasswordPolicy: drop-in for RSAT's Get-ADFineGrainedPasswordPolicy.
/// Fine-grained password policies are msDS-PasswordSettings (PSO) objects in
/// CN=Password Settings Container,CN=System under the domain head; the search defaults there.
/// Age/lockout durations surface as positive TimeSpans (the Interval syntax); AppliesTo is the
/// forward-linked DN list. Identity resolves by policy name, DN, or GUID.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxFineGrainedPasswordPolicy", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxFineGrainedPasswordPolicy : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.FineGrainedPasswordPolicy;
}
