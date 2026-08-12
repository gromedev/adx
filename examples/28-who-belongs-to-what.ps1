# Which groups does this principal belong to? Membership, read from the other end.
#
# Examples 09-13 ask "who is in this group"; Get-ADxPrincipalGroupMembership asks
# the reverse - which groups hold this user, computer, group or service account as a
# DIRECT member. The same two membership-hiding mechanisms from example 10 apply,
# seen from the other side:
#
#   1. Primary group. Nearly every account is in Domain Users only through its
#      primaryGroupID attribute - no member link, no memberOf back-link, so a plain
#      memberOf read misses it. ADx reconstructs it the way RSAT does: take the
#      account's own SID, swap the RID for primaryGroupID, find the group with that
#      SID. If the SID or primaryGroupID cannot be read, the primary group is
#      omitted WITH a warning - never silently.
#
#   2. MaxValRange. Enumeration is a SEARCH of the groups' member attribute, not a
#      read of the principal's memberOf, so a principal in more than 1,500 groups
#      comes back complete instead of truncated at the range cap.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- A user's groups, primary group included ---
Get-ADxPrincipalGroupMembership jdoe | Sort-Object Name |
    Select-Object Name, GroupCategory, GroupScope, SamAccountName | Format-Table -AutoSize

# --- A computer's groups ---
# Any principal qualifies, in any identity form: DN, objectGUID, SID, or
# sAMAccountName. A computer's sAMAccountName is WS01$ - nobody types the $ - so the
# plain name is retried with the suffix automatically, as in Get-ADxComputer.
Get-ADxPrincipalGroupMembership WS01 |
    Select-Object Name, GroupScope | Format-Table -AutoSize

# --- A group's groups: one nesting step upward ---
# Groups are principals too. This answers "where does membership of SG-AllEngineering
# LEAD" - one level up; the full downward flattening is example 11's job.
Get-ADxPrincipalGroupMembership 'SG-AllEngineering' |
    Select-Object Name, GroupScope | Format-Table -AutoSize

# --- Pipe principals in, and decorate the groups that come out ---
# Binds on DistinguishedName, so any ADx object pipes straight through. -Properties
# names extra attributes of the GROUPS being returned, not of the principal.
Get-ADxUser jdoe | Get-ADxPrincipalGroupMembership -Properties Description, ManagedBy |
    Select-Object Name, Description, ManagedBy | Format-Table -AutoSize

# Direct is the contract, matching RSAT: direct memberships plus the primary group,
# NOT the transitive closure. "What does jdoe's membership actually GRANT" is the
# other direction - enumerate the target group with Get-ADxGroupMember -Recursive
# (example 12) and look for jdoe in the result.

# --- Multi-domain forests: the partition boundary, made audible ---
# A membership stored in ANOTHER domain's partition (a universal or domain-local
# group jdoe was added to over there) has no back-link in this one, so a
# single-partition search cannot return it. A Global Catalog (port 3268) replicates
# universal-group membership forest-wide, so against a GC the principal's own
# memberOf DOES surface such groups - and ADx names them in a warning rather than
# dropping them in silence. Single-domain forests never warn.
Get-ADxPrincipalGroupMembership jdoe -Server dc1.corp.contoso.com -Port 3268 |
    Select-Object Name, GroupScope | Format-Table -AutoSize

<#
Sample output

Name              GroupCategory GroupScope SamAccountName
----              ------------- ---------- --------------
Domain Users      Security      Global     Domain Users
SG-AllEngineering Security      Global     SG-AllEngineering
SG-VPN-Users      Security      Global     SG-VPN-Users

Name             GroupScope
----             ----------
Domain Computers Global
SG-LAPS-Managed  Global
SG-Workstations  Global

Name        GroupScope
----        ----------
SG-AllStaff Global

Name              Description               ManagedBy
----              -----------               ---------
Domain Users      All domain users
SG-AllEngineering Engineering department    CN=Ida Berg,OU=Users,OU=Engineering,DC=corp,DC=contoso,DC=com
SG-VPN-Users      Remote access via VPN

Name              GroupScope
----              ----------
Domain Users      Global
SG-AllEngineering Global
SG-VPN-Users      Global

Read the first table knowing that jdoe's memberOf attribute contains exactly TWO
values: SG-AllEngineering and SG-VPN-Users. Domain Users is nowhere in it - that
membership exists only as primaryGroupID 513 on jdoe's own object, and it is the row
a memberOf-based script loses. Losing it matters more than it looks: "member of
Domain Users" is what NTFS ACLs, share permissions and GPO security filtering
scoped to Domain Users actually test.

The GC query returned the same three rows here because the lab forest has one
domain, and one domain means no partition boundary to fall over - and no warning.
In a multi-domain forest the same call adds a warning naming each group in another
domain, e.g. CN=SG-EMEA-Shared,OU=Groups,DC=emea,DC=contoso,DC=com; those rows are
NOT in the output, and the warning is the contract for that - query the group's own
domain to retrieve them.

The upward step composes, if you need it: feeding this cmdlet's output back into
itself walks nesting one level at a time. But if the real question is effective
privilege, walk downward instead - example 12's -Recursive resolves the whole tree,
primaryGroupID routes included, in one query.
#>
