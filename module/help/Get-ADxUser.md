---
external help file: ADx.Cmdlets.dll-Help.xml
Module Name: ADx
online version: https://github.com/gromedev/adx/blob/main/module/help/Get-ADxUser.md
schema: 2.0.0
---

# Get-ADxUser

## SYNOPSIS

Get Active Directory users. Drop-in replacement for RSAT's `Get-ADUser` over raw LDAP.

## SYNTAX

### Filter (Default)
```
Get-ADxUser -Filter <String> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### Identity
```
Get-ADxUser [-Identity] <Object> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### LdapFilter
```
Get-ADxUser -LDAPFilter <String> [-Properties <String[]>] [-SearchBase <String>] [-SearchScope <String>]
 [-ResultSetSize <Int32>] [-ResultPageSize <Int32>] [-AllowUnknownProperty] [-Server <String>] [-Port <Int32>]
 [-UseSsl] [-Credential <PSCredential>] [-AuthType <String>] [-SearchTimeout <Int32>] [-ChaseReferrals]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION

Gets one user by `-Identity`, or the users matching `-Filter` (RSAT expression syntax) or
`-LDAPFilter` (raw LDAP). Existing `Get-ADUser` scripts are expected to work by replacing the
command name: the filter language, identity forms, parameter names, output property names, and
default property set all match RSAT.

It differs from RSAT in how it talks to the domain: raw LDAP on port 389 with server-side
paging and explicit attribute lists, instead of SOAP to Active Directory Web Services on 9389.
That makes bulk enumeration substantially faster, and it works from Linux and macOS, where the
`ActiveDirectory` module cannot be installed at all. No RSAT, no ADWS, no domain join required.

`-Filter` accepts the RSAT expression syntax, including `-and`/`-or`/`-not`, parentheses,
`-band`/`-bor` bitwise tests, `-recursivematch`, variables (`$dept`), variable member access
(`$u.DistinguishedName`), and expandable strings (`"*$dept*"`). Values are marshalled by each
attribute's syntax - dates become FILETIME or GeneralizedTime, SIDs and GUIDs become binary -
so typed comparisons match what the directory stores. A filter that cannot be translated
faithfully is a terminating error, never a silent approximation: misspelled property names,
case-sensitive operators (`-ceq`), regex operators (`-match`), and undefined variables are all
rejected with an explanation, because Active Directory answers a structurally valid but wrong
filter with zero rows and a success code.

Output uses RSAT property names and types: `ObjectClass` is the single most specific class,
dates are local `DateTime`, `SID` is an object with a `.Value` property, and `Enabled` and the
other `userAccountControl` flags are decoded booleans.

## EXAMPLES

### Example 1
```powershell
PS C:\> Get-ADxUser jdoe
```

Gets one user by sAMAccountName. `-Identity` also accepts a distinguished name, an objectGUID,
or a SID, detected in that order.

### Example 2
```powershell
PS C:\> Get-ADxUser -Filter "Enabled -eq $true -and Department -eq 'Sales'" -Properties EmailAddress, Title
```

All enabled users in the Sales department, with two properties beyond the default set.
`Enabled` translates to the server-side `userAccountControl` bitwise filter, so the directory
does the filtering, not the client.

### Example 3
```powershell
PS C:\> $cutoff = (Get-Date).AddDays(-90)
PS C:\> Get-ADxUser -Filter "LastLogonDate -lt $cutoff" -Properties LastLogonDate
```

Users who have not logged on for 90 days. The `DateTime` in `$cutoff` is marshalled to the
FILETIME form `lastLogonTimestamp` actually stores. Note `lastLogonTimestamp` is replicated
with up to 14 days of staleness - RSAT behaves identically.

### Example 4
```powershell
PS C:\> Get-ADxUser -Filter * -SearchBase 'OU=Sales,DC=corp,DC=contoso,DC=com'
```

Every user under one OU. `-Filter *` applies no constraint beyond the user object-class
filter, and results are unlimited by default, matching RSAT's `-ResultSetSize` default.

### Example 5
```powershell
PS C:\> Get-ADxUser -LDAPFilter '(memberOf:1.2.840.113556.1.4.1941:=CN=Admins,OU=Groups,DC=corp,DC=contoso,DC=com)'
```

Raw LDAP escape hatch: transitive members of a group via the matching-rule OID. The same query
is available in filter syntax as `"memberOf -recursivematch '<group DN>'"`.

## PARAMETERS

### -AllowUnknownProperty
Pass property names that ADx's curated schema does not recognise through to the directory
verbatim, in both `-Filter` and `-Properties`. Without this, an unrecognised name is a
terminating error - AD ignores unknown attributes silently, so a typo would otherwise just
match nothing. Use it for schema extensions (custom attributes) ADx cannot know about.

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
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Credential
Credentials for the bind. Defaults to the current security context (Kerberos ticket on a
domain-joined host).

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
Query in RSAT's expression syntax, e.g. `"Name -like 'j*' -and Enabled -eq $true"`. A
ScriptBlock is accepted and coerced to its body, so `-Filter { Name -eq 'x' }` also works.
`-Filter *` matches every user. Must be named (not positional) so `Get-ADxUser jdoe` resolves
an identity instead of parsing "jdoe" as a filter.

Supported operators: `-eq -ne -like -notlike -gt -ge -lt -le -and -or -not -band -bor
-recursivematch`. Explicitly rejected: case-sensitive variants (AD has no case-sensitive
matching), `-match`, `-in`, `-contains`, `-replace`.

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
A distinguished name, objectGUID (D or N format), SID (`S-1-5-...`), or sAMAccountName,
detected in that order. A DN is resolved with a single base-scope read - the fastest possible
lookup - and verified to actually be a user, so a group DN is an ObjectNotFound error rather
than a group object. Takes pipeline input by value and by a `DistinguishedName` property, so
ADx output pipes back in.

An identity that does not exist is a TERMINATING error (matching RSAT), so
`try { Get-ADxUser $name } catch { }` is the existence-check idiom;
`-ErrorAction SilentlyContinue` does not silence the miss. `-SearchBase` and an explicitly
narrowed `-SearchScope` constrain identity resolution for every identity form, DNs and GUIDs
included (an ADx extension - RSAT refuses the combination); only the unconstrained default
keeps the base-read fast path, which also reaches configuration/schema-partition DNs.

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
Raw LDAP filter (RFC 4515), ANDed with the user object-class filter. The escape hatch for
queries the `-Filter` translator does not cover.

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
389 plain, 636 LDAPS, 3268/3269 Global Catalog. Defaults to 389, or 636 with `-UseSsl`.
636 and 3269 imply LDAPS unless `-UseSsl:$false` is explicitly bound - a plaintext bind
against an LDAPS port can never succeed. Conflicts with a port embedded in `-Server`
(a terminating error names both).

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
Properties to fetch beyond the default set (the defaults are always included). Accepts RSAT
names (`EmailAddress`, `LastLogonDate`) and LDAP names (`mail`, `lastLogonTimestamp`)
interchangeably; asking by LDAP name emits the value under both names. `*` fetches everything
the server will return for the object - which, exactly as with RSAT, excludes constructed
attributes (`canonicalName`, `msDS-User-Account-Control-Computed`, ...); those must be named
explicitly.

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
Entries per wire page. Max 1000, which is AD's `MaxPageSize` default - asking for more does
not return more.

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
Maximum number of users to return. The default (0) is unlimited, matching RSAT. This is a
deliberate divergence from `Search-ADxObject`, which stops after one page unless told
otherwise.

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

### -SearchBase
Search root. Defaults to the domain's defaultNamingContext, read from RootDSE. Also
constrains `-Identity` resolution: with a search base, every identity form (DNs and GUIDs
included) resolves inside it.

```yaml
Type: String
Parameter Sets: (All)
Aliases: Base, OrganizationalUnit, OU

