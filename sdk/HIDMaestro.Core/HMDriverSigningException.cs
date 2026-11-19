using System;

namespace HIDMaestro;

/// <summary>
/// A call asked for HIDMaestro's own UMDF2 driver on a PC where Windows
/// does not install it. Derived from <see cref="InvalidOperationException"/>.
///
/// <para>A create and <see cref="HMContext.InstallDriver"/> throw it only
/// for an explicit <see cref="HMBackend.Umdf2"/>: the create's backend
/// argument, the context's backend, or the environment variable
/// <c>HIDMAESTRO_BACKEND</c> set to <c>umdf2</c>. On a PC that holds no
/// HIDMaestro driver package it comes before the call changes anything: no
/// certificate, no staged file and no service setting. Where the store
/// already holds an older HIDMaestro package, the deploy is tried, and the
/// exception names the import Windows refused.
/// <see cref="HMContext.MachineBackend"/> reads <see cref="HMBackend.Usbip"/>
/// on such a PC unless this build's driver is installed there already, and
/// <see cref="HMBackend.Auto"/> then runs every profile it can on the USB/IP
/// backend.</para>
///
/// <para><see cref="HMController.BindWinUsb"/> throws it on such a PC whose
/// driver store holds a HIDMaestro WinUSB package from before, when Windows
/// refuses the binding's package, which is signed as the driver's is. Where
/// the store holds none, the binding goes by the device's Microsoft OS
/// descriptors and adds no package.</para>
/// </summary>
public sealed class HMDriverSigningException : InvalidOperationException
{
    internal const string DefaultMessage =
        "HIDMaestro's own driver is not installed on this PC. "
      + "HMBackend.Auto or HMBackend.Usbip runs the controller on the USB/IP backend.";

    internal HMDriverSigningException() : base(DefaultMessage) { }

    internal HMDriverSigningException(string message) : base(message) { }
}
