#requires -Version 5.1

# Shared, Windows PowerShell 5.1-compatible helpers for the Claude 中文输入法
# installer, launcher, and uninstaller.  This file has no side effects when it
# is dot-sourced; callers decide when to write files or start processes.

function Get-ClaudeProductId {
    return 'ClaudeZhInput'
}

function Get-ClaudeStartupValueName {
    return 'ClaudeZhInput.UserService'
}

function Get-ClaudeDataRoot {
    return (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClaudeZhInput')
}

function Get-ClaudeInstallLogPath {
    return (Join-Path (Get-ClaudeDataRoot) 'install-log')
}

function Write-ClaudeInstallLogEntry {
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$Scope,
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)][ValidateSet('started', 'ok', 'skipped', 'failed', 'completed')][string]$Status,
        [string]$Message,
        [string]$InstallDirectory,
        [int]$ExitCode,
        [string]$AttemptId,
        [string]$ScriptStackTrace,
        [string]$InvocationLocation
    )
    # Logging must never turn a recoverable install result into a second
    # failure.  The caller controls whether simulation mode enables logging.
    if ($script:ClaudeInstallLogDisabled) { return }
    try {
        $parent = Split-Path -Parent (Get-ClaudeFullPath $LogPath)
        if (-not [String]::IsNullOrWhiteSpace($parent)) {
            New-Item -ItemType Directory -Force -Path $parent | Out-Null
        }
        $admin = $false
        try { $admin = [bool](Test-ClaudeAdministrator) } catch { }
        $entry = [ordered]@{
            logVersion = 1
            timestampUtc = (Get-Date).ToUniversalTime().ToString('o')
            scope = $Scope
            step = $Step
            status = $Status
            isAdministrator = $admin
            message = if ($null -eq $Message) { '' } else { [string]$Message }
        }
        $effectiveAttemptId = if (-not [String]::IsNullOrWhiteSpace($AttemptId)) {
            $AttemptId
        } elseif ($script:ClaudeInstallAttemptId) {
            [string]$script:ClaudeInstallAttemptId
        } else {
            ''
        }
        if (-not [String]::IsNullOrWhiteSpace($effectiveAttemptId)) {
            $entry.attemptId = $effectiveAttemptId
        }
        if (-not [String]::IsNullOrWhiteSpace($InstallDirectory)) {
            $entry.installDirectory = Get-ClaudeFullPath $InstallDirectory
        }
        if ($PSBoundParameters.ContainsKey('ExitCode')) {
            $entry.exitCode = $ExitCode
        }
        if (-not [String]::IsNullOrWhiteSpace($ScriptStackTrace)) {
            $entry.scriptStackTrace = $ScriptStackTrace
        }
        if (-not [String]::IsNullOrWhiteSpace($InvocationLocation)) {
            $entry.invocationLocation = $InvocationLocation
        }
        $line = $entry | ConvertTo-Json -Compress -Depth 5
        [IO.File]::AppendAllText(
            (Get-ClaudeFullPath $LogPath),
            ($line + [Environment]::NewLine),
            (New-Object Text.UTF8Encoding($false)))
    }
    catch {
        # The log lives in the user's profile.  ACL, profile, or disk errors
        # must not hide the actual installer exception.
    }
}

function Get-ClaudeLastInstallFailure {
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [string]$AttemptId
    )
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { return $null }
    try {
        $lines = @(Get-Content -LiteralPath $LogPath -Encoding UTF8 -Tail 80 -ErrorAction Stop)
        for ($index = $lines.Count - 1; $index -ge 0; $index--) {
            if ([String]::IsNullOrWhiteSpace([string]$lines[$index])) { continue }
            try { $entry = [string]$lines[$index] | ConvertFrom-Json } catch { continue }
            if ([string]$entry.status -ne 'failed') { continue }
            if (-not [String]::IsNullOrWhiteSpace($AttemptId) -and
                [string]$entry.attemptId -ne $AttemptId) { continue }
            $step = [string]$entry.step
            $message = [string]$entry.message
            if ([String]::IsNullOrWhiteSpace($message)) { $message = '管理员安装步骤失败。' }
            if ([String]::IsNullOrWhiteSpace($step)) { return $message }
            $labels = @{
                'resolve-install-directory' = '解析安装目录'
                'validate-install-directory' = '校验安装目录'
                'validate-protected-package' = '校验保护包'
                'validate-dotnet-runtime' = '校验 .NET Runtime'
                'validate-vc-runtime' = '校验 VC++ Runtime'
                'elevated-launch' = '启动管理员安装子进程'
                'elevated-result' = '管理员安装结果'
                'dotnet-runtime-check' = '检查 .NET Runtime'
                'dotnet-runtime-install' = '安装 .NET Runtime'
                'dotnet-runtime-verify' = '验证 .NET Runtime'
                'vc-runtime-check' = '检查 VC++ Runtime'
                'vc-runtime-install' = '安装 VC++ Runtime'
                'vc-runtime-verify' = '验证 VC++ Runtime'
                'validate-existing-install' = '检查已有安装'
                'stage-copy' = '复制安装暂存目录'
                'stop-existing-process' = '停止旧服务进程'
                'backup-existing-install' = '暂存旧版本'
                'overlay-install-directory' = '更新未锁定的产品文件'
                'replace-install-directory' = '替换安装目录'
                'initialize-user-state' = '初始化用户状态'
                'startup-registration' = '登记当前用户启动项'
                'user-language-list' = '登记当前用户语言列表'
                'user-language-list-remove' = '移除当前用户语言列表中的本产品 TIP'
                'native-registration' = '注册 TSF 输入法'
                'write-install-marker' = '写入安装标记'
                'remove-old-install-backup' = '清理旧版本暂存'
                'start-user-service' = '启动当前用户服务'
                'restore-user-service' = '恢复当前用户服务'
            }
            $displayStep = if ($labels.ContainsKey($step)) { [string]$labels[$step] } else { $step }
            return ($displayStep + '：' + $message)
        }
    }
    catch { return $null }
    return $null
}

function Get-ClaudeDefaultInstallDirectory {
    $local = [Environment]::GetFolderPath('LocalApplicationData')
    return (Join-Path (Join-Path $local 'Programs') 'ClaudeZhInput')
}

function Get-ClaudeFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

