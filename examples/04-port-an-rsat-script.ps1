# Port an existing RSAT script by search-replacing the command name.
#
# Get-ADUser -> Get-ADxUser, Get-ADGroup -> Get-ADxGroup, Get-ADComputer ->
# Get-ADxComputer, Get-ADObject -> Get-ADxObject, Get-ADGroupMember ->
# Get-ADxGroupMember, Get-ADRootDSE -> Get-ADxRootDse. The filter language, identity
# forms, parameter names, output property names and default property sets all match,
# so nothing else in the script has to change.
#
# What follows is a real-shaped RSAT report, unmodified except for the command names.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$cutoff = (Get-Date).AddDays(-90)

# --- was: Get-ADUser -Filter ... ---
$report = Get-ADxUser -Filter 'Enabled -eq $true -and LastLogonDate -lt $cutoff' `
                      -Properties LastLogonDate, Department, Manager, EmailAddress |
    ForEach-Object {
        [PSCustomObject]@{
            Name          = $_.Name
            Sam           = $_.SamAccountName
            Department    = $_.Department
            LastLogonDate = $_.LastLogonDate
            DaysIdle      = if ($_.LastLogonDate) { [int]((Get-Date) - $_.LastLogonDate).TotalDays } else { $null }
            Manager       = $_.Manager
        }
    }

$report | Sort-Object DaysIdle -Descending | Format-Table -AutoSize

# --- was: Get-ADGroup / Get-ADGroupMember ---
Get-ADxGroup -Filter "Name -like 'SG-*'" -Properties Description |
    ForEach-Object {
        [PSCustomObject]@{
            Group   = $_.Name
            Scope   = $_.GroupScope
            Members = @(Get-ADxGroupMember $_.DistinguishedName).Count
        }
    } | Format-Table -AutoSize

<#
Sample output

Name           Sam      Department  LastLogonDate       DaysIdle Manager
----           ---      ----------  -------------       -------- -------
Peter Novak    pnovak   Logistics   2025-11-02 08:12:44      282 CN=Ida Berg,OU=Users,...
Ana Ruiz       aruiz    Sales       2026-01-19 17:03:51      204 CN=Jane Doe,OU=Users,...
Tom Fisher     tfisher  Support     2026-03-30 09:55:02      134 CN=Ida Berg,OU=Users,...

Group        Scope    Members
-----        -----    -------
SG-Finance   Global        24
SG-Sales     Global        63
SG-Tier0     Universal      4

One divergence to know about before you trust a ported script:

  -Filter is a variable-aware translator, not a string. Quote it SINGLY - 'LastLogonDate
  -lt $cutoff' - so the DateTime reaches the translator as a DateTime and is marshalled
  to the FILETIME that lastLogonTimestamp actually stores. Double quotes interpolate it
  into a culture-formatted string first, which is fragile at best.

  RSAT has the same property, but tolerates more, because it hands the expression to
  ADWS rather than compiling it. ADx compiles it, which is also why a misspelled
  property name is an error here instead of a query that quietly matches nothing.

And one caveat, per the README: lastLogonTimestamp replicates with up to 14 days of
staleness. DaysIdle is therefore approximate - identically so under RSAT.
#>
