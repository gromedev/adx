using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxComputer: drop-in replacement for RSAT's Get-ADComputer over raw LDAP. A plain-name
/// -Identity retries with the '$' suffix (computer sAMAccountNames end in '$', which nobody
/// types), per the schema flag.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxComputer", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxComputer : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.Computer;
}
