---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxGroupNested.md
schema: 2.0.0
---

# Get-ADxGroupNested

## SYNOPSIS

Get every group nested inside an Active Directory group, flattened.

## SYNTAX

```
Get-ADxGroupNested [-Identity] <Object> [-Properties <String[]>] [-ResultSetSize <Int32>]
 [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Returns every group nested - directly or transitively - inside the target group: the
flattened nesting tree. This answers the audit question "what does membership of this group
actually grant", which otherwise takes repeated `Get-ADGroupMember` calls and manual
recursion.

It runs as a single server-side query using matching rule 1.2.840.113556.1.4.1941 restricted
to `objectCategory=group`, so nesting of any depth is resolved in one round trip to the
directory. RSAT has no direct counterpart.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxGroupNested 'Tier-0 Admins'
```

Every group that is a member, at any depth, of Tier-0 Admins.

### Example 2
```powershell
PS C:\> Get-ADxGroupNested 'Domain Admins' | Select-Object Name, DistinguishedName
```

The full nesting tree behind a privileged group, flattened to a list.
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
LDAP bind mechanism. `Negotiate` (default) uses Kerberos where available and falls back to
NTLM; `Basic` sends the credential in clear and should only be combined with `-UseSsl`.

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
Follow referrals into other domains. Off by default: chasing them silently widens the search
beyond what was asked for.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
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

### -Identity
The group: a distinguished name, objectGUID (D/N format), SID, or sAMAccountName, detected in
that order. A DN is resolved with a single base-scope read and verified to be a group, so a
user DN is an ObjectNotFound error. Takes pipeline input by value and by `DistinguishedName`,
so `Get-ADxGroup ... | Get-ADxGroupMember` works.

```yaml
Type: Object
Parameter Sets: (All)
Aliases: DistinguishedName, Group

Required: True
Position: 0
Default value: None
Accept pipeline input: True (ByPropertyName, ByValue)
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
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Properties
Properties to emit for each member beyond the default set; RSAT and LDAP names both work.
See `Get-ADxUser`.

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
Maximum number of results to return; 0 (default) is unlimited, matching RSAT.

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
Default value: None
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
Default value: None
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
A group identity, by value or from a group object's DistinguishedName property.

## OUTPUTS

### System.Management.Automation.PSObject
One object per nested group, typed `ADx.Group`. Default properties: DistinguishedName,
GroupCategory, GroupScope, Name, ObjectClass, ObjectGUID, SamAccountName, SID.

## NOTES

The search runs from the domain root; nested groups can live in any organizational unit.

## RELATED LINKS

[Get-ADxGroupMember](Get-ADxGroupMember.md)
[Get-ADxGroup](Get-ADxGroup.md)
