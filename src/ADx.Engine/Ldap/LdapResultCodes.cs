namespace ADx.Engine.Ldap;

/// <summary>
/// LDAP result codes used by ADx, as raw integers.
/// <para>
/// These are not simply <c>(int)ResultCode</c>: .NET's
/// <c>System.DirectoryServices.Protocols.ResultCode</c> omits several codes that AD
/// actually returns, most notably 49 (invalidCredentials) -- the single most common
/// authentication failure. Comparing against the enum alone silently misclassifies it.
/// </para>
/// <para>
/// Codes 81 and 85 are client-side (the connection never reached a server), not protocol
/// results, which is why they appear here rather than in the enum at all.
/// </para>
/// </summary>
public static class LdapResultCodes
{
    public const int Success = 0;
    public const int OperationsError = 1;
    public const int TimeLimitExceeded = 3;
    public const int SizeLimitExceeded = 4;
    public const int AdminLimitExceeded = 11;
    public const int UnavailableCriticalExtension = 12;
    public const int NoSuchAttribute = 16;

    /// <summary>Extended matching rule (such as LDAP_MATCHING_RULE_IN_CHAIN) unsupported.</summary>
    public const int InappropriateMatching = 18;

    public const int InsufficientAccessRights = 50;
    public const int NoSuchObject = 32;
    public const int InappropriateAuthentication = 48;

    /// <summary>Bad username or password. Absent from the .NET ResultCode enum.</summary>
    public const int InvalidCredentials = 49;

    public const int Busy = 51;
    public const int Unavailable = 52;
    public const int UnwillingToPerform = 53;

    /// <summary>Client-side: could not reach the server.</summary>
    public const int ServerDown = 81;

    /// <summary>Client-side: the operation timed out locally.</summary>
    public const int Timeout = 85;
}
