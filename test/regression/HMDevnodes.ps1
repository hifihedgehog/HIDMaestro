# Which devnodes are HIDMaestro's, for the battery's leftover check
# (regression_battery.ps1, Test-Scenario), its create and settle waits,
# and its own offline check (devnode_owner_check.ps1). Dot-sourced by
# all of them.
#
# A controller is one of two shapes, and each has its own proof.
#
# On HIDMaestro's UMDF2 driver, ownership is proved by hardware ID, read
# from the instance key, as DeviceManager.IsHidMaestroOwned proves it,
# with one difference: the three IDs the SDK gives a controller's
# devnodes are matched whole (root\HIDMaestro, root\HIDMaestroGamepad,
# root\HIDMaestroXUSB), where IsHidMaestroOwned takes any ID that holds
# "HIDMaestro". Every controller devnode the SDK creates carries one of
# the three, under any enumerator: ROOT\HIDClass,
# ROOT\VID_xxxx&PID_yyyy&IG_00, SWD\HIDMAESTRO and
# SWD\HIDMAESTRO_VID_xxxx_PID_yyyy&IG_00. A parent's children (its HID
# child) are HIDMaestro's with it. Matching enumerator names instead saw
# only HIDMAESTRO*, HMCOMPANION, 045E:028E and BEEF:F000, so a leftover
# under ROOT\HIDClass or another IG_00 enumerator went unreported, and a
# foreign device under one of those names would have been counted.
#
# On the USB/IP backend, a controller is a USB device that Windows' own
# drivers bind, USB\VID_xxxx&PID_yyyy\<serial>, and nothing on it names
# HIDMaestro. It is plugged into the usbip-win2 host controller the SDK
# installs and stamps with ROOT\HIDMAESTRO_UDE
# (UsbipDriverInstaller.OwnerHardwareId). Every devnode below that host
# controller's root hub is HIDMaestro's: the USB device, its interfaces,
# their HID collections and audio endpoints. The host controller and its
# root hub are the transport, present from its install on, and no
# controller a scenario creates, so they are left out. A host controller
# without the stamp is another program's usbip-win2, and nothing below it
# counts.

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

    // The hardware ID the SDK stamps on the usbip-win2 host controller it
    // installs (UsbipDriverInstaller.OwnerHardwareId).
    public const string UsbipOwnerHardwareId = @"ROOT\HIDMAESTRO_UDE";

    // A USB tree below a root hub is a few levels deep: the device, an
    // interface, a HID collection, an audio endpoint. The walk stops well
    // past that, so a loop in a broken tree cannot hold a poll.
    const int MaxDepth = 16;

    // Every instance key under ROOT and SWD, with its hardware IDs, whether
    // it is in the device tree, and the instance IDs of its direct
    // children when it is. Then, below every present host controller that
    // carries UsbipOwnerHardwareId, every devnode in the tree, each with
    // its hardware IDs and children, so a caller can walk from the root hub
    // down.
    public static List<HMDevnodeRecord> Read() {
        var list = new List<HMDevnodeRecord>();
        var hostControllers = new List<uint>();
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
                        if (present && HasId(hw, UsbipOwnerHardwareId)) hostControllers.Add(devInst);
                    }
                }
            }
        }
        if (hostControllers.Count > 0) {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in list) seen.Add(r.Id);
            foreach (uint hc in hostControllers) AddDescendants(list, seen, hc, 0);
        }
        return list;
    }

    // A record for every devnode below devInst that the list does not hold
    // yet. An SWD audio endpoint below a USB device is already there from
    // the SWD walk, and its children are walked all the same.
    static void AddDescendants(List<HMDevnodeRecord> list, HashSet<string> seen, uint devInst, int depth) {
        if (depth >= MaxDepth) return;
        uint child;
        if (CM_Get_Child(out child, devInst, 0) != 0) return;
        while (true) {
            var sb = new StringBuilder(512);
            if (CM_Get_Device_IDW(child, sb, sb.Capacity, 0) == 0) {
                string id = sb.ToString();
                if (seen.Add(id)) {
                    list.Add(new HMDevnodeRecord {
                        Id = id,
                        HardwareIds = HardwareIdsOf(id),
                        Present = true,
                        Children = ChildIds(child),
                    });
                }
                AddDescendants(list, seen, child, depth + 1);
            }
            uint next;
            if (CM_Get_Sibling(out next, child, 0) != 0) break;
            child = next;
        }
    }

    static string[] HardwareIdsOf(string id) {
        try {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id)) {
                string[] hw = k == null ? null : k.GetValue("HardwareID") as string[];
                return hw ?? new string[0];
            }
        } catch { return new string[0]; }
    }

    static bool HasId(string[] ids, string want) {
        if (ids == null) return false;
        foreach (string id in ids)
            if (string.Equals(id, want, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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

# The hardware ID on the usbip-win2 host controller the SDK installs.
$script:HMUsbipHostHardwareId = 'ROOT\HIDMAESTRO_UDE'

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

# True when the hardware IDs are those of HIDMaestro's usbip-win2 host
# controller.
function Test-HMUsbipHostHardwareIds {
    param([string[]]$HardwareIds)
    foreach ($id in $HardwareIds) {
        if ([string]::Equals($id, $script:HMUsbipHostHardwareId, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

# The present HIDMaestro devnodes among $Nodes (records with Id,
# HardwareIds, Present and Children): each present parent that carries a
# HIDMaestro hardware ID, and its children, and every devnode below the
# root hub of a present host controller that carries ROOT\HIDMAESTRO_UDE,
# the host controller and the hub left out.
function Select-HMOwnedDevnodes {
    param([object[]]$Nodes)
    $owned = New-Object System.Collections.Generic.List[string]
    $byId = @{}
    foreach ($n in $Nodes) { if (-not $byId.ContainsKey($n.Id)) { $byId[$n.Id] = $n } }
    foreach ($n in $Nodes) {
        if (-not $n.Present) { continue }
        if (Test-HMOwnedHardwareIds $n.HardwareIds) {
            $owned.Add($n.Id)
            foreach ($c in $n.Children) { $owned.Add($c) }
            continue
        }
        if (-not (Test-HMUsbipHostHardwareIds $n.HardwareIds)) { continue }
        # Below each root hub, breadth first. A devnode can be reached once
        # only, so a broken tree that names a node twice adds it once.
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $queue = New-Object System.Collections.Generic.Queue[string]
        foreach ($hub in $n.Children) {
            if ($byId.ContainsKey($hub)) { foreach ($c in $byId[$hub].Children) { $queue.Enqueue($c) } }
        }
        while ($queue.Count -gt 0) {
            $id = $queue.Dequeue()
            if (-not $seen.Add($id)) { continue }
            $owned.Add($id)
            if ($byId.ContainsKey($id)) { foreach ($c in $byId[$id].Children) { $queue.Enqueue($c) } }
        }
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
