/* Forced include (cl /FI) for driver.c and companion.c in the driver
 * harness.
 *
 * The real Windows SDK and WDK headers come first, in the order driver.h
 * and companion.c include them (driver.h:17-23, driver.c:20-21,
 * companion.c:9-15), so every type, macro and IOCTL code is the kit's own.
 * Their include guards keep the drivers' own includes from repeating them.
 * Then each Win32 call the drivers make is mapped onto the harness's
 * stand-in (hm_api.h). WDF calls need no mapping: wdf.h routes every one
 * through WdfFunctions_02015 with WdfDriverGlobals (wdf.h:69-72,
 * wdffuncenum.h:21), and the harness defines both.
 *
 * The build checks the compiled driver objects for any import from a
 * system DLL (build.ps1), so a Win32 call missing here fails the build
 * rather than reaching the machine. */
#pragma once

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>
#include <hidport.h>
#include <devpropdef.h>
#include <sddl.h>
#include <aclapi.h>

#include "hm_api.h"

#define RegOpenKeyExW                                       HmRegOpenKeyExW
#define RegQueryValueExW                                    HmRegQueryValueExW
#define RegCloseKey                                         HmRegCloseKey
#define OpenFileMappingW                                    HmOpenFileMappingW
#define MapViewOfFile                                       HmMapViewOfFile
#define UnmapViewOfFile                                     HmUnmapViewOfFile
#define CloseHandle                                         HmCloseHandle
#define CreateEventW                                        HmCreateEventW
#define OpenEventW                                          HmOpenEventW
#define SetEvent                                            HmSetEvent
#define ResetEvent                                          HmResetEvent
#define WaitForSingleObject                                 HmWaitForSingleObject
#define WaitForMultipleObjects                              HmWaitForMultipleObjects
#define CreateThread                                        HmCreateThread
#define GetLastError                                        HmGetLastError
#define CreateFileW                                         HmCreateFileW
#define ReadFile                                            HmReadFile
#define CancelIoEx                                          HmCancelIoEx
#define GetOverlappedResult                                 HmGetOverlappedResult
#define GetNamedPipeServerProcessId                         HmGetNamedPipeServerProcessId
#define ConvertStringSecurityDescriptorToSecurityDescriptorW HmConvertStringSecurityDescriptorToSecurityDescriptorW
#define ConvertSidToStringSidW                              HmConvertSidToStringSidW
#define GetSecurityInfo                                     HmGetSecurityInfo
#define SetSecurityInfo                                     HmSetSecurityInfo
#define IsWellKnownSid                                      HmIsWellKnownSid
#define GetSecurityDescriptorDacl                           HmGetSecurityDescriptorDacl
#define LocalFree                                           HmLocalFree
#define InitializeCriticalSection                           HmInitializeCriticalSection
#define EnterCriticalSection                                HmEnterCriticalSection
#define LeaveCriticalSection                                HmLeaveCriticalSection
#define DeleteCriticalSection                               HmDeleteCriticalSection
#define AcquireSRWLockExclusive                             HmAcquireSRWLockExclusive
#define ReleaseSRWLockExclusive                             HmReleaseSRWLockExclusive
#define GetTickCount64                                      HmGetTickCount64
#define GetSystemTimeAsFileTime                             HmGetSystemTimeAsFileTime
#define RtlInitUnicodeString                                HmRtlInitUnicodeString
#define RtlCompareMemory                                    HmRtlCompareMemory
