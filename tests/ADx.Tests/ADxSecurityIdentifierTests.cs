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
