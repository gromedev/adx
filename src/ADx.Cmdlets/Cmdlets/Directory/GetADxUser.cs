using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxUser: drop-in replacement for RSAT's Get-ADUser over raw LDAP.
/// <para>
/// Everything interesting lives in <see cref="ADxObjectCmdletBase"/> and
/// <see cref="AdObjectSchema.User"/>; this class is the [Cmdlet] attribute plus the table
/// row, which is the design goal -- each further preset is this thin.
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxUser", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxUser : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.User;
}
