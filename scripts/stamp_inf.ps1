# Read a source INF, update its DriverVer line with the build's date and a
# build-unique 4th version component, and write the result to the
# destination path.
#
# The 4th component is stamped HHmm (build minute) so every build carries
# a strictly increasing DriverVer within a day. This guard was removed
# once, on the theory that the install flow's package purge made same-
# version reinstalls safe; that purge itself was later removed (a
# /delete-driver /uninstall /force on an active driver leaves devices in
# Code 14, per the project's install rules), and the 2026-07-20 audit
# session then demonstrated the consequence: three same-day driver
# rebuilds at an identical DriverVer kept binding stale DriverStore
# bytes through forced reinstalls, defeating driver iteration and
# mutation testing. A unique 4th component makes pnputil treat every
# build as an upgrade, deterministically.
#
# Every INF of one build carries one stamp. build_all.cmd takes the time
# once, as HM_STAMP_TIME, for both architectures, and build.cmd and
# build_companion.cmd each take it at their start when run alone. A
# companion build run alone stamps both INFs of its architecture, so its
# package is newer than the one installed and deploys. Until v2.0.0 each
# script read the clock as it stamped, and a build whose stamps fell in
# two minutes carried two DriverVers.
#
# The time is refused when it is later than the clock or earlier than the
# newest file under the source INF's directory, each to the minute. A
# stale HM_STAMP_TIME left in the environment would otherwise stamp a new
# build older than an earlier one, and the deploy policy, which never
# downgrades, would keep the earlier one. A future one would outrank every
# later build until its date.
#
# Tradeoff (accepted): deployed INFs report `x.y.z.HHmm` while managed
# assemblies report `x.y.z.0`. Only the 4th part differs.
#
# The committed INF sources keep a stable `x.y.z.0` for review; only the
# build/ copies are stamped.

param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Dest,
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    # The build's time, yyyy-MM-ddTHH:mm. Read from the clock when omitted.
    [string]$Time
)

if (!(Test-Path -LiteralPath $Source)) {
    Write-Error "stamp_inf: source INF not found: $Source"
    exit 1
}

$content = Get-Content -Raw -LiteralPath $Source -Encoding UTF8

# Decorate the model sections for the target. The committed sources name
# a real architecture so they stay reviewable and installable as-is, so
# both forms are handled: the NT$ARCH$ placeholder and the literal
# NTamd64 the x64 sources carry.
$infArchitecture = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'amd64' }
$content = $content.Replace('NT$ARCH$', "NT$infArchitecture")
if ($Architecture -eq 'arm64') {
    $content = [regex]::Replace($content, 'NTamd64', 'NTARM64', 'IgnoreCase')
}
if ($content -notmatch "NT$infArchitecture") {
    Write-Error "stamp_inf: $Source carries no NT$infArchitecture model section after stamping"
    exit 1
}
$invariant = [Globalization.CultureInfo]::InvariantCulture
function Get-Minute([datetime]$t) { New-Object DateTime $t.Year, $t.Month, $t.Day, $t.Hour, $t.Minute, 0 }
if ($Time) {
    $now = [datetime]::MinValue
    if (![datetime]::TryParseExact($Time, 'yyyy-MM-ddTHH:mm', $invariant, 'None', [ref]$now)) {
        Write-Error "stamp_inf: -Time '$Time' is not yyyy-MM-ddTHH:mm"
        exit 1
    }
} else {
    $now = Get-Minute (Get-Date)
}
$clock = Get-Minute (Get-Date)
if ($now -gt $clock) {
    Write-Error ("stamp_inf: the stamp time {0:yyyy-MM-ddTHH:mm} is later than the clock, {1:yyyy-MM-ddTHH:mm}. Clear HM_STAMP_TIME." -f $now, $clock)
    exit 1
}
$newest = Get-ChildItem -LiteralPath (Split-Path -Parent $Source) -Recurse -File |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest -and $now -lt (Get-Minute $newest.LastWriteTime)) {
    Write-Error ("stamp_inf: the stamp time {0:yyyy-MM-ddTHH:mm} is earlier than {1}, written {2:yyyy-MM-ddTHH:mm}. Clear HM_STAMP_TIME." -f $now, $newest.FullName, $newest.LastWriteTime)
    exit 1
}
$date  = $now.ToString('MM/dd/yyyy', $invariant)
$build = [int]$now.ToString('HHmm', $invariant)   # int-cast drops a leading zero (0930 -> 930)

# Match: DriverVer [ws] = [ws] MM/dd/yyyy,N.N.N.N  (any 4-part version)
# Refresh the date; keep the source's first three parts; stamp the 4th.
$pattern = '(?m)^(DriverVer\s*=\s*)\d{2}/\d{2}/\d{4}\s*,\s*(\d+\.\d+\.\d+)\.\d+\s*$'
$replaced = [regex]::Replace($content, $pattern, { param($m)
    $m.Groups[1].Value + $date + ',' + $m.Groups[2].Value + '.' + $build
})

if ($replaced -eq $content) {
    Write-Warning "stamp_inf: no DriverVer line matched in $Source (INF written unchanged)"
}

# Preserve original byte encoding where possible. Most of our INFs are UTF-8
# with BOM; Set-Content -Encoding UTF8 adds a BOM on Windows PowerShell 5.x
# which matches. Use -NoNewline so we don't append an extra CRLF past the
# source's existing trailer.
Set-Content -LiteralPath $Dest -Value $replaced -Encoding UTF8 -NoNewline
