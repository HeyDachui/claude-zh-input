param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = 'C:\BuildTools\VS2022\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild not found: $msbuild"
}

& $msbuild (Join-Path $root 'NativeCandidate.vcxproj') /m /nologo `
    "/p:Configuration=$Configuration" "/p:Platform=$Platform" `
    '/p:WindowsTargetPlatformVersion=10.0.26100.0' `
    '/t:Rebuild' /fl "/flp:LogFile=$(Join-Path $root 'build.log');Verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "NativeCandidate build failed: $LASTEXITCODE" }

$dll = Join-Path $root "bin\$Platform\$Configuration\ClaudeZhInput.NativeCandidate.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Build output missing: $dll" }
Get-FileHash -Algorithm SHA256 -LiteralPath $dll
