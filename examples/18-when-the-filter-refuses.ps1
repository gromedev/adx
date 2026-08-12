# The escape hatch, and why you need one.
#
# ADx will not translate a filter it cannot honour faithfully. It raises a terminating
# error instead - because Active Directory answers a structurally valid but WRONG
# filter with zero rows and a success code, and a report that silently says "no
# findings" is worse than any error message.
#
# So some perfectly reasonable questions are rejected by -Filter. Every one of them is
# expressible in raw LDAP, and -LDAPFilter takes you straight there. This example is
# the four cases you will actually hit.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# --- 1. Absence of a value ---
# "Users who have NEVER logged on" is an absence test, and lastLogonTimestamp is a
# FileTime, so -notlike '*' is not available:
#
#   Get-ADxUser -Filter 'lastLogonTimestamp -notlike "*"'
#   -> '-like'/'-notlike' only apply to string attributes; 'lastLogonTimestamp' is
#      FileTime-valued.
#
# In LDAP, absence is (!(attr=*)) and works on any syntax:
Get-ADxUser -LDAPFilter '(!(lastLogonTimestamp=*))' -Properties whenCreated |
    Select-Object Name, SamAccountName, Enabled, whenCreated | Format-Table -AutoSize

# --- 2. Substring matching on a DN-valued attribute ---
#
#   Get-ADxGroup -Filter 'Members -notlike "*"'
#   -> '-like' is not supported on 'Members': Active Directory cannot substring-match
#      DN-valued attributes (the query would silently match nothing). Use -eq with a
#      full DN, or -RecursiveMatch for group membership.
#
# Presence and absence still work in raw LDAP; substring genuinely does not exist:
Get-ADxGroup -LDAPFilter '(!(member=*))' | Select-Object Name, GroupScope | Format-Table -AutoSize

# --- 3. Binary attributes ---
# Resource-based constrained delegation lives in a security descriptor, so there is no
# text to match. Presence is the only sensible test, and it is the one that matters:
Get-ADxComputer -LDAPFilter '(msDS-AllowedToActOnBehalfOfOtherIdentity=*)' `
                -Properties OperatingSystem |
    Select-Object Name, DNSHostName, OperatingSystem | Format-Table -AutoSize

# --- 4. Matching-rule OIDs, spelled out ---
# These two are the same query. Use whichever reads better in context.
$adminsDn = (Get-ADxGroup 'Domain Admins').DistinguishedName

$viaFilter = Get-ADxUser -Filter 'memberOf -recursivematch $adminsDn'
$viaLdap   = Get-ADxUser -LDAPFilter "(memberOf:1.2.840.113556.1.4.1941:=$adminsDn)"

[PSCustomObject]@{
    'via -Filter -recursivematch' = @($viaFilter).Count
    'via -LDAPFilter rule 1941'   = @($viaLdap).Count
} | Format-List

# -LDAPFilter is ANDed with the preset's object-class filter, so the line above is
# really (&(&(objectCategory=person)(objectClass=user))(memberOf:...1941:=...)).
# Use Search-ADxObject when you want no object-class constraint at all.

<#
Sample output

Name          SamAccountName Enabled whenCreated
----          -------------- ------- -----------
svc-backup    svc-backup        True 2024-06-02 09:14:33
Contractor 07 ctr07            False 2026-02-20 16:51:09

Name              GroupScope
----              ----------
SG-Project-Apollo Global
SG-Temp-Migration Global

Name  DNSHostName            OperatingSystem
----  -----------            ---------------
FS01  fs01.corp.contoso.com  Windows Server 2019 Standard

via -Filter -recursivematch : 4
via -LDAPFilter rule 1941   : 4

THE OTHER REFUSALS

These are rejected up front rather than sent to the DC, for the same reason:

  -Filter "Name -match 'j.*'"
  -> -match is not supported: AD filters have no regex matching. Use -like with '*'
     wildcards.

  -Filter "Name -ceq 'jdoe'"
  -> '-ceq' is not supported: Active Directory has no case-sensitive matching, and
     silently treating it as case-insensitive would return a superset of what was
     asked for. Use the case-insensitive form.

  -Filter 'Department -eq $typoedVariable'
  -> Variable '$typoedVariable' is not defined. An undefined variable would otherwise
     silently behave as $null and match the wrong set; define it, or write '-eq $null'
     explicitly.

  -Properties EmailAdress
  -> '-Properties EmailAdress' is not a known attribute or RSAT property name. AD
     silently ignores unknown names in a request list, so this would just emit a null
     column. Fix the name, or pass -AllowUnknownProperty if the attribute genuinely
     exists in your schema.

Every one of those would have "worked" against a DC and returned the wrong answer.
That is the whole design: an error you can read beats a result you cannot trust.
#>
