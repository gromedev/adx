using ADx.Engine.Filter;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// M1: the pure AST -> LDAP filter text emitter. The tokenizer/parser bridge that builds these
/// trees from a PowerShell <c>-Filter</c> string is M2; these tests construct the AST directly.
/// </summary>
public class AdFilterEmitterTests
{
    [Fact]
    public void Equality_Emits_Attribute_Eq_Value()
    {
        var node = new AdFilterEquality("name", LdapAssertionValue.Exact("jdoe"));
        Assert.Equal("(name=jdoe)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Inequality_WrapsEqualityInNot()
    {
        var node = new AdFilterInequality("name", LdapAssertionValue.Exact("jdoe"));
        Assert.Equal("(!(name=jdoe))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Like_And_Eq_DivergeOnWildcardEscaping()
    {
        // The golden test from the plan: same raw value, different value-type constructor,
        // different emitted filter. This is the one assertion that covers the whole
        // "-eq unconditionally escapes '*'" vs "-like preserves it" class of bug.
        var eq = new AdFilterEquality("name", LdapAssertionValue.Exact("j*"));
        var like = new AdFilterEquality("name", LdapAssertionValue.Pattern("j*"));

        Assert.Equal("(name=j\\2a)", AdFilterEmitter.Emit(eq));
        Assert.Equal("(name=j*)", AdFilterEmitter.Emit(like));
        Assert.NotEqual(AdFilterEmitter.Emit(eq), AdFilterEmitter.Emit(like));
    }

    [Fact]
    public void GreaterOrEqual_Emits_Attribute_Ge_Value()
    {
        var node = new AdFilterGreaterOrEqual("uSNChanged", LdapAssertionValue.Verbatim("1000"));
        Assert.Equal("(uSNChanged>=1000)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void GreaterThan_EmitsGeAndNotEqConjunction()
    {
        // LDAP has no strict '>' -- ">=  and  not =" is the only encoding.
        var node = new AdFilterGreaterThan("uSNChanged", LdapAssertionValue.Verbatim("1000"));
        Assert.Equal("(&(uSNChanged>=1000)(!(uSNChanged=1000)))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void LessOrEqual_Emits_Attribute_Le_Value()
    {
        var node = new AdFilterLessOrEqual("uSNChanged", LdapAssertionValue.Verbatim("1000"));
        Assert.Equal("(uSNChanged<=1000)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void LessThan_EmitsLeAndNotEqConjunction()
    {
        var node = new AdFilterLessThan("uSNChanged", LdapAssertionValue.Verbatim("1000"));
        Assert.Equal("(&(uSNChanged<=1000)(!(uSNChanged=1000)))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Present_Emits_Attribute_Eq_Star()
    {
        Assert.Equal("(mail=*)", AdFilterEmitter.Emit(new AdFilterPresent("mail")));
    }

    [Fact]
    public void Absent_IsTheEqNullIdiom()
    {
        // "-eq $null" translates to this: the attribute has no value at all.
        Assert.Equal("(!(mail=*))", AdFilterEmitter.Emit(new AdFilterAbsent("mail")));
    }

    [Fact]
    public void BitAnd_UsesTheAndMatchingRuleOid()
    {
        var node = new AdFilterBitAnd("userAccountControl", LdapAssertionValue.Verbatim("2"));
        Assert.Equal("(userAccountControl:1.2.840.113556.1.4.803:=2)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void BitOr_UsesTheOrMatchingRuleOid()
    {
        var node = new AdFilterBitOr("userAccountControl", LdapAssertionValue.Verbatim("2"));
        Assert.Equal("(userAccountControl:1.2.840.113556.1.4.804:=2)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void RecursiveMatch_UsesTheTransitiveClosureOid()
    {
        var node = new AdFilterRecursiveMatch("memberOf", LdapAssertionValue.Exact("CN=G,DC=x"));
        Assert.Equal("(memberOf:1.2.840.113556.1.4.1941:=CN=G,DC=x)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void And_WrapsOperandsWithAmpersand()
    {
        var node = new AdFilterAnd(new AdFilterNode[]
        {
            new AdFilterEquality("name", LdapAssertionValue.Exact("a")),
            new AdFilterEquality("title", LdapAssertionValue.Exact("b"))
        });

        Assert.Equal("(&(name=a)(title=b))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Or_WrapsOperandsWithPipe()
    {
        var node = new AdFilterOr(new AdFilterNode[]
        {
            new AdFilterEquality("name", LdapAssertionValue.Exact("a")),
            new AdFilterEquality("title", LdapAssertionValue.Exact("b"))
        });

        Assert.Equal("(|(name=a)(title=b))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Not_WrapsOperand()
    {
        var node = new AdFilterNot(new AdFilterEquality("name", LdapAssertionValue.Exact("a")));
        Assert.Equal("(!(name=a))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void PrecedenceExample_AOrBAndC()
    {
        // "A -or B -and C" -- the plan's own golden precedence example. AND binds tighter than
        // OR, so this is A-or-(B-and-C), not (A-or-B)-and-C.
        var node = new AdFilterOr(new AdFilterNode[]
        {
            new AdFilterEquality("a", LdapAssertionValue.Exact("1")),
            new AdFilterAnd(new AdFilterNode[]
            {
                new AdFilterEquality("b", LdapAssertionValue.Exact("2")),
                new AdFilterEquality("c", LdapAssertionValue.Exact("3"))
            })
        });

        Assert.Equal("(|(a=1)(&(b=2)(c=3)))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Raw_EmitsFilterTextVerbatim()
    {
        var node = new AdFilterRaw("(objectCategory=person)");
        Assert.Equal("(objectCategory=person)", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void Raw_ComposesWithAndAsABaseClassFilter()
    {
        // How a preset ANDs its base object-class filter with the user's translated filter.
        var node = new AdFilterAnd(new AdFilterNode[]
        {
            new AdFilterRaw("(&(objectCategory=person)(objectClass=user))"),
            new AdFilterEquality("name", LdapAssertionValue.Exact("jdoe"))
        });

        Assert.Equal("(&(&(objectCategory=person)(objectClass=user))(name=jdoe))", AdFilterEmitter.Emit(node));
    }

    [Fact]
    public void And_WithNoOperands_Throws()
    {
        Assert.Throws<ArgumentException>(() => AdFilterEmitter.Emit(new AdFilterAnd(Array.Empty<AdFilterNode>())));
    }

    [Fact]
    public void Or_WithNoOperands_Throws()
    {
        Assert.Throws<ArgumentException>(() => AdFilterEmitter.Emit(new AdFilterOr(Array.Empty<AdFilterNode>())));
    }
}
