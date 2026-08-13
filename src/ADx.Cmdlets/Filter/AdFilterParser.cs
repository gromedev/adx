using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;

namespace ADx.Cmdlets.Filter;

/// <summary>
/// Hand-rolled recursive-descent parser walking the raw <see cref="Token"/> stream from
/// <see cref="Parser.ParseInput(string, out Token[], out ParseError[])"/> directly.
/// <para>
/// AST-walking is not an option here: <c>Parser.ParseInput("Name -like 'j*'")</c> yields a
/// <c>CommandAst</c> (PowerShell parses an unparenthesized filter as a command invocation named
/// "Name"), which has no expression tree to walk. The token stream is the only structure both
/// tokenizer modes agree on, so this parser reads that directly and normalizes both operator
/// encodings via <see cref="AdFilterOperators.Identify"/> as it goes.
/// </para>
/// <para>
/// Grammar (AND binds tighter than OR, matching the plan's golden precedence example):
/// <code>
/// FilterExpr := OrExpr
/// OrExpr     := AndExpr ( 'or' AndExpr )*
/// AndExpr    := NotExpr ( 'and' NotExpr )*
/// NotExpr    := 'not' NotExpr | Primary
/// Primary    := '(' FilterExpr ')' | Comparison
/// Comparison := PropertyName Operator Value
/// </code>
/// </para>
/// <para>
/// Every rejection below shares one rationale: against Active Directory, a structurally valid
/// filter carrying a mistyped or misrendered value is not an error on the wire -- it returns
/// zero rows with success. An explicit exception is always better than that.
/// </para>
/// </summary>
internal sealed class AdFilterParser
{
    // Underscore is included: it is legal in ldapDisplayName and common in HR-sync custom
    // schemas, and blocking it made such attributes unfilterable even under
    // -AllowUnknownProperty, contradicting that switch's purpose.
    private static readonly Regex PropertyNamePattern = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    private readonly Token[] _tokens;
    private readonly Func<string, (bool Found, object? Value)> _resolveVariable;
    private readonly bool _allowUnknownProperty;
    private readonly IReadOnlyDictionary<string, string>? _attributeOverrides;
    private int _pos;

    public AdFilterParser(
        Token[] tokens,
        Func<string, (bool Found, object? Value)> resolveVariable,
        bool allowUnknownProperty,
        IReadOnlyDictionary<string, string>? attributeOverrides = null)
    {
        _tokens = tokens;
        _resolveVariable = resolveVariable;
        _allowUnknownProperty = allowUnknownProperty;
        _attributeOverrides = attributeOverrides;
    }

    public Token Current => _tokens[_pos];

    private Token Advance()
    {
        var token = _tokens[_pos];
        if (_pos < _tokens.Length - 1) _pos++;
        return token;
    }

    public AdFilterNode ParseFilterExpression() => ParseOr();

    private AdFilterNode ParseOr()
    {
        var left = ParseAnd();
        while (AdFilterOperators.Identify(Current) == "or")
        {
            Advance();
            var right = ParseAnd();
            left = left is AdFilterOr existing
                ? new AdFilterOr(existing.Operands.Append(right).ToArray())
                : new AdFilterOr(new[] { left, right });
        }
        return left;
    }

    private AdFilterNode ParseAnd()
    {
        var left = ParseNot();
        while (AdFilterOperators.Identify(Current) == "and")
        {
            Advance();
            var right = ParseNot();
            left = left is AdFilterAnd existing
                ? new AdFilterAnd(existing.Operands.Append(right).ToArray())
                : new AdFilterAnd(new[] { left, right });
        }
        return left;
    }

    private AdFilterNode ParseNot()
    {
        if (AdFilterOperators.Identify(Current) == "not")
        {
            Advance();
            return new AdFilterNot(ParseNot());
        }
        return ParsePrimary();
    }

    private AdFilterNode ParsePrimary()
    {
        if (Current.Kind == TokenKind.LParen)
        {
            Advance();
            var inner = ParseFilterExpression();
            if (Current.Kind != TokenKind.RParen)
                throw new AdFilterTranslationException("Expected a closing ')' in '-Filter'.");
            Advance();
            return inner;
        }

        return ParseComparison();
    }

