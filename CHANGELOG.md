# Changelog

## 0.2.7 — 2026-08-12

### Added — three more read cmdlets (service accounts, fine-grained policies, account search)

`Get-ADxServiceAccount`, `Get-ADxFineGrainedPasswordPolicy`, and `Search-ADxAccount` — drop-ins
for `Get-ADServiceAccount`, `Get-ADFineGrainedPasswordPolicy`, and `Search-ADAccount`. Seventeen
cmdlets total.

`Get-ADxServiceAccount` closes a promise the module already made: `Get-ADxUser`/`Get-ADxComputer`
reject managed service accounts and point at this cmdlet, which now exists. Standalone (sMSA) and
group-managed (gMSA) accounts are matched by their shared base class `msDS-ManagedServiceAccount`
— which a gMSA inherits — so one filter returns both and nothing else. The gMSA
password-retrieval ACL (`PrincipalsAllowedToRetrieveManagedPassword`) is declared unsupported: a
security descriptor whose trustees RSAT resolves to principals, the same gap as the delegation ACL.

`Get-ADxFineGrainedPasswordPolicy` reads PSO objects (`msDS-PasswordSettings`) from the Password
Settings Container, reusing 0.2.6's interval handling so the ages come back as `TimeSpan`s.
`AppliesTo` is the forward-linked DN list. Identity resolves by policy name, DN, or GUID — the
first cmdlet to resolve identity by `name`, a small general addition to the resolver.

`Search-ADxAccount` is switch-driven, matching RSAT: `-AccountDisabled`, `-AccountExpired`,
`-AccountExpiring`, `-AccountInactive`, `-LockedOut`, `-PasswordExpired`, `-PasswordNeverExpires`,
each its own parameter set, scoped by `-UsersOnly`/`-ComputersOnly`. Every criterion emits a
specific LDAP filter (UAC bit-tests, FILETIME range bounds, `lockoutTime`) except
`-PasswordExpired`: its bit lives in the constructed `msDS-User-Account-Control-Computed`, which
AD cannot match in a search filter, so that one criterion reads the in-scope population and
filters each object client-side. The window boundaries are directional (`-AccountExpiring` looks
forward, `-AccountInactive` back), and getting one inverted would be a zero-row success — so each
is pinned by an exact golden filter test.

Three of RSAT's `Search-ADAccount` behaviours turned out to differ from their documentation and
were corrected against a live DC: `-AccountInactive` **includes** never-logged-on accounts (no
`lastLogonTimestamp`); `-PasswordExpired` **excludes** must-change-at-next-logon accounts
(`pwdLastSet = 0`) even though they set the same computed bit that `Get-ADUser`'s PasswordExpired
property reports — a divergence within RSAT itself, so Search matches Search; and the unscoped
default population is every account (`objectClass=user`), which **includes managed service
accounts**, not just users and computers.

### Live validation
Validated natively on the DC under integrated auth with full RSAT parity: `Get-ADxServiceAccount`
(every field of all three lab gMSAs), `Get-ADxFineGrainedPasswordPolicy` (every field of a seeded
PSO including `AppliesTo` and identity-by-name), and `Search-ADxAccount` (every criterion's result
set matches `Search-ADAccount` exactly, all scopes).

## 0.2.6 — 2026-08-12

### Added — five RSAT-compatible read cmdlets (Tier-1 completion)

`Get-ADxOrganizationalUnit`, `Get-ADxDefaultDomainPasswordPolicy`, `Get-ADxDomain`,
`Get-ADxForest`, and `Get-ADxDomainController` — drop-ins for `Get-ADOrganizationalUnit`,
`Get-ADDefaultDomainPasswordPolicy`, `Get-ADDomain`, `Get-ADForest`, and
`Get-ADDomainController`. Fourteen cmdlets total.

`Get-ADxOrganizationalUnit` is a preset like the other object cmdlets: DN/GUID identity only
(matching RSAT), the full `-Filter`/`-LDAPFilter`/`-SearchBase`/`-SearchScope`/`-Properties`
surface, `LinkedGroupPolicyObjects` parsed from `gPLink` into an ordered array of GPO DNs. Its
one subtlety is `StreetAddress`: for an OU that is the LDAP `street` attribute, where for a user
it is `streetAddress`. This is carried by a new per-type attribute-override in the schema, so the
OU reads the right attribute while user projection is completely unchanged — the alternative
(one global mapping) would have silently returned the wrong attribute for one type or the other.

