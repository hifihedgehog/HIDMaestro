# Build HIDMaestro from copies of this tree under each kit layout the build
# supports, and check that each setting in docs/design/v1.13.0.md
# ("Building from source") does what it says. pre-tag-validate.cmd runs this
# after build_all.cmd, so build\ already holds this tree's release build.
#
# The NuGet WDK is Microsoft.Windows.WDK.x64 and Microsoft.Windows.WDK.ARM64
# 10.0.26100.6584, fetched once into build\downloads and pinned by SHA-256.
#
#   N1  native build, NuGet WDK, x64 and ARM64: builds, and each binary is
#       the release build's apart from link timestamps.
#   N2  native build, HM_WDK_ROOT naming a folder without a WDK: fails and
#       names HM_WDK_ROOT.
#   N3  native build in an EWDK prompt's variables (WDKContentRoot,
#       WindowsSdkDir, Version_Number and VSINSTALLDIR, pointed at the
#       installed kits): builds, and reports taking each root from them.
#   N4  native build, WDKContentRoot naming a folder without a WDK: fails
#       and names WDKContentRoot.
#   M1  SDK project, NuGet WDK: builds, and embeds the package's Inf2Cat set.
#   M2  SDK project, HM_WDK_ROOT naming a folder without a WDK: fails and
#       names HMInf2catBin.
#   M3  SDK project, HM_SDK_ROOT naming a folder without an SDK: fails and
#       names HMWdkBin.
#
# Exit 0 when every case passes, 1 otherwise.

param(
    [string]$RepoRoot = '',
    [string]$WorkRoot = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# $PSScriptRoot is empty inside a param default under Windows PowerShell 5.1.
if (!$RepoRoot) { $RepoRoot = Join-Path $PSScriptRoot '..' }
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
if (!$WorkRoot) { $WorkRoot = Join-Path $env:TEMP ('HIDMaestro_layouts_' + $PID) }
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)

$kitVersion = '10.0.26100.0'
$wdkPackageVersion = '10.0.26100.6584'
$wdkPackages = @{
    'x64'   = @{ Id = 'microsoft.windows.wdk.x64';   Sha256 = 'c393d03dfb640b5c92f546b32f6770ef68cd3aaf691956e7d66d8e2c28a1b55e' }
    'arm64' = @{ Id = 'microsoft.windows.wdk.arm64'; Sha256 = 'e705b2a63eab891def8f98087666f93e8f21da8e3b5def81a624b83fef5bdae9' }
}
# Every setting a case controls. Each case starts from none of them, so the
# environment this script runs in cannot leak into a case.
$layoutVariables = 'HM_SDK_ROOT', 'HM_WDK_ROOT', 'HM_WDK_ROOT_ARM64', 'HM_WDK_VERSION', 'HMWdkBin', 'HMInf2catBin',
                   'HMSkipArm64', 'HMSkipUsbip', 'WDKContentRoot', 'WindowsSdkDir', 'Version_Number', 'VSINSTALLDIR'

$script:failures = 0
function Report([string]$case, [bool]$ok, [string]$detail) {
    if (!$ok) { $script:failures++ }
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}{2}" -f $tag, $case, $(if ($detail) { '  ' + $detail } else { '' }))
}

function Test-Sha256([string]$Path, [string]$Expected) {
    (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
}

# The package's folder as nuget install lays it out: <id>.<version>\c\...
function Get-WdkPackage([string]$Arch) {
    $p = $wdkPackages[$Arch]
    $downloads = Join-Path $RepoRoot 'build\downloads'
    New-Item -ItemType Directory -Force $downloads | Out-Null
    $nupkg = Join-Path $downloads ("{0}.{1}.nupkg" -f $p.Id, $wdkPackageVersion)
    $folder = Join-Path $downloads ("{0}.{1}" -f $p.Id, $wdkPackageVersion)
    $marker = Join-Path $folder '.verified'
    if ((Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $p.Sha256)) { return $folder }
    if (!(Test-Sha256 $nupkg $p.Sha256)) {
        if (Test-Path -LiteralPath $nupkg) { throw "Hash mismatch in cached package: $nupkg" }
        $url = "https://api.nuget.org/v3-flatcontainer/{0}/{1}/{0}.{1}.nupkg" -f $p.Id, $wdkPackageVersion
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $nupkg
        if (!(Test-Sha256 $nupkg $p.Sha256)) { Remove-Item -LiteralPath $nupkg; throw "Downloaded package failed SHA256 verification: $url" }
    }
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $folder)
    Set-Content -LiteralPath $marker -Value $p.Sha256 -Encoding ascii
    return $folder
}

