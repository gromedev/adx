using System.Management.Automation;
using ADx.Cmdlets.Filter;
using ADx.Engine.Filter;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M2 golden cases: '-Filter' text in, LDAP filter text out. Runs the internal
/// <see cref="AdFilterTranslator"/> directly (InternalsVisibleTo) with an injected variable
/// resolver -- no runspace, no cmdlet, no DC.
/// <para>
/// Where a case exists in both tokenizer encodings (unparenthesized command mode vs
/// parenthesized expression mode), both are asserted: the tokenizer emits *different token
/// kinds* for the same operator in the two modes, and a translator handling only one silently
/// fails on the other. See the plan's "the PowerShell tokenizer is mode-sensitive" finding.
/// </para>
/// </summary>
public class AdFilterTranslatorTests
{
    private static readonly Func<string, (bool Found, object? Value)> NoVariables =
        _ => (false, null);

    private static Func<string, (bool Found, object? Value)> Vars(params (string Name, object? Value)[] variables)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in variables) map[name] = value;
        return name => map.TryGetValue(name, out var v) ? (true, v) : (false, null);
    }

    private static string T(string filter) =>
        AdFilterEmitter.Emit(AdFilterTranslator.Translate(filter, NoVariables)!);

    private static string T(string filter, Func<string, (bool Found, object? Value)> resolver) =>
        AdFilterEmitter.Emit(AdFilterTranslator.Translate(filter, resolver)!);

    // ---- every operator, both tokenizer encodings ----

    [Theory]
    // -eq
    [InlineData("Name -eq 'jdoe'", "(name=jdoe)")]
    [InlineData("(Name -eq 'jdoe')", "(name=jdoe)")]
    [InlineData("Name -eq \"jdoe\"", "(name=jdoe)")]
    [InlineData("Name -eq jdoe", "(name=jdoe)")]
    // -ne
    [InlineData("Name -ne 'jdoe'", "(!(name=jdoe))")]
    [InlineData("(Name -ne 'jdoe')", "(!(name=jdoe))")]
    // -like / -notlike
    [InlineData("Name -like 'j*'", "(name=j*)")]
    [InlineData("(Name -like 'j*')", "(name=j*)")]
    [InlineData("Name -notlike 'j*'", "(!(name=j*))")]
    [InlineData("(Name -notlike 'j*')", "(!(name=j*))")]
    [InlineData("Name -like '*'", "(name=*)")]
    // Explicitly case-insensitive spellings. Parenthesized, the tokenizer folds these onto
    // the same TokenKinds as the bare forms (-ieq IS TokenKind.Ieq); unparenthesized they
    // arrive as a ParameterToken named "ieq". Both must mean the same thing -- accepting one
    // encoding and rejecting the other is the dual-encoding trap in miniature.
    [InlineData("Name -ieq 'jdoe'", "(name=jdoe)")]
    [InlineData("(Name -ieq 'jdoe')", "(name=jdoe)")]
    [InlineData("Name -ine 'jdoe'", "(!(name=jdoe))")]
    [InlineData("(Name -ine 'jdoe')", "(!(name=jdoe))")]
    [InlineData("Name -ilike 'j*'", "(name=j*)")]
    [InlineData("(Name -ilike 'j*')", "(name=j*)")]
    [InlineData("Name -inotlike 'j*'", "(!(name=j*))")]
    [InlineData("(Name -inotlike 'j*')", "(!(name=j*))")]
    [InlineData("logonCount -ige 5", "(logonCount>=5)")]
    [InlineData("(logonCount -ige 5)", "(logonCount>=5)")]
    [InlineData("logonCount -ilt 5", "(&(logonCount<=5)(!(logonCount=5)))")]
    [InlineData("(logonCount -ilt 5)", "(&(logonCount<=5)(!(logonCount=5)))")]
    // ordering on an integer attribute
    [InlineData("logonCount -ge 5", "(logonCount>=5)")]
    [InlineData("(logonCount -ge 5)", "(logonCount>=5)")]
    [InlineData("logonCount -gt 5", "(&(logonCount>=5)(!(logonCount=5)))")]
    [InlineData("(logonCount -gt 5)", "(&(logonCount>=5)(!(logonCount=5)))")]
    [InlineData("logonCount -le 5", "(logonCount<=5)")]
    [InlineData("(logonCount -le 5)", "(logonCount<=5)")]
    [InlineData("logonCount -lt 5", "(&(logonCount<=5)(!(logonCount=5)))")]
    [InlineData("(logonCount -lt 5)", "(&(logonCount<=5)(!(logonCount=5)))")]
    // bitwise matching rules
    [InlineData("userAccountControl -band 2", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    [InlineData("(userAccountControl -band 2)", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    [InlineData("userAccountControl -bor 2", "(userAccountControl:1.2.840.113556.1.4.804:=2)")]
    [InlineData("(userAccountControl -bor 2)", "(userAccountControl:1.2.840.113556.1.4.804:=2)")]
    // hex literals must reach the marshaller as numbers, not the text "0x2"
    [InlineData("userAccountControl -band 0x2", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    [InlineData("userAccountControl -band 0x10000", "(userAccountControl:1.2.840.113556.1.4.803:=65536)")]
    // transitive membership
    [InlineData("memberOf -recursivematch 'CN=Admins,OU=G,DC=corp,DC=com'",
        "(memberOf:1.2.840.113556.1.4.1941:=CN=Admins,OU=G,DC=corp,DC=com)")]
    [InlineData("MemberOf -RecursiveMatch 'CN=A,DC=x'", "(memberOf:1.2.840.113556.1.4.1941:=CN=A,DC=x)")]
    [InlineData("member -recursivematch 'CN=U,DC=x'", "(member:1.2.840.113556.1.4.1941:=CN=U,DC=x)")]
    public void Operators_BothEncodings(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    // ---- logical composition, both encodings ----

    [Theory]
    [InlineData("Name -eq 'a' -and Title -eq 'b'", "(&(name=a)(title=b))")]
    [InlineData("(Name -eq 'a') -and (Title -eq 'b')", "(&(name=a)(title=b))")]
    [InlineData("Name -eq 'a' -or Title -eq 'b'", "(|(name=a)(title=b))")]
    [InlineData("(Name -eq 'a') -or (Title -eq 'b')", "(|(name=a)(title=b))")]
    [InlineData("-not (Name -eq 'a')", "(!(name=a))")]
    [InlineData("!(Name -eq 'a')", "(!(name=a))")]
    [InlineData("Name -eq 'a' -and -not (Title -eq 'b')", "(&(name=a)(!(title=b)))")]
    [InlineData("(Name -eq 'a') -and !(Title -eq 'b')", "(&(name=a)(!(title=b)))")]
    // n-ary chains flatten instead of nesting pairwise
    [InlineData("Name -eq 'a' -and Title -eq 'b' -and mail -eq 'c'", "(&(name=a)(title=b)(mail=c))")]
    [InlineData("Name -eq 'a' -or Title -eq 'b' -or mail -eq 'c'", "(|(name=a)(title=b)(mail=c))")]
    // double negation stays structural, no simplification
    [InlineData("-not (-not (Name -eq 'a'))", "(!(!(name=a)))")]
    public void LogicalOperators_BothEncodings(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    // ---- precedence: AND binds tighter than OR ----

    [Theory]
    // the plan's own golden precedence example
    [InlineData("Name -eq '1' -or Title -eq '2' -and mail -eq '3'", "(|(name=1)(&(title=2)(mail=3)))")]
    [InlineData("Name -eq '1' -and Title -eq '2' -or mail -eq '3'", "(|(&(name=1)(title=2))(mail=3))")]
    // parentheses override
    [InlineData("(Name -eq '1' -or Title -eq '2') -and mail -eq '3'", "(&(|(name=1)(title=2))(mail=3))")]
    [InlineData("Name -eq '1' -and (Title -eq '2' -or mail -eq '3')", "(&(name=1)(|(title=2)(mail=3)))")]
    // -not binds tighter than -and
    [InlineData("-not (Name -eq '1') -and Title -eq '2'", "(&(!(name=1))(title=2))")]
    public void Precedence(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    // ---- the -eq vs -like escaping divergence ----

    [Theory]
    [InlineData("Name -like 'j*'", "(name=j*)")]
    [InlineData("Name -ilike 'j*'", "(name=j*)")]
    // RFC 4515 specials in exact values
    [InlineData("Name -eq 'a(b)c'", "(name=a\\28b\\29c)")]
    [InlineData("Name -eq 'a\\b'", "(name=a\\5cb)")]
    [InlineData("Name -eq 'a/b'", "(name=a\\2fb)")]
    // patterns escape everything EXCEPT the wildcards
    [InlineData("Name -like '*(x)*'", "(name=*\\28x\\29*)")]
    [InlineData("Name -like '*a\\b*'", "(name=*a\\5cb*)")]
    // a DN value with an escaped comma: the backslash itself must be escaped for the filter
    [InlineData("manager -eq 'CN=Doe\\, John,OU=Users,DC=x'", "(manager=CN=Doe\\5c, John,OU=Users,DC=x)")]
    public void EscapingDivergence(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    // ---- '*' in an exact-match value: the RSAT-vs-PowerShell semantic fork, refused loudly ----

    [Theory]
    // THE case that inverts a drop-in script's result set: RSAT's "mail absent" idiom.
    [InlineData("mail -ne '*'")]
    [InlineData("Name -eq 'j*'")]
    // The i-prefixed spelling must route through the same rejection.
    [InlineData("Name -ieq 'j*'")]
    // Both tokenizer encodings.
    [InlineData("(Name -eq 'j*')")]
    public void WildcardInExactValue_IsATerminatingError(string filter)
    {
        var ex = Assert.Throws<AdFilterTranslationException>(() => T(filter));
        Assert.Contains("wildcard", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-like", ex.Message);
    }

    [Fact]
    public void WildcardInExactValue_FromAVariable_IsATerminatingError()
    {
        var resolver = Vars(("v", "*)(uid=*"));
        var ex = Assert.Throws<AdFilterTranslationException>(() => T("Description -eq $v", resolver));
        Assert.Contains("-like", ex.Message);
    }

    [Fact]
    public void InjectionCharactersInVariableValue_AreEscaped()
    {
        var resolver = Vars(("v", ")(uid=x"));
        Assert.Equal("(description=\\29\\28uid=x)", T("Description -eq $v", resolver));
    }

    // ---- $null: presence and absence ----

    [Theory]
    [InlineData("mail -eq $null", "(!(mail=*))")]
    [InlineData("mail -ne $null", "(mail=*)")]
    [InlineData("(mail -eq $null)", "(!(mail=*))")]
    public void NullComparisons(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Fact]
    public void VariableHoldingNull_BehavesLikeNullLiteral()
    {
        Assert.Equal("(!(mail=*))", T("mail -eq $x", Vars(("x", null))));
    }

    // ---- typed values by syntax ----

    [Theory]
    // Boolean syntax renders LDAP TRUE/FALSE
    [InlineData("Deleted -eq $true", "(isDeleted=TRUE)")]
    [InlineData("isDeleted -eq $false", "(isDeleted=FALSE)")]
    [InlineData("Deleted -eq 'true'", "(isDeleted=TRUE)")]
    // Integer syntax
    [InlineData("logonCount -eq 5", "(logonCount=5)")]
    [InlineData("logonCount -eq '5'", "(logonCount=5)")]
    [InlineData("primaryGroupID -eq 513", "(primaryGroupID=513)")]
    [InlineData("msDS-SupportedEncryptionTypes -eq 24", "(msDS-SupportedEncryptionTypes=24)")]
    // FileTime accepts raw integers: the documented pwdLastSet=0 / accountExpires=0 queries
    [InlineData("pwdLastSet -eq 0", "(pwdLastSet=0)")]
    [InlineData("accountExpires -eq 0", "(accountExpires=0)")]
    [InlineData("PasswordLastSet -eq 0", "(pwdLastSet=0)")]
    // String syntax accepts numbers by explicit conversion
    [InlineData("Name -eq 1", "(name=1)")]
    [InlineData("employeeID -eq 12345", "(employeeID=12345)")]
    // negative number arrives as a Generic bareword, not a NumberToken
    [InlineData("employeeID -eq -5", "(employeeID=-5)")]
    public void TypedValues(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Fact]
    public void DateTimeVariable_GeneralizedTimeAttribute()
    {
        var d = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal("(whenCreated>=20240102030405.0Z)", T("whenCreated -ge $d", Vars(("d", d))));
    }

    [Fact]
    public void DateTimeVariable_AliasResolvesAndMarshalsAsGeneralizedTime()
    {
        var d = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal(
            "(&(whenCreated<=20240102030405.0Z)(!(whenCreated=20240102030405.0Z)))",
            T("Created -lt $d", Vars(("d", d))));
    }

    [Fact]
    public void DateTimeVariable_FileTimeAttribute()
    {
        var d = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var fileTime = d.ToFileTimeUtc();
        Assert.Equal($"(&(pwdLastSet<={fileTime})(!(pwdLastSet={fileTime})))",
            T("PasswordLastSet -lt $d", Vars(("d", d))));
    }

    [Fact]
    public void DateTimeVariable_LastLogonDate_MapsToLastLogonTimestamp()
    {
        var d = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileTime = d.ToFileTimeUtc();
        Assert.Equal($"(&(lastLogonTimestamp<={fileTime})(!(lastLogonTimestamp={fileTime})))",
            T("LastLogonDate -lt $d", Vars(("d", d))));
    }

    [Fact]
    public void DateTimeOffsetVariable_IsAccepted()
    {
        var dto = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal("(whenCreated>=20240102030405.0Z)", T("whenCreated -ge $dto", Vars(("dto", dto))));
    }

    [Fact]
    public void DateStringValue_ParsesInvariantAndMarshalsBySyntax()
    {
        // A bare date string is interpreted as LOCAL wall-clock time (matching RSAT), then
        // rendered in UTC, so the emitted digits depend on the host timezone. Compute the
        // expectation the same way rather than hardcoding a UTC offset -- the deterministic
        // assertions above (explicit DateTimeKind.Utc variables) carry the timezone-free weight.
        var local = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var expected = $"(whenCreated>={local.ToUniversalTime():yyyyMMddHHmmss}.0Z)";
        Assert.Equal(expected, T("whenCreated -ge '2024-01-02 03:04:05'"));
    }

    // ---- DateTime Kind and precision: the silent-skew traps ----

    [Fact]
    public void UnspecifiedKindDateTimeVariable_MarshalsExactlyLikeTheIdenticalDateString()
    {
        // PowerShell's [datetime]'...' cast yields Kind=Unspecified. It must follow the same
        // AssumeLocal rule as a date string -- treating it as UTC made the variable and string
        // spellings of one timestamp differ by the host's offset, silently.
        var unspecified = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        Assert.Equal(
            T("whenCreated -ge '2024-01-02 03:04:05'"),
            T("whenCreated -ge $d", Vars(("d", unspecified))));
    }

    [Fact]
    public void UnspecifiedKindDateTime_FileTimeAttribute_MatchesTheLocalKindRendering()
    {
        var local = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        Assert.Equal(
            T("PasswordLastSet -ge $d", Vars(("d", local))),
            T("PasswordLastSet -ge $d", Vars(("d", unspecified))));
    }

    [Theory]
    // Lower bounds round UP with the strictness dropped -- for whole-second stored values,
    // T >= d and T > d both hold exactly when T >= ceil(d). Emitting the truncated value
    // would wrongly include entries stamped at exactly the truncated second.
    [InlineData("ge", "(whenCreated>=20240102030406.0Z)")]
    [InlineData("gt", "(whenCreated>=20240102030406.0Z)")]
    // Upper bounds round DOWN: T <= d and T < d both hold exactly when T <= floor(d).
    [InlineData("le", "(whenCreated<=20240102030405.0Z)")]
    [InlineData("lt", "(whenCreated<=20240102030405.0Z)")]
    public void SubSecondGeneralizedTimeBound_RoundsDirectionAwareToAnExactBound(string op, string expected)
    {
        var d = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddMilliseconds(789);
        Assert.Equal(expected, T($"whenCreated -{op} $d", Vars(("d", d))));
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    public void SubSecondGeneralizedTimeEquality_IsATerminatingError(string op)
    {
        var d = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddMilliseconds(789);
        var ex = Assert.Throws<AdFilterTranslationException>(
            () => T($"whenCreated -{op} $d", Vars(("d", d))));
        Assert.Contains("whole second", ex.Message);
    }

    [Fact]
    public void SubSecondFileTimeBound_KeepsFullTickPrecision()
    {
        // FILETIME has 100ns resolution: sub-second bounds are faithful as-is, no rounding.
        var d = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1_234_567);
        Assert.Equal($"(pwdLastSet>={d.ToFileTimeUtc()})", T("PasswordLastSet -ge $d", Vars(("d", d))));
    }

    [Fact]
    public void Pre1601FileTimeBound_IsACleanTranslationError()
    {
        var ex = Assert.Throws<AdFilterTranslationException>(
            () => T("PasswordLastSet -lt $d", Vars(("d", DateTime.MinValue))));
        Assert.Contains("1601", ex.Message);
    }

    // ---- -approx: RSAT grammar, LDAP '~=' ----

    [Theory]
    [InlineData("Name -approx 'jdoe'", "(name~=jdoe)")]
    [InlineData("(Name -approx 'jdoe')", "(name~=jdoe)")]
    public void ApproxOperator_EmitsApproximateMatch(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Fact]
    public void ApproxOperator_OnGeneralizedTime_SharesTheSubSecondDoctrine()
    {
        // AD evaluates '~=' as plain equality, so approx must not slip past the
        // whole-second gate that -eq is held to: whole seconds emit, fractions refuse.
        var whole = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.Equal("(whenCreated~=20240102030405.0Z)", T("whenCreated -approx $d", Vars(("d", whole))));

        var fractional = whole.AddMilliseconds(789);
        Assert.Throws<AdFilterTranslationException>(
            () => T("whenCreated -approx $d", Vars(("d", fractional))));
    }

    [Fact]
    public void SubSecondLowerBoundInTheFinalRepresentableSecond_IsACleanTranslationError()
    {
        // ceil() of a fraction inside DateTime's last second cannot be represented;
        // AddSeconds(1) would throw a raw ArgumentOutOfRangeException that escapes the
        // translation-error contract -- the GeneralizedTime twin of the pre-1601 case.
        var ex = Assert.Throws<AdFilterTranslationException>(
            () => T("whenCreated -ge $d", Vars(("d", DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)))));
        Assert.Contains("final second", ex.Message);
    }

    // ---- underscore attribute names (legal in ldapDisplayName, common in HR-sync schemas) ----

    [Fact]
    public void UnderscoreAttributeName_IsFilterableUnderAllowUnknownProperty()
    {
        var node = AdFilterTranslator.Translate("hr_employee_type -eq 'FTE'", NoVariables, allowUnknownProperty: true)!;
        Assert.Equal("(hr_employee_type=FTE)", AdFilterEmitter.Emit(node));
    }

    // ---- GroupCategory -ne: single negation, not a stacked double-not ----

    [Fact]
    public void GroupCategoryNotEquals_UnwrapsTheAlreadyNegatedNode()
    {
        Assert.Equal("(groupType:1.2.840.113556.1.4.803:=2147483648)", T("GroupCategory -ne 'Distribution'"));
        Assert.Equal("(!(groupType:1.2.840.113556.1.4.803:=2147483648))", T("GroupCategory -ne 'Security'"));
    }

    // ---- -RecursiveMatch on DN-valued attributes beyond member/memberOf ----

    [Fact]
    public void RecursiveMatch_OnManagerChain_Emits1941()
    {
        Assert.Equal(
            "(manager:1.2.840.113556.1.4.1941:=CN=Boss,OU=x,DC=y)",
            T("manager -recursivematch 'CN=Boss,OU=x,DC=y'"));
    }

    // ---- SID and GUID binary escaping ----

    [Theory]
    [InlineData("SID -eq 'S-1-5-32-544'",
        "(objectSid=\\01\\02\\00\\00\\00\\00\\00\\05\\20\\00\\00\\00\\20\\02\\00\\00)")]
    [InlineData("ObjectSid -eq 'S-1-5-32-544'",
        "(objectSid=\\01\\02\\00\\00\\00\\00\\00\\05\\20\\00\\00\\00\\20\\02\\00\\00)")]
    public void SidValues_EscapeAsBinary(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Fact]
    public void ADxSecurityIdentifierVariable_IsAcceptedViaItsValueProperty()
    {
        var sid = new ADxSecurityIdentifier("S-1-5-32-544");
        Assert.Equal(
            "(objectSid=\\01\\02\\00\\00\\00\\00\\00\\05\\20\\00\\00\\00\\20\\02\\00\\00)",
            T("SID -eq $sid", Vars(("sid", sid))));
    }

    [Fact]
    public void SidByteArrayVariable_IsAcceptedDirectly()
    {
        var bytes = LdapConvert.SddlToSid("S-1-5-32-544")!;
        Assert.Equal(
            "(objectSid=\\01\\02\\00\\00\\00\\00\\00\\05\\20\\00\\00\\00\\20\\02\\00\\00)",
            T("objectSid -eq $b", Vars(("b", bytes))));
    }

    [Theory]
    [InlineData("ObjectGUID -eq '01234567-89ab-cdef-0123-456789abcdef'",
        "(objectGUID=\\67\\45\\23\\01\\ab\\89\\ef\\cd\\01\\23\\45\\67\\89\\ab\\cd\\ef)")]
    public void GuidStringValue_EscapesAsBinaryInGuidByteOrder(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Fact]
    public void GuidVariable_IsAccepted()
    {
        var g = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        Assert.Equal(
            "(objectGUID=\\67\\45\\23\\01\\ab\\89\\ef\\cd\\01\\23\\45\\67\\89\\ab\\cd\\ef)",
            T("objectGUID -eq $g", Vars(("g", g))));
    }

    // ---- synthetic properties ----

    [Theory]
    // Enabled is inverted from the underlying ACCOUNTDISABLE bit
    [InlineData("Enabled -eq $true", "(!(userAccountControl:1.2.840.113556.1.4.803:=2))")]
    [InlineData("Enabled -eq $false", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    [InlineData("Enabled -ne $true", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    [InlineData("Enabled -ne $false", "(!(userAccountControl:1.2.840.113556.1.4.803:=2))")]
    // the string spellings that real scripts use
    [InlineData("Enabled -eq 'true'", "(!(userAccountControl:1.2.840.113556.1.4.803:=2))")]
    [InlineData("Enabled -eq 'False'", "(userAccountControl:1.2.840.113556.1.4.803:=2)")]
    // straight UAC bits
    [InlineData("PasswordNeverExpires -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=65536)")]
    [InlineData("PasswordNotRequired -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=32)")]
    [InlineData("SmartcardLogonRequired -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=262144)")]
    [InlineData("TrustedForDelegation -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=524288)")]
    [InlineData("TrustedToAuthForDelegation -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=16777216)")]
    [InlineData("AccountNotDelegated -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=1048576)")]
    [InlineData("DoesNotRequirePreAuth -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=4194304)")]
    [InlineData("AllowReversiblePasswordEncryption -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=128)")]
    [InlineData("UseDESKeyOnly -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=2097152)")]
    [InlineData("HomedirRequired -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=8)")]
    [InlineData("MNSLogonAccount -eq $true", "(userAccountControl:1.2.840.113556.1.4.803:=131072)")]
    [InlineData("PasswordNeverExpires -eq $false", "(!(userAccountControl:1.2.840.113556.1.4.803:=65536))")]
    // LockedOut is a lockoutTime threshold, not a UAC bit
    [InlineData("LockedOut -eq $true", "(lockoutTime>=1)")]
    [InlineData("LockedOut -eq $false", "(!(lockoutTime>=1))")]
    public void BooleanSyntheticProperties(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Theory]
    [InlineData("GroupScope -eq 'BuiltinLocal'", "(groupType:1.2.840.113556.1.4.803:=1)")]
    [InlineData("GroupScope -eq 'Global'", "(groupType:1.2.840.113556.1.4.803:=2)")]
    [InlineData("GroupScope -eq 'DomainLocal'", "(groupType:1.2.840.113556.1.4.803:=4)")]
    [InlineData("GroupScope -eq 'Universal'", "(groupType:1.2.840.113556.1.4.803:=8)")]
    [InlineData("GroupScope -ne 'Global'", "(!(groupType:1.2.840.113556.1.4.803:=2))")]
    // the security bit is the sign bit; its filter form is the decimal 2147483648
    [InlineData("GroupCategory -eq 'Security'", "(groupType:1.2.840.113556.1.4.803:=2147483648)")]
    [InlineData("GroupCategory -eq 'Distribution'", "(!(groupType:1.2.840.113556.1.4.803:=2147483648))")]
    [InlineData("GroupCategory -ne 'Security'", "(!(groupType:1.2.840.113556.1.4.803:=2147483648))")]
    public void GroupSyntheticProperties(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    [Fact]
    public void SyntheticProperty_ComposesWithRealAttributes()
    {
        Assert.Equal(
            "(&(!(userAccountControl:1.2.840.113556.1.4.803:=2))(sn=Doe))",
            T("Enabled -eq $true -and Surname -eq 'Doe'"));
    }

    // ---- RSAT aliases and canonical casing ----

    [Theory]
    [InlineData("EmailAddress -eq 'a@b.c'", "(mail=a@b.c)")]
    [InlineData("mail -eq 'a@b.c'", "(mail=a@b.c)")]
    [InlineData("Surname -eq 'Doe'", "(sn=Doe)")]
    [InlineData("sn -eq 'Doe'", "(sn=Doe)")]
    [InlineData("City -eq 'Oslo'", "(l=Oslo)")]
    [InlineData("State -eq 'ON'", "(st=ON)")]
    [InlineData("Country -eq 'NO'", "(c=NO)")]
    [InlineData("Office -like 'B*'", "(physicalDeliveryOfficeName=B*)")]
    [InlineData("OfficePhone -eq '555'", "(telephoneNumber=555)")]
    [InlineData("Members -recursivematch 'CN=U,DC=x'", "(member:1.2.840.113556.1.4.1941:=CN=U,DC=x)")]
    // canonical LDAP casing comes from the schema regardless of input casing
    [InlineData("SamAccountName -eq 'jdoe'", "(sAMAccountName=jdoe)")]
    [InlineData("samaccountname -eq 'jdoe'", "(sAMAccountName=jdoe)")]
    [InlineData("SAMACCOUNTNAME -eq 'jdoe'", "(sAMAccountName=jdoe)")]
    [InlineData("NAME -eq 'x'", "(name=x)")]
    [InlineData("userprincipalname -like '*@corp.com'", "(userPrincipalName=*@corp.com)")]
    public void RsatAliasesAndCasing(string filter, string expected)
    {
        Assert.Equal(expected, T(filter));
    }

    // ---- variables ----

    [Fact]
    public void Variable_ResolvesThroughTheDelegate()
    {
        Assert.Equal("(name=jdoe)", T("Name -eq $name", Vars(("name", "jdoe"))));
    }

    [Fact]
    public void BracedVariable_Resolves()
    {
        Assert.Equal("(name=jdoe)", T("Name -eq ${name}", Vars(("name", "jdoe"))));
    }

    [Fact]
    public void VariableValue_EscapingStillFollowsTheOperator()
    {
        // The escaping decision tracks the operator even when the value came from a variable:
        // parens escape under -eq, survive as literals-to-escape under -like too, while the
        // -like wildcard passes through and the same value under -eq is refused.
        Assert.Equal("(name=j\\281\\29)", T("Name -eq $v", Vars(("v", "j(1)"))));
        var wildcard = Vars(("pattern", "j*"));
        Assert.Equal("(name=j*)", T("Name -like $pattern", wildcard));
        Assert.Throws<AdFilterTranslationException>(() => T("Name -eq $pattern", wildcard));
    }

    [Fact]
    public void PSObjectWrappedVariable_IsUnwrapped()
    {
        // Anything that crossed the pipeline arrives PSObject-wrapped; the marshaller must
        // see the underlying value, not the wrapper.
        var wrapped = PSObject.AsPSObject("jdoe");
        Assert.Equal("(name=jdoe)", T("Name -eq $v", Vars(("v", wrapped))));
    }

    [Fact]
    public void MemberAccess_OnPSObjectNoteProperties()
    {
        var user = new PSObject();
        user.Properties.Add(new PSNoteProperty("DistinguishedName", "CN=Boss,OU=X,DC=corp,DC=com"));

        Assert.Equal("(manager=CN=Boss,OU=X,DC=corp,DC=com)",
            T("manager -eq $u.DistinguishedName", Vars(("u", user))));
    }

    [Fact]
    public void MemberAccess_OnClrObjectProperties()
    {
        var user = new { DistinguishedName = "CN=Boss,DC=x" };
        Assert.Equal("(manager=CN=Boss,DC=x)", T("manager -eq $u.DistinguishedName", Vars(("u", user))));
    }

    [Fact]
    public void MemberAccess_Chained()
    {
        var wrapper = new { Inner = new { Name = "jdoe" } };
        Assert.Equal("(name=jdoe)", T("Name -eq $w.Inner.Name", Vars(("w", wrapper))));
    }

    [Fact]
    public void StructuralTrueFalseNull_CannotBeShadowedByUserVariables()
    {
        // Even with variables literally named true/false/null in scope, $true/$false/$null
        // resolve structurally from the VariablePath, never through the resolver.
        var resolver = Vars(("true", "HACKED"), ("false", "HACKED"), ("null", "HACKED"));
        Assert.Equal("(isDeleted=TRUE)", T("Deleted -eq $true", resolver));
        Assert.Equal("(isDeleted=FALSE)", T("Deleted -eq $false", resolver));
        Assert.Equal("(!(mail=*))", T("mail -eq $null", resolver));
    }

    // ---- expandable strings ----

    [Fact]
    public void ExpandableString_SingleVariable()
    {
        Assert.Equal("(description=*Sales*)", T("Description -like \"*$dept*\"", Vars(("dept", "Sales"))));
    }

    [Fact]
    public void ExpandableString_MultipleVariables()
    {
        Assert.Equal("(name=x-y)", T("Name -eq \"$a-$b\"", Vars(("a", "x"), ("b", "y"))));
    }

    [Fact]
    public void ExpandableString_VariableAtStartAndEnd()
    {
        var resolver = Vars(("a", "x"));
        Assert.Equal("(name=x suffix)", T("Name -eq \"$a suffix\"", resolver));
        Assert.Equal("(name=prefix x)", T("Name -eq \"prefix $a\"", resolver));
    }

    [Fact]
    public void ExpandableString_DollarTrueExpandsLikePowerShell()
    {
        Assert.Equal("(description=True)", T("Description -eq \"$true\""));
    }

    [Fact]
    public void ExpandableString_NumberVariableRendersInvariant()
    {
        Assert.Equal("(name=v5)", T("Name -eq \"v$n\"", Vars(("n", 5))));
    }

    [Fact]
    public void ExpandableString_WildcardsFromTheStringSurviveInPatterns()
    {
        // The '*' around the variable are the user's wildcards in a -like: they must reach
        // the pattern escaper, while injection characters IN the value must still be escaped.
        var resolver = Vars(("dept", "Sa(l)es"));
        Assert.Equal("(description=*Sa\\28l\\29es*)", T("Description -like \"*$dept*\"", resolver));
    }

    // ---- match-all ----

    [Fact]
    public void BareStar_TranslatesToNull_MeaningNoConstraint()
    {
        Assert.Null(AdFilterTranslator.Translate("*", NoVariables));
    }

    // ---- multi-line (ScriptBlock-shaped) filters ----

    [Fact]
    public void MultiLineFilter_NewlinesAreSkipped()
    {
        Assert.Equal("(&(name=a)(title=b))", T("Name -eq 'a' -and\nTitle -eq 'b'"));
    }

    [Fact]
    public void MultiLineFilter_ExpressionModeAcrossLines()
    {
        Assert.Equal("(&(name=a)(title=b))", T("(Name -eq 'a') -and\n(Title -eq 'b')"));
    }

    // ---- unknown properties and the escape hatch ----

    [Fact]
    public void UnknownProperty_WithAllowUnknown_PassesThroughVerbatim()
    {
        var node = AdFilterTranslator.Translate(
            "extensionAttribute7 -eq 'x'", NoVariables, allowUnknownProperty: true);
        Assert.Equal("(extensionAttribute7=x)", AdFilterEmitter.Emit(node!));
    }

    [Fact]
    public void UnknownProperty_WithAllowUnknown_LikeUsesPatternEscaping()
    {
        var node = AdFilterTranslator.Translate(
            "extensionAttribute7 -like 'x*'", NoVariables, allowUnknownProperty: true);
        Assert.Equal("(extensionAttribute7=x*)", AdFilterEmitter.Emit(node!));
    }

    [Fact]
    public void UnknownProperty_WithAllowUnknown_BitwiseIsTrusted()
    {
        var node = AdFilterTranslator.Translate(
            "myCustomFlags -band 4", NoVariables, allowUnknownProperty: true);
        Assert.Equal("(myCustomFlags:1.2.840.113556.1.4.803:=4)", AdFilterEmitter.Emit(node!));
    }

    [Fact]
    public void KeywordShapedTokenKinds_AreValidPropertyNames_WithAllowUnknown()
    {
        // 'in' and 'default' tokenize as TokenKind.In / TokenKind.Default (not as errors) --
        // the property matcher works off token text, never kind, so they behave like any
        // other unknown attribute.
        var inNode = AdFilterTranslator.Translate("in -eq 'a'", NoVariables, allowUnknownProperty: true);
        Assert.Equal("(in=a)", AdFilterEmitter.Emit(inNode!));

        var defaultNode = AdFilterTranslator.Translate("default -eq 'a'", NoVariables, allowUnknownProperty: true);
        Assert.Equal("(default=a)", AdFilterEmitter.Emit(defaultNode!));
    }

    // ---- 0.4: per-schema AttributeOverrides in -Filter ----
    //
    // The real schema dictionaries are used, not copies: the point is that -Filter and the
    // projector resolve a property THROUGH THE SAME MAPPING. Before 0.4 the filter path
    // ignored overrides entirely: a PSO's MinPasswordLength emitted the domain-head
    // minPwdLength (absent on PSOs -- silent zero rows) and Precedence threw "not recognised".

    private static string TFgpp(string filter) =>
        AdFilterEmitter.Emit(AdFilterTranslator.Translate(
            filter, NoVariables,
            attributeOverrides: AdObjectSchema.FineGrainedPasswordPolicy.AttributeOverrides)!);

    [Theory]
    // Integer-valued overrides: Microsoft's own doc example is Precedence -eq 500.
    [InlineData("Precedence -eq 500", "(msDS-PasswordSettingsPrecedence=500)")]
    [InlineData("MinPasswordLength -ge 14", "(msDS-MinimumPasswordLength>=14)")]
    [InlineData("PasswordHistoryCount -eq 24", "(msDS-PasswordHistoryLength=24)")]
    [InlineData("LockoutThreshold -le 5", "(msDS-LockoutThreshold<=5)")]
    // Boolean-valued overrides.
    [InlineData("ComplexityEnabled -eq $true", "(msDS-PasswordComplexityEnabled=TRUE)")]
    [InlineData("ReversibleEncryptionEnabled -eq $false", "(msDS-PasswordReversibleEncryptionEnabled=FALSE)")]
    // Dn-valued override.
    [InlineData("AppliesTo -eq 'CN=Tier0,OU=Groups,DC=corp,DC=com'",
        "(msDS-PSOAppliesTo=CN=Tier0,OU=Groups,DC=corp,DC=com)")]
    public void FgppOverrides_ResolveToTheMsDsAttributes(string filter, string expected)
    {
        Assert.Equal(expected, TFgpp(filter));
    }

    [Theory]
    // Interval-valued overrides: durations are deliberately unfilterable, but the error must
    // be the loud interval refusal naming the property -- resolved through the override --
    // not "not recognised" and never the pre-0.4 silent zero rows via the domain-head name.
    [InlineData("MinPasswordAge -ge 1")]
    [InlineData("MaxPasswordAge -le 90")]
    [InlineData("LockoutDuration -eq 30")]
    [InlineData("LockoutObservationWindow -eq 30")]
    public void FgppIntervalOverrides_ThrowTheIntervalRefusal(string filter)
    {
        var ex = Assert.Throws<AdFilterTranslationException>(() =>
            AdFilterTranslator.Translate(filter, NoVariables,
                attributeOverrides: AdObjectSchema.FineGrainedPasswordPolicy.AttributeOverrides));

        Assert.Contains("interval-valued", ex.Message);
        Assert.DoesNotContain("not a recognised", ex.Message);
    }

    [Fact]
    public void FgppOverrides_CoverEveryEntryInTheSchema()
    {
        // Every override name must resolve through the filter path without "not recognised":
        // a new override added to the schema without filter-side coverage is the 0.3.0 bug
        // reappearing for that property. Interval-valued targets throw the (correct) interval
        // refusal instead of emitting; both outcomes prove the override was consulted.
        foreach (var (name, target) in AdObjectSchema.FineGrainedPasswordPolicy.AttributeOverrides!)
        {
            object? value = AdAttributeSchema.SyntaxOf(target) switch
            {
                AdAttributeSyntax.Integer => 5,
                AdAttributeSyntax.Boolean => true,
                _ => "x"
            };

            try
            {
                var node = AdFilterTranslator.Translate(
                    $"{name} -eq $value",
                    Vars(("value", value)),
                    attributeOverrides: AdObjectSchema.FineGrainedPasswordPolicy.AttributeOverrides);
                Assert.Contains($"({target}=", AdFilterEmitter.Emit(node!));
            }
            catch (AdFilterTranslationException ex)
            {
                Assert.Contains("interval-valued", ex.Message);
            }
        }
    }

    [Fact]
    public void OuStreetAddress_ResolvesToStreet_NotStreetAddress()
    {
        // OUs store the street in 'street'; users in 'streetAddress'. The global table's
        // answer matched nothing on an OU, silently.
        var node = AdFilterTranslator.Translate(
            "StreetAddress -like '*Main*'", NoVariables,
            attributeOverrides: AdObjectSchema.OrganizationalUnit.AttributeOverrides);
        Assert.Equal("(street=*Main*)", AdFilterEmitter.Emit(node!));
    }

    [Theory]
    // Constructed wire attributes: registering their syntax (0.4, for projection) must not
    // open a filter path AD will never evaluate -- the comparison would match nothing with
    // a success code. The refusal is unconditional: these names are KNOWN, so
    // -AllowUnknownProperty is not an escape hatch, same as the synthetic refusals.
    [InlineData("tokenGroups -eq 'S-1-5-21-1-2-3-513'")]
    [InlineData("tokenGroups -ne $null")]
    [InlineData("(tokenGroups -eq 'S-1-5-21-1-2-3-513')")]
    [InlineData("msDS-User-Account-Control-Computed -band 8388608")]
    [InlineData("msDS-User-Account-Control-Computed -eq 0")]
    [InlineData("primaryGroupToken -eq 513")]
    public void ConstructedAttributes_AreRefusedInFilters_WithARedirect(string filter)
    {
        var ex = Assert.Throws<AdFilterTranslationException>(() =>
            AdFilterTranslator.Translate(filter, NoVariables, allowUnknownProperty: true));

        Assert.Contains("constructed attribute", ex.Message);
        Assert.Contains("Instead,", ex.Message);
    }

    [Fact]
    public void WithoutOverrides_TheGlobalTablesStillGovern()
    {
        // On schemas without an override (Get-ADxUser and the domain-head policy), the
        // pre-0.4 resolutions are unchanged -- overrides are additive per type, not global.
        Assert.Equal("(minPwdLength>=14)", T("MinPasswordLength -ge 14"));
        Assert.Equal("(streetAddress=*Main*)", T("StreetAddress -like '*Main*'"));

        var ex = Assert.Throws<AdFilterTranslationException>(() =>
            AdFilterTranslator.Translate("Precedence -eq 500", NoVariables));
        Assert.Contains("not a recognised", ex.Message);
    }
}
