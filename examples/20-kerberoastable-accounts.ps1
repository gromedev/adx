# Service accounts whose passwords can be cracked offline.
#
# Any authenticated user can request a service ticket for any account with a
# servicePrincipalName, and that ticket is encrypted with the account's password hash.
# Request it, take it away, crack it at leisure - no further access needed, and nothing
# on the DC looks unusual. That is Kerberoasting, and the exposure is proportional to
# how old and how guessable those passwords are.
#
# This report is the inventory side of that: which accounts are exposed, how stale
# their passwords are, and which of them are privileged.
#
# Requirements: PowerShell 7.5+, read access to the directory. Read-only throughout.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# Computer accounts have SPNs too, but their passwords are 120-character machine
# secrets rotated every 30 days - not worth cracking. Get-ADxUser's object-class
# filter (objectCategory=person) excludes them for free.
$roastable = Get-ADxUser -Filter "ServicePrincipalNames -like '*' -and Enabled -eq $true" `
                         -Properties ServicePrincipalNames, PasswordLastSet, adminCount,
                                     LastLogonDate, PasswordNeverExpires, Description |
    Select-Object Name, SamAccountName, Description,
                  @{N='SPNs';        E={ @($_.ServicePrincipalNames).Count }},
                  PasswordLastSet,
                  @{N='PwdAgeDays';  E={ if ($_.PasswordLastSet) { [int]((Get-Date) - $_.PasswordLastSet).TotalDays } } },
                  @{N='Privileged';  E={ [bool]$_.adminCount }},
                  PasswordNeverExpires, LastLogonDate, ServicePrincipalNames

$roastable | Sort-Object Privileged, PwdAgeDays -Descending |
    Format-Table Name, SamAccountName, SPNs, PwdAgeDays, Privileged, PasswordNeverExpires -AutoSize

"$($roastable.Count) enabled accounts with an SPN"

# The ones that matter most: privileged AND a password old enough to have been set by
# hand, which is the combination that turns a crackable ticket into domain compromise.
$roastable | Where-Object { $_.Privileged -and $_.PwdAgeDays -gt 365 } |
    Select-Object Name, SamAccountName, PwdAgeDays, @{N='SPN'; E={ $_.ServicePrincipalNames -join '; ' }} |
    Format-List

# AS-REP roasting is the no-credentials-needed cousin: accounts with Kerberos
# pre-authentication disabled can have an encrypted blob requested for them by anyone
# who can reach the KDC.
Get-ADxUser -Filter 'DoesNotRequirePreAuth -eq $true' -Properties PasswordLastSet, adminCount |
    Select-Object Name, SamAccountName, Enabled, PasswordLastSet,
                  @{N='Privileged'; E={ [bool]$_.adminCount }} |
    Format-Table -AutoSize

<#
Sample output

Name        SamAccountName SPNs PwdAgeDays Privileged PasswordNeverExpires
----        -------------- ---- ---------- ---------- --------------------
svc-sql     svc-sql           4       1982       True                 True
svc-web     svc-web           2        412      False                 True
svc-legacy  svc-legacy        1       1601      False                 True
svc-deploy  svc-deploy        1         88      False                False

4 enabled accounts with an SPN

Name            : svc-sql
SamAccountName  : svc-sql
PwdAgeDays      : 1982
SPN             : MSSQLSvc/sql01.corp.contoso.com:1433; MSSQLSvc/sql01.corp.contoso.com;
                  MSSQLSvc/sql02.corp.contoso.com:1433; MSSQLSvc/sql02.corp.contoso.com

Name       SamAccountName Enabled PasswordLastSet     Privileged
----       -------------- ------- ---------------     ----------
svc-legacy svc-legacy        True 2022-01-11 10:05:19      False

svc-sql is the finding. A password set 1,982 days ago, never expiring, on an account
in a protected group - anyone who can authenticate to the domain can request a ticket
for it and crack it offline for as long as they like. Nothing in this script does that;
it just tells you the door is there.

The fix is a long random password (or a group Managed Service Account, which rotates
its own 240-byte secret), not removing the SPN - the SPN is what makes the service
work.
#>