The four topology cmdlets are the module's first readers of the configuration partition. They
build fixed-shape objects rather than attribute projections, and follow the honest-subset rule:
every property is produced from a real read, and properties ADx cannot produce faithfully are
**omitted and documented, never returned as null**. `Get-ADxDomain` resolves the PDC/RID/
Infrastructure FSMO holders to hostnames, parses the well-known containers, and lists the
domain's (read-only) replica directory servers. `Get-ADxForest` gives the forest mode, the
schema/domain-naming masters, the domains, global catalogs, and sites. `Get-ADxDomainController`
returns the connected DC, one DC by `-Identity`, or the domain's DCs by `-Filter *`, each with
its `OperationMasterRoles`, `IsGlobalCatalog`, `IsReadOnly`, and site.

Declared unsupported (an explicit error naming the reason, not a null): `Get-ADxDomainController
-Discover` (the DC locator is the netlogon/CLDAP mailslot protocol, not LDAP) and its
`IPv4Address`/`IPv6Address` (client-side DNS, the same gap the computer preset documents).

### Added — Interval attribute syntax

Duration attributes (`maxPwdAge`, `minPwdAge`, `lockoutDuration`, `lockOutObservationWindow`)
are stored as negative 100ns-tick intervals and must not be decoded as FILETIME timestamps —
FILETIME treats any value ≤ 0 as a "never" sentinel, which would have silently nulled every one
of them. They now decode to positive `TimeSpan`s exactly as RSAT emits them. Filtering on an
interval attribute in `-Filter` is rejected with an explanation rather than marshalled as text
(which would successfully match zero rows).

### Fixed
`Get-ADxPrincipalGroupMembership` was missing its `-Properties` argument completer (an oversight
from 0.2.5); it now has one like the other membership cmdlets.

### Live validation
Validated against a real two-domain forest (Windows Server 2025 DCs, `pentest.lab` +
`child.pentest.lab`). First from macOS over LDAPS, then **natively on the domain controller under
integrated auth with full RSAT side-by-side parity**: every shared field of all five cmdlets
matches `Get-AD*` exactly (50 of 50 checked, zero diffs) — the interval `TimeSpan`s, the domain
SID, the `Windows2016Domain`/`Windows2016Forest` modes (Server 2025 adds no new level), every
FSMO holder resolved to a hostname, all eight well-known containers, the replica lists, the
forest spanning both domains' global catalogs, the DC's `OperationMasterRoles`/`IsReadOnly`/
`IsGlobalCatalog`, and the OU's linked-GPO DN. The OU default property name set matches RSAT's
read surface exactly (RSAT's change-tracking bookkeeping properties are correctly absent from a
read-only module). The multi-domain machinery is confirmed both directions: bound to the root DC,
`Get-ADxForest` spans both domains and both GCs (the child DC's computer object is in an unhosted
partition and returns a referral the enumeration now tolerates rather than aborting), and
`Get-ADxDomain` against the child resolves `ParentDomain` back to the root.

Not exercised (fixture gap, not a code gap): a read-only domain controller for the `IsReadOnly`
= true path — the lab has no RODC. The `-Tag Live` suite carries the full parity assertions.

## 0.2.5 — 2026-08-11

### Added — Get-ADxPrincipalGroupMembership
A drop-in replacement for RSAT's `Get-ADPrincipalGroupMembership`, and the reverse of
`Get-ADxGroupMember`: given a user, computer, group or service account, return the groups it
belongs to. Ninth cmdlet.

The correctness point that justifies a cmdlet rather than a one-line `memberOf` read is the
**primary group**. Every ordinary account's primary group is Domain Users, a membership stored
only in `primaryGroupID` that appears in neither the account's `memberOf` nor the group's
`member` — a plain `memberOf` read misses it. This reconciles it the way RSAT does, by matching
the primary group by SID (the account's own domain SID with the RID replaced by
`primaryGroupID`); if the SID or `primaryGroupID` is unreadable the primary group is omitted
with a warning, not silently. Enumeration is a `member` search rather than a `memberOf` read, so
it is immune to MaxValRange (a principal in more than 1,500 groups comes back complete).
Verified live against RSAT on three lab domains: identical group sets and values for users,
computers (whose only membership, Domain Controllers, is reachable *only* via `primaryGroupID`),
and accounts whose sole group is their primary — including cross-platform from macOS over LDAPS.

Same partition boundary as `Get-ADxGroupMember`, handled the same warn-don't-drop way: a
membership in another forest domain is not returned by the single-partition search, and where
the principal's own `memberOf` surfaces it (against a Global Catalog, which replicates
universal-group membership forest-wide) it is **named in a warning** rather than dropped. That
warning is Global-Catalog-aware: a GC subtree search from the forest root *does* return
partitions namespace-subordinate to the base (a child domain sits under the root), so those are
no longer falsely warned about — only a genuinely-excluded partition (a plain 389/636 bind, or a
parent/other-tree partition on a GC) warns. Caught and fixed against a live two-forest GC.

