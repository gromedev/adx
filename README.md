# ADx

Fast, cross-platform Active Directory collection over raw LDAP.

`ADx` is `AD` + `x`. A drop-in, substantially faster alternative to RSAT's `ActiveDirectory`
module — `Get-ADxUser` for `Get-ADUser`, `Get-ADxGroup` for `Get-ADGroup` — so existing
scripts port by search-replacing the command name. The RSAT `-Filter` syntax, identity forms,
parameter names, output property names and default property sets all match.

```powershell
Get-ADxUser jdoe
Get-ADxUser -Filter "Enabled -eq $true -and Department -eq 'Sales'" -Properties EmailAddress
Get-ADxGroup -Filter "GroupScope -eq 'Universal'"
Get-ADxComputer -Filter "OperatingSystem -like '*Server*'" -Properties OperatingSystem
```

## Why it is faster

RSAT talks SOAP to Active Directory Web Services on port 9389. ADx talks LDAP on 389 directly,
with server-side paging, explicit attribute lists, and streaming output.

**Measured**, not estimated — 3,732 users, both modules running on the domain controller so
neither pays network latency, median of 5 runs with the warm-up discarded:

| Query | ADx | RSAT | |
|---|---|---|---|
| `-Filter *`, default properties | **0.41 s** | 1.13 s | **2.8x faster** |
| `-Filter *`, `-Properties *` | **1.37 s** | 12.53 s | **9.1x faster** |

`-Properties *` is where the gap is **widest**, because ADWS pays SOAP/XML serialisation per
attribute while LDAP does not. Pulling every attribute for 3,732 users takes RSAT 12.5 seconds
and ADx 1.4.

**The win is bulk reads.** For a handful of objects the two are comparable — around 31 ms
versus 25 ms for a single-object lookup — because a fixed connect/bind/RootDSE cost of roughly
15 ms dominates anything that small. ADx is built for the sweep, not the lookup.

Numbers are from one lab domain on modest hardware; treat the ratios as indicative rather than
a guarantee for your environment.

## Memory: pipe it, don't collect it

This matters more than speed if you are running against a large directory from a laptop.

ADx streams — the engine holds one page at a time, never the whole result set. But **the way
you call it decides whether that helps**, and the natural thing to write is the one that
breaks:

```powershell
# BOUNDED - memory stays flat regardless of directory size
Get-ADxUser -Filter * -Properties * | Export-Csv users.csv -NoTypeInformation
Get-ADxUser -Filter * | ForEach-Object { ... }

# UNBOUNDED - PowerShell materialises every object into an array
$users = Get-ADxUser -Filter *
```

Measured client-side memory:

| Rows | Streamed | Collected into a variable |
|---|---|---|
| 1,000 | 4.5 MB | 10.1 MB |
| 5,000 | 6.3 MB | 18.0 MB |
| 10,000 | **8.9 MB** | 32.1 MB |

Streaming is close to flat: ten times the rows costs about twice the memory. Collecting is
linear, roughly 3 KB per object — which extrapolates to around **1 GB for a 350,000-user
domain**, all of it held by PowerShell rather than by ADx.

That second column is not something ADx can fix. `$x = Get-ADxUser ...` tells PowerShell to
build an array of every result; no amount of streaming inside the cmdlet changes it. Pipe into
whatever consumes the data and the problem disappears.

The speed win is bulk reads. A single-object lookup is one round trip either way.

## Why it may matter more than speed

The `ActiveDirectory` module is Windows-only and requires RSAT plus ADWS reachable on 9389. ADx
runs on Linux and macOS over plain LDAP. For CI containers and non-Windows administrators that
is not "faster" — it is "possible at all".

## Requirements

PowerShell 7.5+. No RSAT, no domain join, no ADWS.

`System.DirectoryServices.Protocols` ships with PowerShell itself, so there is nothing to
install and no third-party runtime dependencies.

## Install

```powershell
Import-Module ./module/adx.psd1
```

## Cmdlets

