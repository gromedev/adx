using System.Management.Automation.Language;
using ADx.Engine.Filter;

namespace ADx.Cmdlets.Filter;

/// <summary>
/// Translates an RSAT-syntax <c>-Filter</c> string into an <see cref="AdFilterNode"/> tree.
/// <para>
/// <paramref name="resolveVariable"/> is a delegate rather than reaching for <c>PSCmdlet</c> or
/// <c>SessionState</c> directly, so this is unit-testable with no runspace and no directory
/// connection -- the same pattern <c>Mgx.Cmdlets</c> uses for its own internals, backed by the
/// existing <c>InternalsVisibleTo("ADx.Tests")</c> on this project.
/// </para>
/// </summary>
internal static class AdFilterTranslator
{
    /// <summary>
    /// Translate <paramref name="filterText"/>. Returns <c>null</c> for RSAT's bare <c>*</c>
    /// ("match everything") -- a caller combines that with the preset's base object-class
    /// filter alone, contributing nothing further.
    /// <para>
    /// <paramref name="attributeOverrides"/> is the calling preset's
    /// <c>AdObjectSchema.AttributeOverrides</c>: per-type property names (a PSO's
    /// <c>Precedence</c>, an OU's <c>StreetAddress</c>) that either do not exist in the global
    /// tables or resolve there to a different type's attribute. They take precedence, exactly
    /// as they do in the projector -- filter and output must agree on what a property means.
    /// </para>
    /// </summary>
    public static AdFilterNode? Translate(
        string filterText,
        Func<string, (bool Found, object? Value)> resolveVariable,
        bool allowUnknownProperty = false,
        IReadOnlyDictionary<string, string>? attributeOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(filterText);
        ArgumentNullException.ThrowIfNull(resolveVariable);

        Parser.ParseInput(filterText, out var rawTokens, out var errors);

        // Newlines are dropped up front: a multi-line filter (typically a ScriptBlock body,
        // "-Filter { A -eq 1 -and `n B -eq 2 }") tokenizes with NewLine tokens mid-expression,
        // which the grammar below would otherwise trip over as trailing junk. Statement
        // separators (';') are deliberately NOT dropped -- those must fail the EndOfInput gate.
        var tokens = Array.FindAll(rawTokens,
            t => t.Kind is not (TokenKind.NewLine or TokenKind.LineContinuation));

        // RSAT's "-Filter *": tokenizes as a lone Multiply token (arithmetic mode), not as any
        // form of comparable expression, so it needs its own check ahead of the real parser.
        if (tokens.Length == 2 && tokens[0].Kind == TokenKind.Multiply && tokens[1].Kind == TokenKind.EndOfInput)
            return null;

        // The errors array is unusable in BOTH directions and is consulted only for its
        // message text. Zero errors does not mean valid: "Name -eq a -and" (trailing operator)
        // and "Name -eq a; Write-Host pwned" (trailing statement) both parse clean, which is
        // why the EndOfInput gate below exists. Non-zero does not mean invalid either: a
        // parenthesized group followed by a bareword comparison -- "(A -eq 1) -and B -eq 2",
        // a perfectly good RSAT filter -- makes PowerShell's PARSER complain ("You must
        // provide a value expression..."), because after '(...)' it is in expression mode
        // where a bareword is not an expression. The tokens are fine; only this parser's
        // grammar decides. What IS authoritative is per-token HasError: the TOKENIZER marking
        // a token corrupt (unterminated string, malformed number) means the stream itself is
        // unusable.
        var damaged = Array.Find(tokens, t => t.HasError);
        if (damaged is not null)
            throw new AdFilterTranslationException(
                $"'-Filter' could not be parsed: {(errors.Length > 0 ? errors[0].Message : $"invalid token '{damaged.Text}'")}");

        var parser = new AdFilterParser(tokens, resolveVariable, allowUnknownProperty, attributeOverrides);
        var node = parser.ParseFilterExpression();

        // The real validity gate: nothing may remain after a complete expression. Without this,
        // "Name -eq a -and" silently truncates to "Name -eq a", and "Name -eq a; Write-Host
        // pwned" silently truncates to "Name -eq a" while smuggling a second statement past the
        // filter entirely.
        if (parser.Current.Kind != TokenKind.EndOfInput)
            throw new AdFilterTranslationException(
                $"Unexpected content in '-Filter' after a complete expression, starting at '{parser.Current.Text}'.");

        return node;
    }
}
