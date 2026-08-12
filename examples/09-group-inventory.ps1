# Inventory every group by scope and category, filtered server-side.
#
# GroupScope (DomainLocal/Global/Universal/BuiltinLocal) and GroupCategory
# (Security/Distribution) are not stored attributes - both are packed into the bits of
# groupType. ADx decodes them on output AND compiles them back into bitwise matching
# rules on input, so "GroupScope -eq 'Universal'" is answered by the DC rather than by
# a Where-Object after the fact.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# The whole population, cross-tabulated.
Get-ADxGroup -Filter * |
    Group-Object GroupCategory, GroupScope |
    Select-Object @{N='Category,Scope'; E={$_.Name}}, Count |
    Sort-Object Count -Descending | Format-Table -AutoSize

# One cell of that table, selected on the wire:
#   (&(objectCategory=group)(&(groupType:...803:=2147483648)(groupType:...803:=8)))
Get-ADxGroup -Filter "GroupCategory -eq 'Security' -and GroupScope -eq 'Universal'" `
             -Properties Description, whenCreated |
    Select-Object Name, SamAccountName, Description, whenCreated |
    Format-Table -AutoSize

# Groups nobody is in. Emptiness is an absence test on a DN-valued attribute, which
# -Filter refuses to fake with -notlike (see example 18), so this uses -LDAPFilter.
Get-ADxGroup -LDAPFilter '(!(member=*))' -Properties Description, whenCreated |
    Select-Object Name, GroupScope, whenCreated, Description |
    Sort-Object whenCreated | Format-Table -AutoSize

<#
Sample output

Category,Scope           Count
--------------           -----
Security, Global           184
Security, DomainLocal       71
Distribution, Universal     33
Security, Universal         12
Distribution, Global         6

Name           SamAccountName Description                     whenCreated
----           -------------- -----------                     -----------
SG-Tier0       SG-Tier0       Tier-0 administrative access    2024-02-11 09:31:44
UG-AllStaff    UG-AllStaff    Everyone, all sites             2023-06-02 14:07:19

Name             GroupScope  whenCreated         Description
----             ----------  -----------         -----------
SG-Project-Apollo Global     2024-09-14 11:02:38 Retired project, kept for audit
SG-Temp-Migration Global     2025-01-30 08:45:11

BuiltinLocal is worth a note. Builtin groups (BUILTIN\Users, BUILTIN\Administrators)
carry groupType 0x80000005, which sets BOTH the builtin-local and the domain-local
bit. ADx reports them as DomainLocal, matching RSAT's ADGroupScope enum - which has
no builtin member - and agreeing with the filter that selects them. An earlier version
decoded the low nibble as a whole and reported Unknown for every builtin group; that
was fixed in 0.2.1.

Also note that "empty" here means no member links. It does NOT mean nobody is in the
group: Domain Users has almost no member links at all, because its membership is
carried by each user's primaryGroupID instead. Example 10 is about exactly that.
#>
