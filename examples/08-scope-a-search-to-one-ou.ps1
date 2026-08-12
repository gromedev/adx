# Restrict a query to part of the tree, and understand what the scope actually means.
#
# -SearchBase moves the root of the search; -SearchScope decides how deep it goes.
# Narrowing the base is the cheapest optimisation available: the DC never walks the
# subtrees you excluded, so it beats any client-side filter you could write.
#
#   Base      the search base object itself, nothing below it
#   OneLevel  immediate children only - no grandchildren
#   Subtree   the base and everything beneath it (the default)
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$salesOu = 'OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com'

# Everything under the OU, at any depth.
$subtree = @(Get-ADxUser -Filter * -SearchBase $salesOu)

# Only the objects sitting directly in it - a child OU's contents are excluded.
$oneLevel = @(Get-ADxUser -Filter * -SearchBase $salesOu -SearchScope OneLevel)

[PSCustomObject]@{ Subtree = $subtree.Count; OneLevel = $oneLevel.Count } | Format-Table -AutoSize

# A per-OU headcount, without pulling the directory twice. Enumerate the OUs first,
# then ask each one for its own users - each query is scoped, so each is cheap.
Get-ADxObject -Filter "ObjectClass -eq 'organizationalUnit'" |
    ForEach-Object {
        [PSCustomObject]@{
            OU      = $_.DistinguishedName
            Users   = @(Get-ADxUser -Filter * -SearchBase $_.DistinguishedName -SearchScope OneLevel).Count
            Enabled = @(Get-ADxUser -Filter 'Enabled -eq $true' -SearchBase $_.DistinguishedName -SearchScope OneLevel).Count
        }
    } |
    Where-Object Users -gt 0 | Sort-Object Users -Descending | Format-Table -AutoSize

<#
Sample output

Subtree OneLevel
------- --------
    812      147

OU                                                 Users Enabled
--                                                 ----- -------
OU=Contractors,OU=Users,OU=Sales,DC=corp,DC=cont...   665     603
OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com           147     147
OU=Users,OU=Engineering,DC=corp,DC=contoso,DC=com     644     631
OU=Service Accounts,DC=corp,DC=contoso,DC=com          38      31

Note what the first table says: 812 users live under OU=Users,OU=Sales, but only 147
sit directly in it. The other 665 are one level further down, in the Contractors OU.
Subtree found them; OneLevel did not. That is the whole difference, and it is the
usual cause of a headcount that disagrees with the org chart.

If -SearchBase names a DN that does not exist, the DC answers NoSuchObject rather
than an empty result, and ADx surfaces it as an ObjectNotFound error ending "Verify
-SearchBase names an object that exists in this domain." A typo'd OU is therefore
never silently reported as "zero users".
#>
