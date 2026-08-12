---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxRootDse.md
schema: 2.0.0
---

# Get-ADxRootDse

## SYNOPSIS

Read the RootDSE of an Active Directory domain controller.

## SYNTAX

```
Get-ADxRootDse [-IncludeSupportedControls] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>]
 [-ChaseReferrals] [<CommonParameters>]
```

## DESCRIPTION

Reads the directory's RootDSE: naming contexts, the responding server, its functional level,
and the LDAP controls it supports.

Run this first against an unfamiliar environment. In one round trip it answers whether this
host can reach a domain controller, which one answered, and what that server supports --
which makes every subsequent failure diagnosable rather than mysterious.

It is also how the other `ADx` cmdlets discover their default search base, and the portable
replacement for `[System.DirectoryServices.ActiveDirectory.Domain]::GetCurrentDomain()`,
which is Windows-only.

## EXAMPLES

### Example 1: Check connectivity and discover the naming context

```powershell
Get-ADxRootDse
```

```
Server                        : corp.contoso.com
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
```

### Example 2: Target a specific domain controller

```powershell
Get-ADxRootDse -Server dc2.corp.contoso.com -UseSsl
```

### Example 3: List every supported control OID

```powershell
(Get-ADxRootDse -IncludeSupportedControls).SupportedControls
```

Useful for confirming a capability before relying on it -- for example DirSync
(`1.2.840.113556.1.4.841`) for incremental synchronisation.

### Example 4: Use the discovered naming context

```powershell
$root = Get-ADxRootDse
Search-ADxObject '(objectClass=group)' -SearchBase $root.DefaultNamingContext -All
```

## PARAMETERS

### -AuthType

Bind method. `Basic` sends the password and should be paired with `-UseSsl`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: Negotiate, Kerberos, Basic, Anonymous
Required: False
Position: Named
Default value: Negotiate
Accept pipeline input: False
Accept wildcard characters: False
```

### -ChaseReferrals

Follow referrals into other domains.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Credential

Credentials for the bind. Defaults to the current identity.

```yaml
Type: PSCredential
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -IncludeSupportedControls

Include the full list of supported control OIDs. Omitted by default because it is long and
usually only the count matters.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Port

LDAP port. 389 plain, 636 LDAPS, 3268/3269 for the Global Catalog.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: 389 (636 with -UseSsl)
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchTimeout

Timeout in seconds.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: 110
Accept pipeline input: False
Accept wildcard characters: False
```

### -Server

Domain controller hostname, or preferably the full DNS domain name. Defaults to
`$env:USERDNSDOMAIN`.

```yaml
Type: String
Parameter Sets: (All)
Aliases: DomainController, DC
Required: False
Position: Named
Default value: $env:USERDNSDOMAIN
Accept pipeline input: False
Accept wildcard characters: False
```

### -UseSsl

Connect over LDAPS.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable,
-Verbose, -WarningAction, and -WarningVariable.
For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject

A single object with the PSTypeName `ADx.RootDse`.

`IsActiveDirectory` reports whether the server published a `defaultNamingContext`. When it
is false the server answered but is not an AD domain controller, and AD-specific features
(range retrieval, `primaryGroupID`, DirSync) are unavailable.

## NOTES

Distinct error identifiers are used for the three ways this can fail, so they can be handled
apart:

- `NoDomainController` -- no `-Server` was given and none could be discovered.
- `LdapConnectionFailed` -- a server was named but could not be reached.
- `LdapRuntimeMissing` -- the OpenLDAP client library is absent (Linux/macOS).

## RELATED LINKS

[Search-ADxObject](Search-ADxObject.md)

[RootDSE](https://learn.microsoft.com/en-us/windows/win32/adschema/rootdse)
