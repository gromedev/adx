# Changelog

## 0.3.0 — 2026-08-13

Correctness release: every code finding from a four-reviewer audit (filter subsystem, cmdlets
layer, LDAP engine, packaging/docs) fixed, with tests pinning each fix.

### Fixed — silent wrong answers

- Binary-attribute drift: msDS-AllowedToActOnBehalfOfOtherIdentity (RBCD) and
  msDS-GroupMSAMembership (gMSA password ACL) were declared Binary in the schema but missing
  from the client's forced-byte[] set, silently projecting null whenever the blob decoded as
  valid UTF-8. The set is now derived from the schema (drift-guard test added).
- DateTime skew: Kind=Unspecified DateTimes (what [datetime]'...' produces) marshalled as UTC
  while identical date strings marshalled as local — the same timestamp via variable vs string
  produced filters an offset apart. Unspecified is now local, matching RSAT and .NET.
- LockedOut over-reporting: the projected property read lockoutTime > 0, which persists after
  the lockout window expires; it now reads the DC-computed ADS_UF_LOCKOUT bit, matching
  Get-ADUser. Filter-side LockedOut and Search-ADxAccount -LockedOut keep lockoutTime
  (the computed attribute is not filterable) — documented divergence, like PasswordExpired's.
- Get-ADxPrincipalGroupMembership WS01 failed where RSAT succeeds: the computer '$'-suffix
  retry its schema declared was never applied on this path.
- Global Catalog over-reporting: on port 3268/3269 the domain-relative primaryGroupID arms
  matched other domains' accounts (child-domain RID-513 users as members of the root's Domain
  Users). GC binds now drop the RID arms and warn.
- Foreign-member warnings inspected only the first MaxValRange block, so a cross-domain member
  past index 1499 evaded detection; the range walk completes before the check.

### Changed — silent divergences made loud

- '*' in an -eq/-ne value is a terminating error. RSAT passes it through as an LDAP wildcard
  ("mail -ne '*'" = mail absent); PowerShell -eq means a literal asterisk; the previous silent
  escaping inverted the RSAT idiom's result set. The error offers -like/-notlike, $null
  presence tests, and the -LDAPFilter escape spelling.
- -ResultSetSize warns when it truncates (RSAT errors; ADx previously capped silently).
- Sub-second GeneralizedTime bounds (whenCreated-class) round direction-aware — exact against
  whole-second storage — and sub-second equality is refused; FILETIME bounds keep full 100ns
  precision. Pre-1601 timestamps are a clean translation error.
- The paging empty-page guard announces abandonment through the warning channel; Search-ADxObject
  no longer emits a spurious one-page warning when a result set is exactly one page.

### Added

- -approx (RSAT grammar, LDAP '~='); -RecursiveMatch on any DN-valued attribute (manager
  chains); underscore attribute names (legal in ldapDisplayName).
- GUID identities resolve through AD's <GUID=...> extended-DN read, reaching configuration and
  schema partitions like RSAT; scoped search remains the fallback and the -SearchBase path.
