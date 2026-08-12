using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// The pure topology parsers behind the 0.2.6 cmdlets. Everything here is offline by
/// construction; the values the parsers cannot verify without a directory (well-known GUID
/// -> container mappings, functional-level strings) are pinned here as documented facts and
/// flagged for live confirmation in the lab suite (L-WKG, L-MODE).
/// </summary>
public class AdTopologyTests
{
    // ---- ParseGpLink ----

    [Fact]
    public void ParseGpLink_SingleLink()
    {
        var result = AdTopology.ParseGpLink(
            "[LDAP://cn={31B2F340-016D-11D2-945F-00C04FB984F9},cn=policies,cn=system,DC=corp,DC=com;0]");

        var dn = Assert.Single(result);
        Assert.Equal("cn={31B2F340-016D-11D2-945F-00C04FB984F9},cn=policies,cn=system,DC=corp,DC=com", dn);
    }

    [Fact]
    public void ParseGpLink_MultipleLinks_PreserveStoredOrder()
    {
        var result = AdTopology.ParseGpLink(
            "[LDAP://cn={AAA},cn=policies,cn=system,DC=x;0][LDAP://cn={BBB},cn=policies,cn=system,DC=x;2]");

        Assert.Equal(2, result.Count);
        Assert.StartsWith("cn={AAA}", result[0]);
        Assert.StartsWith("cn={BBB}", result[1]);
    }

    [Theory]
    // Flag word variants: 0 = normal, 1 = disabled, 2 = enforced, 3 = both. All stay in the
    // list -- RSAT's LinkedGroupPolicyObjects includes disabled links too.
    [InlineData(";0")]
    [InlineData(";1")]
    [InlineData(";2")]
    [InlineData(";3")]
    public void ParseGpLink_FlagVariants_AreStrippedNotFiltered(string flagSuffix)
    {
        var result = AdTopology.ParseGpLink($"[LDAP://cn={{X}},cn=policies,cn=system,DC=x{flagSuffix}]");
        var dn = Assert.Single(result);
        Assert.Equal("cn={X},cn=policies,cn=system,DC=x", dn);
    }