function Test-ClaudePathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Child,
        [Parameter(Mandatory = $true)][string]$Parent
    )
    $childFull = (Get-ClaudeFullPath $Child).TrimEnd('\')
    $parentFull = (Get-ClaudeFullPath $Parent).TrimEnd('\')
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    return $childFull.StartsWith($parentFull + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ClaudeSafeInstallDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$PackageRoot
    )

    $full = Get-ClaudeFullPath $Path
    $root = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full.TrimEnd('\').Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "安装目录不能是磁盘根目录：$full"
    }

    $blockedRoots = @(
        $env:WINDIR,
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'),
        [Environment]::GetFolderPath('CommonApplicationData'),
        [Environment]::GetFolderPath('CommonProgramFiles'),
        [Environment]::GetFolderPath('CommonProgramFilesX86')
    ) | Where-Object { -not [String]::IsNullOrWhiteSpace($_) }
    foreach ($blocked in $blockedRoots) {
        if (Test-ClaudePathWithin -Child $full -Parent $blocked) {
            throw "安装目录位于系统或公共程序目录中，拒绝写入：$full"
        }
    }

    if ($PackageRoot) {
        $packageFull = Get-ClaudeFullPath $PackageRoot
        if ((Test-ClaudePathWithin -Child $full -Parent $packageFull) -or
            (Test-ClaudePathWithin -Child $packageFull -Parent $full)) {
            throw "安装目录不能与分享包目录重叠：$full / $packageFull"
        }
    }
    return $full
}

function Test-ClaudeAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ClaudeNativeBinaryRelativePath {
    return 'native\bin\Release\net8.0-windows\win-x64'
}

function Get-ClaudeNativeRegistrationRelativePath {
    return 'native\TsfRegistration.Tool\bin\Release\net8.0-windows\win-x64\ClaudeZhInput.TsfRegistration.exe'
}

function Get-ClaudeNativeComClsid {
    return '{6f44d5fb-4e6d-4da0-9d8c-3e0ec2fa4ee8}'
}

function Get-ClaudeNativeComRegistrationPath {
    $baseKey = $null
    $inprocKey = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $keyPath = 'Software\Classes\CLSID\' + (Get-ClaudeNativeComClsid) + '\InprocServer32'
        $inprocKey = $baseKey.OpenSubKey($keyPath, $false)
        if ($null -eq $inprocKey) { return $null }
        $value = $inprocKey.GetValue('', $null)
        if ($null -eq $value) { return $null }
        return [string]$value
    }
    finally {
        if ($inprocKey) { $inprocKey.Dispose() }
        if ($baseKey) { $baseKey.Dispose() }
    }
}

