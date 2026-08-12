# What operating systems are actually running in this domain?
#
# operatingSystem is self-reported by each machine at join time and refreshed on
# upgrade, so it is a good census and a poor audit. It is still the fastest answer to
# "do we still have 2012 R2 anywhere" that exists, and it costs one sweep.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# Census of enabled machines, newest-seen first.
Get-ADxComputer -Filter 'Enabled -eq $true' `
                -Properties OperatingSystem, OperatingSystemVersion, LastLogonDate |
    Group-Object OperatingSystem |
    Select-Object @{N='OperatingSystem'; E={ if ($_.Name) { $_.Name } else { '(not reported)' } }},
                  Count,
                  @{N='LastSeen'; E={ ($_.Group.LastLogonDate | Measure-Object -Maximum).Maximum }} |
    Sort-Object Count -Descending | Format-Table -AutoSize

# Servers only, with the build number, which is the part that tells you the patch era.
Get-ADxComputer -Filter "OperatingSystem -like '*Server*'" `
                -Properties OperatingSystem, OperatingSystemVersion, DNSHostName, LastLogonDate |
    Select-Object Name, DNSHostName, OperatingSystem, OperatingSystemVersion, LastLogonDate |
    Sort-Object OperatingSystem, Name | Format-Table -AutoSize

# Anything out of support. -like is a server-side substring match on a string
# attribute; the DC does the filtering.
$eol = @('*Windows 7*', '*Windows 8*', '*Server 2008*', '*Server 2012*', '*Windows XP*')
$eol | ForEach-Object {
    Get-ADxComputer -Filter "OperatingSystem -like '$_'" `
                    -Properties OperatingSystem, LastLogonDate |
        Select-Object Name, OperatingSystem, Enabled, LastLogonDate
} | Format-Table -AutoSize

<#
Sample output

OperatingSystem              Count LastSeen
---------------              ----- --------
Windows 10 Enterprise          412 2026-08-11 08:04:19
Windows 11 Enterprise          186 2026-08-11 08:07:52
Windows Server 2022 Standard    34 2026-08-11 08:09:03
Windows Server 2019 Standard    11 2026-08-11 07:58:40
(not reported)                   7 2026-08-09 22:13:05
Windows Server 2012 R2           2 2025-09-30 14:47:53

Name    DNSHostName             OperatingSystem              OperatingSystemVersion LastLogonDate
----    -----------             ---------------              ---------------------- -------------
DC1     dc1.corp.contoso.com    Windows Server 2022 Standard 10.0 (20348)           2026-08-11 08:09:03
DC2     dc2.corp.contoso.com    Windows Server 2022 Standard 10.0 (20348)           2026-08-11 08:08:47
FS01    fs01.corp.contoso.com   Windows Server 2019 Standard 10.0 (17763)           2026-08-11 07:58:40
LAB-VM-11 (none)                Windows Server 2012 R2       6.3 (9600)             2025-09-30 14:47:53

Name      OperatingSystem        Enabled LastLogonDate
----      ---------------        ------- -------------
WS-OLD-04 Windows 7 Professional    True 2024-11-18 09:02:11
LAB-VM-11 Windows Server 2012 R2    True 2025-09-30 14:47:53

"(not reported)" is normal: non-Windows machines and some appliances join without
populating operatingSystem at all. Cross-check those against LastSeen before assuming
they are stale - a blank OS on a machine that authenticated an hour ago is a Linux box
doing its job, not an orphan.
#>
