using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxObject: the untyped preset, RSAT's Get-ADObject. No base class filter (a bare
/// -Filter * really does mean everything under the search base), and -Identity accepts only
/// DN or objectGUID -- both matching the RSAT counterpart.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxObject", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxObject : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.AnyObject;
}
