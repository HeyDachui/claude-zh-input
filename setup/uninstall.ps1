#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [switch]$RestartTextServices,
    [switch]$Simulate,
    [switch]$Interactive,
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$setupRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $setupRoot 'setup-common.ps1')
$uninstallerScriptPath = Get-ClaudeFullPath $MyInvocation.MyCommand.Path

function ConvertTo-ClaudeProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value -match '[\s"]') {
        return ('"' + $Value.Replace('"', '\"') + '"')
    }
    return $Value
}

function Start-ClaudeElevatedUninstall {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:uninstallerScriptPath,
        '-Elevated', '-InstallDirectory', $script:resolvedInstallDirectory)
    if ($RestartTextServices) { $arguments += '-RestartTextServices' }
    $argumentString = (($arguments | ForEach-Object { ConvertTo-ClaudeProcessArgument ([string]$_) }) -join ' ')
    $child = Start-Process -FilePath $powershell -ArgumentList $argumentString -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    return [int]$child.ExitCode
}

function Show-ClaudeUninstallNotice([string]$Message) {
    if ($Interactive -and -not $Simulate) {
        Add-Type -AssemblyName System.Windows.Forms
        [void][Windows.Forms.MessageBox]::Show($Message, 'Claude 中文输入法')
    }
}

try {
$resolvedInstallDirectory = if ([String]::IsNullOrWhiteSpace($InstallDirectory)) {
    Get-ClaudeDefaultInstallDirectory
} else {
    Get-ClaudeFullPath $InstallDirectory
}
$script:resolvedInstallDirectory = Assert-ClaudeSafeInstallDirectory -Path $resolvedInstallDirectory

if (-not (Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container)) {
    if ($Simulate) {
        Write-Host "[模拟] 目标目录不存在，无需卸载：$resolvedInstallDirectory"
    } else {
        Remove-ClaudeUserMaintenanceRegistration -InstallDirectory $resolvedInstallDirectory
        Write-Host "未找到 Claude 中文输入法安装目录：$resolvedInstallDirectory"
        Show-ClaudeUninstallNotice '产品文件已不存在，已清理当前用户的维护入口。个人数据保留。'
    }
    exit 0
}

[void](Assert-ClaudeOwnedInstallDirectory $resolvedInstallDirectory)
if (-not (Test-ClaudeAdministrator) -and -not $Simulate) {
    $elevatedExitCode = Start-ClaudeElevatedUninstall
    if ($elevatedExitCode -ne 0) {
        Write-Host "卸载失败：管理员卸载子进程退出码 $elevatedExitCode。"
        Show-ClaudeUninstallNotice ('卸载未完成，退出码 ' + $elevatedExitCode + '。请运行完整包中的“卸载输入法.cmd”查看详细错误。')
        exit $elevatedExitCode
    }
    # HKCU language/TIP state must be changed by the original user.  The
    # elevated child only removes machine/product files and registration.
    [void](Remove-ClaudeUserLanguageTip)
    Remove-ClaudeStartupRegistration -InstallDirectory $resolvedInstallDirectory
    if (-not (Test-Path -LiteralPath $resolvedInstallDirectory)) { Remove-ClaudeUserMaintenanceRegistration -InstallDirectory $resolvedInstallDirectory }
    Write-Host '已在原始用户语言列表移除本产品 TIP，保留其他语言和输入法。'
    if (Test-Path -LiteralPath $resolvedInstallDirectory) {
        Show-ClaudeUninstallNotice '输入法已注销，部分文件仍被应用占用。保存工作并关闭相关应用后，再次运行卸载即可清理。个人数据保留。'
    } else { Show-ClaudeUninstallNotice 'Claude 中文输入法已卸载。个人数据保留；需要清除或迁移时请查看使用说明。' }
    exit 0
}

$marker = $null
$markerPath = Get-ClaudeInstallMarkerPath $resolvedInstallDirectory
if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
    try { $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "安装标记无法读取，未删除产品目录：$markerPath" }
}

if ($Simulate) {
    Stop-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory -Simulate
    Remove-ClaudeStartupRegistration -InstallDirectory $resolvedInstallDirectory -RecordedCommand ([string]$marker.startupCommand) -Simulate
    Write-Host '[模拟] 将只移除当前用户语言列表中的本产品 TIP，保留其他语言和输入法。'
    Write-Host '[模拟] 将注销本产品 TSF profile/COM host，删除本产品目录，保留个人词库、皮肤和模式状态。'
    exit 0
}

Stop-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory
if (-not ($marker -and $marker.PSObject.Properties['uninstallPending'] -and $marker.uninstallPending)) {
    Invoke-ClaudeNativeUninstall -InstallDirectory $resolvedInstallDirectory -RestartTextServices:$RestartTextServices
}
if (-not $Elevated) {
    Remove-ClaudeStartupRegistration -InstallDirectory $resolvedInstallDirectory -RecordedCommand ([string]$marker.startupCommand)
    [void](Remove-ClaudeUserLanguageTip)
}

# Do not touch %LOCALAPPDATA%\ClaudeZhInput.  It contains the personal
# dictionary and the user's preferences/state, which must survive uninstall.
if (Remove-ClaudeProductDirectorySafely -InstallDirectory $resolvedInstallDirectory) {
    if (-not $Elevated) { Remove-ClaudeUserMaintenanceRegistration -InstallDirectory $resolvedInstallDirectory }
    Write-Host '已卸载 Claude 中文输入法的 TSF profile、COM host、用户启动项和产品文件。'
} else {
    Write-Host '已卸载 Claude 中文输入法的 TSF profile、COM host、用户启动项和可释放的产品文件。'
    Write-Host '仍被其他进程加载的 native 版本已保留；关闭这些宿主后再次运行卸载脚本即可清理。'
}
Write-Host '个人词库、皮肤选择和模式状态已保留：%LOCALAPPDATA%\ClaudeZhInput'
Write-Host '.NET 8 Desktop Runtime 未卸载，因为它可能被其他程序使用。'
if (Test-Path -LiteralPath $resolvedInstallDirectory) {
    Show-ClaudeUninstallNotice '输入法已注销，部分文件仍被应用占用。保存工作并关闭相关应用后，再次运行卸载即可清理。个人数据保留。'
} else { Show-ClaudeUninstallNotice 'Claude 中文输入法已卸载。个人数据保留；需要清除或迁移时请查看使用说明。' }
} catch {
    Show-ClaudeUninstallNotice ('卸载未完成：' + $_.Exception.Message + [Environment]::NewLine + '请保留完整安装包，从“卸载输入法.cmd”重试。')
    throw
}

