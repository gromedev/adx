---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxObject.md
schema: 2.0.0
---

# Get-ADxObject

## SYNOPSIS

Get Active Directory objects of any class. Drop-in replacement for RSAT's `Get-ADObject`.

## SYNTAX

### Filter (Default)
```
Get-ADxObject -Filter <String> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### Identity
```
Get-ADxObject [-Identity] <Object> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### LdapFilter
```
Get-ADxObject -LDAPFilter <String> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Gets one group by `-Identity`, or the groups matching `-Filter` (RSAT expression syntax) or
`-LDAPFilter` (raw LDAP). The filter language, identity forms, output naming and transport
characteristics are shared with `Get-ADxUser` - see its help for the full story; everything
there applies here.

Group-specific behaviour: `GroupScope` (`DomainLocal`/`Global`/`Universal`/`BuiltinLocal`) and
`GroupCategory` (`Security`/`Distribution`) are decoded from the bit-packed `groupType`
attribute, and both are filterable server-side - `"GroupScope -eq 'Global'"` becomes a bitwise
`groupType` test on the wire.

Reading full membership of large groups is a job for `Get-ADxObjectMember` (range retrieval:
past 1,500 members AD returns the attribute renamed to `member;range=0-1499`); asking for
`-Properties Members` here returns what a single read can carry, with truncation flagged
rather than silent.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxObject 'OU=Sales,DC=corp,DC=contoso,DC=com'
```

Gets one object by distinguished name - a single base-scope read.

### Example 2
```powershell
PS C:\> Get-ADxObject -LDAPFilter '(objectClass=organizationalUnit)' -Properties Description
```

Every OU in the domain.

### Example 3
```powershell
PS C:\> $d = (Get-Date).AddDays(-7)
PS C:\> Get-ADxObject -Filter "whenChanged -ge $d" -Properties whenChanged
```

Anything modified in the last week, regardless of object class.

## PARAMETERS

### -AllowUnknownProperty
Pass unrecognised property names through verbatim instead of erroring. See `Get-ADxUser`.

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
LDAP bind mechanism. See `Get-ADxUser`.

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
Follow referrals into other domains. Off by default. See `Get-ADxUser`.

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
Credentials for the bind. Defaults to the current security context.

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

### -Filter
Query in RSAT's expression syntax; `-Filter *` matches every object under the search base
(there is no base class filter on this cmdlet). See `Get-ADxUser` for the supported operator
set and rejection rules.

```yaml
Type: String
Parameter Sets: Filter
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Identity
A distinguished name or objectGUID (D or N format) only, matching RSAT's `Get-ADObject`.
SIDs and sAMAccountNames are not identity forms for the untyped cmdlet.

```yaml
Type: Object
Parameter Sets: Identity
Aliases: DistinguishedName

Required: True
Position: 0
Default value: None
Accept pipeline input: True (ByPropertyName, ByValue)
Accept wildcard characters: False
```

### -LDAPFilter
Raw LDAP filter, applied as-is - this preset adds no object-class filter of its own.

```yaml
Type: String
Parameter Sets: LdapFilter
Aliases: Ldap

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Port
389 plain, 636 LDAPS, 3268/3269 Global Catalog.

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

### -Properties
Properties beyond the default set; RSAT and LDAP names both work; `*` fetches everything
non-constructed. See `Get-ADxUser`.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases: Property

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ResultPageSize
Entries per wire page, max 1000 (AD's MaxPageSize default).

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

### -ResultSetSize
Maximum number of objects to return; 0 (default) is unlimited, matching RSAT.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 0 (unlimited)
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchBase
Search root. Defaults to the domain's defaultNamingContext.

```yaml
Type: String
Parameter Sets: (All)
Aliases: Base, OrganizationalUnit, OU

Required: False
Position: Named
Default value: The domain's defaultNamingContext
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchScope
`Base`, `OneLevel`, or `Subtree` (default).

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

### -SearchTimeout
Per-search timeout in seconds; default 110, just under AD's MaxQueryDuration.

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
Domain controller or DNS domain name. Defaults to `USERDNSDOMAIN`.

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
LDAPS. Changes the default port to 636.

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

### -ProgressAction
Standard PowerShell progress preference for this cmdlet.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.Object
Identities, by value or from objects with a DistinguishedName property.

## OUTPUTS

### System.Management.Automation.PSObject
One object per directory entry, typed `ADx.Object`. Default properties: DistinguishedName,
Name, ObjectClass, ObjectGUID.

## NOTES

`ObjectClass` is the most specific class (`organizationalUnit`, `contact`, ...), not the
full inheritance chain - `$o.ObjectClass -eq 'organizationalUnit'` works as it does in RSAT.

## RELATED LINKS

[Get-ADxUser](Get-ADxUser.md)
[Get-ADxGroup](Get-ADxGroup.md)
[Get-ADxComputer](Get-ADxComputer.md)
[Search-ADxObject](Search-ADxObject.md)
