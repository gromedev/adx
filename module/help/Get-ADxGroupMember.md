---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxGroupMember.md
schema: 2.0.0
---

# Get-ADxGroupMember

## SYNOPSIS

Get the members of an Active Directory group. Drop-in replacement for RSAT's `Get-ADGroupMember`.

## SYNTAX

```
Get-ADxGroupMember [-Recursive] [-Identity] <Object> [-Properties <String[]>] [-ResultSetSize <Int32>]
 [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Returns the members of a group. By default, direct members only (users, computers, contacts,
and nested groups as members in their own right); with `-Recursive`, every principal in the
nesting hierarchy with the nested groups themselves removed.

Unlike a naive read of the group's `member` attribute, this enumerates by searching
`memberOf`, so it is immune to the `MaxValRange` cap (a group with more than 1,500 members is
returned complete, not truncated) and it folds in PRIMARY group membership: a user whose
`primaryGroupID` is this group - every user in Domain Users, for instance - is a member that
never appears in the `member`/`memberOf` link pair. That reconciliation is exactly what RSAT's
`Get-ADGroupMember` does, and it needs the group's SID; if the SID cannot be read, primary
members are omitted with a warning rather than silently.

With `-Recursive`, the primary-group reconciliation extends to every group nested inside the
target, not just the target itself. This matters more than it sounds: matching rule
1.2.840.113556.1.4.1941 walks `member`/`memberOf` links, and primary membership creates none,
so a user whose only route into the group is "primary group of a nested group" is invisible to
the chain rule. `BUILTIN\Users` contains `Domain Users` by default in every domain, which is
precisely that shape.

MULTI-DOMAIN FORESTS - a known limitation. Membership is enumerated within the target group's
own domain partition, so members from OTHER domains in the forest are not returned; RSAT's
`Get-ADGroupMember` resolves those by walking the `member` DNs one at a time. To include them,
read the group's `member` attribute directly and resolve each foreign DN against its own
domain - see Example 4. Single-domain forests are unaffected.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxGroupMember 'Domain Admins'
```

Direct members of a group.

### Example 2
```powershell
PS C:\> Get-ADxGroupMember 'Domain Users' | Measure-Object
```

Includes every user whose primary group is Domain Users - membership that a plain `member`
read would miss entirely.

### Example 3
```powershell
PS C:\> Get-ADxGroupMember 'Enterprise Admins' -Recursive -Properties EmailAddress
```

Every effective member through any depth of nesting, with an extra property. The nested groups
are excluded; only the principals they contain are returned - including principals that reach
the group through a nested group's `primaryGroupID`.

### Example 4
```powershell
PS C:\> (Get-ADxGroup 'Shared Resource' -Properties Members).Members |
    ForEach-Object { Get-ADxObject $_ -Server (($_ -replace '^.*?,DC=', '') -replace ',DC=', '.') }
```

Including members from other domains in a multi-domain forest: read the `member` DNs and
resolve each against the domain named in its own DN, converted from DC components to the DNS
name (`CN=jdoe,OU=Users,DC=child,DC=corp,DC=com` connects to `child.corp.com`). Only needed
when the group spans domains.
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

### -Recursive
Return every member in the nesting hierarchy (matching rule 1.2.840.113556.1.4.1941),
excluding the nested groups themselves - like RSAT's `-Recursive`, only the leaf principals
come back. Without it, only direct members are returned, including any nested groups as
members in their own right.

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
One object per member, typed `ADx.Principal` (members can be users, computers, groups or
contacts). Default properties: DistinguishedName, Name, ObjectClass, ObjectGUID,
SamAccountName, SID.

## NOTES

Members can live in any organizational unit, so the search always runs from the domain root;
there is no `-SearchBase` (RSAT's `Get-ADGroupMember` has none either, for the same reason).

## RELATED LINKS

[Get-ADxGroupNested](Get-ADxGroupNested.md)
[Get-ADxGroup](Get-ADxGroup.md)
[Get-ADxUser](Get-ADxUser.md)