    private AdFilterNode ParseComparison()
    {
        if (Current.Kind == TokenKind.EndOfInput)
            throw new AdFilterTranslationException("'-Filter' ended unexpectedly; expected an attribute name.");

        var propertyToken = Advance();
        var propertyText = propertyToken.Text;

        // Match on .Text against the identifier shape, never on .Kind: property names arrive
        // as Identifier ("Name"), Generic ("msDS-SupportedEncryptionTypes", which contains a
        // hyphen), In ("in"), Default ("default"), and other keyword-shaped kinds that are
        // still perfectly valid AD attribute names. Genuine statement keywords are identified
        // by TokenFlags.Keyword -- which 'in' and 'default' do NOT carry in this position
        // (they tokenize as command names) -- rather than by kind or by a hand-kept name
        // list. No real AD attribute collides with a PowerShell keyword, so nothing is lost.
        if (propertyToken.TokenFlags.HasFlag(TokenFlags.Keyword))
            throw new AdFilterTranslationException(
                $"'{propertyText}' is a PowerShell keyword and cannot be used as an attribute name in '-Filter'. " +
                "No Active Directory attribute has this name.");

        if (!PropertyNamePattern.IsMatch(propertyText))
            throw new AdFilterTranslationException($"'{propertyText}' is not a valid attribute name in '-Filter'.");

        if (Current.Kind == TokenKind.EndOfInput)
            throw new AdFilterTranslationException(
                $"'-Filter' ended unexpectedly; expected a comparison operator after '{propertyText}'.");

        var operatorToken = Advance();
        var operatorId = AdFilterOperators.Identify(operatorToken);

        // Case-sensitivity is checked before the null-id check: the c-variant TokenKinds are
        // deliberately unmapped in Identify (see AdFilterOperators), so "(A -ceq b)" reaches
        // here with a null id but must still get the case-sensitivity message, not a generic
        // "expected an operator".
        if (AdFilterOperators.IsCaseSensitive(operatorToken))
            throw new AdFilterTranslationException(
                $"'{operatorToken.Text}' is not supported: Active Directory has no case-sensitive matching, and " +
                "silently treating it as case-insensitive would return a superset of what was asked for. " +
                "Use the case-insensitive form.");

        if (operatorId is null)
            throw new AdFilterTranslationException(
                $"Expected a comparison operator (-eq, -ne, -like, ...) after '{propertyText}', found '{operatorToken.Text}'.");

        if (AdFilterOperators.TryGetUnsupportedReason(operatorId, out var reason))
            throw new AdFilterTranslationException(reason);

        if (operatorId is not ("eq" or "ne" or "like" or "notlike" or "ge" or "gt" or "le" or "lt" or "band" or "bor" or "recursivematch" or "approx"))
            throw new AdFilterTranslationException($"'-{operatorId}' is not a recognised '-Filter' operator.");

        if (Current.Kind == TokenKind.EndOfInput)
            throw new AdFilterTranslationException(
                $"'-Filter' ended unexpectedly; expected a value after '-{operatorId}'.");

        var rawValue = ReadValue();
        return BuildComparisonNode(propertyText, operatorId, rawValue);
    }

    // --- Value reading -----------------------------------------------------------------

    private object? ReadValue()
    {
        var token = Advance();

        switch (token)
        {
            case VariableToken v when token.Kind == TokenKind.SplattedVariable:
                throw new AdFilterTranslationException(
                    $"Splatting ('@{v.VariablePath.UserPath}') is not valid in '-Filter'. Use '${v.VariablePath.UserPath}'.");

            case VariableToken v:
                return ReadVariableValue(v, isNested: false);

            case StringExpandableToken se:
                return ExpandString(se);

            case StringLiteralToken sl:
                // Kind Generic is an unquoted bareword; "[datetime]::Now" arrives as exactly
                // one of those. Nobody writes an unquoted bareword starting with '[' meaning
                // it literally, and emitting it as a literal would silently match nothing.
                // Quoted strings (Kind StringLiteral / HereStringLiteral) are exempt.
                if (token.Kind == TokenKind.Generic && sl.Value.StartsWith('['))
                    throw new AdFilterTranslationException(
                        $"'{sl.Value}' looks like an expression, and expressions are not evaluated in '-Filter'. " +
                        "Compute the value into a variable first.");
                return sl.Value;

            case NumberToken num:
                // .Value, not .Text: "0x2" must reach the integer marshaller as the number 2,
                // not the unparseable string "0x2".
                return num.Value;

            default:
                if (token.Kind == TokenKind.DollarParen)
                    throw new AdFilterTranslationException(
                        "Subexpressions ('$(...)') are not evaluated in '-Filter'. Compute the value into a variable first.");
                if (token.Kind == TokenKind.LParen)
                    throw new AdFilterTranslationException(
                        "Parenthesized values ('(Get-Date)...') are not evaluated in '-Filter'. " +
                        "Compute the value into a variable first, e.g. $cutoff = (Get-Date).AddDays(-90).");
                if (token.Kind is TokenKind.AtParen or TokenKind.AtCurly)
                    throw new AdFilterTranslationException(
                        "Arrays are not supported as a single '-Filter' value -- use -or for multiple values.");
                if (token.Kind is TokenKind.Identifier or TokenKind.Generic or TokenKind.Multiply)
                    return token.Text;

                throw new AdFilterTranslationException($"'{token.Text}' is not a valid '-Filter' value.");
        }
    }

