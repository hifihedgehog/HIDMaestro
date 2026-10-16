/* The harness's stand-ins for every Win32 call driver.c and companion.c
 * make. hm_fi.h maps each Win32 name onto the Hm name below, so the driver
 * sources compile unmodified and reach no registry, named kernel object,
 * pipe or thread pool of the machine. Each signature is the Win32 one, so
 * the compiler checks every call site against it. */
#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/* Registry: an in-memory map the scenario fills. */
LSTATUS WINAPI HmRegOpenKeyExW(HKEY hKey, LPCWSTR lpSubKey, DWORD ulOptions, REGSAM samDesired, PHKEY phkResult);
LSTATUS WINAPI HmRegQueryValueExW(HKEY hKey, LPCWSTR lpValueName, LPDWORD lpReserved, LPDWORD lpType, LPBYTE lpData, LPDWORD lpcbData);
LSTATUS WINAPI HmRegCloseKey(HKEY hKey);

/* Sections: in-process buffers the scenario owns. */
HANDLE WINAPI HmOpenFileMappingW(DWORD dwDesiredAccess, BOOL bInheritHandle, LPCWSTR lpName);
LPVOID WINAPI HmMapViewOfFile(HANDLE hFileMappingObject, DWORD dwDesiredAccess, DWORD dwFileOffsetHigh, DWORD dwFileOffsetLow, SIZE_T dwNumberOfBytesToMap);
BOOL WINAPI HmUnmapViewOfFile(LPCVOID lpBaseAddress);
BOOL WINAPI HmCloseHandle(HANDLE hObject);

/* Events and waits. A wait on a harness thread switches back to the
 * scenario, which decides what the wait returns. */
HANDLE WINAPI HmCreateEventW(LPSECURITY_ATTRIBUTES lpEventAttributes, BOOL bManualReset, BOOL bInitialState, LPCWSTR lpName);
HANDLE WINAPI HmOpenEventW(DWORD dwDesiredAccess, BOOL bInheritHandle, LPCWSTR lpName);
BOOL WINAPI HmSetEvent(HANDLE hEvent);
BOOL WINAPI HmResetEvent(HANDLE hEvent);
DWORD WINAPI HmWaitForSingleObject(HANDLE hHandle, DWORD dwMilliseconds);
DWORD WINAPI HmWaitForMultipleObjects(DWORD nCount, const HANDLE *lpHandles, BOOL bWaitAll, DWORD dwMilliseconds);
HANDLE WINAPI HmCreateThread(LPSECURITY_ATTRIBUTES lpThreadAttributes, SIZE_T dwStackSize, LPTHREAD_START_ROUTINE lpStartAddress, LPVOID lpParameter, DWORD dwCreationFlags, LPDWORD lpThreadId);
DWORD WINAPI HmGetLastError(VOID);

/* The owner pipe. */
HANDLE WINAPI HmCreateFileW(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode, LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition, DWORD dwFlagsAndAttributes, HANDLE hTemplateFile);
BOOL WINAPI HmReadFile(HANDLE hFile, LPVOID lpBuffer, DWORD nNumberOfBytesToRead, LPDWORD lpNumberOfBytesRead, LPOVERLAPPED lpOverlapped);
BOOL WINAPI HmCancelIoEx(HANDLE hFile, LPOVERLAPPED lpOverlapped);
BOOL WINAPI HmGetOverlappedResult(HANDLE hFile, LPOVERLAPPED lpOverlapped, LPDWORD lpNumberOfBytesTransferred, BOOL bWait);
BOOL WINAPI HmGetNamedPipeServerProcessId(HANDLE Pipe, PULONG ServerProcessId);

/* Security. */
BOOL WINAPI HmConvertStringSecurityDescriptorToSecurityDescriptorW(LPCWSTR StringSecurityDescriptor, DWORD StringSDRevision, PSECURITY_DESCRIPTOR *SecurityDescriptor, PULONG SecurityDescriptorSize);
BOOL WINAPI HmConvertSidToStringSidW(PSID Sid, LPWSTR *StringSid);
DWORD WINAPI HmGetSecurityInfo(HANDLE handle, SE_OBJECT_TYPE ObjectType, SECURITY_INFORMATION SecurityInfo, PSID *ppsidOwner, PSID *ppsidGroup, PACL *ppDacl, PACL *ppSacl, PSECURITY_DESCRIPTOR *ppSecurityDescriptor);
DWORD WINAPI HmSetSecurityInfo(HANDLE handle, SE_OBJECT_TYPE ObjectType, SECURITY_INFORMATION SecurityInfo, PSID psidOwner, PSID psidGroup, PACL pDacl, PACL pSacl);
BOOL WINAPI HmIsWellKnownSid(PSID pSid, WELL_KNOWN_SID_TYPE WellKnownSidType);
BOOL WINAPI HmGetSecurityDescriptorDacl(PSECURITY_DESCRIPTOR pSecurityDescriptor, LPBOOL lpbDaclPresent, PACL *pDacl, LPBOOL lpbDaclDefaulted);
HLOCAL WINAPI HmLocalFree(HLOCAL hMem);

/* Locks, clocks and the two Rtl helpers. */
VOID WINAPI HmInitializeCriticalSection(LPCRITICAL_SECTION lpCriticalSection);
VOID WINAPI HmEnterCriticalSection(LPCRITICAL_SECTION lpCriticalSection);
VOID WINAPI HmLeaveCriticalSection(LPCRITICAL_SECTION lpCriticalSection);
VOID WINAPI HmDeleteCriticalSection(LPCRITICAL_SECTION lpCriticalSection);
ULONGLONG WINAPI HmGetTickCount64(VOID);
VOID WINAPI HmGetSystemTimeAsFileTime(LPFILETIME lpSystemTimeAsFileTime);
VOID NTAPI HmRtlInitUnicodeString(PUNICODE_STRING DestinationString, PCWSTR SourceString);
SIZE_T NTAPI HmRtlCompareMemory(const VOID *Source1, const VOID *Source2, SIZE_T Length);

#ifdef __cplusplus
}
#endif
