namespace ADx.Engine.Filter;

/// <summary>
/// A <c>-Filter</c> string could not be translated to LDAP. Always carries a message safe to
/// show the caller directly -- these surface as terminating <c>ErrorRecord</c>s, not stack
/// traces, per the plan's "explicit errors, never silent downgrades" guard: a filter that can't
/// be translated correctly must fail loudly rather than run as something subtly different from
/// what the caller asked for.
/// </summary>
public sealed class AdFilterTranslationException : Exception
{
    public AdFilterTranslationException(string message) : base(message) { }

    public AdFilterTranslationException(string message, Exception inner) : base(message, inner) { }
}
