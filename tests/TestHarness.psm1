#Requires -Version 7.0

<#
    .SYNOPSIS
        Pester harness for the ADx module.

    .DESCRIPTION
        Wraps Invoke-Pester so CI and local runs share one entry point.
        These tests cover the PowerShell-facing surface only: the module manifest,
        packaging, formatting, and the cmdlet/parameter contract of the built module.

        Engine and cmdlet internals (filter translation, paging, projection) are
        covered by the xUnit suite in tests/ADx.Tests, run via `dotnet test`.
#>

$script:ManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'module/adx.psd1'

function Invoke-TestHarness
{
    <#
        .SYNOPSIS
            Run the Pester test suite for the ADx module.

        .PARAMETER TestResultsFile
            NUnit XML results path. Defaults to tests/TestResults.xml.

        .PARAMETER TestPath
            Test file or directory to run. Defaults to tests/ADx.Tests.ps1.

        .OUTPUTS
            The Pester run object. Callers check $result.FailedCount.
    #>
    [CmdletBinding()]
    param
    (
        [Parameter()]
        [System.String]
        $TestResultsFile = (Join-Path $PSScriptRoot 'TestResults.xml'),

        [Parameter()]
        [System.String]
        $TestPath = (Join-Path $PSScriptRoot 'ADx.Tests.ps1')
    )

    $pesterModule = Get-Module -Name Pester -ListAvailable |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if ($null -eq $pesterModule)
    {
        throw 'Pester is not installed. Run: Install-Module Pester -MinimumVersion 5.0 -Force -SkipPublisherCheck'
    }

    if ($pesterModule.Version.Major -lt 5)
    {
        throw "Pester 5.0 or later is required; found $($pesterModule.Version)."
    }

    Import-Module -Name $pesterModule.Path -Force

    # The module must be built before the surface tests can inspect it
    if (-not (Test-Path -Path $script:ManifestPath))
    {
        throw "Module manifest not found at '$script:ManifestPath'. Run ./build.ps1 first."
    }

    $configuration = New-PesterConfiguration
    $configuration.Run.Path = $TestPath
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = 'Detailed'
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputFormat = 'NUnitXml'
    $configuration.TestResult.OutputPath = $TestResultsFile

    # Binary module: there is no PowerShell source to instrument, so coverage
    # would only measure the psm1 shim. Never collected.
    $configuration.CodeCoverage.Enabled = $false

    return Invoke-Pester -Configuration $configuration
}

Export-ModuleMember -Function Invoke-TestHarness