function Set-ClaudeNativeComRegistration {
    param([Parameter(Mandatory = $true)][string]$ComHostPath)

    $comHostFull = Get-ClaudeFullPath $ComHostPath
    if (-not (Test-Path -LiteralPath $comHostFull -PathType Leaf)) {
        throw "找不到待登记的 native COM host：$comHostFull"
    }

    $baseKey = $null
    $clsidKey = $null
    $inprocKey = $null
    try {
        # regsvr32 is still invoked by native/install.ps1.  This explicit,
        # 64-bit verification write handles hosts that leave an old COM path
        # in HKLM after a side-by-side DLL upgrade; it touches only this fixed
        # product CLSID and never replaces another COM registration.
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $clsidPath = 'Software\Classes\CLSID\' + (Get-ClaudeNativeComClsid)
        $clsidKey = $baseKey.CreateSubKey($clsidPath)
        if ($null -eq $clsidKey) { throw "无法打开 Claude 中文输入法 CLSID 注册项：$clsidPath" }
        if ([String]::IsNullOrWhiteSpace([string]$clsidKey.GetValue('', $null))) {
            $clsidKey.SetValue('', 'ClaudeZhInput.Tsf.ClaudeTextService', [Microsoft.Win32.RegistryValueKind]::String)
        }
        $inprocKey = $clsidKey.CreateSubKey('InprocServer32')
        if ($null -eq $inprocKey) { throw '无法打开 Claude 中文输入法 COM InprocServer32 注册项。' }
        $inprocKey.SetValue('', $comHostFull, [Microsoft.Win32.RegistryValueKind]::String)
        $inprocKey.SetValue('ThreadingModel', 'Both', [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        if ($inprocKey) { $inprocKey.Dispose() }
        if ($clsidKey) { $clsidKey.Dispose() }
        if ($baseKey) { $baseKey.Dispose() }
    }

    $registered = Get-ClaudeNativeComRegistrationPath
    if (-not $registered -or
        -not ([string]$registered).Equals($comHostFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "native COM host 注册路径校验失败：期望 $comHostFull，实际 $registered"
    }
}

function Remove-ClaudeNativeComRegistration {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    $installFull = Get-ClaudeFullPath $InstallDirectory
    $registered = Get-ClaudeNativeComRegistrationPath
    if ([String]::IsNullOrWhiteSpace($registered)) { return $false }
    $registeredFull = Get-ClaudeFullPath $registered
    if (-not (Test-ClaudePathWithin -Child $registeredFull -Parent $installFull)) {
        # Never remove a registration that no longer points into this product.
        return $false
    }

    $baseKey = $null
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $clsidPath = 'Software\Classes\CLSID\' + (Get-ClaudeNativeComClsid)
        $baseKey.DeleteSubKeyTree($clsidPath, $false)
    }
    finally {
        if ($baseKey) { $baseKey.Dispose() }
    }
    return $true
}

function Get-ClaudeNativeVersionId {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $nativeRoot = Join-Path (Get-ClaudeFullPath $PackageRoot) 'native'
    if (-not (Test-Path -LiteralPath $nativeRoot -PathType Container)) {
        throw "保护包缺少 native 目录：$nativeRoot"
    }

    # Hash every native payload file, including the registration scripts and
    # profile tool.  A changed COM host or helper therefore gets a new path;
    # an already-loaded old image is never overwritten in place.
    $hashLines = New-Object Collections.Generic.List[string]
    $nativeRootFull = (Get-ClaudeFullPath $nativeRoot).TrimEnd('\')
    $files = @(Get-ChildItem -LiteralPath $nativeRootFull -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "native 目录为空：$nativeRootFull" }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($nativeRootFull.Length).TrimStart('\').Replace('\', '/')
        $fileHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashLines.Add($relative + '|' + $fileHash)
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($hashLines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString($sha.ComputeHash($payload)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    return ('v-' + $digest.Substring(0, 24))
}

function Get-ClaudeNativeVersionDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$VersionId
    )
    if ($VersionId -notmatch '^v-[0-9a-f]{24}$') {
        throw "native 版本标识格式无效：$VersionId"
    }
    return (Join-Path (Get-ClaudeFullPath $InstallDirectory) ('native\versions\' + $VersionId))
}

function Get-ClaudeInstalledNativeDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        $Marker
    )
    $installFull = Get-ClaudeFullPath $InstallDirectory
    # Repair installs may be newer than the original setup marker. Trust the
    # registered native DLL only after checking it stays inside this product.
    $activeRimeKey = Get-Item -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CLSID\{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}\InprocServer32' -ErrorAction SilentlyContinue
    if ($activeRimeKey) {
        $activeRimeDll = [string]$activeRimeKey.GetValue('')
        if ($activeRimeDll -and (Test-ClaudePathWithin -Child $activeRimeDll -Parent $installFull) -and
            (Test-Path -LiteralPath $activeRimeDll -PathType Leaf)) { return (Split-Path -Parent $activeRimeDll) }
    }
    if ($Marker -and -not [String]::IsNullOrWhiteSpace([string]$Marker.nativeDirectory)) {
        $candidate = [string]$Marker.nativeDirectory
        $candidateFull = if ([IO.Path]::IsPathRooted($candidate)) {
            Get-ClaudeFullPath $candidate
        } else {
            Get-ClaudeFullPath (Join-Path $installFull $candidate)
        }
        if (-not (Test-ClaudePathWithin -Child $candidateFull -Parent $installFull)) {
            throw "安装标记中的 native 目录越出产品目录：$candidateFull"
        }
        if (Test-Path -LiteralPath $candidateFull -PathType Container) {
            return $candidateFull
        }
    }
    if ($Marker -and -not [String]::IsNullOrWhiteSpace([string]$Marker.nativeVersionId)) {
        $versionCandidate = Get-ClaudeNativeVersionDirectory -InstallDirectory $installFull -VersionId ([string]$Marker.nativeVersionId)
        if (Test-Path -LiteralPath $versionCandidate -PathType Container) {
            return $versionCandidate
        }
    }

    # Legacy installs used native\bin directly.  Keep this fallback so an
    # upgrade can register the new version while retaining the old loaded DLL.
    $legacy = Join-Path $installFull 'native'
    if ((Test-Path -LiteralPath (Join-Path $legacy 'install.ps1') -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $legacy 'bin\Release\net8.0-windows\win-x64\ClaudeZhInput.Tsf.comhost.dll') -PathType Leaf)) {
        return $legacy
    }
    $versionRoot = Join-Path $legacy 'versions'
    if (Test-Path -LiteralPath $versionRoot -PathType Container) {
        $latest = @(Get-ChildItem -LiteralPath $versionRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        if ($latest.Count -eq 1) { return $latest[0].FullName }
    }
    throw "找不到已安装的 native 版本目录：$installFull"
}

function Get-ClaudeRequiredPackageFiles {
    return @(
        'BUILD-INFO.json',
        'app\ClaudeZhInput.exe',
        'app\ClaudeZhInput.deps.json',
        'app\ClaudeZhInput.runtimeconfig.json',
        'worker.exe',
        'phrases.dat',
        'commands\claude-commands.json',
        'native\ClaudeZhInput.NativeCandidate.dll',
        'native\ClaudeZhInput.NativeCandidate.pipe',
        'app\ClaudeZhInput.Rime.dll',
        'app\runtime\rime.dll',
        'native\install.ps1',
        'native\uninstall.ps1',
        'licenses\THIRD_PARTY_MANIFEST.txt'
    )
}

function Test-ClaudeSelfContainedPackage {
    param([Parameter(Mandatory=$true)][string]$PackageRoot)
    $info = Get-Content -LiteralPath (Join-Path $PackageRoot 'BUILD-INFO.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($info.appRuntime -ne 'self-contained-net8-windows-x64') { return $false }
    $config = Get-Content -LiteralPath (Join-Path $PackageRoot 'app\ClaudeZhInput.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
    if ('Microsoft.NETCore.App' -notin $frameworks -or 'Microsoft.WindowsDesktop.App' -notin $frameworks) {
        throw '安装包没有完整声明自包含桌面运行时。'
    }
    foreach ($name in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationNative_cor3.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot ('app\'+$name)) -PathType Leaf)) {
            throw ('自包含运行时缺少：'+$name)
        }
    }
    return $true
}

function Assert-ClaudePrimaryTranslation {
    param([string]$PackageRoot, $BuildInfo)
    if ([version]$BuildInfo.version -lt [version]'1.3.0') { return }
    $translation = $BuildInfo.primaryTranslation
    if (-not $translation -or $translation.transport -ne 'private-stdio' -or -not $translation.cpuOnly) {
        throw '安装包缺少本地整句翻译信息，请重新完整解压。'
    }
    $model = $translation.model
    if ($model.file -ne 'models/translation-llm/model.gguf' -or $model.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw '翻译模型清单不完整，请重新取得完整安装包。'
    }
    $modelPath = Join-Path $PackageRoot $model.file
    if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $PackageRoot 'PROGRAM-PACK.txt') -PathType Leaf)) {
        throw '这是程序分包。请先把配套模型包完整解压到程序目录旁，运行“组合模型包.cmd”，组合成功后再安装。现用输入法未因此检查而更换。'
    }
    if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf) -or
        (Get-Item -LiteralPath $modelPath).Length -ne [long]$model.bytes -or
        (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $model.sha256) {
        throw '翻译模型缺失或校验失败，请重新完整解压安装包；现用输入法未因此检查而更换。'
    }
    $seen = @{}
    foreach ($file in $translation.files) {
        if ($file.file -notmatch '^translation-runtime/(ClaudeZhInput\.Translate\.exe|llama\.dll|ggml\.dll|ggml-base\.dll|ggml-cpu-[a-z0-9]+\.dll|libomp\.dll)$' -or
            $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $seen.ContainsKey([string]$file.file)) {
            throw '翻译运行库清单格式不正确。'
        }
        $seen[[string]$file.file] = $true
        $path = Join-Path $PackageRoot $file.file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw '翻译运行库缺失或校验失败，请重新完整解压安装包。'
        }
    }
    foreach ($required in @('ClaudeZhInput.Translate.exe','llama.dll','ggml.dll','ggml-base.dll','libomp.dll')) {
        if (-not $seen.ContainsKey('translation-runtime/' + $required)) { throw '安装包没有列出完整翻译运行库。' }
    }
    if (-not @($seen.Keys | Where-Object { $_ -like 'translation-runtime/ggml-cpu-*.dll' }).Count) {
        throw '安装包没有 CPU 翻译组件。'
    }
}

function Assert-ClaudeProtectedPackage {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $root = Get-ClaudeFullPath $PackageRoot
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "保护包目录不存在：$root"
    }
    $missing = @()
    foreach ($relative in (Get-ClaudeRequiredPackageFiles)) {
        $candidate = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $missing += $relative
        }
    }
    if ($missing.Count -gt 0) {
        throw ("保护包不完整，缺少核心文件：" + ($missing -join '、') +
            '。请先完成 release-tools/protect-build.ps1，再组装分享包。')
    }

    $sizeChecks = @(
        @{ Relative = 'app\ClaudeZhInput.exe'; Minimum = 10240 },
        @{ Relative = 'worker.exe'; Minimum = 1024 },
        @{ Relative = 'phrases.dat'; Minimum = 128 }
    )
    foreach ($check in $sizeChecks) {
        $file = Get-Item -LiteralPath (Join-Path $root $check.Relative)
        if ($file.Length -lt $check.Minimum) {
            throw "保护包核心文件大小异常：$($check.Relative)（$($file.Length) 字节）。"
        }
    }
    $buildInfo = Get-Content -LiteralPath (Join-Path $root 'BUILD-INFO.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($buildInfo.workerProfile -eq 'stdlib-llm-v1') {
        if (-not (Test-ClaudeSelfContainedPackage -PackageRoot $root)) { throw '轻量包必须带完整自包含运行时。' }
        foreach ($legacy in @('models\translate-zh_en-1_9','ctranslate2.dll','ctranslate2','numpy','sentencepiece')) {
            if (Test-Path -LiteralPath (Join-Path $root $legacy)) { throw '轻量包仍含未使用的旧翻译组件。' }
        }
    } else {
        $legacy = Join-Path $root 'models\translate-zh_en-1_9\model\model.bin'
        if (-not (Test-Path -LiteralPath $legacy -PathType Leaf) -or (Get-Item -LiteralPath $legacy).Length -lt 1048576) {
            throw '旧版保护包缺少其配套模型。'
        }
    }
    Assert-ClaudePrimaryTranslation -PackageRoot $root -BuildInfo $buildInfo
}

