# Read attributes ADx has never heard of.
#
# ADx keys its schema off a curated table of RSAT property names and LDAP attribute
# names. Anything outside it is a terminating error by default, because AD ignores
# unknown names in a request list rather than complaining - so a typo would otherwise
# produce a silent null column, and a typo'd filter would match nothing at all.
#
# Your directory has attributes that table cannot know about: extensionAttribute1-15,
# whatever your HR sync writes, anything a schema extension added. -AllowUnknownProperty
# passes names through verbatim, in BOTH -Filter and -Properties.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# Without the switch, a name outside the table is refused before any query runs:
try {
    Get-ADxUser -Filter * -Properties extensionAttribute1 -ErrorAction Stop | Out-Null
} catch {
    "Refused: $($_.Exception.Message)"
}

# With it, the name goes to the directory as written.
Get-ADxUser -Filter * -Properties extensionAttribute1, employeeType, costCenter -AllowUnknownProperty |
    Where-Object extensionAttribute1 |
    Select-Object Name, SamAccountName, extensionAttribute1, employeeType, costCenter |
    Format-Table -AutoSize

# It applies to -Filter too, so custom attributes are filterable server-side like any
# other. This is a substring match on the wire, not a client-side Where-Object.
Get-ADxUser -Filter "extensionAttribute1 -like 'Contractor*'" `
            -Properties extensionAttribute1, Department -AllowUnknownProperty |
    Select-Object Name, SamAccountName, Department, extensionAttribute1 |
    Format-Table -AutoSize

# The switch disables ADx's spell-check, so it will happily send a typo. Confirm the
# attribute exists in the schema first, and you get both the check and the reason:
$schemaNc = (Get-ADxRootDse).SchemaNamingContext
Search-ADxObject '(&(objectClass=attributeSchema)(lDAPDisplayName=extensionAttribute1))' `
    -SearchBase $schemaNc -Property lDAPDisplayName, attributeSyntax, isSingleValued |
    Format-List lDAPDisplayName, attributeSyntax, isSingleValued

<#
Sample output

Refused: '-Properties extensionAttribute1' is not a known attribute or RSAT property
name. AD silently ignores unknown names in a request list, so this would just emit a
null column. Fix the name, or pass -AllowUnknownProperty if the attribute genuinely
exists in your schema.

Name         SamAccountName extensionAttribute1 employeeType costCenter
----         -------------- ------------------- ------------ ----------
Ana Ruiz     aruiz          Contractor-2026     Contractor   CC-4471
Tom Fisher   tfisher        Employee            FTE          CC-1102
Peter Novak  pnovak         Contractor-2025     Contractor   CC-4471

Name        SamAccountName Department costCenter extensionAttribute1
----        -------------- ---------- ---------- -------------------
Ana Ruiz    aruiz          Sales      CC-4471    Contractor-2026
Peter Novak pnovak         Logistics  CC-4471    Contractor-2025

DistinguishedName : CN=ms-Exch-Extension-Attribute-1,CN=Schema,CN=Configuration,DC=corp,DC=contoso,DC=com
lDAPDisplayName   : extensionAttribute1
attributeSyntax   : 2.5.5.12
isSingleValued    : True

Two practical notes:

  Unknown attributes come back as strings, because ADx has no syntax table entry to
  marshal them by. A custom date attribute is text you parse yourself; a custom integer
  compares as text in -Filter. attributeSyntax 2.5.5.12 above is Unicode string, which
  is what extensionAttribute1 genuinely is.

  Prefer naming a known RSAT or LDAP property when one exists. -AllowUnknownProperty
  turns off the one check that catches the failure mode this module was built around,
  so scope it to the attributes that actually need it rather than leaving it on.
#>