    [Fact]
    public void ParseGpLink_PrefixIsCaseInsensitive()
    {
        var result = AdTopology.ParseGpLink("[ldap://cn={X},cn=policies,cn=system,DC=x;0]");
        Assert.Single(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A cleared link list is stored as a single space by some tools.
    [InlineData(" [")]
    public void ParseGpLink_EmptyOrAbsent_IsEmptyList(string? gpLink)
    {
        Assert.Empty(AdTopology.ParseGpLink(gpLink));
    }

    [Theory]
    // No LDAP:// prefix, empty DN, unterminated bracket: skipped, never guessed.
    [InlineData("[cn={X},DC=x;0]")]
    [InlineData("[LDAP://;0]")]
    [InlineData("[LDAP://cn={X},DC=x;0")]
    public void ParseGpLink_MalformedSegments_AreSkipped(string gpLink)
    {
        Assert.Empty(AdTopology.ParseGpLink(gpLink));
    }

    [Fact]
    public void ParseGpLink_MalformedSegment_DoesNotPoisonTheRest()
    {
        var result = AdTopology.ParseGpLink("[garbage][LDAP://cn={X},cn=policies,cn=system,DC=x;0]");
        var dn = Assert.Single(result);
        Assert.StartsWith("cn={X}", dn);
    }

    // ---- ParseWellKnownObjects ----

    [Fact]
    public void ParseWellKnownObjects_ValidValues_MapGuidToDn()
    {
        var map = AdTopology.ParseWellKnownObjects(new[]
        {
            "B:32:AA312825768811D1ADED00C04FD8D5CD:CN=Computers,DC=corp,DC=com",
            "B:32:A9D1CA15768811D1ADED00C04FD8D5CD:CN=Users,DC=corp,DC=com",
        });

        Assert.Equal(2, map.Count);
        Assert.Equal("CN=Computers,DC=corp,DC=com", map["AA312825768811D1ADED00C04FD8D5CD"]);
        Assert.Equal("CN=Users,DC=corp,DC=com", map["A9D1CA15768811D1ADED00C04FD8D5CD"]);
    }

    [Fact]
    public void ParseWellKnownObjects_GuidLookupIsCaseInsensitive()
    {
        var map = AdTopology.ParseWellKnownObjects(new[]
        {
            "B:32:aa312825768811d1aded00c04fd8d5cd:CN=Computers,DC=x",
        });

        Assert.Equal("CN=Computers,DC=x", map["AA312825768811D1ADED00C04FD8D5CD"]);
    }

    [Fact]
    public void ParseWellKnownObjects_UnknownGuid_IsPreserved()
    {
        var map = AdTopology.ParseWellKnownObjects(new[]
        {
            "B:32:00000000000000000000000000000001:CN=Custom,DC=x",
        });

        Assert.Single(map);
        Assert.Equal("CN=Custom,DC=x", map["00000000000000000000000000000001"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("B:32:AA312825768811D1ADED00C04FD8D5CD")]       // no DN part
    [InlineData("B:16:AA312825768811D1:CN=Short,DC=x")]          // wrong length marker
    [InlineData("X:32:AA312825768811D1ADED00C04FD8D5CD:CN=Y,DC=x")] // wrong prefix
    [InlineData("B:32:NOTHEXNOTHEXNOTHEXNOTHEXNOTHEXNO:CN=Y,DC=x")] // non-hex guid
    [InlineData("B:32:AA312825768811D1ADED00C04FD8D5CD:")]       // empty DN
    public void ParseWellKnownObjects_MalformedValues_AreSkipped(string? value)
    {
        Assert.Empty(AdTopology.ParseWellKnownObjects(new[] { value }));
    }

    [Fact]
    public void ParseWellKnownObjects_DnMayContainColons()
    {
        var map = AdTopology.ParseWellKnownObjects(new[]
        {
            "B:32:AA312825768811D1ADED00C04FD8D5CD:CN=host:port,DC=x",
        });

        Assert.Equal("CN=host:port,DC=x", map["AA312825768811D1ADED00C04FD8D5CD"]);
    }

    [Fact]
    public void WellKnownContainerGuids_CoverTheEightGetADDomainContainers()
    {
        var expected = new[]
        {
            "UsersContainer", "ComputersContainer", "DomainControllersContainer",
            "SystemsContainer", "LostAndFoundContainer", "DeletedObjectsContainer",
            "ForeignSecurityPrincipalsContainer", "QuotasContainer",
        };

        Assert.Equal(expected.Length, AdTopology.WellKnownContainerGuids.Count);
        foreach (var name in expected)
        {
            Assert.True(AdTopology.WellKnownContainerGuids.ContainsKey(name), $"missing {name}");
            var guid = AdTopology.WellKnownContainerGuids[name];
            Assert.Equal(32, guid.Length);
            Assert.All(guid, c => Assert.True(Uri.IsHexDigit(c)));
        }
    }

    // ---- DecodePwdProperties ----

    [Theory]
    [InlineData(0x00, false, false)]
    [InlineData(0x01, true, false)]
    [InlineData(0x10, false, true)]
    [InlineData(0x11, true, true)]
    // Other DOMAIN_* bits present must not bleed into either flag.
    [InlineData(0x08, false, false)]
    public void DecodePwdProperties_Bits(int raw, bool complexity, bool reversible)
    {
        var (complexityEnabled, reversibleEnabled) = AdTopology.DecodePwdProperties(raw);
        Assert.Equal(complexity, complexityEnabled);
        Assert.Equal(reversible, reversibleEnabled);
    }

    // ---- Functional levels (values pinned as documented; live confirmation is L-MODE) ----

    [Theory]
    [InlineData(0, "Windows2000Domain")]
    [InlineData(2, "Windows2003Domain")]
    [InlineData(3, "Windows2008Domain")]
    [InlineData(4, "Windows2008R2Domain")]
    [InlineData(5, "Windows2012Domain")]
    [InlineData(6, "Windows2012R2Domain")]
    [InlineData(7, "Windows2016Domain")]
    [InlineData(10, "Windows2025Domain")]
    public void DecodeDomainMode_KnownLevels(int version, string expected)
    {
        Assert.Equal(expected, AdTopology.DecodeDomainMode(version));
    }

    [Theory]
    [InlineData(7, "Windows2016Forest")]
    [InlineData(10, "Windows2025Forest")]
    public void DecodeForestMode_KnownLevels(int version, string expected)
    {
        Assert.Equal(expected, AdTopology.DecodeForestMode(version));
    }

    [Theory]
    // 8 and 9 were never assigned; a future level must be visibly unknown, not mapped to
    // the nearest plausible name.
    [InlineData(8)]
    [InlineData(11)]
    public void DecodeModes_UnknownLevels_AreReportedAsUnknown(int version)
    {
        Assert.Contains($"({version})", AdTopology.DecodeDomainMode(version));
        Assert.StartsWith("UnknownDomainMode", AdTopology.DecodeDomainMode(version));
        Assert.StartsWith("UnknownForestMode", AdTopology.DecodeForestMode(version));
    }

    // ---- crossRef / nTDSDSA / RODC bit tests ----

    [Theory]
    [InlineData(0x00000003, true)]  // NTDS_NC | NTDS_DOMAIN -> a domain
    [InlineData(0x00000002, true)]
    [InlineData(0x00000001, false)] // NTDS_NC only -> config/schema/app partition, not a domain
    [InlineData(0x00000007, true)]  // domain bit (0x2) set among others
    [InlineData(0x00000005, false)] // 0x1 | 0x4, no domain bit
    [InlineData(0x00000000, false)]
    public void IsDomainCrossRef_TestsBit0x2(int systemFlags, bool expected)
    {
        Assert.Equal(expected, AdTopology.IsDomainCrossRef(systemFlags));
    }

    [Theory]
    [InlineData(0x1, true)]   // IS_GC
    [InlineData(0x21, true)]  // IS_GC among other option bits
    [InlineData(0x0, false)]
    [InlineData(0x20, false)] // other bits, not GC
    public void NtdsIsGlobalCatalog_TestsBit0x1(int options, bool expected)
    {
        Assert.Equal(expected, AdTopology.NtdsIsGlobalCatalog(options));
    }

    [Theory]
    [InlineData(0x04000000, true)]  // PARTIAL_SECRETS_ACCOUNT -> RODC
    [InlineData(0x04001000, true)]  // among SERVER_TRUST_ACCOUNT etc.
    [InlineData(0x00001000, false)] // plain writable DC (SERVER_TRUST_ACCOUNT)
    [InlineData(0x00000000, false)]
    public void IsReadOnlyDcUac_TestsBit0x04000000(int uac, bool expected)
    {
        Assert.Equal(expected, AdTopology.IsReadOnlyDcUac(uac));
    }

    // ---- Config-partition DN geometry ----

    [Fact]
    public void NtdsSettingsToServerDn_IsTheParent()
    {
        Assert.Equal(
            "CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=com",
            AdTopology.NtdsSettingsToServerDn(
                "CN=NTDS Settings,CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=com"));
    }

    [Fact]
    public void SiteFromServerDn_IsTheRdnTwoLevelsUp()
    {
        Assert.Equal(
            "Default-First-Site-Name",
            AdTopology.SiteFromServerDn(
                "CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=corp,DC=com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CN=OnlyOneLevel")]
    public void SiteFromServerDn_TooShallow_IsNull(string? dn)
    {
        Assert.Null(AdTopology.SiteFromServerDn(dn));
    }

    [Theory]
    [InlineData("DC=child,DC=corp,DC=com", "child.corp.com")]
    [InlineData("DC=corp,DC=com", "corp.com")]
    // Leading non-DC RDNs (a crossRef's nCName is a plain NC DN, but be robust) are skipped.
    [InlineData("CN=X,DC=corp,DC=com", "corp.com")]
    public void DnsNameFromNamingContext_JoinsTheDcRun(string dn, string expected)
    {
        Assert.Equal(expected, AdTopology.DnsNameFromNamingContext(dn));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CN=NoDomainComponents")]
    public void DnsNameFromNamingContext_NoDcRun_IsNull(string? dn)
    {
        Assert.Null(AdTopology.DnsNameFromNamingContext(dn));
    }
}
