namespace ADx.Engine.Filter;

/// <summary>
/// An already-escaped LDAP filter value.
/// <para>
/// This exists so a bare <see cref="string"/> can never reach <see cref="AdFilterEmitter"/> and
/// be escaped the wrong way. <see cref="LdapConvert.EscapeFilterValue"/> escapes '*'
/// unconditionally -- correct for <c>-eq</c>, catastrophic for <c>-like</c>, where it turns
/// every wildcard search into a literal match. Requiring callers to go through
/// <see cref="Exact"/> or <see cref="Pattern"/> makes that choice a type rather than a
/// discipline: there is no code path where the wrong escaper can be picked by accident once a
/// filter node only accepts this struct.
/// </para>
/// </summary>
public readonly record struct LdapAssertionValue(string Escaped)
{
    /// <summary>An exact-match value ('*' is escaped as a literal character).</summary>
    public static LdapAssertionValue Exact(string raw) => new(Engine.Ldap.LdapConvert.EscapeFilterValue(raw));

    /// <summary>A <c>-like</c> pattern value (the caller's own '*' wildcards survive).</summary>
    public static LdapAssertionValue Pattern(string raw) =>
        new(Engine.Ldap.LdapConvert.EscapeFilterValuePreservingWildcards(raw));

    /// <summary>A binary-syntax value (SID, GUID), hex-escaped byte for byte.</summary>
    public static LdapAssertionValue Binary(byte[] raw) => new(Engine.Ldap.LdapConvert.EscapeBinary(raw));

    /// <summary>
    /// A value that is already filter-safe text with nothing left to escape -- an integer, a
    /// FILETIME digit string, a GeneralizedTime stamp. Using <see cref="Exact"/> on these would
    /// be harmless but is needless work; this makes "nothing to escape" a decision the caller
    /// states rather than something the emitter has to rediscover.
    /// </summary>
    public static LdapAssertionValue Verbatim(string alreadyEscaped) => new(alreadyEscaped);
}
