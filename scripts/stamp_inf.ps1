# Read a source INF, update its DriverVer line with today's date and a
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
# Tradeoff (accepted): deployed INFs report `x.y.z.HHmm` while managed
# assemblies report `x.y.z.0`. Only the 4th part differs, and release
# builds cut from pre-tag-validate all stamp within one minute.
#
# The committed INF sources keep a stable `x.y.z.0` for review; only the
# build/ copies are stamped.

param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Dest
)

if (!(Test-Path -LiteralPath $Source)) {
    Write-Error "stamp_inf: source INF not found: $Source"
    exit 1
}

$content = Get-Content -Raw -LiteralPath $Source
$now   = Get-Date
$date  = $now.ToString('MM/dd/yyyy')
$build = [int]$now.ToString('HHmm')   # int-cast drops a leading zero (0930 -> 930)

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
