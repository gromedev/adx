using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Cmdlets.Directory;

/// <summary>
/// Get-ADxGroupMember: drop-in replacement for RSAT's Get-ADGroupMember.
/// <para>
/// Enumerates by searching <c>memberOf</c> rather than reading the group's <c>member</c>
/// attribute DN-by-DN: one paged server-side search returns every member as a full entry,
/// immune to MaxValRange, and the filter ORs in <c>primaryGroupID=&lt;group RID&gt;</c> --
/// the membership class the member/memberOf link pair does not carry (every user in Domain
/// Users is a member of it through primaryGroupID alone).
/// </para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ADxGroupMember")]
[OutputType(typeof(PSObject))]
public sealed class GetADxGroupMember : ADxGroupQueryCmdletBase
{
    /// <summary>
    /// All members in the nesting hierarchy via matching rule 1.2.840.113556.1.4.1941,
    /// excluding the nested groups themselves -- like RSAT, -Recursive returns the leaves.
    /// </summary>
    [Parameter]
    public SwitchParameter Recursive { get; set; }

    /// <summary>
    /// Only the recursive form needs them, and it needs them badly: a user whose primary
    /// group is a NESTED group (every ordinary account reaches BUILTIN\Users this way, via
    /// Domain Users) has no memberOf link for the chain rule to follow, so without the
    /// nested groups' RIDs -Recursive would report almost none of them.
    /// </summary>
    protected override bool NeedsNestedPrimaryGroupRids => Recursive.IsPresent;

    protected override (string Filter, AdObjectSchema OutputSchema) BuildQuery(
        GroupMembershipTarget target) =>
        (Recursive.IsPresent
                ? AdGroupMemberQuery.TransitiveMembers(target.GroupDn, target.AllPrimaryGroupRids)
                // Direct membership: only the target group's own primary members count.
                : AdGroupMemberQuery.DirectMembers(target.GroupDn, target.PrimaryGroupRid),
            AdObjectSchema.Principal);
}
