@{
    RootModule        = 'adx.psm1'
    ModuleVersion     = '0.4.0'
    GUID              = 'cd162d8a-384b-4915-9013-8d200fd7579e'
    Author            = 'Thomas Maillo Grome'
    CompanyName       = 'ADx'
    Copyright         = '(c) 2026 Thomas Maillo Grome. All rights reserved.'
    Description       = 'Fast, cross-platform Active Directory collection over raw LDAP. Bypasses ADWS for direct LDAP with server-side paging, explicit attribute lists, and streaming output. Runs on Windows, Linux, and macOS.'

    PowerShellVersion = '7.5'
    CompatiblePSEditions = @('Core')

    FormatsToProcess  = @('adx.Format.ps1xml')

    # Pre-load ADx.Engine.dll so it resolves into the same load context as ADx.Cmdlets.dll.
    # Without this, LdapRootDse (a record type returned by ADxLdapClient and surfaced by
    # Get-ADxRootDse) fails to load at JIT time with TypeLoadException.
    RequiredAssemblies = @('ADx.Engine.dll')

    CmdletsToExport   = @(
        'Get-ADxComputer'
        'Get-ADxDefaultDomainPasswordPolicy'
        'Get-ADxDomain'
        'Get-ADxDomainController'
        'Get-ADxFineGrainedPasswordPolicy'
        'Get-ADxForest'
        'Get-ADxGroup'
        'Get-ADxGroupMember'
        'Get-ADxGroupNested'
        'Get-ADxObject'
        'Get-ADxOrganizationalUnit'
        'Get-ADxPrincipalGroupMembership'
        'Get-ADxRootDse'
        'Get-ADxServiceAccount'
        'Get-ADxUser'
        'Search-ADxAccount'
        'Search-ADxObject'
    )

    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('ActiveDirectory', 'AD', 'LDAP', 'DirectoryServices', 'Windows', 'Linux', 'macOS', 'CrossPlatform', 'Identity', 'PowerShell', 'RSAT')
            LicenseUri   = 'https://github.com/gromedev/adx/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/gromedev/adx'
            ReleaseNotes = @'
v0.4.0 - Identity resolution matches RSAT: the 18-agent review closes
- BREAKING: an identity that does not exist now TERMINATES, as it does in RSAT, so
  try/catch existence checks work. -ErrorAction SilentlyContinue no longer swallows the
  miss -- use try/catch instead (examples/12 shows the pattern)
- -SearchBase/-SearchScope constrain every identity form, DNs and GUIDs included: an
  explicit scope routes them through the scoped search rather than the base-read fast
  path, which stays the unconstrained default so it still reaches configuration and
  schema partitions
- Get-ADxDomainController tells the truth about foreign-domain DCs: their own Domain from
  their config partition, RODC status from the nTDSDSARO class, and $null-with-warning
  for roles it cannot read -- never the bound domain's confident values
- Assemblies carry the release version; the loader warns on stale same-session loads
- CI: OpenLDAP integration job (first real coverage of the LDAP client) and a macOS leg

v0.3.4 - The filter resolves per type, and refuses what AD will not evaluate
- -Filter now resolves per-type properties: FGPP filters (Precedence -eq 500,
  MinPasswordLength -ge 14) and OU StreetAddress match instead of silently returning
  nothing or throwing "not recognised"
- Filters on constructed attributes (tokenGroups, msDS-User-Account-Control-Computed,
  primaryGroupToken) are refused loudly with a redirect, instead of emitting a filter the
  DC never evaluates and reporting the empty result as an answer
- $env: and other provider-drive variables resolve in -Filter

v0.3.3 - Projection fidelity: binary attributes and DC-computed reads
- tokenGroups projects as SIDs on every identity and search path -- a follow-up base read
  fills in what the DC computes only for base reads, so a search-resolved identity no
  longer returns a confidently empty group list
- No more UTF-8 mojibake: thumbnailPhoto/jpegPhoto/logonHours project as bytes,
  schemaIDGUID/attributeSecurityGUID as GUIDs
- msDS-User-Account-Control-Computed is Int32, not String

v0.3.2 - The connect budget is real on every platform
- ConnectTimeoutSeconds is enforced on Linux/macOS through a managed deadline (was:
  whatever the OS connect timeout happened to be, 75-130s). A timed-out connect is no
  longer retried -- the budget is already spent and retrying only stacked delay -- and the
  bind runs on a dedicated thread, so an abandoned bind against a dead or tar-pitted host
  cannot consume thread-pool workers
- Ctrl-C interrupts an in-flight bind: the synchronous connect and bind no longer block
  the pipeline thread
- A connection leak when setup throws after the native connection already exists

v0.3.1 - Server addressing: the port is parsed once and honoured everywhere
- -Server host:3268 no longer sneaks past the Global Catalog safeguards: the embedded port
  drives the effective port, the TLS scheme, and the GC result-shape protections. A
  conflicting explicit -Port terminates, and IPv6 literals parse
- Ports 636/3269 imply LDAPS unless -UseSsl is explicitly bound, however the port arrives
  (in 0.3.0 this attempted a plaintext bind against the LDAPS port and failed)
- A USERDNSDOMAIN carrying ":port" is rejected with guidance rather than silently choosing
  the port on the discovery path

v0.3.0 - Multi-domain truth and loud limits: the four-reviewer audit closes
- Global Catalog binds no longer count other domains' primary-group members: a GC cannot
  resolve foreign RIDs, so the RID arms are dropped and a warning says what was excluded
- Foreign-member warnings now cover groups past MaxValRange: the full range walk completes
  before the check, so >1,500-member groups warn as accurately as small ones
- Loud where it was silent: -ResultSetSize warns on truncation (and no longer overflows at
  [int]::MaxValue); the paging empty-page guard announces abandonment; SizeLimit-truncated
  pages are salvaged only when the caller set the limit -- server administrative limits keep
  terminating; Get-ADxGroupNested no longer warns about members it was asked to exclude
- ConnectTimeoutSeconds is honoured; Ctrl-C during cancellation no longer races a disposed
  token source; examples now cover all 17 cmdlets

v0.2.9 - Projection fidelity and identity resolution: audit fixes, second tranche
- Silent-wrong-answer fixes: RBCD/gMSA security descriptors no longer null when their bytes
  decode as UTF-8 (the forced-byte set is schema-derived now); LockedOut reads the DC-computed
  lockout bit so expired lockouts read False; Get-ADxPrincipalGroupMembership resolves
  computer names (WS01) like RSAT
- Fidelity: Description is a scalar string; Integer-syntax attributes are Int32 (uSN* stay
  Int64); AccountDomainSid is null for non-account SIDs
- New: GUID identities resolved across partitions (<GUID=...> reads), RSAT's
  SecurityIdentifier accepted as -Identity

v0.2.8 - Filter translation: new operators, honest errors; audit fixes, first tranche
- '*' in an -eq/-ne value is now a terminating error: RSAT reads it as a wildcard, PowerShell
  as a literal, and silently picking either can invert a result set ("mail -ne '*'"). The
  error offers -like/-notlike, $null tests, and -LDAPFilter spellings
- DateTime variables and date strings in -Filter now agree (Kind=Unspecified is local,
  matching RSAT); sub-second time bounds round direction-aware; pre-1601 timestamps error
  cleanly
- New: -approx operator, -RecursiveMatch on any link-valued DN attribute (manager chains) --
  and only those, so degenerate walks on objectCategory are rejected; underscore attribute
  names in filters

v0.2.7 - Three more read cmdlets: service accounts, fine-grained policies, account search
- Get-ADxServiceAccount (Get-ADServiceAccount): standalone and group-managed service accounts,
  matched by their shared base class; the gMSA password-retrieval ACL is declared unsupported
- Get-ADxFineGrainedPasswordPolicy (Get-ADFineGrainedPasswordPolicy): PSO objects, ages as
  TimeSpans, AppliesTo as a DN list; identity by policy name, DN, or GUID; searches default to
  the Password Settings Container
- Search-ADxAccount (Search-ADAccount): switch-driven account finder --
  -AccountDisabled/-AccountExpired/-AccountExpiring/-AccountInactive/-LockedOut/-PasswordExpired/
  -PasswordNeverExpires, scoped by -UsersOnly/-ComputersOnly. -PasswordExpired is filtered
  client-side because its bit lives in a constructed attribute AD cannot search on

v0.2.6 - Tier-1 completion: five RSAT-compatible read cmdlets
- Get-ADxOrganizationalUnit (Get-ADOrganizationalUnit): DN/GUID identity, LinkedGroupPolicyObjects
  parsed from gPLink, and StreetAddress read from the OU 'street' attribute (a per-type mapping,
  so a user's StreetAddress is unaffected)
- Get-ADxDefaultDomainPasswordPolicy (Get-ADDefaultDomainPasswordPolicy): the domain head's
  password/lockout policy; age and duration values surface as positive TimeSpans
- Get-ADxDomain / Get-ADxForest (Get-ADDomain / Get-ADForest): identity, FSMO role holders
  resolved to hostnames, well-known containers, domains/child domains, sites, global catalogs.
  Honest subset -- properties ADx cannot produce faithfully are omitted and documented, never
  returned as null
- Get-ADxDomainController (Get-ADDomainController): the connected DC, one by -Identity, or the
  domain's DCs by -Filter *; OperationMasterRoles, IsGlobalCatalog, IsReadOnly, site. -Discover
  and IPv4/IPv6Address are declared unsupported (netlogon and client-side DNS, not LDAP)
- New Interval attribute syntax so duration attributes decode correctly (a wrong syntax would
  have silently nulled them); filtering on interval attributes is rejected, not silently wrong

v0.2.5 - Ninth cmdlet: Get-ADxPrincipalGroupMembership
- The reverse of Get-ADxGroupMember and a drop-in for RSAT Get-ADPrincipalGroupMembership:
  the groups a user, computer, group, or service account belongs to. Reconciles the PRIMARY
  group (Domain Users, and a computer's Domain Controllers) via primaryGroupID, which neither
  memberOf nor member records; enumerates by member search, so it never truncates at MaxValRange
- Cross-domain memberships are named in a Global-Catalog-aware warning rather than dropped.
  Verified live vs RSAT on three lab domains, from a native domain-joined Windows 11 client
  under integrated auth, and cross-platform from macOS over LDAPS

v0.2.4 - Cross-domain group members surfaced, not silently dropped
- Get-ADxGroupMember / Get-ADxGroupNested now warn when a group has members in another domain
  of the forest -- stored only as a forward member link with no memberOf back-link anywhere, so
  no single-partition search (nor a Global Catalog) can reach them. Returned objects are
  unchanged; the warning names the foreign members and the Get-ADxGroup -Properties Members
  workaround. Single-domain forests never warn

v0.2.3 - Validated on a live DC at 500k scale; two live-only defects fixed
- Groups over 1500 members (MaxValRange) could return an EMPTY member list in roughly half
  of all processes: AD sends both member;range=0-1499 and an empty plain member, and .NET
  hashtable order (randomised per process) decided which won the range-completion merge. The
  empty sibling is now dropped at the model boundary and the merge is order-independent
- Piping an object into -Identity (Get-ADxGroupNested X | Get-ADxGroupMember, or a piped RSAT
  ADGroup) threw "cannot be a PSCustomObject": whole-object pipeline binding beat the
  DistinguishedName alias. Any piped object with a DistinguishedName now resolves as that DN
- Streaming proven flat (~100-156 MB client memory) from 0 to 506,732 objects; RSAT could not
  complete the same -Properties * enumeration on an 8 GB DC. Native-Windows parity confirmed:
  identical default property sets and values vs RSAT, integrated auth on all cmdlets, and a
  clean 7.5-floor rejection under Windows PowerShell 5.1

v0.2.1 - Correctness fixes from the first full code review
- Get-ADxGroupMember -Recursive now includes members whose PRIMARY group is a group nested
  inside the target. Rule 1941 cannot traverse primary membership, so BUILTIN\Users (which
  contains Domain Users by default) previously returned almost nobody
- GroupScope reported Unknown for builtin groups (groupType 0x80000005 sets two scope bits);
  they now report DomainLocal, matching RSAT and the filter that selects them
- Multi-valued SID attributes (SIDHistory) returned only their first value
- An -Identity matching several objects is now an error instead of an arbitrary pick
- -Identity <DN> no longer rejects derived classes (inetOrgPerson) that -Filter returns
- Range retrieval warns instead of silently returning a truncated value set
- -ieq/-ilike and friends work unparenthesized, as they already did parenthesized
- New warning when credentials cross the wire unprotected (-AuthType Basic without -UseSsl;
  unsigned connections on Linux/macOS)
- KNOWN: in a multi-domain forest, group membership omits members from other domains

v0.2.0 - The RSAT-compatible read cmdlets
- Get-ADxUser, Get-ADxGroup, Get-ADxComputer, Get-ADxObject: drop-in replacements for their
  RSAT counterparts. RSAT -Filter syntax (translated to LDAP server-side), -LDAPFilter,
  -Identity (DN/GUID/SID/sAMAccountName), -Properties with RSAT and LDAP names, RSAT output
  naming and types (local dates, single-string ObjectClass, SID with .Value)
- Get-ADxGroupMember: direct or -Recursive membership, immune to MaxValRange and including
  primary-group members (primaryGroupID) that a plain member read misses
- Get-ADxGroupNested: every group nested at any depth inside the target, flattened
- Range retrieval completes multi-valued attributes past MaxValRange before projection
- A filter that cannot be translated faithfully is a terminating error, never a silent
  approximation: misspelled properties, -ceq/-match/-in/-contains, undefined variables and
  computed expressions are rejected with explanations
- Presets return unlimited results by default, matching RSAT's -ResultSetSize

v0.1.0 - Initial release
- Get-ADxRootDse: probe a domain controller's RootDSE for naming contexts, functional level,
  and supported controls (paged results, DirSync). Works without a domain join
- Search-ADxObject: generic LDAP search with RFC 2696 server-side paging, explicit attribute
  lists, -All streaming, and range retrieval for attributes over MaxValRange
- Cross-platform: runs on Linux and macOS over plain LDAP, where the Windows-only
  ActiveDirectory module cannot run at all
'@
        }
    }
}
