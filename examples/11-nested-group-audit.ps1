# Answer "what does membership of this group actually grant?"
#
# Groups nest. A user in SG-Helpdesk may be in SG-Tier1 may be in SG-Server-Admins,
# and nothing on the user says so. Get-ADxGroupNested flattens the whole tree in ONE
# query using matching rule 1.2.840.113556.1.4.1941, so nesting of any depth costs a
# single round trip instead of a recursive loop of Get-ADGroupMember calls.
#
# RSAT has no counterpart to this cmdlet.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$target = 'SG-Server-Admins'

# Every group inside the target, at any depth, flattened.
Get-ADxGroupNested $target |
    Select-Object Name, GroupScope, GroupCategory, DistinguishedName |
    Format-Table -AutoSize

# Nested groups pipe straight back in - ADx output carries a DistinguishedName, and
# -Identity accepts any object that does. So "who is in each of them" is one pipe.
Get-ADxGroupNested $target | ForEach-Object {
    $group = $_
    Get-ADxGroupMember $group | ForEach-Object {
        [PSCustomObject]@{
            Principal   = $_.SamAccountName
            ObjectClass = $_.ObjectClass
            ViaGroup    = $group.Name
        }
    }
} | Sort-Object Principal | Format-Table -AutoSize

# The reverse question, per user: every group behind an account, including inherited
# ones. memberOf alone gives only the direct links; -recursivematch walks the chain.
$dn = (Get-ADxUser jdoe).DistinguishedName
Get-ADxGroup -Filter 'member -recursivematch $dn' |
    Select-Object Name, GroupScope, DistinguishedName | Format-Table -AutoSize

<#
Sample output

Name             GroupScope GroupCategory DistinguishedName
----             ---------- ------------- -----------------
SG-Tier1         Global     Security      CN=SG-Tier1,OU=Groups,DC=corp,DC=contoso,DC=com
SG-Helpdesk      Global     Security      CN=SG-Helpdesk,OU=Groups,DC=corp,DC=contoso,DC=com
SG-Backup-Ops    DomainLocal Security     CN=SG-Backup-Ops,OU=Groups,DC=corp,DC=contoso,DC=com

Principal  ObjectClass ViaGroup
---------  ----------- --------
aruiz      user        SG-Helpdesk
iberg      user        SG-Tier1
jdoe       user        SG-Helpdesk
svc-backup user        SG-Backup-Ops
tfisher    user        SG-Helpdesk

Name             GroupScope DistinguishedName
----             ---------- -----------------
SG-Helpdesk      Global     CN=SG-Helpdesk,OU=Groups,DC=corp,DC=contoso,DC=com
SG-Tier1         Global     CN=SG-Tier1,OU=Groups,DC=corp,DC=contoso,DC=com
SG-Server-Admins Global     CN=SG-Server-Admins,OU=Groups,DC=corp,DC=contoso,DC=com
Domain Users     Global     CN=Domain Users,CN=Users,DC=corp,DC=contoso,DC=com

The third table is the one to internalise. jdoe is a DIRECT member of SG-Helpdesk
only. The other three arrive through nesting, and none of them appear in the user's
memberOf attribute. Any access review that reads memberOf and stops there understates
what the account can do - here by three groups out of four, one of which is the
server-admin group the audit was about.

Chasing that by hand means recursive Get-ADGroupMember calls and cycle detection.
Rule 1941 does it server-side, and terminates on membership cycles by construction -
verified on a live DC against a deliberately cyclic group pair.
#>