| Cmdlet | Replaces | Purpose |
|---|---|---|
| `Get-ADxUser` | `Get-ADUser` | Users, RSAT `-Filter`/`-LDAPFilter`/`-Identity`/`-Properties` |
| `Get-ADxGroup` | `Get-ADGroup` | Groups, with `GroupScope`/`GroupCategory` filterable server-side |
| `Get-ADxComputer` | `Get-ADComputer` | Computers, `$`-suffix identity handled automatically |
| `Get-ADxObject` | `Get-ADObject` | Any object class; DN/GUID identities |
| `Get-ADxGroupMember` | `Get-ADGroupMember` | Members (incl. primary-group), `-Recursive`, immune to MaxValRange |
| `Get-ADxGroupNested` | — | Every group nested inside a group, flattened |
| `Get-ADxPrincipalGroupMembership` | `Get-ADPrincipalGroupMembership` | Groups a principal belongs to (incl. primary-group), immune to MaxValRange |
| `Get-ADxOrganizationalUnit` | `Get-ADOrganizationalUnit` | OUs; `LinkedGroupPolicyObjects` from `gPLink`, DN/GUID identity |
| `Get-ADxDefaultDomainPasswordPolicy` | `Get-ADDefaultDomainPasswordPolicy` | Domain password/lockout policy; ages as `TimeSpan` |
| `Get-ADxDomain` | `Get-ADDomain` | Domain identity, FSMO holders, containers, replica DCs |
| `Get-ADxForest` | `Get-ADForest` | Forest mode, schema/naming masters, domains, GCs, sites |
| `Get-ADxDomainController` | `Get-ADDomainController` | Connected DC, one by `-Identity`, or all by `-Filter *` |
| `Get-ADxServiceAccount` | `Get-ADServiceAccount` | Standalone and group-managed service accounts (gMSA/sMSA) |
| `Get-ADxFineGrainedPasswordPolicy` | `Get-ADFineGrainedPasswordPolicy` | PSO objects; ages as `TimeSpan`, `AppliesTo` DN list |
| `Search-ADxAccount` | `Search-ADAccount` | Accounts by state: disabled/expired/expiring/inactive/locked/pwd-expired |
| `Get-ADxRootDse` | `Get-ADRootDSE` | Probe a DC: naming contexts, functional level, supported controls |
| `Search-ADxObject` | — | Generic LDAP search with paging, `-All` streaming, range retrieval |

A filter or property list that cannot be honoured faithfully is a terminating error with an
explanation, never a silent approximation — Active Directory answers a structurally valid but
wrong filter with zero rows and a success code, and that is worse than any error message.

## Usage

```powershell
# What am I talking to?
Get-ADxRootDse -Server dc01.corp.contoso.com

# Enabled users, naming attributes explicitly - the single biggest performance lever
Search-ADxObject '(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))' `
    -Property sAMAccountName, displayName, mail, lastLogonTimestamp -All