## 0.2.4 — 2026-08-11

### Changed — cross-domain group members are now surfaced, not silently dropped
`Get-ADxGroupMember` / `Get-ADxGroupNested` enumerate membership with a `memberOf` search under
one domain partition. That is immune to MaxValRange and includes primary-group members, but it
is structurally blind to members in **other domains of the forest** — proven on a two-forest
lab: a cross-domain membership is stored only as a forward `member` link in the group's
partition, with **no `memberOf` back-link maintained anywhere**, so no single-partition search,
and not a Global Catalog bind either (both return zero), can reach it. Previously the cmdlet
returned a partition-complete set that looked total.

It now reads the group's own `member` attribute on the resolution round trip (no extra request)
and **warns** when any member lives in another domain partition, naming the foreign members and
the partition searched, and pointing at `Get-ADxGroup -Identity <group> -Properties Members` —
which returns every member DN verbatim, foreign ones included. Warning-only: the returned
objects are unchanged, so nothing that consumes them breaks. Positive-detection only: a group
whose members are all local never warns, and single-domain forests are entirely unaffected.
Verified live — child `BUILTIN\Administrators` warns about the root's `Enterprise Admins`; a
same-domain group and a single-domain DC stay silent.

Full cross-domain resolution (bind each foreign partition, resolve, merge) was deliberately not
built: it needs reachability and credentials to every domain, which is the wrong trade for a
fast single-domain collector. Making the gap visible is the fix that matches this module's rule
against silent wrong answers.

Also confirmed on the same lab (no code change needed): binding the Global Catalog
(`-Server <root-dc> -Port 3268`) correctly raises `ADxIdentityAmbiguous` when a name like
`Administrators` matches a group in more than one domain — the 0.2.1 ambiguity guard, now
exercised against a real multi-domain GC instead of only a unit test.

## 0.2.3 — 2026-08-11

First release cut after end-to-end validation against a live domain controller at scale: a
500,000-user domain for the streaming/memory headline, and the first native-Windows sweep
(RSAT parity diff, integrated auth, Windows PowerShell 5.1 rejection). That sweep found the
two defects below — neither reachable by the 494 offline unit tests, both a wrong answer
returned successfully, both fixed and re-verified on the wire.

### Fixed — groups past MaxValRange could read as EMPTY, per-process
For a group with more than 1500 members, Active Directory returns **both** the ranged key
(`member;range=0-1499`, holding the first page) **and** a plain `member` attribute with zero
values. That empty sibling is a server artifact — but which key ADx's range-completion merge
encountered first depended on hashtable enumeration order, which .NET randomises **per
process**. The completed 1,700-member walk could be overwritten by the empty sibling, so
`(Get-ADxGroup big -Properties Members).Members` returned `0` in roughly half of all
processes — deterministically within each process, flip-flopping between them. The worst
failure class this project recognises: a confidently wrong answer, returned successfully, on
the exact feature (complete member lists past MaxValRange) the module advertises.

Two-layer fix: `LdapEntry`'s constructor now drops an empty plain sibling of any ranged key
at the model boundary, so no consumer can ever see the artifact; and the range retriever's
merge no longer lets a plain sibling overwrite a completed walk regardless of enumeration
order. Regression tests pin both, in both insertion orders. Found live on a Windows DC when
the same query returned 0 in one process and 1700 in the next; unreachable by the offline
suite because the fake executor never emitted the dual shape a real DC sends.

### Fixed — piping ADx (or RSAT) objects into `-Identity` threw
`Get-ADxGroupNested 'App-Owners' | Get-ADxGroupMember` failed with "-Identity cannot be a
PSCustomObject". The identity resolver assumed the DistinguishedName-alias
ValueFromPipelineByPropertyName binding would run for piped objects, but PowerShell attempts
whole-object binding first and `-Identity` is typed `object`, so the whole output object
always arrived. Any piped object carrying a string `DistinguishedName` property — ADx output,
RSAT's `ADUser`/`ADGroup`, or a `[pscustomobject]` — now classifies as a DN identity, making
`Get-ADxGroup ... | Get-ADxGroupMember` and cross-module pipes work as RSAT users expect.
Found composing the nested-group audit pattern on a live DC; the offline suites never piped
one cmdlet's output into another.

