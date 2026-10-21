# Unpack the usbip-win2 host controller driver files out of the pinned
# installers, so the SDK can move a machine between transport versions
# through PnP without ever running the vendor installer over an existing
# install. That installer launches the previous version's uninstaller,
# which shows a window and pulls the filter off every USB root hub.
#
# Every input and output is hash-checked against UsbipPackage.json. The
# unpacker is fetched from a pinned commit and hash-checked too.
#
# -ManifestPath names another manifest of the same shape, such as S73's
# other release (test\probes\usbip_install_check\ForeignUsbipPackage.json),
# and -FetchInstallers fetches each installer from its manifest URL when it
# is not already in -InstallerDirectory. Only the files a manifest lists
# are unpacked.

param(
    [Parameter(Mandatory=$true)][string]$InstallerDirectory,
    [Parameter(Mandatory=$true)][string]$DestinationDirectory,
    [string]$CacheDirectory = '',
    # x64 only for a build made with HMSkipArm64. A -File invocation passes
    # "x64,arm64" as one string, so the list is split here.
    [string[]]$Architectures = @('x64', 'arm64'),
    [string]$ManifestPath = '',
    [switch]$FetchInstallers
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# $PSScriptRoot is empty inside a param default under Windows PowerShell 5.1.
if (!$CacheDirectory) { $CacheDirectory = Join-Path $PSScriptRoot '..\build\downloads' }
if (!$ManifestPath) { $ManifestPath = Join-Path $PSScriptRoot '..\sdk\HIDMaestro.Core\UsbipPackage.json' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$Architectures = @($Architectures | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($arch in $Architectures) {
    if (!$manifest.architectures.$arch) { throw "$(Split-Path -Leaf $ManifestPath) has no architecture '$arch'." }
}

function Test-FileHash([string]$Path, [string]$Expected) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
}

# A download lands under a name of this process's own and is moved into
# place only once its hash checks, so a build killed partway, or another
# build sharing build\downloads, never leaves a partial file at $Path.
function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Hash) {
    if (Test-FileHash $Path $Hash) { return }
    if (Test-Path -LiteralPath $Path) { throw "Hash mismatch in cached file: $Path. Delete it and rebuild to fetch it again." }
    $partial = "$Path.$PID.tmp"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial
        if (!(Test-FileHash $partial $Hash)) { throw "Downloaded file failed SHA256 verification: $Url" }
        Move-Item -LiteralPath $partial -Destination $Path -Force
    }
    finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}

# Nothing to do when every output is already in place and correct.
$pending = @()
foreach ($arch in $Architectures) {
    $package = $manifest.architectures.$arch
    foreach ($file in $package.files.PSObject.Properties) {
        if (!(Test-FileHash (Join-Path (Join-Path $DestinationDirectory $arch) $file.Name) $file.Value)) { $pending += $arch; break }
    }
}
if ($pending.Count -eq 0) { exit 0 }

New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
$extractorCommit = '6fb49264aacf512a093e7b4fc6fb3dd266dad31a'
$extractorHash = 'f2f037fdbc63de31248efae9ccb294398d160dc0cbad5f36d14c2f159e17bbf5'
$extractorZip = Join-Path $CacheDirectory 'innounp-2.71.1.zip'
Get-VerifiedFile "https://raw.githubusercontent.com/jrathlev/InnoUnpacker-Windows-GUI/$extractorCommit/innounp-2/bin/innounp-2.zip" $extractorZip $extractorHash
$extractorDirectory = Join-Path $CacheDirectory 'innounp-2.71.1'
Expand-Archive -LiteralPath $extractorZip -DestinationPath $extractorDirectory -Force
$extractor = Join-Path $extractorDirectory 'innounp.exe'
if (!(Test-Path -LiteralPath $extractor)) { throw 'The pinned unpacker archive has no innounp.exe.' }

foreach ($arch in $pending) {
    $package = $manifest.architectures.$arch
    $installer = Join-Path $InstallerDirectory $package.installer
    if ($FetchInstallers) {
        New-Item -ItemType Directory -Path $InstallerDirectory -Force | Out-Null
        Get-VerifiedFile $package.url $installer $package.sha256
    }
    if (!(Test-FileHash $installer $package.sha256)) { throw "Installer missing or failed SHA256 verification: $installer" }

    $destination = Join-Path $DestinationDirectory $arch
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $unpacked = Join-Path $CacheDirectory "usbip-$($manifest.version)-$arch"
    if (Test-Path -LiteralPath $unpacked) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
    $patterns = @($package.files.PSObject.Properties | ForEach-Object { '{tmp}\' + $_.Name })
    & $extractor -x -q -y "-d$unpacked" $installer @patterns | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "usbip-win2 driver extraction failed for $arch." }
    foreach ($file in $package.files.PSObject.Properties) {
        $source = Join-Path $unpacked ('{tmp}\' + $file.Name)
        if (!(Test-FileHash $source $file.Value)) { throw "Extracted file failed SHA256 verification: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $file.Name) -Force
    }
    Write-Output "Verified usbip-win2 $($manifest.version) $arch host controller files."
}