function Get-ClaudeRuntimeManifest {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $manifestPath = Join-Path $PackageRoot 'setup\prerequisites\dotnet-desktop-runtime.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "缺少 .NET Desktop Runtime 清单：$manifestPath"
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "无法读取 .NET Desktop Runtime 清单：$manifestPath。$($_.Exception.Message)"
    }
    foreach ($property in @('version', 'architecture', 'fileName', 'url', 'sha512', 'source', 'releaseMetadata')) {
        if (-not $manifest.PSObject.Properties[$property] -or
            [String]::IsNullOrWhiteSpace([string]$manifest.$property)) {
            throw "运行时清单缺少字段：$property"
        }
    }
    if ([string]$manifest.architecture -ne 'x64' -or
        ([string]$manifest.fileName -notmatch '^windowsdesktop-runtime-8\.0\.[0-9]+-win-x64\.exe$') -or
        ([string]$manifest.sha512 -notmatch '^[0-9a-fA-F]{128}$') -or
        ([string]$manifest.url -notmatch '^https://builds\.dotnet\.microsoft\.com/')) {
        throw '运行时清单的版本、架构、官方下载地址或 SHA512 格式不符合固定契约。'
    }
    return $manifest
}

function Get-ClaudeRuntimeInstallerPath {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)]$Manifest
    )
    $fileName = [IO.Path]::GetFileName([string]$Manifest.fileName)
    if ($fileName -ne [string]$Manifest.fileName) {
        throw '运行时文件名不能包含目录。'
    }
    return (Join-Path $PackageRoot ('setup\prerequisites\' + $fileName))
}

function Assert-ClaudeRuntimeInstaller {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)]$Manifest
    )
    $installer = Get-ClaudeRuntimeInstallerPath -PackageRoot $PackageRoot -Manifest $Manifest
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "离线安装包缺少 .NET Desktop Runtime 安装器：$installer"
    }
    $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$Manifest.sha512).ToLowerInvariant()) {
        throw "运行时安装器 SHA512 校验失败：$installer"
    }
    $signature = Get-AuthenticodeSignature -FilePath $installer
    # Windows PowerShell 5.1 runs on .NET Framework, where the
    # SignatureStatus enum type is not exposed even though Get-AuthenticodeSignature
    # returns the same status value.
    if ([string]$signature.Status -ne 'Valid') {
        throw "运行时安装器微软签名无效：$($signature.Status) / $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
        throw "运行时安装器签名发布者不是 Microsoft Corporation：$($signature.SignerCertificate.Subject)"
    }
    return $installer
}

function Get-ClaudeVCRuntimeManifest {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $manifestPath = Join-Path $PackageRoot 'setup\prerequisites\vc-redist-x64.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "缺少 Microsoft Visual C++ x64 Runtime 清单：$manifestPath"
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "无法读取 Microsoft Visual C++ x64 Runtime 清单：$manifestPath。$($_.Exception.Message)"
    }
    foreach ($property in @('version', 'architecture', 'fileName', 'url', 'resolvedUrl', 'sha512', 'source', 'signaturePublisherContains', 'registryKey')) {
        if (-not $manifest.PSObject.Properties[$property] -or
            [String]::IsNullOrWhiteSpace([string]$manifest.$property)) {
            throw "VC++ Runtime 清单缺少字段：$property"
        }
    }
    if ([string]$manifest.architecture -ne 'x64' -or
        ([string]$manifest.fileName -ne 'vc_redist.x64.exe') -or
        ([string]$manifest.sha512 -notmatch '^[0-9a-fA-F]{128}$') -or
        ([string]$manifest.url -ne 'https://aka.ms/vs/17/release/vc_redist.x64.exe') -or
        ([string]$manifest.resolvedUrl -notmatch '^https://download\.visualstudio\.microsoft\.com/download/') -or
        ([string]$manifest.signaturePublisherContains -ne 'Microsoft Corporation')) {
        throw 'VC++ Runtime 清单的版本、架构、官方下载地址或 SHA512 格式不符合固定契约。'
    }
    return $manifest
}

function Get-ClaudeVCRuntimeInstallerPath {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)]$Manifest
    )
    $fileName = [IO.Path]::GetFileName([string]$Manifest.fileName)
    if ($fileName -ne [string]$Manifest.fileName) {
        throw 'VC++ Runtime 文件名不能包含目录。'
    }
    return (Join-Path $PackageRoot ('setup\prerequisites\' + $fileName))
}

function Assert-ClaudeVCRuntimeInstaller {
    param(
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [Parameter(Mandatory = $true)]$Manifest
    )
    $installer = Get-ClaudeVCRuntimeInstallerPath -PackageRoot $PackageRoot -Manifest $Manifest
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "离线安装包缺少 Microsoft Visual C++ x64 Runtime 安装器：$installer"
    }
    $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$Manifest.sha512).ToLowerInvariant()) {
        throw "VC++ Runtime 安装器 SHA512 校验失败：$installer"
    }
    $signature = Get-AuthenticodeSignature -FilePath $installer
    if ([string]$signature.Status -ne 'Valid') {
        throw "VC++ Runtime 安装器微软签名无效：$($signature.Status) / $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
        $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '<无签名证书>' }
        throw "VC++ Runtime 安装器签名发布者不是 Microsoft Corporation：$subject"
    }
    return $installer
}

function Test-ClaudeVCRuntimePresent {
    $baseKey = $null
    $runtimeKey = $null
    try {
        # Open the 64-bit registry view explicitly.  The x86 view must not
        # satisfy the x64 prerequisite even when this script is launched from
        # 32-bit Windows PowerShell.
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        $runtimeKey = $baseKey.OpenSubKey('SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64')
        if ($null -eq $runtimeKey) { return $false }
        $installed = $runtimeKey.GetValue('Installed', 0)
        $version = [string]$runtimeKey.GetValue('Version', '')
        return (([int]$installed -eq 1) -and ($version -match '^v?14\.'))
    }
    catch {
        return $false
    }
    finally {
        if ($runtimeKey) { $runtimeKey.Dispose() }
        if ($baseKey) { $baseKey.Dispose() }
    }
}

function Install-ClaudeVCRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [switch]$Simulate
    )
    if ($Simulate) {
        Write-Host "[模拟] 将静默安装 Microsoft Visual C++ x64 Runtime：$InstallerPath"
        return
    }
    $process = Start-Process -FilePath $InstallerPath -ArgumentList @('/install', '/quiet', '/norestart') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010, 1638)) {
        throw "Microsoft Visual C++ x64 Runtime 安装失败，退出码：$($process.ExitCode)"
    }
}

