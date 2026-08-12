# Password and lockout hygiene, in one pass per question.
#
# Every flag here lives in the bit-packed userAccountControl attribute. ADx decodes
# them into named booleans on output, and - more usefully - translates them back into
# server-side bitwise matching rules on input, so "PasswordNeverExpires -eq $true"
# becomes (userAccountControl:1.2.840.113556.1.4.803:=65536) on the wire.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- Passwords that never expire ---
Get-ADxUser -Filter 'Enabled -eq $true -and PasswordNeverExpires -eq $true' `
            -Properties PasswordLastSet, Department |
    Select-Object Name, SamAccountName, Department, PasswordLastSet,
                  @{N='PwdAgeDays'; E={ [int]((Get-Date) - $_.PasswordLastSet).TotalDays }} |
    Sort-Object PwdAgeDays -Descending | Format-Table -AutoSize

# --- Accounts that may authenticate with no password at all ---
Get-ADxUser -Filter 'Enabled -eq $true -and PasswordNotRequired -eq $true' |
    Select-Object Name, SamAccountName, DistinguishedName | Format-Table -AutoSize

# --- Must change password at next logon (pwdLastSet is literally 0) ---
Get-ADxUser -Filter 'Enabled -eq $true -and pwdLastSet -eq 0' |
    Select-Object Name, SamAccountName | Format-Table -AutoSize

# --- Kerberos pre-authentication disabled (AS-REP roastable) ---
Get-ADxUser -Filter 'DoesNotRequirePreAuth -eq $true' -Properties whenCreated |
    Select-Object Name, SamAccountName, Enabled, whenCreated | Format-Table -AutoSize

# --- Lockout and expiry state ---
# LockedOut and PasswordExpired are synthetics: LockedOut is derived from lockoutTime,
# and PasswordExpired from msDS-User-Account-Control-Computed, which is a CONSTRUCTED
# attribute. Constructed attributes are never returned by -Properties * - by RSAT
# either - so they have to be named explicitly. ADx fetches the right source for each.
Get-ADxUser -Filter 'Enabled -eq $true' `
            -Properties LockedOut, AccountLockoutTime, PasswordExpired,
                        BadLogonCount, LastBadPasswordAttempt |
    Where-Object { $_.LockedOut -or $_.PasswordExpired } |
    Select-Object Name, SamAccountName, LockedOut, AccountLockoutTime,
                  PasswordExpired, BadLogonCount, LastBadPasswordAttempt |
    Format-Table -AutoSize

<#
Sample output

Name        SamAccountName Department PasswordLastSet     PwdAgeDays
----        -------------- ---------- ---------------     ----------
svc-sql     svc-sql        (none)     2021-03-08 14:22:10       1982
svc-backup  svc-backup     (none)     2023-07-19 09:03:44       1119
Ida Berg    iberg          Finance    2024-11-30 16:41:02        619

Name       SamAccountName DistinguishedName
----       -------------- -----------------
kiosk01    kiosk01        CN=kiosk01,OU=Kiosks,DC=corp,DC=contoso,DC=com

Name        SamAccountName
----        --------------
Newstarter  nstarter

Name       SamAccountName Enabled whenCreated
----       -------------- ------- -----------
svc-legacy svc-legacy        True 2022-01-11 10:05:19

Name       SamAccountName LockedOut AccountLockoutTime  PasswordExpired BadLogonCount LastBadPasswordAttempt
----       -------------- --------- ------------------  --------------- ------------- ----------------------
Tom Fisher tfisher             True 2026-08-11 08:32:17           False             7 2026-08-11 08:32:17
Ana Ruiz   aruiz              False                                True             0

Two of these deserve a second look rather than a row in a spreadsheet:

  PasswordNotRequired on an ENABLED account means that account may be able to
  authenticate with a blank password. It is the most security-relevant flag on the
  list and it is set far more often by accident than on purpose.

  DoesNotRequirePreAuth is what makes an account AS-REP roastable: anyone who can
  reach the KDC can request an encrypted blob for it and crack it offline, with no
  credentials at all. See example 20 for the Kerberoasting counterpart.
#>