# Groups, including ones large enough to need range retrieval
Search-ADxObject '(objectCategory=group)' -Property name, member -All
```

Naming attributes with `-Property` matters more than anything else here: without it the DC
serialises every populated attribute on every entry.

## Building

```powershell
pwsh -NoProfile -File ./build.ps1 -Configuration Release
```

## Status

- **At scale:** a 500,000-object domain. Client memory stays flat from 0→500k on default
  properties (~100 MB) and on `-Properties *` (~135 MB) — the streaming design's core claim,
  measured as a sampled curve. RSAT's `Get-ADUser -Filter * -Properties *` did not complete the
  same query within the 8 GB DC's memory (6.7 GB and climbing, aborted); ADx completed it at
  156 MB in 155 s.
- **RSAT parity, three domains:** identical user/group/computer sets and values (DN, Enabled,
  SamAccountName, SID, dates) against `pentest.lab`, its child `child.pentest.lab`, and a second
  forest `partner.lab`, each under native integrated auth with no `-Credential`.
- **The correctness fixtures that needed seeding:** multi-valued `sIDHistory` (migrated
  cross-forest, every value returned and matching RSAT), gMSA rejection (a real Group Managed
  Service Account, refused as computer and user, agreeing with RSAT), builtin group scope,
  recursive membership through nested primary groups, range retrieval past `MaxValRange`,
  rule-1941 transitive closure, membership-cycle termination, filter escaping, injection
  rejection, and Global Catalog identity ambiguity across a real multi-domain GC.
- **Cross-platform:** the full cmdlet surface from macOS (pwsh 7.5) over LDAPS. Against hardened
  Windows Server 2025 DCs a simple bind on 389 is refused ("strong authentication required"), so
  LDAPS (`-UseSsl -Port 636`) is the non-Windows path; on Windows, integrated Negotiate/Kerberos
  needs neither.

### Known limitations

- **Multi-domain forests:** the membership cmdlets — `Get-ADxGroupMember`, `Get-ADxGroupNested`,
  and `Get-ADxPrincipalGroupMembership` — enumerate within one domain partition, so a membership
  in another domain of the forest is not returned. This is structural, not a tuning problem: a
  cross-domain membership is stored only as a forward `member` link in the group's partition,
  with no `memberOf` back-link maintained anywhere, so no single-partition search — nor a Global
  Catalog bind — can reach it (for `Get-ADxGroupMember`). **Each cmdlet detects the gap and
  warns**, naming the foreign side and the partition searched, rather than returning a
  partition-complete set that looks total. Verified on a two-forest lab: `BUILTIN\Administrators`
  in a child domain lists the root's `Enterprise Admins`, which the warning surfaces; and a root
  user in a child universal group is warned about by `Get-ADxPrincipalGroupMembership` on a plain
  bind. The workaround for `Get-ADxGroupMember` is
  `Get-ADxGroup -Identity <group> -Properties Members`, which returns every member DN verbatim.
  Single-domain forests are unaffected and never warn. Because a **Global Catalog** (`-Port 3268`)
  answers a subtree search from the forest root across every same-forest domain,
  `Get-ADxPrincipalGroupMembership` on a GC both *returns* those child-domain memberships and
  suppresses the warning for them (only genuinely-excluded partitions warn) — verified live.
- **Trusted-forest members are `foreignSecurityPrincipal` stubs, by design.** When a principal
  from a trusted forest is a group member, the local partition holds a `foreignSecurityPrincipal`
  object (DN `CN=<SID>,CN=ForeignSecurityPrincipals,…`) with the foreign SID but no name. ADx
  **returns it as-is** — objectClass `foreignSecurityPrincipal`, SID and DN present — where RSAT
  resolves the SID to the foreign principal's friendly name across the trust. The set and SIDs
  match; only the name resolution differs. Nothing is dropped, and the durable identity (the SID)
  is present. Resolving a foreign SID to a name needs a bind into the foreign forest with its own
  credentials — the same trade rejected for cross-domain resolution — so it is documented, not
  performed. Verified live against the well-known FSP members of `BUILTIN\Users`.
- **Declared-unsupported properties:** `PrimaryGroup`, `IPv4Address`, `IPv6Address`,
  `ProtectedFromAccidentalDeletion`, `PrincipalsAllowedToDelegateToAccount`,
  `KerberosEncryptionType`, and `CompoundIdentitySupported` each need data a plain attribute
  read cannot reach. They raise an explicit error rather than returning null.
- **`-Recursive` returns more than RSAT, deliberately.** `Get-ADxGroupMember -Recursive`
  reports effective membership, including principals whose only route into a nested group is
  `primaryGroupID`. RSAT resolves that attribute for the group you name but not for groups
  nested inside it, so on one test domain `BUILTIN\Users` returned 100 members under RSAT and
  3,733 under ADx. ADx's answer is the correct one — RSAT confirms those users are in Domain
  Users, and Domain Users is in BUILTIN\Users — but if you need byte-identical RSAT output,
  this is where the two differ. See the CHANGELOG for the full measurement.
- **Unvalidated at scale.** Everything above was verified on a small domain. Nothing here has
  been run against 100,000+ objects yet, so treat 0.2.x as pre-production for large
  environments.

## License

MIT
