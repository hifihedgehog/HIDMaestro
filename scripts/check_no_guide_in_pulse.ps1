# Pre-build check: strict Guide-reference guard under example/ and test/.
#
# Background: the Windows shell fires a Guide-long-press haptic (hi=0x7F short
# burst) to the controller's XInput slot whenever Guide is seen pressed on our
# virtual. A test loop that cycles Guide pollutes the xusbshim_log.txt /
# [HMCOMP] SET_STATE log with phantom haptic writes that mask real Chromium
# playEffect dispatch analysis. The previous (looser) version of this check
# only caught the pulse idiom `HMButton.Guide ... HMButton.None`; this version
# is strict: *any* reference to `HMButton.Guide` under example/ or test/
# requires an explicit allowlist comment on the same line. That catches
# continuous-hold regressions, renamed shims, and other contamination shapes
# that the pulse-only regex missed.
#
# To allow a deliberate Guide reference, add a trailing comment containing the
# token `ALLOW-GUIDE:` followed by a short justification on the same line:
#
#     Buttons = HMButton.A | HMButton.Guide,  // ALLOW-GUIDE: one-shot snapshot frame, not looped
#
# The allowlist comment must be on the SAME line as the HMButton.Guide
# reference. Lines without it fail the check.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$targets = @("$root\example", "$root\test")
$guidePattern = 'HMButton\.Guide'
$allowToken   = 'ALLOW-GUIDE:'
$violations = @()
foreach ($t in $targets) {
    if (-not (Test-Path $t)) { continue }
    Get-ChildItem -Path $t -Recurse -File -Include *.cs,*.fs,*.vb | ForEach-Object {
        $file = $_.FullName
        Select-String -Path $file -Pattern $guidePattern -AllMatches | ForEach-Object {
            $line = $_.Line
            if ($line -notmatch [regex]::Escape($allowToken)) {
                $violations += "$($_.Path):$($_.LineNumber): $($line.Trim())"
            }
        }
    }
}
if ($violations.Count -gt 0) {
    Write-Host "ERROR: HMButton.Guide reference(s) in test code without ALLOW-GUIDE: justification." -ForegroundColor Red
    Write-Host "Windows shell fires a Guide-haptic ack that pollutes xusbshim_log.txt." -ForegroundColor Red
    Write-Host "Either remove the reference, or add a trailing '// ALLOW-GUIDE: <reason>' comment on the same line." -ForegroundColor Red
    Write-Host ""
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 1
}
Write-Host "check_no_guide_in_pulse: OK (no unaccounted HMButton.Guide references in example/ or test/)"
exit 0
