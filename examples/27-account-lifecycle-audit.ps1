# Account lifecycle, one question per call.
#
# Search-ADxAccount is deliberately not a general query tool: no -Filter, no
# -Identity, no -Properties. You pick exactly ONE criterion switch, scope it with
# -UsersOnly / -ComputersOnly and -SearchBase, and get a fixed slim account shape
# back - the same contract as RSAT's Search-ADAccount. Each criterion is a specific
# server-side LDAP filter over userAccountControl bits, accountExpires,
# lastLogonTimestamp or lockoutTime; the one criterion that CANNOT be expressed
# server-side says so below.
#
# One documented divergence from RSAT: -AccountExpiring and -AccountInactive require
# exactly one of -DateTime or -TimeSpan. RSAT silently supplies a default window when
# you give neither; ADx refuses to guess which cutoff you meant.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- Disabled accounts ---
# UAC bit 0x2, matched server-side with the same bitwise rule example 07 describes.
Search-ADxAccount -AccountDisabled -UsersOnly |
    Select-Object Name, SamAccountName, LastLogonDate | Format-Table -AutoSize

# --- Already-expired accounts ---
# accountExpires between 1 and now. AD stores "never expires" two different ways -
# 0 and the FILETIME maximum - and the range excludes both on the wire, so unset
# expiry never reads as expired.
Search-ADxAccount -AccountExpired |
    Select-Object Name, SamAccountName, AccountExpirationDate, Enabled | Format-Table -AutoSize

# --- Expiring soon: contractor offboarding before it becomes a helpdesk call ---
# The window is (now, cutoff]. -TimeSpan is relative - now plus 30 days here;
# -DateTime is an absolute cutoff, interpreted in LOCAL time, matching RSAT.
Search-ADxAccount -AccountExpiring -TimeSpan 30.00:00:00 -UsersOnly |
    Select-Object Name, SamAccountName, AccountExpirationDate | Format-Table -AutoSize

# --- Inactive accounts - and "never" counts as inactive ---
# lastLogonTimestamp older than the cutoff OR absent entirely. An account that has
# never logged on has no lastLogonTimestamp at all, and a filter with only the range
# arm would silently drop exactly the accounts most worth seeing; RSAT includes
# them, and so does ADx. (Example 06 reaches the same absent-attribute case through
# Get-ADxUser, where it needs an explicit -LDAPFilter.) The usual caveat applies:
# lastLogonTimestamp replicates lazily, up to ~14 days stale - right for "idle for
# months", wrong for "logged on this morning".
Search-ADxAccount -AccountInactive -TimeSpan 90.00:00:00 -UsersOnly |
    Select-Object Name, SamAccountName, Enabled, LastLogonDate | Format-Table -AutoSize

# --- Locked out, right now ---
Search-ADxAccount -LockedOut |
    Select-Object Name, SamAccountName, ObjectClass | Format-Table -AutoSize

# --- Expired passwords: the one criterion the server cannot answer ---
# The PASSWORD_EXPIRED bit lives in msDS-User-Account-Control-Computed - a
# CONSTRUCTED attribute, computed per read, which AD cannot match in a search
# filter. So this criterion reads the whole in-scope population and tests each entry
# client-side. That is the cost of the question; pay less of it by scoping with
# -UsersOnly and -SearchBase, as here.
Search-ADxAccount -PasswordExpired -UsersOnly -SearchBase 'OU=Sales,DC=corp,DC=contoso,DC=com' |
    Select-Object Name, SamAccountName, Enabled | Format-Table -AutoSize

# --- Passwords that never expire ---
# The same population example 07 found via Get-ADxUser - one switch instead of a
# filter expression, at the price of the fixed output shape.
Search-ADxAccount -PasswordNeverExpires -UsersOnly |
    Select-Object Name, SamAccountName, Enabled | Format-Table -AutoSize

# --- When the slim shape is not enough ---
# There is no -Properties, by contract. Pipe the results back through the type
# cmdlet instead - it binds on DistinguishedName - and name the columns there.
Search-ADxAccount -AccountInactive -TimeSpan 90.00:00:00 -UsersOnly |
    Get-ADxUser -Properties Department, Manager |
    Select-Object Name, SamAccountName, Department, Manager | Format-Table -AutoSize

<#
Sample output

Name          SamAccountName LastLogonDate
----          -------------- -------------
Contractor 07 ctr07
svc-legacy    svc-legacy     2024-03-02 11:41:08

Name          SamAccountName AccountExpirationDate Enabled
----          -------------- --------------------- -------
Contractor 03 ctr03          2026-05-31 00:00:00      True
Contractor 07 ctr07          2026-02-28 00:00:00     False

Name          SamAccountName AccountExpirationDate
----          -------------- ---------------------
Contractor 12 ctr12          2026-08-29 00:00:00
Contractor 15 ctr15          2026-09-02 00:00:00

Name          SamAccountName Enabled LastLogonDate
----          -------------- ------- -------------
Peter Novak   pnovak            True 2025-11-02 08:12:44
Ana Ruiz      aruiz             True 2026-01-19 17:03:51
Tom Fisher    tfisher           True 2026-03-30 09:55:02
Newstarter    nstarter          True
Contractor 07 ctr07            False

Name       SamAccountName ObjectClass
----       -------------- -----------
Tom Fisher tfisher        user

Name     SamAccountName Enabled
----     -------------- -------
Ana Ruiz aruiz             True

Name       SamAccountName Enabled
----       -------------- -------
svc-sql    svc-sql           True
svc-backup svc-backup        True
Ida Berg   iberg             True

Name          SamAccountName Department Manager
----          -------------- ---------- -------
Peter Novak   pnovak         Logistics  CN=Ida Berg,OU=Users,OU=Engineering,DC=corp,DC=contoso,DC=com
Ana Ruiz      aruiz          Sales      CN=Ida Berg,OU=Users,OU=Engineering,DC=corp,DC=contoso,DC=com
Tom Fisher    tfisher        Support
Newstarter    nstarter
Contractor 07 ctr07

Three rows worth a second look. ctr03 is EXPIRED but still ENABLED: expiry alone
blocks logon, so nothing is on fire - but one well-meaning "extend the contractor"
click on accountExpires reactivates a live credential, which is why expired-enabled
accounts belong in the disable queue, not the backlog. In the inactive table,
nstarter and ctr07 have no LastLogonDate at all - the never-logged-on arm at work;
without it they would be the report's blind spot.

And one subtlety inherited from RSAT on purpose: -PasswordExpired EXCLUDES accounts
flagged must-change-at-next-logon (pwdLastSet 0), even though AD sets the same
computed bit for them - because RSAT's Search-ADAccount excludes them, while RSAT's
own Get-ADUser PasswordExpired property includes them. RSAT disagrees with itself;
ADx sides with the cmdlet it is replacing. Example 07 reads the property side.
#>
