#requires -Version 5.1
param()
$ErrorActionPreference = 'Stop'
$dll = Join-Path $PSScriptRoot 'ClaudeZhInput.NativeCandidate.dll'
$registered = Get-Item -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CLSID\{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}\InprocServer32' -ErrorAction SilentlyContinue
if ($registered -and $registered.GetValue('') -ne $dll) { throw 'A different native version is registered; refusing to unregister it from an old slot.' }
if (-not $registered) { return }
$process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/regsvr32.exe') -ArgumentList ('/u /s "' + $dll + '"') -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Native Rime unregistration failed: $($process.ExitCode)" }