function Test-ClaudeDesktopRuntimePresent {
    $programFiles64 = [Environment]::GetEnvironmentVariable('ProgramW6432')
    if ([String]::IsNullOrWhiteSpace($programFiles64)) {
        $programFiles64 = [Environment]::GetFolderPath('ProgramFiles')
    }
    $dotnet = Join-Path $programFiles64 'dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        return $false
    }
    try {
        $lines = & $dotnet --list-runtimes 2>$null
    }
    catch {
        return $false
    }
    foreach ($line in $lines) {
        if ([string]$line -match '^Microsoft\.WindowsDesktop\.App\s+8\.0\.[0-9]+(?:\s|$)') {
            return $true
        }
    }
    return $false
}

function Install-ClaudeDesktopRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [switch]$Simulate
    )
    if ($Simulate) {
        Write-Host "[模拟] 将静默安装 .NET Desktop Runtime：$InstallerPath"
        return
    }
    $process = Start-Process -FilePath $InstallerPath -ArgumentList @('/install', '/quiet', '/norestart') -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw ".NET Desktop Runtime 安装失败，退出码：$($process.ExitCode)"
    }
}

function Initialize-ClaudeUserState {
    param([switch]$Simulate)
    $dataRoot = Get-ClaudeDataRoot
    $statePath = Join-Path $dataRoot 'ime-state.json'
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        return $statePath
    }
    if ($Simulate) {
        Write-Host "[模拟] 首次创建普通模式状态：$statePath"
        return $statePath
    }
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $state = [ordered]@{ mode = 'normal'; theme = 'paper'; candidateLimit = 9; enabled = $true }
    $json = $state | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($statePath, $json, (New-Object Text.UTF8Encoding($false)))
    return $statePath
}

function Get-ClaudeUserLanguageTag {
    return 'zh-Hans-CN'
}

function Get-ClaudeUserLanguageTip {
    return '0804:{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}{56E8B5B8-99E6-4C0B-AF16-33C5805FC3D2}'
}

function Get-ClaudeInputNativeMethodsType {
    if ($null -eq ('ClaudeZhInputNativeMethods' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ClaudeZhInputNativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool InstallLayoutOrTipDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string psz,
        uint dwFlags);
}
'@
    }
    return ('ClaudeZhInputNativeMethods' -as [type])
}

function Invoke-ClaudeInstallLayoutOrTip {
    param(
        [Parameter(Mandatory = $true)][string]$InputMethodTip,
        [Parameter(Mandatory = $true)][ValidateSet(0, 1)][uint32]$Flags,
        [scriptblock]$Invoker
    )

    if ($null -ne $Invoker) {
        $injectedResult = & $Invoker $InputMethodTip $Flags
        if (-not [bool]$injectedResult) {
            throw "InstallLayoutOrTip(flags=$Flags) 注入调用失败。"
        }
        return $true
    }

    $nativeMethods = Get-ClaudeInputNativeMethodsType
    $systemDirectory = [Environment]::SystemDirectory
    if ([String]::IsNullOrWhiteSpace($systemDirectory)) {
        throw '无法解析 Windows System32 目录，未调用 InstallLayoutOrTip。'
    }
    $inputDllPath = Join-Path $systemDirectory 'input.dll'
    if (-not (Test-Path -LiteralPath $inputDllPath -PathType Leaf)) {
        throw "找不到 Windows 输入系统 DLL：$inputDllPath"
    }

    # LOAD_LIBRARY_SEARCH_SYSTEM32 prevents an untrusted current directory or
    # PATH entry from supplying input.dll.  The path is also explicit so the
    # selected system module is deterministic.
    $loadLibrarySearchSystem32 = [uint32]0x00000800
    $module = [IntPtr]::Zero
    try {
        $module = $nativeMethods::LoadLibraryEx(
            $inputDllPath,
            [IntPtr]::Zero,
            $loadLibrarySearchSystem32)
        if ($module -eq [IntPtr]::Zero) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "加载 Windows 输入系统 DLL 失败：$inputDllPath（Win32Error=$errorCode）"
        }

        $procedure = $nativeMethods::GetProcAddress($module, 'InstallLayoutOrTip')
        if ($procedure -eq [IntPtr]::Zero) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "Windows 输入系统 DLL 缺少 InstallLayoutOrTip 导出（Win32Error=$errorCode）。"
        }

        $delegateType = $nativeMethods.GetNestedType(
            'InstallLayoutOrTipDelegate',
            [Reflection.BindingFlags]::Public)
        $callback = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
            $procedure,
            $delegateType)
        $success = [bool]$callback.Invoke($InputMethodTip, $Flags)
        if (-not $success) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "InstallLayoutOrTip(flags=$Flags) 失败（Win32Error=$errorCode）。"
        }
        return $true
    }
    finally {
        if ($module -ne [IntPtr]::Zero) {
            [void]$nativeMethods::FreeLibrary($module)
        }
    }
}

function Get-ClaudeLanguageEntries {
    param($LanguageList)

    $entries = New-Object 'System.Collections.Generic.List[object]'
    if ($null -eq $LanguageList) { return $entries.ToArray() }
    foreach ($language in $LanguageList) {
        if ($null -ne $language) { [void]$entries.Add($language) }
    }
    return $entries.ToArray()
}

function ConvertTo-ClaudeMutableLanguageList {
    param(
        $LanguageList,
        [Parameter(Mandatory = $true)][scriptblock]$LanguageListFactory
    )

    $entries = @(Get-ClaudeLanguageEntries -LanguageList $LanguageList)
    $working = $LanguageList
    $hasAdd = $null -ne $working -and $null -ne $working.PSObject.Methods['Add']
    $hasClear = $null -ne $working -and $null -ne $working.PSObject.Methods['Clear']
    if ($hasAdd -and $hasClear) {
        return [pscustomobject]@{ LanguageList = $working; Entries = $entries }
    }

    $seedTag = if ($entries.Count -gt 0 -and
        -not [String]::IsNullOrWhiteSpace([string]$entries[0].LanguageTag)) {
        [string]$entries[0].LanguageTag
    } else {
        Get-ClaudeUserLanguageTag
    }
    $working = & $LanguageListFactory $seedTag
    if ($null -eq $working -or
        $null -eq $working.PSObject.Methods['Add'] -or
        $null -eq $working.PSObject.Methods['Clear']) {
        throw 'Windows 用户语言列表不是可编辑的 List，无法保留现有语言顺序。'
    }
    if ($entries.Count -gt 0) {
        $working.Clear()
        foreach ($entry in $entries) { [void]$working.Add($entry) }
    }
    return [pscustomobject]@{ LanguageList = $working; Entries = $entries }
}

function Add-ClaudeInputMethodTipToLanguage {
    param(
        [Parameter(Mandatory = $true)]$Language,
        [Parameter(Mandatory = $true)][string]$Tip
    )

    foreach ($existingTip in $Language.InputMethodTips) {
        if ([string]$existingTip -ieq $Tip) { return $false }
    }

    $tips = $Language.InputMethodTips
    if ($null -ne $tips -and $null -ne $tips.PSObject.Methods['Add']) {
        [void]$tips.Add($Tip)
        return $true
    }

    $newTips = New-Object 'System.Collections.Generic.List[string]'
    foreach ($existingTip in $Language.InputMethodTips) { [void]$newTips.Add([string]$existingTip) }
    [void]$newTips.Add($Tip)
    try {
        $Language.InputMethodTips = [string[]]$newTips.ToArray()
    }
    catch {
        throw "无法编辑 $([string]$Language.LanguageTag) 的 InputMethodTips：$($_.Exception.Message)"
    }
    return $true
}

