#Requires -Version 7.0

<#
    .SYNOPSIS
        Live integration suite against a containerized OpenLDAP server.

    .DESCRIPTION
        Engine-level coverage for the one class the offline suites structurally cannot reach:
        ADxLdapClient. Its S.DS.Protocols response types have internal-only constructors, so
        no fake can exercise the real bind, search, SizeLimit-salvage or timeout paths -- only
        a real directory can. slapd is not AD (no GC, no primaryGroupID, no
        defaultNamingContext), so this gate covers transport-level behavior; AD semantics stay
        in the live-lab suite.

        Self-skips unless ADX_INTEGRATION=1, so running this file offline is inert. CI runs it
        against a bitnami/openldap service seeded by the workflow:
          - 25 inetOrgPerson entries under ou=people (enumeration counts)
          - cn=reader with a password (a NON-rootdn bind: OpenLDAP exempts the rootdn from
            every limit, so the size-limit tests are only meaningful as reader)
          - the server's global olcSizeLimit lowered to 10 (the server-enforced-limit branch)
#>

param()

# Discovery-time gate for the -Skip on each Describe. Pester discovery and run are separate
# executions: run-phase code must re-read the environment itself, never this variable.
$script:Enabled = $env:ADX_INTEGRATION -eq '1'

BeforeAll {
    if ($env:ADX_INTEGRATION -ne '1') { return }

    Import-Module (Join-Path $PSScriptRoot '../module/adx.psd1') -Force

    $script:Server   = if ($env:ADX_IT_SERVER)   { $env:ADX_IT_SERVER }   else { 'localhost:1389' }
    $script:Base     = if ($env:ADX_IT_BASE)     { $env:ADX_IT_BASE }     else { 'dc=example,dc=org' }
    $script:PeopleOu = "ou=people,$($script:Base)"

    function script:MakeCred([string] $dn, [string] $password) {
        [pscredential]::new($dn, (ConvertTo-SecureString $password -AsPlainText -Force))
    }

    # Admin is slapd's rootdn: limit-EXEMPT, used for enumeration. Reader is a plain entry:
    # limits apply, used for every size-limit assertion.
    $script:AdminCred  = MakeCred "cn=admin,$($script:Base)" 'adminpassword'
    $script:ReaderCred = MakeCred "cn=reader,$($script:Base)" 'readerpassword'

    # Basic-without-TLS is deliberate here (throwaway container credentials on loopback);
    # the cleartext warning the module rightly emits is silenced per call.
    $script:Common = @{
        Server        = $script:Server
        AuthType      = 'Basic'
        WarningAction = 'SilentlyContinue'
        ErrorAction   = 'Stop'
    }

    function script:NewOptions([hashtable] $overrides) {
        $options = [ADx.Engine.Ldap.LdapClientOptions]::new()
        # init-only properties: PowerShell's binder sets them fine (init enforcement is
        # C#-compile-time only).
        foreach ($entry in $overrides.GetEnumerator()) {
            $options.($entry.Key) = $entry.Value
        }
        $options
    }

    function script:ConnectEngine([ADx.Engine.Ldap.LdapClientOptions] $options) {
        [ADx.Engine.Ldap.ADxLdapClient]::ConnectAsync(
            $options, $null, $null, [System.Threading.CancellationToken]::None
        ).GetAwaiter().GetResult()
    }

    function script:ReaderOptions {
        NewOptions @{
            Server     = $script:Server
            AuthMode   = 'Basic'
            Credential = $script:ReaderCred.GetNetworkCredential()
        }
    }
}

