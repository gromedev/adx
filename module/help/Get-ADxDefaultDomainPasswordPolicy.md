---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxDefaultDomainPasswordPolicy.md
schema: 2.0.0
---

# Get-ADxDefaultDomainPasswordPolicy

## SYNOPSIS
Get the domain's default password and lockout policy — a drop-in replacement for RSAT's
Get-ADDefaultDomainPasswordPolicy.

## SYNTAX

```
Get-ADxDefaultDomainPasswordPolicy [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>]
 [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

## DESCRIPTION
Reads the password and account-lockout policy stored on the domain head (the
defaultNamingContext object) in a single base-scope read: MinPasswordLength,
PasswordHistoryCount, MaxPasswordAge, MinPasswordAge, LockoutDuration, LockoutObservationWindow,
LockoutThreshold, ComplexityEnabled, and ReversibleEncryptionEnabled.

The four age/duration values are AD *interval* attributes — stored as negative 100-nanosecond
tick counts — and are surfaced as positive `TimeSpan`s. Two stored states are special, and ADx
deliberately keeps them distinguishable where RSAT does not: a stored `0` ("no value set")
surfaces as `00:00:00`, and the "never" sentinel (`0x8000000000000000`) surfaces as
`TimeSpan.MaxValue` — RSAT collapses both to `00:00:00`. An audit ported from RSAT that
detects never-expire policies with `MaxPasswordAge -eq 0` must also test
`[TimeSpan]::MaxValue` here. See the README's "Deliberate divergences from RSAT".

Unlike RSAT there is no `-Identity`/`-Current`: RSAT locates other domains through the netlogon
DC locator, which is not an LDAP operation. Here the connected domain is the target — point
`-Server` at a DC (or the DNS domain name) of the domain whose policy you want.

Note this is the *default domain policy* only. Fine-grained password policies (PSOs) are
separate objects and are not read by this cmdlet, same as its RSAT counterpart.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxDefaultDomainPasswordPolicy -Server dc01.corp.contoso.com
```

The connected domain's policy.

### Example 2
```powershell
PS C:\> Get-ADxDefaultDomainPasswordPolicy -Server child.corp.contoso.com
```

Another domain's policy, by pointing -Server at that domain.

### Example 3
```powershell
PS C:\> (Get-ADxDefaultDomainPasswordPolicy).MaxPasswordAge.TotalDays
```

The maximum password age in days, as a number.

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
Domain controller or DNS domain name of the domain whose policy to read. Defaults to
`USERDNSDOMAIN`.

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
PSTypeName `ADx.DefaultDomainPasswordPolicy`, one object: ComplexityEnabled,
DistinguishedName, LockoutDuration, LockoutObservationWindow, LockoutThreshold, MaxPasswordAge,
MinPasswordAge, MinPasswordLength, ObjectClass, ObjectGUID, PasswordHistoryCount,
ReversibleEncryptionEnabled.

## NOTES
The interval attributes must not be decoded as FILETIME timestamps — FILETIME's ≤ 0 "never"
sentinel would silently null every one of them. ADx gives them their own syntax; for the same
reason, filtering on an interval attribute in any `-Filter` is rejected with an explanation.

## RELATED LINKS

[Get-ADxDomain](Get-ADxDomain.md)
[Get-ADxRootDse](Get-ADxRootDse.md)
