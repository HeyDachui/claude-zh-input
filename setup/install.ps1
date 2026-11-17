#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [switch]$NoRuntimeInstall,
    [switch]$NoStart,
    [switch]$RestartTextServices,
    [switch]$Simulate,
    [switch]$Elevated,
    [string]$LogPath,
    [string]$AttemptId
)

$ErrorActionPreference = 'Stop'
$setupRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Split-Path -Parent $setupRoot
. (Join-Path $setupRoot 'setup-common.ps1')
$installerScriptPath = Get-ClaudeFullPath $MyInvocation.MyCommand.Path

function ConvertTo-ClaudeProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value -match '[\s"]') {
        return ('"' + $Value.Replace('"', '\"') + '"')
    }
    return $Value
}

function Start-ClaudeElevatedInstall {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    # The elevated child must never start the WPF singleton.  The original
    # medium-integrity caller starts it after this child returns successfully.
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:installerScriptPath,
        '-Elevated', '-NoStart', '-InstallDirectory', $script:resolvedInstallDirectory,
        '-LogPath', $script:installLogPath, '-AttemptId', $script:ClaudeInstallAttemptId)
    if ($NoRuntimeInstall) { $arguments += '-NoRuntimeInstall' }
    # -NoStart is already forced above so a privileged process cannot own the
    # long-lived tray/pipe service.
    if ($RestartTextServices) { $arguments += '-RestartTextServices' }
    $argumentString = (($arguments | ForEach-Object { ConvertTo-ClaudeProcessArgument ([string]$_) }) -join ' ')
    $script:InstallStep = 'elevated-launch'
    Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope 'invoker' -Step $script:InstallStep -Status 'started' -Message '请求管理员安装子进程。' -InstallDirectory $script:resolvedInstallDirectory
    $child = Start-Process -FilePath $powershell -ArgumentList $argumentString -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope 'invoker' -Step $script:InstallStep -Status 'completed' -Message '管理员安装子进程已返回。' -InstallDirectory $script:resolvedInstallDirectory -ExitCode ([int]$child.ExitCode)
    return [int]$child.ExitCode
}

function Copy-ClaudeTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Copy-ClaudePackageToStage {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$StageRoot
    )
    New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null
    # Nuitka standalone may put DLLs and dependency directories beside
    # worker.exe (for example ctranslate2/, numpy/ and numpy.libs/).  Copy
    # every protected-package root entry so the offline worker keeps all of
    # its own sidecar dependencies.  The source package's setup/ directory is
    # copied separately below from the current installer source.
    foreach ($entry in (Get-ChildItem -LiteralPath $SourceRoot -Force)) {
        if ($entry.Name -ieq 'setup' -or $entry.Name -ieq 'native') { continue }
        if ($entry.PSIsContainer) {
            Copy-ClaudeTree -Source $entry.FullName -Destination (Join-Path $StageRoot $entry.Name)
        } else {
            Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $StageRoot $entry.Name) -Force
        }
    }

    # Keep the maintenance entry points with the installed product.  The
    # runtime installer is intentionally copied too, so a repaired install can
    # remain offline; it is still verified before it is ever executed.
    Copy-ClaudeTree -Source $setupRoot -Destination (Join-Path $StageRoot 'setup')

    # Native COM hosts are immutable release slots.  The active slot is
    # selected by the COM registration, so an already-loaded old slot can stay
    # mapped by Explorer/Chrome/ChatGPT while the new slot is copied and
    # registered beside it.
    $nativeVersionId = Get-ClaudeNativeVersionId -PackageRoot $SourceRoot
    $nativeVersionStage = Get-ClaudeNativeVersionDirectory -InstallDirectory $StageRoot -VersionId $nativeVersionId
    Copy-ClaudeTree -Source (Join-Path $SourceRoot 'native') -Destination $nativeVersionStage
    return [pscustomobject]@{
        NativeVersionId = $nativeVersionId
        NativeDirectory = $nativeVersionStage
    }
}

function Copy-ClaudeNonNativeTree {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )
    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null
    foreach ($entry in (Get-ChildItem -LiteralPath $SourceRoot -Force)) {
        if ($entry.Name -ieq 'native') { continue }
        if ($entry.PSIsContainer) {
            Copy-ClaudeTree -Source $entry.FullName -Destination (Join-Path $DestinationRoot $entry.Name)
        } else {
            Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $DestinationRoot $entry.Name) -Force
        }
    }
}

