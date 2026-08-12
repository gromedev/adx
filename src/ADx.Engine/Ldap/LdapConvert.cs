using System.Globalization;
using System.Linq;
using System.Text;

namespace ADx.Engine.Ldap;

/// <summary>
/// Attribute value converters for Active Directory / LDAP.
/// <para>
/// Every method here is deliberately free of Windows-only APIs so the collector runs on
/// Linux and macOS. In particular <c>objectSid</c> is decoded by hand rather than through
/// <c>System.Security.Principal.SecurityIdentifier</c>, which throws
/// <c>PlatformNotSupportedException</c> off Windows.
/// </para>
/// </summary>
public static class LdapConvert
{
    /// <summary>
    /// Parse an LDAP GeneralizedTime (RFC 4517), e.g. "20250102030405.0Z".
    /// <para>
    /// The trailing timezone designator is significant: AD emits UTC, so a parser that
    /// drops the 'Z' reinterprets every timestamp as local and skews it by the machine's
    /// offset. Returns null rather than throwing on anything unrecognised.
    /// </para>
    /// </summary>
    public static DateTimeOffset? GeneralizedTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var s = value.Trim();

        // yyyyMMddHHmmss is the shortest form AD ever emits.
        if (s.Length < 14) return null;

        if (!TryParseInt(s, 0, 4, out var year) ||
            !TryParseInt(s, 4, 2, out var month) ||
            !TryParseInt(s, 6, 2, out var day) ||
            !TryParseInt(s, 8, 2, out var hour) ||
            !TryParseInt(s, 10, 2, out var minute) ||
            !TryParseInt(s, 12, 2, out var second))
            return null;

        var rest = s.Substring(14);

        // Optional fractional seconds: ".0" or ",0"
        double fraction = 0;
        if (rest.Length > 0 && (rest[0] == '.' || rest[0] == ','))
        {
            var i = 1;
            while (i < rest.Length && char.IsAsciiDigit(rest[i])) i++;
            var digits = rest.Substring(1, i - 1);
            if (digits.Length > 0)
                fraction = double.Parse("0." + digits, CultureInfo.InvariantCulture);
            rest = rest.Substring(i);
        }

        // Timezone: 'Z', or +/-HHmm, or +/-HH. Absent means local time per RFC,
        // but AD always emits Z; treat absent as UTC to avoid silent host-dependent skew.
        var offset = TimeSpan.Zero;
        if (rest.Length > 0)
        {
            if (rest[0] is 'Z' or 'z')
            {
                offset = TimeSpan.Zero;
            }
            else if (rest[0] is '+' or '-')
            {
                var sign = rest[0] == '-' ? -1 : 1;
                if (!TryParseInt(rest, 1, 2, out var offHours)) return null;
                var offMinutes = 0;
                if (rest.Length >= 5 && !TryParseInt(rest, 3, 2, out offMinutes)) return null;
                offset = new TimeSpan(sign * offHours, sign * offMinutes, 0);
            }
            else
            {
                return null;
            }
        }

