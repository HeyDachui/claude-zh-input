[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputRoot,
    [ValidateSet('Release')][string]$Configuration = 'Release',
    [switch]$SkipPythonCompile,
    [string]$WorkerPackage,
    [ValidateSet('Q5_K_M','Q4_K_M','UD-IQ2_M','Q3_K_S')][string]$ModelQuantization = 'Q5_K_M',
    [switch]$SkipDotnetBuild,
    [switch]$SkipNativeBuild,
    [switch]$SkipSmoke,
    [switch]$KeepStaging,
    [switch]$Overwrite,
    [switch]$AllowPrivateMinGWDownload,
    [string]$NuitkaVersion = '4.2.2'
)

$ErrorActionPreference = 'Stop'
$scriptRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $scriptRoot }
$project = (Resolve-Path -LiteralPath $ProjectRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $project 'dist/protected' }
$output = [IO.Path]::GetFullPath($OutputRoot)

function Assert-ChildPath([string]$Child, [string]$Parent) {
    $childFull = [IO.Path]::GetFullPath($Child).TrimEnd('\','/')
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\','/')
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    if ($childFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase) -or
        !$childFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须位于工程目录内：$childFull"
    }
}

Assert-ChildPath $output $project
$src = Join-Path $project 'src'
$csproj = Join-Path $src 'ClaudeZhInput.csproj'
$nativeRoot = Join-Path $project 'native-cpp'
$workerSource = Join-Path $project 'worker.py'
$runtimeSource = Join-Path $project 'runtime'
$rimeProject = Join-Path $project 'rime/ClaudeZhInput.Rime.csproj'
$rimeRuntimeSource = Join-Path $project 'rime/runtime'
$licensesSource = Join-Path $project 'licenses'
$commandsSource = Join-Path $project 'commands/claude-commands.json'
foreach ($required in @($csproj, $rimeProject, $rimeRuntimeSource, $workerSource, $licensesSource, $commandsSource)) {
    if (!(Test-Path -LiteralPath $required)) { throw "缺少构建输入：$required" }
}
$builtInSource = Join-Path $src 'BuiltInPhrases.cs'
$builtInText = [IO.File]::ReadAllText($builtInSource)
$builtInHash = [regex]::Match($builtInText, 'SourceSha256\s*=\s*"([A-Fa-f0-9]{64})"').Groups[1].Value
if ($builtInHash -ne (Get-FileHash -LiteralPath (Join-Path $project 'phrases.json')).Hash) {
    throw 'BuiltInPhrases.cs is stale. Regenerate and verify it against phrases.json before packaging.'
}

