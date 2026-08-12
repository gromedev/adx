using System.Management.Automation.Language;

namespace ADx.Cmdlets.Filter;

/// <summary>
/// Operator identification for the filter tokenizer bridge.
/// <para>
/// The PowerShell tokenizer is mode-sensitive: a statement starting with a bareword enters
/// command mode, where every <c>-xxx</c> becomes a <see cref="ParameterToken"/> ("Name -eq a"),
/// while the same operator inside parentheses is a real operator <see cref="TokenKind"/>
/// ("(Name -eq a) -and (Title -eq b)" tokenizes the outer <c>-and</c> as <c>TokenKind.And</c>).
/// A translator that only recognises one encoding fails on the common (unparenthesized) case
/// and passes on the parenthesized one -- a maddening bug class caught only by testing both.
/// <see cref="Identify"/> resolves an operator identity from either encoding so the parser
/// never has to care which one it is looking at.
/// </para>
/// </summary>
internal static class AdFilterOperators
{
    /// <summary>
    /// AD has no case-sensitive matching. Silently treating <c>-ceq</c>/<c>-clike</c> as their
    /// case-insensitive equivalents would return a superset of what the caller asked for --
    /// worse than an error, because nothing signals it. The ordering variants
    /// (<c>-cgt</c> etc.) are rejected too: RSAT's filter grammar does not accept any
    /// c-prefixed operator, and rejecting the whole family keeps one rule instead of a
    /// per-operator judgement call about when the downgrade happens to be harmless.
    /// </summary>
    private static readonly HashSet<string> CaseSensitiveNames =
        new(StringComparer.OrdinalIgnoreCase) { "ceq", "cne", "clike", "cnotlike", "cgt", "cge", "clt", "cle" };

    /// <summary>
    /// Operators PowerShell recognises that AD's filter grammar cannot express. Each gets its
    /// own explanation rather than falling into a generic "unrecognized token" parse error.
    /// </summary>
    private static readonly Dictionary<string, string> UnsupportedReasons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["match"] = "-match is not supported: AD filters have no regex matching. Use -like with '*' wildcards.",
            ["notmatch"] = "-notmatch is not supported: AD filters have no regex matching. Use -notlike with '*' wildcards.",
            ["in"] = "-in is not supported by AD filters. Use -or with repeated -eq comparisons.",
            ["notin"] = "-notin is not supported by AD filters. Use -and with repeated -ne comparisons.",
            ["contains"] = "-contains is not supported by AD filters. -eq already matches within a multi-valued attribute.",
            ["notcontains"] = "-notcontains is not supported by AD filters. -ne already matches within a multi-valued attribute.",
            ["replace"] = "-replace is not supported: filter values cannot be computed. Resolve the value first and pass it in a variable.",

