@echo off
set XPERF="C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe"
%XPERF% -on PROC_THREAD+LOADER+PROFILE -stackwalk Profile -f c:\tmp\ChromiumTrace\kernel.etl > c:\tmp\ChromiumTrace\xperf_kernel_start.log 2>&1
%XPERF% -start UserTrace -on Microsoft-Windows-HID-Class+Microsoft-Windows-COMRuntime -f c:\tmp\ChromiumTrace\user.etl > c:\tmp\ChromiumTrace\xperf_user_start.log 2>&1
echo DONE_START >> c:\tmp\ChromiumTrace\xperf_user_start.log
