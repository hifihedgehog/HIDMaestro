using System.Runtime.CompilerServices;

// HIDMaestroTest is the in-tree CLI test client for the SDK. It's allowed to
// reach into the library's internal helpers (SharedMemoryIO, DeviceOrchestrator,
// etc.) during the gradual extraction so the test app and SDK can stay in
// parallel as code moves between them.
[assembly: InternalsVisibleTo("HIDMaestroTest")]
[assembly: InternalsVisibleTo("SonyBtReport31Check")]
[assembly: InternalsVisibleTo("SonyBtOutputDecodeCheck")]
[assembly: InternalsVisibleTo("SonyDataDrivenCoverage")]
[assembly: InternalsVisibleTo("V135FeaturesCheck")]
[assembly: InternalsVisibleTo("TriggerClassifierCheck")]
[assembly: InternalsVisibleTo("TriggerLiveCheck")]
[assembly: InternalsVisibleTo("SideWinderFfbCheck")]
[assembly: InternalsVisibleTo("LayoutAuditCheck")]
[assembly: InternalsVisibleTo("AxisAddressingCheck")]
[assembly: InternalsVisibleTo("SonyBtAxisRoleCheck")]
[assembly: InternalsVisibleTo("XboxGipTriggerResolverCheck")]
[assembly: InternalsVisibleTo("SwitchProCheck")]
[assembly: InternalsVisibleTo("Switch2ProCheck")]
[assembly: InternalsVisibleTo("SwitchDescriptorIdleCheck")]
[assembly: InternalsVisibleTo("StopEventResilienceCheck")]
[assembly: InternalsVisibleTo("UsbCompositeSchemaCheck")]
[assembly: InternalsVisibleTo("ValvePersonaCheck")]
[assembly: InternalsVisibleTo("UsbipServerCheck")]
[assembly: InternalsVisibleTo("UsbipBundleCheck")]
[assembly: InternalsVisibleTo("switch_writer_tap")]
[assembly: InternalsVisibleTo("SonyFeatureGateCheck")]
[assembly: InternalsVisibleTo("OutputPerfBench")]
[assembly: InternalsVisibleTo("VendorBlobGoldenCheck")]
[assembly: InternalsVisibleTo("Switch2ProSdl3Check")]
[assembly: InternalsVisibleTo("SonyExtraButtonsCheck")]
[assembly: InternalsVisibleTo("VrControllerSmoke")]
[assembly: InternalsVisibleTo("ValveRawPathCheck")]
