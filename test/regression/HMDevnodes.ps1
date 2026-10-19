# Which devnodes are HIDMaestro's, for the battery's leftover check
# (regression_battery.ps1, Test-Scenario) and its own offline check
# (devnode_owner_check.ps1). Dot-sourced by both.
#
# Ownership is proved by hardware ID, read from the instance key, as
# DeviceManager.IsHidMaestroOwned proves it, with one difference: the
# three IDs the SDK gives a controller's devnodes are matched whole
# (root\HIDMaestro, root\HIDMaestroGamepad, root\HIDMaestroXUSB), where
# IsHidMaestroOwned takes any ID that holds "HIDMaestro". That leaves out
# the usbip-win2 host controllers, ROOT\USB\<n> carrying
# ROOT\HIDMAESTRO_UDE (UsbipDriverInstaller.OwnerHardwareId), which are
# the USB/IP transport, present from its install on, and no controller a
# scenario creates. Every controller devnode the SDK creates carries one
# of the three, under any enumerator: ROOT\HIDClass, ROOT\VID_xxxx&PID_yyyy&IG_00, SWD\HIDMAESTRO and
# SWD\HIDMAESTRO_VID_xxxx_PID_yyyy&IG_00. A parent's children (its HID
# child) are HIDMaestro's with it. Matching enumerator names instead saw
# only HIDMAESTRO*, HMCOMPANION, 045E:028E and BEEF:F000, so a leftover
# under ROOT\HIDClass or another IG_00 enumerator went unreported, and a
# foreign device under one of those names would have been counted.

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

public sealed class HMDevnodeRecord {
    public string Id;
    public string[] HardwareIds;
    public bool Present;
    public string[] Children;
}

public static class HMDevnodeReader {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint dnDevInst, StringBuilder buffer, int bufferLen, uint ulFlags);

    // Every instance key under ROOT and SWD, with its hardware IDs, whether
    // it is in the device tree, and the instance IDs of its direct
    // children when it is.
    public static List<HMDevnodeRecord> Read() {
        var list = new List<HMDevnodeRecord>();
        foreach (string root in new[] { "ROOT", "SWD" }) {
            using (RegistryKey rootKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + root)) {
                if (rootKey == null) continue;
                foreach (string enumerator in rootKey.GetSubKeyNames()) {
                    string[] names;
                    try {
                        using (RegistryKey ek = rootKey.OpenSubKey(enumerator)) {
                            names = ek == null ? new string[0] : ek.GetSubKeyNames();
                        }
                    } catch { continue; }
                    foreach (string name in names) {
                        string id = root + @"\" + enumerator + @"\" + name;
                        string[] hw = null;
                        try {
                            using (RegistryKey ik = rootKey.OpenSubKey(enumerator + @"\" + name)) {
                                if (ik != null) hw = ik.GetValue("HardwareID") as string[];
                            }
                        } catch { }
                        uint devInst;
                        bool present = CM_Locate_DevNodeW(out devInst, id, 0) == 0;
                        list.Add(new HMDevnodeRecord {
                            Id = id,
                            HardwareIds = hw ?? new string[0],
                            Present = present,
                            Children = present ? ChildIds(devInst) : new string[0],
                        });
                    }
                }
            }
        }
        return list;
    }

    static string[] ChildIds(uint devInst) {
        var ids = new List<string>();
        uint child;
        if (CM_Get_Child(out child, devInst, 0) != 0) return ids.ToArray();
        while (true) {
            var sb = new StringBuilder(512);
            if (CM_Get_Device_IDW(child, sb, sb.Capacity, 0) == 0) ids.Add(sb.ToString());
            uint next;
            if (CM_Get_Sibling(out next, child, 0) != 0) break;
            child = next;
        }
        return ids.ToArray();
    }
}
"@ -ErrorAction SilentlyContinue

# The hardware IDs the SDK writes on a controller's devnodes
# (DeviceNodeCreator.CreateDeviceNode, DeviceOrchestrator.CreateGamepadCompanion
# and DeviceOrchestrator.CreateXusbCompanion).
$script:HMControllerHardwareIds = @('root\HIDMaestro', 'root\HIDMaestroGamepad', 'root\HIDMaestroXUSB')

# True when one of the hardware IDs is one of those three, compared
# without case, as PnP compares IDs.
function Test-HMOwnedHardwareIds {
    param([string[]]$HardwareIds)
    foreach ($id in $HardwareIds) {
        foreach ($own in $script:HMControllerHardwareIds) {
            if ([string]::Equals($id, $own, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        }
    }
    return $false
}

# The present HIDMaestro devnodes among $Nodes (records with Id,
# HardwareIds, Present and Children): each present parent that carries a
# HIDMaestro hardware ID, and its children.
function Select-HMOwnedDevnodes {
    param([object[]]$Nodes)
    $owned = New-Object System.Collections.Generic.List[string]
    foreach ($n in $Nodes) {
        if (-not $n.Present) { continue }
        if (-not (Test-HMOwnedHardwareIds $n.HardwareIds)) { continue }
        $owned.Add($n.Id)
        foreach ($c in $n.Children) { $owned.Add($c) }
    }
    return ,($owned.ToArray())
}

# Every HIDMaestro devnode on the machine in the PRESENT state. PHANTOM
# records are registry residue from prior sessions, harmless to
# consumers, and ignored here.
function Get-HMPresentDevnodes {
    return Select-HMOwnedDevnodes ([HMDevnodeReader]::Read().ToArray())
}

# The devnodes present after a scenario that were not present before it:
# what the scenario created and did not clean up.
function Get-HMLeftovers {
    param([string[]]$Baseline, [string[]]$After)
    return ,@($After | Where-Object { $Baseline -notcontains $_ })
}
