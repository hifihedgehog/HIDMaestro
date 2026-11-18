using System;

namespace HIDMaestro;

/// <summary>
/// A call asked for HIDMaestro's own UMDF2 driver on a PC where Windows
/// does not install it. Derived from <see cref="InvalidOperationException"/>.
/// Thrown only for an explicit <see cref="HMBackend.Umdf2"/>, on the context
/// or on the create, before the call changes anything on a PC that holds no
/// HIDMaestro driver: no certificate, no staged file and no service setting.
/// <see cref="HMContext.MachineBackend"/> reads <see cref="HMBackend.Usbip"/>
/// on such a PC, and <see cref="HMBackend.Auto"/> runs every profile there
/// on the USB/IP backend.
/// </summary>
public sealed class HMDriverSigningException : InvalidOperationException
{
    internal const string DefaultMessage =
        "HIDMaestro's own driver is not installed on this PC. "
      + "HMBackend.Auto or HMBackend.Usbip runs the controller on the USB/IP backend.";

    internal HMDriverSigningException() : base(DefaultMessage) { }

    internal HMDriverSigningException(string message) : base(message) { }
}