function Remove-ClaudeNonNativeTree {
    param([Parameter(Mandatory = $true)][string]$ProductRoot)
    foreach ($entry in @(Get-ChildItem -LiteralPath $ProductRoot -Force -ErrorAction SilentlyContinue)) {
        if ($entry.Name -ieq 'native') { continue }
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force
    }
}

function Remove-ClaudeNativeVersion {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$VersionId
    )
    $directory = Get-ClaudeNativeVersionDirectory -InstallDirectory $InstallDirectory -VersionId $VersionId
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { return }
    try {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction Stop
    }
    catch {
        Write-Warning "保留仍被其他进程加载的 native 版本目录：$directory；$($_.Exception.Message)"
    }
}

function Move-ClaudeDirectorySafely {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Get-ClaudeInstallInvocationLocation {
    param([Parameter(Mandatory = $true)]$ErrorRecord)
    $path = [string]$ErrorRecord.InvocationInfo.ScriptName
    if ([String]::IsNullOrWhiteSpace($path) -and $ErrorRecord.InvocationInfo.MyCommand) {
        $path = [string]$ErrorRecord.InvocationInfo.MyCommand.Path
    }
    $line = [int]$ErrorRecord.InvocationInfo.ScriptLineNumber
    if ([String]::IsNullOrWhiteSpace($path)) { return $null }
    if ($line -gt 0) { return ($path + ':' + $line) }
    return $path
}

$script:ClaudeInstallLogDisabled = [bool]$Simulate
$script:ClaudeInstallAttemptId = if ([String]::IsNullOrWhiteSpace($AttemptId)) {
    [Guid]::NewGuid().ToString('N')
} else {
    $AttemptId
}
$script:installLogPath = if ([String]::IsNullOrWhiteSpace($LogPath)) {
    Get-ClaudeInstallLogPath
} else {
    Get-ClaudeFullPath $LogPath
}
$script:InstallStep = 'startup'
$script:InstallFailureStackTrace = $null
$script:InstallFailureLocation = $null
$script:InstallScope = if ($Elevated) {
    'elevated'
} elseif (Test-ClaudeAdministrator) {
    'direct-admin'
} else {
    'invoker'
}

try {
$script:InstallStep = 'resolve-install-directory'
$resolvedInstallDirectory = if ([String]::IsNullOrWhiteSpace($InstallDirectory)) {
    Get-ClaudeDefaultInstallDirectory
} else {
    Get-ClaudeFullPath $InstallDirectory
}
$script:resolvedInstallDirectory = $resolvedInstallDirectory
$script:InstallStep = 'validate-install-directory'
$resolvedInstallDirectory = Assert-ClaudeSafeInstallDirectory -Path $resolvedInstallDirectory -PackageRoot $packageRoot
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64') {
    throw '此安装包仅支持 Windows x64；不支持 32 位 Windows 或 ARM64 原生输入组件。'
}
Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope $script:InstallScope -Step 'process-start' -Status 'started' -Message '开始安装流程。' -InstallDirectory $resolvedInstallDirectory

# Validate the source before any UAC prompt or filesystem mutation.  A source
# containing only a framework shell is deliberately rejected here.
$script:InstallStep = 'validate-protected-package'
Assert-ClaudeProtectedPackage -PackageRoot $packageRoot
$script:InstallStep = 'validate-dotnet-runtime'
$selfContained = Test-ClaudeSelfContainedPackage -PackageRoot $packageRoot
$runtimeInstaller = $null
if (-not $selfContained) {
    $runtimeManifest = Get-ClaudeRuntimeManifest -PackageRoot $packageRoot
    $runtimeInstaller = Assert-ClaudeRuntimeInstaller -PackageRoot $packageRoot -Manifest $runtimeManifest
}
$script:InstallStep = 'validate-vc-runtime'
$vcManifest = Get-ClaudeVCRuntimeManifest -PackageRoot $packageRoot
$vcInstaller = Assert-ClaudeVCRuntimeInstaller -PackageRoot $packageRoot -Manifest $vcManifest

if ($Simulate) {
    $simulatedNativeVersionId = Get-ClaudeNativeVersionId -PackageRoot $packageRoot
    Write-Host "[模拟] 分享包校验通过：$packageRoot"
    Write-Host "[模拟] 目标目录：$resolvedInstallDirectory"
    Write-Host "[模拟] native 将登记为不可变版本：native\versions\$simulatedNativeVersionId"
    if ($selfContained) {
        Write-Host '[模拟] 应用已带完整自包含运行时，无需额外安装 .NET。'
    } elseif (Test-ClaudeDesktopRuntimePresent) {
        Write-Host '[模拟] 已检测到 .NET 8 x64 Desktop Runtime，将跳过运行时安装。'
    } elseif ($NoRuntimeInstall) {
        Write-Host '[模拟] 未检测到 .NET 8 x64 Desktop Runtime；-NoRuntimeInstall 会使真实安装失败。'
    } else {
        Write-Host "[模拟] 未检测到 .NET 8 x64 Desktop Runtime，将使用已校验安装器：$runtimeInstaller"
    }
    if (Test-ClaudeVCRuntimePresent) {
        Write-Host '[模拟] 已检测到 VC14 x64 Runtime，将跳过 VC++ Runtime 安装。'
    } else {
        Write-Host "[模拟] 未检测到 VC14 x64 Runtime，将使用已校验安装器：$vcInstaller"
    }
    if (Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container) {
        [void](Assert-ClaudeOwnedInstallDirectory $resolvedInstallDirectory)
        Write-Host '[模拟] 已存在目录可识别为本产品，升级会先暂存旧目录。'
    } else {
        Write-Host '[模拟] 目标目录尚不存在，将按首次安装处理。'
    }
    Write-Host '[模拟] 不写注册表、不写用户状态、不复制文件、不注册 TSF、不启动服务。'
    exit 0
}

if (-not (Test-ClaudeAdministrator)) {
    $serviceWasRunningBeforeElevation = $false
    try {
        if (Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container) {
            $serviceWasRunningBeforeElevation = @(
                Get-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory
            ).Count -gt 0
        }
    }
    catch { $serviceWasRunningBeforeElevation = $false }
    $elevatedExitCode = Start-ClaudeElevatedInstall
    if ($elevatedExitCode -ne 0) {
        # The elevated child stops the old product before staging an upgrade,
        # but it must never own the long-lived user service.  Restore it only
        # in the original user process, and only when the rollback left the
        # product executable available and no matching service is running.
        if ($serviceWasRunningBeforeElevation -and
            (Test-Path -LiteralPath (Join-Path $resolvedInstallDirectory 'app\ClaudeZhInput.exe') -PathType Leaf) -and
            (@(Get-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory).Count -eq 0)) {
            try {
                $script:InstallStep = 'restore-user-service'
                Start-ClaudeProduct -InstallDirectory $resolvedInstallDirectory
                Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope 'invoker' -Step $script:InstallStep -Status 'completed' -Message '管理员安装失败回滚后，已由原始用户权限恢复输入法服务。' -InstallDirectory $resolvedInstallDirectory
            }
            catch {
                Write-Warning "管理员安装失败回滚后无法恢复输入法服务：$($_.Exception.Message)"
            }
        }
        $detail = Get-ClaudeLastInstallFailure -LogPath $script:installLogPath -AttemptId $script:ClaudeInstallAttemptId
        if ([String]::IsNullOrWhiteSpace($detail)) {
            $detail = "管理员安装子进程退出码 $elevatedExitCode。"
        }
        Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope 'invoker' -Step 'elevated-result' -Status 'failed' -Message $detail -InstallDirectory $resolvedInstallDirectory -ExitCode $elevatedExitCode
        Write-Host ('安装失败：' + $detail)
        Write-Host ('详细日志：' + $script:installLogPath)
        exit $elevatedExitCode
    }
    # HKCU language/TIP state must be changed by the original medium-integrity
    # user.  The elevated child deliberately skips this step so a different
    # elevated profile can never receive the product TIP.
    $script:InstallStep = 'user-language-list'
    [void](Ensure-ClaudeUserLanguageTip)
    [void](Initialize-ClaudeUserState)
    [void](Ensure-ClaudeStartupRegistration -InstallDirectory $resolvedInstallDirectory)
    Ensure-ClaudeUserMaintenanceRegistration -InstallDirectory $resolvedInstallDirectory
    Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope 'invoker' -Step $script:InstallStep -Status 'completed' -Message '已在原始用户语言列表登记本产品 TIP。' -InstallDirectory $resolvedInstallDirectory
    if (-not $NoStart) {
        $script:InstallStep = 'start-user-service'
        Start-ClaudeProduct -InstallDirectory $resolvedInstallDirectory -Settings
        Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope 'invoker' -Step $script:InstallStep -Status 'completed' -Message '已由原始用户权限启动输入法服务。' -InstallDirectory $resolvedInstallDirectory
        Write-Host '已由原始用户权限启动 Claude 中文输入法服务。'
    }
    exit 0
}

$script:InstallStep = 'dotnet-runtime-check'
if (-not $selfContained -and -not (Test-ClaudeDesktopRuntimePresent)) {
    if ($NoRuntimeInstall) {
        throw '未检测到 .NET 8 x64 Desktop Runtime，且指定了 -NoRuntimeInstall。'
    }
    $script:InstallStep = 'dotnet-runtime-install'
    Install-ClaudeDesktopRuntime -InstallerPath $runtimeInstaller
    $script:InstallStep = 'dotnet-runtime-verify'
    if (-not (Test-ClaudeDesktopRuntimePresent)) {
        throw '运行时安装器已返回，但仍未检测到 .NET 8 x64 Desktop Runtime；请重启后重试。'
    }
}

# The protected worker is started only after both offline prerequisites have
# been checked/installed.  VC14 is shared by the Nuitka worker and is never
# removed by uninstall.ps1.
$script:InstallStep = 'vc-runtime-check'
if (-not (Test-ClaudeVCRuntimePresent)) {
    $script:InstallStep = 'vc-runtime-install'
    Install-ClaudeVCRuntime -InstallerPath $vcInstaller
    $script:InstallStep = 'vc-runtime-verify'
    if (-not (Test-ClaudeVCRuntimePresent)) {
        throw 'VC++ Runtime 安装器已返回，但仍未检测到 VC14 x64 Runtime；请重启后重试。'
    }
}

$script:InstallStep = 'validate-existing-install'
$hadExistingInstall = Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container
if ($hadExistingInstall) { [void](Assert-ClaudeOwnedInstallDirectory $resolvedInstallDirectory) }
$existingMarker = $null
$previousNativeDirectory = $null
if ($hadExistingInstall) {
    $existingMarkerPath = Get-ClaudeInstallMarkerPath $resolvedInstallDirectory
    if (Test-Path -LiteralPath $existingMarkerPath -PathType Leaf) {
        try {
            $existingMarker = Get-Content -LiteralPath $existingMarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            throw "安装标记无法读取，未覆盖目录：$existingMarkerPath"
        }
    }
    $previousNativeDirectory = Get-ClaudeInstalledNativeDirectory -InstallDirectory $resolvedInstallDirectory -Marker $existingMarker
}

$stageDirectory = New-ClaudeControlledTempDirectory
$backupDirectory = $null
$backupComplete = $false
$preserveBackup = $false
$newInstallActive = $false
$overlayApplied = $false
$startupCommand = $null
$nativeAttempted = $false
$nativeCopyStarted = $false
$nativeVersionCreated = $false
$startupChanged = $false
$nativeVersionId = $null
$nativeDirectory = $null
try {
    $script:InstallStep = 'stage-copy'
    $stageInfo = Copy-ClaudePackageToStage -SourceRoot $packageRoot -StageRoot $stageDirectory
    $nativeVersionId = [string]$stageInfo.NativeVersionId

    $script:InstallStep = 'stop-existing-process'
    Stop-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory

    if ($hadExistingInstall) {
        # Keep the rollback outside the product parent so the only directory
        # removed during cleanup is an explicitly named, controlled temp path.
        # The old native tree is deliberately excluded: Explorer/Chrome/ChatGPT
        # may have the legacy COM image mapped from that directory.
        $backupDirectory = New-ClaudeControlledTempDirectory
        $script:InstallStep = 'backup-existing-install'
        Copy-ClaudeNonNativeTree -SourceRoot $resolvedInstallDirectory -DestinationRoot $backupDirectory
        $backupComplete = $true
        $script:InstallStep = 'overlay-install-directory'
        # Replace all mutable product files so removed release files cannot
        # survive an upgrade.  The immutable native tree is intentionally
        # excluded; old COM images may still be mapped from it.
        $overlayApplied = $true
        Remove-ClaudeNonNativeTree -ProductRoot $resolvedInstallDirectory
        Copy-ClaudeNonNativeTree -SourceRoot $stageDirectory -DestinationRoot $resolvedInstallDirectory
        $nativeDirectory = Get-ClaudeNativeVersionDirectory -InstallDirectory $resolvedInstallDirectory -VersionId $nativeVersionId
        if (-not (Test-Path -LiteralPath $nativeDirectory -PathType Container)) {
            $nativeCopyStarted = $true
            Copy-ClaudeTree -Source $stageInfo.NativeDirectory -Destination $nativeDirectory
            $nativeVersionCreated = $true
        }
        else {
            $requiredNewComHost = Join-Path $nativeDirectory 'ClaudeZhInput.NativeCandidate.dll'
            if (-not (Test-Path -LiteralPath $requiredNewComHost -PathType Leaf)) {
                throw "native 版本目录已存在但不完整，拒绝覆盖已存在路径：$nativeDirectory"
            }
        }
    }
    else {
        $script:InstallStep = 'replace-install-directory'
        Move-ClaudeDirectorySafely -Source $stageDirectory -Destination $resolvedInstallDirectory
        $stageDirectory = $null
        $newInstallActive = $true
        $nativeDirectory = Get-ClaudeNativeVersionDirectory -InstallDirectory $resolvedInstallDirectory -VersionId $nativeVersionId
        $nativeVersionCreated = $true
    }

    $script:InstallStep = 'initialize-user-state'
    if (-not $Elevated) { [void](Initialize-ClaudeUserState) }
    $script:InstallStep = 'startup-registration'
    $startupCommand = Get-ClaudeStartupCommand -InstallDirectory $resolvedInstallDirectory
    if (-not $Elevated) {
        $startupCommand = Ensure-ClaudeStartupRegistration -InstallDirectory $resolvedInstallDirectory
        $startupChanged = $true
    }

    $script:InstallStep = 'native-registration'
    $nativeAttempted = $true
    Invoke-ClaudeNativeInstall -InstallDirectory $resolvedInstallDirectory -NativeDirectory $nativeDirectory -RestartTextServices:$RestartTextServices

    $script:InstallStep = 'write-install-marker'
    Write-ClaudeInstallMarker -InstallDirectory $resolvedInstallDirectory -SourcePackageRoot $packageRoot -StartupCommand $startupCommand -NativeDirectory $nativeDirectory -NativeVersionId $nativeVersionId

    # A directly elevated invocation is already running in the caller's user
    # profile.  Internal -Elevated children skip this and let their original
    # medium-integrity invoker perform the user-language update above.
    if (-not $Elevated) {
        $script:InstallStep = 'user-language-list'
        [void](Ensure-ClaudeUserLanguageTip)
        Ensure-ClaudeUserMaintenanceRegistration -InstallDirectory $resolvedInstallDirectory
        Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope $script:InstallScope -Step $script:InstallStep -Status 'completed' -Message '已在当前用户语言列表登记本产品 TIP。' -InstallDirectory $resolvedInstallDirectory
    }

    if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
        $script:InstallStep = 'remove-old-install-backup'
        Remove-ClaudeControlledTempDirectory -Path $backupDirectory
        $backupDirectory = $null
    }

    # A directly elevated invocation also leaves the long-lived process to an
    # ordinary user launch.  The normal one-click path reaches this branch in
    # the elevated child with -NoStart; the parent starts the service above.
    if (-not $NoStart -and -not (Test-ClaudeAdministrator)) {
        Start-ClaudeProduct -InstallDirectory $resolvedInstallDirectory
    }
    $script:InstallStep = 'complete'
    Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope $script:InstallScope -Step $script:InstallStep -Status 'completed' -Message '安装流程完成。' -InstallDirectory $resolvedInstallDirectory
    Write-Host "已安装 Claude 中文输入法：$resolvedInstallDirectory"
    Write-Host '默认模式为普通中文；不会替换系统默认输入法。开机服务已登记为当前用户启动项。'
    Write-Host '需要切换模式或更换皮肤时运行 setup\start.ps1 -Settings。'
}
catch {
    $failure = $_.Exception.Message
    $rollbackComplete = $true
    $script:InstallFailureStackTrace = [string]$_.ScriptStackTrace
    $script:InstallFailureLocation = Get-ClaudeInstallInvocationLocation $_
    try { Stop-ClaudeProductProcesses -InstallDirectory $resolvedInstallDirectory } catch { }

    if ($hadExistingInstall -and $overlayApplied -and $backupComplete) {
        if ($nativeAttempted -and $nativeDirectory) {
            try { Invoke-ClaudeNativeUninstall -InstallDirectory $resolvedInstallDirectory -NativeDirectory $nativeDirectory } catch { Write-Warning "新版本 TSF 注销失败：$($_.Exception.Message)" }
            if ($previousNativeDirectory -and (Test-Path -LiteralPath $previousNativeDirectory -PathType Container)) {
                try { Invoke-ClaudeNativeInstall -InstallDirectory $resolvedInstallDirectory -NativeDirectory $previousNativeDirectory } catch { $rollbackComplete = $false; Write-Warning "旧版本 TSF 注册恢复失败：$($_.Exception.Message)" }
            }
        }
        if ($nativeVersionCreated -or $nativeCopyStarted) {
            Remove-ClaudeNativeVersion -InstallDirectory $resolvedInstallDirectory -VersionId $nativeVersionId
        }
        if ($backupDirectory -and (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
            try {
                Remove-ClaudeNonNativeTree -ProductRoot $resolvedInstallDirectory
                Copy-ClaudeNonNativeTree -SourceRoot $backupDirectory -DestinationRoot $resolvedInstallDirectory
            }
            catch { $rollbackComplete = $false; Write-Warning "旧版本产品文件恢复失败：$($_.Exception.Message)" }
        }
    }
    elseif ($newInstallActive -and (Test-Path -LiteralPath $resolvedInstallDirectory -PathType Container)) {
        if ($nativeAttempted) {
            try { Invoke-ClaudeNativeUninstall -InstallDirectory $resolvedInstallDirectory -NativeDirectory $nativeDirectory } catch { Write-Warning "清理本次 TSF 注册失败：$($_.Exception.Message)" }
        }
        if ($startupChanged) {
            try { Remove-ClaudeStartupRegistration -InstallDirectory $resolvedInstallDirectory -RecordedCommand $startupCommand } catch { }
        }
        try { Remove-Item -LiteralPath $resolvedInstallDirectory -Recurse -Force }
        catch { Write-Warning "新安装目录中仍有文件被加载，已保留以便下次清理：$($_.Exception.Message)" }
    }
    if (-not $rollbackComplete -and $backupDirectory) {
        $preserveBackup = $true
        throw "安装未完成，自动回滚也未全部完成。旧版本备份已保留：$backupDirectory。请保留该目录并附安装日志反馈。原错误：$failure"
    }
    throw "安装未完成，已执行回滚处理，请查看日志并重新运行安装。原错误：$failure"
}
finally {
    if ($stageDirectory -and (Test-Path -LiteralPath $stageDirectory)) {
        Remove-ClaudeControlledTempDirectory -Path $stageDirectory
    }
    if (-not $preserveBackup -and $backupDirectory -and (Test-Path -LiteralPath $backupDirectory)) {
        Remove-ClaudeControlledTempDirectory -Path $backupDirectory
    }
}
}
catch {
    $message = $_.Exception.Message
    $logDirectory = if ($resolvedInstallDirectory) { $resolvedInstallDirectory } else { '' }
    $stackTrace = if (-not [String]::IsNullOrWhiteSpace($script:InstallFailureStackTrace)) {
        $script:InstallFailureStackTrace
    } else {
        [string]$_.ScriptStackTrace
    }
    $location = if (-not [String]::IsNullOrWhiteSpace($script:InstallFailureLocation)) {
        $script:InstallFailureLocation
    } else {
        Get-ClaudeInstallInvocationLocation $_
    }
    Write-ClaudeInstallLogEntry -LogPath $script:installLogPath -Scope $script:InstallScope -Step $script:InstallStep -Status 'failed' -Message $message -InstallDirectory $logDirectory -ScriptStackTrace $stackTrace -InvocationLocation $location
    Write-Host ('安装失败：' + $message)
    Write-Host ('详细日志：' + $script:installLogPath)
    throw
}

