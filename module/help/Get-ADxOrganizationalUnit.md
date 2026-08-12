---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxOrganizationalUnit.md
schema: 2.0.0
---

# Get-ADxOrganizationalUnit

## SYNOPSIS
Get organizational units over raw LDAP — a drop-in replacement for RSAT's
Get-ADOrganizationalUnit.

## SYNTAX

### Filter (Default)
```
Get-ADxOrganizationalUnit -Filter <String> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### Identity
```
Get-ADxOrganizationalUnit [-Identity] <Object> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### LdapFilter
```
Get-ADxOrganizationalUnit -LDAPFilter <String> [-Properties <String[]>] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty]
 [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Returns organizational units with RSAT-compatible property names, over raw LDAP on port 389/636
instead of ADWS. Filters use RSAT's PowerShell expression syntax (`-Filter`) or raw LDAP
(`-LDAPFilter`); `-Identity` accepts a distinguished name or objectGUID only, matching RSAT
(an OU has no sAMAccountName).

Default output: City, Country, DistinguishedName, LinkedGroupPolicyObjects, ManagedBy, Name,
ObjectClass, ObjectGUID, PostalCode, State, StreetAddress.

Two OU-specific behaviours worth knowing:

- `LinkedGroupPolicyObjects` is parsed from the OU's `gPLink` attribute into an ordered array of
  GPO distinguished names (link flags such as enforced/disabled do not remove a link from the
  list, matching RSAT).
- `StreetAddress` is read from the LDAP `street` attribute — for OUs that is the schema's
  attribute, where a user's StreetAddress is `streetAddress`. ADx maps this per object type, so
  both cmdlets read the right attribute.

A misspelled property or a filter that cannot be translated faithfully is a terminating error,
never a silently empty column or zero-row success.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxOrganizationalUnit -Filter * -Server dc01.corp.contoso.com
```

Every OU in the domain, default properties.

### Example 2
```powershell
PS C:\> Get-ADxOrganizationalUnit -Filter "Name -like 'Sales*'" -Properties Description
```

OUs whose name starts with Sales, adding Description to the default set.

### Example 3
```powershell
PS C:\> Get-ADxOrganizationalUnit -Identity 'OU=Workstations,DC=corp,DC=contoso,DC=com'
```

One OU by distinguished name — a single base-scope read, the fastest path.

### Example 4
```powershell
PS C:\> Get-ADxOrganizationalUnit -Filter * |
    Where-Object { $_.LinkedGroupPolicyObjects.Count -eq 0 }
```

OUs with no linked GPOs. `LinkedGroupPolicyObjects` is always an array (empty when there are no
links), so `.Count` is always valid.

### Example 5
```powershell
PS C:\> Get-ADxOrganizationalUnit -SearchBase 'OU=Departments,DC=corp,DC=contoso,DC=com' `
    -SearchScope OneLevel -Filter *
```

The direct child OUs of one container only.

## PARAMETERS

### -AllowUnknownProperty
Pass property names that are not in ADx's attribute table through to the server verbatim. See
`Get-ADxUser`.

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
RSAT-style PowerShell expression filter, e.g. `"Name -like 'Sales*'"` or `*` for all OUs.
Same syntax and guarantees as `Get-ADxUser`.

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
A distinguished name or objectGUID. OUs accept only these two identity forms, matching RSAT —
an OU has no sAMAccountName. Takes pipeline input, including objects with a DistinguishedName
property.

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
A raw LDAP filter, combined with the OU base filter `(objectCategory=organizationalUnit)`.

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
Entries per server page. See `Get-ADxUser`.

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
Maximum number of OUs to return; 0 (default) is unlimited, matching RSAT.

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

### -SearchBase
Search root; defaults to the domain's defaultNamingContext.

```yaml
Type: String
Parameter Sets: (All)
Aliases: Base, OrganizationalUnit, OU

Required: False
Position: Named
Default value: defaultNamingContext
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
Default value: Subtree
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
Default value: USERDNSDOMAIN
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
An identity (DN or GUID), or any object with a DistinguishedName property.

## OUTPUTS

### System.Management.Automation.PSObject
PSTypeName `ADx.OrganizationalUnit`. Default properties: City, Country, DistinguishedName,
LinkedGroupPolicyObjects, ManagedBy, Name, ObjectClass, ObjectGUID, PostalCode, State,
StreetAddress.

## NOTES
`StreetAddress` reads the OU schema's `street` attribute (users use `streetAddress`); the
mapping is per object type, so both are correct. `LinkedGroupPolicyObjects` preserves the
`gPLink` order and includes disabled/enforced links, like RSAT.

## RELATED LINKS

[Get-ADxUser](Get-ADxUser.md)
[Get-ADxObject](Get-ADxObject.md)
[Get-ADxDomain](Get-ADxDomain.md)
