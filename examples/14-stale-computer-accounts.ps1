# The classic stale-machine sweep: computer accounts that stopped authenticating.
#
# A computer account changes its own password every 30 days by default, so an account
# that has not authenticated in 60+ days is almost certainly a machine that no longer
# exists. Each one is a live credential and an ACL entry for hardware nobody can find.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$cutoff = (Get-Date).AddDays(-60)

$stale = Get-ADxComputer -Filter 'Enabled -eq $true -and LastLogonDate -lt $cutoff' `
                         -Properties LastLogonDate, OperatingSystem, whenCreated, PasswordLastSet |
    Select-Object Name, DNSHostName, OperatingSystem, LastLogonDate, PasswordLastSet,
                  @{N='DaysIdle'; E={ [int]((Get-Date) - $_.LastLogonDate).TotalDays }},
                  DistinguishedName

$stale | Sort-Object DaysIdle -Descending |
    Format-Table Name, OperatingSystem, LastLogonDate, DaysIdle -AutoSize

"$($stale.Count) enabled computer accounts idle for 60+ days"

# Group them by OU, because that is usually how they get cleaned up.
$stale | Group-Object { ($_.DistinguishedName -split ',', 2)[1] } |
    Select-Object @{N='OU'; E={$_.Name}}, Count |
    Sort-Object Count -Descending | Format-Table -AutoSize

# Export a work list. Disabling before deleting is the reversible order of operations;
# ADx is read-only, so the write half is RSAT's or ADSI's job.
$stale | Export-Csv './stale-computers.csv' -NoTypeInformation -Encoding utf8

<#
Sample output

Name       OperatingSystem              LastLogonDate       DaysIdle
----       ---------------              -------------       --------
WS-OLD-04  Windows 7 Professional       2024-11-18 09:02:11      631
LAB-VM-11  Windows Server 2012 R2       2025-09-30 14:47:53      315
WS-2201    Windows 10 Enterprise        2026-04-02 08:19:24      131
KIOSK-03   Windows 10 IoT Enterprise    2026-05-28 17:55:40       75

4 enabled computer accounts idle for 60+ days

OU                                              Count
--                                              -----
OU=Workstations,DC=corp,DC=contoso,DC=com           2
OU=Lab,OU=Servers,DC=corp,DC=contoso,DC=com         1
OU=Kiosks,DC=corp,DC=contoso,DC=com                 1

The Windows 7 machine has been gone for 631 days and its account is still enabled.
That is the normal finding, and it is why this report exists.

Two notes on identity, since computers are the awkward case:

  The stored sAMAccountName ends in '$' - WS-2201$ - which nobody types. Get-ADxComputer
  retries with the suffix automatically, so Get-ADxComputer WS-2201 works.

  Managed service accounts derive from the computer class but carry their own
  objectCategory, so (objectCategory=computer) excludes them - the same way RSAT points
  you at Get-ADServiceAccount. They will not appear in this report.
#>
