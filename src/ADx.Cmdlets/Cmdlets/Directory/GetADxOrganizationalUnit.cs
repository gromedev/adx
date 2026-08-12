using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxOrganizationalUnit: drop-in replacement for RSAT's Get-ADOrganizationalUnit over
/// raw LDAP. Like the other presets, this is the [Cmdlet] attribute plus the schema row --
/// the OU-specific behaviour (StreetAddress from the <c>street</c> attribute, and
/// LinkedGroupPolicyObjects parsed from <c>gPLink</c>) is carried entirely by
/// <see cref="AdObjectSchema.OrganizationalUnit"/> and the projector.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxOrganizationalUnit", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxOrganizationalUnit : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.OrganizationalUnit;
}
