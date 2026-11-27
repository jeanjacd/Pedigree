# Builds Pedigree as a native DLL (NativeAOT) for the game's community mod framework.
#
# Needs, once:
#   - The current .NET SDK (11, or 10 LTS)   https://dotnet.microsoft.com/download
#     The mod targets .NET 8 like the framework SDK, so expect an "out of support" warning; it is harmless.
#   - Visual Studio Build Tools 2026 (or 2022) with the "Desktop development with C++" workload (NativeAOT uses its linker)
#   - The framework SDK DLL in sdk\ (see sdk\README.md)
#
# Usage (PowerShell, from this folder):
#   .\build.ps1                                   # runs the tests, builds out\Pedigree.dll
#   .\build.ps1 -Install -GameDir "<game folder>" # also copies it into <game folder>\Mods

param(
    [string]$GameDir = "",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Running the planner tests..."
dotnet run --project tests\Pedigree.Tests -c Release -- tests\fixtures
if ($LASTEXITCODE -ne 0) { throw "Planner tests failed; not building." }

Write-Host "Publishing Pedigree.dll (NativeAOT, win-x64)..."
dotnet publish src\Mod\Pedigree.csproj -c Release -r win-x64 -p:PublishAot=true -o out
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $PSScriptRoot "out\Pedigree.dll"
Write-Host "Built $dll"

if ($Install) {
    if (-not $GameDir) { throw "Pass -GameDir with the game's install folder (the one with its .exe)." }
    if (-not (Test-Path $GameDir)) { throw "$GameDir does not exist." }
    if (-not (Test-Path (Join-Path $GameDir "version.dll"))) { Write-Warning "The mod loader (version.dll) is not in $GameDir yet." }
    $mods = Join-Path $GameDir "Mods"
    New-Item -ItemType Directory -Force -Path $mods | Out-Null
    Copy-Item $dll (Join-Path $mods "Pedigree.dll") -Force
    Write-Host "Installed to $mods\Pedigree.dll. Start the game and press F10."
}
