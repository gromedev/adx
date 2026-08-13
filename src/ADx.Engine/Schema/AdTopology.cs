namespace ADx.Engine.Ldap;

/// <summary>
/// Pure parsers and decoders for the domain/forest topology surface: gPLink lists,
/// wellKnownObjects DN-Binary values, pwdProperties bits, functional-level names, and the
/// config-partition DN geometry (nTDSDSA settings vs server objects). No LDAP, no SMA --
/// the topology cmdlets are thin glue over round trips, and everything branchy enough to
/// get wrong lives here where xUnit can reach it without a domain controller.
/// </summary>
public static class AdTopology
{
    /// <summary>
    /// Parse a <c>gPLink</c> value into the linked GPO DNs, in stored order.
    /// <para>
    /// Format: <c>[LDAP://cn={GUID},cn=policies,cn=system,DC=x;0][LDAP://...;2]</c>. The
    /// digit after the last ';' is the per-link flag word (disabled/enforced) and is not
    /// part of the DN. Malformed segments are skipped rather than guessed at; an empty,
    /// whitespace, or absent value is an empty list, matching a domain or OU with no links.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ParseGpLink(string? gpLink)
    {
        if (string.IsNullOrWhiteSpace(gpLink)) return Array.Empty<string>();

        var result = new List<string>();
        var index = 0;

        while (index < gpLink.Length)
        {
            var open = gpLink.IndexOf('[', index);
            if (open < 0) break;
            var close = gpLink.IndexOf(']', open + 1);
            if (close < 0) break;

            var segment = gpLink.Substring(open + 1, close - open - 1).Trim();
            index = close + 1;

            if (!segment.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase)) continue;
            var body = segment.Substring("LDAP://".Length);

            // The flag word sits after the LAST ';'. DNs cannot contain an unescaped ';',
            // so this split is unambiguous; a segment with no ';' keeps its whole body.
            var separator = body.LastIndexOf(';');
            var dn = (separator >= 0 ? body.Substring(0, separator) : body).Trim();

            if (dn.Length > 0) result.Add(dn);
        }

