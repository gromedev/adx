namespace ADx.Engine.Filter;

/// <summary>
/// AST for a translated AD filter. Built by the tokenizer/parser bridge in
/// <c>ADx.Cmdlets/Filter</c> (needs <c>System.Management.Automation</c> to consume PowerShell's
/// tokenizer) and consumed here by <see cref="AdFilterEmitter"/>, which needs neither SMA nor a
/// directory connection. <see cref="Attribute"/> on every leaf is the already-resolved LDAP
/// attribute name -- resolving an RSAT alias like <c>sn</c>/<c>Surname</c> is the parser's job,
/// not the AST's.
/// </summary>
public abstract record AdFilterNode;

/// <summary>(Attribute=Value)</summary>
public sealed record AdFilterEquality(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>(!(Attribute=Value))</summary>
public sealed record AdFilterInequality(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>
/// (Attribute~=Value) -- RFC 4511 approximate match, for RSAT's <c>-approx</c>. Active
/// Directory implements no phonetic algorithm and evaluates <c>~=</c> as plain equality,
/// but the operator is part of RSAT's accepted filter grammar, so a drop-in module must
/// accept it and emit the same wire filter RSAT does.
/// </summary>
public sealed record AdFilterApprox(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>(Attribute&gt;=Value)</summary>
public sealed record AdFilterGreaterOrEqual(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>
/// (&amp;(Attribute&gt;=Value)(!(Attribute=Value))) -- LDAP (RFC 4511) has no strict
/// greater-than operator, so "&gt;" is "&gt;=, and not equal".
/// </summary>
public sealed record AdFilterGreaterThan(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>(Attribute&lt;=Value)</summary>
public sealed record AdFilterLessOrEqual(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>(&amp;(Attribute&lt;=Value)(!(Attribute=Value))) -- see <see cref="AdFilterGreaterThan"/>.</summary>
public sealed record AdFilterLessThan(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>(Attribute=*) -- the attribute has at least one value.</summary>
public sealed record AdFilterPresent(string Attribute) : AdFilterNode;

/// <summary>(!(Attribute=*)) -- the LDAP idiom for "-eq $null": the attribute is absent.</summary>
public sealed record AdFilterAbsent(string Attribute) : AdFilterNode;

/// <summary>
/// (Attribute:1.2.840.113556.1.4.803:=Value) -- AD's bitwise-AND matching rule, for
/// <c>-band</c>.
/// </summary>
public sealed record AdFilterBitAnd(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>
/// (Attribute:1.2.840.113556.1.4.804:=Value) -- AD's bitwise-OR matching rule, for
/// <c>-bor</c>.
/// </summary>
public sealed record AdFilterBitOr(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>
/// (Attribute:1.2.840.113556.1.4.1941:=Value) -- AD's transitive closure matching rule, for
/// <c>-recursivematch</c>. RSAT/AD restricts this to <c>member</c>/<c>memberOf</c>; that
/// restriction is enforced by the translator that constructs this node, not by the AST or the
/// emitter, both of which are pure structure with no notion of "which attributes exist".
/// </summary>
public sealed record AdFilterRecursiveMatch(string Attribute, LdapAssertionValue Value) : AdFilterNode;

/// <summary>(&amp;Operand1 Operand2 ...)</summary>
public sealed record AdFilterAnd(IReadOnlyList<AdFilterNode> Operands) : AdFilterNode;

/// <summary>(|Operand1 Operand2 ...)</summary>
public sealed record AdFilterOr(IReadOnlyList<AdFilterNode> Operands) : AdFilterNode;

/// <summary>(!Operand)</summary>
public sealed record AdFilterNot(AdFilterNode Operand) : AdFilterNode;

/// <summary>
/// A pre-built, already-syntactically-valid LDAP filter string, embedded verbatim. Used to fold
/// a preset's base object-class filter (e.g. <c>(&amp;(objectCategory=person)(objectClass=user))</c>)
/// into the same AST as the user's translated <c>-Filter</c>/<c>-LDAPFilter</c>, rather than
/// string-concatenating two independently-produced filters at the call site.
/// </summary>
public sealed record AdFilterRaw(string Filter) : AdFilterNode;
