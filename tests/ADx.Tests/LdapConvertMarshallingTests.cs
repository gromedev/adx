using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M1 engine primitives: the filter-value marshalling half of LdapConvert. The decode
/// direction (GeneralizedTime/FileTime/SidToSddl parsing) already had coverage via
/// LdapPagingTests; this covers the encode direction plus the two escapers.
/// </summary>
public class LdapConvertMarshallingTests
{
    // --- The -eq vs -like escaping asymmetry: the one golden test that covers the whole class ---

    [Fact]
    public void ExactAndPatternEscaping_DivergeOnWildcards()
    {
        var exact = LdapConvert.EscapeFilterValue("j*");
        var pattern = LdapConvert.EscapeFilterValuePreservingWildcards("j*");

        Assert.Equal("j\\2a", exact);
        Assert.Equal("j*", pattern);
        Assert.NotEqual(exact, pattern);
    }

    [Theory]
    [InlineData("j*", "j*")]
    [InlineData("a\\b", "a\\5cb")]
    [InlineData("a(b)c", "a\\28b\\29c")]
    [InlineData("a/b", "a\\2fb")]
    public void EscapeFilterValuePreservingWildcards_EscapesEverythingExceptStar(string raw, string expected)
    {
        Assert.Equal(expected, LdapConvert.EscapeFilterValuePreservingWildcards(raw));
    }

