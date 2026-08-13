using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxGroupNested: every group nested -- directly or transitively -- inside the target,
/// i.e. the flattened nesting tree. RSAT has no counterpart; the closest is running
/// Get-ADGroupMember repeatedly and filtering for groups. This answers the audit question
/// "what does membership of X actually grant" in one server-side query
/// (matching rule 1.2.840.113556.1.4.1941 restricted to objectCategory=group).
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxGroupNested")]
[OutputType(typeof(PSObject))]
public sealed class GetADxGroupNested : ADxGroupQueryCmdletBase
{
    // Groups are never anyone's primary group in practice (primaryGroupID points at
    // Domain Users/Computers/Controllers), so the RID arm does not apply here -- and the
    // base's primary-group warnings (unreadable SID, Global Catalog exclusion) do not either.
    protected override bool UsesPrimaryGroupRids => false;

    protected override (string Filter, AdObjectSchema OutputSchema) BuildQuery(
        GroupMembershipTarget target) =>
        (AdGroupMemberQuery.NestedGroups(target.GroupDn), AdObjectSchema.Group);
}
