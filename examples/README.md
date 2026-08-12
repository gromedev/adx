# ADx Examples

Runnable scripts covering common Active Directory collection scenarios. Each one is
self-contained, states its requirements in the header, and ends with a `<# ... #>`
block showing what the output actually looks like.

## Prerequisites

PowerShell 7.5+ and TCP reach to a domain controller on 389 (or 636 with `-UseSsl`).
No RSAT, no ADWS, no domain join. `System.DirectoryServices.Protocols` ships with
PowerShell itself, so there is nothing to install.

```powershell
Import-Module ./module/adx.psd1     # or Import-Module ADx once it is on a module path
Get-ADxRootDse                      # confirm you can reach a DC before anything else
```

Every script imports the module relative to its own location, so they run from
anywhere:

```powershell
pwsh ./examples/01-probe-a-domain-controller.ps1
```

The scripts use `corp.contoso.com` / `DC=corp,DC=contoso,DC=com` and names like `jdoe`
and `SG-Tier0` throughout. Replace those with your own before running anything that
matters.

**Everything here is read-only.** ADx has no write cmdlets, so nothing in this folder
can change your directory.

## Scripts

### Getting connected

| Script | Description |
|--------|-------------|
| [01-probe-a-domain-controller.ps1](01-probe-a-domain-controller.ps1) | Reach a DC, read RootDSE, confirm which LDAP controls it supports |
| [02-connect-from-linux-or-macos.ps1](02-connect-from-linux-or-macos.ps1) | The authentication decision tree off Windows: Kerberos ticket vs `-AuthType Basic -UseSsl` |

### Users

| Script | Description |
|--------|-------------|
| [03-get-one-user.ps1](03-get-one-user.ps1) | The four identity forms — DN, GUID, SID, sAMAccountName — and piping output back in |
| [04-port-an-rsat-script.ps1](04-port-an-rsat-script.ps1) | Port a real RSAT report by search-replacing the command name |
| [05-export-every-user-to-csv.ps1](05-export-every-user-to-csv.ps1) | Export the whole directory with flat memory — the one-line difference that decides it |
| [06-stale-user-accounts.ps1](06-stale-user-accounts.ps1) | Accounts idle for 90+ days, filtered at the DC, plus the never-logged-on case |
| [07-password-hygiene-report.ps1](07-password-hygiene-report.ps1) | Never-expires, not-required, must-change, no-preauth, locked out, expired |
| [08-scope-a-search-to-one-ou.ps1](08-scope-a-search-to-one-ou.ps1) | `-SearchBase` and `-SearchScope`, and why OneLevel disagrees with your headcount |

### Groups and membership

| Script | Description |
|--------|-------------|
| [09-group-inventory.ps1](09-group-inventory.ps1) | Every group by scope and category, decoded from `groupType` and filtered server-side |
| [10-large-group-membership.ps1](10-large-group-membership.ps1) | The two ways AD hides members: MaxValRange and `primaryGroupID` |
| [11-nested-group-audit.ps1](11-nested-group-audit.ps1) | Flatten a nesting tree in one query; what membership actually grants |
| [12-privileged-access-review.ps1](12-privileged-access-review.ps1) | Effective membership of every Tier-0 group, plus `adminCount` leftovers |
| [13-user-to-group-matrix.ps1](13-user-to-group-matrix.ps1) | Direct membership as a flat CSV, against effective membership per group |

### Computers

| Script | Description |
|--------|-------------|
| [14-stale-computer-accounts.ps1](14-stale-computer-accounts.ps1) | Machines that stopped authenticating — live credentials for hardware nobody can find |
| [15-operating-system-inventory.ps1](15-operating-system-inventory.ps1) | OS census, server build numbers, and anything out of support |

### Any object, and raw LDAP

| Script | Description |
|--------|-------------|
| [16-recently-changed-objects.ps1](16-recently-changed-objects.ps1) | What changed this week, regardless of object class |
| [17-raw-ldap-search.ps1](17-raw-ldap-search.ps1) | `Search-ADxObject`: the primitive the presets are built on, and `-Raw` |
| [18-when-the-filter-refuses.ps1](18-when-the-filter-refuses.ps1) | Every filter ADx rejects, why, and the `-LDAPFilter` that expresses it |
| [19-custom-schema-attributes.ps1](19-custom-schema-attributes.ps1) | `-AllowUnknownProperty` for schema extensions ADx cannot know about |

### Security review