    [Fact]
    public void EscapeFilterValuePreservingWildcards_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LdapConvert.EscapeFilterValuePreservingWildcards(null));
        Assert.Equal(string.Empty, LdapConvert.EscapeFilterValuePreservingWildcards(""));
    }

    // --- EscapeBinary ---

    [Fact]
    public void EscapeBinary_EncodesEveryByteAsHexPair()
    {
        var result = LdapConvert.EscapeBinary(new byte[] { 0x00, 0x2a, 0xff, 0x5c });
        Assert.Equal("\\00\\2a\\ff\\5c", result);
    }

    [Fact]
    public void EscapeBinary_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, LdapConvert.EscapeBinary(null));
        Assert.Equal(string.Empty, LdapConvert.EscapeBinary(Array.Empty<byte>()));
    }

    // --- ToGeneralizedTime ---

    [Fact]
    public void ToGeneralizedTime_UtcInput_RendersDigitsVerbatim()
    {
        var dt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal("20250102030405.0Z", LdapConvert.ToGeneralizedTime(dt));
    }

    [Fact]
    public void ToGeneralizedTime_LocalInput_ConvertsToUtcFirst()
    {
        var utc = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();

        Assert.Equal(LdapConvert.ToGeneralizedTime(utc), LdapConvert.ToGeneralizedTime(local));
    }

    [Fact]
    public void ToGeneralizedTime_RoundTripsThroughParser()
    {
        var dt = new DateTime(2024, 3, 17, 9, 30, 0, DateTimeKind.Utc);
        var text = LdapConvert.ToGeneralizedTime(dt);
        var parsed = LdapConvert.GeneralizedTime(text);

        Assert.Equal(dt, parsed!.Value.UtcDateTime);
    }

    // --- ToFileTime ---

    [Fact]
    public void ToFileTime_RoundTripsThroughParser()
    {
        var dt = new DateTime(2025, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var text = LdapConvert.ToFileTime(dt);
        var parsed = LdapConvert.FileTime(text);

        Assert.Equal(dt, parsed!.Value.UtcDateTime);
    }

    [Fact]
    public void ToFileTime_IsAPlainDecimalInteger()
    {
        var dt = new DateTime(1601, 1, 1, 0, 0, 1, DateTimeKind.Utc);
        var text = LdapConvert.ToFileTime(dt);

        Assert.True(long.TryParse(text, out var value));
        Assert.True(value > 0);
    }

    // --- SddlToSid: inverse of SidToSddl ---

    [Theory]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-1-0")]
    public void SddlToSid_RoundTripsThroughSidToSddl(string sddl)
    {
        var sid = LdapConvert.SddlToSid(sddl);
        Assert.NotNull(sid);

        var roundTripped = LdapConvert.SidToSddl(sid);
        Assert.Equal(sddl, roundTripped);
    }

    [Fact]
    public void SddlToSid_NullOrMalformed_ReturnsNull()
    {
        Assert.Null(LdapConvert.SddlToSid(null));
        Assert.Null(LdapConvert.SddlToSid(""));
        Assert.Null(LdapConvert.SddlToSid("not-a-sid"));
        Assert.Null(LdapConvert.SddlToSid("S-1"));
    }

    [Fact]
    public void SddlToSid_LargeAuthority_RoundTrips()
    {
        // SidToSddl renders authorities > uint.MaxValue in hex ("0x...").
        const string sddl = "S-1-0x123456789abc-1-2";
        var sid = LdapConvert.SddlToSid(sddl);
        Assert.NotNull(sid);
        Assert.Equal(sddl, LdapConvert.SidToSddl(sid));
    }

    // ---- groupType decoding ----

    [Theory]
    // The ordinary domain groups: exactly one scope bit plus the security bit.
    [InlineData(-2147483646, GroupScopeKind.Global, true)]        // 0x80000002
    [InlineData(-2147483644, GroupScopeKind.DomainLocal, true)]   // 0x80000004
    [InlineData(-2147483640, GroupScopeKind.Universal, true)]     // 0x80000008
    [InlineData(2, GroupScopeKind.Global, false)]                 // distribution
    [InlineData(4, GroupScopeKind.DomainLocal, false)]
    [InlineData(8, GroupScopeKind.Universal, false)]
    // BUILTIN\Administrators and every other system builtin: BUILTIN_LOCAL_GROUP (0x1)
    // AND RESOURCE_GROUP (0x4) are both set, so the low nibble is 5. A switch on the whole
    // nibble matched none of the single-bit cases and reported Unknown for groups that exist
    // in every domain on earth.
    [InlineData(-2147483643, GroupScopeKind.BuiltinLocal, true)]  // 0x80000005
    public void GroupType_DecodesScopeByBit_NotByWholeNibble(
        int raw, GroupScopeKind expectedScope, bool expectedSecurity)
    {
        var info = LdapConvert.GroupType(raw);

        Assert.Equal(expectedScope, info.Scope);
        Assert.Equal(expectedSecurity, info.IsSecurity);
        Assert.Equal(raw, info.Raw);
    }

    [Fact]
    public void GroupType_BuiltinMatchesTheDomainLocalFilterBit()
    {
        // Filter and projection must agree: "GroupScope -eq 'DomainLocal'" emits a bitwise-AND
        // test on 0x4, which DOES match a builtin group -- so the decoder must not classify
        // builtins as something the filter that selected them would contradict.
        const int builtin = -2147483643; // 0x80000005
        Assert.NotEqual(0u, unchecked((uint)builtin) & 0x4);
        Assert.Equal(GroupScopeKind.BuiltinLocal, LdapConvert.GroupType(builtin).Scope);
    }

    // --- DomainNamingContext: the discriminator behind the cross-partition membership warning ---

    [Theory]
    [InlineData("CN=Administrators,CN=Builtin,DC=child,DC=pentest,DC=lab", "DC=child,DC=pentest,DC=lab")]
    [InlineData("CN=Enterprise Admins,CN=Users,DC=pentest,DC=lab", "DC=pentest,DC=lab")]
    [InlineData("DC=corp,DC=com", "DC=corp,DC=com")]
    // escaped comma in the leaf RDN must not confuse the DC run
    [InlineData("CN=Doe\\, John,OU=Users,DC=corp,DC=com", "DC=corp,DC=com")]
    public void DomainNamingContext_ReturnsTheTrailingDcRun(string dn, string expected)
    {
        Assert.Equal(expected, LdapConvert.DomainNamingContext(dn));
    }

    [Fact]
    public void DomainNamingContext_IgnoresADcLookalikeInsideAnRdnValue()
    {
        // A CN whose VALUE contains "DC=" is not a DC component; only the real trailing RDNs
        // count, so a naive IndexOf("DC=") would have returned the wrong partition here.
        Assert.Equal("DC=corp,DC=com",
            LdapConvert.DomainNamingContext("CN=weird DC=thing,OU=x,DC=corp,DC=com"));
    }

    [Fact]
    public void DomainNamingContext_NullWhenNoDcComponent()
    {
        Assert.Null(LdapConvert.DomainNamingContext("CN=orphan,OU=nowhere"));
        Assert.Null(LdapConvert.DomainNamingContext(null));
    }

    [Fact]
    public void DomainNamingContext_TwoDnsSharePartitionRegardlessOfCasingAndSpacing()
    {
        var a = LdapConvert.DomainNamingContext("CN=A,DC=child,DC=pentest,DC=lab");
        var b = LdapConvert.DomainNamingContext("CN=B,OU=T, dc=CHILD, dc=Pentest, dc=Lab");
        Assert.Equal(a, b, ignoreCase: true);
    }

    // --- Interval (maxPwdAge and friends): stored negative, surfaced positive ---

    [Fact]
    public void Interval_StoredNegative_BecomesPositiveTimeSpan()
    {
        // 42 days, as AD stores it on the domain head.
        var stored = -TimeSpan.FromDays(42).Ticks;
        Assert.Equal(TimeSpan.FromDays(42), LdapConvert.Interval(stored));
    }

    [Fact]
    public void Interval_Zero_IsZeroNotNull()
    {
        // 0 means "none" (e.g. minPwdAge unset) -- unlike FileTime, where 0 is a null
        // sentinel. Conflating the two was the whole reason Interval is its own syntax.
        Assert.Equal(TimeSpan.Zero, LdapConvert.Interval(0));
    }

    [Fact]
    public void Interval_NeverSentinel_IsMaxValue()
    {
        // long.MinValue is AD's "never" AND the one long that cannot be negated: the same
        // branch handles both, which is why no overflow path exists.
        Assert.Equal(TimeSpan.MaxValue, LdapConvert.Interval(long.MinValue));
    }

    [Fact]
    public void Interval_PositiveValue_IsAcceptedAsIs()
    {
        Assert.Equal(TimeSpan.FromHours(1), LdapConvert.Interval(TimeSpan.FromHours(1).Ticks));
    }

    [Fact]
    public void Interval_StringOverload_ParsesInvariantNegative()
    {
        var stored = (-TimeSpan.FromDays(30).Ticks).ToString();
        Assert.Equal(TimeSpan.FromDays(30), LdapConvert.Interval(stored));
        Assert.Null(LdapConvert.Interval("not a number"));
        Assert.Null(LdapConvert.Interval((string?)null));
    }

    [Fact]
    public void Interval_RoundTripsTickMagnitude()
    {
        const long magnitude = 36_288_000_000_000; // 42 days in 100ns ticks
        Assert.Equal(LdapConvert.Interval(magnitude), LdapConvert.Interval(-magnitude));
        Assert.Equal(magnitude, LdapConvert.Interval(-magnitude)!.Value.Ticks);
    }
}
