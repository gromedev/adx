using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxGroup: drop-in replacement for RSAT's Get-ADGroup over raw LDAP. GroupScope and
/// GroupCategory are decoded from groupType by the shared projector; note that reading the
/// full member list of a large group is Get-ADxGroupMember's job (range retrieval), not
/// -Properties Members.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxGroup", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxGroup : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.Group;
}
