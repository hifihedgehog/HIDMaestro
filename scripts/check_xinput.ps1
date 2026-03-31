# Check HID child hardware IDs and XInput state
$child = Get-PnpDevice | Where-Object { $_.InstanceId -like "HID\HIDCLASS\*" -and $_.Status -eq "OK" }
if ($child) {
    $hwids = (Get-PnpDeviceProperty -InstanceId $child.InstanceId -KeyName DEVPKEY_Device_HardwareIds -EA SilentlyContinue).Data
    Write-Host "HID Child: $($child.FriendlyName)"
    Write-Host "Instance: $($child.InstanceId)"
    foreach ($h in $hwids) { Write-Host "  HWID: $h" }

    $upFilters = (Get-PnpDeviceProperty -InstanceId $child.InstanceId -KeyName DEVPKEY_Device_UpperFilters -EA SilentlyContinue).Data
    if ($upFilters) { foreach ($f in $upFilters) { Write-Host "  UpperFilter: $f" } }
    else { Write-Host "  UpperFilters: (none)" }
}

Write-Host "`n=== XInput State ==="
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class XI {
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);
}
"@

for ($i = 0; $i -lt 4; $i++) {
    $state = New-Object XI+XINPUT_STATE
    $r = [XI]::XInputGetState($i, [ref]$state)
    if ($r -eq 0) {
        Write-Host "  XInput Slot $i : Connected (packet=$($state.dwPacketNumber))"
    } else {
        Write-Host "  XInput Slot $i : Not connected (err=$r)"
    }
}
