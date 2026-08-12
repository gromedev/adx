# Who actually holds privilege in this domain?
#
# The honest answer is not the member list of Domain Admins. It is the effective
# membership of every Tier-0 group, through any depth of nesting, including principals
# whose only route in is a primaryGroupID - which no link-walking rule can see.
#
# -Recursive gives you that. Note that it can therefore return MORE than RSAT's
# Get-ADGroupMember -Recursive; the block at the bottom explains exactly when and why.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$tier0 = @(
    'Domain Admins', 'Enterprise Admins', 'Schema Admins', 'Administrators',
    'Account Operators', 'Backup Operators', 'Server Operators', 'Print Operators'
)

$findings = foreach ($groupName in $tier0) {
    $group = Get-ADxGroup $groupName -ErrorAction SilentlyContinue
    if (-not $group) { continue }

    foreach ($member in Get-ADxGroupMember $group -Recursive -Properties EmailAddress, LastLogonDate, Enabled) {
        [PSCustomObject]@{
            Group         = $groupName
            Principal     = $member.SamAccountName
            Name          = $member.Name
            ObjectClass   = $member.ObjectClass
            Enabled       = $member.Enabled
            LastLogonDate = $member.LastLogonDate
        }
    }
}

$findings | Sort-Object Group, Principal | Format-Table -AutoSize

# Accounts holding privilege from more than one direction are the ones worth a
# conversation - and disabled accounts inside Tier-0 are worth a ticket.
$findings | Group-Object Principal | Where-Object Count -gt 1 |
    Select-Object @{N='Principal'; E={$_.Name}},
                  @{N='Groups';    E={ ($_.Group.Group | Sort-Object -Unique) -join ', ' }} |
    Format-Table -AutoSize

$findings | Where-Object { -not $_.Enabled } |
    Select-Object Group, Principal, Name | Format-Table -AutoSize

# The other half of the picture: adminCount 1 marks accounts that ARE or WERE in a
# protected group. A 1 on an account that is in none of the groups above is a leftover
# from privilege that was removed - AdminSDHolder does not reset the flag.
$current = $findings.Principal | Sort-Object -Unique
Get-ADxUser -Filter 'adminCount -ge 1' -Properties adminCount, LastLogonDate |
    Where-Object { $_.SamAccountName -notin $current } |
    Select-Object Name, SamAccountName, Enabled, LastLogonDate |
    Format-Table -AutoSize

<#
Sample output

Group             Principal   Name              ObjectClass Enabled LastLogonDate
-----             ---------   ----              ----------- ------- -------------
Administrators    Administrator Administrator   user           True 2026-08-11 07:14:22
Administrators    iberg       Ida Berg          user           True 2026-08-11 07:22:41
Domain Admins     Administrator Administrator   user           True 2026-08-11 07:14:22
Domain Admins     iberg       Ida Berg          user           True 2026-08-11 07:22:41
Domain Admins     svc-legacy  svc-legacy        user          False 2024-03-02 11:41:08
Enterprise Admins Administrator Administrator   user           True 2026-08-11 07:14:22
Server Operators  svc-backup  svc-backup        user           True 2026-08-11 06:00:12

Principal     Groups
---------     ------
Administrator Administrators, Domain Admins, Enterprise Admins
iberg         Administrators, Domain Admins

Group         Principal  Name
-----         ---------  ----
Domain Admins svc-legacy svc-legacy

Name        SamAccountName Enabled LastLogonDate
----        -------------- ------- -------------
Peter Novak pnovak            True 2025-11-02 08:12:44

Read the last table carefully: pnovak is in none of the Tier-0 groups, but still
carries adminCount 1. That means the account WAS privileged. When it was removed,
AdminSDHolder left the flag - and, more importantly, left the account's ACL
disinherited. It is a former admin account that still looks like one to anything
keying off adminCount.

ON -Recursive AND RSAT PARITY

Get-ADxGroupMember -Recursive reports EFFECTIVE membership and can return
substantially more than RSAT does. Measured on the lab domain of 3,731 users, for
BUILTIN\Users: RSAT returns 100, ADx returns 3,733.

Neither tool is malfunctioning. Ordinary nesting behaves identically in both. The
divergence is confined to primary group membership, which AD does not store in a
group's member attribute at all - it is a primaryGroupID number on the user, and
nearly every account belongs to Domain Users that way. RSAT resolves primaryGroupID
for the group you name, but not for groups nested inside it. So RSAT reports all
3,731 users for "Domain Users" and 100 for "Users -Recursive", even though Domain
Users is a member of BUILTIN\Users - it contradicts itself between those two answers.
ADx is consistent across both, which for a privilege review is the answer you want.

If you need byte-identical RSAT output, run -Recursive against the specific group
rather than a parent that nests it.
#>
