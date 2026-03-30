$build = "C:\Users\sonic\OneDrive\Documents\GitHub\HIDMaestro\build"
$makecert = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makecert.exe"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$certName = "HIDMaestroTestCert"
$store = "PrivateCertStore"
$sys = "$build\HIDMaestro.dll"
$cer = "$build\$certName.cer"
$log = "$build\sign_log.txt"

"=== HIDMaestro Sign Log ===" | Out-File $log

"Creating test certificate..." | Tee-Object -Append $log
& $makecert -r -pe -ss $store -n "CN=$certName" $cer *>> $log 2>&1

"Adding to trusted stores..." | Tee-Object -Append $log
certutil -addstore Root $cer *>> $log 2>&1
certutil -addstore TrustedPublisher $cer *>> $log 2>&1

"Signing $sys ..." | Tee-Object -Append $log
& $signtool sign /v /a /s $store /n $certName /fd SHA256 "$sys" *>> $log 2>&1

"Verifying..." | Tee-Object -Append $log
$sig = Get-AuthenticodeSignature $sys
$sig | Format-List * >> $log 2>&1
"Status: $($sig.Status)" | Tee-Object -Append $log
"Signer: $($sig.SignerCertificate.Subject)" | Tee-Object -Append $log

"Done. Log: $log" | Tee-Object -Append $log