function Copy-Tree([string]$From, [string]$To, [string[]]$ExcludeDirs = @()) {
    $roboArgs = @($From, $To, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
    if ($ExcludeDirs.Count -gt 0) { $roboArgs += '/XD'; $roboArgs += $ExcludeDirs }
    & robocopy.exe @roboArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $From -> $To failed with $LASTEXITCODE" }
}

# Run a command line under cmd.exe with exactly the given layout settings.
# Returns the exit code and the combined output.
function Invoke-Layout([hashtable]$Settings, [string]$CommandLine, [string]$WorkingDirectory) {
    $saved = @{}
    foreach ($v in $layoutVariables) {
        $saved[$v] = [Environment]::GetEnvironmentVariable($v, 'Process')
        [Environment]::SetEnvironmentVariable($v, $null, 'Process')
    }
    foreach ($k in $Settings.Keys) { [Environment]::SetEnvironmentVariable($k, $Settings[$k], 'Process') }
    try {
        $log = Join-Path $WorkRoot ('cmd_' + [guid]::NewGuid().ToString('N') + '.log')
        # Wait for cmd.exe alone. Start-Process -Wait also waits for every
        # process the build leaves behind, such as the compiler's telemetry
        # helper and MSBuild's reusable nodes, which outlive it by minutes.
        $psi = New-Object Diagnostics.ProcessStartInfo 'cmd.exe', ('/d /c "' + $CommandLine + ' > "' + $log + '" 2>&1"')
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = $WorkingDirectory
        $p = [Diagnostics.Process]::Start($psi)
        $p.WaitForExit()
        $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
        return @{ ExitCode = $p.ExitCode; Output = "$text" }
    } finally {
        foreach ($v in $layoutVariables) { [Environment]::SetEnvironmentVariable($v, $saved[$v], 'Process') }
    }
}

# A PE image with every link-time stamp zeroed: the COFF header's time stamp
# and checksum, and the time stamps of the export directory, the root
# resource directory and each debug directory entry, with a CodeView
# entry's GUID and age.
function Get-NormalizedImage([string]$Path) {
    $b = [IO.File]::ReadAllBytes($Path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $sections = [BitConverter]::ToUInt16($b, $pe + 6)
    $optSize = [BitConverter]::ToUInt16($b, $pe + 20)
    $opt = $pe + 24
    $magic = [BitConverter]::ToUInt16($b, $opt)
    $dirs = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
    $secTable = $opt + $optSize
    function RvaToOffset([uint32]$rva) {
        for ($i = 0; $i -lt $sections; $i++) {
            $s = $secTable + $i * 40
            $va = [BitConverter]::ToUInt32($b, $s + 12); $size = [BitConverter]::ToUInt32($b, $s + 8)
            $raw = [BitConverter]::ToUInt32($b, $s + 20)
            if ($rva -ge $va -and $rva -lt $va + [Math]::Max($size, 1)) { return [int]($rva - $va + $raw) }
        }
        return -1
    }
    function Zero([int]$at, [int]$n) { if ($at -ge 0) { for ($i = 0; $i -lt $n; $i++) { $b[$at + $i] = 0 } } }
    Zero ($pe + 8) 4
    Zero ($opt + 64) 4
    $exportRva = [BitConverter]::ToUInt32($b, $dirs)
    if ($exportRva) { Zero ((RvaToOffset $exportRva) + 4) 4 }
    $resourceRva = [BitConverter]::ToUInt32($b, $dirs + 16)
    if ($resourceRva) { Zero ((RvaToOffset $resourceRva) + 4) 4 }
    $debugRva = [BitConverter]::ToUInt32($b, $dirs + 48); $debugSize = [BitConverter]::ToUInt32($b, $dirs + 52)
    if ($debugRva) {
        $d = RvaToOffset $debugRva
        for ($e = 0; $e -lt [int]($debugSize / 28); $e++) {
            $entry = $d + $e * 28
            Zero ($entry + 4) 4
            $type = [BitConverter]::ToUInt32($b, $entry + 12)
            $dataPtr = [BitConverter]::ToInt32($b, $entry + 24)
            if ($type -eq 2 -and $dataPtr -gt 0) { Zero ($dataPtr + 4) 20 }
        }
    }
    return ,$b
}

function Compare-Image([string]$A, [string]$B) {
    $x = Get-NormalizedImage $A; $y = Get-NormalizedImage $B
    if ($x.Length -ne $y.Length) { return "sizes $($x.Length) and $($y.Length)" }
    $diff = @(); for ($i = 0; $i -lt $x.Length -and $diff.Count -lt 8; $i++) { if ($x[$i] -ne $y[$i]) { $diff += ('0x{0:X}' -f $i) } }
    if ($diff.Count) { return 'bytes differ at ' + ($diff -join ', ') }
    return ''
}

# ── Setup ───────────────────────────────────────────────────────────────

if (!(Test-Path -LiteralPath (Join-Path $RepoRoot 'build\HIDMaestro.dll'))) { throw "No release build in $RepoRoot\build. Run scripts\build_all.cmd first." }
if (Test-Path -LiteralPath $WorkRoot) { Remove-Item -LiteralPath $WorkRoot -Recurse -Force }
New-Item -ItemType Directory -Force $WorkRoot | Out-Null
$emptyRoot = Join-Path $WorkRoot 'no-kit'
New-Item -ItemType Directory -Force $emptyRoot | Out-Null

$installedKit = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if (!$vsRoot) { throw 'No Visual Studio with the C++ tools found through vswhere.' }
$vsRoot = $vsRoot.TrimEnd('\') + '\'

Write-Host 'Fetching the pinned NuGet WDK packages...'
$nugetX64 = Join-Path (Get-WdkPackage 'x64') 'c'
$nugetArm64 = Join-Path (Get-WdkPackage 'arm64') 'c'

$native = Join-Path $WorkRoot 'native'
foreach ($d in 'scripts', 'driver', 'include') {
    if (Test-Path -LiteralPath (Join-Path $RepoRoot $d)) { Copy-Tree (Join-Path $RepoRoot $d) (Join-Path $native $d) @('__pycache__') }
}
Copy-Item -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Destination $native

$sdk = Join-Path $WorkRoot 'sdk'
Copy-Tree (Join-Path $RepoRoot 'sdk\HIDMaestro.Core') (Join-Path $sdk 'sdk\HIDMaestro.Core') @('bin', 'obj')
Copy-Tree (Join-Path $RepoRoot 'profiles') (Join-Path $sdk 'profiles')
Copy-Tree (Join-Path $RepoRoot 'scripts') (Join-Path $sdk 'scripts') @('__pycache__')
Copy-Tree (Join-Path $RepoRoot 'build') (Join-Path $sdk 'build') @('downloads')
Copy-Item -LiteralPath (Join-Path $RepoRoot 'Directory.Build.props') -Destination $sdk
# The staged Inf2Cat set comes out of the cache, so each M case copies it
# from whichever folder its settings name.
$sdkResources = Join-Path $sdk 'sdk\HIDMaestro.Core\Resources'
$inf2catSet = 'Inf2Cat.exe', 'WindowsProtectedFiles.xml',
              'Microsoft.UniversalStore.HardwareWorkflow.Cabinets.dll', 'Microsoft.UniversalStore.HardwareWorkflow.Catalogs.dll',
              'Microsoft.UniversalStore.HardwareWorkflow.InfReader.dll', 'Microsoft.UniversalStore.HardwareWorkflow.SubmissionBuilder.dll'
function Clear-StagedInf2Cat { foreach ($f in $inf2catSet + 'inf2cat.exe.manifest', 'Microsoft.Kits.Logger.dll') { Remove-Item -LiteralPath (Join-Path $sdkResources $f) -ErrorAction SilentlyContinue } }

Write-Host "Layouts under $WorkRoot"

# ── N1: native, NuGet WDK, both architectures ──────────────────────────

$n1 = @{ HM_WDK_ROOT = $nugetX64; HM_WDK_ROOT_ARM64 = $nugetArm64 }
$n1ok = $true; $n1detail = @()
foreach ($step in 'build.cmd x64', 'build_companion.cmd x64', 'build.cmd arm64', 'build_companion.cmd arm64') {
    $r = Invoke-Layout $n1 ('call "' + (Join-Path $native 'scripts') + '\' + $step.Replace(' ', '" ')) $native
    if ($r.ExitCode -ne 0) { $n1ok = $false; $n1detail += "$step exited $($r.ExitCode)"; continue }
    $arch = $step.Split(' ')[1]
    $want = if ($arch -eq 'arm64') { $nugetArm64 } else { $nugetX64 }
    if ($r.Output -notmatch [regex]::Escape('WDK  ' + $want)) { $n1ok = $false; $n1detail += "$step did not report the NuGet WDK root" }
}
if ($n1ok) {
    foreach ($rel in 'HIDMaestro.dll', 'HMXInput.dll', 'hmswd.exe', 'arm64\HIDMaestro.dll', 'arm64\HMXInput.dll', 'arm64\hmswd.exe') {
        $d = Compare-Image (Join-Path $RepoRoot "build\$rel") (Join-Path $native "build\$rel")
        if ($d) { $n1ok = $false; $n1detail += "$rel $d" }
    }
}
Report 'N1 native build with the NuGet WDK matches the release build' $n1ok ($n1detail -join '; ')

# ── N2: native, HM_WDK_ROOT without a WDK ───────────────────────────────

$r = Invoke-Layout @{ HM_WDK_ROOT = $emptyRoot } ('call "' + $native + '\scripts\build.cmd" x64') $native
Report 'N2 native build with HM_WDK_ROOT naming no WDK fails and names HM_WDK_ROOT' `
    ($r.ExitCode -ne 0 -and $r.Output -match 'HM_WDK_ROOT') "exit $($r.ExitCode)"

# ── N3: native, an EWDK prompt's variables ──────────────────────────────

$ewdk = @{ WDKContentRoot = $installedKit; WindowsSdkDir = $installedKit; Version_Number = $kitVersion; VSINSTALLDIR = $vsRoot }
$r = Invoke-Layout $ewdk ('call "' + $native + '\scripts\build.cmd" x64') $native
$tags = @('(WindowsSdkDir)', '(WDKContentRoot)', '(Version_Number)', '(VSINSTALLDIR)') | Where-Object { $r.Output -notmatch [regex]::Escape($_) }
Report 'N3 native build in an EWDK prompt''s variables takes every root from them' `
    ($r.ExitCode -eq 0 -and @($tags).Count -eq 0) ("exit $($r.ExitCode)" + $(if (@($tags).Count) { ', not reported: ' + ($tags -join ' ') } else { '' }))

# ── N4: native, WDKContentRoot without a WDK ────────────────────────────

$r = Invoke-Layout @{ WDKContentRoot = $emptyRoot } ('call "' + $native + '\scripts\build.cmd" x64') $native
Report 'N4 native build with WDKContentRoot naming no WDK fails and names WDKContentRoot' `
    ($r.ExitCode -ne 0 -and $r.Output -match 'WDKContentRoot') "exit $($r.ExitCode)"

# ── M1 to M3: the SDK project ───────────────────────────────────────────

$csproj = Join-Path $sdk 'sdk\HIDMaestro.Core\HIDMaestro.Core.csproj'
$dotnetBuild = 'dotnet build "' + $csproj + '" -c Release --nologo -v:minimal -nodeReuse:false'

Clear-StagedInf2Cat
$m1 = @{ HM_WDK_ROOT = $nugetX64 }
$r1 = Invoke-Layout $m1 $dotnetBuild $sdk
$r2 = if ($r1.ExitCode -eq 0) { Invoke-Layout $m1 $dotnetBuild $sdk } else { $r1 }
$m1detail = @("exit $($r2.ExitCode)")
$m1ok = $r2.ExitCode -eq 0
if ($m1ok) {
    $built = Join-Path $sdk 'sdk\HIDMaestro.Core\bin\Release\net10.0-windows10.0.26100.0\HIDMaestro.Core.dll'
    $asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($built))
    foreach ($f in $inf2catSet) {
        $s = $asm.GetManifestResourceStream('HIDMaestro.Resources.' + $f)
        if (!$s) { $m1ok = $false; $m1detail += "$f not embedded"; continue }
        $sha = [Security.Cryptography.SHA256]::Create()
        $embedded = [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-', '')
        $s.Dispose()
        $package = (Get-FileHash -LiteralPath (Join-Path $nugetX64 "bin\$kitVersion\x86\$f") -Algorithm SHA256).Hash
        if ($embedded -ne $package) { $m1ok = $false; $m1detail += "$f differs from the package's" }
    }
    $extra = @($asm.GetManifestResourceNames() | Where-Object { $_ -match 'Kits\.Logger|inf2cat\.exe\.manifest' })
    if ($extra.Count) { $m1ok = $false; $m1detail += 'also embedded: ' + ($extra -join ', ') }
}
Report 'M1 SDK build with the NuGet WDK embeds the package''s Inf2Cat set' $m1ok ($m1detail -join '; ')

Clear-StagedInf2Cat
$r = Invoke-Layout @{ HM_WDK_ROOT = $emptyRoot } $dotnetBuild $sdk
Report 'M2 SDK build with HM_WDK_ROOT naming no WDK fails and names HMInf2catBin' `
    ($r.ExitCode -ne 0 -and $r.Output -match 'HMInf2catBin') "exit $($r.ExitCode)"

$r = Invoke-Layout @{ HM_SDK_ROOT = $emptyRoot } $dotnetBuild $sdk
Report 'M3 SDK build with HM_SDK_ROOT naming no SDK fails and names HMWdkBin' `
    ($r.ExitCode -ne 0 -and $r.Output -match 'HMWdkBin') "exit $($r.ExitCode)"

Write-Host ''
if ($script:failures -eq 0) {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Every build layout passed.'
    exit 0
}
Write-Host "$($script:failures) build layout case(s) failed. Logs and trees stay in $WorkRoot."
exit 1
