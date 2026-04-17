# Read a source INF, replace its DriverVer line with an auto-stamped
# (current date + time-of-day-derived build number), and write the result
# to the destination path.
#
# The 4th component of DriverVer is a 16-bit unsigned integer (max 65535).
# We use HHmm (0000..2359) which is monotonic within a day and well below
# the 16-bit cap. The MM/dd/yyyy date component is what carries monotonicity
# across day boundaries — pnputil compares that first, so 04/18/2026,1.1.6.0015
# is still considered newer than 04/17/2026,1.1.6.2359.
#
# The committed source INF keeps a stable major.minor.patch.0 for code review;
# the build/*.inf that actually gets signed + installed gets the fresh stamp.
# This makes every produced package uniquely versioned, eliminating the
# "pnputil /add-driver sees same DriverVer and silently keeps stale bytes
# in the existing DriverStore directory" failure mode.

param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Dest
)

if (!(Test-Path -LiteralPath $Source)) {
    Write-Error "stamp_inf: source INF not found: $Source"
    exit 1
}

$content = Get-Content -Raw -LiteralPath $Source
$date  = (Get-Date).ToString('MM/dd/yyyy')
$build = (Get-Date).ToString('HHmm')

# Match: DriverVer [ws] = [ws] MM/dd/yyyy,N.N.N.N  (any 4-part version)
# Keep the major.minor.patch from source, replace date + 4th component.
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