function Remove-ClaudeInputMethodTipFromLanguage {
    param(
        [Parameter(Mandatory = $true)]$Language,
        [Parameter(Mandatory = $true)][string]$Tip
    )

    $changed = $false
    $tips = $Language.InputMethodTips
    if ($null -ne $tips -and $null -ne $tips.PSObject.Methods['Remove']) {
        $removed = $true
        while ($removed) {
            $removed = $false
            foreach ($existingTip in $Language.InputMethodTips) {
                if ([string]$existingTip -ieq $Tip) {
                    [void]$tips.Remove($existingTip)
                    $changed = $true
                    $removed = $true
                    break
                }
            }
        }
        return $changed
    }

    $remaining = New-Object 'System.Collections.Generic.List[string]'
    foreach ($existingTip in $Language.InputMethodTips) {
        if ([string]$existingTip -ieq $Tip) {
            $changed = $true
        } else {
            [void]$remaining.Add([string]$existingTip)
        }
    }
    if ($changed) {
        try {
            $Language.InputMethodTips = [string[]]$remaining.ToArray()
        }
        catch {
            throw "无法编辑 $([string]$Language.LanguageTag) 的 InputMethodTips：$($_.Exception.Message)"
        }
    }
    return $changed
}

function Update-ClaudeUserLanguageTipModel {
    param(
        $LanguageList,
        [Parameter(Mandatory = $true)][ValidateSet('Add', 'Remove')][string]$Operation,
        [scriptblock]$LanguageListFactory
    )

    if ($null -eq $LanguageListFactory) {
        # Unary comma keeps the generic List intact in Windows PowerShell 5.1;
        # without it the pipeline can enumerate the list into separate entries.
        $LanguageListFactory = { param($tag) return ,(New-WinUserLanguageList -Language $tag) }
    }
    $converted = ConvertTo-ClaudeMutableLanguageList -LanguageList $LanguageList -LanguageListFactory $LanguageListFactory
    $working = $converted.LanguageList
    $entries = @(Get-ClaudeLanguageEntries -LanguageList $working)
    $tag = Get-ClaudeUserLanguageTag
    $tip = Get-ClaudeUserLanguageTip
    $target = $null
    foreach ($language in $entries) {
        if ([string]$language.LanguageTag -ieq $tag) {
            $target = $language
            break
        }
    }

    $changed = $false
    if ($Operation -eq 'Add') {
        if ($null -eq $target) {
            $created = & $LanguageListFactory $tag
            $createdEntries = @(Get-ClaudeLanguageEntries -LanguageList $created)
            foreach ($language in $createdEntries) {
                if ([string]$language.LanguageTag -ieq $tag) {
                    $target = $language
                    break
                }
            }
            if ($null -eq $target) { throw "无法创建 $tag 用户语言条目。" }
            if ($null -eq $working.PSObject.Methods['Add']) {
                throw 'Windows 用户语言列表缺少 Add 方法，无法追加中文语言条目。'
            }
            [void]$working.Add($target)
            $changed = $true
        }
        if (Add-ClaudeInputMethodTipToLanguage -Language $target -Tip $tip) { $changed = $true }
    } else {
        foreach ($language in $entries) {
            if (Remove-ClaudeInputMethodTipFromLanguage -Language $language -Tip $tip) { $changed = $true }
        }
    }

    return [pscustomobject]@{
        LanguageList = $working
        Changed = $changed
        Operation = $Operation
        LanguageTag = $tag
        InputMethodTip = $tip
    }
}

function Ensure-ClaudeUserLanguageTip {
    param(
        [switch]$Simulate,
        [scriptblock]$InstallLayoutOrTipInvoker
    )

    $result = Update-ClaudeUserLanguageTipModel -LanguageList (Get-WinUserLanguageList) -Operation Add
    if ($result.Changed -and -not $Simulate) {
        Set-WinUserLanguageList -LanguageList $result.LanguageList -Force
    }
    if (-not $Simulate) {
        # Call even for an idempotent language-list result so a successful
        # install makes the already-present TIP available immediately.
        [void](Invoke-ClaudeInstallLayoutOrTip -InputMethodTip $result.InputMethodTip -Flags 0 -Invoker $InstallLayoutOrTipInvoker)
    }
    if ($Simulate) {
        if ($result.Changed) {
            Write-Host "[模拟] 将在 $($result.LanguageTag) 保留现有语言顺序并加入本产品 InputMethodTip。"
        } else {
            Write-Host '[模拟] 本产品 InputMethodTip 已存在，不修改用户语言列表。'
        }
    }
    return $result
}

function Remove-ClaudeUserLanguageTip {
    param(
        [switch]$Simulate,
        [scriptblock]$InstallLayoutOrTipInvoker
    )

    $result = Update-ClaudeUserLanguageTipModel -LanguageList (Get-WinUserLanguageList) -Operation Remove
    if ($result.Changed -and -not $Simulate) {
        Set-WinUserLanguageList -LanguageList $result.LanguageList -Force
        [void](Invoke-ClaudeInstallLayoutOrTip -InputMethodTip $result.InputMethodTip -Flags 1 -Invoker $InstallLayoutOrTipInvoker)
    }
    if ($Simulate) {
        if ($result.Changed) {
            Write-Host '[模拟] 将只移除本产品 InputMethodTip，保留其他语言和输入法。'
        } else {
            Write-Host '[模拟] 用户语言列表中没有本产品 InputMethodTip。'
        }
    }
    return $result
}

function Get-ClaudeInstallMarkerPath {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    return (Join-Path $InstallDirectory '.claude-zh-input.install.json')
}

function Assert-ClaudeOwnedInstallDirectory {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    if (-not (Test-Path -LiteralPath $InstallDirectory -PathType Container)) {
        return $false
    }
    $markerPath = Get-ClaudeInstallMarkerPath $InstallDirectory
    if (Test-Path -LiteralPath $markerPath -PathType Leaf) {
        try {
            $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            throw "已存在的安装标记无法读取，未覆盖目录：$InstallDirectory"
        }
        if ([string]$marker.productId -ne (Get-ClaudeProductId)) {
            throw "安装目录标记属于其他产品，未覆盖目录：$InstallDirectory"
        }
        return $true
    }
    $legacyApp = Join-Path $InstallDirectory 'app\ClaudeZhInput.exe'
    $legacyPhrases = Join-Path $InstallDirectory 'phrases.dat'
    if ((Test-Path -LiteralPath $legacyApp -PathType Leaf) -and
        (Test-Path -LiteralPath $legacyPhrases -PathType Leaf)) {
        return $true
    }
    $children = @(Get-ChildItem -LiteralPath $InstallDirectory -Force -ErrorAction SilentlyContinue)
    if ($children.Count -gt 0) {
        throw "安装目录不是本产品的已知目录，未覆盖现有内容：$InstallDirectory"
    }
    return $true
}

