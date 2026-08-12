using System.Net;

namespace ADx.Engine.Ldap;

/// <summary>How to bind to the directory.</summary>
public enum LdapAuthMode
{
    /// <summary>SPNEGO/Kerberos with the caller's identity. Default; works domain-joined.</summary>
    Negotiate,
    Kerberos,
    /// <summary>Simple bind. Sends the password; requires <c>UseSsl</c> in practice.</summary>
    Basic,
    Anonymous
}

/// <summary>
/// Configuration for <see cref="ADxLdapClient"/>. Validated on construction.
/// <para>
/// Defaults track Active Directory's own LDAP policy limits, since exceeding them produces
/// server-side errors rather than better throughput.
/// </para>
/// </summary>
public sealed class LdapClientOptions
{
    private readonly int _port = 389;
    private readonly int _searchTimeoutSeconds = 110;
    private readonly int _connectTimeoutSeconds = 30;
    private readonly int _maxRetryAttempts = 3;
    private readonly int _retryDelaySeconds = 5;
    private readonly int _pageSize = 1000;
    private readonly int _rangeSize = 1000;

    /// <summary>
    /// Directory server. Accepts a DC hostname or, preferably, the full DNS domain name --
    /// on Windows that engages the DC Locator, and on Linux/macOS it resolves the domain
    /// apex A records, which AD publishes round-robin across its DCs.
    /// <para>
    /// Must be the complete name: taking only the first label of a domain (turning
    /// "corp.contoso.com" into "corp") only resolves when a DNS suffix search list happens
    /// to complete it.
    /// </para>
    /// </summary>
    public string? Server { get; init; }

    /// <summary>Port. 389 plain / StartTLS, 636 LDAPS, 3268/3269 Global Catalog. Range: 1-65535.</summary>
    public int Port
    {
        get => _port;
        init => _port = value is > 0 and <= 65535
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Port), value, "Must be between 1 and 65535.");
    }

    /// <summary>Use LDAPS. Pair with <see cref="Port"/> 636.</summary>
    public bool UseSsl { get; init; }

    /// <summary>
    /// Request signing and sealing on the connection. Best-effort: not all platforms
    /// support it with every auth mode, so failure degrades with a warning rather than
    /// terminating.
    /// </summary>
    public bool RequireSigning { get; init; } = true;

    public LdapAuthMode AuthMode { get; init; } = LdapAuthMode.Negotiate;

    /// <summary>Explicit credential. Null binds as the current identity.</summary>
    public NetworkCredential? Credential { get; init; }

    /// <summary>
    /// Follow referrals. Off by default: chasing referrals silently expands a search into
    /// other domains, which inflates run time and produces results the caller did not ask
    /// for. Leaving it off keeps a search scoped to the target DC.
    /// </summary>
    public bool ChaseReferrals { get; init; }

    /// <summary>
    /// Per-search timeout. Default 110s, deliberately just under AD's <c>MaxQueryDuration</c>
    /// (120s), so the client gives up fractionally before the server would.
    /// Range: 1-3600.
    /// </summary>
    public int SearchTimeoutSeconds
    {
        get => _searchTimeoutSeconds;
        init => _searchTimeoutSeconds = value is > 0 and <= 3600
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SearchTimeoutSeconds), value, "Must be between 1 and 3600.");
    }

    /// <summary>Connect/bind timeout. Range: 1-300. Default: 30.</summary>
    public int ConnectTimeoutSeconds
    {
        get => _connectTimeoutSeconds;
        init => _connectTimeoutSeconds = value is > 0 and <= 300
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ConnectTimeoutSeconds), value, "Must be between 1 and 300.");
    }

    /// <summary>Bind retry attempts. Range: 0-10. Default: 3.</summary>
    public int MaxRetryAttempts
    {
        get => _maxRetryAttempts;
        init => _maxRetryAttempts = value is >= 0 and <= 10
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), value, "Must be between 0 and 10.");
    }

    /// <summary>Delay between bind retries. Range: 0-60. Default: 5.</summary>
    public int RetryDelaySeconds
    {
        get => _retryDelaySeconds;
        init => _retryDelaySeconds = value is >= 0 and <= 60
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RetryDelaySeconds), value, "Must be between 0 and 60.");
    }

    /// <summary>
    /// Entries per page. Default 1000, matching AD's <c>MaxPageSize</c> -- asking for more
    /// does not return more. Range: 1-1000.
    /// </summary>
    public int PageSize
    {
        get => _pageSize;
        init => _pageSize = value is > 0 and <= 1000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(PageSize), value, "Must be between 1 and 1000.");
    }

    /// <summary>
    /// Values per range-retrieval read. Default 1000, under AD's <c>MaxValRange</c> of 1500.
    /// Range: 1-1500.
    /// </summary>
    public int RangeSize
    {
        get => _rangeSize;
        init => _rangeSize = value is > 0 and <= 1500
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RangeSize), value, "Must be between 1 and 1500.");
    }

    public static LdapClientOptions Default { get; } = new();
}
