@echo off
set XPERF="C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\xperf.exe"
%XPERF% -stop UserTrace > c:\tmp\ChromiumTrace\xperf_user_stop.log 2>&1
%XPERF% -stop > c:\tmp\ChromiumTrace\xperf_kernel_stop.log 2>&1
%XPERF% -merge c:\tmp\ChromiumTrace\kernel.etl c:\tmp\ChromiumTrace\user.etl c:\tmp\ChromiumTrace\merged.etl > c:\tmp\ChromiumTrace\xperf_merge.log 2>&1
echo DONE_STOP >> c:\tmp\ChromiumTrace\xperf_merge.log