### Changed — `-Recursive` returns MORE than RSAT, deliberately
`Get-ADxGroupMember -Recursive` now reports **effective** membership, which means it can
return substantially more objects than `Get-ADGroupMember -Recursive`. Measured on a test
domain of 3,731 users: RSAT returns **100** members of `BUILTIN\Users`, ADx returns **3,733**.

This is not a bug in either direction, and 0.2.1 described it incorrectly as an RSAT-parity
fix. The truth is narrower and more interesting:

- Normal nesting behaves identically in both tools. Three groups nested three deep with three
  members each returns nine members in both.
- The divergence is confined to **primary group membership**, which AD does not store in a
  group's `member` attribute at all -- it is a `primaryGroupID` number on the user. Nearly
  every account in a domain belongs to Domain Users this way.
- RSAT resolves `primaryGroupID` for the group you name, but not for groups nested inside it.
  So `Get-ADGroupMember 'Domain Users'` correctly returns all 3,731 users, while
  `Get-ADGroupMember 'Users' -Recursive` returns 100 -- omitting those same users even though
  Domain Users is a member of BUILTIN\Users. RSAT contradicts itself between those two
  answers; ADx is consistent across both.

Verified against a live DC: an ordinary user is confirmed by RSAT to be in Domain Users, and
Domain Users is confirmed to be in BUILTIN\Users, yet RSAT's recursive enumeration of
BUILTIN\Users omits that user. ADx includes it.

If you need byte-for-byte RSAT output rather than the true answer, use `-Recursive` against
the specific group instead of a parent that nests it, or compare on explicit `memberOf` links
only. Raise an issue if a per-call opt-out would help.

## 0.2.2

### Fixed
- `-AuthType Negotiate` or `Kerberos` together with `-Credential` failed on Linux and macOS
  with only "The feature is not supported". The LDAP client library there cannot perform a
  SASL/GSSAPI bind from a supplied username and password -- Windows can only because it
  brokers through SSPI. Since Negotiate is the default, this was the first thing a
  non-Windows caller hit, with no indication of the cause or the way out. The error now names
  the platform limitation and both alternatives: `-AuthType Basic -UseSsl`, or `kinit`
  followed by omitting `-Credential`. Found on first contact with a real domain controller.

## 0.2.1

Correctness fixes from the first full code review of the 0.2.0 tree. Every one of these was a
case of returning a wrong answer rather than raising an error, which is the failure mode this
module is built to avoid.

### Fixed
- `Get-ADxGroupMember -Recursive` missed members whose PRIMARY group was a group nested inside
  the target. Primary membership creates no `member`/`memberOf` link, so matching rule
  1.2.840.113556.1.4.1941 cannot traverse it, and only the target group's own RID was being
  matched. `BUILTIN\Users` contains `Domain Users` by default in every domain, so
  `Get-ADxGroupMember Users -Recursive` returned almost none of the domain's users. The nested
  groups' RIDs are now collected and matched as well
- `GroupScope` reported `Unknown` for builtin groups. Their `groupType` (`0x80000005`) sets
  both the builtin-local and resource bits, and the decoder matched the low nibble as a whole
  instead of testing bits. Builtin groups now report `DomainLocal`, matching RSAT and agreeing
  with the filter (`GroupScope -eq 'DomainLocal'` already matched them)
- Multi-valued SID attributes returned only their first value: `-Properties SIDHistory` on a
  twice-migrated account reported one SID and silently dropped the rest
- `-Identity` returned an arbitrary object when the value matched more than one - reachable
  against a Global Catalog (port 3268), where `sAMAccountName` is not forest-unique. Ambiguous
  identities are now a terminating `ADxIdentityAmbiguous` error
- `-Identity <DN>` rejected objects of derived classes that the same cmdlet returned via
  `-Filter`: an `inetOrgPerson` account was `ObjectNotFound` by DN but found by filter. The
  object-class check now tests chain membership, the way the wire filter does, while still
  rejecting computers from `Get-ADxUser`
- Range retrieval returned truncated values silently when a walk ended early (the object was
  deleted mid-walk, the server stopped answering, or a loop guard tripped). Each of those
  paths now warns that the value set may be incomplete
- `-Filter` accepted the explicitly case-insensitive operators (`-ieq`, `-ilike`, ...) only
  when parenthesized, and errored on the identical unparenthesized filter. The unsupported
  operators' i-forms (`-imatch`, `-icontains`, ...) also now explain themselves identically in
  both encodings
- `-Identity <DN>` on `Get-ADxComputer` accepted managed service accounts (gMSA/sMSA), which
  derive from the computer class but which the same cmdlet's `-Filter`
  (`objectCategory=computer`) excludes - RSAT points those at `Get-ADServiceAccount`

