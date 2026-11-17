#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [switch]$Settings,
    [switch]$Stop,
    [switch]$Simulate
)

$ErrorActionPreference = 'Stop'
$setupRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $setupRoot 'setup-common.ps1')

$defaultDirectory = Get-ClaudeDefaultInstallDirectory
$resolvedInstallDirectory = if ([String]::IsNullOrWhiteSpace($InstallDirectory)) {
    $defaultDirectory
} else {
    Get-ClaudeFullPath $InstallDirectory
}
$resolvedInstallDirectory = Assert-ClaudeSafeInstallDirectory -Path $resolvedInstallDirectory

if ($Stop) {
    if (-not (Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container)) {
        Write-Host "未找到 Claude 中文输入法安装目录：$resolvedInstallDirectory"
        exit 0
    }
    Stop-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory -Simulate:$Simulate
    if (-not $Simulate) { Write-Host '已停止 Claude 中文输入法服务。' }
    exit 0
}

if (-not (Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container)) {
    throw "未找到已安装的 Claude 中文输入法：$resolvedInstallDirectory。请先运行 setup\install.ps1。"
}
if (-not (Test-Path -LiteralPath (Join-Path $resolvedInstallDirectory 'app\ClaudeZhInput.exe') -PathType Leaf)) {
    throw "安装目录缺少输入法程序：$resolvedInstallDirectory。请重新运行安装脚本。"
}

Start-ClaudeProduct -InstallDirectory $resolvedInstallDirectory -Settings:$Settings -Simulate:$Simulate
if (-not $Simulate) {
    if ($Settings) { Write-Host '已打开 Claude 中文输入法设置。' }
    else { Write-Host '已在当前用户后台启动 Claude 中文输入法服务。' }
}

