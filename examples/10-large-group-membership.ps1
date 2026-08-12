# Enumerate a group's members completely - including the ones AD hides in two
# different ways.
#
# Naive membership collectors are wrong twice over, and both failures are silent:
#
#   1. MaxValRange. Past 1,500 values AD does not truncate the member attribute, it
#      RENAMES it: "member;range=0-1499". A collector that reads $group.member gets
#      $null and reports the group as empty.
#
#   2. Primary group membership. A user whose primaryGroupID points at the group is a
#      member, but AD creates no member/memberOf link for it. Nearly every account in
#      the domain is in Domain Users this way, so a member-attribute read of Domain
#      Users returns almost nobody.
#
# Get-ADxGroupMember handles both: it enumerates by searching memberOf, so MaxValRange
# does not apply, and it reconciles primaryGroupID against the group's SID.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# Domain Users: the case that breaks a member-attribute read entirely.
$viaMemberAttribute = @((Get-ADxGroup 'Domain Users' -Properties Members).Members).Count
$viaGroupMember     = @(Get-ADxGroupMember 'Domain Users').Count

[PSCustomObject]@{
    'member attribute'    = $viaMemberAttribute
    'Get-ADxGroupMember'  = $viaGroupMember
} | Format-List

# Members by type. A member can be a user, computer, group or contact, so the default
# property set is RSAT's ADPrincipal set - the intersection meaningful for all of them.
Get-ADxGroupMember 'SG-AllEngineering' |
    Group-Object ObjectClass | Select-Object Name, Count | Format-Table -AutoSize

# Extra properties on members work, and are fetched in the same pass.
Get-ADxGroupMember 'SG-Tier0' -Properties EmailAddress, LastLogonDate, Enabled |
    Select-Object Name, SamAccountName, ObjectClass, Enabled, EmailAddress, LastLogonDate |
    Format-Table -AutoSize

# A group large enough to cross MaxValRange (1,500 by default), read three ways.
# The two cmdlets have deliberately different contracts here:
#
#   Get-ADxGroup -Properties Members   completes the range walk transparently, because
#                                      RSAT returns a complete Members collection
#   Search-ADxObject -Property member  flags the truncation and does NOT fetch the
#                                      rest, because it is the raw transport primitive
$big = 'SG-AllStaff'
$bigDn = (Get-ADxGroup $big).DistinguishedName

$complete = Get-ADxGroup $big -Properties Members
$raw = Search-ADxObject '(objectClass=*)' -SearchBase $bigDn -Scope Base -Property member

[PSCustomObject]@{
    Group                  = $big
    'Get-ADxGroup Members' = @($complete.Members).Count
    'Search-ADxObject'     = @($raw.member).Count
    'memberTruncated'      = [bool]$raw.memberTruncated
    'memberRangeHigh'      = $raw.memberRangeHigh
    'Get-ADxGroupMember'   = @(Get-ADxGroupMember $big).Count
} | Format-List

<#
Sample output

member attribute   : 0
Get-ADxGroupMember : 3731

Name     Count
----     -----
user       618
computer    24
group        3

Name        SamAccountName ObjectClass Enabled EmailAddress          LastLogonDate
----        -------------- ----------- ------- ------------          -------------
Ida Berg    iberg          user           True ida.berg@contoso.com  2026-08-11 07:22:41
svc-deploy  svc-deploy     user           True                       2026-08-11 06:00:12
DC1         DC1$           computer       True                       2026-08-11 07:59:03

Group                  : SG-AllStaff
Get-ADxGroup Members   : 1980
Search-ADxObject       : 1500
memberTruncated        : True
memberRangeHigh        : 1499
Get-ADxGroupMember     : 1980

Read the first block again: the member attribute of Domain Users contains ZERO
entries, and the group has 3,731 members. Nothing is broken - that is simply not where
AD stores this membership. A collector that trusts the attribute reports an empty
group and moves on.

The last block is the other failure, and the two contracts side by side. The DC
returned 1,500 of 1,980 members - AD's MaxValRange default - under the renamed key
"member;range=0-1499". Get-ADxGroup finished the walk and returned all 1,980, because
RSAT's Members is a complete collection and ported scripts depend on that.
Search-ADxObject returned the 1,500 it was given and set memberTruncated, because it
is the raw primitive and hiding extra round trips inside it would be the wrong default
there. Neither one hands you a short list that looks complete - which is what a naive
$group.member read does, silently.

Known limitation, multi-domain forests: membership is enumerated within the target
group's own domain partition, so members from other domains are not returned. RSAT
resolves those by walking each member DN individually. See Get-Help Get-ADxGroupMember
example 4 for the workaround; single-domain forests are unaffected.
#>