    /// <summary>
    /// Resolve a variable token to its value. <c>$true</c>/<c>$false</c>/<c>$null</c> are
    /// handled structurally from <see cref="VariablePath"/> before the resolver ever runs, so
    /// they cannot be shadowed by a same-named user variable. Real variables go through
    /// <c>SessionState.PSVariable.Get</c> semantics via the injected delegate -- distinguishing
    /// "undefined" from "defined as $null" is the point: <c>GetVariableValue</c> returns null
    /// for both, which would make a typo'd variable silently become "-eq $null".
    /// </summary>
    private object? ReadVariableValue(VariableToken v, bool isNested)
    {
        var path = v.VariablePath.UserPath;

        if (path.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        var (found, value) = _resolveVariable(path);
        if (!found)
            throw new AdFilterTranslationException(
                $"Variable '${path}' is not defined. An undefined variable would otherwise silently behave " +
                "as $null and match the wrong set; define it, or write '-eq $null' explicitly.");

        // Member access chains ($u.DistinguishedName, $a.b.c) -- RSAT's filter grammar
        // supports these, so drop-in scripts use them. Indexing and method calls stay out.
        // The chain runs BEFORE unwrapping: a property-bag PSObject (PSCustomObject) loses
        // its note properties the moment BaseObject is taken.
        while (!isNested && Current.Kind == TokenKind.Dot)
        {
            Advance();
            var memberToken = Current;
            if (memberToken.Kind is not (TokenKind.Identifier or TokenKind.Generic) ||
                !PropertyNamePattern.IsMatch(memberToken.Text))
                throw new AdFilterTranslationException(
                    $"Expected a property name after '${path}.', found '{memberToken.Text}'.");
            Advance();
            if (Current.Kind == TokenKind.LParen)
                throw new AdFilterTranslationException(
                    $"Method calls ('${path}.{memberToken.Text}(...)') are not supported in '-Filter'. " +
                    "Compute the value into a variable first.");
            value = GetMemberValue(value, memberToken.Text, path);
        }

        if (!isNested && Current.Kind == TokenKind.LBracket)
            throw new AdFilterTranslationException(
                $"Indexing ('${path}[...]') is not supported in '-Filter'. Compute the value into a variable first.");

        value = Unwrap(value);

        // byte[] is exempt: it is the natural single value for SID/GUID/binary-syntax
        // attributes, not a collection of comparisons.
        if (value is System.Collections.IEnumerable and not string and not byte[])
            throw new AdFilterTranslationException(
                $"'${path}' resolves to a collection. '-Filter' compares a single value at a time -- " +
                "use -or for multiple values.");

        return value;
    }

    /// <summary>
    /// PSVariable values are frequently PSObject-wrapped (anything that ever crossed the
    /// pipeline). Unwrapping is what keeps the collection check and the typed marshallers
    /// honest -- a PSObject-wrapped array is not IEnumerable itself, and would otherwise
    /// sail through to a ToString() that renders "System.Object[]".
    /// </summary>
    private static object? Unwrap(object? value) => value is PSObject pso ? pso.BaseObject : value;

    private static object? GetMemberValue(object? instance, string member, string variableName)
    {
        if (instance is null)
            throw new AdFilterTranslationException(
                $"Cannot read '.{member}' in '-Filter': '${variableName}' (or an earlier member in the chain) is null.");

        // Values stay possibly-PSObject-wrapped through the chain (this branch reads wrapped
        // intermediates fine, and PSObject.Properties also surfaces the reflected properties
        // of a wrapped CLR object); the caller unwraps once at the end.
        if (instance is PSObject pso)
        {
            var psProperty = pso.Properties[member];
            if (psProperty is not null) return psProperty.Value;
            instance = pso.BaseObject;
        }

