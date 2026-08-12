using System.Text;

namespace ADx.Engine.Filter;

/// <summary>
/// Renders an <see cref="AdFilterNode"/> tree to LDAP filter text (RFC 4515). Purely
/// structural: every value arrives pre-escaped as an <see cref="LdapAssertionValue"/>, so this
/// type makes no escaping decisions and cannot introduce the <c>-eq</c>/<c>-like</c> escaping
/// bug -- that choice was already made, and typed, at the value's construction site.
/// </summary>
public static class AdFilterEmitter
{
    public const string OidBitAnd = "1.2.840.113556.1.4.803";
    public const string OidBitOr = "1.2.840.113556.1.4.804";
    public const string OidRecursiveMatch = "1.2.840.113556.1.4.1941";

    public static string Emit(AdFilterNode node)
    {
        var sb = new StringBuilder();
        EmitInto(node, sb);
        return sb.ToString();
    }

    private static void EmitInto(AdFilterNode node, StringBuilder sb)
    {
        switch (node)
        {
            case AdFilterEquality n:
                EmitAssertion(sb, n.Attribute, "=", n.Value);
                break;

            case AdFilterInequality n:
                sb.Append("(!");
                EmitAssertion(sb, n.Attribute, "=", n.Value);
                sb.Append(')');
                break;

            case AdFilterApprox n:
                EmitAssertion(sb, n.Attribute, "~=", n.Value);
                break;

            case AdFilterGreaterOrEqual n:
                EmitAssertion(sb, n.Attribute, ">=", n.Value);
                break;

            case AdFilterGreaterThan n:
                sb.Append("(&");
                EmitAssertion(sb, n.Attribute, ">=", n.Value);
                sb.Append("(!");
                EmitAssertion(sb, n.Attribute, "=", n.Value);
                sb.Append("))");
                break;

            case AdFilterLessOrEqual n:
                EmitAssertion(sb, n.Attribute, "<=", n.Value);
                break;

            case AdFilterLessThan n:
                sb.Append("(&");
                EmitAssertion(sb, n.Attribute, "<=", n.Value);
                sb.Append("(!");
                EmitAssertion(sb, n.Attribute, "=", n.Value);
                sb.Append("))");
                break;

            case AdFilterPresent n:
                sb.Append('(').Append(n.Attribute).Append("=*)");
                break;

            case AdFilterAbsent n:
                sb.Append("(!(").Append(n.Attribute).Append("=*))");
                break;

            case AdFilterBitAnd n:
                EmitExtensibleMatch(sb, n.Attribute, OidBitAnd, n.Value);
                break;

            case AdFilterBitOr n:
                EmitExtensibleMatch(sb, n.Attribute, OidBitOr, n.Value);
                break;

            case AdFilterRecursiveMatch n:
                EmitExtensibleMatch(sb, n.Attribute, OidRecursiveMatch, n.Value);
                break;

            case AdFilterAnd n:
                EmitConjunction(sb, '&', n.Operands);
                break;

            case AdFilterOr n:
                EmitConjunction(sb, '|', n.Operands);
                break;

            case AdFilterNot n:
                sb.Append("(!");
                EmitInto(n.Operand, sb);
                sb.Append(')');
                break;

            case AdFilterRaw n:
                sb.Append(n.Filter);
                break;

            default:
                throw new NotSupportedException($"Unknown {nameof(AdFilterNode)} type '{node.GetType()}'.");
        }
    }

    private static void EmitAssertion(StringBuilder sb, string attribute, string op, LdapAssertionValue value) =>
        sb.Append('(').Append(attribute).Append(op).Append(value.Escaped).Append(')');

    private static void EmitExtensibleMatch(StringBuilder sb, string attribute, string oid, LdapAssertionValue value) =>
        sb.Append('(').Append(attribute).Append(':').Append(oid).Append(":=").Append(value.Escaped).Append(')');

    private static void EmitConjunction(StringBuilder sb, char op, IReadOnlyList<AdFilterNode> operands)
    {
        if (operands.Count == 0)
            throw new ArgumentException($"A '{op}' filter node needs at least one operand.", nameof(operands));

        sb.Append('(').Append(op);
        foreach (var operand in operands) EmitInto(operand, sb);
        sb.Append(')');
    }
}
