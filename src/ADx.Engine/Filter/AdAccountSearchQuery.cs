using ADx.Engine.Ldap;

namespace ADx.Engine.Filter;

/// <summary>
/// The wire filters behind Search-ADxAccount, one per criterion switch. Pure and
/// golden-testable (the emitted filter IS the behaviour): the cmdlet captures "now" once and
/// hands it in, so nothing here reads the clock. The account scope reuses
/// <see cref="AdObjectSchema"/>'s user/computer base filters so "what is an account" cannot
/// drift from Get-ADxUser/Get-ADxComputer.
/// </summary>
public static class AdAccountSearchQuery
{
    public enum Criterion
    {
        AccountDisabled,
        AccountExpired,
        AccountExpiring,
        AccountInactive,
        LockedOut,
        PasswordExpired,
        PasswordNeverExpires
    }

    public enum AccountScope
    {
        AllAccounts,
        UsersOnly,
        ComputersOnly
    }

    /// <summary>The account-class scope filter alone.</summary>
    public static string ScopeFilter(AccountScope scope) => AdFilterEmitter.Emit(ScopeNode(scope));

    private static AdFilterNode ScopeNode(AccountScope scope) => scope switch
    {
        // -UsersOnly / -ComputersOnly stay narrow (objectCategory-gated), matching RSAT.
        AccountScope.UsersOnly => new AdFilterRaw(AdObjectSchema.User.BaseFilter!),
        AccountScope.ComputersOnly => new AdFilterRaw(AdObjectSchema.Computer.BaseFilter!),
        // Default: EVERY account. objectClass=user matches user, computer, inetOrgPerson AND
        // managed service accounts (all derive from user), which RSAT's unscoped Search-ADAccount
        // includes -- confirmed live (the gMSAs were in RSAT's set and missing from a user-union).
        _ => new AdFilterRaw("(objectClass=user)")
    };

    /// <summary>
    /// The full wire filter: <c>(&amp; scope criterion)</c>. <paramref name="nowUtc"/> and
    /// <paramref name="cutoffUtc"/> must be UTC (the cmdlet normalizes a local -DateTime first).
    /// PasswordExpired has no wire criterion -- it is a constructed attribute filtered
    /// client-side by <see cref="PasswordExpiredPredicate"/> -- so its filter is the scope alone.
    /// </summary>
    public static string BuildFilter(Criterion criterion, AccountScope scope, DateTime nowUtc, DateTime? cutoffUtc)
    {
        var scopeNode = ScopeNode(scope);
        var criterionNode = CriterionNode(criterion, nowUtc, cutoffUtc);

        return criterionNode is null
            ? AdFilterEmitter.Emit(scopeNode)
            : AdFilterEmitter.Emit(new AdFilterAnd(new[] { scopeNode, criterionNode }));
    }

    private static AdFilterNode? CriterionNode(Criterion criterion, DateTime nowUtc, DateTime? cutoffUtc)
    {
        static string FileTime(DateTime t) => LdapConvert.ToFileTime(t);

        switch (criterion)
        {
            // UAC ACCOUNTDISABLE (0x2) via the 1.2.840.113556.1.4.803 bitwise-AND matching rule.
            case Criterion.AccountDisabled:
                return new AdFilterBitAnd("userAccountControl", LdapAssertionValue.Verbatim("2"));

            // UAC DONT_EXPIRE_PASSWORD (0x10000 = 65536).
            case Criterion.PasswordNeverExpires:
                return new AdFilterBitAnd("userAccountControl", LdapAssertionValue.Verbatim("65536"));

            // lockoutTime >= 1 -- the same signal the LockedOut synthetic uses.
            case Criterion.LockedOut:
                return new AdFilterGreaterOrEqual("lockoutTime", LdapAssertionValue.Verbatim("1"));

            // Expiration set (>= 1 drops the "never" 0) AND already past (<= now drops the
            // long.MaxValue "never").
            case Criterion.AccountExpired:
                return new AdFilterAnd(new AdFilterNode[]
                {
                    new AdFilterGreaterOrEqual("accountExpires", LdapAssertionValue.Verbatim("1")),
                    new AdFilterLessOrEqual("accountExpires", LdapAssertionValue.Verbatim(FileTime(nowUtc))),
                });

            // Expiration in the window (now, cutoff]. >= now excludes both unset and already-expired.
            case Criterion.AccountExpiring:
                return new AdFilterAnd(new AdFilterNode[]
                {
                    new AdFilterGreaterOrEqual("accountExpires", LdapAssertionValue.Verbatim(FileTime(nowUtc))),
                    new AdFilterLessOrEqual("accountExpires", LdapAssertionValue.Verbatim(FileTime(RequireCutoff(cutoffUtc, criterion)))),
                });

            // Last replicated logon older than the cutoff, OR never logged on at all -- an
            // account that never logged on has no lastLogonTimestamp and IS inactive, which
            // RSAT includes (confirmed live: -AccountInactive returns exactly the never-logged
            // set on a freshly seeded domain). Without the absent arm those accounts are
            // silently dropped.
            case Criterion.AccountInactive:
                return new AdFilterOr(new AdFilterNode[]
                {
                    new AdFilterLessOrEqual("lastLogonTimestamp",
                        LdapAssertionValue.Verbatim(FileTime(RequireCutoff(cutoffUtc, criterion)))),
                    new AdFilterAbsent("lastLogonTimestamp"),
                });

            // Constructed attribute -- not filterable on the wire; the cmdlet post-filters.
            case Criterion.PasswordExpired:
                return null;

            default:
                throw new ArgumentOutOfRangeException(nameof(criterion), criterion, "Unknown account criterion.");
        }
    }

    private static DateTime RequireCutoff(DateTime? cutoffUtc, Criterion criterion) =>
        cutoffUtc ?? throw new InvalidOperationException(
            $"{criterion} requires a -DateTime or -TimeSpan cutoff.");

    /// <summary>
    /// The -PasswordExpired predicate: the PASSWORD_EXPIRED bit (0x800000) of the constructed
    /// msDS-User-Account-Control-Computed, applied per streamed entry (the attribute cannot
    /// appear in a search filter). Accounts flagged must-change-at-next-logon (pwdLastSet == 0)
    /// ALSO set that bit, but RSAT's Search-ADAccount excludes them -- a divergence within RSAT
    /// itself (Get-ADUser's PasswordExpired *property* includes them). Search must match Search,
    /// so pwdLastSet == 0 is excluded here. The caller fetches pwdLastSet for this criterion.
    /// </summary>
    public static bool PasswordExpiredPredicate(LdapEntry entry)
    {
        var computed = entry.GetInt64("msDS-User-Account-Control-Computed");
        if (computed is not { } v || ((uint)v & 0x800000u) == 0) return false;

        // pwdLastSet == 0 is must-change-at-next-logon: excluded. Absent/non-zero: included.
        return entry.GetInt64("pwdLastSet") != 0;
    }
}