        try
        {
            var property = instance.GetType().GetProperty(
                member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null)
                throw new AdFilterTranslationException(
                    $"'${variableName}' ({instance.GetType().Name}) has no property '{member}'.");
            return property.GetValue(instance);
        }
        catch (AdFilterTranslationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AdFilterTranslationException(
                $"Reading '.{member}' on '${variableName}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Expand a double-quoted string's embedded variables using absolute source offsets
    /// (<c>Token.Extent</c>), never <c>ToString()</c> on the token. <c>StringExpandableToken.Value</c>
    /// returns the text unexpanded (e.g. literally "*$dept*"), so a naive projector that reads
    /// <c>.Value</c> directly would filter on the string "$dept" rather than its value.
    /// </summary>
    private string ExpandString(StringExpandableToken token)
    {
        if (token.NestedTokens is null || token.NestedTokens.Count == 0)
            return token.Value;

        if (token.Kind == TokenKind.HereStringExpandable)
            throw new AdFilterTranslationException(
                "Variables inside here-strings are not supported in '-Filter'. Use a regular double-quoted string.");

        var source = token.Extent.Text;

        // The splice below works on the RAW source between the quotes; .Value has backtick
        // escapes already processed, which would desynchronise the nested tokens' offsets.
        // A raw splice of "`n$v" would emit a literal backtick-n -- diverging silently from
        // what PowerShell would build -- so reject rather than guess.
        if (source.Contains('`'))
            throw new AdFilterTranslationException(
                "Escape sequences (`) in an expandable '-Filter' string are not supported. " +
                "Use a single-quoted string or compute the value into a variable.");

        var parentStart = token.Extent.StartOffset;
        var sb = new StringBuilder();
        var cursor = 1; // skip the opening quote

        foreach (var nested in token.NestedTokens)
        {
            if (nested.Kind == TokenKind.DollarParen)
                throw new AdFilterTranslationException(
                    "Subexpressions '$(...)' inside a '-Filter' string are not evaluated. " +
                    "\"*$dept*\" works; \"*$($u.Dept)*\" does not -- compute the value into a variable first.");

            var relativeStart = nested.Extent.StartOffset - parentStart;
            var relativeEnd = nested.Extent.EndOffset - parentStart;

            sb.Append(source, cursor, relativeStart - cursor);

            if (nested is VariableToken nv)
            {
                var value = ReadVariableValue(nv, isNested: true);
                sb.Append(ScalarToString(value, $"the value of '${nv.VariablePath.UserPath}'"));
            }
            else
            {
                throw new AdFilterTranslationException(
                    "Only simple variable references ('$name') are supported inside a '-Filter' string value.");
            }

            cursor = relativeEnd;
        }

        sb.Append(source, cursor, source.Length - 1 - cursor); // stop before the closing quote
        return sb.ToString();
    }

    private static string ScalarToString(object? value, string context) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "True" : "False",
        sbyte or byte or short or ushort or int or uint or long =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        float or double or decimal => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        _ => throw new AdFilterTranslationException(
            $"Cannot interpolate {context} (a {value.GetType().Name}) into a '-Filter' string.")
    };

    // --- Comparison building -------------------------------------------------------------

    private AdFilterNode BuildComparisonNode(string propertyText, string operatorId, object? rawValue)
    {
        if (AdSyntheticProperties.UnsupportedForFiltering.Contains(propertyText))
            throw new AdFilterTranslationException(
                $"'{propertyText}' cannot be used in '-Filter': it is computed client-side or from data a filter " +
                "cannot reach (a security descriptor, a bind response, or DNS). Filter on the underlying " +
                "attribute instead, or filter the results in PowerShell.");

        // Constructed wire attributes: AD computes them per read and never evaluates them in
        // a filter -- the comparison would succeed with zero rows, silently. Checked before
        // the resolution tables so registering their SYNTAX (needed for projection) cannot
        // quietly open a dead filter path.
        if (AdSyntheticProperties.UnfilterableConstructedAttributes.TryGetValue(propertyText, out var redirect))
            throw new AdFilterTranslationException(
                $"'{propertyText}' is a constructed attribute: the DC computes it per read and does not evaluate " +
                $"it in filters, so the comparison would successfully match nothing. Instead, {redirect}.");

        if (AdSyntheticProperties.IsKnownSyntheticProperty(propertyText))
            return BuildSyntheticComparison(propertyText, operatorId, rawValue);

        // Per-type overrides win over the global tables -- the same precedence the projector
        // applies. Without this, a PSO's MinPasswordLength resolves to the domain-head
        // minPwdLength (absent on PSOs: silent zero rows) and an OU's StreetAddress to
        // streetAddress where OUs store the value in street.
        bool isKnownAttribute;
        string? ldapName;
        if (_attributeOverrides is not null &&
            _attributeOverrides.TryGetValue(propertyText, out var overriddenName))
        {
            ldapName = overriddenName;
            isKnownAttribute = true;
        }
        else
        {
            isKnownAttribute = AdAttributeSchema.TryResolveAttributeName(propertyText, out ldapName);
        }

        if (!isKnownAttribute)
        {
            if (!_allowUnknownProperty)
                throw new AdFilterTranslationException(
                    $"'{propertyText}' is not a recognised attribute or RSAT property name. A misspelled property " +
                    "would not error against AD -- it would successfully match nothing. Fix the name, or pass " +
                    "-AllowUnknownProperty if the attribute genuinely exists in your schema.");
            ldapName = propertyText;
        }

        if (rawValue is null)
        {
            // AD has no NULL comparison, only attribute presence -- "(A=)" is malformed.
            return operatorId switch
            {
                "eq" => new AdFilterAbsent(ldapName),
                "ne" => new AdFilterPresent(ldapName),
                _ => throw new AdFilterTranslationException(
                    $"'-{operatorId}' cannot be used with $null. Only -eq and -ne support $null.")
            };
        }

        var syntax = AdAttributeSchema.SyntaxOf(ldapName);

        // GeneralizedTime comparisons get their own builder: AD stores these attributes at
        // whole-second precision, so a sub-second bound must be rounded direction-aware (with
        // the operator adjusted to keep the comparison exact) rather than silently truncated.
        // -approx is included: AD evaluates '~=' as plain equality, so it shares -eq's
        // sub-second refusal rather than slipping through to a silent truncation.
        if (syntax == AdAttributeSyntax.GeneralizedTime &&
            operatorId is "eq" or "ne" or "approx" or "ge" or "gt" or "le" or "lt")
            return BuildGeneralizedTimeComparison(ldapName, operatorId, rawValue, propertyText);

        return operatorId switch
        {
            "eq" => new AdFilterEquality(ldapName, MarshalExact(syntax, rawValue, propertyText)),
            "ne" => new AdFilterInequality(ldapName, MarshalExact(syntax, rawValue, propertyText)),
            "like" => new AdFilterEquality(ldapName, MarshalPattern(syntax, rawValue, propertyText)),
            "notlike" => new AdFilterInequality(ldapName, MarshalPattern(syntax, rawValue, propertyText)),
            "ge" => new AdFilterGreaterOrEqual(ldapName, MarshalOrdering(syntax, rawValue, propertyText)),
            "gt" => new AdFilterGreaterThan(ldapName, MarshalOrdering(syntax, rawValue, propertyText)),
            "le" => new AdFilterLessOrEqual(ldapName, MarshalOrdering(syntax, rawValue, propertyText)),
            "lt" => new AdFilterLessThan(ldapName, MarshalOrdering(syntax, rawValue, propertyText)),
            "band" => new AdFilterBitAnd(ldapName, MarshalBitmask(syntax, rawValue, propertyText, isKnownAttribute)),
            "bor" => new AdFilterBitOr(ldapName, MarshalBitmask(syntax, rawValue, propertyText, isKnownAttribute)),
            "recursivematch" => BuildRecursiveMatch(ldapName, rawValue, propertyText),
            "approx" => new AdFilterApprox(ldapName, MarshalExact(syntax, rawValue, propertyText)),
            _ => throw new AdFilterTranslationException($"'-{operatorId}' is not supported in '-Filter'.")
        };
    }

    /// <summary>
    /// GeneralizedTime bounds, kept exact against whole-second storage. For a bound with
    /// sub-second ticks the value is rounded in the operator's direction AND the strictness
    /// dropped, which is an equivalence, not an approximation: for whole-second T,
    /// <c>T &gt;= d</c> and <c>T &gt; d</c> both hold exactly when <c>T &gt;= ceil(d)</c>, and
    /// <c>T &lt;= d</c> / <c>T &lt; d</c> exactly when <c>T &lt;= floor(d)</c>. Equality
    /// against a sub-second bound can never match (and its negation always matches), so both
    /// are refused rather than silently truncated -- the truncated filter would include
    /// entries stamped at exactly the truncated second, which the caller's bound excludes.
    /// </summary>
    private static AdFilterNode BuildGeneralizedTimeComparison(
        string ldapName, string operatorId, object rawValue, string propertyText)
    {
        var utc = LdapConvert.ToUtc(ToDateTime(rawValue, propertyText));
        var fractionalTicks = utc.Ticks % TimeSpan.TicksPerSecond;

        if (fractionalTicks == 0)
        {
            var value = LdapAssertionValue.Verbatim(LdapConvert.ToGeneralizedTime(utc));
            return operatorId switch
            {
                "eq" => new AdFilterEquality(ldapName, value),
                "ne" => new AdFilterInequality(ldapName, value),
                "approx" => new AdFilterApprox(ldapName, value),
                "ge" => new AdFilterGreaterOrEqual(ldapName, value),
                "gt" => new AdFilterGreaterThan(ldapName, value),
                "le" => new AdFilterLessOrEqual(ldapName, value),
                _ => new AdFilterLessThan(ldapName, value)
            };
        }

        var floor = new DateTime(utc.Ticks - fractionalTicks, DateTimeKind.Utc);
        return operatorId switch
        {
            // A fractional bound inside the FINAL representable second has no whole-second
            // ceiling: AddSeconds(1) would throw a raw ArgumentOutOfRangeException, escaping
            // the translation-error contract exactly like the pre-1601 FILETIME case.
            "ge" or "gt" when floor.Ticks > DateTime.MaxValue.Ticks - TimeSpan.TicksPerSecond =>
                throw new AdFilterTranslationException(
                    $"'{propertyText}' cannot be compared against a bound inside the final second of the " +
                    "representable time range: no whole-second timestamp can satisfy it, so the filter " +
                    "could only successfully match nothing."),
            "ge" or "gt" => new AdFilterGreaterOrEqual(
                ldapName, LdapAssertionValue.Verbatim(LdapConvert.ToGeneralizedTime(floor.AddSeconds(1)))),
            "le" or "lt" => new AdFilterLessOrEqual(
                ldapName, LdapAssertionValue.Verbatim(LdapConvert.ToGeneralizedTime(floor))),
            _ => throw new AdFilterTranslationException(
                $"'{propertyText}' stores whole seconds; -{operatorId} against a sub-second timestamp " +
                "can never compare equal, so the filter would successfully match the wrong set. " +
                "Truncate the value to a whole second first.")
        };
    }

    private static AdFilterNode BuildSyntheticComparison(string propertyText, string operatorId, object? rawValue)
    {
        if (operatorId is not ("eq" or "ne"))
            throw new AdFilterTranslationException($"'{propertyText}' only supports -eq and -ne in '-Filter'.");

        if (rawValue is null)
            throw new AdFilterTranslationException($"'{propertyText}' cannot be compared to $null in '-Filter'.");

        if (AdSyntheticProperties.IsBooleanSynthetic(propertyText))
        {
            // Strings spelled true/false are accepted because "Enabled -eq 'true'" is all
            // over real scripts; anything else is an error, not a truthiness guess.
            var boolValue = rawValue switch
            {
                bool b => b,
                string s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
                string s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
                _ => throw new AdFilterTranslationException(
                    $"'{propertyText}' requires $true or $false in '-Filter', not '{rawValue}'.")
            };

            if (operatorId == "ne") boolValue = !boolValue;
            AdSyntheticProperties.TryEmitBooleanEquality(propertyText, boolValue, out var node);
            return node;
        }

        // GroupScope / GroupCategory
        if (rawValue is not string enumName)
            throw new AdFilterTranslationException(
                $"'{propertyText}' requires a string value in '-Filter', not '{rawValue}'.");

        AdSyntheticProperties.TryEmitStringEquality(propertyText, enumName, out var equality);
        // Unwrap rather than stack: "GroupCategory -eq 'Distribution'" already emits a
        // negated node, and wrapping that again would render (!(!(...))).
        return operatorId == "eq"
            ? equality
            : equality is AdFilterNot negated ? negated.Operand : new AdFilterNot(equality);
    }

    private static AdFilterNode BuildRecursiveMatch(string ldapName, object rawValue, string propertyText)
    {
        // The 1941 chain rule walks LINK-valued attributes (manager chains are a real, if
        // exotic, use RSAT accepts). Plain DN-SYNTAX attributes with no link pair
        // (objectCategory, distinguishedName, fSMORoleOwner) are not walkable -- the chain
        // is degenerate there, and AD would answer the structurally valid filter with the
        // wrong set and a success code, so they stay a loud error.
        if (!AdAttributeSchema.IsLinkValuedDnAttribute(ldapName))
            throw new AdFilterTranslationException(
                $"'-RecursiveMatch' (transitive chain matching) only applies to link-valued DN attributes " +
                $"such as 'member', 'memberOf' or 'manager'; '{propertyText}' is not one.");

        if (rawValue is not string dn || dn.Length == 0)
            throw new AdFilterTranslationException("'-RecursiveMatch' needs a distinguished name string.");

        return new AdFilterRecursiveMatch(ldapName, LdapAssertionValue.Exact(dn));
    }

    // --- Typed value marshalling -- one source of truth per syntax, no ToString() fallback ---

    private static LdapAssertionValue MarshalExact(AdAttributeSyntax syntax, object rawValue, string propertyText) =>
        syntax switch
        {
            AdAttributeSyntax.Integer or AdAttributeSyntax.LargeInteger =>
                LdapAssertionValue.Verbatim(ToIntegerText(rawValue, propertyText)),
            AdAttributeSyntax.Boolean => LdapAssertionValue.Verbatim(ToLdapBooleanText(rawValue, propertyText)),
            AdAttributeSyntax.GeneralizedTime => LdapAssertionValue.Verbatim(LdapConvert.ToGeneralizedTime(ToDateTime(rawValue, propertyText))),
            AdAttributeSyntax.FileTime => MarshalFileTime(rawValue, propertyText),
            // Explicit, not the String default arm: a TimeSpan blind-marshalled as text would
            // successfully match nothing. The interval attributes live on the domain head and
            // are read, never searched.
            AdAttributeSyntax.Interval => throw IntervalNotFilterable(propertyText),
            AdAttributeSyntax.Sid => LdapAssertionValue.Binary(ToSidBytes(rawValue, propertyText)),
            AdAttributeSyntax.Guid => LdapAssertionValue.Binary(ToGuidBytes(rawValue, propertyText)),
            AdAttributeSyntax.Binary => rawValue is byte[] bytes
                ? LdapAssertionValue.Binary(bytes)
                : throw new AdFilterTranslationException(
                    $"'{propertyText}' is a binary attribute; '-Filter' needs a byte[] value for it."),
            _ => LdapAssertionValue.Exact(RejectWildcardInExactValue(ToNonEmptyText(rawValue, propertyText), propertyText))
        };

    /// <summary>
    /// A '*' inside an exact-match value is where RSAT and PowerShell semantics part ways:
    /// RSAT passes it to LDAP as a wildcard (its <c>mail -ne '*'</c> idiom means "mail
    /// absent"), while PowerShell's <c>-eq</c> means an exact match against a literal
    /// asterisk. Escaping it (what this module did) silently inverted the result set of the
    /// RSAT idiom -- <c>(!(mail=\2a))</c> matches nearly the whole directory. Neither reading
    /// is safe to pick silently, so this is a terminating error with both spellings offered.
    /// </summary>
    private static string RejectWildcardInExactValue(string text, string propertyText)
    {
        if (text.Contains('*'))
            throw new AdFilterTranslationException(
                $"'{propertyText}' compared with an exact-match operator (-eq/-ne/-approx) against a value " +
                "containing '*': RSAT would treat the '*' as a wildcard, PowerShell's -eq means a literal " +
                "asterisk, and silently picking either can invert the result set (RSAT's \"mail -ne '*'\" means " +
                "\"mail absent\"). Use -like/-notlike on string attributes for wildcard or presence semantics " +
                "(\"mail -notlike '*'\" is \"mail absent\"), '-eq $null'/'-ne $null' for presence tests on any " +
                "attribute, or -LDAPFilter with the escaped value '\\2a' for a literal asterisk.");
        return text;
    }

    /// <summary>
    /// FileTime attributes accept both shapes that appear in real queries: a DateTime (or a
    /// date string), and a raw integer -- "pwdLastSet -eq 0" (must change password at next
    /// logon) and "accountExpires -eq 0" are documented, widely used filters.
    /// </summary>
    private static LdapAssertionValue MarshalFileTime(object rawValue, string propertyText)
    {
        if (rawValue is sbyte or byte or short or ushort or int or uint or long or ulong)
            return LdapAssertionValue.Verbatim(ToIntegerText(rawValue, propertyText));
        if (rawValue is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
            return LdapAssertionValue.Verbatim(raw.ToString(CultureInfo.InvariantCulture));

        try
        {
            return LdapAssertionValue.Verbatim(LdapConvert.ToFileTime(ToDateTime(rawValue, propertyText)));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // [datetime]::MinValue and friends: without this wrap the raw
            // ArgumentOutOfRangeException escapes the cmdlet's translation-error handling and
            // surfaces as an unclassified crash instead of the promised clean error.
            throw new AdFilterTranslationException(
                $"'{propertyText}' cannot be compared against a timestamp before 1601-01-01 UTC (the FILETIME " +
                "epoch): Active Directory cannot store one. To match \"never set\", compare against the raw " +
                $"sentinel instead: '{propertyText} -eq 0'.", ex);
        }
    }

    private static LdapAssertionValue MarshalPattern(AdAttributeSyntax syntax, object rawValue, string propertyText)
    {
        // -like on a DN-syntax attribute gets its own message: AD has no substring matching
        // rule for DN syntax, so (memberOf=*x*) is not an error on the wire -- it just
        // matches nothing, silently.
        if (syntax == AdAttributeSyntax.Dn)
            throw new AdFilterTranslationException(
                $"'-like' is not supported on '{propertyText}': Active Directory cannot substring-match " +
                "DN-valued attributes (the query would silently match nothing). Use -eq with a full DN, " +
                "or -RecursiveMatch for group membership.");

        if (syntax != AdAttributeSyntax.String)
            throw new AdFilterTranslationException(
                $"'-like'/'-notlike' only apply to string attributes; '{propertyText}' is {syntax}-valued.");

        if (rawValue is not string pattern)
            throw new AdFilterTranslationException(
                $"'-like' on '{propertyText}' needs a string pattern, not '{rawValue}' ({rawValue.GetType().Name}).");
        if (pattern.Length == 0)
            throw EmptyStringValue(propertyText);

        return LdapAssertionValue.Pattern(pattern);
    }

    private static LdapAssertionValue MarshalOrdering(AdAttributeSyntax syntax, object rawValue, string propertyText) =>
        syntax switch
        {
            AdAttributeSyntax.Integer or AdAttributeSyntax.LargeInteger
                or AdAttributeSyntax.FileTime or AdAttributeSyntax.GeneralizedTime =>
                MarshalExact(syntax, rawValue, propertyText),
            AdAttributeSyntax.Interval => throw IntervalNotFilterable(propertyText),
            AdAttributeSyntax.String => LdapAssertionValue.Exact(ToNonEmptyText(rawValue, propertyText)),
            _ => throw new AdFilterTranslationException(
                $"Ordering comparisons (-gt/-ge/-lt/-le) are not supported on '{propertyText}' ({syntax}-valued).")
        };

    private static AdFilterTranslationException IntervalNotFilterable(string propertyText) => new(
        $"'{propertyText}' is an interval-valued attribute (a duration). Filtering on interval " +
        "attributes is not supported; read the value with Get-ADxDefaultDomainPasswordPolicy or " +
        "Search-ADxObject and compare it in PowerShell.");

    private static LdapAssertionValue MarshalBitmask(
        AdAttributeSyntax syntax, object rawValue, string propertyText, bool isKnownAttribute)
    {
        // Unknown pass-through attributes (-AllowUnknownProperty) default to String syntax;
        // trust the caller there. On a KNOWN non-integer attribute, a bitwise test is a
        // structural mistake.
        if (syntax != AdAttributeSyntax.Integer && isKnownAttribute)
            throw new AdFilterTranslationException(
                $"'-band'/'-bor' need an integer-syntax attribute; '{propertyText}' is {syntax}-valued.");

        return LdapAssertionValue.Verbatim(ToIntegerText(rawValue, propertyText));
    }

    /// <summary>
    /// Text for String-syntax assertion values. Explicit type-by-type conversion, not a
    /// ToString() fallback: numbers and Guids render invariantly, booleans as True/False;
    /// a DateTime or arbitrary object here is an error, because whatever ToString() would
    /// produce is not what the directory stores.
    /// </summary>
    private static string ToNonEmptyText(object? value, string propertyText)
    {
        var text = value switch
        {
            string s => s,
            bool b => b ? "True" : "False",
            sbyte or byte or short or ushort or int or uint or long =>
                Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ulong u => u.ToString(CultureInfo.InvariantCulture),
            float or double or decimal => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            null => throw new AdFilterTranslationException($"'{propertyText}' requires a value."),
            _ => throw new AdFilterTranslationException(
                $"'{propertyText}' is a string attribute; cannot use a {value.GetType().Name} value in '-Filter'. " +
                "Values are marshalled by the attribute's syntax, never blind-converted -- a mistyped value " +
                "would otherwise successfully match nothing.")
        };

        if (text.Length == 0)
            throw EmptyStringValue(propertyText);
        return text;
    }

    private static string ToIntegerText(object? value, string propertyText) => value switch
    {
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) =>
            parsed.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        null => throw new AdFilterTranslationException($"'{propertyText}' requires an integer value."),
        _ => throw new AdFilterTranslationException($"'{propertyText}' requires an integer value, not '{value}'.")
    };

    private static string ToLdapBooleanText(object? value, string propertyText) => value switch
    {
        bool b => b ? "TRUE" : "FALSE",
        string s when bool.TryParse(s, out var parsed) => parsed ? "TRUE" : "FALSE",
        _ => throw new AdFilterTranslationException($"'{propertyText}' requires a boolean value, not '{value}'.")
    };

    /// <summary>
    /// Invariant culture matches PowerShell's own [datetime] cast semantics (which are
    /// invariant, not current-culture); AssumeLocal matches RSAT, which treats naked date
    /// strings in a filter as local wall-clock time. LdapConvert's encoders convert local
    /// kinds to UTC before rendering.
    /// </summary>
    private static DateTime ToDateTime(object? value, string propertyText) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        string s when DateTime.TryParse(
            s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) => parsed,
        _ => throw new AdFilterTranslationException(
            $"'{propertyText}' requires a DateTime value (for example a $variable holding [DateTime]), not '{value}'.")
    };

