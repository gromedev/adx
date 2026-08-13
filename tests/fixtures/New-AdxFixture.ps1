#Requires -Version 7.0

<#
    .SYNOPSIS
        Deterministic synthetic-directory fixture generator with a reconciliation manifest.

    .DESCRIPTION
        The data-integrity programme's ground truth. One generator emits BOTH the fixture
        (LDIF for the OpenLDAP CI tier; CSV for the live-lab AD tier) and the manifest the
        tests reconcile against -- from one deterministic derivation, so the fixture and the
        oracle cannot drift apart. Everything derives from -Seed via HMAC-SHA256: same seed,
        same directory, same manifest, forever.

        Why this exists: the module's recurring failure class is silently dropped results. A
        test that knows EXACTLY what the directory contains turns "looks complete" into
        set-reconciliation evidence -- if 12,000 entries were seeded and 11,999 come back,
        the failure names the missing DN. Counts alone cannot do that (one substitution plus
        one omission passes a count), so the manifest carries every DN and its expected
        attribute values, plus an order-independent population checksum.

        Populations (all under one fixture root):
          - bulk        : -BulkCount entries (default 12,000) -- the headline oracle
          - b0999..b2001: page-boundary populations (999, 1000, 1001, 2000, 2001) sized at
                          the wire page-size cliff edges, where a paging bug drops rows
          - special     : DNs and values that stress escaping (comma, plus, quotes, slash,
                          non-ASCII UTF-8) -- where marshalling bugs corrupt or lose entries
          - multivalue  : entries with 1 vs many values in a multi-valued attribute

    .PARAMETER Seed
        Deterministic derivation key. Change it and every derived value changes.

    .PARAMETER Base
        Directory suffix the fixture lives under.

    .PARAMETER FixtureOu
        The single OU (relative to -Base) that contains every population.

    .PARAMETER BulkCount
        Size of the bulk population.

    .PARAMETER OutputDir
        Where fixture.ldif / fixture.csv / manifest.json land.

    .PARAMETER Format
        Ldif (Tier 1, OpenLDAP), Csv (Tier 2, AD provisioning), or Both.

    .EXAMPLE
        ./New-AdxFixture.ps1 -OutputDir ./out
        ldapadd -H ldap://localhost:1389 -D cn=admin,dc=example,dc=org -w adminpassword -f ./out/fixture.ldif
#>

[CmdletBinding()]
param(
    [string] $Seed = 'adx-integrity-1',
    [string] $Base = 'dc=example,dc=org',
    [string] $FixtureOu = 'ou=adxfix',
    [ValidateRange(1, 1000000)]
    [int] $BulkCount = 12000,
    [Parameter(Mandatory)]
    [string] $OutputDir,
    [ValidateSet('Ldif', 'Csv', 'Both')]
    [string] $Format = 'Ldif',

    # Ldif = OpenLDAP inetOrgPerson (Tier 1 container). AD = Active Directory user objects,
    # created DISABLED (userAccountControl 514) with a unique short sAMAccountName -- for the
    # live-lab tier. The manifest shape is identical either way, so one oracle reconciles both.
    [ValidateSet('Ldif', 'AD')]
    [string] $Target = 'Ldif',

    # Emit only the bulk population (skip the page-boundary/special/multivalue OUs). For a
    # fast pipeline smoke, and for scaling the bulk count without recreating the fixed-size
    # boundary OUs that would collide on a second import.
    [switch] $BulkOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($Seed))

function Get-Token([string] $name) {
    # 16 hex chars of HMAC(seed, name): deterministic, seed-scoped, collision-safe at this scale.
    $bytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($name))
    ([System.Convert]::ToHexString($bytes)).Substring(0, 16).ToLowerInvariant()
}

$fixtureRoot = "$FixtureOu,$Base"
$populations = [ordered]@{}
# AD needs a unique, rule-legal sAMAccountName (<=20 chars, domain-unique) independent of the
# CN -- special-character CNs cannot be sAMAccountNames. A global counter gives every entry a
# stable "axiNNNNNNN" handle; the manifest records it so identity round-trips can resolve by it.
$script:SamCounter = 0

function New-Person([string] $ou, [string] $cn, [int] $index, [string[]] $extraDescriptions = @()) {
    # employeeNumber carries the index, description carries the seed-derived token: a
    # substituted or half-written entry fails value reconciliation even when the DN matches.
    $token = Get-Token "$ou/$cn"
    $script:SamCounter++
    [pscustomobject]@{
        Dn             = "cn=$cn,$ou,$fixtureRoot"
        Cn             = $cn
        Sn             = "Fixture $index"
        SamAccountName = 'axi{0:d7}' -f $script:SamCounter
        EmployeeNumber = [string] $index
        Description    = @("adxfix-$token") + $extraDescriptions
    }
}