function Get-ClaudeStartupCommand {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    $app = Join-Path $InstallDirectory 'app\ClaudeZhInput.exe'
    return ('"' + $app + '" --ime-service')
}

function Ensure-ClaudeStartupRegistration {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [switch]$Simulate
    )
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $valueName = Get-ClaudeStartupValueName
    $expected = Get-ClaudeStartupCommand $InstallDirectory
    $existing = $null
    if (Test-Path -LiteralPath $runKey) {
        $property = Get-ItemProperty -LiteralPath $runKey -Name $valueName -ErrorAction SilentlyContinue
        if ($null -ne $property) { $existing = [string]$property.$valueName }
    }
    if (-not [String]::IsNullOrWhiteSpace($existing) -and $existing -ne $expected) {
        throw "用户启动项 '$valueName' 已有其他内容，未覆盖：$existing"
    }
    if ($Simulate) {
        Write-Host "[模拟] 将登记用户启动项：$valueName = $expected"
        return $expected
    }
    if (-not (Test-Path -LiteralPath $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    New-ItemProperty -LiteralPath $runKey -Name $valueName -Value $expected -PropertyType String -Force | Out-Null
    return $expected
}

function Remove-ClaudeStartupRegistration {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [string]$RecordedCommand,
        [switch]$Simulate
    )
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $valueName = Get-ClaudeStartupValueName
    if (-not (Test-Path -LiteralPath $runKey)) { return }
    $property = Get-ItemProperty -LiteralPath $runKey -Name $valueName -ErrorAction SilentlyContinue
    if ($null -eq $property) { return }
    $current = [string]$property.$valueName
    $expected = Get-ClaudeStartupCommand $InstallDirectory
    if ($current -ne $expected -and $current -ne $RecordedCommand) {
        Write-Warning "启动项 '$valueName' 已被改为其他内容，已保留：$current"
        return
    }
    if ($Simulate) {
        Write-Host "[模拟] 将移除用户启动项：$valueName"
        return
    }
    Remove-ItemProperty -LiteralPath $runKey -Name $valueName -ErrorAction Stop
}

function Get-ClaudeProductProcesses {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    $appPath = (Get-ClaudeFullPath (Join-Path $InstallDirectory 'app\ClaudeZhInput.exe')).TrimEnd('\')
    try {
        $processes = Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ClaudeZhInput.exe'" -ErrorAction Stop
        foreach ($process in $processes) {
            if (-not [String]::IsNullOrWhiteSpace($process.ExecutablePath) -and
                (Get-ClaudeFullPath $process.ExecutablePath).Equals($appPath, [StringComparison]::OrdinalIgnoreCase)) {
                $process
            }
        }
    }
    catch {
        # A process whose executable path cannot be inspected is deliberately
        # skipped; killing every same-named process could affect another app.
    }
}

function Stop-ClaudeProductProcesses {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [switch]$Simulate
    )
    $processes = @(Get-ClaudeProductProcesses $InstallDirectory)
    if ($processes.Count -eq 0) { return }
    foreach ($process in $processes) {
        if ($Simulate) {
            Write-Host "[模拟] 将停止输入法服务 PID $($process.ProcessId)"
        }
        else {
            Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $Simulate) {
        $deadline = (Get-Date).AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 200
            $remaining = @(Get-ClaudeProductProcesses $InstallDirectory)
        } while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)
        if ($remaining.Count -gt 0) {
            throw '输入法服务仍在运行，无法安全替换或卸载；请先退出托盘中的输入法助手。'
        }
    }
}

function Start-ClaudeProduct {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [switch]$Settings,
        [switch]$Simulate
    )
    $app = Join-Path $InstallDirectory 'app\ClaudeZhInput.exe'
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
        throw "找不到输入法程序：$app。请先运行 install.ps1。"
    }
    if ($Simulate) {
        if ($Settings) { Write-Host "[模拟] 将打开输入法设置：$app" }
        else { Write-Host "[模拟] 将在用户范围后台启动输入法服务：$app --ime-service" }
        return
    }
    if ($Settings) {
        Start-Process -FilePath $app -WorkingDirectory $InstallDirectory -WindowStyle Hidden | Out-Null
    }
    else {
        Start-Process -FilePath $app -ArgumentList @('--ime-service') -WorkingDirectory $InstallDirectory -WindowStyle Hidden | Out-Null
    }
}

