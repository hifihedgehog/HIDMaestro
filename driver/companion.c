/*
 * HIDMaestro Companion — minimal UMDF2 driver that registers
 * XUSB + WinExInput device interfaces. No HID, no filter mode.
 * Used for WGI browser detection and SDL3 identity.
 */

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD CompanionDeviceAdd;

static const GUID XUSB_GUID =
    { 0xEC87F1E3, 0xC13B, 0x4100, { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB } };
static const GUID WINEXINPUT_GUID =
    { 0x6C53D5FD, 0x6480, 0x440F, { 0xB6, 0x18, 0x47, 0x67, 0x50, 0xC5, 0xE1, 0xA6 } };

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, CompanionDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS CompanionDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;

    UNREFERENCED_PARAMETER(Driver);

    /* NOT a filter — function driver mode. Can register interfaces. */
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    /* Register XUSB interface */
    WdfDeviceCreateDeviceInterface(device, (LPGUID)&XUSB_GUID, NULL);

    /* Register WinExInput interface with XI_00 reference */
    {
        UNICODE_STRING refStr;
        RtlInitUnicodeString(&refStr, L"XI_00");
        WdfDeviceCreateDeviceInterface(device, (LPGUID)&WINEXINPUT_GUID, &refStr);
    }

    return STATUS_SUCCESS;
}
