using System.Management.Automation;
using ADx.Cmdlets.Filter;
using ADx.Engine.Filter;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M2 guard cases: everything that must fail LOUDLY. The failure mode this whole design
/// defends against is a filter that translates "successfully" into something subtly different
/// from what was asked -- against AD that is not an error, it is a wrong result set with a
/// success code. So every case here asserts both the exception type and a distinctive
/// fragment of its message, because the message is the product.
/// </summary>
public class AdFilterTranslatorGuardTests
{
    private static readonly Func<string, (bool Found, object? Value)> NoVariables =
        _ => (false, null);

    private static Func<string, (bool Found, object? Value)> Vars(params (string Name, object? Value)[] variables)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables) map[name] = value;
        return name => map.TryGetValue(name, out var v) ? (true, v) : (false, null);
    }

    private static AdFilterTranslationException Fails(
        string filter, Func<string, (bool Found, object? Value)>? resolver = null) =>
        Assert.Throws<AdFilterTranslationException>(
            () => AdFilterTranslator.Translate(filter, resolver ?? NoVariables));

    private static void FailsWith(
        string filter, string messagePart, Func<string, (bool Found, object? Value)>? resolver = null)
    {
        var ex = Fails(filter, resolver);
        Assert.Contains(messagePart, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- structural validity: EndOfInput is the gate, not the parse error count ----

    [Theory]
    // both of these parse with ZERO PowerShell errors -- the EndOfInput requirement is
    // what catches them
    [InlineData("Name -eq 'a' -and", "ended unexpectedly")]
    [InlineData("Name -eq 'a'; Write-Host pwned", "Unexpected content")]
    [InlineData("Name -eq 'a' Title", "Unexpected content")]
    [InlineData("Name -eq 'a') ", "Unexpected content")]
    [InlineData("Name -eq 'a' -or", "ended unexpectedly")]
    [InlineData("Name -eq", "expected a value")]
    [InlineData("Name", "expected a comparison operator")]
    [InlineData("", "ended unexpectedly")]
    [InlineData("   ", "ended unexpectedly")]
    [InlineData("(Name -eq 'a'", "closing ')'")]
    [InlineData("-not", "ended unexpectedly")]
    public void StructurallyInvalidFilters(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    // ---- property-name position ----

    [Theory]
    [InlineData("-eq 'a'", "not a valid attribute name")]
    [InlineData("$x -eq 'a'", "not a valid attribute name")]
    [InlineData("'Name' -eq 'a'", "not a valid attribute name")]
    [InlineData("\"Name\" -eq 'a'", "not a valid attribute name")]
    [InlineData("5 -eq 'a'", "not a valid attribute name")]
    public void InvalidPropertyPositions(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    // statement keywords carry TokenFlags.Keyword and can never be attribute names; note the
    // parse-ERROR array is deliberately not the gate here (valid mixed-encoding filters
    // produce parser errors too), the token flag is
    [InlineData("class -eq 'a'")]
    [InlineData("if -eq 'a'")]
    [InlineData("filter -eq 'a'")]
    [InlineData("switch -eq 'a'")]
    [InlineData("for -eq 'a'")]
    public void PowerShellKeywords_AreRejected(string filter)
    {
        FailsWith(filter, "PowerShell keyword");
    }

    [Theory]
    // tokenizer-level corruption (Token.HasError) IS authoritative, unlike parser errors
    [InlineData("Name -eq 'abc")]
    [InlineData("Name -eq \"abc")]
    public void UnterminatedStrings_AreRejected(string filter)
    {
        FailsWith(filter, "could not be parsed");
    }

    // ---- unsupported operators, each with its own explanation ----

    [Theory]
    [InlineData("Name -match 'j.*'", "regex")]
    [InlineData("Name -notmatch 'j.*'", "regex")]
    [InlineData("Name -in 'a'", "-in is not supported")]
    [InlineData("Name -notin 'a'", "-notin is not supported")]
    [InlineData("memberOf -contains 'CN=x'", "-contains is not supported")]
    [InlineData("memberOf -notcontains 'CN=x'", "-notcontains is not supported")]
    [InlineData("Name -replace 'a'", "-replace is not supported")]
    // The i-prefixed spellings get the same tailored explanation in BOTH tokenizer
    // encodings; the command-mode ParameterToken form previously fell through to a generic
    // "not a recognised operator" while the parenthesized form explained itself.
    [InlineData("Name -imatch 'j.*'", "regex")]
    [InlineData("(Name -imatch 'j.*')", "regex")]
    [InlineData("Name -icontains 'a'", "-contains is not supported")]
    [InlineData("Name -iin 'a'", "-in is not supported")]
    [InlineData("Name -ireplace 'a'", "-replace is not supported")]
    public void UnsupportedOperators(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    // both encodings: bareword command mode (ParameterToken) and parenthesized expression
    // mode (dedicated TokenKind) must produce the same rejection
    [InlineData("Name -ceq 'a'")]
    [InlineData("(Name -ceq 'a')")]
    [InlineData("Name -cne 'a'")]
    [InlineData("(Name -cne 'a')")]
    [InlineData("Name -clike 'a*'")]
    [InlineData("(Name -clike 'a*')")]
    [InlineData("Name -cnotlike 'a*'")]
    [InlineData("(Name -cnotlike 'a*')")]
    [InlineData("logonCount -cgt 5")]
    [InlineData("(logonCount -cgt 5)")]
    public void CaseSensitiveOperators_AreRejectedNotDowngraded(string filter)
    {
        FailsWith(filter, "case-sensitive");
    }

    [Theory]
    [InlineData("Name -xor 'a'")]
    [InlineData("Name -is 'a'")]
    [InlineData("Name -shl 2")]
    public void OtherPowerShellOperators_AreRejected(string filter)
    {
        Fails(filter);
    }

    // ---- variables ----

    [Fact]
    public void UndefinedVariable_IsAnError_NotSilentNull()
    {
        // The core reason the resolver reports Found: GetVariableValue-style null-for-both
        // would turn a typo into '-eq $null' and return the wrong set with no signal.
        FailsWith("Name -eq $nmae", "not defined");
    }

    [Fact]
    public void UndefinedVariable_InsideExpandableString_IsAnError()
    {
        FailsWith("Name -like \"*$nope*\"", "not defined");
    }

    [Fact]
    public void CollectionVariable_IsRejected()
    {
        FailsWith("Name -eq $arr", "collection", Vars(("arr", new object[] { "a", "b" })));
    }

    [Fact]
    public void PSObjectWrappedCollection_IsStillRejected()
    {
        // The wrapper is not IEnumerable itself; without unwrapping this would sail through
        // to a ToString() rendering "System.Object[]".
        var wrapped = PSObject.AsPSObject(new object[] { "a", "b" });
        FailsWith("Name -eq $arr", "collection", Vars(("arr", wrapped)));
    }

    [Fact]
    public void HashtableVariable_IsRejected()
    {
        FailsWith("Name -eq $h", "collection", Vars(("h", new System.Collections.Hashtable())));
    }

    [Fact]
    public void MethodCall_IsRejected()
    {
        FailsWith("Name -eq $s.ToUpper()", "Method calls", Vars(("s", "x")));
    }

    [Fact]
    public void Indexing_IsRejected()
    {
        FailsWith("Name -eq $arr[0]", "Indexing", Vars(("arr", new object[] { "a" })));
    }

    [Fact]
    public void MissingMember_IsAnError()
    {
        FailsWith("Name -eq $u.Missing", "has no property", Vars(("u", new { Present = 1 })));
    }

    [Fact]
    public void MemberAccessOnNull_IsAnError()
    {
        FailsWith("manager -eq $u.DistinguishedName", "is null", Vars(("u", null)));
    }

    [Fact]
    public void SplattedVariable_IsRejected()
    {
        FailsWith("Name -eq @arr", "Splatting", Vars(("arr", new object[] { "a" })));
    }

    // ---- expressions that PowerShell would evaluate but a filter cannot ----

    [Theory]
    [InlineData("Name -eq $(Get-Date)", "Subexpressions")]
    [InlineData("whenCreated -ge (Get-Date)", "not evaluated")]
    [InlineData("whenCreated -ge [datetime]::Now", "looks like an expression")]
    [InlineData("Name -eq @('a','b')", "Arrays are not supported")]
    [InlineData("Name -like \"*$(Get-Date)*\"", "Subexpressions")]
    public void ExpressionsInValuePosition(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Fact]
    public void BacktickEscapes_InExpandableStrings_AreRejected()
    {
        // The splicer works on raw source offsets; .Value has escapes processed, so a raw
        // splice of "`n" would silently emit a literal backtick-n instead of a newline.
        FailsWith("Name -eq \"a`n$v\"", "Escape sequences", Vars(("v", "x")));
    }

    [Fact]
    public void HereStringWithVariables_IsRejected()
    {
        FailsWith("Name -eq @\"\n$v\n\"@", "here-strings", Vars(("v", "x")));
    }

    // ---- value validity ----

    [Theory]
    [InlineData("Name -eq ''", "Empty string")]
    [InlineData("Name -ne ''", "Empty string")]
    [InlineData("Name -like ''", "Empty string")]
    [InlineData("Name -ge ''", "Empty string")]
    public void EmptyStringValues_AreRejectedAsMalformedLdap(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    [InlineData("logonCount -eq 'abc'", "integer")]
    [InlineData("logonCount -eq 2.5", "integer")]
    [InlineData("userAccountControl -band 'x'", "integer")]
    [InlineData("userAccountControl -eq $true", "integer")]
    [InlineData("whenCreated -ge 'not-a-date'", "DateTime")]
    [InlineData("whenCreated -ge $true", "DateTime")]
    [InlineData("SID -eq 'S-bogus'", "not a valid SID")]
    [InlineData("SID -eq 5", "SID")]
    [InlineData("ObjectGUID -eq 'not-a-guid'", "not a valid GUID")]
    [InlineData("isDeleted -eq 'maybe'", "boolean")]
    [InlineData("userCertificate -eq 'abc'", "binary attribute")]
    [InlineData("nTSecurityDescriptor -eq 'abc'", "binary attribute")]
    public void TypeMismatchedValues_AreRejectedBySyntax(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Fact]
    public void DateTimeOnStringAttribute_IsRejected_NotToStringed()
    {
        // The no-ToString()-fallback rule: whatever DateTime.ToString() would produce is not
        // what the directory stores in a string attribute.
        FailsWith("Name -eq $d", "string attribute", Vars(("d", DateTime.UtcNow)));
    }

    [Fact]
    public void ByteArrayOnStringAttribute_IsRejected()
    {
        FailsWith("Name -eq $b", "string attribute", Vars(("b", new byte[] { 1, 2 })));
    }

    [Theory]
    [InlineData("whenCreated -like '2024*'", "only apply to string attributes")]
    [InlineData("logonCount -like '5*'", "only apply to string attributes")]
    [InlineData("objectGUID -like 'a*'", "only apply to string attributes")]
    [InlineData("Name -like 5", "string pattern")]
    public void LikeOnNonStringAttributes_IsRejected(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    // AD cannot substring-match DN syntax: the query would silently match nothing, so this
    // gets its own explanation rather than the generic non-string message.
    [InlineData("memberOf -like '*Admins*'")]
    [InlineData("MemberOf -like '*Admins*'")]
    [InlineData("manager -like '*Doe*'")]
    [InlineData("Members -like '*x*'")]
    public void LikeOnDnAttributes_IsRejectedWithExplanation(string filter)
    {
        FailsWith(filter, "substring-match");
    }

    [Theory]
    [InlineData("objectSid -ge 'S-1-5-32'", "Ordering comparisons")]
    [InlineData("Deleted -gt $true", "Ordering comparisons")]
    [InlineData("memberOf -ge 'CN=x'", "Ordering comparisons")]
    [InlineData("objectGUID -lt 'x'", "Ordering comparisons")]
    public void OrderingOnUnorderedSyntaxes_IsRejected(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    [InlineData("whenCreated -ge $null", "cannot be used with $null")]
    [InlineData("Name -like $null", "cannot be used with $null")]
    [InlineData("Name -gt $null", "cannot be used with $null")]
    [InlineData("userAccountControl -band $null", "cannot be used with $null")]
    public void NullWithNonEqualityOperators_IsRejected(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Fact]
    public void BitwiseOnKnownNonIntegerAttribute_IsRejected()
    {
        FailsWith("Name -band 2", "integer-syntax");
    }

    // ---- property validation ----

    [Fact]
    public void MisspelledProperty_IsATerminatingError()
    {
        // The classic: 'Deparment'. AD would return success and zero rows.
        FailsWith("Deparment -eq 'Sales'", "not a recognised attribute");
    }

    [Fact]
    public void UnknownProperty_MessageMentionsTheEscapeHatch()
    {
        FailsWith("extensionAttribute7 -eq 'x'", "AllowUnknownProperty");
    }

    [Theory]
    [InlineData("PasswordExpired -eq $true")]
    [InlineData("KerberosEncryptionType -eq 'AES256'")]
    [InlineData("CompoundIdentitySupported -eq $true")]
    [InlineData("PrimaryGroup -eq 'CN=Domain Users,CN=Users,DC=x'")]
    [InlineData("IPv4Address -eq '10.0.0.1'")]
    [InlineData("IPv6Address -eq '::1'")]
    [InlineData("ProtectedFromAccidentalDeletion -eq $true")]
    [InlineData("PrincipalsAllowedToDelegateToAccount -eq 'CN=x'")]
    public void UnfilterableSyntheticProperties_AreRejectedExplicitly(string filter)
    {
        // Declared unsupported rather than silently null-matching: each needs data a filter
        // cannot reach (security descriptor, bind response, DNS, domain-SID join).
        FailsWith(filter, "cannot be used in '-Filter'");
    }

    // ---- synthetic property misuse ----

    [Theory]
    [InlineData("Enabled -like 'tr*'", "only supports -eq and -ne")]
    [InlineData("Enabled -gt $true", "only supports -eq and -ne")]
    [InlineData("GroupScope -like 'G*'", "only supports -eq and -ne")]
    [InlineData("LockedOut -ge $true", "only supports -eq and -ne")]
    public void SyntheticProperties_OnlySupportEquality(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    [InlineData("Enabled -eq 'yes'", "$true or $false")]
    [InlineData("Enabled -eq 5", "$true or $false")]
    [InlineData("Enabled -eq $null", "cannot be compared to $null")]
    [InlineData("GroupScope -eq $true", "requires a string")]
    public void SyntheticProperties_ValueValidation(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    [Theory]
    [InlineData("GroupScope -eq 'Mega'", "must be one of")]
    [InlineData("GroupCategory -eq 'Other'", "'Security' or 'Distribution'")]
    public void GroupEnums_RejectUnknownNames(string filter, string messagePart)
    {
        FailsWith(filter, messagePart);
    }

    // ---- -recursivematch restrictions ----

    [Theory]
    [InlineData("Name -recursivematch 'CN=x,DC=y'")]
    [InlineData("manager -recursivematch 'CN=x,DC=y'")]
    public void RecursiveMatch_OnlyOnMemberAndMemberOf(string filter)
    {
        FailsWith(filter, "only applies to 'member' and 'memberOf'");
    }

    [Fact]
    public void RecursiveMatch_NeedsADnString()
    {
        FailsWith("memberOf -recursivematch 5", "distinguished name");
    }

    // ---- Interval attributes: explicitly not filterable ----

    [Theory]
    // Both spellings, both operator families. The trap being guarded: a new enum member
    // falling through to the String default arm would marshal a TimeSpan as text and
    // successfully match zero rows.
    [InlineData("maxPwdAge -eq 0")]
    [InlineData("MaxPasswordAge -eq '42.00:00:00'")]
    [InlineData("maxPwdAge -ge 5")]
    [InlineData("lockoutDuration -lt 5")]
    [InlineData("MinPasswordAge -ne 0")]
    public void IntervalAttributes_AreRejectedInFilters(string filter)
    {
        FailsWith(filter, "interval-valued");
    }

    [Fact]
    public void IntervalAttributes_LikeGetsTheStandardNonStringRejection()
    {
        FailsWith("maxPwdAge -like '4*'", "Interval-valued");
    }
}