### Added
- A warning when credentials cross the wire unprotected: `-AuthType Basic` without `-UseSsl`
  sends the password in cleartext, and on Linux/macOS LDAP signing/sealing is unavailable, so
  those connections are unsigned. Both were previously silent

### Known limitation
- Group membership is enumerated by searching `memberOf` within the target group's own domain
  partition. In a MULTI-DOMAIN forest, members from other domains are not returned - RSAT's
  `Get-ADGroupMember` resolves those by walking the `member` DNs. Single-domain forests are
  unaffected. See `Get-ADxGroupMember`'s help for the workaround

## 0.2.0

The RSAT-compatible read cmdlets: `Get-ADxUser`, `Get-ADxGroup`, `Get-ADxComputer`,
`Get-ADxObject`, `Get-ADxGroupMember`, plus `Get-ADxGroupNested`. Existing RSAT scripts are
expected to port by search-replacing the command name.

### Added — group membership
- `Get-ADxGroupMember`: direct members by default, or the flattened hierarchy with
  `-Recursive` (matching rule 1.2.840.113556.1.4.1941, nested groups removed). Enumerates by
  searching `memberOf`, so it is immune to the `MaxValRange` cap and folds in PRIMARY-group
  members - users whose `primaryGroupID` is the group (every account in Domain Users), which a
  plain `member` read misses entirely
- `Get-ADxGroupNested`: every group nested at any depth inside the target, flattened - the
  "what does membership here actually grant" audit query, in one server-side round trip. No
  RSAT counterpart
- Range retrieval: multi-valued attributes past `MaxValRange` (returned by AD as
  `member;range=0-1499`) are completed with follow-up reads before projection, so the presets
  return complete `Members`/`MemberOf` collections

### Added
- RSAT `-Filter` expression syntax, translated to server-side LDAP: `-eq -ne -like -notlike
  -gt -ge -lt -le -and -or -not -band -bor -recursivematch`, parentheses, variables (resolved
  from session state), variable member access (`$u.DistinguishedName`), and expandable strings
  (`"*$dept*"`). Values are marshalled by attribute syntax - dates to FILETIME or
  GeneralizedTime, SIDs and GUIDs to binary - so typed comparisons match what the directory
  stores. `-LDAPFilter` remains the raw escape hatch on every preset
- `-Identity`: DN (single base-scope read, with client-side object-class verification), GUID
  (D/N formats), SID, and sAMAccountName, detected in that order; computers retry the `$`
  suffix. Pipes by value and by `DistinguishedName` property
- `-Properties` accepts RSAT names and LDAP names interchangeably; asking by LDAP name emits
  the value under both. `-Properties *` sends the literal `*` and - exactly like RSAT -
  excludes constructed attributes. Synthetic booleans (`Enabled`, `PasswordNeverExpires`,
  `LockedOut`, `PasswordExpired` from the computed UAC, ...) are filterable and projected
  from one shared table
- RSAT output fidelity: PascalCase names, `ObjectClass` as the single most specific class,
  dates as local `DateTime`, `SID` as an object with `.Value`, requested-but-absent
  properties present with null. Per-type list views matching RSAT's rendering
- MAML help for all six cmdlets

### Changed
- Filters and property lists that cannot be honoured faithfully are terminating errors with
  explanations, never silent approximations: misspelled names (AD returns zero rows with
  success for those), case-sensitive operators, `-match`/`-in`/`-contains`/`-replace`,
  undefined variables, computed expressions, `-like` on DN-valued attributes. The
  `-AllowUnknownProperty` switch passes schema extensions through deliberately
- Presets return unlimited results by default, matching RSAT's `-ResultSetSize` default;
  `Search-ADxObject` keeps its explicit one-page default with a warning

## 0.1.0

- `Get-ADxRootDse`: probe a domain controller's RootDSE for naming contexts, functional level, and supported controls (paged results, DirSync). Works without a domain join, and reports plainly when the server is not an Active Directory DC
- `Search-ADxObject`: generic LDAP search with RFC 2696 server-side paging, explicit attribute lists, `-All` streaming, and range retrieval for attributes that exceed `MaxValRange`
- Cross-platform: runs on Linux and macOS over plain LDAP, where the Windows-only `ActiveDirectory` module cannot run at all
- Fixed range-suffixed attributes being emitted under their raw key. A group with over 1,500 members returns `member;range=0-1499`, which previously surfaced as a property of that literal name, leaving `$group.member` null
