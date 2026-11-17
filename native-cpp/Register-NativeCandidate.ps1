param(
    [Parameter(Mandatory = $true)] [string]$DllPath,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Native candidate registration writes HKLM; run this script from an elevated PowerShell.'
}
$resolved = (Resolve-Path -LiteralPath $DllPath).Path
$regsvr = Join-Path $env:WINDIR 'System32\regsvr32.exe'
if (-not (Test-Path -LiteralPath $regsvr)) { throw "x64 regsvr32 not found: $regsvr" }

$argument = if ($Unregister) { "/u /s `"$resolved`"" } else { "/s `"$resolved`"" }
$process = Start-Process -FilePath $regsvr -ArgumentList $argument -WindowStyle Hidden -PassThru
$process.WaitForExit()
$process.Refresh()
if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0) {
    $exitCode = if ($null -eq $process.ExitCode) { 0xFFFFFFFF } else { [uint32]$process.ExitCode }
    throw "regsvr32 failed with HRESULT/exit code 0x{0:X8}" -f $exitCode
}
$message = if ($Unregister) { "Unregistered $resolved" } else { "Registered $resolved" }
Write-Output $message
