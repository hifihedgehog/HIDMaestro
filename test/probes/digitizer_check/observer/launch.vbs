' Starts observer.ps1 with no window, so nothing appears on the desktop.
CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\HIDMaestro\obs\observer.ps1", 0, False