    private static byte[] ToSidBytes(object? value, string propertyText)
    {
        switch (value)
        {
            case byte[] b:
                return b;
            case string s:
                return LdapConvert.SddlToSid(s) ??
                    throw new AdFilterTranslationException($"'{s}' is not a valid SID for '{propertyText}'.");
            default:
            {
                // Duck-typed: ADxSecurityIdentifier and Windows' SecurityIdentifier both carry
                // SDDL in a string 'Value' property. Structural, not a ToString() fallback.
                var property = value?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (property?.PropertyType == typeof(string) &&
                    property.GetValue(value) is string sddl &&
                    LdapConvert.SddlToSid(sddl) is { } sid)
                    return sid;

                throw new AdFilterTranslationException(
                    $"'{propertyText}' requires a SID (SDDL string 'S-1-5-...', SecurityIdentifier, or byte[]), not '{value}'.");
            }
        }
    }

    private static byte[] ToGuidBytes(object? value, string propertyText) => value switch
    {
        byte[] { Length: 16 } b => b,
        Guid g => g.ToByteArray(),
        string s when Guid.TryParse(s, out var g) => g.ToByteArray(),
        _ => throw new AdFilterTranslationException($"'{value}' is not a valid GUID for '{propertyText}'.")
    };

    private static AdFilterTranslationException EmptyStringValue(string propertyText) => new(
        $"Empty string values are not valid in '-Filter' ('({propertyText}=)' is malformed LDAP). " +
        $"To match entries where '{propertyText}' is not set, use '{propertyText} -eq $null'.");
}
