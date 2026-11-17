#requires -Version 5.1
param()
$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'ClaudeZhInput.NativeCandidate.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Native Rime DLL missing: $dll" }
$process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/regsvr32.exe') -ArgumentList ('/s "' + $dll + '"') -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Native Rime registration failed: $($process.ExitCode)" }
$key = Get-Item -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CLSID\{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}\InprocServer32'
if ($key.GetValue('') -ne $dll) { throw 'Native Rime registration readback mismatch' }