function New-ClaudeControlledTempDirectory {
    $tempRoot = Get-ClaudeFullPath ([IO.Path]::GetTempPath())
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $path = Join-Path $tempRoot ('ClaudeZhInput-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

function Remove-ClaudeControlledTempDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $tempRoot = (Get-ClaudeFullPath ([IO.Path]::GetTempPath())).TrimEnd('\')
    $full = (Get-ClaudeFullPath $Path).TrimEnd('\')
    if (-not (Test-ClaudePathWithin -Child $full -Parent $tempRoot) -or
        $full.Equals($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除非本次受控临时目录：$full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

function Write-ClaudeInstallMarker {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$SourcePackageRoot,
        [Parameter(Mandatory = $true)][string]$StartupCommand,
        [string]$NativeDirectory,
        [string]$NativeVersionId
    )
    $marker = [ordered]@{
        productId = Get-ClaudeProductId
        installedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        sourcePackage = (Get-ClaudeFullPath $SourcePackageRoot)
        startupValueName = Get-ClaudeStartupValueName
        startupCommand = $StartupCommand
        personalDataRoot = '%LOCALAPPDATA%\ClaudeZhInput'
    }
    if (-not [String]::IsNullOrWhiteSpace($NativeDirectory)) {
        $installFull = (Get-ClaudeFullPath $InstallDirectory).TrimEnd('\')
        $nativeFull = (Get-ClaudeFullPath $NativeDirectory).TrimEnd('\')
        if (-not (Test-ClaudePathWithin -Child $nativeFull -Parent $installFull)) {
            throw "native 版本目录越出产品目录：$nativeFull"
        }
        $marker.nativeDirectory = $nativeFull.Substring($installFull.Length).TrimStart('\')
    }
    if (-not [String]::IsNullOrWhiteSpace($NativeVersionId)) {
        $marker.nativeVersionId = $NativeVersionId
    }
    $path = Get-ClaudeInstallMarkerPath $InstallDirectory
    $json = $marker | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

function Invoke-ClaudeNativeInstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [string]$NativeDirectory,
        [switch]$RestartTextServices
    )
    $nativeRoot = if ([String]::IsNullOrWhiteSpace($NativeDirectory)) {
        Get-ClaudeInstalledNativeDirectory -InstallDirectory $InstallDirectory
    } else {
        Get-ClaudeFullPath $NativeDirectory
    }
    if (Test-Path -LiteralPath (Join-Path $nativeRoot 'ClaudeZhInput.NativeCandidate.dll')) {
        $rimeDll = Join-Path $nativeRoot 'ClaudeZhInput.NativeCandidate.dll'
        $rimeRegistration = Start-Process -FilePath (Join-Path $env:WINDIR 'System32/regsvr32.exe') -ArgumentList ('/s "' + $rimeDll + '"') -WindowStyle Hidden -Wait -PassThru
        if ($rimeRegistration.ExitCode -ne 0) { throw "Rime registration failed: $($rimeRegistration.ExitCode)" }
        $registeredRime = Get-Item -LiteralPath 'Registry::HKEY_CLASSES_ROOT\CLSID\{B763D5F3-0F9E-47D2-8CD8-7CCBFA6C3A41}\InprocServer32'
        if ($registeredRime.GetValue('') -ne $rimeDll) { throw 'Rime registration readback mismatch' }
        return
    }
    $script = Join-Path $nativeRoot 'install.ps1'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        throw "安装包缺少 native/install.ps1：$script"
    }
    # Splat named parameters as a hashtable.  An array containing strings such
    # as '-NoBuild' is passed as positional data by the call operator, which
    # makes the callee reject the following path value.
    $arguments = @{
        NoBuild = $true
        BinaryDirectory = (Join-Path $nativeRoot 'bin\Release\net8.0-windows\win-x64')
        RegistrationToolPath = (Join-Path $nativeRoot 'TsfRegistration.Tool\bin\Release\net8.0-windows\win-x64\ClaudeZhInput.TsfRegistration.exe')
        # The package comhost's DllRegisterServer returns CLASS_E_CLASSNOTAVAILABLE;
        # setup owns the fixed 64-bit CLSID/InprocServer32 write below instead.
        SkipComRegistration = $true
    }
    if ($RestartTextServices) { $arguments.RestartTextServices = $true }
    & $script @arguments
    $comHost = Join-Path $nativeRoot 'bin\Release\net8.0-windows\win-x64\ClaudeZhInput.Tsf.comhost.dll'
    Set-ClaudeNativeComRegistration -ComHostPath $comHost
}

function Invoke-ClaudeNativeUninstall {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [string]$NativeDirectory,
        [switch]$RestartTextServices
    )
    $nativeRoot = if ([String]::IsNullOrWhiteSpace($NativeDirectory)) {
        Get-ClaudeInstalledNativeDirectory -InstallDirectory $InstallDirectory
    } else {
        Get-ClaudeFullPath $NativeDirectory
    }
    if (Test-Path -LiteralPath (Join-Path $nativeRoot 'ClaudeZhInput.NativeCandidate.dll')) {
        & (Join-Path $nativeRoot 'uninstall.ps1')
        return
    }
    $script = Join-Path $nativeRoot 'uninstall.ps1'
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        throw "安装目录缺少 native/uninstall.ps1，未删除产品目录：$script"
    }
    $arguments = @{
        BinaryDirectory = (Join-Path $nativeRoot 'bin\Release\net8.0-windows\win-x64')
        RegistrationToolPath = (Join-Path $nativeRoot 'TsfRegistration.Tool\bin\Release\net8.0-windows\win-x64\ClaudeZhInput.TsfRegistration.exe')
        SkipComRegistration = $true
    }
    if ($RestartTextServices) { $arguments.RestartTextServices = $true }
    & $script @arguments
    [void](Remove-ClaudeNativeComRegistration -InstallDirectory $InstallDirectory)
}

function Remove-ClaudeProductDirectorySafely {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    $installFull = Assert-ClaudeSafeInstallDirectory -Path $InstallDirectory
    [void](Assert-ClaudeOwnedInstallDirectory $installFull)
    $markerPath = Get-ClaudeInstallMarkerPath $installFull
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $marker | Add-Member -NotePropertyName uninstallPending -NotePropertyValue $true -Force
    [IO.File]::WriteAllText($markerPath, ($marker | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    Set-Location ([IO.Path]::GetTempPath())
    # Keep the identity and runnable cleanup entry until every payload is gone.
    # Recursive removal of the whole tree first can erase these before a
    # locked native DLL fails, leaving an orphan nobody can safely identify.
    $keep = @('.claude-zh-input.install.json','setup','卸载输入法.cmd')
    foreach ($entry in @(Get-ChildItem -LiteralPath $installFull -Force)) {
        if ($entry.Name -in $keep) { continue }
        $target = Get-ClaudeFullPath $entry.FullName
        if (-not (Test-ClaudePathWithin -Child $target -Parent $installFull)) { throw '清理目标越出安装目录。' }
        try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning ('保留占用中的文件：' + $entry.Name) }
    }
    $payload = @(Get-ChildItem -LiteralPath $installFull -Force | Where-Object { $_.Name -notin $keep })
    if ($payload.Count -gt 0) { return $false }
    foreach ($name in @('setup','卸载输入法.cmd')) {
        $target = Join-Path $installFull $name
        if (Test-Path -LiteralPath $target) {
            try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
            catch { return $false }
        }
    }
    Remove-Item -LiteralPath $markerPath -Force
    Remove-Item -LiteralPath $installFull -Force
    return $true
}

function Ensure-ClaudeUserMaintenanceRegistration {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    $installFull = Assert-ClaudeSafeInstallDirectory -Path $InstallDirectory
    [void](Assert-ClaudeOwnedInstallDirectory $installFull)
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeZhInput'
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installFull 'setup\uninstall.ps1') + '" -Interactive -InstallDirectory "' + $installFull + '"'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installFull 'app\ClaudeZhInput.dll')).ProductVersion
    New-Item -Path $key -Force | Out-Null
    $values = @{DisplayName='Claude 中文输入法';DisplayVersion=$version;InstallLocation=$installFull;UninstallString=('"' + $powershell + '" ' + $arguments);DisplayIcon=(Join-Path $installFull 'app\ClaudeZhInput.exe')}
    foreach ($name in $values.Keys) { New-ItemProperty -LiteralPath $key -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null }
    foreach ($name in @('NoModify','NoRepair')) { New-ItemProperty -LiteralPath $key -Name $name -Value 1 -PropertyType DWord -Force | Out-Null }
    $folder = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude 中文输入法'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link=$shell.CreateShortcut((Join-Path $folder 'Claude 中文输入法.lnk'))
        $link.TargetPath=Join-Path $installFull 'app\ClaudeZhInput.exe'; $link.WorkingDirectory=$installFull; $link.Save()
        $link=$shell.CreateShortcut((Join-Path $folder '卸载 Claude 中文输入法.lnk'))
        $link.TargetPath=$powershell; $link.Arguments=$arguments; $link.WindowStyle=7; $link.Save()
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
}

function Remove-ClaudeUserMaintenanceRegistration {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClaudeZhInput'
    $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
    if ($null -eq $entry -or ([string]$entry.InstallLocation) -ine (Get-ClaudeFullPath $InstallDirectory)) { return }
    Remove-Item -LiteralPath $key -Recurse -Force
    $folder = Join-Path ([Environment]::GetFolderPath('Programs')) 'Claude 中文输入法'
    foreach ($name in @('Claude 中文输入法.lnk','卸载 Claude 中文输入法.lnk')) {
        $target = Join-Path $folder $name
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    }
    if ((Test-Path -LiteralPath $folder) -and @(Get-ChildItem -LiteralPath $folder -Force).Count -eq 0) { Remove-Item -LiteralPath $folder }
}
