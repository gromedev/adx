#Requires -Version 7.0

<#
    .SYNOPSIS
        Set-reconciliation assertions for the data-integrity suites.

    .DESCRIPTION
        The shared oracle-comparison contract for Tier 1 (OpenLDAP CI) and Tier 2 (live-lab
        AD): results are compared against a New-AdxFixture manifest population as SETS, not
        counts. A count passes with one substitution plus one omission; a reconciliation
        cannot, and its failure output is audit evidence -- expected/actual counts, the
        first missing and extra DNs BY NAME, value mismatches, and the checksum status.
#>

Set-StrictMode -Version Latest

function Get-AdxManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)
    Get-Content -Raw -Path $Path | ConvertFrom-Json
}

function Invoke-AdxReconciliation {
    <#
        .SYNOPSIS
            Reconcile actual result objects against one manifest population; returns a
            report object (does not assert -- Assert-AdxReconciliation does).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Population,          # one entry of manifest.populations
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Actual,
        [string] $DnProperty = 'DistinguishedName',
        # Compare attribute values too (employeeNumber/description). Off for surfaces that
        # deliberately return DNs only.
        [switch] $ValuesToo
    )

    $expectedByDn = @{}
    foreach ($e in $Population.entries) { $expectedByDn[$e.dn.ToLowerInvariant()] = $e }

    $actualByDn = @{}
    $duplicates = [System.Collections.Generic.List[string]]::new()
    foreach ($a in $Actual) {
        $dn = ([string] $a.$DnProperty).ToLowerInvariant()
        if ($actualByDn.ContainsKey($dn)) { $duplicates.Add($dn) } else { $actualByDn[$dn] = $a }
    }

    $missing = [System.Collections.Generic.List[string]]::new()
    foreach ($dn in $expectedByDn.Keys) { if (-not $actualByDn.ContainsKey($dn)) { $missing.Add($dn) } }
    $extra = [System.Collections.Generic.List[string]]::new()
    foreach ($dn in $actualByDn.Keys) { if (-not $expectedByDn.ContainsKey($dn)) { $extra.Add($dn) } }

    $mismatches = [System.Collections.Generic.List[string]]::new()
    $checksum = $null
    if ($ValuesToo) {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $acc = [byte[]]::new(32)
        foreach ($dn in $expectedByDn.Keys) {
            if (-not $actualByDn.ContainsKey($dn)) { continue }
            $exp = $expectedByDn[$dn]
            $act = $actualByDn[$dn]

            $actualEmployee = [string] $act.employeeNumber
            # Sorted: LDAP does not guarantee the order of a multi-valued attribute's values,
            # so reconciliation compares them as a SET. Order is AD's, not something ADx (or
            # any correct client) controls -- comparing ordered would flag a non-defect.
            $actualDescriptions = @(@($act.description) | Where-Object { $null -ne $_ } | ForEach-Object { [string] $_ } | Sort-Object)
            $expectedDescriptions = @(@($exp.description) | Sort-Object)

            if ($actualEmployee -ne [string] $exp.employeeNumber) {
                $mismatches.Add("$dn employeeNumber expected '$($exp.employeeNumber)' got '$actualEmployee'")
            }
            if (($actualDescriptions -join ';') -ne ($expectedDescriptions -join ';')) {
                $mismatches.Add("$dn description expected '$($expectedDescriptions -join ';')' got '$($actualDescriptions -join ';')'")
            }

            # Checksum over the MATCHED actual values, on the manifest's tuple shape (values
            # sorted, matching the generator): equals the manifest checksum only when nothing
            # is missing, extra, or substituted.
            $tuple = "{0}|{1}|{2}" -f $exp.dn.ToLowerInvariant(), $actualEmployee, ($actualDescriptions -join ';')
            $h = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($tuple))
            for ($b = 0; $b -lt 32; $b++) { $acc[$b] = $acc[$b] -bxor $h[$b] }
        }
        $checksum = [System.Convert]::ToHexString($acc).ToLowerInvariant()
    }

    [pscustomobject]@{
        ExpectedCount = $Population.count
        ActualCount   = $Actual.Count
        Missing       = $missing
        Extra         = $extra
        Duplicates    = $duplicates
        Mismatches    = $mismatches
        Checksum      = $checksum
        ChecksumOk    = -not $ValuesToo -or ($checksum -eq $Population.checksum -and $missing.Count -eq 0 -and $extra.Count -eq 0)
    }
}

function Assert-AdxReconciliation {
    <#
        .SYNOPSIS
            Throw with a named-DN evidence report unless the reconciliation is exact.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Population,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Actual,
        [string] $DnProperty = 'DistinguishedName',
        [switch] $ValuesToo,
        [string] $Surface = 'result set'
    )

    $r = Invoke-AdxReconciliation -Population $Population -Actual $Actual -DnProperty $DnProperty -ValuesToo:$ValuesToo

    $clean = $r.Missing.Count -eq 0 -and $r.Extra.Count -eq 0 -and $r.Duplicates.Count -eq 0 -and
             $r.Mismatches.Count -eq 0 -and $r.ChecksumOk
    if ($clean) { return $r }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("RECONCILIATION FAILED for $Surface")
    $lines.Add("  expected $($r.ExpectedCount) entries, got $($r.ActualCount)")
    if ($r.Missing.Count)    { $lines.Add("  MISSING ($($r.Missing.Count)): " + (($r.Missing | Select-Object -First 10) -join '; ')) }
    if ($r.Extra.Count)      { $lines.Add("  EXTRA ($($r.Extra.Count)): " + (($r.Extra | Select-Object -First 10) -join '; ')) }
    if ($r.Duplicates.Count) { $lines.Add("  DUPLICATED ($($r.Duplicates.Count)): " + (($r.Duplicates | Select-Object -First 10) -join '; ')) }
    if ($r.Mismatches.Count) { $lines.Add("  VALUE MISMATCHES ($($r.Mismatches.Count)): " + (($r.Mismatches | Select-Object -First 5) -join ' || ')) }
    if (-not $r.ChecksumOk)  { $lines.Add("  CHECKSUM: expected $($Population.checksum), computed $($r.Checksum)") }

    throw ($lines -join "`n")
}

Export-ModuleMember -Function Get-AdxManifest, Invoke-AdxReconciliation, Assert-AdxReconciliation
