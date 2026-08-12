using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

public class AdAttributeSchemaTests
{
    [Theory]
    [InlineData("whenCreated", AdAttributeSyntax.GeneralizedTime)]
    [InlineData("whenChanged", AdAttributeSyntax.GeneralizedTime)]
    [InlineData("pwdLastSet", AdAttributeSyntax.FileTime)]
    [InlineData("lastLogonTimestamp", AdAttributeSyntax.FileTime)]
    [InlineData("userAccountControl", AdAttributeSyntax.Integer)]
    [InlineData("groupType", AdAttributeSyntax.Integer)]
    [InlineData("isDeleted", AdAttributeSyntax.Boolean)]
    [InlineData("objectSid", AdAttributeSyntax.Sid)]
    [InlineData("sIDHistory", AdAttributeSyntax.Sid)]
    [InlineData("objectGUID", AdAttributeSyntax.Guid)]
    [InlineData("memberOf", AdAttributeSyntax.Dn)]
    [InlineData("member", AdAttributeSyntax.Dn)]
    [InlineData("userCertificate", AdAttributeSyntax.Binary)]
    [InlineData("sAMAccountName", AdAttributeSyntax.String)]
    [InlineData("mail", AdAttributeSyntax.String)]
    // 0.2.6 topology: intervals are NOT FileTime (whose <= 0 sentinel would null them all).
    [InlineData("maxPwdAge", AdAttributeSyntax.Interval)]
    [InlineData("minPwdAge", AdAttributeSyntax.Interval)]
    [InlineData("lockoutDuration", AdAttributeSyntax.Interval)]
    [InlineData("lockOutObservationWindow", AdAttributeSyntax.Interval)]
    [InlineData("gPOptions", AdAttributeSyntax.Integer)]
    [InlineData("minPwdLength", AdAttributeSyntax.Integer)]
    [InlineData("pwdHistoryLength", AdAttributeSyntax.Integer)]
    [InlineData("lockoutThreshold", AdAttributeSyntax.Integer)]
    [InlineData("pwdProperties", AdAttributeSyntax.Integer)]
    [InlineData("msDS-Behavior-Version", AdAttributeSyntax.Integer)]
    [InlineData("systemFlags", AdAttributeSyntax.Integer)]
    [InlineData("options", AdAttributeSyntax.Integer)]
    [InlineData("fSMORoleOwner", AdAttributeSyntax.Dn)]
    [InlineData("trustParent", AdAttributeSyntax.Dn)]
    [InlineData("serverReference", AdAttributeSyntax.Dn)]
    [InlineData("invocationId", AdAttributeSyntax.Guid)]
    [InlineData("gPLink", AdAttributeSyntax.String)]
    [InlineData("street", AdAttributeSyntax.String)]
    [InlineData("ou", AdAttributeSyntax.String)]
    public void SyntaxOf_KnownAttributes(string attribute, AdAttributeSyntax expected)
    {
        Assert.Equal(expected, AdAttributeSchema.SyntaxOf(attribute));
    }

    [Fact]
    public void SyntaxOf_UnknownAttribute_DefaultsToString()
    {
        Assert.Equal(AdAttributeSyntax.String, AdAttributeSchema.SyntaxOf("thisIsNotARealAttribute"));
    }

    [Fact]
    public void SyntaxOf_IsCaseInsensitive()
    {
        Assert.Equal(AdAttributeSyntax.Integer, AdAttributeSchema.SyntaxOf("USERACCOUNTCONTROL"));
        Assert.Equal(AdAttributeSyntax.Integer, AdAttributeSchema.SyntaxOf("useraccountcontrol"));
    }

    [Fact]
    public void SyntaxOf_RangeSuffixedAttribute_ResolvesAsBaseName()
    {
        // "member;range=0-1499" must be recognised as the Dn-syntax "member", using the same
        // range parser the projector uses -- otherwise range-limited attributes silently fall
        // back to AdAttributeSyntax.String just because of the suffix.
        Assert.Equal(AdAttributeSyntax.Dn, AdAttributeSchema.SyntaxOf("member;range=0-1499"));
        Assert.Equal(AdAttributeSyntax.Dn, AdAttributeSchema.SyntaxOf("member;range=1500-*"));
    }

    [Fact]
    public void IsKnownAttribute_TrueForSchemaEntries_FalseOtherwise()
    {
        Assert.True(AdAttributeSchema.IsKnownAttribute("objectGUID"));
        Assert.True(AdAttributeSchema.IsKnownAttribute("member;range=0-1499"));
        Assert.False(AdAttributeSchema.IsKnownAttribute("thisIsNotARealAttribute"));
    }

    [Theory]
    // 0.2.6: the OU and topology attributes resolve with canonical casing, whichever
    // casing the caller typed.
    [InlineData("GPLINK", "gPLink")]
    [InlineData("gplink", "gPLink")]
    [InlineData("Street", "street")]
    [InlineData("OU", "ou")]
    [InlineData("netbiosname", "nETBIOSName")]
    [InlineData("WELLKNOWNOBJECTS", "wellKnownObjects")]
    [InlineData("lockoutobservationwindow", "lockOutObservationWindow")]
    // RSAT spellings that differ by more than case go through the alias table.
    [InlineData("MaxPasswordAge", "maxPwdAge")]
    [InlineData("MinPasswordAge", "minPwdAge")]
    [InlineData("MinPasswordLength", "minPwdLength")]
    [InlineData("PasswordHistoryCount", "pwdHistoryLength")]
    public void TryResolveAttributeName_TopologyAttributes_CanonicalCasing(string requested, string expected)
    {
        Assert.True(AdAttributeSchema.TryResolveAttributeName(requested, out var resolved));
        Assert.Equal(expected, resolved);
    }
}
