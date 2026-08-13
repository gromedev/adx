#Requires -Modules Pester

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '../module/adx.psd1'
    Import-Module $ModulePath -Force
}

AfterAll {
    Remove-Module ADx -Force -ErrorAction SilentlyContinue
}

Describe 'Module Loading' {

    It 'Should import without error' {
        Get-Module ADx | Should -Not -BeNullOrEmpty
    }

    It 'Module version should match the manifest' {
        $manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot '../module/adx.psd1')
        (Get-Module ADx).Version | Should -Be $manifest.ModuleVersion
    }

    It 'Should export exactly 17 cmdlets' {
        # Filtered to -CommandType Cmdlet on purpose: Get-Command -Module also counts
        # exported functions, so an unfiltered count would drift for reasons that have
        # nothing to do with the compiled surface.
        $commands = (Get-Command -Module ADx -CommandType Cmdlet).Name | Sort-Object
        $commands | Should -Contain 'Get-ADxComputer'
        $commands | Should -Contain 'Get-ADxDefaultDomainPasswordPolicy'
        $commands | Should -Contain 'Get-ADxDomain'
        $commands | Should -Contain 'Get-ADxDomainController'
        $commands | Should -Contain 'Get-ADxFineGrainedPasswordPolicy'
        $commands | Should -Contain 'Get-ADxForest'
        $commands | Should -Contain 'Get-ADxGroup'
        $commands | Should -Contain 'Get-ADxGroupMember'
        $commands | Should -Contain 'Get-ADxGroupNested'
        $commands | Should -Contain 'Get-ADxObject'
        $commands | Should -Contain 'Get-ADxOrganizationalUnit'
        $commands | Should -Contain 'Get-ADxPrincipalGroupMembership'
        $commands | Should -Contain 'Get-ADxRootDse'
        $commands | Should -Contain 'Get-ADxServiceAccount'
        $commands | Should -Contain 'Get-ADxUser'
        $commands | Should -Contain 'Search-ADxAccount'
        $commands | Should -Contain 'Search-ADxObject'
        $commands | Should -HaveCount 17
    }

    It 'Should carry no Graph dependency' {
        # The whole point of splitting ADx out of Mgx: an AD collector must work with no
        # cloud connection, no Connect-MgGraph, and none of the Graph HTTP stack.
        $types = [ADx.Cmdlets.Base.ADxCmdletBase].Assembly.GetTypes().FullName
        $types | Where-Object { $_ -match 'Mgx|Graph|Polly' } | Should -BeNullOrEmpty
    }

    It 'Search-ADxObject must not expose -Filter' {
        # In the Graph world -Filter is OData; in RSAT's ActiveDirectory module it is
        # PowerShell expression syntax. Accepting either here and sending it as a raw LDAP
        # filter would silently return the wrong set instead of failing. The parameter is
        # deliberately -LdapFilter with no alias.
        $params = (Get-Command Search-ADxObject).Parameters
        $params.ContainsKey('Filter') | Should -BeFalse
        $params.ContainsKey('LdapFilter') | Should -BeTrue
    }

    It 'Search-ADxObject -PageSize allows 1000 (AD MaxPageSize, not the Graph 999)' {
        $range = (Get-Command Search-ADxObject).Parameters['PageSize'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
        $range.MaxRange | Should -Be 1000
    }
}

