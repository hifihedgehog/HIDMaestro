using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// The durable identity of one virtual controller across every life of
/// that controller (issue #60): the same process recreating it, a process
/// restart, a reboot, and a driver upgrade. Everything a consumer keys on
/// derives from here, so the interface path, the ContainerId and, for the
/// USB/IP personas, the USB serial come back the same on every life.
///
/// <para>The identity is keyed on what the consumer means, not on the
/// controller index Windows happened to hand out: PadForge's slot 0 is
/// slot 0 whether or not another pad was created first. A consumer that
/// passes no key gets the index as its key, which is what every caller
/// got before this existed.</para>
///
/// <para>Every derived value is a pure function of the key. Nothing is
/// stored, so a purged registry record, a fresh driver install or a new
/// machine image reproduces the same ids from the same key.</para>
///
/// <list type="bullet">
/// <item><see cref="Token"/> is the instance-name segment of every
/// devnode this controller owns: <c>ROOT\HIDClass\HM_0000</c> for the
/// default key of index 0, <c>SWD\HIDMAESTRO\HM_3F2A...</c> for a consumer
/// key. Windows reuses a ParentIdPrefix only for a parent whose instance
/// id it has seen before, so the parent's name has to be the same every
/// life.</item>
/// <item><see cref="ParentIdPrefix"/> is the <c>1&amp;hash&amp;0</c>
/// value Windows would otherwise mint per life. It is written into the
/// parent's instance key before the HID child enumerates (ROOT parents)
/// or reconciled and re-applied by a restart (SWD parents), so the child
/// path and with it the HID interface path, the SDL joystick path and the
/// RawInput device name stay put. Measured on 26200: PnP honors a value
/// that is in the key when the child first arrives, and a
/// disable/enable of the parent re-keys an already enumerated child.</item>
/// <item><see cref="ContainerId"/> groups the main devnode and any
/// companion of one controller, and is what Windows.Gaming.Input and
/// GameInput key on.</item>
/// <item><see cref="SyntheticSerial"/> is the USB serial served for a
/// USB/IP persona whose profile carries none (the Sony composites), so
/// the USB instance id keys on the serial instead of the vhci port the
/// attach happened to land on. Profiles with a captured serial keep it,
/// varied by <see cref="SerialVariant"/>.</item>
/// </list>
/// </summary>
internal sealed class DeviceIdentity
{
    /// <summary>Prefix of every instance-name token, so a token can never
    /// collide with a Windows-generated <c>0000</c>-style name and the
    /// sweeps can tell an identity-shaped record from a legacy one.</summary>
    public const string TokenPrefix = "HM_";

    /// <summary>The effective key: the consumer's string, or
    /// <see cref="DefaultKey"/> when none was given.</summary>
    public string Key { get; }

    /// <summary>True when the key was derived from the controller index
    /// because the consumer passed none.</summary>
    public bool IsDefault { get; }

    /// <summary>The controller index this identity is live at.</summary>
    public int Index { get; }

    /// <summary>Instance-name segment shared by every devnode of this
    /// controller.</summary>
    public string Token { get; }

    /// <summary>Per-controller container GUID for SwDeviceCreate.</summary>
    public Guid ContainerId { get; }

    /// <summary>The <c>level&amp;hash&amp;n</c> value the HID child's
    /// instance id is built from.</summary>
    public string ParentIdPrefix { get; }

    /// <summary>USB serial for personas whose profile carries none.</summary>
    public string SyntheticSerial { get; }

    /// <summary>Number folded into a captured USB serial's trailing digit
    /// run so two personas of one profile never share a serial. The index
    /// for the default key, which keeps index 0 byte-identical to the
    /// captured unit.</summary>
    public int SerialVariant { get; }

    private DeviceIdentity(string key, bool isDefault, int index, ulong hash)
    {
        Key = key;
        IsDefault = isDefault;
        Index = index;
        Token = isDefault ? $"{TokenPrefix}{index:D4}" : $"{TokenPrefix}{hash:X16}";
        if (isDefault)
        {
            ContainerId = SwdDeviceFactory.ContainerIdFor(index);
        }
        else
        {
            var d4 = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(d4, hash);
            ContainerId = new Guid(0x48494430, 0x4D41, 0x4553, d4);
        }
        ParentIdPrefix = $"1&{(uint)(hash >> 32):x}&0";
        SyntheticSerial = "HM" + (hash & 0xFFFF_FFFF_FFFFUL).ToString("X12");
        SerialVariant = isDefault ? index : (int)(hash % 999_999UL) + 1;
    }

    /// <summary>The key a caller gets when it passes none.</summary>
    public static string DefaultKey(int index) => $"index:{index}";

