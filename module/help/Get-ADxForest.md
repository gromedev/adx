---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxForest.md
schema: 2.0.0
---

# Get-ADxForest

## SYNOPSIS
Get the connected forest's identity, FSMO roles, domains, and sites — a drop-in replacement for
RSAT's Get-ADForest.

## SYNTAX

```
Get-ADxForest [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Reads the configuration partition — shared by every domain in the forest — to build one forest
summary: Name, RootDomain, ForestMode, the two forest-wide FSMO roles resolved to hostnames
(SchemaMaster, DomainNamingMaster), the member Domains, GlobalCatalogs, Sites, and the UPN and
SPN suffixes.

There is no `-Identity`; point `-Server` at any DC in the forest.

**Honest subset.** Omitted in this version (documented, not returned as null):
ApplicationPartitions and CrossForestReferences, which need additional configuration reads and
trust data.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxForest -Server dc01.corp.contoso.com
```

The forest the DC belongs to.

### Example 2
```powershell
PS C:\> (Get-ADxForest).GlobalCatalogs
```

Every global-catalog server's hostname in the forest (nTDSDSA objects with the GC option bit).

### Example 3
```powershell
PS C:\> Get-ADxForest | Select-Object RootDomain, ForestMode, Domains
```

The forest root, functional level, and its domains.

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

### None

## OUTPUTS

### System.Management.Automation.PSObject
PSTypeName `ADx.Forest`, one object: Name, RootDomain, ForestMode, SchemaMaster,
DomainNamingMaster, Domains, GlobalCatalogs, Sites, UPNSuffixes, SPNSuffixes,
PartitionsContainer.

## NOTES
Domains are the crossRefs marked as real domain partitions (systemFlags bit 0x2), so
application partitions and the config/schema partitions are correctly excluded.

## RELATED LINKS

[Get-ADxDomain](Get-ADxDomain.md)
[Get-ADxDomainController](Get-ADxDomainController.md)
