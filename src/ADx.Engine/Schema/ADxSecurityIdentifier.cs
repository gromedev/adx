namespace ADx.Engine.Ldap;

/// <summary>
/// A small SID shim standing in for <c>System.Security.Principal.SecurityIdentifier</c>, which
/// throws <c>PlatformNotSupportedException</c> off Windows and so cannot be constructed on
/// Linux/macOS at all. RSAT emits a real <c>SecurityIdentifier</c>; a bare string in its place
/// would break the common <c>$u.SID.Value</c> pattern in existing scripts, so this carries just
/// enough surface for that to keep working.
/// </summary>
public sealed class ADxSecurityIdentifier : IEquatable<ADxSecurityIdentifier>
{
    /// <summary>The SDDL form, e.g. "S-1-5-21-...-512".</summary>
    public string Value { get; }

    /// <summary>
    /// The account-domain SID (<c>S-1-5-21-a-b-c</c>) when this is an account SID, else null
    /// -- the same contract as <c>SecurityIdentifier.AccountDomainSid</c>, which is null for
    /// builtin and well-known SIDs rather than a fabricated prefix like "S-1-5-32".
    /// </summary>
    public string? AccountDomainSid { get; }

    public ADxSecurityIdentifier(string sddl)
    {
        Value = sddl ?? throw new ArgumentNullException(nameof(sddl));
        AccountDomainSid = LdapConvert.SidAccountDomain(sddl);
    }

    /// <summary>Build from a binary <c>objectSid</c>/<c>sIDHistory</c> value. Null if malformed.</summary>
    public static ADxSecurityIdentifier? FromBinary(byte[]? sid)
    {
        var sddl = LdapConvert.SidToSddl(sid);
        return sddl is null ? null : new ADxSecurityIdentifier(sddl);
    }

    public override string ToString() => Value;

    public bool Equals(string? other) =>
        other is not null && string.Equals(Value, other, StringComparison.OrdinalIgnoreCase);

    public bool Equals(ADxSecurityIdentifier? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj switch
    {
        ADxSecurityIdentifier sid => Equals(sid),
        string s => Equals(s),
        _ => false
    };

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
}
