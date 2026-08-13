---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Search-ADxAccount.md
schema: 2.0.0
---

# Search-ADxAccount

## SYNOPSIS
Find accounts by state (disabled, expired, locked out) - a drop-in for RSAT Search-ADAccount.

## SYNTAX

### AccountDisabled
```
Search-ADxAccount [-AccountDisabled] [-UsersOnly] [-ComputersOnly] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### AccountExpired
```
Search-ADxAccount [-AccountExpired] [-UsersOnly] [-ComputersOnly] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### AccountExpiring
```
Search-ADxAccount [-AccountExpiring] [-DateTime <DateTime>] [-TimeSpan <TimeSpan>] [-UsersOnly]
 [-ComputersOnly] [-SearchBase <String>] [-SearchScope <String>] [-ResultSetSize <Int32>]
 [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>]
 [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

### AccountInactive
```
Search-ADxAccount [-AccountInactive] [-DateTime <DateTime>] [-TimeSpan <TimeSpan>] [-UsersOnly]
 [-ComputersOnly] [-SearchBase <String>] [-SearchScope <String>] [-ResultSetSize <Int32>]
 [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>] [-UseSsl] [-Credential <PSCredential>]
 [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals] [-ProgressAction <ActionPreference>]
 [<CommonParameters>]
```

### LockedOut
```
Search-ADxAccount [-LockedOut] [-UsersOnly] [-ComputersOnly] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>] [-UseSsl]
 [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### PasswordExpired
```
Search-ADxAccount [-PasswordExpired] [-UsersOnly] [-ComputersOnly] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### PasswordNeverExpires
```
Search-ADxAccount [-PasswordNeverExpires] [-UsersOnly] [-ComputersOnly] [-SearchBase <String>]
 [-SearchScope <String>] [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Finds user and computer accounts by a single state criterion. Each criterion switch is its own
parameter set, so exactly one is chosen per call; results are scoped with -UsersOnly /
-ComputersOnly. There is no -Filter/-Identity/-Properties - the criterion is the filter and the
output is a fixed slim account shape, matching RSAT.

Criteria: -AccountDisabled, -AccountExpired, -AccountExpiring (with -DateTime or -TimeSpan),
-AccountInactive (with -DateTime or -TimeSpan), -LockedOut, -PasswordExpired,
-PasswordNeverExpires.

-PasswordExpired is filtered client-side per object: its bit lives in the constructed
msDS-User-Account-Control-Computed, which Active Directory cannot match in a search filter, so
this criterion reads the whole in-scope account population and tests each locally. -DateTime is
interpreted in local time, matching RSAT. -AccountInactive uses the replicated lastLogonTimestamp
(up to ~14 days stale), the same signal RSAT uses.

-LockedOut tests the stored lockoutTime (>= 1), matching Search-ADAccount - which also means it
can return accounts whose lockout window has already expired, since Active Directory clears
lockoutTime only on the next successful logon or an admin unlock. The LockedOut column those
objects project reads the DC-computed bit and reports False for them; the projected property is
the truth.

-AccountExpiring and -AccountInactive require exactly one of -DateTime or -TimeSpan. RSAT
applies an undocumented default window when neither is given; ADx asks for the boundary
explicitly - a deliberate, documented divergence.

## EXAMPLES

### Example 1
```powershell
PS C:\> Search-ADxAccount -AccountDisabled -UsersOnly -Server dc01.corp.contoso.com
```

Disabled user accounts.

### Example 2
```powershell
PS C:\> Search-ADxAccount -AccountInactive -TimeSpan 90.00:00:00 -UsersOnly
```

Users whose last replicated logon is older than 90 days.

### Example 3
```powershell
PS C:\> Search-ADxAccount -AccountExpiring -DateTime (Get-Date).AddDays(14)
```

Accounts expiring within the next two weeks.

### Example 4
```powershell
PS C:\> Search-ADxAccount -LockedOut
```

Accounts whose stored lockout flag (lockoutTime) is set. This can include accounts whose
lockout window has already expired - see DESCRIPTION; the projected LockedOut column reads the
DC-computed bit and is the truth.

### Example 5
```powershell
PS C:\> Search-ADxAccount -PasswordNeverExpires -UsersOnly | Select-Object Name, SamAccountName
```

Users whose password never expires.
## PARAMETERS

### -AccountDisabled
Accounts whose ACCOUNTDISABLE flag is set.

```yaml
Type: SwitchParameter
Parameter Sets: AccountDisabled
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -AccountExpired
Accounts whose expiration date has already passed.

```yaml
Type: SwitchParameter
Parameter Sets: AccountExpired
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -AccountExpiring
Accounts expiring between now and the cutoff. Requires -DateTime or -TimeSpan.

```yaml
Type: SwitchParameter
Parameter Sets: AccountExpiring
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -AccountInactive
Accounts whose last replicated logon is older than the cutoff. Requires -DateTime or -TimeSpan.

```yaml
Type: SwitchParameter
Parameter Sets: AccountInactive
Aliases:

Required: True
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

### -ComputersOnly
Restrict results to computer accounts.

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

### -DateTime
An absolute cutoff for -AccountExpiring / -AccountInactive, in local time.

```yaml
Type: DateTime
Parameter Sets: AccountExpiring, AccountInactive
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -LockedOut
Accounts that are locked out (lockoutTime set).

```yaml
Type: SwitchParameter
Parameter Sets: LockedOut
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -PasswordExpired
Accounts with an expired password. Filtered client-side (a constructed attribute), so the whole in-scope population is read.

```yaml
Type: SwitchParameter
Parameter Sets: PasswordExpired
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -PasswordNeverExpires
Accounts whose DONT_EXPIRE_PASSWORD flag is set.

```yaml
Type: SwitchParameter
Parameter Sets: PasswordNeverExpires
Aliases:

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

### -TimeSpan
A relative window: -AccountExpiring uses now+span, -AccountInactive uses now-span.

```yaml
Type: TimeSpan
Parameter Sets: AccountExpiring, AccountInactive
Aliases:

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

### -UsersOnly
Restrict results to user accounts.

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
## NOTES

## RELATED LINKS

[Get-ADxUser](Get-ADxUser.md)
[Get-ADxComputer](Get-ADxComputer.md)