if (Test-Path -LiteralPath $output) {
    if (!$Overwrite) { throw "输出目录已存在：$output；需要覆盖时显式传 -Overwrite。" }
    Get-ChildItem -LiteralPath $output -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $output | Out-Null
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging = Join-Path $scriptRoot "staging/protected-$stamp"
Assert-ChildPath $staging $scriptRoot
$appStage = Join-Path $staging 'app'
$workerTempRoot = Join-Path ([IO.Path]::GetTempPath()) "ClaudeZhInput-Nuitka-$stamp"
Assert-ChildPath $workerTempRoot ([IO.Path]::GetTempPath())
if ($workerTempRoot -match '[^\x00-\x7F]') {
    throw "Nuitka 编译临时目录必须是 ASCII 路径：$workerTempRoot"
}
$workerBuild = Join-Path $workerTempRoot 'worker-build'
$workerOut = Join-Path $workerTempRoot 'worker-out'
$workerSitePackages = Join-Path $workerTempRoot 'site-packages'
$obfuscationOut = Join-Path $staging 'obfuscation-out'
New-Item -ItemType Directory -Force -Path $appStage,$workerBuild,$workerOut,$workerSitePackages,$obfuscationOut | Out-Null

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$File,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $File @Arguments
        if ($LASTEXITCODE -ne 0) { throw "$File 退出码为 $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

function Copy-Tree([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
}

function Stage-RimeRuntime([string]$AppRoot) {
    $target = Join-Path $AppRoot 'runtime'
    $requiredSource = @(
        (Join-Path $rimeRuntimeSource 'rime.dll'),
        (Join-Path $rimeRuntimeSource 'data'),
        (Join-Path $rimeRuntimeSource 'licenses')
    )
    foreach ($item in $requiredSource) {
        if (!(Test-Path -LiteralPath $item)) { throw "Rime 运行时输入缺失：$item" }
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $rimeRuntimeSource 'rime.dll') -Destination (Join-Path $target 'rime.dll') -Force
    foreach ($directory in @('data', 'licenses')) {
        $destination = Join-Path $target $directory
        if (Test-Path -LiteralPath $destination -PathType Container) {
            Remove-Item -LiteralPath $destination -Recurse -Force
        }
        Copy-Tree (Join-Path $rimeRuntimeSource $directory) $destination
    }
    # Headers, import libraries, and the deployer are development inputs. The
    # application consumes only rime.dll, prebuilt data, and notices. The
    # development README/MANIFEST stay in rime/runtime for source verification.
    foreach ($directory in @('include', 'tools')) {
        $unwanted = Join-Path $target $directory
        if (Test-Path -LiteralPath $unwanted -PathType Container) {
            Remove-Item -LiteralPath $unwanted -Recurse -Force
        }
    }
    $developmentManifest = Join-Path $target 'MANIFEST.json'
    if (Test-Path -LiteralPath $developmentManifest -PathType Leaf) {
        Remove-Item -LiteralPath $developmentManifest -Force
    }
}

if ($SkipDotnetBuild) {
    Copy-Tree (Join-Path $project 'app') $appStage
} else {
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    $publishArgs = @(
        'publish', $csproj, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:PublishSingleFile=false',
        '-p:PublishTrimmed=false', '-p:IncludeNativeLibrariesForSelfExtract=false',
        '-o', $appStage
    )
    Invoke-External $dotnet $publishArgs $project
}

Stage-RimeRuntime $appStage
foreach ($required in @(
    (Join-Path $appStage 'ClaudeZhInput.Rime.dll'),
    (Join-Path $appStage 'runtime/rime.dll'),
    (Join-Path $appStage 'runtime/data/build/claude_pinyin.schema.yaml'),
    (Join-Path $appStage 'runtime/data/build/pinyin_simp.table.bin'),
    (Join-Path $appStage 'runtime/licenses/librime-BSD-3-Clause.txt'),
    (Join-Path $appStage 'runtime/licenses/rime-pinyin-simp-Apache-2.0.txt')
)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Rime 发布输出缺少：$required" }
}

if (!$SkipNativeBuild) { & (Join-Path $nativeRoot 'build-native-candidate.ps1') | Out-Host }
$nativeDll = Join-Path $nativeRoot 'bin/x64/Release/ClaudeZhInput.NativeCandidate.dll'
if (!(Test-Path -LiteralPath $nativeDll)) { throw 'Native Rime build output missing' }
$ownAssembly = Join-Path $appStage 'ClaudeZhInput.dll'
if (!(Test-Path -LiteralPath $ownAssembly -PathType Leaf)) { throw "发布输出缺少 ClaudeZhInput.dll" }
$ownAssemblies = @(Get-ChildItem -LiteralPath $appStage -File -Filter 'ClaudeZhInput*.dll' |
    Where-Object { $_.Name -notlike '*.resources.dll' })
if ($ownAssemblies.Count -eq 0) { throw '发布输出没有可保护的自有程序集。' }

$obfuscar = Join-Path $scriptRoot '.dotnet-tools/obfuscar.console.exe'
if (!(Test-Path -LiteralPath $obfuscar -PathType Leaf)) {
    $dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $obfuscar) | Out-Null
    Invoke-External $dotnet @('tool','install','obfuscar.globaltool','--tool-path',(Split-Path -Parent $obfuscar),'--version','2.2.50') $project
}

function Find-ReferenceFolder([string]$PackName) {
    $pack = Join-Path ${env:ProgramFiles} "dotnet/packs/$PackName"
    if (!(Test-Path -LiteralPath $pack -PathType Container)) { return $null }
    $version = Get-ChildItem -LiteralPath $pack -Directory |
        Sort-Object { try { [Version]$_.Name } catch { [Version]'0.0' } } -Descending |
        Select-Object -First 1
    if ($null -eq $version) { return $null }
    $candidate = Join-Path $version.FullName 'ref/net8.0'
    if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    return $null
}

$windowsRef = Find-ReferenceFolder 'Microsoft.WindowsDesktop.App.Ref'
$coreRef = Find-ReferenceFolder 'Microsoft.NETCore.App.Ref'
if (!$windowsRef -or !$coreRef) { throw '找不到 .NET 8 WPF/核心引用程序集，不能安全执行 WPF 混淆。' }
$appBin = Join-Path $src 'bin/Release/net8.0-windows'

function XmlPath([string]$Value) {
    return [Security.SecurityElement]::Escape($Value.Replace('\','/'))
}

$obfuscarConfig = Join-Path $staging 'obfuscar.generated.xml'
$inXml = XmlPath $appStage
$outXml = XmlPath $obfuscationOut
$windowsRefXml = XmlPath $windowsRef
$coreRefXml = XmlPath $coreRef
$appBinXml = XmlPath $appBin
$moduleXml = ($ownAssemblies | ForEach-Object {
    $modulePathXml = XmlPath $_.FullName
    if ($_.Name -eq 'ClaudeZhInput.dll') {
@"
  <Module file="$modulePathXml">
    <!-- WPF resolves code-behind event handlers from BAML by name. -->
    <SkipType name="ClaudeZhInput.App" />
    <SkipType name="ClaudeZhInput.MainWindow" />
    <SkipType name="ClaudeZhInput.StatusBarWindow" />
    <SkipType name="ClaudeZhInput.WorkerProcessJob*" skipMethods="true" skipFields="true" skipProperties="true" />
    <!-- System.Text.Json reads compiler-generated anonymous types by constructor metadata. -->
    <SkipType name="*AnonymousType*" skipMethods="true" skipFields="true" skipProperties="true" />
  </Module>
"@
    } elseif ($_.Name -eq 'ClaudeZhInput.Commands.Wpf.dll') {
@"
  <Module file="$modulePathXml">
    <!-- XAML event handlers and generated field names are resolved by the WPF loader. -->
    <SkipType name="ClaudeZhInput.Commands.Wpf.CommandPickerWindow" />
  </Module>
"@
    } elseif ($_.Name -eq 'ClaudeZhInput.Rime.dll') {
@"
  <Module file="$modulePathXml">
    <!-- Preserve the public bridge state and options consumed by the WPF/native layers. -->
    <SkipType name="ClaudeZhInput.Rime.RimeCandidate" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeEngineOptions" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeSessionState" skipMethods="true" skipFields="true" skipProperties="true" />
    <!-- Native function-pointer offsets, StructLayout fields, and DllImport entry points must stay exact. -->
    <SkipType name="ClaudeZhInput.Rime.RimeNative" />
    <SkipType name="ClaudeZhInput.Rime.RimeTraits" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeComposition" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeCandidateNative" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeMenu" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeCommit" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeContext" skipMethods="true" skipFields="true" skipProperties="true" />
    <SkipType name="ClaudeZhInput.Rime.RimeApiLayout" skipMethods="true" skipFields="true" skipProperties="true" />
  </Module>
"@
    } else {
@"
  <Module file="$modulePathXml">
    <!-- Command records are deserialized through their public JSON contract. -->
    <SkipType name="*AnonymousType*" skipMethods="true" skipFields="true" skipProperties="true" />
  </Module>
"@
    }
}) -join "`n"
@"
<?xml version="1.0"?>
<Obfuscator>
  <Var name="InPath" value="$inXml" />
  <Var name="OutPath" value="$outXml" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="RegenerateDebugInfo" value="false" />
  <Var name="SuppressIldasm" value="true" />
  <Var name="SkipGenerated" value="true" />
  <Var name="SkipSpecialName" value="true" />
  <AssemblySearchPath path="$windowsRefXml" />
  <AssemblySearchPath path="$coreRefXml" />
  <AssemblySearchPath path="$appBinXml" />
$moduleXml
</Obfuscator>
"@ | Set-Content -LiteralPath $obfuscarConfig -Encoding UTF8
Invoke-External $obfuscar @($obfuscarConfig,'-v:n') $project
$obfuscatedAssemblies = @(Get-ChildItem -LiteralPath $obfuscationOut -File -Filter 'ClaudeZhInput*.dll' |
    Where-Object { $_.Name -notlike '*.resources.dll' })
if ($obfuscatedAssemblies.Count -ne $ownAssemblies.Count) {
    throw "Obfuscar 自有程序集输出数量不一致：输入 $($ownAssemblies.Count)，输出 $($obfuscatedAssemblies.Count)"
}
foreach ($obfuscated in $obfuscatedAssemblies) {
    Copy-Item -LiteralPath $obfuscated.FullName -Destination (Join-Path $appStage $obfuscated.Name) -Force
}

function Join-Bytes([object[]]$Parts) {
    $stream = New-Object IO.MemoryStream
    try {
        foreach ($part in $Parts) {
            [byte[]]$bytes = $part
            $stream.Write($bytes, 0, $bytes.Length)
        }
        return $stream.ToArray()
    } finally { $stream.Dispose() }
}

function Protect-Bytes([byte[]]$Plain, [byte[]]$Key) {
    $nonce = New-Object byte[] 16
    [Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
    $cipher = New-Object byte[] $Plain.Length
    for ($offset = 0; $offset -lt $Plain.Length; $offset += 32) {
        $counter = [BitConverter]::GetBytes([uint32]($offset / 32))
        $seed = Join-Bytes @($Key, $nonce, $counter)
        $digest = [Security.Cryptography.SHA256]::HashData($seed)
        $count = [Math]::Min(32, $Plain.Length - $offset)
        for ($i = 0; $i -lt $count; $i++) { $cipher[$offset + $i] = $Plain[$offset + $i] -bxor $digest[$i] }
    }
    $tag = [Security.Cryptography.HMACSHA256]::new($Key).ComputeHash((Join-Bytes @($nonce, $cipher)))
    return Join-Bytes @([Text.Encoding]::ASCII.GetBytes('CZP1'), $nonce, $cipher, $tag)
}

$key = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($key)
$phrases = [IO.File]::ReadAllBytes((Join-Path $project 'phrases.json'))
$phrasesBlob = Protect-Bytes -Plain $phrases -Key $key
$phrasesOut = Join-Path $output 'phrases.dat'
[IO.File]::WriteAllBytes($phrasesOut, $phrasesBlob)
$keyHex = [Convert]::ToHexString($key).ToLowerInvariant()

if (!$SkipPythonCompile) {
    $nuitkaCache = Join-Path $scriptRoot '.nuitka-cache'
    $privateGcc = if (Test-Path -LiteralPath $nuitkaCache -PathType Container) {
        Get-ChildItem -LiteralPath $nuitkaCache -Recurse -File -Filter 'gcc.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    } else { $null }
    $compiler = $null
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) { $compiler = 'msvc' }
    elseif (Get-Command clang-cl.exe -ErrorAction SilentlyContinue) { $compiler = 'clang' }
    elseif (Get-Command gcc.exe -ErrorAction SilentlyContinue) { $compiler = 'mingw' }
    elseif ($privateGcc) { $compiler = 'mingw-private' }
    elseif (Test-Path -LiteralPath 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe') {
        $vswhere = 'C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe'
        $vs = & $vswhere '-latest' '-products' '*' '-requires' 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' '-property' 'installationPath'
        if ($vs) { $compiler = 'msvc' }
    }
    if (!$compiler -and $AllowPrivateMinGWDownload) {
        $compiler = 'mingw-private'
    }
    if (!$compiler) {
        throw 'Nuitka 需要现有 MSVC/clang-cl/MinGW C 编译器；当前未找到。脚本默认不安装工具链；如需本机私有 MinGW，请显式传 -AllowPrivateMinGWDownload。'
    }

    $pyLauncher = (Get-Command py.exe -ErrorAction Stop).Source
    $nuitkaVenv = Join-Path $scriptRoot '.nuitka-venv'
    if (!(Test-Path -LiteralPath (Join-Path $nuitkaVenv 'Scripts/python.exe') -PathType Leaf)) {
        Invoke-External $pyLauncher @('-3.11','-m','venv',$nuitkaVenv) $project
    }
    $nuitkaPython = Join-Path $nuitkaVenv 'Scripts/python.exe'
    $oldNuitkaCache = $env:NUITKA_CACHE_DIR
    $env:NUITKA_CACHE_DIR = $nuitkaCache
    $nuitkaModule = & $nuitkaPython '-m' 'nuitka' '--version' 2>$null
    if (!$nuitkaModule) {
        Invoke-External $nuitkaPython @('-m','pip','install','--disable-pip-version-check','--no-input',"Nuitka==$NuitkaVersion") $project
    }

    $entryPath = Join-Path $workerBuild 'worker_entry.py'
    $sourceCopy = Join-Path $workerBuild 'worker.py'
    Copy-Item -LiteralPath $workerSource -Destination $sourceCopy -Force
    Copy-Item -LiteralPath (Join-Path $project 'translation_text.py') -Destination $workerBuild -Force
    @"
from __future__ import annotations
import hashlib
import hmac
import json
import os
from pathlib import Path
import struct
import sys

_KEY = bytes.fromhex('$keyHex')
_PACKAGE_ROOT = Path(sys.executable).resolve().parent

def _unseal(path: Path) -> bytes:
    blob = path.read_bytes()
    if len(blob) < 52 or blob[:4] != b'CZP1':
        raise RuntimeError('默认短语资源损坏。')
    nonce, cipher, tag = blob[4:20], blob[20:-32], blob[-32:]
    if not hmac.compare_digest(tag, hmac.new(_KEY, nonce + cipher, hashlib.sha256).digest()):
        raise RuntimeError('默认短语资源校验失败。')
    plain = bytearray(len(cipher))
    for offset in range(0, len(cipher), 32):
        stream = hashlib.sha256(_KEY + nonce + struct.pack('<I', offset // 32)).digest()
        for index in range(min(32, len(cipher) - offset)):
            plain[offset + index] = cipher[offset + index] ^ stream[index]
    return bytes(plain)

def _load_phrases() -> dict[str, str]:
    data = json.loads(_unseal(_PACKAGE_ROOT / 'phrases.dat').decode('utf-8'))
    return {_worker.key(p['zh']): p['en'] for p in data}

import worker as _worker
os.chdir(_PACKAGE_ROOT)
_worker.ROOT = _PACKAGE_ROOT
_worker.MODEL = Path('models') / 'translation-llm' / 'model.gguf'
_worker.load_phrases = _load_phrases
_worker.main()
"@ | Set-Content -LiteralPath $entryPath -Encoding UTF8

    $nuitkaArgs = @(
        '-m','nuitka','--standalone','--remove-output','--jobs=4',
        "--output-dir=$workerOut",'--output-filename=worker.exe',
        '--include-module=worker','--include-module=translation_text',
        '--nofollow-import-to=ctranslate2,numpy,sentencepiece',
        '--windows-console-mode=force',
        $entryPath
    )
    if ($compiler -eq 'msvc') { $nuitkaArgs += '--msvc=latest' }
    elseif ($compiler -eq 'clang') { $nuitkaArgs += '--clang' }
    else {
        $nuitkaArgs += '--mingw64'
        if ($AllowPrivateMinGWDownload) { $nuitkaArgs += '--assume-yes-for-downloads' }
    }
    $oldPythonPath = $env:PYTHONPATH
    $env:PYTHONPATH = "$workerBuild;$workerSitePackages"
    $workerBuildSucceeded = $false
    try {
        Invoke-External $nuitkaPython $nuitkaArgs $workerBuild
        $workerExe = Get-ChildItem -LiteralPath $workerOut -Recurse -File -Filter 'worker.exe' | Select-Object -First 1
        if (!$workerExe) { throw 'Nuitka 没有生成 worker.exe' }
        Get-ChildItem -LiteralPath $workerExe.Directory.FullName -Force | Copy-Item -Destination $output -Recurse -Force
        $workerBuildSucceeded = $true
    }
    finally {
        $env:PYTHONPATH = $oldPythonPath
        if ($null -eq $oldNuitkaCache) { Remove-Item Env:NUITKA_CACHE_DIR -ErrorAction SilentlyContinue }
        else { $env:NUITKA_CACHE_DIR = $oldNuitkaCache }
        if ($workerBuildSucceeded -and (Test-Path -LiteralPath $workerTempRoot)) {
            Remove-Item -LiteralPath $workerTempRoot -Recurse -Force
        }
    }
} else {
    if (!$WorkerPackage) { throw '-SkipPythonCompile requires -WorkerPackage with a matching source manifest and paired phrases.dat.' }
    $workerPackageRoot = (Resolve-Path -LiteralPath $WorkerPackage).Path
    $workerInfo = Get-Content -LiteralPath (Join-Path $workerPackageRoot 'BUILD-INFO.json') -Raw | ConvertFrom-Json
    if ($workerInfo.workerProfile -ne 'stdlib-llm-v1') { throw 'The compact package requires a rebuilt standard-library-only worker.' }
    if ($workerInfo.sourceSha256.worker -ne (Get-FileHash -LiteralPath $workerSource).Hash -or
        $workerInfo.sourceSha256.translationText -ne (Get-FileHash -LiteralPath (Join-Path $project 'translation_text.py')).Hash -or
        $workerInfo.phrasesSourceSha256 -ne (Get-FileHash -LiteralPath (Join-Path $project 'phrases.json')).Hash) {
        throw 'Prebuilt worker or phrases source hash does not match; compile it again.'
    }
    # Reuse the previously tested worker; verify the copied bytes without rerunning its old tests.
    & (Join-Path $scriptRoot 'verify-package.ps1') -PackageRoot $workerPackageRoot -SkipSmoke -SkipWorker | Out-Host
    foreach ($entry in Get-ChildItem -LiteralPath $workerPackageRoot -Force) {
        if ($entry.Name -in @('app','native','models','translation-runtime','commands','phrase-packs','licenses','setup','BUILD-INFO.json','RELEASE-MANIFEST.sha256','使用说明.txt','验证范围.txt','安装输入法.cmd','卸载输入法.cmd','打开设置.cmd')) { continue }
        Copy-Item -LiteralPath $entry.FullName -Destination $output -Recurse -Force
    }
}

$nativePackage = Join-Path $output 'native'
New-Item -ItemType Directory -Force -Path $nativePackage | Out-Null
Copy-Item -LiteralPath $nativeDll -Destination $nativePackage
foreach ($name in @('install.ps1','uninstall.ps1')) {
    Copy-Item -LiteralPath (Join-Path $nativeRoot $name) -Destination $nativePackage
}
[IO.File]::WriteAllText((Join-Path $nativePackage 'ClaudeZhInput.NativeCandidate.pipe'),'ClaudeZhInput',[Text.UTF8Encoding]::new($false))
$commandsOut = Join-Path $output 'commands'
New-Item -ItemType Directory -Force -Path $commandsOut | Out-Null
$catalog = Get-Content -LiteralPath $commandsSource -Raw | ConvertFrom-Json
if ($catalog.verification) {
    # The source catalog records the author's machine path; a share package must not expose it.
    $catalog.verification.localExecutable = 'claude.exe'
}
[IO.File]::WriteAllText((Join-Path $commandsOut 'claude-commands.json'),
    ($catalog | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
$llmInfo = & (Join-Path $scriptRoot 'stage-translation-runtime.ps1') -OutputRoot $output -Quantization $ModelQuantization
$publicPacks = Join-Path $output 'phrase-packs'
New-Item -ItemType Directory -Force -Path $publicPacks | Out-Null
foreach ($publicPackFile in @('common-communication.zh-en.json','my-phrases.example.json','使用说明.txt')) {
    Copy-Item -LiteralPath (Join-Path $project ('phrase-packs/' + $publicPackFile)) -Destination $publicPacks
}
& (Join-Path $scriptRoot 'collect-licenses.ps1') -ProjectRoot $project -PackageRoot $output -SourceLicenseRoot $licensesSource -PrimaryTranslation $llmInfo | Out-Host

$appForbidden = Get-ChildItem -LiteralPath $appStage -Recurse -File |
    Where-Object { $_.Extension.ToLowerInvariant() -in @('.pdb','.cs','.csproj','.xaml') }
if ($appForbidden) { $appForbidden | Remove-Item -Force }
Copy-Tree $appStage (Join-Path $output 'app')
$packageForbidden = Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { $_.Extension.ToLowerInvariant() -in @('.py','.pyc','.pyo','.pdb','.cs','.csproj','.xaml') -and
        $_.FullName -notlike "$(Join-Path $output 'licenses')*" }
if ($packageForbidden) { $packageForbidden | Remove-Item -Force }

$requiredFinal = @(
    (Join-Path $output 'native/ClaudeZhInput.NativeCandidate.dll'),
    (Join-Path $output 'app/ClaudeZhInput.exe'),
    (Join-Path $output 'worker.exe'),
    (Join-Path $output 'phrases.dat'),
    (Join-Path $output 'commands/claude-commands.json'),
    (Join-Path $output 'app/ClaudeZhInput.Rime.dll'),
    (Join-Path $output 'app/runtime/rime.dll'),
    (Join-Path $output 'app/runtime/data/build/claude_pinyin.schema.yaml'),
    (Join-Path $output 'app/runtime/data/build/pinyin_simp.table.bin'),
    (Join-Path $output 'app/runtime/licenses/librime-BSD-3-Clause.txt'),
    (Join-Path $output 'app/runtime/licenses/rime-pinyin-simp-Apache-2.0.txt'),
    (Join-Path $output 'models/translation-llm/model.gguf'),
    (Join-Path $output 'translation-runtime/ClaudeZhInput.Translate.exe'),
    (Join-Path $output 'licenses/THIRD_PARTY_MANIFEST.txt')
)
foreach ($file in $requiredFinal) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "保护包缺少必需文件：$file" } }

@{
    version = ([xml](Get-Content -LiteralPath $csproj -Raw)).Project.PropertyGroup.Version
    phrasesSourceSha256 = (Get-FileHash -LiteralPath (Join-Path $project 'phrases.json')).Hash
    commonPhrasePackSha256 = (Get-FileHash -LiteralPath (Join-Path $publicPacks 'common-communication.zh-en.json')).Hash
    workerProfile = 'stdlib-llm-v1'
    legacyTranslationIncluded = $false
    appRuntime = 'self-contained-net8-windows-x64'
    primaryTranslation = $llmInfo
    inputLayer = 'native-rime-v2'
    nativeSha256 = (Get-FileHash -LiteralPath $nativeDll).Hash
    sourceSha256 = @{
        worker = (Get-FileHash -LiteralPath $workerSource).Hash
        translationText = (Get-FileHash -LiteralPath (Join-Path $project 'translation_text.py')).Hash
        translationNative = (Get-FileHash -LiteralPath (Join-Path $project 'translation-native/Translator.cpp')).Hash
        core = (Get-FileHash -LiteralPath (Join-Path $src 'Core.cs')).Hash
        strictTemplates = (Get-FileHash -LiteralPath (Join-Path $src 'StrictPhraseTemplates.cs')).Hash
        builtInPhrases = (Get-FileHash -LiteralPath $builtInSource).Hash
        phrasePacks = (Get-FileHash -LiteralPath (Join-Path $src 'PhrasePacks.cs')).Hash
        phrasePackSettings = (Get-FileHash -LiteralPath (Join-Path $src 'MainWindow.PhrasePacks.cs')).Hash
        workerProcessJob = (Get-FileHash -LiteralPath (Join-Path $src 'WorkerProcessJob.cs')).Hash
        coordinator = (Get-FileHash -LiteralPath (Join-Path $src 'RimeSessionCoordinator.cs')).Hash
        bridge = (Get-FileHash -LiteralPath (Join-Path $src 'BridgeServer.cs')).Hash
        app = (Get-FileHash -LiteralPath (Join-Path $src 'App.xaml.cs')).Hash
        toolbar = (Get-FileHash -LiteralPath (Join-Path $src 'StatusBarWindow.xaml.cs')).Hash
        toolbarPosition = (Get-FileHash -LiteralPath (Join-Path $src 'ToolbarPositionStore.cs')).Hash
        translationReview = (Get-FileHash -LiteralPath (Join-Path $src 'TranslationReview.cs')).Hash
        productMaintenance = (Get-FileHash -LiteralPath (Join-Path $src 'ProductMaintenance.cs')).Hash
        settings = (Get-FileHash -LiteralPath (Join-Path $src 'MainWindow.xaml.cs')).Hash
        settingsView = (Get-FileHash -LiteralPath (Join-Path $src 'MainWindow.xaml')).Hash
        toolbarView = (Get-FileHash -LiteralPath (Join-Path $src 'StatusBarWindow.xaml')).Hash
        skins = (Get-FileHash -LiteralPath (Join-Path $src 'Skins.cs')).Hash
        fontIdentity = (Get-FileHash -LiteralPath (Join-Path $src 'Fonts/font-build.json')).Hash
        native = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'NativeCandidate.cpp')).Hash
        nativeHeader = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'NativeCandidate.h')).Hash
        routing = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'KeyRouting.h')).Hash
        serviceStartup = (Get-FileHash -LiteralPath (Join-Path $nativeRoot 'ServiceStartup.h')).Hash
    }
    builtAt = (Get-Date).ToUniversalTime().ToString('o')
    dotnetProtection = 'Obfuscar 2.2.50; WPF App/MainWindow and anonymous JSON types preserved'
    pythonProtection = if ($SkipPythonCompile) { 'prebuilt worker.exe supplied by caller' } else { "Nuitka $NuitkaVersion standalone" }
    commandCatalog = 'commands/claude-commands.json copied; local executable path scrubbed'
    rime = 'librime 1.17.0 official DLL kept unmodified; prebuilt pinyin data and notices staged under app/runtime; ClaudeZhInput.Rime interop layout preserved'
    native = 'Native C++ TSF Rime; whole-program optimized machine code; no CodeView/PDB debug directory; no source files'
    model = ('Primary: '+$llmInfo.engine+', CPU llama.cpp b11580, private stdio. No legacy Argos/CTranslate2 payload. See licenses/redistribution/TRANSLATION-MODEL-NOTICE.txt.')
    userDataIncluded = $false
    businessSourceFilesIncluded = $false
    readableMaintenanceScriptsIncluded = $true
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'BUILD-INFO.json') -Encoding UTF8

$hashPath = Join-Path $output 'RELEASE-MANIFEST.sha256'
$hashLines = Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { $_.FullName -ne $hashPath } | Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($output.Length).TrimStart('\','/').Replace('\','/')
        $digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$digest  $relative"
    }
$hashLines | Set-Content -LiteralPath $hashPath -Encoding UTF8

$forbidden = Get-ChildItem -LiteralPath $output -Recurse -File |
    Where-Object { $_.Extension.ToLowerInvariant() -in @('.py','.pyc','.pyo','.pdb','.cs','.csproj','.xaml') }
if ($forbidden) { throw "保护包仍包含源码或调试文件：$($forbidden.FullName -join ', ')" }

if (!$SkipSmoke) {
    $smokeSnapshot = Join-Path $staging 'smoke.png'
    $appExe = Join-Path $output 'app/ClaudeZhInput.exe'
    $smokeProcess = Start-Process -FilePath $appExe -ArgumentList @('--snapshot',$smokeSnapshot) -WorkingDirectory $output -WindowStyle Hidden -PassThru
    if (!$smokeProcess.WaitForExit(120000)) {
        if (!$smokeProcess.HasExited) { $smokeProcess.Kill($true) }
        throw '保护包 WPF snapshot 超过 120 秒。'
    }
    if ($smokeProcess.ExitCode -ne 0) { throw "保护包 WPF snapshot 退出码为 $($smokeProcess.ExitCode)。" }
    if (!(Test-Path -LiteralPath $smokeSnapshot -PathType Leaf) -or (Get-Item -LiteralPath $smokeSnapshot).Length -le 0) {
        throw '保护包 WPF snapshot 没有生成有效图片。'
    }
}

Write-Output (ConvertTo-Json @{
    output = $output
    staging = $staging
    worker = (Get-Item -LiteralPath (Join-Path $output 'worker.exe')).Length
    primaryModel = $llmInfo.model
    smoke = !$SkipSmoke
    sourceFiles = $false
} -Compress)

if (!$KeepStaging) {
    # This exact generated directory is disposable; leave it when a step throws for diagnosis.
    Remove-Item -LiteralPath $staging -Recurse -Force
}
