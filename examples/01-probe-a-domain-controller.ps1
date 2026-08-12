# Find out what you are actually talking to, before anything else.
#
# Get-ADxRootDse is one round trip that answers three questions at once: can this
# host reach a domain controller, which one answered, and what does it support.
# Run it first against an unfamiliar environment - every later failure is then
# diagnosable instead of mysterious.
#
# It is also the portable replacement for
# [System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain(), which is
# Windows-only, and it is how every other ADx cmdlet discovers its default search base.
#
# Requirements: PowerShell 7.5+, LDAP reachable on 389 (or 636 with -UseSsl).

Import-Module "$PSScriptRoot/../module/adx.psd1"

$root = Get-ADxRootDse -Server dc1.corp.contoso.com
$root

# Confirm a capability before writing code that depends on it, rather than
# discovering at 3am that the DC does not offer it.
$controls = (Get-ADxRootDse -Server dc1.corp.contoso.com -IncludeSupportedControls).SupportedControls

[PSCustomObject]@{
    PagedResults  = $controls -contains '1.2.840.113556.1.4.319'   # server-side paging
    DirSync       = $controls -contains '1.2.840.113556.1.4.841'   # incremental sync
    ChainRule1941 = $controls -contains '1.2.840.113556.1.4.1941'  # transitive membership
} | Format-List

# Everything downstream can hang off the discovered naming context.
Search-ADxObject '(objectClass=group)' -SearchBase $root.DefaultNamingContext -Property name -Top 5

<#
Sample output

Server                        : dc1.corp.contoso.com
DnsHostName                   : dc1.corp.contoso.com
DefaultNamingContext          : DC=corp,DC=contoso,DC=com
ConfigurationNamingContext    : CN=Configuration,DC=corp,DC=contoso,DC=com
SchemaNamingContext           : CN=Schema,CN=Configuration,DC=corp,DC=contoso,DC=com
HighestCommittedUsn           : 184729
DomainControllerFunctionality : 7
IsActiveDirectory             : True
SupportsPagedResults          : True
SupportsDirSync               : True
SupportedControlCount         : 38

PagedResults  : True
DirSync       : True
ChainRule1941 : True

sAMAccountName           Enabled  DistinguishedName
--------------           -------  -----------------
Administrators                    CN=Administrators,CN=Builtin,DC=corp,DC=contoso,DC=com
Users                             CN=Users,CN=Builtin,DC=corp,DC=contoso,DC=com
Guests                            CN=Guests,CN=Builtin,DC=corp,DC=contoso,DC=com
Domain Admins                     CN=Domain Admins,CN=Users,DC=corp,DC=contoso,DC=com
Domain Users                      CN=Domain Users,CN=Users,DC=corp,DC=contoso,DC=com

DomainControllerFunctionality 7 is Windows Server 2016 or later. The Enabled column
is blank for groups - it is a userAccountControl flag, and groups do not have one.
#>
