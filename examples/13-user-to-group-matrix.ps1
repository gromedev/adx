# Export who is in what, as a flat table you can pivot.
#
# Two ways to build this, and they answer different questions:
#
#   per user, from memberOf   one sweep, cheap, DIRECT membership only
#   per group, from members   one query per group, and it can be made effective
#
# Both are here. Which one is correct depends on whether "member" in your report means
# "has the link" or "has the access". For access reviews it is the second one.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$outFile = './user-group-matrix.csv'

# --- Direct membership, one pass over the directory ---
# memberOf is a DN array, so it is always emitted as an array even with one value.
# Streaming straight into Export-Csv keeps memory flat regardless of directory size.
Get-ADxUser -Filter 'Enabled -eq $true' -Properties memberOf, Department |
    ForEach-Object {
        $user = $_
        if (-not $user.memberOf) {
            [PSCustomObject]@{ Sam = $user.SamAccountName; Department = $user.Department; Group = '(none)' }
            return
        }
        foreach ($groupDn in $user.memberOf) {
            [PSCustomObject]@{
                Sam        = $user.SamAccountName
                Department = $user.Department
                # The RDN is enough for a report; keep the DN if you need to join on it.
                Group      = ($groupDn -split ',')[0] -replace '^CN='
            }
        }
    } |
    Export-Csv $outFile -NoTypeInformation -Encoding utf8

"Wrote $outFile ($((Import-Csv $outFile).Count) rows)"

# --- Effective membership, per group ---
# Slower (one query per group) but it resolves nesting AND primary-group membership,
# so a user who reaches the group through three levels of nesting still appears.
Get-ADxGroup -Filter "Name -like 'SG-*'" | ForEach-Object {
    $group = $_
    $direct    = @(Get-ADxGroupMember $group).Count
    $effective = @(Get-ADxGroupMember $group -Recursive).Count
    [PSCustomObject]@{
        Group     = $group.Name
        Scope     = $group.GroupScope
        Direct    = $direct
        Effective = $effective
        Inherited = $effective - $direct
    }
} | Sort-Object Inherited -Descending | Format-Table -AutoSize

<#
Sample output

Wrote ./user-group-matrix.csv (9418 rows)

Group             Scope       Direct Effective Inherited
-----             -----       ------ --------- ---------
SG-Server-Admins  Global           2        14        12
SG-Tier1          Global           3         9         6
SG-AllStaff       Universal     1980      1980         0
SG-Finance        Global          24        24         0
SG-Backup-Ops     DomainLocal      1         1         0

The Inherited column is the reason to run the slow version. SG-Server-Admins has two
direct members and fourteen effective ones - twelve people hold server-admin access
without appearing anywhere in the group's member list, and without it appearing in
their memberOf either. The CSV from the first half of this script shows those two, not
the fourteen.

Neither number is wrong; they answer different questions. Just be sure the report says
which one it is.
#>