# ---- bulk -------------------------------------------------------------------------------
$bulk = [System.Collections.Generic.List[object]]::new()
for ($i = 1; $i -le $BulkCount; $i++) {
    $bulk.Add((New-Person 'ou=bulk' ('bulk-{0:d6}' -f $i) $i))
}
$populations['bulk'] = @{ Ou = "ou=bulk,$fixtureRoot"; Entries = $bulk }

# ---- page-boundary populations ----------------------------------------------------------
foreach ($size in ($BulkOnly ? @() : @(999, 1000, 1001, 2000, 2001))) {
    $name = 'b{0:d4}' -f $size
    $list = [System.Collections.Generic.List[object]]::new()
    for ($i = 1; $i -le $size; $i++) {
        $list.Add((New-Person "ou=$name" ('{0}-{1:d4}' -f $name, $i) $i))
    }
    $populations[$name] = @{ Ou = "ou=$name,$fixtureRoot"; Entries = $list }
}

# ---- special characters -----------------------------------------------------------------
# RDN values below are the UNESCAPED forms; DN escaping is applied at emission. Each is a
# real-world spelling that has broken an LDAP tool somewhere: RDN metacharacters, leading
# hash, and non-ASCII UTF-8.
$specialNames = @(
    'Doe, John'          # escaped comma in RDN
    'Smith + Jones'      # escaped plus
    'Quote "Q" Person'   # escaped quotes
    'Back\slash'         # escaped backslash
    '#LeadingHash'       # escaped leading hash
    'Ærøskøbing Ålqvist' # Latin-1 supplement UTF-8
    'Пример Пользователь' # Cyrillic UTF-8
    '例子 用户'           # CJK UTF-8
)
$specialList = [System.Collections.Generic.List[object]]::new()
$specialIndex = 0
foreach ($name in ($BulkOnly ? @() : $specialNames)) {
    $specialIndex++
    # Escape RDN metacharacters per RFC 4514 for the DN; cn attribute value stays unescaped.
    $escaped = $name -replace '\\', '\\\\' -replace ',', '\,' -replace '\+', '\+' -replace '"', '\"' -replace '^#', '\#'
    $token = Get-Token "ou=special/$name"
    $script:SamCounter++
    $specialList.Add([pscustomobject]@{
        Dn             = "cn=$escaped,ou=special,$fixtureRoot"
        Cn             = $name
        Sn             = "Special $specialIndex"
        SamAccountName = 'axi{0:d7}' -f $script:SamCounter
        EmployeeNumber = [string] (90000 + $specialIndex)
        Description    = @("adxfix-$token")
    })
}
if (-not $BulkOnly) {
    $populations['special'] = @{ Ou = "ou=special,$fixtureRoot"; Entries = $specialList }
}

# ---- multi-valued -----------------------------------------------------------------------
# description with exactly one value vs several: scalar-vs-array projection and multi-value
# completeness in one small population.
if (-not $BulkOnly) {
    $multiList = [System.Collections.Generic.List[object]]::new()
    $multiList.Add((New-Person 'ou=multivalue' 'multi-single' 95001))
    $multiList.Add((New-Person 'ou=multivalue' 'multi-three' 95002 @('second value', 'third value')))
    $multiList.Add((New-Person 'ou=multivalue' 'multi-five' 95003 @('v2', 'v3', 'v4', 'v5')))
    $populations['multivalue'] = @{ Ou = "ou=multivalue,$fixtureRoot"; Entries = $multiList }
}

# ---- checksums + manifest ---------------------------------------------------------------
$sha = [System.Security.Cryptography.SHA256]::Create()
function Get-PopulationChecksum($entries) {
    # Order-independent XOR of per-entry hashes over the reconciled tuple. Any missing,
    # extra, or value-substituted entry changes it.
    $acc = [byte[]]::new(32)
    foreach ($e in $entries) {
        # Description sorted: it is multi-valued and LDAP does not preserve value order, so the
        # oracle must be order-independent on both sides (see AdxReconciliation.psm1).
        $tuple = "{0}|{1}|{2}" -f $e.Dn.ToLowerInvariant(), $e.EmployeeNumber, ((@($e.Description) | Sort-Object) -join ';')
        $h = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($tuple))
        for ($b = 0; $b -lt 32; $b++) { $acc[$b] = $acc[$b] -bxor $h[$b] }
    }
    [System.Convert]::ToHexString($acc).ToLowerInvariant()
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$manifest = [ordered]@{
    seed        = $Seed
    base        = $Base
    fixtureRoot = $fixtureRoot
    generated   = 'New-AdxFixture.ps1'
    populations = [ordered]@{}
}
foreach ($key in $populations.Keys) {
    $entries = $populations[$key].Entries
    $manifest.populations[$key] = [ordered]@{
        ou       = $populations[$key].Ou
        count    = $entries.Count
        checksum = Get-PopulationChecksum $entries
        entries  = @($entries | ForEach-Object {
            [ordered]@{
                dn             = $_.Dn
                cn             = $_.Cn
                samAccountName = $_.SamAccountName
                employeeNumber = $_.EmployeeNumber
                description    = @($_.Description)
            }
        })
    }
}
$manifestPath = Join-Path $OutputDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 6 -Compress | Set-Content -Path $manifestPath -Encoding utf8

