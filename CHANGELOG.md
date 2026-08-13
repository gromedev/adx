# Changelog

## 0.4.0

### Fixed

- Fixed Get-ADxDomainController metadata for cross-domain DCs. Domain now reflects the DC's actual config partition, IsReadOnly derives from the nTDSDSARO object class, and foreign FSMO roles return $null with a warning rather than an empty list. A DC whose home domain cannot be determined at all (missing serverReference) also gets $null Domain and roles with a warning, never the bound domain's confident values.

### Changed

- Identity-not-found errors on all cmdlets are now terminating errors, matching native RSAT behavior and allowing try/catch blocks to function as expected. Note that -ErrorAction SilentlyContinue no longer silences the miss — use try/catch for existence checks (see examples/12).
- -SearchBase and -SearchScope now constrain identity resolution across all identity forms: an explicit -SearchBase or an explicitly narrowed -SearchScope routes distinguished-name and GUID identities through the scoped search instead of the base-read fast path (which is kept only for the unconstrained default, where it also reaches configuration/schema partitions).
- Assemblies now stamp FileVersion and InformationalVersion from the manifest ModuleVersion on build, while AssemblyVersion remains pinned. The module loader now warns if a stale assembly version is already loaded in the session.
- Documented the deliberate TimeSpan.MaxValue interval sentinel behavior in the README (preserving "never" values that RSAT collapses to 00:00:00).

### Added

- Added an OpenLDAP integration job to CI covering client binding, host:port parsing, wire paging, SizeLimit handling, connect timeouts, and cancellation against a live directory server.
- Added macOS to the automated CI test matrix.
- Updated README with documented divergences for identity scoping, foreign-DC attributes, interval sentinels, and msDS-UserPasswordExpiryTimeComputed DateTime conversion.

## 0.3.4

### Fixed

- Fixed -Filter ignoring per-type attribute mappings. Filters on Get-ADxFineGrainedPasswordPolicy (e.g., Precedence, MinPasswordLength, ComplexityEnabled) and Get-ADxOrganizationalUnit (StreetAddress) now map to their correct underlying LDAP attributes instead of defaulting to domain-head or user attributes. (Interval-valued properties such as LockoutDuration remain unfilterable by design and now resolve to the correct loud refusal.)
- Fixed provider-drive variables (such as $env:COMPUTERNAME) in -Filter throwing "not defined" errors.

### Changed

- Filtering on constructed attributes (tokenGroups, msDS-User-Account-Control-Computed, primaryGroupToken) is refused loudly with a redirect instead of emitting a filter AD never evaluates.

## 0.3.3

### Fixed

- Fixed forced-binary attributes projecting as UTF-8 corrupted strings. tokenGroups now decodes to SIDs; thumbnailPhoto, jpegPhoto, and logonHours decode to byte arrays; and schemaIDGUID and attributeSecurityGUID decode to GUIDs.
- Fixed tokenGroups (and primaryGroupToken) silently missing from search-path results: the DC computes these only for base-scope reads, so when explicitly requested via -Properties they are now filled in with a follow-up base read per entry — a search-resolved identity no longer projects a confidently empty group list.
- Fixed msDS-User-Account-Control-Computed projecting as String instead of Int32.

## 0.3.2

### Fixed

- Fixed ConnectTimeoutSeconds being ignored on Linux and macOS by adding a managed deadline to enforce the connect budget across all platforms. A timed-out connect is no longer retried (the budget was already spent; retries only stacked delays), and the bind worker runs on a dedicated thread so an abandoned bind against a dead or tar-pitted host cannot consume thread-pool workers.
- Fixed Ctrl-C cancellation freezing during synchronous connection and bind calls by running the bind off the pipeline thread with active cancellation handling.
- Fixed a connection leak in ConnectAsync when setup throws after creating a native connection.

### Added

- Added regression tests for the 0.3.0 Ctrl-C and object disposal race conditions.

## 0.3.1

### Fixed

- Fixed server strings with embedded ports (e.g., -Server host:3268) bypassing Global Catalog result-shape safeguards, TLS rules, and diagnostics. The embedded port now drives the effective port, the TLS scheme, and the GC protections. Explicit port conflicts throw terminating errors, and IPv6 literals are properly parsed.
- Fixed a mis-set USERDNSDOMAIN carrying an embedded ":port" silently choosing the port on the discovery path; it is now rejected with guidance (a DNS domain name cannot carry a port).
- Fixed connection verbose log messages formatting duplicate ports (e.g., "host:3268:389").

### Changed

- Ports 636 and 3269 now imply LDAPS when -UseSsl is not explicitly bound, whether the port arrives via -Port or embedded in -Server (in 0.3.0 this attempted a plaintext bind against the LDAPS port and failed); an explicit -UseSsl:$false still wins.

## 0.3.0

### Fixed

- Fixed Global Catalog primaryGroupID queries matching accounts in external domains on ports 3268/3269. GC binds now drop RID arms and warn.
- Fixed foreign-member warning detection in Get-ADxGroupMember and Get-ADxPrincipalGroupMembership to complete the full MaxValRange walk before checking for foreign members.
- Fixed SizeLimitExceeded partial page salvaging so it only applies when the caller explicitly sets a SizeLimit, preserving loud errors on server administrative limits.
- Fixed an overflow crash when -ResultSetSize is set to [int]::MaxValue.
- Fixed Get-ADxGroupNested emitting irrelevant primary-group warnings for excluded members.
- Fixed a Ctrl-C cancellation race condition during cmdlet disposal (the cancellation token is cached at construction and StopProcessing tolerates losing the disposal race).

