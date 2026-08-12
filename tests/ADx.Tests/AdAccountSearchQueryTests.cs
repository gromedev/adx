using ADx.Engine.Filter;
using ADx.Engine.Ldap;
using Xunit;

namespace ADx.Tests;

/// <summary>
/// Golden wire filters for Search-ADxAccount. The emitted filter IS the behaviour, so these
/// assert exact strings; a wrong bit or an inverted window boundary is a zero-row success, the
/// failure class the whole module guards against. FILETIME expectations are built with
/// LdapConvert.ToFileTime rather than magic literals, so tick arithmetic can't be transcribed wrong.
/// </summary>
public class AdAccountSearchQueryTests
{
    // A fixed, Kind=Utc "now" so the tests are deterministic.
    private static readonly DateTime Now = new(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);

    private const string UserScope = "(&(objectCategory=person)(objectClass=user))";
    private const string ComputerScope = "(objectCategory=computer)";
    // Default (AllAccounts) scope: every account class derives from user.
    private const string AllScope = "(objectClass=user)";

    private static string FT(DateTime t) => LdapConvert.ToFileTime(t);

    [Fact]
    public void AccountDisabled_TestsUacBit2()
    {
        Assert.Equal(
            $"(&{AllScope}(userAccountControl:1.2.840.113556.1.4.803:=2))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.AccountDisabled,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, null));
    }

    [Fact]
    public void PasswordNeverExpires_TestsUacBit65536()
    {
        Assert.Equal(
            $"(&{AllScope}(userAccountControl:1.2.840.113556.1.4.803:=65536))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.PasswordNeverExpires,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, null));
    }

    [Fact]
    public void LockedOut_TestsLockoutTimeAtLeastOne()
    {
        Assert.Equal(
            $"(&{AllScope}(lockoutTime>=1))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.LockedOut,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, null));
    }

    [Fact]
    public void AccountExpired_IsPastButNotTheNeverSentinels()
    {
        Assert.Equal(
            $"(&{AllScope}(&(accountExpires>=1)(accountExpires<={FT(Now)})))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.AccountExpired,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, null));
    }

    [Fact]
    public void AccountExpiring_WindowIsNowToCutoff_Forward()
    {
        var cutoff = Now.AddDays(30);
        Assert.Equal(
            $"(&{AllScope}(&(accountExpires>={FT(Now)})(accountExpires<={FT(cutoff)})))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.AccountExpiring,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, cutoff));
    }

    [Fact]
    public void AccountInactive_IsLastLogonBeforeCutoff_OrNeverLoggedOn()
    {
        // RSAT includes never-logged-on accounts (no lastLogonTimestamp) in -AccountInactive
        // (confirmed live). Without the absent arm they are silently dropped.
        var cutoff = Now.AddDays(-90);
        Assert.Equal(
            $"(&{AllScope}(|(lastLogonTimestamp<={FT(cutoff)})(!(lastLogonTimestamp=*))))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.AccountInactive,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, cutoff));
    }

    [Fact]
    public void PasswordExpired_HasNoWireCriterion_ScopeOnly()
    {
        // Constructed attribute; filtered client-side. The wire filter is the scope alone.
        Assert.Equal(
            AllScope,
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.PasswordExpired,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, null));
    }

    [Theory]
    [InlineData(AdAccountSearchQuery.AccountScope.UsersOnly, UserScope)]
    [InlineData(AdAccountSearchQuery.AccountScope.ComputersOnly, ComputerScope)]
    public void Scope_RestrictsToOneClass(AdAccountSearchQuery.AccountScope scope, string expectedScope)
    {
        Assert.Equal(expectedScope, AdAccountSearchQuery.ScopeFilter(scope));
        Assert.Equal(
            $"(&{expectedScope}(userAccountControl:1.2.840.113556.1.4.803:=2))",
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.AccountDisabled, scope, Now, null));
    }

    [Fact]
    public void DefaultScope_IsEveryAccountClass()
    {
        // objectClass=user matches user, computer, inetOrgPerson AND managed service accounts,
        // which RSAT's unscoped Search-ADAccount includes (the -UsersOnly/-ComputersOnly scopes
        // stay narrow). Confirmed live: the gMSAs were in RSAT's set.
        Assert.Equal("(objectClass=user)", AdAccountSearchQuery.ScopeFilter(AdAccountSearchQuery.AccountScope.AllAccounts));
    }

    [Fact]
    public void WindowedCriterion_WithoutCutoff_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdAccountSearchQuery.BuildFilter(
                AdAccountSearchQuery.Criterion.AccountExpiring,
                AdAccountSearchQuery.AccountScope.AllAccounts, Now, null));
    }

    [Theory]
    [InlineData(0x800000, true)]
    [InlineData(0x800010, true)]   // other computed bits set alongside
    [InlineData(0x0, false)]
    [InlineData(0x10, false)]      // a different computed bit, not PASSWORD_EXPIRED
    public void PasswordExpiredPredicate_TestsBit0x800000(int computedUac, bool expected)
    {
        // No pwdLastSet -> not must-change, so the bit alone decides.
        var dict = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
        {
            ["msDS-User-Account-Control-Computed"] = new object[] { computedUac.ToString() },
        };
        var entry = new LdapEntry("CN=x,DC=y", dict);
        Assert.Equal(expected, AdAccountSearchQuery.PasswordExpiredPredicate(entry));
    }

    [Fact]
    public void PasswordExpiredPredicate_MustChangeAtNextLogon_IsExcluded()
    {
        // pwdLastSet == 0 (must change at next logon) ALSO sets the 0x800000 computed bit, but
        // RSAT's Search-ADAccount excludes it -- a divergence within RSAT itself, proven live
        // against a real must-change account.
        var mustChange = new LdapEntry("CN=x,DC=y",
            new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
            {
                ["msDS-User-Account-Control-Computed"] = new object[] { "8388608" }, // 0x800000
                ["pwdLastSet"] = new object[] { "0" },
            });
        Assert.False(AdAccountSearchQuery.PasswordExpiredPredicate(mustChange));

        // A genuinely expired password (pwdLastSet a real timestamp, bit set) is included.
        var expired = new LdapEntry("CN=y,DC=y",
            new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase)
            {
                ["msDS-User-Account-Control-Computed"] = new object[] { "8388608" },
                ["pwdLastSet"] = new object[] { "133000000000000000" },
            });
        Assert.True(AdAccountSearchQuery.PasswordExpiredPredicate(expired));
    }

    [Fact]
    public void PasswordExpiredPredicate_AbsentAttribute_IsFalse()
    {
        var entry = new LdapEntry("CN=x,DC=y",
            new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase));
        Assert.False(AdAccountSearchQuery.PasswordExpiredPredicate(entry));
    }
}