# ---- LDIF emission ----------------------------------------------------------------------
if ($Format -in 'Ldif', 'Both') {
    $ldif = [System.Text.StringBuilder]::new()

    function Add-LdifAttribute([System.Text.StringBuilder] $sb, [string] $name, [string] $value) {
        # RFC 2849: values that are non-ASCII or begin with space/colon/'<' must be base64.
        $needsBase64 = $value -match '^[ :<]' -or $value -match '[^\x20-\x7E]'
        if ($needsBase64) {
            $encoded = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($value))
            [void] $sb.AppendLine("${name}:: $encoded")
        }
        else {
            [void] $sb.AppendLine("${name}: $value")
        }
    }

    function Add-LdifOu([System.Text.StringBuilder] $sb, [string] $dn, [string] $ouName) {
        Add-LdifAttribute $sb 'dn' $dn
        [void] $sb.AppendLine('objectClass: organizationalUnit')
        Add-LdifAttribute $sb 'ou' $ouName
        [void] $sb.AppendLine()
    }

    Add-LdifOu $ldif $fixtureRoot ($FixtureOu -replace '^ou=')
    foreach ($key in $populations.Keys) {
        $ou = $populations[$key].Ou
        Add-LdifOu $ldif $ou (($ou -split ',')[0] -replace '^ou=')
        foreach ($e in $populations[$key].Entries) {
            Add-LdifAttribute $ldif 'dn' $e.Dn
            if ($Target -eq 'AD') {
                # Real AD user objects, created DISABLED (userAccountControl 514 =
                # NORMAL_ACCOUNT | ACCOUNTDISABLE) so the fixture never introduces enabled
                # passwordless accounts. sAMAccountName is the rule-legal unique handle;
                # cn/name come from the RDN. ldifde adds these as-is.
                [void] $ldif.AppendLine('objectClass: user')
                Add-LdifAttribute $ldif 'sAMAccountName' $e.SamAccountName
                Add-LdifAttribute $ldif 'userAccountControl' '514'
                Add-LdifAttribute $ldif 'sn' $e.Sn
                Add-LdifAttribute $ldif 'employeeNumber' $e.EmployeeNumber
                foreach ($d in $e.Description) { Add-LdifAttribute $ldif 'description' $d }
            }
            else {
                [void] $ldif.AppendLine('objectClass: inetOrgPerson')
                Add-LdifAttribute $ldif 'cn' $e.Cn
                Add-LdifAttribute $ldif 'sn' $e.Sn
                Add-LdifAttribute $ldif 'employeeNumber' $e.EmployeeNumber
                foreach ($d in $e.Description) { Add-LdifAttribute $ldif 'description' $d }
            }
            [void] $ldif.AppendLine()
        }
    }
    Set-Content -Path (Join-Path $OutputDir 'fixture.ldif') -Value $ldif.ToString() -Encoding utf8 -NoNewline
}

# ---- CSV emission (Tier 2: AD provisioning input) ---------------------------------------
if ($Format -in 'Csv', 'Both') {
    $rows = foreach ($key in $populations.Keys) {
        foreach ($e in $populations[$key].Entries) {
            [pscustomobject]@{
                Population     = $key
                Dn             = $e.Dn
                Cn             = $e.Cn
                Sn             = $e.Sn
                EmployeeNumber = $e.EmployeeNumber
                Description    = ($e.Description -join '|')
            }
        }
    }
    $rows | Export-Csv -Path (Join-Path $OutputDir 'fixture.csv') -Encoding utf8 -NoTypeInformation
}

$total = ($populations.Values | ForEach-Object { $_.Entries.Count } | Measure-Object -Sum).Sum
Write-Host "Fixture: $total entries across $($populations.Count) populations -> $OutputDir"
Write-Host "Manifest: $manifestPath"