### Changed

- -ResultSetSize now issues a warning when output is truncated, across object, membership, and Search-ADxAccount cmdlets.
- Paging empty-page abandons now route through the warning channel, and Search-ADxObject no longer emits false warnings on single-page sets.

### Added

- Added consumption of ConnectTimeoutSeconds, separating connection timeouts from search timeouts.

## 0.2.9

### Fixed

- Fixed binary-attribute drift where msDS-AllowedToActOnBehalfOfOtherIdentity and msDS-GroupMSAMembership projected null when decoding as UTF-8. The forced-byte array set is now schema-derived.
- Fixed LockedOut property over-reporting by reading the computed ADS_UF_LOCKOUT bit instead of stale lockoutTime values. Filter-side queries retain lockoutTime.
- Fixed Get-ADxPrincipalGroupMembership failing on computer names missing the trailing '$' suffix.

### Changed

- Description attributes project as scalar strings rather than arrays.
- Integer-syntax attributes project as Int32 (while LargeInteger syntax keeps Int64 for uSNCreated/uSNChanged).
- AccountDomainSid returns null for non-account SIDs instead of fabricating domain SIDs for BUILTIN principals.

### Added

- Added extended-DN (<GUID=...>) resolution for GUID identities, reaching configuration and schema partitions.
- Added support for SecurityIdentifier objects as -Identity input on security-principal cmdlets.

## 0.2.8

### Fixed

- Fixed DateTime skew where Kind=Unspecified values marshalled as UTC instead of local time, so date strings and DateTime variables in -Filter produce the same query.

### Changed

- Using '*' wildcards in -eq and -ne filter values now throws a terminating error rather than silently escaping it.
- GeneralizedTime bounds round direction-aware against sub-second values, and exact sub-second equality checks are rejected. Pre-1601 timestamps return clean translation errors.
- -RecursiveMatch is restricted to link-valued DN attributes, blocking degenerate walks on objectCategory and distinguishedName.
- GroupCategory -ne 'Distribution' now emits a single optimized negation filter.

### Added

- Added support for the -approx operator (LDAP '~=').
- Added -RecursiveMatch on all link-valued DN attributes (manager and managedBy chain walks), beyond member and memberOf.
- Added support for underscore attribute names in filters.

## 0.2.7

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

## 0.2.6

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
  - Get-ADxDomainController: Returns DCs via -Identity or -Filter - with OperationMasterRoles, IsGlobalCatalog, IsReadOnly, and site info.
- Data Types: Interval/duration attributes (maxPwdAge, minPwdAge, lockoutDuration, lockOutObservationWindow) stored as negative 100ns ticks now decode to positive TimeSpan objects. Filtering on intervals in -Filter is explicitly rejected.
- Unsupported Limits: Get-ADxDomainController -Discover, IPv4Address, and IPv6Address explicitly throw errors explaining non-LDAP limitations.

### Fixed

- Added missing -Properties argument completer to Get-ADxPrincipalGroupMembership.

## 0.2.5

### Added

- Get-ADxPrincipalGroupMembership: Added reverse group membership cmdlet.
  - Resolves primary groups by SID manipulation (replacing RID in account SID with primaryGroupID). Emits a warning if SID or primaryGroupID is unreadable.
  - Uses member searches instead of memberOf attributes to prevent MaxValRange truncation on accounts with >1,500 groups.
  - Emits Global-Catalog-aware warnings when membership spans into external, unsearched forest partitions.

## 0.2.4

### Changed

- Cross-Domain Member Warnings: Get-ADxGroupMember and Get-ADxGroupNested now check raw member attributes during resolution and issue warnings naming foreign-domain members that cannot be traversed via single-partition memberOf searches.

### Fixed

- Multi-Domain Ambiguity: Binding Global Catalog endpoints (-Port 3268) throws ADxIdentityAmbiguous when search names (e.g., Administrators) exist in multiple domains.

## 0.2.3

### Fixed

- MaxValRange Race Condition: Fixed an issue where LdapEntry could overwrite a fully retrieved >1,500 member list with an empty member attribute depending on per-process hashtable enumeration order.
- Pipeline Binding: Piped objects containing a DistinguishedName property (including native RSAT ADUser/ADGroup and [pscustomobject]) now bind properly to -Identity without throwing "cannot be a PSCustomObject".

### Changed

- Get-ADxGroupMember -Recursive: Expanded recursion to include primaryGroupID relationships across nested groups, capturing effective domain membership (e.g., returning all domain users when evaluating BUILTIN\Users).

## 0.2.2

### Fixed

- Non-Windows Auth Error Messaging: When using -AuthType Negotiate or Kerberos with -Credential on Linux/macOS, errors now explicitly explain platform SASL/GSSAPI limitations and suggest -AuthType Basic -UseSsl or using kinit.

## 0.2.1

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

## 0.2.0

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

## 0.1.0

### Added

- Get-ADxRootDse: Queries RootDSE metadata (naming contexts, functional levels, supported controls) without requiring a domain join.
- Search-ADxObject: Low-level LDAP search wrapper supporting RFC 2696 paging, explicit attribute sets, and -All streaming.
- Cross-platform support for Linux and macOS via native LDAP libraries.

### Fixed

- Fixed an issue where range-suffixed attributes (member;range=0-1499) were assigned to literal property names instead of merging into standard properties.