            // The c-prefixed forms of the operators above fail twice over (AD has neither the
            // operation nor case-sensitive matching); they get the operation's explanation,
            // which is the actionable half, rather than falling into the generic
            // "not a recognised operator" message their i-form siblings avoid.
            ["cmatch"] = "-cmatch is not supported: AD filters have no regex matching (and no case-sensitive matching). Use -like with '*' wildcards.",
            ["cnotmatch"] = "-cnotmatch is not supported: AD filters have no regex matching (and no case-sensitive matching). Use -notlike with '*' wildcards.",
            ["cin"] = "-cin is not supported by AD filters. Use -or with repeated -eq comparisons.",
            ["cnotin"] = "-cnotin is not supported by AD filters. Use -and with repeated -ne comparisons.",
            ["ccontains"] = "-ccontains is not supported by AD filters. -eq already matches within a multi-valued attribute.",
            ["cnotcontains"] = "-cnotcontains is not supported by AD filters. -ne already matches within a multi-valued attribute.",
            ["creplace"] = "-creplace is not supported: filter values cannot be computed. Resolve the value first and pass it in a variable.",
        };

    /// <summary>
    /// The c-prefixed <see cref="TokenKind"/>s are deliberately absent from the map below:
    /// they are identified (and rejected) by <see cref="IsCaseSensitive"/>, which the parser
    /// checks before dispatching on the id. Mapping <c>Ceq</c> to "eq" here would create two
    /// inconsistent answers for the same operator depending on which encoding it arrived in.
    /// </summary>
    /// <summary>
    /// Explicitly case-INsensitive spellings (-ieq, -ilike, ...). The tokenizer maps these
    /// onto the same TokenKinds as their bare forms when parenthesized (<c>-ieq</c> is
    /// <c>TokenKind.Ieq</c>, exactly like <c>-eq</c>), so accepting them there but not in
    /// command mode -- where they arrive as a ParameterToken named "ieq" -- would make the
    /// same filter work or fail depending on parenthesization. That is the dual-encoding
    /// inconsistency this class exists to eliminate.
    /// <para>
    /// The unsupported operators' i-forms are here too, mapped to the ids
    /// <see cref="UnsupportedReasons"/> is keyed by: <c>-imatch</c> deserves the same "AD has
    /// no regex" explanation in both encodings, not the tailored message parenthesized and a
    /// generic "not a recognised operator" otherwise.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> CaseInsensitivePrefixed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ieq"] = "eq",
            ["ine"] = "ne",
            ["ilike"] = "like",
            ["inotlike"] = "notlike",
            ["igt"] = "gt",
            ["ige"] = "ge",
            ["ilt"] = "lt",
            ["ile"] = "le",
            ["imatch"] = "match",
            ["inotmatch"] = "notmatch",
            ["icontains"] = "contains",
            ["inotcontains"] = "notcontains",
            ["ireplace"] = "replace",
            ["iin"] = "in",
            ["inotin"] = "notin",
        };

    public static string? Identify(Token token) => token switch
    {
        ParameterToken p => CaseInsensitivePrefixed.TryGetValue(p.ParameterName, out var normalized)
            ? normalized
            : p.ParameterName.ToLowerInvariant(),
        _ => token.Kind switch
        {
            TokenKind.Ieq => "eq",
            TokenKind.Ine => "ne",
            TokenKind.Ilike => "like",
            TokenKind.Inotlike => "notlike",
            TokenKind.Igt => "gt",
            TokenKind.Ige => "ge",
            TokenKind.Ilt => "lt",
            TokenKind.Ile => "le",
            TokenKind.And => "and",
            TokenKind.Or => "or",
            TokenKind.Not or TokenKind.Exclaim => "not",
            TokenKind.Band => "band",
            TokenKind.Bor => "bor",
            TokenKind.Imatch => "match",
            TokenKind.Inotmatch => "notmatch",
            TokenKind.Icontains => "contains",
            TokenKind.Inotcontains => "notcontains",
            TokenKind.Ireplace => "replace",
            TokenKind.In or TokenKind.Iin => "in",
            TokenKind.Inotin => "notin",
            // The c-prefixed NON-comparison kinds map to their own ids (keys in
            // UnsupportedReasons), so the parenthesized encoding gets the same tailored
            // message as the command-mode ParameterToken encoding. The c-prefixed COMPARISON
            // kinds stay unmapped -- IsCaseSensitive rejects those first.
            TokenKind.Cmatch => "cmatch",
            TokenKind.Cnotmatch => "cnotmatch",
            TokenKind.Ccontains => "ccontains",
            TokenKind.Cnotcontains => "cnotcontains",
            TokenKind.Creplace => "creplace",
            TokenKind.Cin => "cin",
            TokenKind.Cnotin => "cnotin",
            _ => null
        }
    };

    public static bool IsCaseSensitive(Token token) => token switch
    {
        ParameterToken p => CaseSensitiveNames.Contains(p.ParameterName),
        _ => token.Kind is TokenKind.Ceq or TokenKind.Cne or TokenKind.Clike or TokenKind.Cnotlike
            or TokenKind.Cgt or TokenKind.Cge or TokenKind.Clt or TokenKind.Cle
    };

    public static bool TryGetUnsupportedReason(string operatorId, out string reason) =>
        UnsupportedReasons.TryGetValue(operatorId, out reason!);
}
