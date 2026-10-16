<#
.SYNOPSIS
  Offline check of the battery's leftover detection (HMDevnodes.ps1).

.DESCRIPTION
  Feeds Select-HMOwnedDevnodes and Get-HMLeftovers a fixed set of
  devnode records and checks what the leftover check reports. A parent
  is a HIDMaestro controller's when it carries one of the SDK's three
  controller hardware IDs, under whatever enumerator it sits: a
  ROOT\HIDClass parent and an IG_00 parent other than 045E:028E must be
  reported with their HID children, and a foreign ROOT\HIDClass device
  (vJoy's shape), a phantom and the usbip-win2 host controller must
  not. No device, no
  elevation, no registry read. Exit 0 when every check passes, 1
  otherwise.
#>
$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $scriptDir 'HMDevnodes.ps1')

function Node([string]$Id, [string[]]$Hw, [bool]$Present, [string[]]$Children = @()) {
    $r = New-Object HMDevnodeRecord
    $r.Id = $Id
    $r.HardwareIds = $Hw
    $r.Present = $Present
    $r.Children = $Children
    return $r
}

# The SDK's own hardware IDs (DeviceNodeCreator.CreateDeviceNode,
# DeviceOrchestrator.CreateGamepadCompanion and CreateXusbCompanion).
$nodes = @(
    (Node 'ROOT\HIDCLASS\HM_0001' @('root\VID_046D&PID_C21D', 'root\HIDMaestro') $true @('HID\HIDCLASS\1&2D595CA7&0&0000')),
    (Node 'ROOT\VID_045E&PID_001B&IG_00\HM_0002' @('root\VID_045E&PID_001B&IG_00', 'root\HIDMaestro') $true @('HID\VID_045E&PID_001B&IG_00\1&1A2B3C4D&0&0000')),
    (Node 'ROOT\VID_045E&PID_028E&IG_00\HM_0003' @('root\VID_045E&PID_028E&IG_00', 'root\HIDMaestro') $true @('HID\VID_045E&PID_028E&IG_00\1&3E4F5061&0&0000')),
    (Node 'SWD\HIDMAESTRO\HM_0003' @('root\VID_045E&PID_028E&XI_00', 'root\HIDMaestroXUSB') $true),
    (Node 'SWD\HIDMAESTRO_VID_045E_PID_02FF&IG_00\HM_0004' @('root\VID_045E&PID_02FF&IG_00', 'root\HIDMaestroGamepad', 'root\HIDMaestro') $true @('HID\HIDMAESTRO_VID_045E_PID_02FF&IG_00\1&72839405&0&0000')),
    # vJoy's root device: same enumerator, no HIDMaestro hardware ID.
    (Node 'ROOT\HIDCLASS\0000' @('root\VID_1234&PID_BEAD&REV_0222') $true @('HID\HIDCLASS\1&2D595CA7&1&0000')),
    # A phantom of ours: registry residue, not a leftover.
    (Node 'ROOT\HIDCLASS\HM_0005' @('root\VID_054C&PID_0CE6', 'root\HIDMaestro') $false),
    # The usbip-win2 host controller: the USB/IP transport, present from
    # its install on (UsbipDriverInstaller.OwnerHardwareId).
    (Node 'ROOT\USB\0000' @('ROOT\USBIP_WIN2\UDE', 'ROOT\HIDMAESTRO_UDE') $true @('USB\ROOT_HUB30\1&2B53A856&1&0'))
)

$fails = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { Write-Host "  [PASS] $Name" } else { Write-Host "  [FAIL] $Name"; $script:fails++ }
}

Write-Host '=== devnode ownership for the leftover check ==='
$present = Select-HMOwnedDevnodes $nodes
$expected = @(
    'ROOT\HIDCLASS\HM_0001', 'HID\HIDCLASS\1&2D595CA7&0&0000',
    'ROOT\VID_045E&PID_001B&IG_00\HM_0002', 'HID\VID_045E&PID_001B&IG_00\1&1A2B3C4D&0&0000',
    'ROOT\VID_045E&PID_028E&IG_00\HM_0003', 'HID\VID_045E&PID_028E&IG_00\1&3E4F5061&0&0000',
    'SWD\HIDMAESTRO\HM_0003',
    'SWD\HIDMAESTRO_VID_045E_PID_02FF&IG_00\HM_0004', 'HID\HIDMAESTRO_VID_045E_PID_02FF&IG_00\1&72839405&0&0000'
)
foreach ($e in $expected) { Check "present set holds $e" ($present -contains $e) }
Check 'the foreign ROOT\HIDCLASS\0000 is not counted' (-not ($present -contains 'ROOT\HIDCLASS\0000'))
Check 'the foreign device''s HID child is not counted' (-not ($present -contains 'HID\HIDCLASS\1&2D595CA7&1&0000'))
Check 'the phantom is not counted' (-not ($present -contains 'ROOT\HIDCLASS\HM_0005'))
Check 'the usbip-win2 host controller is not counted' (-not ($present -contains 'ROOT\USB\0000'))
Check 'the host controller''s root hub is not counted' (-not ($present -contains 'USB\ROOT_HUB30\1&2B53A856&1&0'))
Check "the present set has exactly $($expected.Count) entries" ($present.Count -eq $expected.Count)

# A scenario that starts with nothing and leaves the HIDClass parent and
# the IG_00 parent behind.
$after = Select-HMOwnedDevnodes @($nodes[0], $nodes[1], $nodes[5])
$left = Get-HMLeftovers -Baseline @() -After $after
Check 'the leftover check reports a ROOT\HIDClass parent' ($left -contains 'ROOT\HIDCLASS\HM_0001')
Check 'the leftover check reports its HID child' ($left -contains 'HID\HIDCLASS\1&2D595CA7&0&0000')
Check 'the leftover check reports an IG_00 parent other than 045E:028E' ($left -contains 'ROOT\VID_045E&PID_001B&IG_00\HM_0002')
Check 'the leftover check does not report a foreign device' (-not ($left -contains 'ROOT\HIDCLASS\0000'))
$left2 = Get-HMLeftovers -Baseline $after -After $after
Check 'nothing is a leftover when the scenario changed nothing' ($left2.Count -eq 0)

Write-Host ''
if ($fails -eq 0) { Write-Host '=== PASS ==='; exit 0 }
Write-Host "=== FAIL: $fails check(s) ==="
exit 1
