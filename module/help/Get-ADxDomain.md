---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxDomain.md
schema: 2.0.0
---

# Get-ADxDomain

## SYNOPSIS
Get the connected domain's identity, FSMO roles, and well-known containers — a drop-in
replacement for RSAT's Get-ADDomain.

## SYNTAX

```
Get-ADxDomain [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>]
 [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Reads the domain object and the configuration partition to build one domain summary:
DNSRoot, NetBIOSName, DomainSID, DomainMode, Forest, ParentDomain, ChildDomains, the five FSMO
roles that apply at the domain level resolved to hostnames (PDCEmulator, RIDMaster,
InfrastructureMaster), the well-known containers (Users/Computers/DomainControllers/System/
LostAndFound/DeletedObjects/ForeignSecurityPrincipals/Quotas), LinkedGroupPolicyObjects,
ManagedBy, and the domain's read-write and read-only directory servers.

There is no `-Identity`: RSAT's domain targeting uses the netlogon DC locator, which is not
LDAP. Point `-Server` at a DC of the domain you want.

**Honest subset.** Every property emitted is produced from a real read. RSAT properties that
ADx cannot yet produce faithfully — LastLogonReplicationInterval, SubordinateReferences, and any
value needing a domain-trust walk — are *omitted* rather than returned as null. Absence is
visible; a null-valued property would be a false statement about the directory.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxDomain -Server dc01.corp.contoso.com
```

The connected domain.

### Example 2
```powershell
PS C:\> (Get-ADxDomain).PDCEmulator
```

The hostname of the PDC-emulator FSMO role holder (resolved from the role owner's nTDSDSA DN to
the DC's dNSHostName).

### Example 3
```powershell
PS C:\> Get-ADxDomain -Server child.corp.contoso.com | Select-Object DNSRoot, ParentDomain, ChildDomains
```

A child domain's place in the forest hierarchy.

### Example 4
```powershell
PS C:\> (Get-ADxDomain).DomainControllersContainer
```

The DN of the Domain Controllers container, parsed from the domain head's wellKnownObjects.

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
PSTypeName `ADx.Domain`, one object. Includes DistinguishedName, Name, ObjectClass, ObjectGUID,
DNSRoot, NetBIOSName, DomainMode, DomainSID, Forest, ParentDomain, ChildDomains, PDCEmulator,
RIDMaster, InfrastructureMaster, the eight well-known containers, LinkedGroupPolicyObjects,
ManagedBy, ReplicaDirectoryServers, ReadOnlyReplicaDirectoryServers, AllowedDNSSuffixes.

## NOTES
The FSMO role holders are stored as nTDSDSA DNs; ADx resolves each to the DC's dNSHostName via
its parent server object, so PDCEmulator/RIDMaster/InfrastructureMaster come back as hostnames
as RSAT emits them, not DNs.

## RELATED LINKS

[Get-ADxForest](Get-ADxForest.md)
[Get-ADxDomainController](Get-ADxDomainController.md)
[Get-ADxDefaultDomainPasswordPolicy](Get-ADxDefaultDomainPasswordPolicy.md)