Describe 'Preset parameter surface' {
    # These are the drop-in guarantees from the plan's guard table, asserted for every
    # preset (they all inherit ADxObjectCmdletBase, so one regression breaks all four the
    # same way). Cheap reflection checks, but each pins a behaviour whose regression would
    # silently change which objects a caller gets back.

    BeforeDiscovery {
        $script:PresetNames = 'Get-ADxUser', 'Get-ADxGroup', 'Get-ADxComputer', 'Get-ADxObject',
            'Get-ADxOrganizationalUnit', 'Get-ADxServiceAccount', 'Get-ADxFineGrainedPasswordPolicy'
    }

    It '<_> defaults to the Filter set with Identity as the only positional' -ForEach $PresetNames {
        $cmd = Get-Command $_
        $cmd.DefaultParameterSet | Should -Be 'Filter'
        ($cmd.ParameterSets | Where-Object Name -eq 'Filter').Parameters |
            Where-Object { $_.Position -ge 0 } | Should -BeNullOrEmpty
        (($cmd.ParameterSets | Where-Object Name -eq 'Identity').Parameters |
            Where-Object { $_.Position -eq 0 }).Name | Should -Be 'Identity'
    }

    BeforeAll {
        $script:cmd = Get-Command Get-ADxUser
    }

    It 'Defaults to the Filter parameter set' {
        $cmd.DefaultParameterSet | Should -Be 'Filter'
    }

    It 'Identity is positional; Filter and LDAPFilter are named-only' {
        # If all three were positional, "Get-ADxUser jdoe" would bind jdoe into -Filter
        # and return the wrong thing instead of resolving an identity.
        $identity = $cmd.Parameters['Identity'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $identity.Position | Should -Be 0

        foreach ($name in 'Filter', 'LDAPFilter') {
            $attr = $cmd.Parameters[$name].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
            $attr.Position | Should -Be ([int]::MinValue) -Because "-$name must be named-only"
        }
    }

    It 'Get-ADxUser jdoe binds Identity, not Filter' {
        $binding = $cmd.ResolveParameter('Identity')
        $binding | Should -Not -BeNullOrEmpty
        # Static proof via the parameter sets: only the Identity set has a positional slot.
        ($cmd.ParameterSets | Where-Object Name -eq 'Filter').Parameters |
            Where-Object { $_.Position -ge 0 } | Should -BeNullOrEmpty
        (($cmd.ParameterSets | Where-Object Name -eq 'Identity').Parameters |
            Where-Object { $_.Position -eq 0 }).Name | Should -Be 'Identity'
    }

    It 'Identity accepts pipeline input by value and by DistinguishedName' {
        $attr = $cmd.Parameters['Identity'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $attr.ValueFromPipeline | Should -BeTrue
        $attr.ValueFromPipelineByPropertyName | Should -BeTrue
        $cmd.Parameters['Identity'].Aliases | Should -Contain 'DistinguishedName'
    }

    It 'ResultSetSize defaults to unlimited, unlike Search-ADxObject' {
        # RSAT's -ResultSetSize default is unlimited; a preset that silently stopped at one
        # page would be a drop-in lie. Search-ADxObject deliberately keeps its one-page
        # default with a warning instead.
        ([ADx.Cmdlets.Cmdlets.Directory.GetADxUser]::new()).ResultSetSize | Should -Be 0
        ([ADx.Cmdlets.Cmdlets.Directory.GetADxGroup]::new()).ResultSetSize | Should -Be 0
        ([ADx.Cmdlets.Cmdlets.Directory.GetADxComputer]::new()).ResultSetSize | Should -Be 0
        ([ADx.Cmdlets.Cmdlets.Directory.GetADxObject]::new()).ResultSetSize | Should -Be 0
    }

    It 'Filter takes a ScriptBlock via coercion' {
        # -Filter { Name -eq 'x' } must behave like the string form: ScriptBlock -> string
        # coercion yields the body without braces.
        $cmd.Parameters['Filter'].ParameterType | Should -Be ([string])
    }

    It 'Exposes the RSAT parameter names' {
        foreach ($name in 'Properties', 'SearchBase', 'SearchScope', 'ResultSetSize', 'ResultPageSize') {
            $cmd.Parameters.ContainsKey($name) | Should -BeTrue -Because "-$name is part of the RSAT surface"
        }
        $cmd.Parameters['Properties'].Aliases | Should -Contain 'Property'
    }

    It 'Has the -AllowUnknownProperty escape hatch' {
        $cmd.Parameters.ContainsKey('AllowUnknownProperty') | Should -BeTrue
    }
}

Describe 'Membership cmdlet surface' {

    It '<_> takes a positional pipeline Identity but no -Filter/-LDAPFilter' -ForEach @(
        'Get-ADxGroupMember', 'Get-ADxGroupNested'
    ) {
        $cmd = Get-Command $_
        $identity = $cmd.Parameters['Identity'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $identity.Position | Should -Be 0
        $identity.ValueFromPipeline | Should -BeTrue
        # RSAT's Get-ADGroupMember has no -Filter; inheriting the preset base would have
        # leaked it, so these use a separate base that does not.
        $cmd.Parameters.ContainsKey('Filter') | Should -BeFalse
        $cmd.Parameters.ContainsKey('LDAPFilter') | Should -BeFalse
        $cmd.Parameters['Identity'].Aliases | Should -Contain 'Group'
    }

    It 'Get-ADxGroupMember has -Recursive; Get-ADxGroupNested does not' {
        (Get-Command Get-ADxGroupMember).Parameters.ContainsKey('Recursive') | Should -BeTrue
        (Get-Command Get-ADxGroupNested).Parameters.ContainsKey('Recursive') | Should -BeFalse
    }

    It 'Get-ADxPrincipalGroupMembership mirrors the membership surface without the Group alias' {
        $cmd = Get-Command Get-ADxPrincipalGroupMembership
        $identity = $cmd.Parameters['Identity'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $identity.Position | Should -Be 0
        $identity.ValueFromPipeline | Should -BeTrue
        $cmd.Parameters.ContainsKey('Filter') | Should -BeFalse
        $cmd.Parameters.ContainsKey('LDAPFilter') | Should -BeFalse
        # RSAT's Get-ADPrincipalGroupMembership -Identity is a principal, not a group: it must
        # carry the DistinguishedName alias but NOT the group cmdlets' 'Group' alias.
        $cmd.Parameters['Identity'].Aliases | Should -Contain 'DistinguishedName'
        $cmd.Parameters['Identity'].Aliases | Should -Not -Contain 'Group'
        # It is a direct-membership query like RSAT's: no -Recursive.
        $cmd.Parameters.ContainsKey('Recursive') | Should -BeFalse
    }
}

Describe 'Assembly binding' {

    It 'ADx.Engine must not demand a newer S.DS.Protocols than PowerShell ships' {
        # A compile-time PackageReference becomes a MINIMUM-version demand at runtime.
        # System.DirectoryServices.Protocols is deliberately not shipped in module/ (its
        # portable lib/ asset is a PlatformNotSupported stub) -- it is supplied by the
        # PowerShell host. Referencing package 9.0.14 emitted a dependency on assembly
        # 9.0.0.14 while pwsh 7.5 ships 9.0.0.10, so every ADx cmdlet died with
        # FileNotFoundException before doing any work, with a fully green build.
        # Pin the reference to the 9.0.0 baseline.
        $engine = [Reflection.Assembly]::LoadFrom(
            (Join-Path $PSScriptRoot '../module/ADx.Engine.dll'))
        $referenced = $engine.GetReferencedAssemblies() |
            Where-Object { $_.Name -eq 'System.DirectoryServices.Protocols' }
        $referenced | Should -Not -BeNullOrEmpty

        $hostPath = Join-Path ([AppDomain]::CurrentDomain.BaseDirectory) 'System.DirectoryServices.Protocols.dll'
        $hostVersion = [Reflection.AssemblyName]::GetAssemblyName($hostPath).Version

        $referenced.Version | Should -BeLessOrEqual $hostVersion
    }
}

Describe 'Manifest and release hygiene' {

    BeforeAll {
        $script:Manifest = Test-ModuleManifest -Path (Join-Path $PSScriptRoot '../module/adx.psd1') -ErrorAction Stop
    }

    It 'Is a valid module manifest with a parsable version' {
        $script:Manifest.Name | Should -Be 'adx'
        $script:Manifest.Version | Should -BeOfType [System.Version]
    }

    It 'Targets PowerShell 7.5 Core only' {
        # Built for net9.0; must never claim it can load in Windows PowerShell 5.1
        $script:Manifest.PowerShellVersion | Should -BeGreaterOrEqual ([Version]'7.5')
        $script:Manifest.CompatiblePSEditions | Should -Contain 'Core'
        $script:Manifest.CompatiblePSEditions | Should -Not -Contain 'Desktop'
    }

    It 'Pre-loads ADx.Engine.dll via RequiredAssemblies' {
        # Without this, LdapRootDse resolves into a different load context and
        # Get-ADxRootDse dies at JIT time with TypeLoadException
        $script:Manifest.RequiredAssemblies | Should -Contain 'ADx.Engine.dll'
    }

    It 'Exports cmdlets and no functions' {
        $script:Manifest.ExportedCmdlets.Count | Should -BeGreaterThan 0
        $script:Manifest.ExportedFunctions.Count | Should -Be 0
    }

    It 'CHANGELOG documents the manifest version' {
        # Catches a version bump that forgot its changelog entry
        $changelog = Get-Content -Path (Join-Path $PSScriptRoot '../CHANGELOG.md') -Raw
        $changelog | Should -Match ('##\s+{0}' -f [Regex]::Escape($script:Manifest.Version.ToString()))
    }

    It 'Release notes lead with the manifest version' {
        $notes = $script:Manifest.PrivateData.PSData.ReleaseNotes
        $notes | Should -Not -BeNullOrEmpty
        $notes | Should -Match ('v{0}' -f [Regex]::Escape($script:Manifest.Version.ToString()))
    }

    It 'Names the module files exactly, byte for byte' {
        # On a case-sensitive filesystem PowerShell resolves <name>.psd1 against the module
        # directory byte for byte, so casing drift breaks Import-Module on Linux -- one of
        # the platforms this module exists for. Get-ChildItem reports on-disk casing even
        # where the filesystem matches case-insensitively.
        $moduleRoot = Join-Path $PSScriptRoot '../module'
        foreach ($file in 'adx.psd1', 'adx.psm1', 'adx.Format.ps1xml') {
            (Get-ChildItem -Path $moduleRoot -Filter $file).Name | Should -BeExactly $file
        }
        $script:Manifest.RootModule | Should -BeExactly 'adx.psm1'
    }
}

Describe 'Packaging' {

    It 'Must never stage a directory-services assembly' {
        # The package is RID-split and its portable asset is a PlatformNotSupportedException
        # stub. Staging it would shadow the working implementation PowerShell already
        # carries in its app base and break every Windows host.
        $moduleRoot = Join-Path $PSScriptRoot '../module'
        foreach ($name in 'System.DirectoryServices.Protocols.dll',
                          'System.DirectoryServices.dll',
                          'System.DirectoryServices.AccountManagement.dll') {
            Test-Path (Join-Path $moduleRoot $name) | Should -BeFalse -Because "$name must come from the PowerShell host, not the module"
        }
    }

    It 'Needs no Dependencies folder' {
        # ADx has no third-party runtime dependencies, which is why it needs no ALC
        # brokering and no AlcInitializer. If this ever appears, that assumption broke.
        Test-Path (Join-Path $PSScriptRoot '../module/Dependencies') | Should -BeFalse
    }

    It 'Should unload cleanly' {
        Import-Module (Join-Path $PSScriptRoot '../module/adx.psd1') -Force
        { Remove-Module ADx -ErrorAction Stop } | Should -Not -Throw
        Get-Module ADx | Should -BeNullOrEmpty
        Import-Module (Join-Path $PSScriptRoot '../module/adx.psd1') -Force
    }

    It 'Should import, unload, and re-import in a fresh session' {
        # The in-process check above runs with Pester's own module bookkeeping in play.
        # A child pwsh proves the contract from a cold start, the way a user's script
        # sees it -- including any lazy assembly load that only a first-touch hits.
        $modulePath = Join-Path $PSScriptRoot '../module/adx.psd1'
        $probe = @"
`$ErrorActionPreference = 'Stop'
try {
    Import-Module '$modulePath' -Force
    Remove-Module adx
    if (Get-Module adx) { throw 'still loaded after Remove-Module' }
    Import-Module '$modulePath' -Force
    if (-not (Get-Module adx)) { throw 're-import produced no module' }
} catch { Write-Output ('THREW: ' + `$_.Exception.Message.Split([char]10)[0]); exit 1 }
Write-Output 'OK'
"@
        $result = pwsh -NoProfile -Command $probe
        $result | Should -Contain 'OK'
        $LASTEXITCODE | Should -Be 0
    }
}

Describe 'Formatting' {

    It 'Format.ps1xml defines views for every emitted PSTypeName' {
        $xml = [xml](Get-Content (Join-Path $PSScriptRoot '../module/adx.Format.ps1xml') -Raw)
        $names = $xml.Configuration.ViewDefinitions.View.Name
        $names | Should -Contain 'ADx.Entry'
        $names | Should -Contain 'ADx.RootDse'
        foreach ($type in 'ADx.User', 'ADx.Group', 'ADx.Computer', 'ADx.Object', 'ADx.OrganizationalUnit',
            'ADx.ServiceAccount') {
            $names | Should -Contain $type
        }
    }

    It 'Preset views are lists, matching how RSAT renders these objects' {
        $xml = [xml](Get-Content (Join-Path $PSScriptRoot '../module/adx.Format.ps1xml') -Raw)
        foreach ($type in 'ADx.User', 'ADx.Group', 'ADx.Computer', 'ADx.Object', 'ADx.OrganizationalUnit',
            'ADx.ServiceAccount') {
            $view = $xml.Configuration.ViewDefinitions.View | Where-Object Name -eq $type
            $view.ListControl | Should -Not -BeNullOrEmpty -Because "$type should render as a list"
        }
    }

    It 'Entry type name matches what the projector stamps' {
        # If these drift the table view silently stops applying, and output falls back to
        # an unformatted property dump.
        [ADx.Cmdlets.Base.ADxCmdletBase]::EntryTypeName | Should -Be 'ADx.Entry'
    }

    It 'Topology views exist and are lists' {
        $xml = [xml](Get-Content (Join-Path $PSScriptRoot '../module/adx.Format.ps1xml') -Raw)
        foreach ($type in 'ADx.DefaultDomainPasswordPolicy', 'ADx.Domain', 'ADx.Forest', 'ADx.DomainController',
            'ADx.FineGrainedPasswordPolicy', 'ADx.Account') {
            $view = $xml.Configuration.ViewDefinitions.View | Where-Object Name -eq $type
            $view | Should -Not -BeNullOrEmpty -Because "$type needs a view"
            $view.ListControl | Should -Not -BeNullOrEmpty -Because "$type should render as a list"
        }
    }
}

Describe 'Topology cmdlet surface' {
    # These cmdlets read fixed well-known objects, so their surface is deliberately
    # narrow: no -Filter/-Identity/-Properties. If one of those parameters appears, the
    # cmdlet has drifted from its design (RSAT's domain targeting is the netlogon DC
    # locator, which is not LDAP; -Server picks the domain here).

    It '<_> takes no Identity/Filter/Properties but inherits the connection surface' -ForEach @(
        'Get-ADxDefaultDomainPasswordPolicy', 'Get-ADxDomain', 'Get-ADxForest'
    ) {
        $params = (Get-Command $_).Parameters
        $params.ContainsKey('Identity') | Should -BeFalse
        $params.ContainsKey('Filter') | Should -BeFalse
        $params.ContainsKey('Properties') | Should -BeFalse
        $params.ContainsKey('Server') | Should -BeTrue
        $params.ContainsKey('Credential') | Should -BeTrue
    }

    It 'Search-ADxAccount is switch-driven with no Filter/Identity/Properties' {
        $cmd = Get-Command Search-ADxAccount
        $p = $cmd.Parameters
        # The criterion IS the filter; RSAT has no -Filter/-Identity/-Properties here.
        $p.ContainsKey('Filter') | Should -BeFalse
        $p.ContainsKey('LDAPFilter') | Should -BeFalse
        $p.ContainsKey('Identity') | Should -BeFalse
        $p.ContainsKey('Properties') | Should -BeFalse
        # The seven mutually-exclusive criterion switches, each its own parameter set.
        foreach ($sw in 'AccountDisabled','AccountExpired','AccountExpiring','AccountInactive',
            'LockedOut','PasswordExpired','PasswordNeverExpires') {
            $p.ContainsKey($sw) | Should -BeTrue -Because "criterion -$sw must exist"
        }
        ($cmd.ParameterSets.Name | Sort-Object) | Should -Be (@(
            'AccountDisabled','AccountExpired','AccountExpiring','AccountInactive',
            'LockedOut','PasswordExpired','PasswordNeverExpires') | Sort-Object)
        # -DateTime / -TimeSpan exist only for the two windowed criteria.
        $p.ContainsKey('DateTime') | Should -BeTrue
        $p.ContainsKey('TimeSpan') | Should -BeTrue
    }

    It 'Get-ADxDomainController has Identity (positional 0), Filter, and Discover' {
        $cmd = Get-Command Get-ADxDomainController
        $cmd.DefaultParameterSet | Should -Be 'Identity'
        $cmd.Parameters.ContainsKey('Identity') | Should -BeTrue
        $cmd.Parameters.ContainsKey('Filter') | Should -BeTrue
        $cmd.Parameters.ContainsKey('Discover') | Should -BeTrue
        (($cmd.ParameterSets | Where-Object Name -eq 'Identity').Parameters |
            Where-Object { $_.Position -eq 0 }).Name | Should -Be 'Identity'
        # Filter is mandatory in its own set (so a bare noun does not silently enumerate).
        (($cmd.ParameterSets | Where-Object Name -eq 'Filter').Parameters |
            Where-Object Name -eq 'Filter').IsMandatory | Should -BeTrue
    }
}

Describe 'Help' {

    It '<_> has real MAML help, not autogenerated syntax' -ForEach @(
        'Get-ADxRootDse', 'Get-ADxUser', 'Get-ADxGroup', 'Get-ADxComputer', 'Get-ADxObject',
        'Get-ADxGroupMember', 'Get-ADxGroupNested', 'Get-ADxPrincipalGroupMembership', 'Search-ADxObject',
        'Get-ADxOrganizationalUnit', 'Get-ADxDefaultDomainPasswordPolicy', 'Get-ADxDomain',
        'Get-ADxForest', 'Get-ADxDomainController',
        'Get-ADxServiceAccount', 'Get-ADxFineGrainedPasswordPolicy', 'Search-ADxAccount'
    ) {
        $help = Get-Help $_
        # Autogenerated help has an empty description; MAML-backed help carries the one
        # written in module/help/*.md.
        $help.Synopsis | Should -Not -BeNullOrEmpty
        $help.Synopsis | Should -Not -Match '^\s*$'
        $help.description | Should -Not -BeNullOrEmpty
    }

    It 'Preset help documents at least one example each' -ForEach @(
        'Get-ADxUser', 'Get-ADxGroup', 'Get-ADxComputer', 'Get-ADxObject',
        'Get-ADxGroupMember', 'Get-ADxGroupNested', 'Get-ADxPrincipalGroupMembership',
        'Get-ADxOrganizationalUnit', 'Get-ADxDefaultDomainPasswordPolicy', 'Get-ADxDomain',
        'Get-ADxForest', 'Get-ADxDomainController',
        'Get-ADxServiceAccount', 'Get-ADxFineGrainedPasswordPolicy', 'Search-ADxAccount'
    ) {
        (Get-Help $_).examples.example | Should -Not -BeNullOrEmpty
    }
}

Describe 'Live suite hygiene' -Skip:(-not (Test-Path (Join-Path $PSScriptRoot '../../-PRIVATE/adx-lab'))) {

    # The live and stress suites CANNOT run until a domain controller exists, so a defect in
    # either stays latent until the one session where it is needed and then wastes that
    # session. These are the checks that are possible without a DC. They exist because a real
    # bug shipped into the live suite: a newline after `-Because`, which PowerShell parses
    # happily as a switch followed by an orphaned string, silently discarding the failure
    # message on the single most important assertion in the file.
    #
    # Those files live in -PRIVATE/adx-lab/ rather than here: they are lab tooling for a
    # disposable domain, not part of the module's own test suite, and -PRIVATE is never
    # exported. This block skips entirely when that directory is absent, so a tree without it
    # (an export, a fresh clone) still runs a clean offline gate.

    BeforeDiscovery {
        $script:LabRoot       = Join-Path $PSScriptRoot '../../-PRIVATE/adx-lab'
        $script:LiveSuitePath = Join-Path $script:LabRoot 'ADx.Live.Tests.ps1'
        $script:SeedPath      = Join-Path $script:LabRoot 'Seed-ADxTestDomain.ps1'
    }

    BeforeAll {
        $script:LabRoot       = Join-Path $PSScriptRoot '../../-PRIVATE/adx-lab'
        $script:LiveSuitePath = Join-Path $script:LabRoot 'ADx.Live.Tests.ps1'
        $script:SeedPath      = Join-Path $script:LabRoot 'Seed-ADxTestDomain.ps1'

        function Get-ScriptAst {
            param([string]$Path)
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                $Path, [ref]$null, [ref]$errors)
            [pscustomobject]@{ Ast = $ast; Errors = $errors }
        }

        # Pester parameters that take a VALUE. If one of these is the final element of a
        # command, its argument ended up on the next line and is now a no-op statement.
        # Matching on "is it last" rather than on bare strings elsewhere is what keeps this
        # free of false positives: a string as an if-branch or scriptblock result is
        # perfectly legitimate and the naive version flags all of them.
        $script:ValueTakingParameters = @(
            'Because', 'Be', 'BeExactly', 'BeLike', 'BeLikeExactly', 'BeGreaterThan',
            'BeGreaterOrEqual', 'BeLessThan', 'BeLessOrEqual', 'Contain', 'Match',
            'MatchExactly', 'HaveCount', 'BeOfType', 'ErrorId', 'ExpectedMessage'
        )

        function Find-DanglingParameter {
            param([string]$Path)
            $parsed = Get-ScriptAst -Path $Path
            $parsed.Ast.FindAll(
                { param($node) $node -is [System.Management.Automation.Language.CommandAst] },
                $true
            ) | ForEach-Object {
                $last = $_.CommandElements[-1]
                if ($last -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $last.ParameterName -in $script:ValueTakingParameters -and
                    $null -eq $last.Argument) {
                    "line $($last.Extent.StartLineNumber): -$($last.ParameterName)"
                }
            }
        }
    }

    It '<_> parses without syntax errors' -ForEach @(
        'ADx.Live.Tests.ps1', 'ADx.Stress.Tests.ps1',
        'Seed-ADxTestDomain.ps1', 'Invoke-ADxStressSeed.ps1'
    ) {
        $parsed = Get-ScriptAst -Path (Join-Path $script:LabRoot $_)
        $parsed.Errors | Should -BeNullOrEmpty
    }

    It '<_> has no value-taking parameter left dangling at end of line' -ForEach @(
        'ADx.Live.Tests.ps1', 'ADx.Stress.Tests.ps1'
    ) {
        $dangling = Find-DanglingParameter -Path (Join-Path $script:LabRoot $_)
        $dangling | Should -BeNullOrEmpty -Because "the argument is on the next line and is silently discarded: $($dangling -join '; ')"
    }

    It '<_> never splits a -f operand list across a method argument boundary' -ForEach @(
        'Invoke-ADxStressSeed.ps1', 'Seed-ADxTestDomain.ps1',
        'ADx.Live.Tests.ps1', 'ADx.Stress.Tests.ps1'
    ) {
        # $w.WriteLine("{0}{1}" -f $a, $b) does NOT do what it looks like: inside a method
        # call PowerShell reads the comma as an argument separator, so -f receives only $a
        # and $b becomes a second argument to WriteLine. The format string then throws on
        # {1} at runtime -- and only at runtime, which cost a seeding run against a real DC
        # to discover. The fix is a second set of parentheses.
        #
        # Detectable structurally: a method invocation whose FIRST argument is a format
        # expression, and which has more arguments after it.
        $parsed = Get-ScriptAst -Path (Join-Path $script:LabRoot $_)

        $split = $parsed.Ast.FindAll(
            { param($node)
              $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
              $node.Arguments -and $node.Arguments.Count -gt 1 -and
              $node.Arguments[0] -is [System.Management.Automation.Language.BinaryExpressionAst] -and
              $node.Arguments[0].Operator -eq [System.Management.Automation.Language.TokenKind]::Format },
            $true
        ) | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Extent.Text -replace '\s+',' ')" }

        $split | Should -BeNullOrEmpty -Because "-f lost its later operands to the method's argument list; wrap it in parentheses: $($split -join '; ')"
    }

    It 'This suite has no dangling value-taking parameter either' {
        $dangling = Find-DanglingParameter -Path (Join-Path $PSScriptRoot 'ADx.Tests.ps1')
        $dangling | Should -BeNullOrEmpty -Because "the argument is on the next line and is silently discarded: $($dangling -join '; ')"
    }

    It 'Tags every Describe in <_> with Live' -ForEach @(
        'ADx.Live.Tests.ps1', 'ADx.Stress.Tests.ps1'
    ) {
        # `Live` means "needs a domain controller" and is the tag the offline gate excludes.
        # An untagged Describe in either file runs during that gate and fails or hangs
        # reaching for a DC -- the offline suite's whole contract is that it needs nothing.
        # (Stress blocks carry Live AND Stress for exactly this reason: one exclusion covers
        # both files, while -Tag Stress still selects just the scale suite.)
        $parsed = Get-ScriptAst -Path (Join-Path $script:LabRoot $_)

        $describes = $parsed.Ast.FindAll(
            { param($node)
              $node -is [System.Management.Automation.Language.CommandAst] -and
              $node.GetCommandName() -eq 'Describe' },
            $true)

        $describes | Should -Not -BeNullOrEmpty

        $untagged = $describes | Where-Object {
            $tagParam = $_.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                $_.ParameterName -in @('Tag', 'Tags')
            }
            -not $tagParam -or $_.Extent.Text -notmatch '\bLive\b'
        } | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.CommandElements[1].Extent.Text)" }

        $untagged | Should -BeNullOrEmpty -Because "a Describe without the Live tag runs in the offline gate and needs a DC: $($untagged -join '; ')"
    }

    It 'Keeps -WhatIf working on <_>' -ForEach @(
        'Seed-ADxTestDomain.ps1', 'Invoke-ADxStressSeed.ps1'
    ) {
        # These create thousands of AD objects; losing ShouldProcess support would remove the
        # only safe way to inspect what they are about to do.
        $parsed = Get-ScriptAst -Path (Join-Path $script:LabRoot $_)
        $parsed.Ast.Extent.Text | Should -Match 'SupportsShouldProcess'
    }

    It 'Documents the live runbook alongside the lab tooling' {
        Join-Path $script:LabRoot 'LIVE-VALIDATION.md' | Should -Exist
    }

    It 'Points the lab suites at the module across the tree boundary' {
        # The lab files live outside adx/, so their module path is ../../adx/module. A stale
        # relative path here fails only on the DC, which is the worst place to discover it.
        foreach ($file in 'ADx.Live.Tests.ps1', 'ADx.Stress.Tests.ps1') {
            $text = Get-Content (Join-Path $script:LabRoot $file) -Raw
            $text | Should -Match ([regex]::Escape("'../../adx/module/adx.psd1'"))
            $text | Should -Not -Match ([regex]::Escape("'../module/adx.psd1'"))
        }
    }
}
