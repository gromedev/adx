---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxDomainController.md
schema: 2.0.0
---

# Get-ADxDomainController

## SYNOPSIS
Get one or all domain controllers — a drop-in replacement for RSAT's Get-ADDomainController.

## SYNTAX

### Identity (Default)
```
Get-ADxDomainController [[-Identity] <String>] [-Discover] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### Filter
```
Get-ADxDomainController -Filter <String> [-Discover] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Returns domain controllers, joined across their configuration-partition nTDSDSA and server
objects and their domain-partition computer accounts. With no arguments it returns the connected
DC; with `-Identity <name|DN>` it returns that one DC; with `-Filter *` it returns every DC in
the connected domain.

Each DC carries Name, HostName, Site, Domain, Forest, IsGlobalCatalog, IsReadOnly,
OperatingSystem, OperationMasterRoles (the FSMO roles this DC holds), InvocationId,
NTDSSettingsObjectDN, ServerObjectDN, and ComputerObjectDN.

**Limitations, declared not hidden:**

- `-Filter` accepts only `*` in this version. A client-side property filter is out of scope; use
  `-Filter *` and filter the results in PowerShell, or target one DC with `-Identity`. Any other
  filter value is a terminating error.
- `-Discover` is not supported: the DC locator uses the netlogon/CLDAP mailslot protocol, not
  LDAP. Name a DC with `-Server`, or enumerate with `-Filter *`. Passing `-Discover` is a
  terminating error that says so.
- IPv4Address/IPv6Address are not returned — they require client-side DNS resolution, the same
  declared gap as the computer preset.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxDomainController -Server dc01.corp.contoso.com
```

The connected DC.

### Example 2
```powershell
PS C:\> Get-ADxDomainController -Filter * -Server dc01.corp.contoso.com
```

Every DC in the connected domain.

### Example 3
```powershell
PS C:\> Get-ADxDomainController -Identity DC02
```

One DC by short name (a full hostname or a server/computer/nTDSDSA DN also work).

### Example 4
```powershell
PS C:\> Get-ADxDomainController -Filter * | Where-Object OperationMasterRoles
```

The DCs that hold at least one FSMO role.

### Example 5
```powershell
PS C:\> Get-ADxDomainController -Filter * | Where-Object IsGlobalCatalog | Select-Object HostName, Site
```

Global-catalog DCs and their sites.

## PARAMETERS

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

### -Discover
Declared unsupported. The DC locator is the netlogon/CLDAP mailslot protocol, not LDAP; passing
this is a terminating error naming the workaround.

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

### -Filter
Only `*` is accepted, returning every DC in the connected domain. Any other value is a
terminating error.

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
A DC by hostname (full or short), or by server-object, computer-object, or nTDSDSA DN. Takes
pipeline input, including objects with a matching Name/HostName property.

```yaml
Type: String
Parameter Sets: Identity
Aliases: Name, HostName

Required: False
Position: 0
Default value: None
Accept pipeline input: True (ByPropertyName, ByValue)
Accept wildcard characters: False
```

### -Port
389 plain, 636 LDAPS.

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

### System.String
A DC identity (hostname or DN), or an object with a Name/HostName property.

## OUTPUTS

### System.Management.Automation.PSObject
PSTypeName `ADx.DomainController`: Name, HostName, Site, Domain, Forest, IsGlobalCatalog,
IsReadOnly, OperatingSystem, OperationMasterRoles, InvocationId, NTDSSettingsObjectDN,
ServerObjectDN, ComputerObjectDN.

## NOTES
OperationMasterRoles is computed by resolving the five FSMO role holders to hostnames and
matching them to each DC. `-Filter *` is scoped to the connected domain, matching RSAT; the
schema and domain-naming masters are forest-wide, the PDC/RID/Infrastructure masters are of the
connected domain.

`-Identity` matches forest-wide (RSAT errors for a DC outside the connected domain — a
deliberate, documented divergence). A foreign-domain DC comes back with per-DC-honest values:
its own Domain (config-partition data), IsReadOnly from the forest-replicated `nTDSDSARO`
object class, and OperationMasterRoles as `$null` plus a warning — its domain's role objects
sit behind a referral this bind cannot read, so a confident empty list would be wrong. Bind
`-Server <that domain>` to read its roles.

## RELATED LINKS

[Get-ADxDomain](Get-ADxDomain.md)
[Get-ADxForest](Get-ADxForest.md)
[Get-ADxRootDse](Get-ADxRootDse.md)
