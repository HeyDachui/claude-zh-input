param([switch]$Rollback,[ValidatePattern('^[a-z0-9-]+$')][string]$Revision='rime-20261009',[string]$Payload,[switch]$EnableBehaviorRecording)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'setup-common.ps1')
$install=Assert-ClaudeSafeInstallDirectory -Path (Join-Path $env:LOCALAPPDATA 'Programs/ClaudeZhInput')
$backup=Join-Path $install ('repair-backups/'+$Revision)
$receipt=Join-Path $backup 'rollback.json'
$registry='Registry::HKEY_CLASSES_ROOT\CLSID\{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}\InprocServer32'
$preferences=Join-Path $env:LOCALAPPDATA 'ClaudeZhInput/preferences.json'
$behaviorConfig=Join-Path $env:LOCALAPPDATA 'ClaudeZhInput/behavior-recording.json'
function Register-RimeDll([string]$Path){
    $child=Start-Process -FilePath "$env:WINDIR/System32/regsvr32.exe" -ArgumentList ('/s "'+$Path+'"') -Verb RunAs -WindowStyle Hidden -PassThru -Wait
    if($child.ExitCode -ne 0){throw "Rime registration failed: $($child.ExitCode)"}
    if((Get-Item -LiteralPath $registry).GetValue('') -ne $Path){throw 'Rime registration readback mismatch'}
}
if($Rollback){
    $saved=Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    if($saved.install -ne $install){throw 'Rollback belongs to another installation'}
    Stop-ClaudeProductProcesses -InstallDirectory $install
    Copy-Item -Path (Join-Path $backup 'app/*') -Destination (Join-Path $install 'app') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $backup 'preferences.json') -Destination $preferences -Force
    Register-RimeDll $saved.previousDll
    Start-ClaudeProduct -InstallDirectory $install
    'Previous application and Rime registration restored.'
    return
}
if(Test-Path -LiteralPath $receipt){throw 'This repair already has a rollback receipt; do not overwrite it'}
$payload=if($Payload){[IO.Path]::GetFullPath($Payload)}else{Join-Path $project 'artifacts/app-rime-repair'}
if ((Test-Path -LiteralPath (Join-Path $payload 'ClaudeZhInput.dll')) -and
    [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'ClaudeZhInput.dll')).Version -ge [Version]'1.1.0.0') {
    throw 'Version 1.1 requires the complete matching worker and native package. Use that package setup/install.ps1; this legacy app-only repair cannot deploy it.'
}
$sourceDll=Join-Path $project 'native-cpp/bin/x64/Release/ClaudeZhInput.NativeCandidate.dll'
foreach($required in @('ClaudeZhInput.exe','ClaudeZhInput.dll','runtime/rime.dll','runtime/data/build/pinyin_simp.table.bin')){
    if(-not (Test-Path -LiteralPath (Join-Path $payload $required))){throw "Missing application payload: $required"}
}
$dllHash=(Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash
$slot=Join-Path $install ('native/versions/rime-'+$dllHash.Substring(0,24).ToLowerInvariant())
$dll=Join-Path $slot 'ClaudeZhInput.NativeCandidate.dll'
[void](New-Item -ItemType Directory -Path $backup -Force)
Copy-Item -LiteralPath (Join-Path $install 'app') -Destination (Join-Path $backup 'app') -Recurse
Copy-Item -LiteralPath $preferences -Destination (Join-Path $backup 'preferences.json')
$saved=@{install=$install;previousDll=(Get-Item -LiteralPath $registry).GetValue('');dll=$dll;sha256=$dllHash;at=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText($receipt,($saved | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
try{
    [void](New-Item -ItemType Directory -Path $slot -Force)
    if(Test-Path -LiteralPath $dll){
        if((Get-FileHash -LiteralPath $dll).Hash -ne $dllHash){throw 'Immutable Rime slot mismatch'}
    }else{Copy-Item -LiteralPath $sourceDll -Destination $dll}
    [IO.File]::WriteAllText((Join-Path $slot 'ClaudeZhInput.NativeCandidate.pipe'),'ClaudeZhInput',[Text.UTF8Encoding]::new($false))
    Stop-ClaudeProductProcesses -InstallDirectory $install
    Copy-Item -Path (Join-Path $payload '*') -Destination (Join-Path $install 'app') -Recurse -Force
    Register-RimeDll $dll
    if($EnableBehaviorRecording){[IO.File]::WriteAllText($behaviorConfig,'{"enabled":true,"inputText":false}',[Text.UTF8Encoding]::new($false))}
    Start-ClaudeProduct -InstallDirectory $install
    $saved | ConvertTo-Json -Compress
}catch{
    $failure=$_
    Stop-ClaudeProductProcesses -InstallDirectory $install
    Copy-Item -Path (Join-Path $backup 'app/*') -Destination (Join-Path $install 'app') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $backup 'preferences.json') -Destination $preferences -Force
    Register-RimeDll $saved.previousDll
    Start-ClaudeProduct -InstallDirectory $install
    throw $failure
}
