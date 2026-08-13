# Build script for ADx PowerShell module
# Compiles both projects and stages output into module/ directory

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ModuleRoot = Join-Path $PSScriptRoot 'module'

Write-Host "Building ADx ($Configuration)..." -ForegroundColor Cyan

# Clean previous build artifacts
$binDir = Join-Path $ModuleRoot 'bin'
if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
Get-ChildItem $ModuleRoot -Filter '*.dll' | Remove-Item -Force
Get-ChildItem $ModuleRoot -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $ModuleRoot -Filter '*.deps.json' | Remove-Item -Force

# Build the solution. Version stamping (FileVersion/InformationalVersion from the manifest's
# ModuleVersion) happens in Directory.Build.props so that EVERY build path stamps -- a bare
# `dotnet build`/`dotnet test` restages module/*.dll via the StageModuleOutput target, and a
# stamping step that lived only here would let those silently revert the staged DLLs.
dotnet build "$PSScriptRoot/ADx.slnx" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# Copy Cmdlets + Engine DLLs.
# Detect TFM from csproj instead of hardcoding (survives TFM upgrades)
$csproj = [xml](Get-Content "$PSScriptRoot/src/ADx.Cmdlets/ADx.Cmdlets.csproj")
$tfm = $csproj.Project.PropertyGroup.TargetFramework
$CmdletsOutput = Join-Path $PSScriptRoot "src/ADx.Cmdlets/bin/$Configuration/$tfm"
Copy-Item "$CmdletsOutput/ADx.Cmdlets.dll" $ModuleRoot -Force
Copy-Item "$CmdletsOutput/ADx.Cmdlets.pdb" $ModuleRoot -Force -ErrorAction SilentlyContinue
Copy-Item "$CmdletsOutput/ADx.Engine.dll" $ModuleRoot -Force
Copy-Item "$CmdletsOutput/ADx.Engine.pdb" $ModuleRoot -Force -ErrorAction SilentlyContinue

# Copy deps.json (useful for diagnostic tooling; deleted by clean step above)
$depsJson = Join-Path $CmdletsOutput 'ADx.Cmdlets.deps.json'
if (Test-Path $depsJson) {
    Copy-Item $depsJson $ModuleRoot -Force
} else {
    Write-Warning "ADx.Cmdlets.deps.json not found in build output - diagnostics may be limited"
}

# NOTE: there is deliberately no Dependencies/ folder and no ALC resolver here.
# ADx has zero third-party runtime dependencies: System.DirectoryServices.Protocols is
# referenced with ExcludeAssets="runtime" and comes from PowerShell's own app base. That
# removes the entire assembly-brokering layer the Graph module needs for Polly.

# Verify module output is in expected state
$RequiredRoot = @('ADx.Cmdlets.dll', 'ADx.Engine.dll', 'adx.psd1', 'adx.psm1')
foreach ($f in $RequiredRoot) {
    if (-not (Test-Path (Join-Path $ModuleRoot $f))) {
        throw "Module integrity check failed: $f missing from module root"
    }
}

# Directory-services assemblies must never reach the build output, let alone module/.
#
# The System.DirectoryServices.Protocols package is RID-split: its portable lib/net9.0 asset
# is a ~74 KB PlatformNotSupportedException stub, and that is the copy CopyLocalLockFileAssemblies
# would drop into bin/. Shipping it would shadow the working implementation pwsh already carries
# in its app base and break every Windows host. ADx.Engine.csproj prevents this with
# ExcludeAssets="runtime".
#
# This checks $CmdletsOutput, NOT $ModuleRoot, and the distinction is the whole point: the clean
# step above deletes every *.dll in the module root before we get here, so a module-root check
# can never fire and is pure theatre. bin/ is where a regressed ExcludeAssets actually shows up.
$ForbiddenInOutput = @(
    'System.DirectoryServices.Protocols.dll'
    'System.DirectoryServices.dll'
    'System.DirectoryServices.AccountManagement.dll'
)
foreach ($f in $ForbiddenInOutput) {
    $leaked = Join-Path $CmdletsOutput $f
    if (Test-Path $leaked) {
        throw "Module integrity check failed: $f reached the build output at $leaked. The portable lib/ asset is a PlatformNotSupported stub; the working implementation ships with PowerShell. Check that ADx.Engine.csproj still uses ExcludeAssets=`"runtime`"."
    }
}

Write-Host "`nBuild complete!" -ForegroundColor Green
Write-Host "Module output: $ModuleRoot" -ForegroundColor Yellow
Write-Host "`nTo use:" -ForegroundColor Cyan
Write-Host "  Import-Module '$ModuleRoot/adx.psd1'" -ForegroundColor White
Write-Host "  Get-ADxRootDse" -ForegroundColor White
Write-Host "  Search-ADxObject -LdapFilter '(objectCategory=group)' -All" -ForegroundColor White
