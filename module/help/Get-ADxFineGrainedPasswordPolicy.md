---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: adx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxFineGrainedPasswordPolicy.md
schema: 2.0.0
---

# Get-ADxFineGrainedPasswordPolicy

## SYNOPSIS
Get fine-grained password policies (PSOs) - a drop-in for RSAT Get-ADFineGrainedPasswordPolicy.

## SYNTAX

### Filter (Default)
```
Get-ADxFineGrainedPasswordPolicy -Filter <String> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### Identity
```
Get-ADxFineGrainedPasswordPolicy [-Identity] <Object> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### LdapFilter
```
Get-ADxFineGrainedPasswordPolicy -LDAPFilter <String> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Returns fine-grained password policy (PSO) objects - msDS-PasswordSettings - with
RSAT-compatible property names. PSOs live in CN=Password Settings Container,CN=System under the
domain head, and the search defaults there. The age and lockout-duration values surface as
positive TimeSpans (the same interval handling as Get-ADxDefaultDomainPasswordPolicy);
AppliesTo is the forward-linked list of principal DNs the policy applies to (always an array).

-Identity accepts the policy name, a distinguished name, or an objectGUID. This reads
fine-grained policies only; the domain-wide default policy is Get-ADxDefaultDomainPasswordPolicy.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxFineGrainedPasswordPolicy -Filter *
```

Every fine-grained password policy, from the Password Settings Container.

### Example 2
```powershell
PS C:\> Get-ADxFineGrainedPasswordPolicy -Identity 'Admins-Strong-Policy'
```

One policy by name.

### Example 3
```powershell
PS C:\> Get-ADxFineGrainedPasswordPolicy -Filter * |
    Select-Object Name, Precedence, MinPasswordLength, MaxPasswordAge, AppliesTo
```

The key settings of each policy and who it applies to.
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
RSAT-style PowerShell expression filter, or * for all policies. Interval attributes cannot be filtered on; compare them after reading.

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
The policy name, a distinguished name, or an objectGUID.

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
A raw LDAP filter, combined with (objectClass=msDS-PasswordSettings).

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

[Get-ADxDefaultDomainPasswordPolicy](Get-ADxDefaultDomainPasswordPolicy.md)
[Get-ADxUser](Get-ADxUser.md)
