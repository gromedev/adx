# Kerberos delegation: which accounts can impersonate other people.
#
# Delegation lets a service act as the user who called it. There are three kinds, and
# they carry very different risk:
#
#   Unconstrained  the service can impersonate the caller to ANY service. It caches
#                  the caller's TGT in memory. Compromise the host, replay any TGT it
#                  has seen - including a Domain Admin's.
#   Constrained    limited to the SPNs listed in msDS-AllowedToDelegateTo.
#   Resource-based limited, and configured on the TARGET, which is what makes it easy
#                  to miss: nothing on the delegating account says so.
#
# Unconstrained delegation on anything that is not a domain controller is the finding.
#
# Requirements: PowerShell 7.5+, read access to the directory. Read-only throughout.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- Unconstrained (TRUSTED_FOR_DELEGATION, 0x80000) ---
# DCs have this by design; everything else on the list is worth a conversation.
Get-ADxComputer -Filter 'TrustedForDelegation -eq $true' `
                -Properties OperatingSystem, LastLogonDate, primaryGroupID |
    Select-Object Name, DNSHostName, OperatingSystem,
                  @{N='IsDomainController'; E={ $_.primaryGroupID -eq 516 }},
                  LastLogonDate |
    Format-Table -AutoSize

Get-ADxUser -Filter 'TrustedForDelegation -eq $true' -Properties ServicePrincipalNames |
    Select-Object Name, SamAccountName, Enabled,
                  @{N='SPNs'; E={ @($_.ServicePrincipalNames).Count }} |
    Format-Table -AutoSize

# --- Constrained (TRUSTED_TO_AUTH_FOR_DELEGATION = protocol transition, 0x1000000) ---
# msDS-AllowedToDelegateTo is not in the curated schema, so it needs the pass-through.
Get-ADxUser -Filter 'TrustedToAuthForDelegation -eq $true' `
            -Properties 'msDS-AllowedToDelegateTo', ServicePrincipalNames -AllowUnknownProperty |
    Select-Object Name, SamAccountName,
                  @{N='DelegatesTo'; E={ @($_.'msDS-AllowedToDelegateTo') -join '; ' }} |
    Format-List

# --- Resource-based (msDS-AllowedToActOnBehalfOfOtherIdentity) ---
# A security descriptor, so presence is the only useful test - see example 18.
Get-ADxComputer -LDAPFilter '(msDS-AllowedToActOnBehalfOfOtherIdentity=*)' `
                -Properties OperatingSystem |
    Select-Object Name, DNSHostName, OperatingSystem | Format-Table -AutoSize

# --- The mitigation, for contrast ---
# "Account is sensitive and cannot be delegated" (0x100000) exempts an account from
# every form of delegation. Privileged accounts should have it, and mostly do not.
$protected = @(Get-ADxUser -Filter 'AccountNotDelegated -eq $true -and adminCount -ge 1').Count
$privileged = @(Get-ADxUser -Filter 'adminCount -ge 1').Count

[PSCustomObject]@{
    'Privileged accounts'          = $privileged
    'Marked not-delegated'         = $protected
    'Delegatable privileged accts' = $privileged - $protected
} | Format-List

<#
Sample output

Name  DNSHostName            OperatingSystem              IsDomainController LastLogonDate
----  -----------            ---------------              ------------------ -------------
DC1   dc1.corp.contoso.com   Windows Server 2022 Standard               True 2026-08-11 08:09:03
DC2   dc2.corp.contoso.com   Windows Server 2022 Standard               True 2026-08-11 08:08:47
APP03 app03.corp.contoso.com Windows Server 2019 Standard              False 2026-08-11 07:41:55

Name SamAccountName Enabled SPNs
---- -------------- ------- ----

Name           : svc-web
SamAccountName : svc-web
DelegatesTo    : HTTP/intranet.corp.contoso.com; HTTP/intranet

Name  DNSHostName            OperatingSystem
----  -----------            ---------------
FS01  fs01.corp.contoso.com  Windows Server 2019 Standard

Privileged accounts          : 6
Marked not-delegated         : 1
Delegatable privileged accts : 5

APP03 is the finding: unconstrained delegation on a member server. Anyone who
compromises that host can replay the TGT of every user who has authenticated to it,
and if a Domain Admin ever browsed a share on it, that includes theirs. DC1 and DC2
holding the same flag is normal - primaryGroupID 516 is Domain Controllers.

The last block is the counterpart nobody runs: five of six privileged accounts can be
delegated. Marking them sensitive (or putting them in Protected Users) costs nothing
and closes the replay path above.
#>