Required: False
Position: Named
Default value: The domain's defaultNamingContext
Accept pipeline input: False
Accept wildcard characters: False
```

### -SearchScope
`Base`, `OneLevel`, or `Subtree` (default). Also constrains `-Identity` resolution: an
explicitly narrowed scope routes DN and GUID identities through the scoped search instead
of their base-read fast path.

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
Per-search timeout in seconds. Default 110, just under AD's default `MaxQueryDuration` of 120,
so the client gives up marginally before the server does.

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
Domain controller or, preferably, the DNS domain name. Defaults to `USERDNSDOMAIN`.
The RSAT `host:port` spelling is honoured: the embedded port drives the effective port
(Global Catalog handling included) and 636/3269 imply LDAPS. Combining it with a
conflicting `-Port` is a terminating error; malformed values (whitespace, bad ports,
broken IPv6 brackets) are rejected loudly rather than passed to the native stack.

```yaml
Type: String
Parameter Sets: (All)
Aliases: DomainController, DC

Required: False
Position: Named
Default value: $env:USERDNSDOMAIN
Accept pipeline input: False
Accept wildcard characters: False
```

### -UseSsl
LDAPS. Changes the default port to 636. When not explicitly bound, an effective port of
636/3269 (via `-Port` or `-Server host:port`) implies it; `-UseSsl:$false` always wins.

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
Identities, by value or from objects with a DistinguishedName property.

## OUTPUTS

### System.Management.Automation.PSObject
One object per user, typed `ADx.User`, with RSAT property names. Default properties:
DistinguishedName, Enabled, GivenName, Name, ObjectClass, ObjectGUID, SamAccountName, SID,
Surname, UserPrincipalName.

## NOTES

`PrimaryGroup`, `IPv4Address`, `IPv6Address`, `ProtectedFromAccidentalDeletion`,
`PrincipalsAllowedToDelegateToAccount`, `KerberosEncryptionType`, and
`CompoundIdentitySupported` are not supported in `-Properties`; each needs data outside a
plain attribute read, and asking for them is an explicit error rather than a silent null
column.

`LockedOut` reads the DC-computed lockout bit, which respects the lockout window (matching
`Get-ADUser`). Filtering on `LockedOut` can only test the stored `lockoutTime`, which persists
after a lockout expires — an account the filter matches can therefore project
`LockedOut: False`; the projected property is the truth.

## RELATED LINKS

[Get-ADxGroup](Get-ADxGroup.md)
[Get-ADxComputer](Get-ADxComputer.md)
[Get-ADxObject](Get-ADxObject.md)
[Search-ADxObject](Search-ADxObject.md)
