using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M3: the -Identity detection ladder. Order matters -- every form is technically a legal
/// sAMAccountName, so DN, GUID and SID must be recognised first, and only the remainder
/// falls through.
/// </summary>
public class AdIdentityResolverTests
{
    private static (AdIdentityKind Kind, object Value) Classify(object identity) =>
        AdIdentityResolver.Classify(identity, AdObjectSchema.User);

    [Theory]
    [InlineData("CN=John Doe,OU=Users,DC=corp,DC=com")]
    [InlineData("CN=X,DC=x")]
    [InlineData("OU=Sales,DC=corp,DC=com")]
    // escaped comma inside the RDN must not break DN detection
    [InlineData("CN=Doe\\, John,OU=Users,DC=corp,DC=com")]
    public void DistinguishedNames_AreDetectedFirst(string identity)
    {
        Assert.Equal(AdIdentityKind.DistinguishedName, Classify(identity).Kind);
    }

    [Theory]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")] // D
    [InlineData("0123456789abcdef0123456789abcdef")]     // N
    public void Guids_DAndNFormats(string identity)
    {
        var (kind, value) = Classify(identity);
        Assert.Equal(AdIdentityKind.ObjectGuid, kind);
        Assert.Equal(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), value);
    }

    [Fact]
    public void BracedGuid_IsNotAGuidIdentity_PerRsat()
    {
        // RSAT accepts D/N only; "{...}" falls through to sAMAccountName.
        Assert.Equal(AdIdentityKind.SamAccountName,
            Classify("{01234567-89ab-cdef-0123-456789abcdef}").Kind);
    }

    // ---- SID identities are a security-principal form, matching RSAT ----

    [Fact]
    public void SidString_OnANonPrincipalSchema_FallsThroughToTheRejection()
    {
        // Get-ADObject/-ADOrganizationalUnit accept DN/GUID only; a SID string must not
        // classify as a SID lookup their RSAT counterpart would refuse.
        Assert.Throws<AdFilterTranslationException>(
            () => AdIdentityResolver.Classify("S-1-5-32-544", AdObjectSchema.AnyObject));
    }

    [Fact]
    public void TypedSid_OnANonPrincipalSchema_IsRejectedWithATailoredMessage()
    {
        var ex = Assert.Throws<AdFilterTranslationException>(
            () => AdIdentityResolver.Classify(
                new ADxSecurityIdentifier("S-1-5-32-544"), AdObjectSchema.AnyObject));
        Assert.Contains("does not accept a SID", ex.Message);
    }

    [Fact]
    public void TypedSid_OnAPrincipalSchema_ClassifiesAsSid()
    {
        var (kind, value) = Classify(new ADxSecurityIdentifier("S-1-5-32-544"));
        Assert.Equal(AdIdentityKind.Sid, kind);
        Assert.Equal("S-1-5-32-544", value);
    }

    [Theory]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    [InlineData("S-1-5-32-544")]
    [InlineData("s-1-5-32-544")]
    public void Sids_AreDetected(string identity)
    {
        Assert.Equal(AdIdentityKind.Sid, Classify(identity).Kind);
    }

    [Theory]
    [InlineData("jdoe")]
    [InlineData("j.doe")]
    [InlineData("S-1-5")]     // too short for the SID pattern
    [InlineData("S-orta")]    // SID lookalike
    [InlineData("12345")]
    public void EverythingElse_FallsThroughToSamAccountName(string identity)
    {
        Assert.Equal(AdIdentityKind.SamAccountName, Classify(identity).Kind);
    }

    [Fact]
    public void GuidObject_IsAGuidIdentity()
    {
        var g = Guid.NewGuid();
        var (kind, value) = Classify(g);
        Assert.Equal(AdIdentityKind.ObjectGuid, kind);
        Assert.Equal(g, value);
    }

    [Fact]
    public void ADxSecurityIdentifierObject_IsASidIdentity()
    {
        var (kind, value) = Classify(new ADxSecurityIdentifier("S-1-5-32-544"));
        Assert.Equal(AdIdentityKind.Sid, kind);
        Assert.Equal("S-1-5-32-544", value);
    }

    [Fact]
    public void PSObjectWrappedString_IsUnwrapped()
    {
        Assert.Equal(AdIdentityKind.SamAccountName, Classify(PSObject.AsPSObject("jdoe")).Kind);
    }

    [Fact]
    public void GetADxObject_AcceptsOnlyDnAndGuid()
    {
        // Matching RSAT: Get-ADObject takes DN or GUID; a bare name is an error, not a
        // sAMAccountName lookup.
        Assert.Equal(AdIdentityKind.DistinguishedName,
            AdIdentityResolver.Classify("CN=X,DC=x", AdObjectSchema.AnyObject).Kind);
        Assert.Equal(AdIdentityKind.ObjectGuid,
            AdIdentityResolver.Classify("01234567-89ab-cdef-0123-456789abcdef", AdObjectSchema.AnyObject).Kind);

        Assert.Throws<AdFilterTranslationException>(
            () => AdIdentityResolver.Classify("jdoe", AdObjectSchema.AnyObject));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIdentity_IsAnError(string identity)
    {
        Assert.Throws<AdFilterTranslationException>(() => Classify(identity));
    }

    [Fact]
    public void UnusableType_IsAnError()
    {
        Assert.Throws<AdFilterTranslationException>(() => Classify(42));
    }

    // ---- whole-object pipeline binding ----
    // -Identity is typed object and PowerShell tries ValueFromPipeline before
    // ByPropertyName, so `Get-ADxGroupNested ... | Get-ADxGroupMember` delivers the ENTIRE
    // output object here. Anything with a string DistinguishedName must classify as a DN.

    [Fact]
    public void PipedPSObjectWithDistinguishedNameNoteProperty_ClassifiesAsDn()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("DistinguishedName", "CN=G,OU=Groups,DC=corp,DC=com"));
        pso.Properties.Add(new PSNoteProperty("Name", "G"));

        var (kind, value) = Classify(pso);
        Assert.Equal(AdIdentityKind.DistinguishedName, kind);
        Assert.Equal("CN=G,OU=Groups,DC=corp,DC=com", value);
    }

    private sealed class FakeRsatObject
    {
        public string DistinguishedName { get; init; } = "";
        public string Name { get; init; } = "";
    }

    [Fact]
    public void PipedDotNetObjectWithDistinguishedNameProperty_ClassifiesAsDn()
    {
        // The RSAT shape: ADGroup/ADUser expose DistinguishedName as an adapted .NET
        // property, not a note property.
        var rsat = new FakeRsatObject { DistinguishedName = "CN=R,DC=corp,DC=com", Name = "R" };

        Assert.Equal(AdIdentityKind.DistinguishedName, Classify(rsat).Kind);
        Assert.Equal(AdIdentityKind.DistinguishedName, Classify(PSObject.AsPSObject(rsat)).Kind);
    }

    [Fact]
    public void PipedObjectWithEscapedCommaDn_ClassifiesAsDn()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("DistinguishedName", "CN=Doe\\, John,OU=Users,DC=corp,DC=com"));

        Assert.Equal(AdIdentityKind.DistinguishedName, Classify(pso).Kind);
    }

    [Fact]
    public void PipedObjectWithoutDistinguishedName_IsAnErrorNamingTheEscape()
    {
        var pso = new PSObject();
        pso.Properties.Add(new PSNoteProperty("Name", "not enough"));

        var ex = Assert.Throws<AdFilterTranslationException>(() => Classify(pso));
        Assert.Contains("DistinguishedName", ex.Message);
    }

    [Fact]
    public void PipedObjectWithNullOrEmptyDistinguishedName_IsAnError()
    {
        var withNull = new PSObject();
        withNull.Properties.Add(new PSNoteProperty("DistinguishedName", null));
        Assert.Throws<AdFilterTranslationException>(() => Classify(withNull));

        var withEmpty = new PSObject();
        withEmpty.Properties.Add(new PSNoteProperty("DistinguishedName", ""));
        Assert.Throws<AdFilterTranslationException>(() => Classify(withEmpty));
    }

    // ---- lookup filters ----

    [Fact]
    public void GuidLookup_UsesBinaryEscapedObjectGuid()
    {
        var g = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var node = AdIdentityResolver.BuildLookupFilter(AdIdentityKind.ObjectGuid, g);

        Assert.Equal(
            "(objectGUID=\\67\\45\\23\\01\\ab\\89\\ef\\cd\\01\\23\\45\\67\\89\\ab\\cd\\ef)",
            AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void SidLookup_UsesBinaryEscapedObjectSid()
    {
        var node = AdIdentityResolver.BuildLookupFilter(AdIdentityKind.Sid, "S-1-5-32-544");

        Assert.Equal(
            "(objectSid=\\01\\02\\00\\00\\00\\00\\00\\05\\20\\00\\00\\00\\20\\02\\00\\00)",
            AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void SamLookup_EscapesTheValue()
    {
        // A sAMAccountName with filter metacharacters must not corrupt the lookup filter.
        var node = AdIdentityResolver.BuildLookupFilter(AdIdentityKind.SamAccountName, "we(i)rd");
        Assert.Equal("(sAMAccountName=we\\28i\\29rd)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void DnLookup_EmitsDistinguishedNameEquality()
    {
        // 0.4: the scoped identity path. With -SearchBase, a DN identity resolves through a
        // search INSIDE the requested subtree instead of a base read that escapes it.
        var node = AdIdentityResolver.BuildLookupFilter(
            AdIdentityKind.DistinguishedName, "CN=John Doe,OU=Sales,DC=corp,DC=com");
        Assert.Equal(
            "(distinguishedName=CN=John Doe,OU=Sales,DC=corp,DC=com)",
            AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void DnLookup_EscapesFilterMetacharacters()
    {
        // A DN legitimately contains an escaped comma as backslash-comma; in the FILTER
        // encoding that backslash must itself become \5c or the assertion is corrupt.
        var node = AdIdentityResolver.BuildLookupFilter(
            AdIdentityKind.DistinguishedName, @"CN=Doe\, John,OU=Sales,DC=corp,DC=com");
        Assert.Equal(
            @"(distinguishedName=CN=Doe\5c, John,OU=Sales,DC=corp,DC=com)",
            AdFilterEmitter.Emit(node));
    }
}
