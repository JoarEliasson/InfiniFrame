#Requires -Version 7.0
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Rid,
    [Parameter(Mandatory = $true)]
    [string]$CefRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot = '',
    [string]$Generator = ''
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'cef-distributions.json') -Raw | ConvertFrom-Json
$CefRoot = [IO.Path]::GetFullPath($CefRoot)
$identityPath = Join-Path $CefRoot 'cef-distribution.json'
if (-not (Test-Path -LiteralPath $identityPath)) {
    throw 'CEF distribution identity is missing. Use scripts\cef_restore.py to restore the pinned archive first.'
}
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
if ($identity.rid -ne $Rid -or $identity.cefVersion -ne $pin.cefVersion -or
    $identity.upstreamSha1 -ne $pin.distributions.$Rid.sha1) {
    throw "CEF distribution version/RID/checksum mismatch for $Rid."
}
if (($IsWindows -and -not $Rid.StartsWith('win-')) -or ($IsLinux -and -not $Rid.StartsWith('linux-')) -or
    (-not $IsWindows -and -not $IsLinux)) {
    throw 'Build CEF on the requested operating system. macOS keeps the default backend.'
}
if ($IsLinux) {
    $processArch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if ($Rid -ne "linux-$processArch") {
        throw 'The Linux CEF build script requires a native process/toolchain for the requested architecture. Cross-compilation is not configured by this script.'
    }
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repository 'artifacts\cef' }
$apiVersion = '15600'
$root = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) "$($pin.cefVersion)\$Rid\$Configuration\api-$apiVersion-sandbox"
$build = Join-Path $root 'build'
$installed = Join-Path $root 'installed'
New-Item -ItemType Directory -Force -Path $root | Out-Null
# Both entrypoints own a real configure/clean-build/install before emitting a build receipt.
# The explicit local CEF root never triggers restore/download. Existing installed output is refused;
# choose a fresh OutputRoot for another build so historical payloads and their provenance stay intact.
$python = if ($IsWindows) { 'python' } else { 'python3' }
$arguments = @((Join-Path $PSScriptRoot 'cef_runtime.py'), 'build-native', '--rid', $Rid,
    '--cef-root', $CefRoot, '--build-dir', $build, '--payload', $installed,
    '--configuration', $Configuration, '--parallel', '4')
if (-not [string]::IsNullOrWhiteSpace($Generator)) { $arguments += @('--generator', $Generator) }
if ($IsWindows) { $arguments += @('--binlog', (Join-Path $root 'native-{}.binlog')) }
& $python @arguments
if ($LASTEXITCODE -ne 0) { throw "CEF native build/receipt failed ($LASTEXITCODE)." }
