using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxServiceAccount: drop-in replacement for RSAT's Get-ADServiceAccount over raw LDAP.
/// Returns both standalone (msDS-ManagedServiceAccount) and group-managed
/// (msDS-GroupManagedServiceAccount) accounts via the base-class objectClass filter. Like the
/// other presets it is the [Cmdlet] attribute plus the schema row; the '$'-suffix identity
/// retry is the same as Get-ADxComputer's.
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxServiceAccount", DefaultParameterSetName = FilterSet)]
[OutputType(typeof(PSObject))]
public sealed class GetADxServiceAccount : ADxObjectCmdletBase
{
    protected override AdObjectSchema ObjectSchema => AdObjectSchema.ServiceAccount;
}