- System.Security.Principal.SecurityIdentifier (RSAT's own SID type) accepted as -Identity;
  SID identities are gated to security-principal cmdlets, matching which RSAT counterparts
  accept them.
- ConnectTimeoutSeconds is consumed (was validated, documented, and never read); SizeLimit-
  truncated responses salvage their partial page.

### Fidelity

- Description projects as a scalar string (RSAT flattens; the array broke Export-Csv and
  .Substring); Integer-syntax attributes project as Int32 (new LargeInteger syntax keeps
  uSNCreated/uSNChanged Int64); AccountDomainSid is null for non-account SIDs, matching
  SecurityIdentifier (no more fabricated "S-1-5-32" for BUILTIN principals).
- GroupCategory -ne 'Distribution' emits a single negation; c-prefixed unsupported operators
  get tailored messages in both tokenizer encodings; LdapEntry's public constructor enforces
  the case-insensitive-key promise; a Ctrl-C race against the disposed cancellation source is
  fixed.

### Docs / packaging

- The private agent-runner scripts under tests/scripts/ are untracked (they predated their
  ignore rule and shipped with the public repo); .github/ is no longer ignored, unblocking CI.
- README documents the deliberate RSAT divergences in their own section and resolves the
  scale-validation contradiction; examples cover all 17 cmdlets (24–28 added); help synced
  (unsupported-property list, LockedOut semantics, Search-ADxAccount windows) and the external
  help XML regenerated.

## 0.2.7 — 2026-08-12

### Added

- Cmdlets: Added Get-ADxServiceAccount, Get-ADxFineGrainedPasswordPolicy, and Search-ADxAccount.
- Get-ADxServiceAccount
  - Matches sMSA and gMSA via shared base class msDS-ManagedServiceAccount.
  - Get-ADxUser/Get-ADxComputer now reject managed service accounts and direct to this cmdlet.
  - PrincipalsAllowedToRetrieveManagedPassword ACL declared unsupported due to RSAT resolution gaps.
- Get-ADxFineGrainedPasswordPolicy
  - Reads msDS-PasswordSettings objects from the Password Settings Container.
  - Maps duration attributes to TimeSpan.
  - Resolves identity by policy name, DN, or GUID (adds name resolution to resolver).
- Search-ADxAccount
  - Switch-driven parameter sets: -AccountDisabled, -AccountExpired, -AccountExpiring, -AccountInactive, -LockedOut, -PasswordExpired, -PasswordNeverExpires.
  - Scoped via -UsersOnly / -ComputersOnly.
  - Generates specific LDAP filters except -PasswordExpired (evaluated client-side via computed msDS-User-Account-Control-Computed).

### Fixed / Behavioral Corrections

- Search-ADxAccount RSAT Parity Alignment
  - -AccountInactive includes accounts with no lastLogonTimestamp (never logged on).
  - -PasswordExpired excludes accounts flagged for "must change password at next logon" (pwdLastSet = 0).
  - Default unscoped population matches objectClass=user (includes service accounts, not just users/computers).

## 0.2.6 — 2026-08-12

### Added

- Cmdlets: Added Get-ADxOrganizationalUnit, Get-ADxDefaultDomainPasswordPolicy, Get-ADxDomain, Get-ADxForest, and Get-ADxDomainController.
- Get-ADxOrganizationalUnit
  - Maps LinkedGroupPolicyObjects from gPLink to an ordered array of GPO DNs.
  - Added schema per-type attribute overrides so StreetAddress resolves to street for OUs and streetAddress for users.
- Topology Cmdlets
  - Read configuration partition into fixed-shape objects.
  - Properties that cannot be accurately produced are omitted entirely rather than returned as $null.
  - Get-ADxDomain: Resolves PDC/RID/Infrastructure FSMO hostnames, parses well-known containers, and lists replica directory servers.
  - Get-ADxForest: Exposes forest mode, schema/domain-naming masters, domains, GCs, and sites.
  - Get-ADxDomainController: Returns DCs via -Identity or -Filter * with OperationMasterRoles, IsGlobalCatalog, IsReadOnly, and site info.
- Data Types: Interval/duration attributes (maxPwdAge, minPwdAge, lockoutDuration, lockOutObservationWindow) stored as negative 100ns ticks now decode to positive TimeSpan objects. Filtering on intervals in -Filter is explicitly rejected.
- Unsupported Limits: Get-ADxDomainController -Discover, IPv4Address, and IPv6Address explicitly throw errors explaining non-LDAP limitations.

### Fixed

- Added missing -Properties argument completer to Get-ADxPrincipalGroupMembership.

## 0.2.5 — 2026-08-11

### Added

- Get-ADxPrincipalGroupMembership: Added reverse group membership cmdlet.
  - Resolves primary groups by SID manipulation (replacing RID in account SID with primaryGroupID). Emits a warning if SID or primaryGroupID is unreadable.
  - Uses member searches instead of memberOf attributes to prevent MaxValRange truncation on accounts with >1,500 groups.
  - Emits Global-Catalog-aware warnings when membership spans into external, unsearched forest partitions.

## 0.2.4 — 2026-08-11

### Changed

- Cross-Domain Member Warnings: Get-ADxGroupMember and Get-ADxGroupNested now check raw member attributes during resolution and issue warnings naming foreign-domain members that cannot be traversed via single-partition memberOf searches.

### Fixed

- Multi-Domain Ambiguity: Binding Global Catalog endpoints (-Port 3268) throws ADxIdentityAmbiguous when search names (e.g., Administrators) exist in multiple domains.

## 0.2.3 — 2026-08-11

### Fixed

- MaxValRange Race Condition: Fixed an issue where LdapEntry could overwrite a fully retrieved >1,500 member list with an empty member attribute depending on per-process hashtable enumeration order.
- Pipeline Binding: Piped objects containing a DistinguishedName property (including native RSAT ADUser/ADGroup and [pscustomobject]) now bind properly to -Identity without throwing "cannot be a PSCustomObject".

### Changed

- Get-ADxGroupMember -Recursive: Expanded recursion to include primaryGroupID relationships across nested groups, capturing effective domain membership (e.g., returning all domain users when evaluating BUILTIN\Users).

## 0.2.2 — 2026-08-11

### Fixed

- Non-Windows Auth Error Messaging: When using -AuthType Negotiate or Kerberos with -Credential on Linux/macOS, errors now explicitly explain platform SASL/GSSAPI limitations and suggest -AuthType Basic -UseSsl or using kinit.

## 0.2.1 — 2026-08-11

### Fixed

- Primary Group Recursion: Get-ADxGroupMember -Recursive now collects nested groups' RIDs to traverse accounts linked via primaryGroupID.
- Builtin Group Scope: Builtin groups (groupType 0x80000005) now correctly report GroupScope as DomainLocal instead of Unknown.
- Multi-Valued SIDs: -Properties SIDHistory now returns array sets instead of truncating after the first SID.
- Identity Ambiguity: -Identity calls that match multiple objects (e.g., duplicate sAMAccountNames across Global Catalogs) now throw ADxIdentityAmbiguous.
- Derived Class Matching: -Identity <DN> now properly accepts derived schema classes (e.g., inetOrgPerson under Get-ADxUser).
- Truncated Range Warnings: Warns callers if a MaxValRange walk ends early due to object deletion, disconnects, or loop guards.
- Filter Operator Parsing: Standardized case-insensitive operator handling (-ieq, -ilike, etc.) between parenthesized and unparenthesized filters.
- Service Account Rejection: Get-ADxComputer -Identity <DN> now rejects managed service accounts.

### Added

- Cleartext credential warning thrown when using -AuthType Basic without -UseSsl or on unsigned non-Windows connections.

## 0.2.0 — 2026-08-11

### Added

- Initial Cmdlet Set: Get-ADxUser, Get-ADxGroup, Get-ADxComputer, Get-ADxObject, Get-ADxGroupMember, and Get-ADxGroupNested.
- Get-ADxGroupNested: Flattens group hierarchies server-side in a single request (no direct RSAT counterpart).
- Filter Translation: Translates PowerShell filter syntax (-eq, -like, -band, -recursivematch, variables, expressions) into LDAP filter strings.
- Range Retrieval: Automatic multi-page retrieval for attributes exceeding MaxValRange (member;range=0-1499).
- Property System: Dual RSAT/LDAP property mapping, constructed property exclusions on -Properties *, and computed synthetic booleans (Enabled, PasswordNeverExpires, LockedOut, PasswordExpired).
- MAML help documentation across all cmdlets.

### Changed

- Unsupported filter operators (-match, -in, -contains, -replace) or invalid properties throw terminating errors rather than returning empty result sets.
- Output formatting mimics RSAT (PascalCase properties, single-string ObjectClass, local DateTime, SID objects).
- Default result set sizes changed to unlimited to mirror RSAT defaults.

## 0.1.0 — 2026-08-10

### Added

- Get-ADxRootDse: Queries RootDSE metadata (naming contexts, functional levels, supported controls) without requiring a domain join.
- Search-ADxObject: Low-level LDAP search wrapper supporting RFC 2696 paging, explicit attribute sets, and -All streaming.
- Cross-platform support for Linux and macOS via native LDAP libraries.

### Fixed

- Fixed an issue where range-suffixed attributes (member;range=0-1499) were assigned to literal property names instead of merging into standard properties.
