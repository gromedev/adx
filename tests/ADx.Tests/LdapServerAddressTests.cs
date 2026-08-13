using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// 0.4: the "host:port" -Server spelling. The stakes are not cosmetic -- an unparsed
/// embedded port binds whatever port the NATIVE stack reads out of the string while every
/// ADx decision keyed on the configured port (the GC safeguards above all) reads a
/// different one.
/// </summary>
public class LdapServerAddressTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("  ", null, null)]
    [InlineData("dc01.corp.com", "dc01.corp.com", null)]
    [InlineData(" dc01.corp.com ", "dc01.corp.com", null)]
    [InlineData("dc01.corp.com:3268", "dc01.corp.com", 3268)]
    [InlineData("dc01.corp.com:636", "dc01.corp.com", 636)]
    [InlineData("dc01:389", "dc01", 389)]
    [InlineData("10.0.0.5:3269", "10.0.0.5", 3269)]
    // Bare IPv6 literals: colons galore, never a port.
    [InlineData("::1", "::1", null)]
    [InlineData("fe80::1", "fe80::1", null)]
    [InlineData("2001:db8::636", "2001:db8::636", null)]
    // Bracketed IPv6: the only way to give a v6 literal a port.
    [InlineData("[::1]", "[::1]", null)]
    [InlineData("[::1]:636", "[::1]", 636)]
    [InlineData("[2001:db8::5]:3268", "[2001:db8::5]", 3268)]
    public void Parse_SplitsHostAndPort(string? input, string? host, int? port)
    {
        Assert.Equal((host, port), LdapServerAddress.Parse(input));
    }

    [Theory]
    [InlineData("dc01:abc")]           // non-numeric port
    [InlineData("dc01:")]              // empty port
    [InlineData("dc01:0")]             // out of range
    [InlineData("dc01:65536")]         // out of range
    [InlineData("dc01:-1")]            // NumberStyles.None rejects the sign
    [InlineData(":3268")]              // empty host
    [InlineData("[::1")]               // unterminated bracket
    [InlineData("[::1]x")]             // junk after bracket
    [InlineData("[]")]                 // empty bracket pair
    [InlineData("[]:636")]             // empty bracket pair with port
    [InlineData("dc01 :636")]          // interior whitespace before the separator
    [InlineData("dc01. corp.com:636")] // interior whitespace inside the host
    [InlineData("dc01 dc02")]          // space-separated multi-host list is not supported
    public void Parse_MalformedValues_ThrowLoudly(string input)
    {
        Assert.Throws<ArgumentException>(() => LdapServerAddress.Parse(input));
    }
}
