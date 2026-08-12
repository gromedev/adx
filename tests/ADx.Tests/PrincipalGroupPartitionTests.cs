using ADx.Cmdlets.Cmdlets.Directory;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// Get-ADxPrincipalGroupMembership's cross-partition warning gate. The rule the live GC run
/// exposed: on a plain bind anything outside the search NC is dropped, but a Global Catalog
/// subtree search also returns partitions namespace-subordinate to the base, so those must NOT
/// be warned about (they came back).
/// </summary>
public class PrincipalGroupPartitionTests
{
    private const string Root = "DC=pentest,DC=lab";
    private const string Child = "DC=child,DC=pentest,DC=lab";
    private const string Partner = "DC=partner,DC=lab";

    [Fact]
    public void SamePartition_IsNeverExcluded()
    {
        Assert.False(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(Root, Root, isGlobalCatalog: false));
        Assert.False(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(Root, Root, isGlobalCatalog: true));
    }

    [Fact]
    public void ForeignPartition_OnPlainBind_IsExcluded()
    {
        // 389/636 hosts one partition; a child-domain membership is genuinely dropped -> warn.
        Assert.True(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(Child, Root, isGlobalCatalog: false));
    }

    [Fact]
    public void SubordinatePartition_OnGlobalCatalog_IsNotExcluded()
    {
        // The live case: a GC subtree search from the forest root returns child-domain groups,
        // so warning that they are excluded would be a false positive.
        Assert.False(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(Child, Root, isGlobalCatalog: true));
    }

    [Fact]
    public void NonSubordinatePartition_OnGlobalCatalog_IsStillExcluded()
    {
        // A GC bound to the CHILD sees the parent as namespace-SUPERIOR, not subordinate, so a
        // parent-domain membership is not returned by a search rooted at the child -> warn.
        Assert.True(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(Root, Child, isGlobalCatalog: true));
        // A different tree (separate forest) is never subordinate either.
        Assert.True(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(Partner, Root, isGlobalCatalog: true));
    }

    [Fact]
    public void UnreadableMemberNc_IsNotExcluded()
    {
        // No partition could be parsed from the DN: not something to warn about.
        Assert.False(GetADxPrincipalGroupMembership.IsGenuinelyExcluded(null, Root, isGlobalCatalog: false));
    }
}
