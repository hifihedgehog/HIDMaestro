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
  (vJoy's shape) and a phantom must not. A USB/IP controller is every
  devnode below the root hub of the host controller that carries
  ROOT\HIDMAESTRO_UDE: the USB device, its interfaces, their HID
  collections and an audio endpoint must be reported, and the host
  controller, its root hub, another program's usbip-win2 host controller
  and the device below it must not. A persona left behind must be a
  leftover. No device, no elevation, no registry read. Exit 0 when every
  check passes, 1 otherwise.
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
    # its install on (UsbipDriverInstaller.OwnerHardwareId), and its root
    # hub.
    (Node 'ROOT\USB\0000' @('ROOT\USBIP_WIN2\UDE', 'ROOT\HIDMAESTRO_UDE') $true @('USB\ROOT_HUB30\1&2B53A856&1&0')),
    (Node 'USB\ROOT_HUB30\1&2B53A856&1&0' @('USB\ROOT_HUB30&VID0000&PID0000&REV0000', 'USB\ROOT_HUB30') $true @('USB\VID_054C&PID_0CE6\HM0001')),
    # A composite persona on that hub (dualsense-composite's shape): the
    # USB device, its audio and HID interfaces, the HID collection and an
    # audio endpoint, which Windows lists under SWD.
    (Node 'USB\VID_054C&PID_0CE6\HM0001' @('USB\VID_054C&PID_0CE6&REV_0100', 'USB\VID_054C&PID_0CE6') $true @('USB\VID_054C&PID_0CE6&MI_00\7&1F2E3D4C&0&0000', 'USB\VID_054C&PID_0CE6&MI_03\7&1F2E3D4C&0&0003')),
    (Node 'USB\VID_054C&PID_0CE6&MI_00\7&1F2E3D4C&0&0000' @('USB\VID_054C&PID_0CE6&REV_0100&MI_00') $true @('SWD\MMDEVAPI\{0.0.0.00000000}.{8A3C1E2B-0000-4000-8000-000000000001}')),
    (Node 'SWD\MMDEVAPI\{0.0.0.00000000}.{8A3C1E2B-0000-4000-8000-000000000001}' @('MMDEVAPI\AudioEndpoints') $true),
    (Node 'USB\VID_054C&PID_0CE6&MI_03\7&1F2E3D4C&0&0003' @('USB\VID_054C&PID_0CE6&REV_0100&MI_03') $true @('HID\VID_054C&PID_0CE6&MI_03\8&2A3B4C5D&0&0000')),
    (Node 'HID\VID_054C&PID_0CE6&MI_03\8&2A3B4C5D&0&0000' @('HID\VID_054C&PID_0CE6&REV_0100&MI_03') $true),
    # Another program's usbip-win2 host controller, without HIDMaestro's
    # stamp, its hub and a device on it.
    (Node 'ROOT\USB\0001' @('ROOT\USBIP_WIN2\UDE') $true @('USB\ROOT_HUB30\1&3C64B967&1&0')),
    (Node 'USB\ROOT_HUB30\1&3C64B967&1&0' @('USB\ROOT_HUB30') $true @('USB\VID_046D&PID_C21D\FOREIGN1')),
    (Node 'USB\VID_046D&PID_C21D\FOREIGN1' @('USB\VID_046D&PID_C21D&REV_0305') $true @('HID\VID_046D&PID_C21D\8&11223344&0&0000')),
    (Node 'HID\VID_046D&PID_C21D\8&11223344&0&0000' @('HID\VID_046D&PID_C21D&REV_0305') $true)
)

