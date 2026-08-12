using System.Management.Automation;
using ADx.Cmdlets.Base;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// ADxCmdletBase.LdapEntryToPSObject is protected static, so reaching it needs a derived type
/// rather than InternalsVisibleTo. Nothing is instantiated: the projector is static and pure.
/// </summary>
internal sealed class ProjectorProbe : ADxCmdletBase
{
    public static PSObject Project(LdapEntry entry, bool raw) => LdapEntryToPSObject(entry, raw);

    protected override void ProcessRecord() { }
}

/// <summary>
/// Regression coverage for range-suffixed attribute projection.
/// <para>
/// Active Directory caps a single attribute read at MaxValRange (default 1500). Past that it
/// does not truncate the attribute, it <em>renames</em> it: a group with 3000 members comes
/// back as "member;range=0-1499" rather than "member". A projector that adds attributes under
/// their raw key therefore emits a property literally called "member;range=0-1499", and
/// $group.member is null - so the group reads as empty rather than large. Silently returning
/// the wrong set is worse than any error, because nothing signals it.
/// </para>
/// </summary>
public class ProjectionTests
{
    private static LdapEntry Entry(string dn, params (string Name, object[] Values)[] attributes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in attributes) dict[name] = values;
        return new LdapEntry(dn, dict);
    }

    private static object? Value(PSObject pso, string name) => pso.Properties[name]?.Value;

    [Fact]
    public void RangedAttribute_IsEmittedUnderItsBaseName()
    {
        var entry = Entry("CN=Big,DC=corp,DC=contoso,DC=com",
            ("member;range=0-1499", new object[] { "CN=a,DC=x", "CN=b,DC=x" }));

        var pso = ProjectorProbe.Project(entry, raw: false);

        // The bug: this property used to be named "member;range=0-1499".
        Assert.NotNull(pso.Properties["member"]);
        Assert.Null(pso.Properties["member;range=0-1499"]);
    }

    [Fact]
    public void RangedAttribute_KeepsItsValues()
    {
        var entry = Entry("CN=Big,DC=corp,DC=contoso,DC=com",
            ("member;range=0-1499", new object[] { "CN=a,DC=x", "CN=b,DC=x" }));

        var members = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            Value(ProjectorProbe.Project(entry, raw: false), "member"));

        Assert.Equal(2, members.Cast<object>().Count());
    }

    [Fact]
    public void PartialRange_FlagsTruncation()
    {
        // "0-1499" without a trailing '*' means more values remain on the server.
        var entry = Entry("CN=Big,DC=corp,DC=contoso,DC=com",
            ("member;range=0-1499", new object[] { "CN=a,DC=x" }));

        var pso = ProjectorProbe.Project(entry, raw: false);

        Assert.Equal(true, Value(pso, "memberTruncated"));
        Assert.Equal(1499, Value(pso, "memberRangeHigh"));
    }

    [Fact]
    public void FinalRange_DoesNotFlagTruncation()
    {
        // A '*' upper bound means this is the last chunk - the set is complete.
        var entry = Entry("CN=Big,DC=corp,DC=contoso,DC=com",
            ("member;range=1500-*", new object[] { "CN=z,DC=x" }));

        var pso = ProjectorProbe.Project(entry, raw: false);

        Assert.NotNull(pso.Properties["member"]);
        Assert.Null(pso.Properties["memberTruncated"]);
    }

    [Fact]
    public void UnrangedAttribute_IsUnaffected()
    {
        var entry = Entry("CN=Small,DC=corp,DC=contoso,DC=com",
            ("member", new object[] { "CN=a,DC=x" }),
            ("sAMAccountName", new object[] { "small" }));

        var pso = ProjectorProbe.Project(entry, raw: false);

        Assert.NotNull(pso.Properties["member"]);
        Assert.Null(pso.Properties["memberTruncated"]);
        Assert.Equal("small", Value(pso, "sAMAccountName"));
    }

    [Fact]
    public void RawMode_AlsoUsesTheBaseName()
    {
        // -Raw changes value conversion, not attribute naming: a caller asking for raw values
        // still needs $_.member to resolve.
        var entry = Entry("CN=Big,DC=corp,DC=contoso,DC=com",
            ("member;range=0-1499", new object[] { "CN=a,DC=x" }));

        var pso = ProjectorProbe.Project(entry, raw: true);

        Assert.NotNull(pso.Properties["member"]);
        Assert.Null(pso.Properties["member;range=0-1499"]);
    }

    [Fact]
    public void NonRangeOption_IsNotTreatedAsARange()
    {
        // "member;binary" carries an option that is not a range. It must not be mistaken for
        // one, or the option would be silently dropped from the emitted property name.
        var entry = Entry("CN=Odd,DC=corp,DC=contoso,DC=com",
            ("userCertificate;binary", new object[] { "AAAA" }));

        var pso = ProjectorProbe.Project(entry, raw: false);

        Assert.NotNull(pso.Properties["userCertificate;binary"]);
        Assert.Null(pso.Properties["userCertificateTruncated"]);
    }

    [Fact]
    public void MultiValuedSidHistory_KeepsEveryValue()
    {
        // sIDHistory is Sid-syntax AND multi-valued: a twice-migrated account carries several.
        // The converted (non-raw) path used to keep only the first, silently losing migrated
        // access history -- the one attribute whose whole purpose is auditing it.
        var a = LdapConvert.SddlToSid("S-1-5-21-1-2-3-1105")!;
        var b = LdapConvert.SddlToSid("S-1-5-21-9-8-7-1106")!;
        var entry = Entry("CN=Migrated,DC=corp,DC=contoso,DC=com",
            ("sIDHistory", new object[] { a, b }));

        var value = Value(ProjectorProbe.Project(entry, raw: false), "sIDHistory");
        var sddl = Assert.IsType<string[]>(value);

        Assert.Equal(2, sddl.Length);
        Assert.Contains("S-1-5-21-1-2-3-1105", sddl);
        Assert.Contains("S-1-5-21-9-8-7-1106", sddl);
    }

    [Fact]
    public void SingleValuedSid_IsStillAScalarSddlString()
    {
        var entry = Entry("CN=One,DC=corp,DC=contoso,DC=com",
            ("objectSid", new object[] { LdapConvert.SddlToSid("S-1-5-21-1-2-3-1013")! }));

        Assert.Equal("S-1-5-21-1-2-3-1013", Value(ProjectorProbe.Project(entry, raw: false), "objectSid"));
    }

    [Fact]
    public void BinaryAttribute_StaysBytes_NotUtf8Garbled()
    {
        // userCertificate is Binary-syntax: converting it via GetStrings would UTF-8-decode
        // the DER bytes into mojibake. It must come back as byte[].
        var der = new byte[] { 0x30, 0x82, 0x01, 0x0a, 0xff, 0x00 };
        var entry = Entry("CN=Cert,DC=corp,DC=contoso,DC=com",
            ("userCertificate", new object[] { der }));

        Assert.Equal(der, Assert.IsType<byte[]>(Value(ProjectorProbe.Project(entry, raw: false), "userCertificate")));
    }

    [Fact]
    public void DistinguishedName_IsAlwaysPresent()
    {
        var pso = ProjectorProbe.Project(Entry("CN=X,DC=corp,DC=contoso,DC=com"), raw: false);

        Assert.Equal("CN=X,DC=corp,DC=contoso,DC=com", Value(pso, "DistinguishedName"));
    }

    [Fact]
    public void EntriesCarryTheFormattingTypeName()
    {
        var pso = ProjectorProbe.Project(Entry("CN=X,DC=corp,DC=contoso,DC=com"), raw: false);

        Assert.Equal(ADxCmdletBase.EntryTypeName, pso.TypeNames[0]);
        Assert.Equal("ADx.Entry", pso.TypeNames[0]);
    }
}
