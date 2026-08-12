using ADx.Cmdlets.Cmdlets.Directory;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// The FSMO-role comparison behind Get-ADxDomainController's OperationMasterRoles. Extracted
/// as an internal static pure function (reached via InternalsVisibleTo) because the cmdlet
/// itself has no offline test seam -- the same pattern as
/// GetADxPrincipalGroupMembership.IsGenuinelyExcluded.
/// </summary>
public class DomainControllerRolesTests
{
    private static readonly (string Role, string? Holder)[] FiveRoles =
    {
        ("SchemaMaster", "dc1.corp.com"),
        ("DomainNamingMaster", "dc1.corp.com"),
        ("PDCEmulator", "dc1.corp.com"),
        ("RIDMaster", "dc1.corp.com"),
        ("InfrastructureMaster", "dc2.corp.com"),
    };

    [Fact]
    public void HolderOfAllFive_GetsAllButTheOneItDoesNotHold()
    {
        var roles = GetADxDomainController.OperationMasterRolesFor("dc1.corp.com", FiveRoles);

        Assert.Equal(
            new[] { "SchemaMaster", "DomainNamingMaster", "PDCEmulator", "RIDMaster" },
            roles);
    }

    [Fact]
    public void HolderOfOneRole_GetsOnlyThatRole()
    {
        var roles = GetADxDomainController.OperationMasterRolesFor("dc2.corp.com", FiveRoles);
        Assert.Equal(new[] { "InfrastructureMaster" }, roles);
    }

    [Fact]
    public void NonHolder_GetsNothing()
    {
        Assert.Empty(GetADxDomainController.OperationMasterRolesFor("dc3.corp.com", FiveRoles));
    }

    [Fact]
    public void ComparisonIsCaseInsensitive()
    {
        var roles = GetADxDomainController.OperationMasterRolesFor("DC2.CORP.COM", FiveRoles);
        Assert.Equal(new[] { "InfrastructureMaster" }, roles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlankHostname_HoldsNothing(string? hostName)
    {
        Assert.Empty(GetADxDomainController.OperationMasterRolesFor(hostName, FiveRoles));
    }

    [Fact]
    public void UnreadableRoleHolder_DoesNotFalselyMatchABlankHost()
    {
        // A role whose holder we could not resolve (null) must not match a DC whose hostname
        // is also unknown -- else an unreadable DC would claim every unreadable role.
        var roles = new (string, string?)[] { ("PDCEmulator", null) };
        Assert.Empty(GetADxDomainController.OperationMasterRolesFor(null, roles));
    }
}
