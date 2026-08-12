---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Search-ADxObject.md
schema: 2.0.0
---

# Search-ADxObject

## SYNOPSIS

Search on-premises Active Directory over LDAP.

## SYNTAX

```
Search-ADxObject [[-LdapFilter] <String>] [-SearchBase <String>] [-Property <String[]>]
 [-Scope <String>] [-Top <Int32>] [-All] [-PageSize <Int32>] [-Raw] [-Server <String>]
 [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [<CommonParameters>]
```

## DESCRIPTION

The generic LDAP search primitive for on-premises Active Directory. Deliberately one
general-purpose cmdlet taking a raw LDAP filter, rather than a cmdlet per object type. The
RSAT-compatible presets build on this transport rather than replacing it, so a raw filter
stays available whenever a preset does not cover the query.

It talks to the directory through `System.DirectoryServices.Protocols` rather than ADSI or
the RSAT `ActiveDirectory` module, which makes it substantially faster and lets it run on
Linux and macOS as well as Windows. Results are streamed page by page using server-side
paging, so memory stays flat regardless of result size.

Attribute values are converted to useful CLR types by default: `objectSid` becomes an SDDL
string, `objectGUID` a `Guid`, `whenCreated` and `lastLogonTimestamp` become
`DateTimeOffset` in UTC, and the bit-packed `userAccountControl` and `groupType` attributes
are decoded into readable properties (`Enabled`, `PasswordNeverExpires`,
`TrustedForDelegation`, `GroupScope`, `GroupCategory`). Use `-Raw` to bypass all of this.

## EXAMPLES

### Example 1: Find enabled user accounts

```powershell
Search-ADxObject '(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))' `
    -Property sAMAccountName, mail, lastLogonTimestamp -All
```

The bitwise matching rule filters disabled accounts at the domain controller rather than in
PowerShell, so the DC never sends rows that are about to be discarded.

Note that `(objectCategory=user)` on its own also matches computer accounts, because the
computer class derives from user. Pair `objectCategory=person` with `objectClass=user` when
you mean human accounts.

### Example 2: Scope the search to one OU

```powershell
Search-ADxObject '(objectClass=group)' -SearchBase 'OU=Groups,DC=corp,DC=contoso,DC=com' -All
```

### Example 3: Query a specific domain controller with explicit credentials

```powershell
$cred = Get-Credential
Search-ADxObject '(objectClass=user)' -Server dc1.corp.contoso.com -Credential $cred -UseSsl -Top 10
```

### Example 4: Export to CSV

```powershell
Search-ADxObject '(objectCategory=group)' -Property name, groupType, description -All |
    Select-Object name, GroupScope, GroupCategory, description |
    Export-Csv groups.csv -NoTypeInformation
```

### Example 5: Join on-premises identities to Entra ID

Requires the separate `Mgx` module for the Graph half; `ADx` itself has no cloud dependency.

```powershell
$onPrem = Search-ADxObject '(&(objectCategory=person)(objectClass=user))' `
    -Property sAMAccountName, objectSid -All
$cloud = Invoke-MgxRequest /users -Property id,onPremisesSecurityIdentifier -All

$byName = $onPrem | Group-Object objectSid -AsHashTable
$cloud | Where-Object onPremisesSecurityIdentifier |
    ForEach-Object { [pscustomobject]@{
        CloudId = $_.id
        OnPrem  = $byName[$_.onPremisesSecurityIdentifier].sAMAccountName
    } }
```

`objectSid` from AD and `onPremisesSecurityIdentifier` from Graph are the same value, which
makes them a reliable join key across the two directories.

## PARAMETERS

### -All

Return every matching entry, following paging to the end. Overrides `-Top`. Without either
`-All` or `-Top`, a single page is returned and a warning is written.

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

Follow referrals into other domains. Off by default, because chasing them silently widens a
search beyond what was asked for and can greatly increase run time.

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

### -LdapFilter

