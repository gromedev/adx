using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace ADx.Engine.Ldap;

/// <summary>LDAP search scope (RFC 4511 §4.5.1.2).</summary>
public enum LdapScope
{
    Base,
    OneLevel,
    Subtree
}

/// <summary>
/// A directory entry, decoupled from <c>System.DirectoryServices.Protocols</c>.
/// <para>
/// This type exists because <c>SearchResultEntry</c>, <c>SearchResponse</c> and
/// <c>PageResultResponseControl</c> all have internal-only constructors with no accessible
/// base constructor, so they can be neither built nor subclassed in a test. Projecting to
/// this record at the client boundary is what makes the paging loop, range completion and
/// membership resolution testable without a directory server.
/// </para>
/// <para>Attribute values are <see cref="string"/> or <see cref="byte"/>[].</para>
/// </summary>
public sealed class LdapEntry
{
    public string DistinguishedName { get; }

    /// <summary>Attribute name to values. Keys are case-insensitive, as LDAP requires.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<object>> Attributes { get; }

    public LdapEntry(string distinguishedName, IReadOnlyDictionary<string, IReadOnlyList<object>> attributes)
    {
        DistinguishedName = distinguishedName ?? string.Empty;
        Attributes = DropEmptyRangedSiblings(NormalizeComparer(
            attributes ?? new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The case-insensitive-keys promise is enforced here, not merely documented: a caller
    /// handing in a default-comparer dictionary previously kept case-SENSITIVE lookups
    /// whenever <see cref="DropEmptyRangedSiblings"/> returned the input unchanged (the
    /// common case), so <c>GetString("samaccountname")</c> silently missed
    /// <c>sAMAccountName</c> depending on which constructor path built the entry.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<object>> NormalizeComparer(
        IReadOnlyDictionary<string, IReadOnlyList<object>> attributes)
    {
        if (attributes is Dictionary<string, IReadOnlyList<object>> dictionary &&
            ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase))
            return attributes;

        var copy = new Dictionary<string, IReadOnlyList<object>>(attributes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in attributes) copy[key] = values;
        return copy;
    }

    /// <summary>
    /// When an attribute exceeds MaxValRange, AD returns BOTH the ranged key
    /// (<c>member;range=0-1499</c>, holding the values) AND a plain <c>member</c> with ZERO
    /// values. That empty sibling is a server artifact, not data -- but it is
    /// indistinguishable from "genuinely empty" to every consumer downstream, and which key
    /// a consumer encounters first depends on hashtable enumeration order, which .NET
    /// randomises PER PROCESS. Left in the model, it made a 1,700-member group read as empty
    /// in roughly half of all processes, deterministically within each. Dropped here, at the
    /// model boundary, so no consumer can ever see the artifact.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<object>> DropEmptyRangedSiblings(
        IReadOnlyDictionary<string, IReadOnlyList<object>> attributes)
    {
        List<string>? doomed = null;
        foreach (var key in attributes.Keys)
        {
            if (!TryParseRangeOption(key, out var baseName, out _, out _, out _))
                continue;
            if (attributes.TryGetValue(baseName!, out var plain) && plain.Count == 0)
                (doomed ??= new List<string>()).Add(baseName!);
        }

        if (doomed is null) return attributes;

        var filtered = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in attributes)
        {
            if (!doomed.Contains(key, StringComparer.OrdinalIgnoreCase))
                filtered[key] = values;
        }
        return filtered;
    }

    /// <summary>Build an entry from loose values, normalising the dictionary comparer.</summary>
    public static LdapEntry Create(
        string distinguishedName,
        IEnumerable<KeyValuePair<string, IReadOnlyList<object>>> attributes)
    {
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in attributes) dict[kvp.Key] = kvp.Value;
        return new LdapEntry(distinguishedName, dict);
    }

    private IReadOnlyList<object>? Raw(string name) =>
        Attributes.TryGetValue(name, out var values) && values.Count > 0 ? values : null;

    /// <summary>First value as a string. Byte arrays are decoded as UTF-8.</summary>
    public string? GetString(string name)
    {
        var values = Raw(name);
        if (values is null) return null;
        return values[0] switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b).TrimEnd('\0'),
            var o => o?.ToString()
        };
    }

    /// <summary>First value as raw bytes. Strings are encoded back to UTF-8.</summary>
    public byte[]? GetBytes(string name)
    {
        var values = Raw(name);
        if (values is null) return null;
        return values[0] switch
        {
            byte[] b => b,
            string s => Encoding.UTF8.GetBytes(s),
            _ => null
        };
    }

    public int? GetInt32(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public long? GetInt64(string name) =>
        long.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>All values as strings. Empty when the attribute is absent.</summary>
    public IReadOnlyList<string> GetStrings(string name)
    {
        var values = Raw(name);
        if (values is null) return Array.Empty<string>();

        var result = new List<string>(values.Count);
        foreach (var v in values)
        {
            switch (v)
            {
                case string s: result.Add(s); break;
                case byte[] b: result.Add(Encoding.UTF8.GetString(b).TrimEnd('\0')); break;
                default: if (v is not null) result.Add(v.ToString()!); break;
            }
        }
        return result;
    }

    /// <summary>
    /// Read a multi-valued attribute that may have come back range-limited.
    /// <para>
    /// Active Directory caps a single read at <c>MaxValRange</c> (default 1500) values. Past
    /// that it does not truncate the attribute -- it <em>renames</em> it, returning
    /// <c>member;range=0-1499</c> instead of <c>member</c>. A caller that only looks up
    /// "member" therefore sees nothing at all and concludes the group is empty. That is the
    /// single most consequential bug in naive AD collectors: every group over 1500 members
    /// silently reports zero.
    /// </para>
    /// <para>
    /// <paramref name="isFinal"/> is true when the range ends in '*', meaning no further
    /// reads are needed.
    /// </para>
    /// </summary>
    public bool TryGetRanged(
        string name,
        out IReadOnlyList<string> values,
        out int low,
        out int high,
        out bool isFinal)
    {
        // Unranged: the attribute fit under MaxValRange and is complete as-is.
        if (Attributes.ContainsKey(name))
        {
            values = GetStrings(name);
            low = 0;
            high = values.Count - 1;
            isFinal = true;
            return true;
        }

        foreach (var key in Attributes.Keys)
        {
            if (!TryParseRangeOption(key, out var baseName, out low, out high, out isFinal))
                continue;
            if (!string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase))
                continue;

            values = GetStrings(key);
            return true;
        }

        values = Array.Empty<string>();
        low = 0;
        high = -1;
        isFinal = true;
        return false;
    }

    /// <summary>
    /// Parse an attribute description carrying a range option, e.g. "member;range=0-1499"
    /// or "member;range=1500-*". Returns false for a plain name or any other option
    /// (such as "member;binary").
    /// </summary>
    public static bool TryParseRangeOption(
        string attributeDescription,
        [NotNullWhen(true)] out string? baseName,
        out int low,
        out int high,
        out bool isFinal)
    {
        baseName = null;
        low = 0;
        high = -1;
        isFinal = false;

        if (string.IsNullOrEmpty(attributeDescription)) return false;

        var semi = attributeDescription.IndexOf(';');
        if (semi <= 0) return false;

        const string marker = "range=";
        var optionsPart = attributeDescription.Substring(semi + 1);
        if (!optionsPart.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return false;

        var range = optionsPart.Substring(marker.Length);
        var dash = range.IndexOf('-');
        if (dash <= 0) return false;

        if (!int.TryParse(range.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out low))
            return false;

        var highPart = range.Substring(dash + 1);
        if (highPart == "*")
        {
            high = -1;
            isFinal = true;
        }
        else if (int.TryParse(highPart, NumberStyles.None, CultureInfo.InvariantCulture, out high))
        {
            isFinal = false;
        }
        else
        {
            return false;
        }

        baseName = attributeDescription.Substring(0, semi);
        return true;
    }

    public override string ToString() => DistinguishedName;
}

