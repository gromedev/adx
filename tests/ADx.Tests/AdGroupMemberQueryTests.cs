using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M6: the membership filters. Golden strings, because the wire filter IS the behaviour --
/// a wrong OID or a missing primaryGroupID arm silently changes who counts as a member.
/// </summary>
public class AdGroupMemberQueryTests
{
    private const string GroupDn = "CN=Admins,OU=Groups,DC=corp,DC=com";

    [Fact]
    public void DirectMembers_OrsMemberOfWithPrimaryGroupRid()
    {
        Assert.Equal(
            "(|(memberOf=CN=Admins,OU=Groups,DC=corp,DC=com)(primaryGroupID=512))",
            AdGroupMemberQuery.DirectMembers(GroupDn, 512));
    }

    [Fact]
    public void DirectMembers_WithoutARid_IsJustTheMemberOfArm()
    {
        Assert.Equal(
            "(memberOf=CN=Admins,OU=Groups,DC=corp,DC=com)",
            AdGroupMemberQuery.DirectMembers(GroupDn, null));
    }

    [Fact]
    public void TransitiveMembers_UseTheChainRule_AndExcludeGroups()
    {
        Assert.Equal(
            "(&(|(memberOf:1.2.840.113556.1.4.1941:=CN=Admins,OU=Groups,DC=corp,DC=com)(primaryGroupID=512))" +
            "(!(objectCategory=group)))",
            AdGroupMemberQuery.TransitiveMembers(GroupDn, new uint[] { 512 }));
    }

    [Fact]
    public void TransitiveMembers_WithoutARid()
    {
        Assert.Equal(
            "(&(memberOf:1.2.840.113556.1.4.1941:=CN=Admins,OU=Groups,DC=corp,DC=com)" +
            "(!(objectCategory=group)))",
            AdGroupMemberQuery.TransitiveMembers(GroupDn, Array.Empty<uint>()));
    }

    [Fact]
    public void TransitiveMembers_CarryEveryNestedGroupsRid()
    {
        // The BUILTIN\Users case: a user's ONLY path into the target is
        // primaryGroupID=513 (Domain Users), which is nested inside it. Rule 1941 walks
        // member/memberOf links and primary membership creates none, so without a RID arm
        // per nested group those users are invisible to -Recursive -- the group reads as
        // nearly empty when it in fact contains the whole domain.
        Assert.Equal(
            "(&(|(memberOf:1.2.840.113556.1.4.1941:=CN=Admins,OU=Groups,DC=corp,DC=com)" +
            "(primaryGroupID=544)(primaryGroupID=513)(primaryGroupID=515))" +
            "(!(objectCategory=group)))",
            AdGroupMemberQuery.TransitiveMembers(GroupDn, new uint[] { 544, 513, 515 }));
    }

    [Fact]
    public void NestedGroups_RestrictsTheChainToGroups()
    {
        Assert.Equal(
            "(&(objectCategory=group)(memberOf:1.2.840.113556.1.4.1941:=CN=Admins,OU=Groups,DC=corp,DC=com))",
            AdGroupMemberQuery.NestedGroups(GroupDn));
    }

    [Fact]
    public void GroupDnWithFilterMetacharacters_IsEscaped()
    {
        // A DN containing parens must not corrupt the filter structure.
        var filter = AdGroupMemberQuery.DirectMembers("CN=We(i)rd,DC=corp", 513);
        Assert.Equal("(|(memberOf=CN=We\\28i\\29rd,DC=corp)(primaryGroupID=513))", filter);
    }

    private const string PrincipalDn = "CN=jdoe,OU=Users,DC=corp,DC=com";

    [Fact]
    public void PrincipalGroups_WithoutAPrimaryGroup_IsJustTheMemberArm()
    {
        // No readable primary-group SID: only explicit member links, still constrained to groups.
        Assert.Equal(
            "(&(objectCategory=group)(member=CN=jdoe,OU=Users,DC=corp,DC=com))",
            AdGroupMemberQuery.PrincipalGroups(PrincipalDn, null));
    }

    [Fact]
    public void PrincipalGroups_OrsMemberWithThePrimaryGroupSid()
    {
        // The mirror of DirectMembers' primaryGroupID arm: the primary group is matched by SID
        // (Domain Users, RID 513) because it appears in neither member nor memberOf.
        var primarySid = LdapConvert.SddlToSid("S-1-5-21-1-2-3-513");
        var expected =
            "(&(objectCategory=group)(|(member=CN=jdoe,OU=Users,DC=corp,DC=com)" +
            $"(objectSid={LdapConvert.EscapeBinary(primarySid)})))";

        Assert.Equal(expected, AdGroupMemberQuery.PrincipalGroups(PrincipalDn, primarySid));
    }

    [Fact]
    public void PrincipalGroups_PrincipalDnWithFilterMetacharacters_IsEscaped()
    {
        var filter = AdGroupMemberQuery.PrincipalGroups("CN=We(i)rd,DC=corp", null);
        Assert.Equal("(&(objectCategory=group)(member=CN=We\\28i\\29rd,DC=corp))", filter);
    }

    [Fact]
    public void PrimaryGroupSid_IsAccountDomainSidWithTheRid()
    {
        // The composition the cmdlet runs: principal objectSid -> domain SID -> + primaryGroupID.
        // A user in S-1-5-21-1-2-3 with primaryGroupID 513 is a member of Domain Users
        // (S-1-5-21-1-2-3-513), the primary membership no link carries.
        var principalSid = LdapConvert.SddlToSid("S-1-5-21-1-2-3-1105");
        var domain = LdapConvert.SidDomain(principalSid);

        Assert.Equal("S-1-5-21-1-2-3", domain);
        Assert.Equal(
            LdapConvert.SddlToSid("S-1-5-21-1-2-3-513"),
            LdapConvert.SddlToSid($"{domain}-513"));
    }

    [Fact]
    public void PrincipalDefaults_FetchList_IsTheAdPrincipalSet()
    {
        var list = AdRsatProjector.BuildFetchList(AdObjectSchema.Principal, null, false, out _);

        Assert.Equal(
            new[] { "distinguishedName", "name", "objectClass", "objectGUID", "sAMAccountName", "objectSid" },
            list);
    }

    [Fact]
    public void MembershipTarget_DeduplicatesRids_PreservingOrder()
    {
        // The cycle case: NestedGroups(A) on an A<->B nesting cycle matches A itself, so the
        // target's own RID (545) arrives again in the nested set.
        var target = new ADxGroupQueryCmdletBase.GroupMembershipTarget(
            "CN=Users,CN=Builtin,DC=corp,DC=com",
            PrimaryGroupRid: 545,
            NestedPrimaryGroupRids: new uint[] { 513, 545, 513, 515 });

        Assert.Equal(new uint[] { 545, 513, 515 }, target.AllPrimaryGroupRids);
    }

    [Fact]
    public void MembershipTarget_WithoutOwnRid_IsJustTheNestedSet()
    {
        var target = new ADxGroupQueryCmdletBase.GroupMembershipTarget(
            "CN=G,DC=corp", PrimaryGroupRid: null, NestedPrimaryGroupRids: new uint[] { 513 });

        Assert.Equal(new uint[] { 513 }, target.AllPrimaryGroupRids);
    }

    [Fact]
    public void DomainUsersRid_FromWellKnownSid()
    {
        // The chain the cmdlet runs: group SID -> RID -> primaryGroupID arm. Domain Users'
        // RID is the canonical 513.
        var sid = LdapConvert.SddlToSid("S-1-5-21-1-2-3-513");
        Assert.Equal(513u, LdapConvert.SidRid(sid));
    }
}