A raw LDAP filter, for example `(&(objectCategory=person)(objectClass=user))`.

This is deliberately **not** named `-Filter`. In the Graph world `-Filter` means OData; in the RSAT
`ActiveDirectory` module it means PowerShell expression syntax. Accepting either spelling
here and forwarding it as an LDAP filter would silently return the wrong set instead of
failing, so there is no `-Filter` alias.

See [LDAP filter syntax](https://learn.microsoft.com/en-us/windows/win32/adsi/search-filter-syntax).

```yaml
Type: String
Parameter Sets: (All)
Aliases: Ldap
Required: False
Position: 0
Default value: (objectClass=*)
Accept pipeline input: False
Accept wildcard characters: False
```

### -PageSize

Entries per page. The maximum is 1000, which is Active Directory's `MaxPageSize` default;
requesting more does not return more. (The Graph cmdlets cap at 999 -- a different server
with a different limit.)

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: 1000
Accept pipeline input: False
Accept wildcard characters: False
```

### -Port

LDAP port. 389 plain, 636 LDAPS, 3268/3269 for the Global Catalog. Defaults to 636 when
`-UseSsl` is present, otherwise 389.

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

### -Property

Attributes to return. Naming them explicitly is the single biggest performance lever in an
LDAP sweep: without it the domain controller serialises every populated attribute on every
entry.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases: Select, Attributes
Required: False
Position: Named
Default value: None (server default attribute set)
Accept pipeline input: False
Accept wildcard characters: False
```

### -Raw

Emit attribute values with no type conversion: strings and byte arrays exactly as the
directory returned them. Also suppresses the derived properties.

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

### -Scope

Search scope. `Base` reads only the search base itself, `OneLevel` its immediate children,
`Subtree` everything beneath it.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: Base, OneLevel, Subtree
Required: False
Position: Named
Default value: Subtree
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchBase

Distinguished name to search from. Defaults to the domain's `defaultNamingContext`, read
from RootDSE.

```yaml
Type: String
Parameter Sets: (All)
Aliases: Base, OrganizationalUnit, OU
Required: False
Position: Named
Default value: RootDSE defaultNamingContext
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchTimeout

Per-search timeout in seconds. The default of 110 sits just below Active Directory's
`MaxQueryDuration` of 120, so the client gives up marginally before the server would.

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

Domain controller hostname, or preferably the full DNS domain name. On Windows a domain name
engages the DC Locator; elsewhere it resolves the domain apex A records, which AD publishes
round-robin across its domain controllers. Defaults to `$env:USERDNSDOMAIN`.

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

### -Top

Maximum entries to return. Ignored when `-All` is present.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:
Required: False
Position: Named
Default value: 0
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

One object per directory entry, with the PSTypeName `ADx.Entry`. `DistinguishedName` and
`ParentDn` are always present; the rest depends on `-Property`.

## NOTES

Requires network access to a domain controller and rights to read the objects queried. It
does not require `Connect-MgGraph` -- the `ADx` cmdlets are independent of the Graph side of
this module.

On Linux and macOS the LDAP stack is provided by OpenLDAP. Minimal container images often
omit it; if it is missing the cmdlet reports `LdapRuntimeMissing` with installation
instructions rather than a native load failure.

Active Directory caps a single attribute read at `MaxValRange` (default 1500). Past that it
does not truncate the attribute, it renames it: a group with 3000 members comes back as
`member;range=0-1499` rather than `member`.

`Search-ADxObject` emits such attributes under their **base** name, so `$group.member`
resolves as expected. When the returned range is partial it also emits `<name>Truncated`
(`$true`) and `<name>RangeHigh` (the last index received), so a partial set is visible rather
than silently mistaken for the whole set.

## RELATED LINKS

[Get-ADxRootDse](Get-ADxRootDse.md)

[LDAP filter syntax](https://learn.microsoft.com/en-us/windows/win32/adsi/search-filter-syntax)