/// <summary>A search request, independent of the transport.</summary>
public sealed record LdapSearchSpec(
    string SearchBase,
    string Filter,
    IReadOnlyList<string> Attributes,
    LdapScope Scope = LdapScope.Subtree,
    int PageSize = 1000,
    int SizeLimit = 0)
{
    /// <summary>
    /// Stable identity for this search, used as the checkpoint <c>Resource</c> so a resume
    /// against a different query is detected rather than silently mixing results.
    /// </summary>
    public string ToResourceKey(string? server) =>
        $"ldap://{server ?? "default"}/{SearchBase}?{Filter}?{Scope}";
}

/// <summary>
/// One page of results plus the server's continuation cookie.
/// <para>
/// The cookie is meaningful only within the LDAP session that produced it -- it is not
/// portable across processes or reconnects. Persisting it for resume does not work.
/// </para>
/// </summary>
public sealed record LdapPage(IReadOnlyList<LdapEntry> Entries, byte[]? Cookie)
{
    public bool HasMore => Cookie is { Length: > 0 };
}

/// <summary>
/// RootDSE attributes. Readable before bind on AD, which makes this a reachability probe,
/// a capability probe, and the portable replacement for the Windows-only
/// <c>ActiveDirectory.Domain.GetCurrentDomain()</c> all at once.
/// </summary>
public sealed record LdapRootDse(
    string? DefaultNamingContext,
    string? ConfigurationNamingContext,
    string? SchemaNamingContext,
    string? RootDomainNamingContext,
    string? DnsHostName,
    string? ServerName,
    long? HighestCommittedUsn,
    int? DomainControllerFunctionality,
    IReadOnlySet<string> SupportedControls)
{
    public const string OidPagedResults = "1.2.840.113556.1.4.319";
    public const string OidDirSync = "1.2.840.113556.1.4.841";
    public const string OidDomainScope = "1.2.840.113556.1.4.1339";
    public const string OidShowDeleted = "1.2.840.113556.1.4.417";

    public bool SupportsPagedResults => SupportedControls.Contains(OidPagedResults);
    public bool SupportsDirSync => SupportedControls.Contains(OidDirSync);
    public bool SupportsDomainScope => SupportedControls.Contains(OidDomainScope);

    /// <summary>
    /// A defaultNamingContext is the marker that distinguishes AD from a generic LDAP
    /// server. Without it, AD-only features (range retrieval, primaryGroupID, DirSync)
    /// are unavailable and the caller should be told plainly.
    /// </summary>
    public bool IsActiveDirectory => !string.IsNullOrEmpty(DefaultNamingContext);
}
