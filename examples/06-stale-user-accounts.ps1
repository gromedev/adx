# Find accounts nobody has used, filtered at the domain controller.
#
# The comparison happens server-side: "LastLogonDate -lt $cutoff" is translated to a
# FILETIME range test on lastLogonTimestamp, so the DC never sends the rows that are
# about to be discarded. Doing it client-side - pulling every user and filtering in
# PowerShell - is the most common way an AD report becomes slow.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$cutoff = (Get-Date).AddDays(-90)

# Single quotes, so $cutoff reaches the translator as a DateTime rather than as a
# culture-formatted string. It is marshalled to the FILETIME lastLogonTimestamp stores.
$stale = Get-ADxUser -Filter 'Enabled -eq $true -and LastLogonDate -lt $cutoff' `
                     -Properties LastLogonDate, PasswordLastSet, Department |
    Select-Object Name, SamAccountName, Department, LastLogonDate, PasswordLastSet,
                  @{N='DaysIdle'; E={ [int]((Get-Date) - $_.LastLogonDate).TotalDays }}

$stale | Sort-Object DaysIdle -Descending | Format-Table -AutoSize
"$($stale.Count) enabled accounts idle for 90+ days"

# Accounts that have NEVER logged on are a separate case: they have no
# lastLogonTimestamp at all, so no range comparison can find them. Absence is a
# presence test, which -Filter deliberately refuses to fake on a FileTime attribute
# (see example 18) - so this one goes through -LDAPFilter.
Get-ADxUser -LDAPFilter '(!(lastLogonTimestamp=*))' -Properties whenCreated |
    Select-Object Name, SamAccountName, Enabled, whenCreated |
    Sort-Object whenCreated | Format-Table -AutoSize

<#
Sample output

Name         SamAccountName Department LastLogonDate       PasswordLastSet     DaysIdle
----         -------------- ---------- -------------       ---------------     --------
Peter Novak  pnovak         Logistics  2025-11-02 08:12:44 2025-04-14 11:20:07      282
Ana Ruiz     aruiz          Sales      2026-01-19 17:03:51 2025-09-01 08:44:12      204
Tom Fisher   tfisher        Support    2026-03-30 09:55:02 2026-02-11 13:02:55      134

3 enabled accounts idle for 90+ days

Name          SamAccountName Enabled whenCreated
----          -------------- ------- -----------
svc-backup    svc-backup        True 2024-06-02 09:14:33
Contractor 07 ctr07            False 2026-02-20 16:51:09

A caveat that applies equally to RSAT, because it is a property of the attribute
rather than of either tool: lastLogonTimestamp replicates lazily, with up to 14 days
of staleness by default. It is the right attribute for "has this account been idle for
months"; it is the wrong one for "did this account log on this morning". For that you
need lastLogon, which is NOT replicated and must be read from every DC and maxed.
#>
