using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

public class ADxSecurityIdentifierTests
{
    private const string UserSddl = "S-1-5-21-3623811015-3361044348-30300820-1013";

    [Fact]
    public void Value_IsTheSddlString()
    {
        var sid = new ADxSecurityIdentifier(UserSddl);
        Assert.Equal(UserSddl, sid.Value);
    }

    [Fact]
    public void ToString_IsTheSddlString()
    {
        var sid = new ADxSecurityIdentifier(UserSddl);
        Assert.Equal(UserSddl, sid.ToString());
    }

    [Fact]
    public void AccountDomainSid_IsSddlMinusTrailingRid()
    {
        var sid = new ADxSecurityIdentifier(UserSddl);
        Assert.Equal("S-1-5-21-3623811015-3361044348-30300820", sid.AccountDomainSid);
    }

    [Theory]
    // Matching SecurityIdentifier.AccountDomainSid: null for anything that is not an
    // account SID -- the old last-dash strip fabricated "S-1-5-32" for BUILTIN groups,
    // which every domain has, breaking "is this principal in my domain" comparisons.
    [InlineData("S-1-5-32-544", null)]
    [InlineData("S-1-5-18", null)]
    [InlineData("S-1-1-0", null)]
    // The domain SID is its own account domain.
    [InlineData("S-1-5-21-1-2-3", "S-1-5-21-1-2-3")]
    [InlineData("S-1-5-21-1-2-3-513", "S-1-5-21-1-2-3")]
    public void AccountDomainSid_IsNullForNonAccountSids(string sddl, string? expected)
    {
        Assert.Equal(expected, new ADxSecurityIdentifier(sddl).AccountDomainSid);
    }

    [Fact]
    public void EqualsString_IsCaseInsensitive()
    {
        var sid = new ADxSecurityIdentifier(UserSddl);
        Assert.True(sid.Equals(UserSddl.ToLowerInvariant()));
        Assert.False(sid.Equals("S-1-5-32-544"));
    }

    [Fact]
    public void EqualsAnotherInstance_ComparesByValue()
    {
        var a = new ADxSecurityIdentifier(UserSddl);
        var b = new ADxSecurityIdentifier(UserSddl);
        var c = new ADxSecurityIdentifier("S-1-5-32-544");

        Assert.True(a.Equals(b));
        Assert.False(a.Equals(c));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void FromBinary_RoundTripsThroughSidToSddl()
    {
        var bytes = LdapConvert.SddlToSid(UserSddl);
        var sid = ADxSecurityIdentifier.FromBinary(bytes);

        Assert.NotNull(sid);
        Assert.Equal(UserSddl, sid!.Value);
    }

    [Fact]
    public void FromBinary_Malformed_ReturnsNull()
    {
        Assert.Null(ADxSecurityIdentifier.FromBinary(null));
        Assert.Null(ADxSecurityIdentifier.FromBinary(Array.Empty<byte>()));
    }
}
