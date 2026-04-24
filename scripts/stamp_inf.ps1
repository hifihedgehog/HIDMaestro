# Read a source INF, update its DriverVer line with today's date while
# preserving the full 4-part version from source, and write the result to
# the destination path.
#
# Previous behavior auto-stamped the 4th component with HHmm so every build
# produced a unique DriverVer. That guarded against `pnputil /add-driver`
# seeing the same DriverVer twice and silently keeping stale DriverStore
# bytes. The stale-install risk is now covered elsewhere: `PnputilHelper`
# calls `/delete-driver /uninstall /force` during cleanup, which purges the
# old package before reinstall regardless of DriverVer equality. Auto-
# stamping was causing the deployed INFs' 4-part version to drift from the
# managed assembly FileVersion (always `<Version>.0`), so every release
# had native and managed sides reporting different version strings.
#
# Now all artifacts in a given release share the exact 4-part version
# string from source (e.g. `1.1.19.0`). pnputil still accepts reinstall
# because the cleanup flow uninstalls the prior package first. Date is
# still refreshed on each build so INFs rebuilt on different days carry
# a current date.

param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Dest
)

if (!(Test-Path -LiteralPath $Source)) {
    Write-Error "stamp_inf: source INF not found: $Source"
    exit 1
}

$content = Get-Content -Raw -LiteralPath $Source
$date    = (Get-Date).ToString('MM/dd/yyyy')

# Match: DriverVer [ws] = [ws] MM/dd/yyyy,N.N.N.N  (any 4-part version)
# Refresh the date; preserve the full 4-part version byte-for-byte from source.
$pattern = '(?m)^(DriverVer\s*=\s*)\d{2}/\d{2}/\d{4}\s*,\s*(\d+\.\d+\.\d+\.\d+)\s*$'
$replaced = [regex]::Replace($content, $pattern, { param($m)
    $m.Groups[1].Value + $date + ',' + $m.Groups[2].Value
})

if ($replaced -eq $content) {
    Write-Warning "stamp_inf: no DriverVer line matched in $Source (INF written unchanged)"
}

# Preserve original byte encoding where possible. Most of our INFs are UTF-8
# with BOM; Set-Content -Encoding UTF8 adds a BOM on Windows PowerShell 5.x
# which matches. Use -NoNewline so we don't append an extra CRLF past the
# source's existing trailer.
Set-Content -LiteralPath $Dest -Value $replaced -Encoding UTF8 -NoNewline
