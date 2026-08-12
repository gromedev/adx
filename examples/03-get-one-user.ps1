# Look up a single user, four different ways.
#
# -Identity accepts a distinguished name, an objectGUID, a SID, or a sAMAccountName,
# detected in that order. A DN is resolved with a single base-scope read - the fastest
# lookup the protocol allows - and is then verified to actually be a user, so handing
# it a group DN is an ObjectNotFound error rather than a group object wearing a user's
# property names.
#
# Single-object lookups are where ADx and RSAT are closest: roughly 31ms against 25ms,
# because a fixed connect/bind/RootDSE cost of about 15ms dominates anything this small.
# ADx is built for the sweep, not the lookup. Examples 05 onward are the sweep.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$user = Get-ADxUser jdoe
$user

# The same object, by each of the other three identity forms.
Get-ADxUser $user.DistinguishedName | Select-Object -ExpandProperty SamAccountName
Get-ADxUser $user.ObjectGUID        | Select-Object -ExpandProperty SamAccountName
Get-ADxUser $user.SID.Value         | Select-Object -ExpandProperty SamAccountName

# Output pipes back in: -Identity binds by value and by DistinguishedName property,
# so ADx output (and RSAT's, and any [pscustomobject] carrying a DN) round-trips.
Get-ADxUser jdoe | Get-ADxUser -Properties EmailAddress, Department, Title |
    Format-List Name, EmailAddress, Department, Title

# Ask for more than the default set. RSAT names and LDAP names are interchangeable;
# asking by LDAP name emits the value under both.
Get-ADxUser jdoe -Properties mail, lastLogonTimestamp |
    Format-List Name, mail, EmailAddress, lastLogonTimestamp, LastLogonDate

<#
Sample output

DistinguishedName : CN=Jane Doe,OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com
Enabled           : True
GivenName         : Jane
Name              : Jane Doe
ObjectClass       : user
ObjectGUID        : 8f2b1c4e-6a19-4d33-9f07-2c5b1e0a77d4
SamAccountName    : jdoe
SID               : S-1-5-21-1004336348-1177238915-682003330-1163
Surname           : Doe
UserPrincipalName : jdoe@corp.contoso.com

jdoe
jdoe
jdoe

Name         : Jane Doe
EmailAddress : jane.doe@contoso.com
Department   : Sales
Title        : Account Executive

Name               : Jane Doe
mail               : jane.doe@contoso.com
EmailAddress       : jane.doe@contoso.com
lastLogonTimestamp : 2026-08-10 07:41:56
LastLogonDate      : 2026-08-10 07:41:56

Note the shapes, which are RSAT's rather than LDAP's: ObjectClass is a single string
(the most specific class), not the full class chain - so $u.ObjectClass -eq 'user'
works. SID is an object with a .Value property, because scripts read $u.SID.Value.
Dates are local DateTime. All three are deliberate, so ported scripts keep working.
#>
