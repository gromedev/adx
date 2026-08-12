---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: adx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxServiceAccount.md
schema: 2.0.0
---

# Get-ADxServiceAccount

## SYNOPSIS
Get managed service accounts over raw LDAP - a drop-in for RSAT Get-ADServiceAccount.

## SYNTAX

### Filter (Default)
```
Get-ADxServiceAccount -Filter <String> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### Identity
```
Get-ADxServiceAccount [-Identity] <Object> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### LdapFilter
```
Get-ADxServiceAccount -LDAPFilter <String> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Returns standalone (sMSA) and group-managed (gMSA) service accounts with RSAT-compatible
property names, over raw LDAP. Both account types are matched by their shared base class
(msDS-ManagedServiceAccount), which a gMSA inherits - so one filter returns both and nothing
else. Identity accepts a distinguished name, objectGUID, SID, or sAMAccountName (the $ suffix
is retried automatically, as for computers).

PrincipalsAllowedToRetrieveManagedPassword (the gMSA password-retrieval ACL) is declared
unsupported: it is a security descriptor whose trustees RSAT resolves to principals, which needs
an ACE walk ADx does not do.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxServiceAccount -Filter * -Server dc01.corp.contoso.com
```

Every managed service account (gMSA and sMSA) in the domain.

### Example 2
```powershell
PS C:\> Get-ADxServiceAccount -Identity websvc$ -Properties ServicePrincipalNames
```

One service account by name, adding its SPNs.

### Example 3
```powershell
PS C:\> Get-ADxServiceAccount -Filter * | Where-Object { -not $_.Enabled }
```

Disabled service accounts.
## PARAMETERS

### -AllowUnknownProperty
Pass property names not in the ADx table through verbatim. See `Get-ADxUser`.

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

### -AuthType
LDAP bind mechanism. See `Get-ADxUser`.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: Negotiate, Kerberos, Basic, Anonymous

Required: False
Position: Named
Default value: None
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
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Credential
Credentials for the LDAP bind. See `Get-ADxUser` for the platform notes.

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
RSAT-style PowerShell expression filter, or * for all service accounts. Same syntax as `Get-ADxUser`.

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
A distinguished name, objectGUID, SID, or sAMAccountName. The $ suffix is retried automatically.

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
A raw LDAP filter, combined with the service-account base filter.

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
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Properties
Properties beyond the default set; RSAT and LDAP names both work; * fetches everything non-constructed. See `Get-ADxUser`.

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
Entries per server page (default 1000).

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

### -ResultSetSize
Maximum results to return; 0 (default) is unlimited, matching RSAT.

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

### -SearchBase
Search root; defaults to the domain defaultNamingContext (or the type container).

```yaml
Type: String
Parameter Sets: (All)
Aliases: Base, OrganizationalUnit, OU

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchScope
Base, OneLevel, or Subtree (default).

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: Base, OneLevel, Subtree

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchTimeout
Per-search timeout in seconds. See `Get-ADxUser`.

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

### -Server
Domain controller or DNS domain name. Defaults to USERDNSDOMAIN.

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
LDAPS on port 636. See `Get-ADxUser`.

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
Standard PowerShell common parameter.

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
## OUTPUTS

### System.Management.Automation.PSObject
## NOTES

## RELATED LINKS

[Get-ADxComputer](Get-ADxComputer.md)
[Get-ADxUser](Get-ADxUser.md)
