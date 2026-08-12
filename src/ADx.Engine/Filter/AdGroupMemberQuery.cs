using System.Globalization;

namespace ADx.Engine.Filter;

/// <summary>
/// Builds the LDAP filters behind Get-ADxGroupMember / Get-ADxGroupNested. Pure functions of
/// (group DN, primary-group RID), built on the M1 AST so DN escaping is decided by
/// <see cref="LdapAssertionValue"/> rather than string concatenation.
/// <para>
/// The membership queries deliberately search by <c>memberOf</c> instead of reading the
/// group's <c>member</c> attribute and resolving each DN: one server-side paged search
/// returns every member as a full entry, sidesteps <c>MaxValRange</c> range retrieval
/// entirely for enumeration, and folds in the one membership class the <c>member</c>/
/// <c>memberOf</c> link pair does not carry -- PRIMARY membership. A user's primary group
/// (their <c>primaryGroupID</c> RID, e.g. every user in Domain Users) appears in neither
/// attribute; it is reconciled here by OR-ing <c>primaryGroupID=&lt;group RID&gt;</c> into
/// the filter, which is exactly what RSAT's Get-ADGroupMember does under the hood.
/// </para>
/// </summary>
public static class AdGroupMemberQuery
{
    /// <summary>
    /// Direct members: anything whose <c>memberOf</c> names the group, plus anything whose
    /// primary group it is. Includes nested groups themselves (they are direct members),
    /// matching RSAT's non-recursive behaviour.
    /// </summary>
    public static string DirectMembers(string groupDn, uint? primaryGroupRid) =>
        AdFilterEmitter.Emit(WithPrimaryGroup(
            new AdFilterEquality("memberOf", LdapAssertionValue.Exact(groupDn)),
            primaryGroupRid));

    /// <summary>
    /// Transitive members via matching rule 1.2.840.113556.1.4.1941 (LDAP_MATCHING_RULE_IN_CHAIN),
    /// excluding the nested groups themselves -- RSAT's -Recursive returns only the leaves.
    /// <para>
    /// <paramref name="primaryGroupRids"/> must carry the RIDs of the target group AND every
    /// group nested inside it, not just the target's. Primary membership creates no
    /// member/memberOf link, so the 1941 chain cannot see a user whose only path into the
    /// target is "primary group of a nested group" -- e.g. every ordinary user reaches
    /// BUILTIN\Users solely via primaryGroupID=513 (Domain Users), which is nested in it by
    /// default. A RID arm per nested group is the only way to include them from a single
    /// wire query; the caller enumerates the nested groups (they are one NestedGroups
    /// search away) and passes the combined set.
    /// </para>
    /// </summary>
    public static string TransitiveMembers(string groupDn, IReadOnlyCollection<uint> primaryGroupRids) =>
        AdFilterEmitter.Emit(new AdFilterAnd(new[]
        {
            WithPrimaryGroups(
                new AdFilterRecursiveMatch("memberOf", LdapAssertionValue.Exact(groupDn)),
                primaryGroupRids),
            new AdFilterNot(new AdFilterRaw("(objectCategory=group)"))
        }));

    /// <summary>
    /// The groups a principal belongs to DIRECTLY, for Get-ADxPrincipalGroupMembership -- the
    /// reverse of <see cref="DirectMembers"/>. Anything whose <c>member</c> names the principal,
    /// OR'd with the principal's PRIMARY group, matched by its SID.
    /// <para>
    /// This is the mirror image of the member query's primaryGroupID trick. A principal's
    /// primary group (Domain Users for an ordinary user) appears in neither the principal's
    /// <c>memberOf</c> nor the group's <c>member</c>; RSAT's Get-ADPrincipalGroupMembership
    /// still returns it. It is reconciled here by OR-ing <c>objectSid=&lt;primary group SID&gt;</c>
    /// -- the primary group's SID is the principal's own account-domain SID with the RID
    /// replaced by <c>primaryGroupID</c>, so the caller computes it and passes the bytes. When
    /// the SID is unavailable (unreadable objectSid, or no primaryGroupID) the arm is dropped
    /// and only explicit <c>member</c> links are returned.
    /// </para>
    /// <para>
    /// Searching by the group's forward <c>member</c> link rather than reading the principal's
    /// <c>memberOf</c> keeps this immune to MaxValRange the same way the member query is: a
    /// principal in more than 1,500 groups would have a range-suffixed <c>memberOf</c>, but the
    /// forward search returns every group as a full entry regardless.
    /// </para>
    /// </summary>
    public static string PrincipalGroups(string principalDn, byte[]? primaryGroupSid)
    {
        var memberArm = new AdFilterEquality("member", LdapAssertionValue.Exact(principalDn));

        AdFilterNode membership = primaryGroupSid is { Length: > 0 }
            ? new AdFilterOr(new AdFilterNode[]
            {
                memberArm,
                new AdFilterEquality("objectSid", LdapAssertionValue.Binary(primaryGroupSid))
            })
            : memberArm;

        // The results are groups by definition; constrain the class so nothing else can slip
        // in and so the output matches the Group projection the cmdlet applies.
        return AdFilterEmitter.Emit(new AdFilterAnd(new AdFilterNode[]
        {
            new AdFilterRaw("(objectCategory=group)"),
            membership
        }));
    }

    /// <summary>
    /// Every group nested (directly or transitively) inside the target: the flattened
    /// nesting tree, which is the audit question "what does membership of X actually grant".
    /// </summary>
    public static string NestedGroups(string groupDn) =>
        AdFilterEmitter.Emit(new AdFilterAnd(new AdFilterNode[]
        {
            new AdFilterRaw("(objectCategory=group)"),
            new AdFilterRecursiveMatch("memberOf", LdapAssertionValue.Exact(groupDn))
        }));

    private static AdFilterNode WithPrimaryGroup(AdFilterNode membershipArm, uint? primaryGroupRid) =>
        primaryGroupRid is { } rid
            ? WithPrimaryGroups(membershipArm, new[] { rid })
            : membershipArm;

    private static AdFilterNode WithPrimaryGroups(AdFilterNode membershipArm, IReadOnlyCollection<uint> rids)
    {
        if (rids is not { Count: > 0 }) return membershipArm;

        var operands = new List<AdFilterNode>(rids.Count + 1) { membershipArm };
        foreach (var rid in rids)
        {
            operands.Add(new AdFilterEquality(
                "primaryGroupID",
                LdapAssertionValue.Verbatim(rid.ToString(CultureInfo.InvariantCulture))));
        }

        return new AdFilterOr(operands);
    }
}