# The persona's devnodes, in the order the leftover check reads them.
$persona = @(
    'USB\VID_054C&PID_0CE6\HM0001',
    'USB\VID_054C&PID_0CE6&MI_00\7&1F2E3D4C&0&0000',
    'USB\VID_054C&PID_0CE6&MI_03\7&1F2E3D4C&0&0003',
    'SWD\MMDEVAPI\{0.0.0.00000000}.{8A3C1E2B-0000-4000-8000-000000000001}',
    'HID\VID_054C&PID_0CE6&MI_03\8&2A3B4C5D&0&0000'
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
$expected += $persona
foreach ($e in $expected) { Check "present set holds $e" ($present -contains $e) }
Check 'the foreign ROOT\HIDCLASS\0000 is not counted' (-not ($present -contains 'ROOT\HIDCLASS\0000'))
Check 'the foreign device''s HID child is not counted' (-not ($present -contains 'HID\HIDCLASS\1&2D595CA7&1&0000'))
Check 'the phantom is not counted' (-not ($present -contains 'ROOT\HIDCLASS\HM_0005'))
Check 'the usbip-win2 host controller is not counted' (-not ($present -contains 'ROOT\USB\0000'))
Check 'the host controller''s root hub is not counted' (-not ($present -contains 'USB\ROOT_HUB30\1&2B53A856&1&0'))
Check 'another program''s usbip-win2 host controller is not counted' (-not ($present -contains 'ROOT\USB\0001'))
Check 'its root hub is not counted' (-not ($present -contains 'USB\ROOT_HUB30\1&3C64B967&1&0'))
Check 'the device on its hub is not counted' (-not ($present -contains 'USB\VID_046D&PID_C21D\FOREIGN1'))
Check 'that device''s HID child is not counted' (-not ($present -contains 'HID\VID_046D&PID_C21D\8&11223344&0&0000'))
Check "the present set has exactly $($expected.Count) entries" ($present.Count -eq $expected.Count)

# The same tree with the host controller not present: a phantom record
# of the transport, whose old tree no reader would list.
$hcGone = @($nodes | ForEach-Object {
    if ($_.Id -eq 'ROOT\USB\0000') { Node $_.Id $_.HardwareIds $false } else { $_ }
})
$noHc = Select-HMOwnedDevnodes $hcGone
Check 'nothing below a host controller that is not present is counted' (-not ($noHc -contains 'USB\VID_054C&PID_0CE6\HM0001'))
# The stamp taken off (another vendor's install took the controller):
# nothing below it is HIDMaestro's.
$unstamped = @($nodes | ForEach-Object {
    if ($_.Id -eq 'ROOT\USB\0000') { Node $_.Id @('ROOT\USBIP_WIN2\UDE') $true $_.Children } else { $_ }
})
$noStamp = Select-HMOwnedDevnodes $unstamped
Check 'nothing below a host controller without ROOT\HIDMAESTRO_UDE is counted' (-not ($noStamp -contains 'USB\VID_054C&PID_0CE6\HM0001'))

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

# A scenario that starts with the transport installed and no persona, and
# leaves a persona plugged in.
$hcOnly = @(
    (Node 'ROOT\USB\0000' @('ROOT\USBIP_WIN2\UDE', 'ROOT\HIDMAESTRO_UDE') $true @('USB\ROOT_HUB30\1&2B53A856&1&0')),
    (Node 'USB\ROOT_HUB30\1&2B53A856&1&0' @('USB\ROOT_HUB30') $true)
)
$before = Select-HMOwnedDevnodes $hcOnly
Check 'the transport alone is no HIDMaestro devnode' ($before.Count -eq 0)
$leftPersona = Get-HMLeftovers -Baseline $before -After $present
foreach ($p in $persona) { Check "the leftover check reports the persona's $p" ($leftPersona -contains $p) }
Check 'the leftover check does not report the transport' (-not ($leftPersona -contains 'ROOT\USB\0000') -and -not ($leftPersona -contains 'USB\ROOT_HUB30\1&2B53A856&1&0'))

Write-Host ''
if ($fails -eq 0) { Write-Host '=== PASS ==='; exit 0 }
Write-Host "=== FAIL: $fails check(s) ==="
exit 1
