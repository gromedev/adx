---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxPrincipalGroupMembership.md
schema: 2.0.0
---

# Get-ADxPrincipalGroupMembership

## SYNOPSIS

Get the groups a principal belongs to. Drop-in replacement for RSAT's `Get-ADPrincipalGroupMembership`.

## SYNTAX

```
Get-ADxPrincipalGroupMembership [-Identity] <Object> [-Properties <String[]>] [-ResultSetSize <Int32>]
 [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Returns the groups a principal - user, computer, group, or service account - is directly a
member of. The reverse of `Get-ADxGroupMember`, and like RSAT's `Get-ADPrincipalGroupMembership`
it returns DIRECT memberships only, not the transitive closure.

Crucially, the result includes the PRIMARY group. Every ordinary account's primary group is
`Domain Users`, a membership that lives only in the account's `primaryGroupID` and appears in
neither the account's `memberOf` nor the group's `member` attribute. A one-line read of
`memberOf` would miss it; this cmdlet reconciles it the way RSAT does, by matching the primary
group by its SID (the account's own domain SID with the RID replaced by `primaryGroupID`). If
the account's SID or `primaryGroupID` cannot be read, the primary group is omitted with a
warning rather than silently.

Enumeration is by a `member` search rather than a `memberOf` read, so it is immune to the
`MaxValRange` cap: a principal in more than 1,500 groups is returned complete, not truncated.

MULTI-DOMAIN FORESTS - the same partition boundary as `Get-ADxGroupMember`. Membership is
enumerated within one domain partition, so a membership stored in ANOTHER domain of the forest
(a domain-local or universal group the principal was added to in a different domain) is not
returned: that link lives as a forward `member` in the group's own partition, with no
`memberOf` back-link maintained on this principal locally, so a single-partition search cannot
reach it. Where the principal's own `memberOf` does surface such groups - reading it against a
Global Catalog (`-Port 3268`), which replicates universal-group membership forest-wide - they
are named in a warning rather than dropped in silence. To include them, query each group's own
domain. Single-domain forests are unaffected and never warn.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxPrincipalGroupMembership jdoe
```

The groups a user belongs to, including `Domain Users` (their primary group), which a plain
`memberOf` read would miss.

### Example 2
```powershell
PS C:\> Get-ADxPrincipalGroupMembership 'CN=WEB01,OU=Servers,DC=corp,DC=com'
```

A computer's group memberships, resolved by distinguished name. Any principal identity form
works: DN, objectGUID, SID, or sAMAccountName (a computer's ends in `$`).

### Example 3
```powershell
PS C:\> Get-ADxUser jdoe | Get-ADxPrincipalGroupMembership | Sort-Object Name
```

Pipe a principal object straight in - it binds by its `DistinguishedName` property - to list
that user's groups by name.

### Example 4
```powershell
PS C:\> Get-ADxPrincipalGroupMembership admin -Port 3268 -Server gc.corp.com 3>&1 |
    Where-Object { $_ -is [System.Management.Automation.WarningRecord] }
```

Against a Global Catalog, surface the warning that names any groups in OTHER forest domains the
principal belongs to - the memberships a single-partition query cannot return.

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
The principal: a distinguished name, objectGUID (D/N format), SID, or sAMAccountName, detected
in that order. No object class constrains a principal - user, computer, group and service
account all qualify - so a DN is resolved with a single base-scope read and required only to
carry an objectSid; a non-principal DN (an OU, say) is an ObjectNotFound error. Takes pipeline
input by value and by `DistinguishedName`, so `Get-ADxUser ... | Get-ADxPrincipalGroupMembership`
works.

```yaml
Type: Object
Parameter Sets: (All)
Aliases: DistinguishedName

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
Properties to emit for each group beyond the default set; RSAT and LDAP names both work.
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
A principal identity, by value or from a principal object's DistinguishedName property.

## OUTPUTS

### System.Management.Automation.PSObject
One object per group the principal belongs to, typed `ADx.Group`. Default properties:
DistinguishedName, GroupCategory, GroupScope, Name, ObjectClass, ObjectGUID, SamAccountName,
SID.

## NOTES

Groups can live in any organizational unit, so the search always runs from the domain root;
there is no `-SearchBase` (RSAT's `Get-ADPrincipalGroupMembership` has none either, for the
same reason).

Returns DIRECT memberships plus the primary group, not the transitive closure. For "every group
membership X actually grants", enumerate the other direction with `Get-ADxGroupMember -Recursive`.

## RELATED LINKS

[Get-ADxGroupMember](Get-ADxGroupMember.md)
[Get-ADxGroupNested](Get-ADxGroupNested.md)
[Get-ADxGroup](Get-ADxGroup.md)
[Get-ADxUser](Get-ADxUser.md)
