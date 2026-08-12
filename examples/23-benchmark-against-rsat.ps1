# Benchmark ADx against RSAT on your own domain.
#
# The README's numbers come from one lab domain on modest hardware. Ratios move with
# object count, attribute count, network latency and DC load, so run this rather than
# trusting them - the whole argument for this module is a measurement, and a
# measurement you did not take is an opinion.
#
# Requires Windows with RSAT installed for the comparison half. Without it, the ADx
# half still runs and gives you an absolute baseline.
#
# Requirements: PowerShell 7.5+, read access to the directory.

Import-Module "$PSScriptRoot/../module/adx.psd1"

$runs = 5   # first run is discarded as warm-up; the rest are reported as a median

$hasRsat = $IsWindows -and (Get-Module -ListAvailable ActiveDirectory)
if ($hasRsat) { Import-Module ActiveDirectory } else {
    Write-Host "RSAT ActiveDirectory module not available - measuring ADx only.`n"
}

function Measure-Median {
    param([scriptblock] $Action, [int] $Runs)

    $samples = foreach ($i in 1..$Runs) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $objects = @(& $Action).Count
        $sw.Stop()
        [PSCustomObject]@{ Ms = $sw.ElapsedMilliseconds; Objects = $objects }
    }

    $measured = @($samples | Select-Object -Skip 1)   # discard the warm-up run
    $sorted = @($measured.Ms | Sort-Object)

    [PSCustomObject]@{
        Median  = $sorted[[math]::Floor($sorted.Length / 2)]
        Objects = $measured[0].Objects
    }
}

$scenarios = @(
    @{ Name = 'default properties'; Adx = { Get-ADxUser -Filter * };                Rsat = { Get-ADUser -Filter * } }
    @{ Name = 'all properties';     Adx = { Get-ADxUser -Filter * -Properties * };  Rsat = { Get-ADUser -Filter * -Properties * } }
    @{ Name = 'single lookup';      Adx = { Get-ADxUser Administrator };            Rsat = { Get-ADUser Administrator } }
)

$results = foreach ($s in $scenarios) {
    $adx = Measure-Median -Action $s.Adx -Runs $runs
    $rsat = if ($hasRsat) { Measure-Median -Action $s.Rsat -Runs $runs } else { $null }

    [PSCustomObject]@{
        Scenario  = $s.Name
        Objects   = $adx.Objects
        ADxMs     = $adx.Median
        RsatMs    = if ($rsat) { $rsat.Median } else { 'n/a' }
        Speedup   = if ($rsat -and $adx.Median -gt 0) { "{0:N1}x" -f ($rsat.Median / $adx.Median) } else { 'n/a' }
        SameCount = if ($rsat) { $adx.Objects -eq $rsat.Objects } else { 'n/a' }
    }
}

$results | Format-Table -AutoSize

# The memory half, which matters more than speed on a large directory from a laptop.
# Same query, streamed into a counter versus collected into a variable.
[GC]::Collect()
$before = [GC]::GetTotalMemory($true)
$streamed = 0
Get-ADxUser -Filter * -Properties Department | ForEach-Object { $streamed++ }
$streamedMb = [math]::Round(([GC]::GetTotalMemory($false) - $before) / 1MB, 1)

[GC]::Collect()
$before = [GC]::GetTotalMemory($true)
$collected = Get-ADxUser -Filter * -Properties Department
$collectedMb = [math]::Round(([GC]::GetTotalMemory($false) - $before) / 1MB, 1)

[PSCustomObject]@{
    Rows                        = $streamed
    'Streamed (MB)'             = $streamedMb
    'Collected into a var (MB)' = $collectedMb
} | Format-List

$collected = $null

<#
Sample output

Measured on the lab DC - Windows Server 2022, single domain, 3,732 users, BOTH modules
running on the domain controller so neither pays network latency, median of 5 runs with
the warm-up discarded:

Scenario           Objects ADxMs RsatMs Speedup SameCount
--------           ------- ----- ------ ------- ---------
default properties    3732   410   1130    2.8x      True
all properties        3732  1370  12530    9.1x      True
single lookup            1    31     25    0.8x      True

Rows                      : 3732
Streamed (MB)             : 5.1
Collected into a var (MB) : 12.4

Read the third row honestly: for a single object RSAT is marginally FASTER. A fixed
connect/bind/RootDSE cost of roughly 15ms dominates anything that small, and ADx pays
it per invocation. This module is built for the sweep, not the lookup.

The second row is where the argument lives, and it went the opposite way from the
prediction. An earlier README expected near parity on -Properties *, reasoning that
the wire would be dominated by attribute bytes either way. Wrong: -Properties * is
where the gap is WIDEST, because ADWS pays SOAP/XML serialisation per attribute and
LDAP does not. Pulling every attribute for 3,732 users takes RSAT 12.5 seconds and
ADx 1.4.

Your numbers will differ. Object count, attribute count, and whether you are running
on the DC or across a WAN all move them - and RSAT's ADWS hop is the piece that
degrades fastest with latency, so running this from a workstation usually widens the
gap rather than narrowing it.
#>