Describe 'OpenLDAP integration: cmdlet surface' -Skip:(-not $script:Enabled) {

    It 'Binds with the host:port -Server spelling and reads RootDSE' {
        # localhost:1389 is itself the regression test for the embedded-port parsing: before
        # 0.4 the native stack honoured the :1389 while ADx believed it was port 389. The
        # projected Server must be the bare host -- the split, observed end to end.
        $dse = Get-ADxRootDse @script:Common -Credential $script:AdminCred
        $dse | Should -Not -BeNullOrEmpty
        $dse.Server | Should -Be (($script:Server -split ':')[0])
        $dse.IsActiveDirectory | Should -BeFalse
    }

    It 'Enumerates every seeded entry through the RSAT preset path' {
        $people = Get-ADxObject @script:Common -Credential $script:AdminCred `
            -Filter "ObjectClass -eq 'inetOrgPerson'" -SearchBase $script:PeopleOu
        @($people).Count | Should -Be 25
    }

    It 'Pages through the result set' {
        # 25 entries at page size 10 forces three wire pages through LdapPageIterator
        # against a real server (the fake executor can only simulate the cookie protocol).
        $people = Get-ADxObject @script:Common -Credential $script:AdminCred `
            -Filter "ObjectClass -eq 'inetOrgPerson'" -SearchBase $script:PeopleOu -ResultPageSize 10
        @($people).Count | Should -Be 25
    }

    It 'Searches via the raw LDAP path too' {
        $found = Search-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $script:PeopleOu -LdapFilter '(objectClass=inetOrgPerson)' -All
        @($found).Count | Should -Be 25
    }

    It 'Throws a CATCHABLE error for an identity that does not exist' {
        # 0.4 behavior change, matching RSAT: the ported existence-check idiom
        # `try { Get-ADxObject x } catch { }` must take the catch branch on a miss.
        #
        # Deliberately NOT -ErrorAction Stop: Stop promotes even a pre-0.4 non-terminating
        # WriteError into an equally catchable error carrying the same record, which made an
        # earlier version of this test pass against the very behavior it exists to rule out.
        # Under Continue, only a genuinely TERMINATING error takes the catch branch.
        $continueCommon = $script:Common.Clone()
        $continueCommon.ErrorAction = 'Continue'

        $caught = $null
        try {
            Get-ADxObject @continueCommon -Credential $script:AdminCred `
                -Identity "cn=does-not-exist,$($script:PeopleOu)" -SearchBase $script:PeopleOu 2>$null
        }
        catch { $caught = $_ }

        $caught | Should -Not -BeNullOrEmpty
        $caught.FullyQualifiedErrorId | Should -Match 'ADxObjectNotFound'
        $caught.CategoryInfo.Category | Should -Be 'ObjectNotFound'
    }

    It 'Routes a DN identity with -SearchBase through the scoped search, not the base read' {
        # Mechanism pin, deliberately reading "backwards". 0.4 gates the DN fast path: with
        # -SearchBase, resolution runs a (distinguishedName=...) filter INSIDE the base.
        # slapd, unlike AD, has no searchable distinguishedName attribute -- so the scoped
        # path yields not-found here, while the base read at the DN happily returns this
        # entry (proven by the sibling test below). The not-found is therefore PROOF the
        # scoped path ran. The positive case (the filter matching on AD) belongs to the
        # live-lab suite.
        #
        # cn=person01, NOT person001: the fixture seeds person01..person25 (`seq -w 1 25`).
        # An earlier version of this test queried a nonexistent DN, which the old base read
        # would ALSO have missed -- making the pin unable to fail. The identity here MUST be
        # an entry that exists.
        Get-ADxObject @script:Common -Credential $script:AdminCred `
            -Identity "cn=person01,$($script:PeopleOu)" |
            ForEach-Object DistinguishedName |
            Should -Be "cn=person01,$($script:PeopleOu)"  # base read (no -SearchBase) finds it

        $caught = $null
        try {
            Get-ADxObject @script:Common -Credential $script:AdminCred `
                -Identity "cn=person01,$($script:PeopleOu)" -SearchBase $script:PeopleOu
        }
        catch { $caught = $_ }

        $caught.FullyQualifiedErrorId | Should -Match 'ADxObjectNotFound'
    }

    It 'Errors loudly when the SERVER size limit truncates an unlimited search' {
        # As reader (rootdn is exempt): 25 matches against the server's olcSizeLimit of 10.
        # A partial set that looks total is the module's defining failure; it must throw.
        $caught = $null
        try {
            Get-ADxObject @script:Common -Credential $script:ReaderCred `
                -Filter "ObjectClass -eq 'inetOrgPerson'" -SearchBase $script:PeopleOu
        }
        catch { $caught = $_ }

        $caught | Should -Not -BeNullOrEmpty
        $caught.Exception.Message | Should -Match '(?i)size'
    }
}

Describe 'OpenLDAP integration: completeness' -Skip:(-not $script:Enabled) {
    <#
        The data-integrity gate. The fixture (tests/fixtures/New-AdxFixture.ps1) seeded a
        synthetic population whose manifest this block reconciles against as SETS: missing,
        extra, duplicated and value-substituted entries all fail BY NAME. If 12,000 entries
        were seeded and 11,999 come back, the failure says which one is gone -- "looks
        complete" is not a property this suite accepts on faith.

        Everything here binds as admin: slapd's rootdn is exempt from the olcSizeLimit=10
        the size-limit tests configured, and completeness must enumerate everything.
    #>

    BeforeAll {
        if ($env:ADX_INTEGRATION -ne '1') { return }

        Import-Module (Join-Path $PSScriptRoot 'fixtures/AdxReconciliation.psm1') -Force
        $fixtureDir = if ($env:ADX_IT_FIXTURE_DIR) { $env:ADX_IT_FIXTURE_DIR }
                      else { Join-Path $PSScriptRoot 'fixtures/out' }
        $script:Manifest = Get-AdxManifest (Join-Path $fixtureDir 'manifest.json')

        # Raw-path results name attributes verbatim; preset-path results project typed
        # properties. Both carry DistinguishedName, which is what reconciliation keys on.
        function script:GetPopulation([string] $name) { $script:Manifest.populations.$name }
    }

    It 'Enumerates the bulk population EXACTLY via the raw search path' {
        $population = GetPopulation 'bulk'
        $actual = Search-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $population.ou -LdapFilter '(objectClass=inetOrgPerson)' `
            -Property employeeNumber, description -All

        Assert-AdxReconciliation -Population $population -Actual @($actual) -ValuesToo `
            -Surface "Search-ADxObject over $($population.count) seeded entries" | Out-Null
    }

    It 'Enumerates the bulk population EXACTLY via the RSAT preset path (paged at 100)' {
        $population = GetPopulation 'bulk'
        $actual = Get-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $population.ou -Filter "ObjectClass -eq 'inetOrgPerson'" `
            -Properties employeeNumber, description -ResultPageSize 100

        Assert-AdxReconciliation -Population $population -Actual @($actual) -ValuesToo `
            -Surface "Get-ADxObject (page size 100) over $($population.count) seeded entries" | Out-Null
    }

    It 'Loses nothing at the page-size cliff edges (<_>)' -ForEach @(
        'b0999', 'b1000', 'b1001', 'b2000', 'b2001'
    ) {
        # Populations sized exactly at the wire page-size boundaries, enumerated at the
        # default page size 1000: a cookie-walk off-by-one drops or duplicates a row here
        # before it shows anywhere else.
        $population = GetPopulation $_
        $actual = Get-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $population.ou -Filter "ObjectClass -eq 'inetOrgPerson'" `
            -Properties employeeNumber, description

        Assert-AdxReconciliation -Population $population -Actual @($actual) -ValuesToo `
            -Surface "page-boundary population $_ ($($population.count) entries)" | Out-Null
    }

    It 'Round-trips EVERY bulk DN through -Identity resolution' {
        # Enumeration completeness and identity resolution are different code paths; an
        # entry that enumerates but does not resolve (or vice versa) is still an integrity
        # failure. One pipeline, one connection, 12,000 scoped base reads.
        $population = GetPopulation 'bulk'
        $resolved = $population.entries.dn |
            Get-ADxObject @script:Common -Credential $script:AdminCred

        Assert-AdxReconciliation -Population $population -Actual @($resolved) `
            -Surface 'per-identity round-trip of the bulk population' | Out-Null
    }

    It 'Preserves DN-escaped and non-ASCII entries end to end' {
        # Escaped commas/plus/quotes/backslash/hash and UTF-8 names: where marshalling bugs
        # corrupt or lose entries. Value reconciliation catches mojibake, not just absence.
        $population = GetPopulation 'special'
        $actual = Get-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $population.ou -Filter "ObjectClass -eq 'inetOrgPerson'" `
            -Properties employeeNumber, description

        Assert-AdxReconciliation -Population $population -Actual @($actual) -ValuesToo `
            -Surface 'special-character population' | Out-Null
    }

    It 'Keeps every value of multi-valued attributes' {
        $population = GetPopulation 'multivalue'
        $actual = Search-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $population.ou -LdapFilter '(objectClass=inetOrgPerson)' `
            -Property employeeNumber, description -All

        Assert-AdxReconciliation -Population $population -Actual @($actual) -ValuesToo `
            -Surface 'multi-valued attribute population' | Out-Null
    }

    It 'Truncates LOUDLY and EXACTLY under -ResultSetSize' {
        # A cap must return exactly the cap and say so -- a partial set that looks total is
        # the failure this module exists to avoid.
        $population = GetPopulation 'bulk'
        $warnings = @()
        $actual = Get-ADxObject @script:Common -Credential $script:AdminCred `
            -SearchBase $population.ou -Filter "ObjectClass -eq 'inetOrgPerson'" `
            -ResultSetSize 100 -WarningVariable warnings -WarningAction SilentlyContinue

        @($actual).Count | Should -Be 100
        ($warnings -join ' ') | Should -Match 'truncated'
    }
}

Describe 'OpenLDAP integration: engine client' -Skip:(-not $script:Enabled) {

    It 'Salvages the partial page when the CALLER set the SizeLimit' {
        # Requested limit 5 < server limit 10: the server answers sizeLimitExceeded with 5
        # entries attached, and the 0.3.0 salvage gate (spec.SizeLimit > 0) keeps them.
        $client = ConnectEngine (ReaderOptions)
        try {
            $spec = [ADx.Engine.Ldap.LdapSearchSpec]::new(
                $script:PeopleOu, '(objectClass=inetOrgPerson)', [string[]]@('cn'),
                [ADx.Engine.Ldap.LdapScope]::Subtree, 1000, 5)
            $page = $client.SearchPageAsync(
                $spec, $null, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $page.Entries.Count | Should -Be 5
        }
        finally { $client.Dispose() }
    }

    It 'Stays loud when the SERVER enforces its own limit (no caller SizeLimit)' {
        # The other branch of the same gate, reasoned about in ADxLdapClient but never
        # verified anywhere before this: an administrative limit with no caller limit is a
        # truncation the caller did not ask for, so salvage must NOT engage.
        $client = ConnectEngine (ReaderOptions)
        try {
            $spec = [ADx.Engine.Ldap.LdapSearchSpec]::new(
                $script:PeopleOu, '(objectClass=inetOrgPerson)', [string[]]@('cn'),
                [ADx.Engine.Ldap.LdapScope]::Subtree, 1000, 0)

            # PowerShell wraps .NET method throws in MethodInvocationException; unwrap to
            # assert the real type.
            $caught = $null
            try {
                $client.SearchPageAsync(
                    $spec, $null, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
            }
            catch { $caught = $_.Exception.GetBaseException() }

            $caught | Should -BeOfType ([System.DirectoryServices.Protocols.DirectoryOperationException])
        }
        finally { $client.Dispose() }
    }

    It 'Enforces ConnectTimeoutSeconds against a black-holed host' {
        # 203.0.113.1 is TEST-NET-3: never routed, SYN goes unanswered. Without the timeout
        # actually being consumed (a 0.3.0 fix that had zero coverage), this waits for the
        # OS TCP timeout -- minutes.
        $options = NewOptions @{
            Server                = '203.0.113.1'
            AuthMode              = 'Anonymous'
            ConnectTimeoutSeconds = 2
            MaxRetryAttempts      = 0
        }

        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        { ConnectEngine $options } | Should -Throw
        $stopwatch.Stop()

        # 2s configured, generous CI slack -- but far under any OS connect timeout (75s
        # macOS, ~130s Linux), which is what governed before the managed deadline existed.
        $stopwatch.Elapsed.TotalSeconds | Should -BeLessThan 20
    }

    It 'Honours cancellation during a bind against a black-holed host' {
        # 0.4: the bind runs off-thread with a dispose-on-cancel registration. The
        # caller-visible contract: cancel completes the task promptly (as cancelled), never
        # after the full connect timeout.
        $options = NewOptions @{
            Server                = '203.0.113.1'
            AuthMode              = 'Anonymous'
            ConnectTimeoutSeconds = 30
            MaxRetryAttempts      = 0
        }

        $cts = [System.Threading.CancellationTokenSource]::new()
        try {
            $task = [ADx.Engine.Ldap.ADxLdapClient]::ConnectAsync($options, $null, $null, $cts.Token)
            Start-Sleep -Milliseconds 500

            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $cts.Cancel()
            $caught = $null
            try { $task.GetAwaiter().GetResult() }
            catch { $caught = $_.Exception.GetBaseException() }
            $caught | Should -BeOfType ([System.OperationCanceledException])
            $stopwatch.Stop()

            # Well under the 30s connect timeout: proof the cancellation, not the timeout,
            # ended the wait.
            $stopwatch.Elapsed.TotalSeconds | Should -BeLessThan 10
            $task.IsCompleted | Should -BeTrue
        }
        finally { $cts.Dispose() }
    }
}
