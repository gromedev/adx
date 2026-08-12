# Inventory the accounts whose passwords Active Directory itself owns.
#
# Managed service accounts exist to eliminate the worst password in any domain: the
# service account one that nobody has rotated since it was created. For an MSA the DC
# generates a 240-byte random password and rotates it on schedule; no human ever
# knows it. A standalone MSA (sMSA) is bound to ONE computer; a group-managed MSA
# (gMSA) derives its password from the forest's KDS root key, so any authorised host
# can fetch it.
#
# The class model is why one cmdlet covers both: the gMSA class DERIVES from the sMSA
# class, so the base-class filter (objectClass=msDS-ManagedServiceAccount) matches
# both and nothing else. It is also why Get-ADxUser and Get-ADxComputer deliberately
# REJECT MSAs - by -Filter and by DN alike - and point you here, exactly as RSAT
# does: an object that answered two different presets would appear or vanish
# depending on which report ran.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- The full inventory, gMSA and sMSA in one pass ---
# ObjectClass tells them apart: the derived class means gMSA, the base class means a
# standalone sMSA.
Get-ADxServiceAccount -Filter * -Properties PasswordLastSet, LastLogonDate |
    Select-Object Name, SamAccountName, Enabled,
                  @{N='Type'; E={ if ($_.ObjectClass -eq 'msDS-GroupManagedServiceAccount') { 'gMSA' } else { 'sMSA' } }},
                  PasswordLastSet, LastLogonDate |
    Sort-Object Type, Name | Format-Table -AutoSize

# --- One account, by any identity form ---
# DN, objectGUID, SID, or sAMAccountName. An MSA's sAMAccountName ends in $, which
# nobody types - the suffix is retried automatically, same as Get-ADxComputer.
Get-ADxServiceAccount gmsa-web -Properties ServicePrincipalNames, ManagedPasswordIntervalInDays |
    Select-Object Name, SamAccountName, Enabled, ObjectClass,
                  ServicePrincipalNames, ManagedPasswordIntervalInDays |
    Format-List

# --- Which services they actually carry ---
# An SPN on an MSA is the fix for example 20's kerberoastable svc-* users: the ticket
# is still there to request, but it is encrypted with 240 bytes of random key
# rotated monthly. Offline cracking stops being a plan.
Get-ADxServiceAccount -Filter * -Properties ServicePrincipalNames |
    Where-Object { $_.ServicePrincipalNames.Count -gt 0 } |
    Select-Object Name, @{N='SPNs'; E={ $_.ServicePrincipalNames -join ', ' }} |
    Format-Table -AutoSize

# --- Which computer an sMSA is bound to ---
# HostComputers is the msDS-HostServiceAccountBL back-link: plain DNs, readable from
# the account side. (gMSAs have no such link - their authorisation is the ACL below.)
Get-ADxServiceAccount -Filter * -Properties HostComputers |
    Where-Object { $_.ObjectClass -eq 'msDS-ManagedServiceAccount' } |
    Select-Object Name, @{N='BoundTo'; E={ $_.HostComputers -join ', ' }} |
    Format-Table -AutoSize

# --- Rotation as a decommissioning signal ---
# A gMSA's password rotates (default every 30 days, ManagedPasswordIntervalInDays)
# when an authorised host fetches the next one. PasswordLastSet far beyond the
# interval therefore means NO host has asked - the strongest "nothing uses this
# anymore" signal available without touching a single server.
$cutoff = (Get-Date).AddDays(-60)
Get-ADxServiceAccount -Filter * -Properties PasswordLastSet, ManagedPasswordIntervalInDays |
    Where-Object { $_.ObjectClass -eq 'msDS-GroupManagedServiceAccount' -and $_.PasswordLastSet -lt $cutoff } |
    Select-Object Name, PasswordLastSet, ManagedPasswordIntervalInDays |
    Format-Table -AutoSize

# One declared gap to know about: PrincipalsAllowedToRetrieveManagedPassword - WHO
# may fetch a gMSA's password - is unsupported. The underlying msDS-GroupMSAMembership
# is a security descriptor whose trustees RSAT resolves to principals, an ACE walk
# ADx does not do. Asking for it is a terminating error that says so, never a
# silently null column that looks like "nobody".

<#
Sample output

Name        SamAccountName Enabled Type PasswordLastSet     LastLogonDate
----        -------------- ------- ---- ---------------     -------------
gmsa-legacy gmsa-legacy$      True gMSA 2024-11-20 03:12:44 2024-12-01 09:41:20
gmsa-sql    gmsa-sql$         True gMSA 2026-07-28 03:00:11 2026-08-11 07:58:02
gmsa-web    gmsa-web$         True gMSA 2026-08-02 03:00:09 2026-08-11 07:59:44
svc-scan    svc-scan$         True sMSA 2026-07-15 02:41:37 2026-08-11 05:12:19

Name                          : gmsa-web
SamAccountName                : gmsa-web$
Enabled                       : True
ObjectClass                   : msDS-GroupManagedServiceAccount
ServicePrincipalNames         : {HTTP/webfarm.corp.contoso.com, HTTP/webfarm}
ManagedPasswordIntervalInDays : 30

Name     SPNs
----     ----
gmsa-sql MSSQLSvc/sql1.corp.contoso.com:1433, MSSQLSvc/sql1.corp.contoso.com
gmsa-web HTTP/webfarm.corp.contoso.com, HTTP/webfarm

Name     BoundTo
----     -------
svc-scan CN=FS01,OU=Servers,DC=corp,DC=contoso,DC=com

Name        PasswordLastSet     ManagedPasswordIntervalInDays
----        ---------------     -----------------------------
gmsa-legacy 2024-11-20 03:12:44                            30

Read the last table against the first: gmsa-legacy's password was last set in
November 2024 on a 30-day interval. Rotation is demand-driven - the DC computes a new
password only when an authorised host retrieves it - so twenty missed intervals means
no host has authenticated as this account since then. That is a decommissioning
ticket, not a malfunction; the account itself is still enabled and still carries
whatever group memberships it always had (example 28 lists them).

The inventory above is also the migration worklist for example 20: every svc-* USER
account with an SPN and a years-old password is a candidate to become a row in this
report instead.
#>