| Script | Description |
|--------|-------------|
| [20-kerberoastable-accounts.ps1](20-kerberoastable-accounts.ps1) | Accounts with SPNs, ranked by password age and privilege; AS-REP roasting too |
| [21-delegation-report.ps1](21-delegation-report.ps1) | Unconstrained, constrained and resource-based delegation, and who is exempt |

### Operations

| Script | Description |
|--------|-------------|
| [22-paging-limits-and-timeouts.ps1](22-paging-limits-and-timeouts.ps1) | `-ResultSetSize`, `-ResultPageSize`, `-Top`, `-All`, `-SearchTimeout` — which knob does what |
| [23-benchmark-against-rsat.ps1](23-benchmark-against-rsat.ps1) | Measure ADx against RSAT on your own domain instead of trusting the README |

### Domain, forest, and policy

| Script | Description |
|--------|-------------|
| [24-domain-and-forest-topology.ps1](24-domain-and-forest-topology.ps1) | Domain and forest identity, all five FSMO roles, every DC, and the OU tree with its GPO links |
| [25-password-policy-review.ps1](25-password-policy-review.ps1) | The default domain policy next to every PSO — interval TimeSpans, `AppliesTo`, and precedence |

### Service accounts and account state

| Script | Description |
|--------|-------------|
| [26-service-account-inventory.ps1](26-service-account-inventory.ps1) | gMSA and sMSA inventory — the accounts whose passwords AD itself rotates |
| [27-account-lifecycle-audit.ps1](27-account-lifecycle-audit.ps1) | Disabled, expired, expiring, inactive, locked out — one criterion switch per call |
| [28-who-belongs-to-what.ps1](28-who-belongs-to-what.ps1) | A principal's groups, including the primary-group membership a `memberOf` read misses |

## Cmdlet coverage

Every ADx cmdlet is demonstrated in at least one script:

| Cmdlet | Scripts |
|--------|---------|
| `Get-ADxUser` | 02, 03, 04, 05, 06, 07, 08, 11, 12, 13, 18, 19, 20, 21, 22, 23, 27, 28 |
| `Get-ADxGroup` | 04, 09, 10, 11, 12, 13, 18 |
| `Get-ADxComputer` | 14, 15, 18, 21 |
| `Get-ADxObject` | 08, 16 |
| `Get-ADxOrganizationalUnit` | 24 |
| `Get-ADxServiceAccount` | 26 |
| `Get-ADxGroupMember` | 04, 10, 11, 12, 13 |
| `Get-ADxGroupNested` | 11 |
| `Get-ADxPrincipalGroupMembership` | 28 |
| `Get-ADxDomain` | 24 |
| `Get-ADxForest` | 24 |
| `Get-ADxDomainController` | 24 |
| `Get-ADxDefaultDomainPasswordPolicy` | 25 |
| `Get-ADxFineGrainedPasswordPolicy` | 25 |
| `Search-ADxAccount` | 27 |
| `Get-ADxRootDse` | 01, 19 |
| `Search-ADxObject` | 01, 10, 17, 19, 22 |

## Three things worth reading before you write your own

**Pipe it, don't collect it.** `$users = Get-ADxUser -Filter *` tells PowerShell to
materialise an array of every result — roughly 3 KB per object, held by PowerShell, not
by ADx. Piping into whatever consumes the data keeps memory flat regardless of
directory size. Example 05 measures it.

**Name your properties.** `-Properties` is the single biggest performance lever in an
LDAP sweep. Without it the DC sends the default set; with `-Properties *` it serialises
every populated attribute on every entry. Ask for what the report needs.

**A refused filter is the feature.** Active Directory answers a structurally valid but
wrong filter with zero rows and a success code, so ADx raises a terminating error
rather than approximating: misspelled property names, `-match`, case-sensitive
operators and undefined variables are all rejected up front. Example 18 covers each
one and the raw-LDAP form that expresses it properly.

## Two known limitations these examples run into

**Multi-domain forests.** `Get-ADxGroupMember` and `Get-ADxGroupNested` enumerate
membership within the target group's own domain partition, so members from other
domains are not returned. Single-domain forests are unaffected; see
`Get-Help Get-ADxGroupMember` for the workaround.

**`-Recursive` returns more than RSAT, deliberately.** It reports *effective*
membership, including principals whose only route into a nested group is
`primaryGroupID`. On the lab domain, `BUILTIN\Users` returns 100 members under RSAT and
3,733 under ADx. Example 12 explains why ADx's answer is the correct one and when you
would want RSAT's instead.
