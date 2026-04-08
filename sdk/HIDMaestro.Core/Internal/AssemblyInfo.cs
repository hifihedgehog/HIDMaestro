using System.Runtime.CompilerServices;

// HIDMaestroTest is the in-tree CLI test client for the SDK. It's allowed to
// reach into the library's internal helpers (SharedMemoryIO, DeviceOrchestrator,
// etc.) during the gradual extraction so the test app and SDK can stay in
// parallel as code moves between them.
[assembly: InternalsVisibleTo("HIDMaestroTest")]