        try
        {
            var dto = new DateTimeOffset(year, month, day, hour, minute, second, offset);
            if (fraction > 0)
                dto = dto.AddTicks((long)(fraction * TimeSpan.TicksPerSecond));
            return dto.ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryParseInt(string s, int start, int length, out int value)
    {
        value = 0;
        if (start + length > s.Length) return false;
        return int.TryParse(
            s.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Convert a Windows FILETIME (100ns intervals since 1601-01-01 UTC) to a UTC timestamp.
    /// <para>
    /// 0 and <see cref="long.MaxValue"/> are AD's "never" sentinels and map to null. Uses
    /// <c>FromFileTimeUtc</c>; <c>FromFileTime</c> would apply the host's local offset and
    /// make results machine-dependent.
    /// </para>
    /// </summary>
    public static DateTimeOffset? FileTime(long value)
    {
        if (value <= 0 || value == long.MaxValue) return null;
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(value), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="FileTime(long)"/>
    public static DateTimeOffset? FileTime(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? FileTime(l)
            : null;

    /// <summary>
    /// Convert an AD interval attribute (maxPwdAge, lockoutDuration...) to a TimeSpan.
    /// <para>
    /// Intervals are stored as NEGATIVE 100ns tick counts; RSAT surfaces them positive.
    /// They cannot share <see cref="FileTime(long)"/>: that converter treats any value
    /// &lt;= 0 as a "never" sentinel and would silently null every interval. Here 0 means
    /// "none" (TimeSpan.Zero) and <see cref="long.MinValue"/> is the "never" sentinel
    /// (TimeSpan.MaxValue) -- conveniently the one long that cannot be negated, so the
    /// sentinel branch and the overflow guard are the same check. Every other magnitude
    /// fits a TimeSpan (its tick range IS the long range), so no further guard exists.
    /// </para>
    /// </summary>
    public static TimeSpan? Interval(long value)
    {
        if (value == 0) return TimeSpan.Zero;
        if (value == long.MinValue) return TimeSpan.MaxValue;
        return TimeSpan.FromTicks(value < 0 ? -value : value);
    }

    /// <inheritdoc cref="Interval(long)"/>
    public static TimeSpan? Interval(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            ? Interval(l)
            : null;

    /// <summary>
    /// Render a binary objectSid as SDDL ("S-1-5-21-...-512").
    /// <para>
    /// Hand-rolled on purpose: <c>SecurityIdentifier</c> is Windows-only and throws
    /// <c>PlatformNotSupportedException</c> on Linux/macOS.
    /// </para>
    /// <para>
    /// Layout: byte 0 revision, byte 1 sub-authority count, bytes 2-7 identifier authority
    /// (big-endian), then that many 4-byte little-endian sub-authorities.
    /// </para>
    /// </summary>
    public static string? SidToSddl(byte[]? sid)
    {
        if (sid is null || sid.Length < 8) return null;

        int revision = sid[0];
        int subAuthorityCount = sid[1];
        if (subAuthorityCount > 15) return null;
        if (sid.Length < 8 + (subAuthorityCount * 4)) return null;

        // Identifier authority is 6 bytes, big-endian.
        ulong authority = 0;
        for (var i = 2; i < 8; i++)
            authority = (authority << 8) | sid[i];

        var sb = new StringBuilder(64);
        sb.Append("S-").Append(revision).Append('-');

        // Authorities that don't fit in 32 bits are rendered as hex, per the SDDL convention.
        if (authority > uint.MaxValue)
            sb.Append("0x").Append(authority.ToString("x12", CultureInfo.InvariantCulture));
        else
            sb.Append(authority.ToString(CultureInfo.InvariantCulture));

        for (var i = 0; i < subAuthorityCount; i++)
        {
            var offset = 8 + (i * 4);
            uint sub = (uint)(sid[offset]
                              | (sid[offset + 1] << 8)
                              | (sid[offset + 2] << 16)
                              | (sid[offset + 3] << 24));
            sb.Append('-').Append(sub.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The RID (last sub-authority) of a binary SID. This is what
    /// <c>primaryGroupID</c> on a user matches against a group's <c>primaryGroupToken</c>.
    /// </summary>
    public static uint? SidRid(byte[]? sid)
    {
        if (sid is null || sid.Length < 12) return null;
        int subAuthorityCount = sid[1];
        if (subAuthorityCount == 0) return null;

        var offset = 8 + ((subAuthorityCount - 1) * 4);
        if (sid.Length < offset + 4) return null;

        return (uint)(sid[offset]
                      | (sid[offset + 1] << 8)
                      | (sid[offset + 2] << 16)
                      | (sid[offset + 3] << 24));
    }

    /// <summary>The domain portion of a SID: the full SDDL minus the trailing RID.</summary>
    public static string? SidDomain(byte[]? sid)
    {
        var sddl = SidToSddl(sid);
        if (sddl is null) return null;
        var lastDash = sddl.LastIndexOf('-');
        return lastDash <= 0 ? sddl : sddl.Substring(0, lastDash);
    }

    /// <summary>Decode a binary objectGUID. AD stores it in .NET Guid byte order.</summary>
    public static Guid? ObjectGuid(byte[]? value) =>
        value is { Length: 16 } ? new Guid(value) : null;

    /// <summary>Decode userAccountControl into named flags.</summary>
    public static UacFlags Uac(int value) => (UacFlags)unchecked((uint)value);

    /// <summary>
    /// Decode groupType. The security bit is 0x80000000, so the value arrives as a
    /// negative Int32 for every security group; unchecked conversion restores the
    /// intended bit pattern rather than doing arithmetic on the sign.
    /// </summary>
    public static GroupTypeInfo GroupType(int value)
    {
        var raw = unchecked((uint)value);

        // Bit tests, NOT a switch on the whole nibble: builtin groups combine bits. Every
        // system-created builtin (BUILTIN\Administrators etc.) has groupType 0x80000005 --
        // BUILTIN_LOCAL_GROUP (0x1) | RESOURCE_GROUP (0x4) | SECURITY_ENABLED -- so an exact
        // nibble match sees 0x5, matches nothing, and reports Unknown for groups that exist
        // in every domain. The 0x1 test precedes 0x4 so those decode as BuiltinLocal; the
        // RSAT projector maps that to DomainLocal (RSAT's ADGroupScope has no builtin member).
        var scope = (raw & 0x2) != 0 ? GroupScopeKind.Global
            : (raw & 0x8) != 0 ? GroupScopeKind.Universal
            : (raw & 0x1) != 0 ? GroupScopeKind.BuiltinLocal
            : (raw & 0x4) != 0 ? GroupScopeKind.DomainLocal
            : GroupScopeKind.Unknown;

        return new GroupTypeInfo(scope, (raw & 0x80000000) != 0, value);
    }

    /// <summary>
    /// Escape a value for safe interpolation into an LDAP filter (RFC 4515 §3).
    /// <para>
    /// Not cosmetic: an unescaped DN containing '(' or ')' both corrupts the filter and
    /// opens an injection path. Escape every value that comes from data rather than code.
    /// </para>
    /// <para>
    /// Escapes '*' unconditionally, which is correct for an exact match (<c>-eq</c>) but wrong
    /// for a pattern (<c>-like</c>), where the caller's own wildcards must survive. Use
    /// <see cref="EscapeFilterValuePreservingWildcards"/> for that case -- never decide which
    /// escaper to call by string-typing the value at the callsite, since that is exactly the
    /// mistake that turns every <c>-like</c> search into a literal match.
    /// </para>
    /// </summary>
    public static string EscapeFilterValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                case '/': sb.Append("\\2f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escape a value for interpolation into an LDAP filter <em>pattern</em> (a <c>-like</c>
    /// argument), leaving the caller's own <c>*</c> wildcards intact.
    /// <para>
    /// <see cref="EscapeFilterValue"/> escapes '*' unconditionally, which is correct for
    /// <c>-eq</c> but turns every <c>-like 'j*'</c> search into a literal match on the string
    /// "j*" -- silently returning zero rows instead of the expected prefix match. This is the
    /// other half of the same value: everything <see cref="EscapeFilterValue"/> escapes, minus
    /// '*'.
    /// </para>
    /// </summary>
    public static string EscapeFilterValuePreservingWildcards(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                case '/': sb.Append("\\2f"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escape raw bytes as an LDAP filter value using <c>\XX</c> hex pairs for every byte
    /// (RFC 4515 §3). Required for binary-syntax attributes in a filter -- <c>objectGUID</c>
    /// and <c>objectSid</c> assertions -- where the value can contain any byte, not just the
    /// handful <see cref="EscapeFilterValue"/> special-cases.
    /// </summary>
    public static string EscapeBinary(byte[]? value)
    {
        if (value is null || value.Length == 0) return string.Empty;

        var sb = new StringBuilder(value.Length * 3);
        foreach (var b in value)
            sb.Append('\\').Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Render a UTC <see cref="DateTime"/> as an LDAP GeneralizedTime filter value
    /// (<c>yyyyMMddHHmmss.0Z</c>), the inverse of <see cref="GeneralizedTime(string?)"/>.
    /// <para>
    /// <paramref name="value"/> is converted to UTC first regardless of its
    /// <see cref="DateTime.Kind"/> -- AD always compares GeneralizedTime attributes
    /// (<c>whenCreated</c>, <c>whenChanged</c>) in UTC, so emitting local wall-clock digits
    /// with a 'Z' suffix would silently skew the comparison by the host's offset.
    /// </para>
    /// </summary>
    public static string ToGeneralizedTime(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return utc.ToString("yyyyMMddHHmmss.0Z", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Render a UTC <see cref="DateTime"/> as a Windows FILETIME filter value (100ns intervals
    /// since 1601-01-01 UTC), the inverse of <see cref="FileTime(long)"/>. Used for
    /// FileTime-syntax attributes in a filter -- <c>pwdLastSet</c>, <c>accountExpires</c>,
    /// <c>lastLogonTimestamp</c> -- which AD compares as raw 64-bit integers, not text.
    /// </summary>
    public static string ToFileTime(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return utc.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parse an SDDL string ("S-1-5-21-...-512") back into a binary objectSid, the inverse of
    /// <see cref="SidToSddl"/>. Needed to marshal a SID typed into a filter (<c>-Identity</c>
    /// resolution, <c>SID -eq 'S-1-5-...'</c>) into the binary form <c>objectSid</c> assertions
    /// require.
    /// </summary>
    public static byte[]? SddlToSid(string? sddl)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return null;

        var parts = sddl.Trim().Split('-');
        // "S", revision, authority, then >= 1 sub-authority.
        if (parts.Length < 4 || !string.Equals(parts[0], "S", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            return null;

        if (!ulong.TryParse(
                parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[2].AsSpan(2) : parts[2].AsSpan(),
                parts[2].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var authority))
            return null;

        var subAuthorityCount = parts.Length - 3;
        if (subAuthorityCount > 15) return null;

        var sid = new byte[8 + (subAuthorityCount * 4)];
        sid[0] = revision;
        sid[1] = (byte)subAuthorityCount;

        // Identifier authority is 6 bytes, big-endian.
        for (var i = 0; i < 6; i++)
            sid[7 - i] = (byte)(authority >> (i * 8));

        for (var i = 0; i < subAuthorityCount; i++)
        {
            if (!uint.TryParse(parts[3 + i], NumberStyles.None, CultureInfo.InvariantCulture, out var sub))
                return null;

            var offset = 8 + (i * 4);
            sid[offset] = (byte)sub;
            sid[offset + 1] = (byte)(sub >> 8);
            sid[offset + 2] = (byte)(sub >> 16);
            sid[offset + 3] = (byte)(sub >> 24);
        }

        return sid;
    }

    /// <summary>
    /// Split a distinguished name into its RDN components, unescaping per RFC 4514.
    /// <para>
    /// A naive <c>Split(',')</c> breaks on escaped commas, so "CN=Doe\, John,OU=Users"
    /// yields a truncated name. This handles <c>\,</c>, <c>\\</c>, other single-character
    /// escapes, and <c>\XX</c> hex escapes.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Type, string Value)> ParseDn(string? dn)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(dn)) return result;

        var type = new StringBuilder();
        var value = new StringBuilder();
        var onType = true;

        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];

            if (c == '\\' && i + 1 < dn.Length)
            {
                var next = dn[i + 1];
                // \XX hex escape
                if (i + 2 < dn.Length && IsHex(next) && IsHex(dn[i + 2]))
                {
                    var hex = (char)Convert.ToInt32(dn.Substring(i + 1, 2), 16);
                    if (onType) type.Append(hex); else value.Append(hex);
                    i += 2;
                }
                else
                {
                    if (onType) type.Append(next); else value.Append(next);
                    i += 1;
                }
                continue;
            }

            if (c == '=' && onType)
            {
                onType = false;
                continue;
            }

            if (c == ',' && !onType)
            {
                result.Add((type.ToString().Trim(), value.ToString().Trim()));
                type.Clear();
                value.Clear();
                onType = true;
                continue;
            }

            if (onType) type.Append(c); else value.Append(c);
        }

        if (type.Length > 0 || value.Length > 0)
            result.Add((type.ToString().Trim(), value.ToString().Trim()));

        return result;
    }

    private static bool IsHex(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// The value of the leading RDN, unescaped. For "CN=Doe\, John,OU=Users" this is
    /// "Doe, John" -- where a <c>CN=([^,]+),</c> regex would return "Doe\".
    /// </summary>
    public static string? FirstRdnValue(string? dn)
    {
        var parts = ParseDn(dn);
        return parts.Count > 0 ? parts[0].Value : null;
    }

    /// <summary>The parent container DN, or null for a single-component DN.</summary>
    public static string? ParentDn(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;

        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] == ',') return dn.Substring(i + 1).TrimStart();
        }
        return null;
    }

    /// <summary>
    /// The domain naming context a DN belongs to: the run of trailing <c>DC=</c> RDNs, e.g.
    /// <c>CN=A,CN=Users,DC=child,DC=corp,DC=com</c> → <c>DC=child,DC=corp,DC=com</c>. Two DNs
    /// share a partition iff their domain NCs are equal; a member DN whose NC differs from its
    /// group's is in another domain, where a same-partition memberOf search cannot reach it.
    /// Returns null when the DN carries no DC component.
    /// </summary>
    public static string? DomainNamingContext(string? dn)
    {
        var rdns = ParseDn(dn);
        var dc = rdns
            .SkipWhile(r => !string.Equals(r.Type, "DC", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dc.Count == 0) return null;

        // Rebuilt canonically as DC=a,DC=b. Domain labels carry no DN metacharacters, so this
        // is escaping-safe, and comparing two NCs built the same way is exact regardless of the
        // source casing or spacing. A stray "DC=" inside an earlier RDN value is ignored
        // because ParseDn only reports it as type "DC" when it is a real RDN type.
        return string.Join(",", dc.Select(r => "DC=" + r.Value));
    }
}

/// <summary>userAccountControl bit flags (MS-ADTS 2.2.16).</summary>
[Flags]
public enum UacFlags : uint
{
    None = 0,
    Script = 0x1,
    AccountDisabled = 0x2,
    HomedirRequired = 0x8,
    Lockout = 0x10,
    PasswdNotRequired = 0x20,
    PasswdCantChange = 0x40,
    EncryptedTextPwdAllowed = 0x80,
    TempDuplicateAccount = 0x100,
    NormalAccount = 0x200,
    InterdomainTrustAccount = 0x800,
    WorkstationTrustAccount = 0x1000,
    ServerTrustAccount = 0x2000,
    DontExpirePassword = 0x10000,
    MnsLogonAccount = 0x20000,
    SmartcardRequired = 0x40000,
    TrustedForDelegation = 0x80000,
    NotDelegated = 0x100000,
    UseDesKeyOnly = 0x200000,
    DontRequirePreauth = 0x400000,
    PasswordExpired = 0x800000,
    TrustedToAuthForDelegation = 0x1000000,
    PartialSecretsAccount = 0x4000000
}

/// <summary>Group scope, from the low nibble of groupType.</summary>
public enum GroupScopeKind
{
    Unknown = 0,
    BuiltinLocal,
    Global,
    DomainLocal,
    Universal
}

/// <summary>Decoded groupType: scope plus security-vs-distribution.</summary>
public readonly record struct GroupTypeInfo(GroupScopeKind Scope, bool IsSecurity, int Raw)
{
    public override string ToString() =>
        $"{Scope} {(IsSecurity ? "Security" : "Distribution")}";
}
