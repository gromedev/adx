# Search-ADxObject: the generic primitive the presets are built on.
#
# One cmdlet, one raw LDAP filter, no object-class assumptions. The RSAT-compatible
# presets add a filter translator and an output projector on top of this transport;
# they do not replace it, because a raw filter is the escape hatch when a preset does
# not cover the query.
#
# Note the parameter names differ from the presets, deliberately: -Property (not
# -Properties), -Scope (not -SearchScope), -Top / -All (not -ResultSetSize),
# -PageSize (not -ResultPageSize). Different cmdlet, different contract.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

# Naming attributes with -Property is the single biggest performance lever in an LDAP
# sweep. Without it the DC serialises every populated attribute on every entry.
Search-ADxObject '(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))' `
    -Property sAMAccountName, mail, department, lastLogonTimestamp -All |
    Select-Object -First 5

# Values are converted to useful CLR types by default: objectSid becomes an SDDL
# string, objectGUID a Guid, whenCreated and lastLogonTimestamp become DateTimeOffset
# in UTC, and userAccountControl / groupType are decoded into readable properties.
Search-ADxObject '(objectCategory=group)' -Property name, groupType, member -Top 3 |
    Select-Object name, GroupScope, GroupCategory, @{N='Members'; E={ @($_.member).Count }} |
    Format-Table -AutoSize

# -Raw bypasses all of it when you want exactly what the wire carried.
Search-ADxObject '(sAMAccountName=jdoe)' -Property objectSid, userAccountControl, whenCreated -Raw |
    Format-List

# Without -All or -Top, the search stops after ONE page and says so. That default is
# the opposite of the presets, which are unlimited to match RSAT.
Search-ADxObject '(objectClass=user)' -Property sAMAccountName | Measure-Object |
    Select-Object Count

<#
Sample output

sAMAccountName           Enabled  DistinguishedName
--------------           -------  -----------------
Administrator               True  CN=Administrator,CN=Users,DC=corp,DC=contoso,DC=com
jdoe                        True  CN=Jane Doe,OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com
iberg                       True  CN=Ida Berg,OU=Users,OU=Finance,DC=corp,DC=contoso,DC=com
aruiz                       True  CN=Ana Ruiz,OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com
tfisher                     True  CN=Tom Fisher,OU=Users,OU=Support,DC=corp,DC=contoso,DC=com

name          GroupScope  GroupCategory Members
----          ----------  ------------- -------
Administrators DomainLocal Security           3
Domain Admins  Global      Security           3
SG-AllStaff    Universal   Security        1500

DistinguishedName   : CN=Jane Doe,OU=Users,OU=Sales,DC=corp,DC=contoso,DC=com
objectSid           : {1, 5, 0, 0...}
userAccountControl  : {512}
whenCreated         : {20230419102251.0Z}

Count
-----
 1000

WARNING: Search stopped at 1000 entries (one page). Use -All to return everything,
or -Top N for an explicit limit.

Three things worth pulling out of that:

  The default table view is sAMAccountName / Enabled / DistinguishedName, and Enabled
  is populated even though nobody asked for userAccountControl by name - the decoder
  fills in what it can from what was fetched.

  SG-AllStaff reports 1500 members, not its real 1980. That is MaxValRange, and
  Search-ADxObject flags it (memberTruncated) rather than fetching the rest - see
  example 10 for both contracts side by side.

  -Raw returns everything as arrays of raw values, because that is what LDAP actually
  carries. userAccountControl 512 is NORMAL_ACCOUNT with no other flags.
#>
