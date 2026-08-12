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
    // THE golden pair: same raw value, different operator, different escaping
    [InlineData("Name -eq 'j*'", "(name=j\\2a)")]
    [InlineData("Name -like 'j*'", "(name=j*)")]
    // The i-prefixed spellings escape the same way as their bare equivalents -- the
    // normalization must not accidentally route -ieq through the pattern escaper.
    [InlineData("Name -ieq 'j*'", "(name=j\\2a)")]
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

    [Fact]
    public void InjectionCharactersInVariableValue_AreEscaped()
    {
        var resolver = Vars(("v", "*)(uid=*"));
        Assert.Equal("(description=\\2a\\29\\28uid=\\2a)", T("Description -eq $v", resolver));
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
        // The escaping decision tracks the operator even when the value came from a variable.
        var resolver = Vars(("pattern", "j*"));
        Assert.Equal("(name=j\\2a)", T("Name -eq $pattern", resolver));
        Assert.Equal("(name=j*)", T("Name -like $pattern", resolver));
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
}
