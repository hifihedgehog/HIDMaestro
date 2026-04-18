@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat" amd64 > nul 2>&1
cl.exe /nologo /EHsc /Zi /std:c++17 /I"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um" /Fewgi_read.exe wgi_read.cpp /link /LIBPATH:"C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0\um\x64" runtimeobject.lib