        return result;
    }

    /// <summary>
    /// Parse <c>wellKnownObjects</c>/<c>otherWellKnownObjects</c> DN-Binary values
    /// (<c>B:32:&lt;32-hex-guid&gt;:&lt;dn&gt;</c>) into wkGuid (uppercase hex, no dashes)
    /// -&gt; DN. Malformed values are skipped; unknown GUIDs are preserved so callers can
    /// look up only what they recognise.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseWellKnownObjects(IEnumerable<string?> values)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            // Split on the first three ':' only -- the DN itself may legally contain ':'.
            var parts = value.Split(':', 4);
            if (parts.Length != 4) continue;
            if (!parts[0].Equals("B", StringComparison.OrdinalIgnoreCase)) continue;
            if (parts[1] != "32") continue;

            var guidHex = parts[2];
            var dn = parts[3];
            if (guidHex.Length != 32 || !guidHex.All(Uri.IsHexDigit)) continue;
            if (dn.Length == 0) continue;

            map[guidHex.ToUpperInvariant()] = dn;
        }
        return map;
    }

    /// <summary>
    /// The documented well-known-object GUIDs on the domain NC head, keyed by the RSAT
    /// property each container surfaces as (Get-ADDomain's *Container properties). Values
    /// are the 32-hex wkGuid, matching <see cref="ParseWellKnownObjects"/> keys.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WellKnownContainerGuids =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UsersContainer"] = "A9D1CA15768811D1ADED00C04FD8D5CD",
            ["ComputersContainer"] = "AA312825768811D1ADED00C04FD8D5CD",
            ["DomainControllersContainer"] = "A361B2FFFFD211D1AA4B00C04FD7D83A",
            ["SystemsContainer"] = "AB1D30F3768811D1ADED00C04FD8D5CD",
            ["LostAndFoundContainer"] = "AB8153B7768811D1ADED00C04FD8D5CD",
            ["DeletedObjectsContainer"] = "18E2EA80684F11D2B9AA00C04F79F805",
            ["ForeignSecurityPrincipalsContainer"] = "22B70C67D56E4EFB91E9300FCA3DC1AA",
            ["QuotasContainer"] = "6227F0AF1FC2410D8E3BB10615BB5B0F",
        };

    /// <summary>
    /// Decode the domain head's <c>pwdProperties</c> bit field. 0x1 is
    /// DOMAIN_PASSWORD_COMPLEX; 0x10 is DOMAIN_PASSWORD_STORE_CLEARTEXT (what RSAT surfaces
    /// as ReversibleEncryptionEnabled).
    /// </summary>
    public static (bool ComplexityEnabled, bool ReversibleEncryptionEnabled) DecodePwdProperties(int pwdProperties) =>
        ((pwdProperties & 0x1) != 0, (pwdProperties & 0x10) != 0);

    /// <summary>
    /// <c>msDS-Behavior-Version</c> on the domain crossRef/NC head -&gt; RSAT's ADDomainMode
    /// name. Levels 8 and 9 were never assigned (no new level shipped with Server 2019/2022);
    /// Server 2025 introduced 10. An unrecognised level is reported AS unrecognised rather
    /// than mapped to the nearest known name -- a wrong-but-plausible mode string is exactly
    /// the silent failure this module refuses to produce.
    /// </summary>
    public static string DecodeDomainMode(int behaviorVersion) => behaviorVersion switch
    {
        0 => "Windows2000Domain",
        1 => "Windows2003InterimDomain",
        2 => "Windows2003Domain",
        3 => "Windows2008Domain",
        4 => "Windows2008R2Domain",
        5 => "Windows2012Domain",
        6 => "Windows2012R2Domain",
        7 => "Windows2016Domain",
        10 => "Windows2025Domain",
        _ => $"UnknownDomainMode({behaviorVersion})"
    };

    /// <inheritdoc cref="DecodeDomainMode"/>
    public static string DecodeForestMode(int behaviorVersion) => behaviorVersion switch
    {
        0 => "Windows2000Forest",
        1 => "Windows2003InterimForest",
        2 => "Windows2003Forest",
        3 => "Windows2008Forest",
        4 => "Windows2008R2Forest",
        5 => "Windows2012Forest",
        6 => "Windows2012R2Forest",
        7 => "Windows2016Forest",
        10 => "Windows2025Forest",
        _ => $"UnknownForestMode({behaviorVersion})"
    };

    /// <summary>
    /// An <c>fSMORoleOwner</c> value is the role holder's nTDSDSA DN
    /// (<c>CN=NTDS Settings,CN=DC1,CN=Servers,CN=Site,...</c>); the object that knows the
    /// DC's hostname is its PARENT server object. Named so the two-object geometry is a
    /// stated fact rather than an inline <c>ParentDn</c> a reader has to decode.
    /// </summary>
    public static string? NtdsSettingsToServerDn(string? ntdsSettingsDn) =>
        LdapConvert.ParentDn(ntdsSettingsDn);

    /// <summary>
    /// The site name a config-partition server object belongs to:
    /// <c>CN=DC1,CN=Servers,CN=Default-First-Site-Name,CN=Sites,...</c> -&gt; the RDN value
    /// two levels up.
    /// </summary>
    public static string? SiteFromServerDn(string? serverDn) =>
        LdapConvert.FirstRdnValue(LdapConvert.ParentDn(LdapConvert.ParentDn(serverDn)));

    /// <summary>
    /// A crossRef's <c>systemFlags</c> marks a real domain partition with bit 0x2
    /// (FLAG_CR_NTDS_DOMAIN); application partitions and the config/schema crossRefs do not
    /// carry it. This is how Get-ADForest's Domains list excludes non-domain partitions.
    /// </summary>
    public static bool IsDomainCrossRef(int systemFlags) => (systemFlags & 0x2) != 0;

    /// <summary>An nTDSDSA's <c>options</c> bit 0x1 (NTDSDSA_OPT_IS_GC) marks a Global Catalog.</summary>
    public static bool NtdsIsGlobalCatalog(int options) => (options & 0x1) != 0;

    /// <summary>
    /// A read-only DC's NTDS Settings object is class <c>nTDSDSARO</c>, a subclass of
    /// <c>nTDSDSA</c> -- so it already appears in an (objectClass=nTDSDSA) enumeration, and
    /// its class chain is the RODC signal. Preferred over the computer account's
    /// PARTIAL_SECRETS_ACCOUNT UAC bit because the config partition is replicated
    /// forest-wide: it answers correctly for a DC in ANY domain, where the computer-object
    /// read fails behind a referral for foreign domains (and used to silently default the
    /// flag to writable).
    /// </summary>
    public static bool NtdsIsReadOnly(IReadOnlyList<string> objectClasses) =>
        objectClasses.Any(c => c.Equals("nTDSDSARO", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A naming context DN's DNS name: the trailing <c>DC=</c> run joined with dots
    /// (<c>DC=child,DC=corp,DC=com</c> -&gt; <c>child.corp.com</c>). Null when the DN has no
    /// DC components. Non-DC leading RDNs are ignored, so a crossRef's nCName works directly.
    /// </summary>
    public static string? DnsNameFromNamingContext(string? namingContextDn)
    {
        var components = LdapConvert.ParseDn(namingContextDn)
            .Where(rdn => rdn.Type.Equals("DC", StringComparison.OrdinalIgnoreCase))
            .Select(rdn => rdn.Value)
            .ToArray();

        return components.Length > 0 ? string.Join('.', components) : null;
    }
}
