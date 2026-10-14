# Fetch one published HIDMaestro release, check it against its pinned
# hashes and keep its HIDMaestro.Core.dll in the cache. The coexistence
# probe builds a peer program against that DLL, so a mixed-version test
# runs the exact bytes the release shipped. A hash mismatch fails the
# build rather than testing against an unverified binary.
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ZipSha256,
    [Parameter(Mandatory)][string]$CoreSha256,
    [Parameter(Mandatory)][string]$Cache
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Cache $Version
$core = Join-Path $dir 'HIDMaestro.Core.dll'
if ((Test-Path $core) -and ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -eq $CoreSha256)) { exit 0 }

New-Item -ItemType Directory -Force -Path $dir | Out-Null
$zip = Join-Path $Cache "HIDMaestro-$Version.zip"
if (-not (Test-Path $zip) -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $ZipSha256) {
    $url = "https://github.com/hifihedgehog/HIDMaestro/releases/download/$Version/HIDMaestro-$Version.zip"
    Write-Host "fetch_legacy: downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
}
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
if ($actual -ne $ZipSha256) {
    Remove-Item -LiteralPath $zip -Force
    throw "fetch_legacy: HIDMaestro-$Version.zip hash $actual, expected $ZipSha256. Deleted."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.GetEntry('HIDMaestro.Core.dll')
    if ($null -eq $entry) { throw "fetch_legacy: HIDMaestro-$Version.zip has no HIDMaestro.Core.dll at its root" }
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $core, $true)
} finally { $archive.Dispose() }

$actual = (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash
if ($actual -ne $CoreSha256) {
    Remove-Item -LiteralPath $core -Force
    throw "fetch_legacy: $Version HIDMaestro.Core.dll hash $actual, expected $CoreSha256. Deleted."
}