    public static DeviceIdentity ForIndex(int index)
        => new(DefaultKey(index), true, index, Hash(DefaultKey(index)));

    public static DeviceIdentity ForKey(string key, int index)
    {
        if (string.IsNullOrWhiteSpace(key)) return ForIndex(index);
        key = key.Trim();

        // A key in the default form IS the default identity of that index.
        // HMController.IdentityKey reports "index:N" for a controller made
        // without a key, and a consumer that hands it back (recreate with
        // the old controller's key) must get the same identity it had.
        // Before this, the same string derived a second token over the
        // same hash: two parents named HM_0000 and HM_<hash> sharing one
        // ParentIdPrefix. hidclass then built one HID child id under both,
        // and PnP bugchecks 0xCA (duplicate PDO) on that. ROOT parents are
        // re-enumerated at boot, so the machine could not start until one
        // was removed in Safe Mode. Measured on 26200, 2026-09-20.
        if (TryParseDefaultKey(key, out int defaultIndex)) return ForIndex(defaultIndex);

        return new DeviceIdentity(key, false, index, Hash(key));
    }

    /// <summary>True when <paramref name="key"/> is exactly what
    /// <see cref="DefaultKey"/> produces for some index.</summary>
    private static bool TryParseDefaultKey(string key, out int index)
    {
        index = -1;
        const string lead = "index:";
        if (!key.StartsWith(lead, StringComparison.Ordinal)) return false;
        if (!int.TryParse(key.AsSpan(lead.Length), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out int n)) return false;
        if (DefaultKey(n) != key) return false;   // canonical form only: no "index:007"
        index = n;
        return true;
    }

    /// <summary>The identity for a create call: the consumer's key when it
    /// gave one, the index otherwise.</summary>
    public static DeviceIdentity Resolve(string? key, int index)
        => string.IsNullOrWhiteSpace(key) ? ForIndex(index) : ForKey(key!, index);

    /// <summary>True when an instance name is one of ours in the identity
    /// shape.</summary>
    public static bool IsIdentityToken(string instanceName)
        => instanceName != null && instanceName.StartsWith(TokenPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an instance name is the token of a default
    /// identity, <see cref="TokenPrefix"/> and the index in four or more
    /// decimal digits, and gives that index. A keyed token is sixteen hex
    /// digits, so a name of that length is never read as one.</summary>
    public static bool TryParseDefaultToken(string instanceName, out int index)
    {
        index = -1;
        if (!IsIdentityToken(instanceName)) return false;
        ReadOnlySpan<char> digits = instanceName.AsSpan(TokenPrefix.Length);
        if (digits.Length < 4 || digits.Length >= 16) return false;
        if (!int.TryParse(digits, System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out int n)) return false;
        if (!string.Equals(ForIndex(n).Token, instanceName, StringComparison.OrdinalIgnoreCase)) return false;
        index = n;
        return true;
    }

    /// <summary>Write <paramref name="prefix"/> as the ParentIdPrefix of the
    /// devnode at <paramref name="instanceId"/> unless it is already there.
    /// Returns the previous value, or null when nothing was written. With
    /// <paramref name="createKey"/> the instance key is created when
    /// missing: correct for a ROOT parent between SetupDiCreateDeviceInfo
    /// and DIF_REGISTERDEVICE, and never for an SWD parent, where a key
    /// that exists before SwDeviceCreate leaves the device unenumerable.
    /// Administrators hold full control on instance keys on 26200, so a
    /// failure here is logged and the caller carries on with whatever
    /// prefix Windows assigns.</summary>
    public static (bool Written, string? Previous) ApplyParentIdPrefix(string instanceId, string prefix, bool createKey)
    {
        string path = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}";
        try
        {
            using var key = createKey
                ? Registry.LocalMachine.CreateSubKey(path)
                : Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key == null)
            {
                DeviceOrchestrator.LogDiag($"      ParentIdPrefix: no instance key for {instanceId}");
                return (false, null);
            }
            string? previous = key.GetValue("ParentIdPrefix") as string;
            if (string.Equals(previous, prefix, StringComparison.OrdinalIgnoreCase))
                return (false, previous);
            key.SetValue("ParentIdPrefix", prefix, RegistryValueKind.String);
            DeviceOrchestrator.LogDiag($"      ParentIdPrefix: {instanceId} <- {prefix} (was {previous ?? "(none)"})");
            return (true, previous);
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"      ParentIdPrefix write FAILED for {instanceId}: {ex.GetType().Name}");
            return (false, null);
        }
    }

    private static ulong Hash(string key)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("HIDMaestro.DeviceIdentity.v1\n" + key));
        return BinaryPrimitives.ReadUInt64BigEndian(digest);
    }

    public override string ToString() => $"{Key} -> {Token}";
}
