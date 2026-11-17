#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProgramRoot,
    [string]$ModelPackRoot,
    [string]$ExistingOfflinePackage,
    [switch]$ValidateOnly,
    [switch]$LibraryOnly
)

$ErrorActionPreference = 'Stop'
$script:SplitModelPath = 'models/translation-llm/model.gguf'
$script:SplitModelFiles = @(
    $script:SplitModelPath,
    'licenses/redistribution/Qwen3.5-4B-Apache-2.0.txt',
    'models/translation-llm/MODEL-NOTICE.txt'
)
$script:SplitIdentityKeys = @('modelId','sourceRevision','quantizationSource','quantizationRevision','quantization','modelPath','modelBytes','modelSha256','license')

function Get-SplitFullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw '路径不能为空。' }
    if (![IO.Path]::IsPathRooted($Path)) { $Path = Join-Path (Get-Location).Path $Path }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
}
function Test-SplitWithin([string]$Child, [string]$Parent) {
    $c = Get-SplitFullPath $Child; $p = Get-SplitFullPath $Parent
    return $c.Equals($p,[StringComparison]::OrdinalIgnoreCase) -or $c.StartsWith($p + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
}
function Assert-SplitPath([string]$Relative) {
    if ([string]::IsNullOrEmpty($Relative) -or $Relative -match '[\\:\x00-\x1f|]' -or $Relative.StartsWith('/') -or $Relative.EndsWith('/')) { throw "不安全的包内路径：$Relative" }
    foreach ($part in $Relative.Split('/')) {
        if ($part -in @('','.','..') -or $part -match '[. ]$|^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)' -or $part -match '[<>"?*]') { throw "不安全的包内路径：$Relative" }
    }
}
function Assert-SplitAncestors([string]$Path) {
    $current = Get-SplitFullPath $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "包目录不允许链接或重解析点：$current" }
        }
        $parent = Split-Path -Parent $current
        if (!$parent -or $parent -eq $current) { break }; $current = $parent
    }
}
function Get-SplitFiles([string]$Root) {
    $full = Get-SplitFullPath $Root
    Assert-SplitAncestors $full
    if (!(Test-Path -LiteralPath $full -PathType Container)) { throw "包目录不存在：$full" }
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($full)
    $files = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "包中不允许链接或重解析点：$($item.FullName)" }
            $relative = $item.FullName.Substring($full.Length + 1).Replace('\','/')
            Assert-SplitPath $relative
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else {
                if ($files.ContainsKey($relative)) { throw "包路径重复：$relative" }
                $files.Add($relative,$item.FullName)
            }
        }
    }
    return ,$files
}
function Get-SplitHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }
function Assert-SplitPayload([Collections.Generic.Dictionary[string,string]]$Files) {
    $allowedScripts = @('native/install.ps1','native/uninstall.ps1','setup/install.ps1','setup/start.ps1','setup/uninstall.ps1','setup/setup-common.ps1','setup/combine-model-pack.ps1')
    $privateNames = @('phrases.json','mapping.txt','mapping.xml','.env','preferences.json','ime-state.json','toolbar-position.json','behavior-recording.json','user-dictionary.tsv','activity.jsonl')
    foreach ($relative in $Files.Keys) {
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        if ($extension -in @('.py','.pyc','.pyo','.cs','.csproj','.xaml','.c','.cc','.cpp','.h','.hpp','.pdb','.obj','.lib','.map','.sln','.vcxproj') -or
            [IO.Path]::GetFileName($relative).ToLowerInvariant() -in $privateNames -or
            $relative -match '(^|/)(\.git|staging|behavior-log|install-log|user_data)(/|$)' -or
            ($extension -eq '.ps1' -and $relative -notin $allowedScripts)) { throw "发布包包含源码、私人数据或非许可脚本：$relative" }
    }
}
function Assert-SplitManifest([string]$Root, [hashtable]$VerifiedHashes) {
    $files = Get-SplitFiles $Root
    Assert-SplitPayload $files
    if (!$files.ContainsKey('RELEASE-MANIFEST.sha256')) { throw '缺少完整文件清单。' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadAllLines($files['RELEASE-MANIFEST.sha256'],[Text.UTF8Encoding]::new($false,$true))) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw '文件清单格式不正确。' }
        $hash = $Matches[1]; $relative = $Matches[2]; Assert-SplitPath $relative
        if ($relative -eq 'RELEASE-MANIFEST.sha256' -or !$seen.Add($relative) -or !$files.ContainsKey($relative)) { throw "文件清单路径重复、自引用或缺失：$relative" }
        $actual = Get-SplitHash $files[$relative]
        if ($actual -ne $hash) { throw "文件清单哈希不符：$relative" }
        if ($null -ne $VerifiedHashes) { $VerifiedHashes[$relative] = $actual }
    }
    if ($seen.Count -ne $files.Count - 1) { throw '文件清单未完整覆盖包内文件。' }
    return ,$files
}
function Write-SplitAtomic([string]$Path, [byte[]]$Bytes) {
    $temporary = $Path + '.tmp-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllBytes($temporary,$Bytes)
        if (Test-Path -LiteralPath $Path -PathType Leaf) { [IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary,$Path) }
    } finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
}
function Write-SplitManifest([string]$Root) {
    $files = Get-SplitFiles $Root
    $lines = @($files.Keys | Where-Object { $_ -ne 'RELEASE-MANIFEST.sha256' } | Sort-Object | ForEach-Object { (Get-SplitHash $files[$_]) + '  ' + $_ })
    Write-SplitAtomic (Join-Path $Root 'RELEASE-MANIFEST.sha256') ([Text.UTF8Encoding]::new($true).GetBytes(($lines -join "`r`n") + "`r`n"))
}
function Get-SplitIdentity($BuildInfo) {
    $m = $BuildInfo.primaryTranslation.model
    if (!$m -or $BuildInfo.primaryTranslation.offline -ne $true -or $BuildInfo.primaryTranslation.cpuOnly -ne $true -or $BuildInfo.primaryTranslation.transport -ne 'private-stdio') { throw '基线未声明完整离线 CPU 翻译。' }
    $identity = [ordered]@{
        modelId = [string]$m.source; sourceRevision = [string]$m.sourceRevision
        quantizationSource = [string]$m.quantizationSource; quantizationRevision = [string]$m.quantizationRevision
        quantization = [string]$m.quantization; modelPath = [string]$m.file
        modelBytes = [string]$m.bytes; modelSha256 = ([string]$m.sha256).ToUpperInvariant(); license = [string]$m.license
    }
    Assert-SplitIdentity $identity
    return $identity
}
function Assert-SplitIdentity($Identity) {
    foreach ($key in @('modelId','quantizationSource')) { if ($Identity[$key] -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "模型身份格式错误：$key" } }
    foreach ($key in @('sourceRevision','quantizationRevision')) { if ($Identity[$key] -cnotmatch '^[0-9a-f]{40}$') { throw "模型版本格式错误：$key" } }
    if ($Identity['quantization'] -cnotmatch '^[A-Z0-9_]{2,20}$' -or $Identity['modelPath'] -cne $script:SplitModelPath -or
        $Identity['modelBytes'] -cnotmatch '^[1-9][0-9]{0,12}$' -or $Identity['modelSha256'] -cnotmatch '^[0-9A-F]{64}$' -or $Identity['license'] -cne 'Apache-2.0') { throw '模型路径、量化、大小、哈希或许可格式错误。' }
}
function Assert-SplitIdentityEqual($Actual,$Expected) {
    foreach ($key in $script:SplitIdentityKeys) { if ([string]$Actual[$key] -cne [string]$Expected[$key]) { throw "模型包不匹配：$key" } }
}
# Deliberately use a small, strict line format: no JSON duplicate-key or number coercion ambiguity.
# Each key occurs once; each file row is path|decimal bytes|uppercase SHA256.
function Read-SplitMetadata([string]$Path,[ValidateSet('program','model')][string]$Kind) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 32768) { throw '分包元数据缺失或过大。' }
    $allowed = @('format') + $script:SplitIdentityKeys
    if ($Kind -eq 'program') { $allowed += @('productVersion','buildInfoSha256') }
    $data = [ordered]@{}; $rows = [ordered]@{}
    foreach ($line in [IO.File]::ReadAllLines($Path,[Text.UTF8Encoding]::new($false,$true))) {
        if ($line -cnotmatch '^([A-Za-z][A-Za-z0-9]*)=([^\x00-\x1f=]+)$') { throw '分包元数据格式错误。' }
        $key=$Matches[1]; $value=$Matches[2]
        if ($key -ceq 'file') {
            $parts=$value.Split('|')
            if ($parts.Count -ne 3 -or $parts[0] -cnotin $script:SplitModelFiles -or $rows.Contains($parts[0]) -or $parts[1] -cnotmatch '^[1-9][0-9]{0,12}$' -or $parts[2] -cnotmatch '^[0-9A-F]{64}$') { throw '模型文件白名单、大小或摘要格式错误。' }
            $rows[$parts[0]] = [pscustomobject]@{bytes=[long]$parts[1];sha256=$parts[2]}
        } else {
            if ($key -cnotin $allowed -or $data.Contains($key)) { throw "分包元数据包含未知或重复键：$key" }; $data[$key]=$value
        }
    }
    if ($data.Count -ne $allowed.Count -or $rows.Count -ne $script:SplitModelFiles.Count -or $data['format'] -cne ('ClaudeZhInput.' + $Kind + '/1')) { throw '分包元数据字段或格式版本不完整。' }
    Assert-SplitIdentity $data
    if ($rows[$script:SplitModelPath].bytes -ne [long]$data['modelBytes'] -or $rows[$script:SplitModelPath].sha256 -cne $data['modelSha256']) { throw '模型元数据与白名单自相矛盾。' }
    if ($Kind -eq 'program' -and ($data['productVersion'] -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or $data['buildInfoSha256'] -cnotmatch '^[0-9A-F]{64}$')) { throw '程序版本或基线摘要格式错误。' }
    return [pscustomobject]@{data=$data;files=$rows}
}
function Write-SplitMetadata([string]$Path,[string]$Kind,$Identity,$Rows,[string]$Version,[string]$BuildHash) {
    $lines = [Collections.Generic.List[string]]::new(); $lines.Add('format=ClaudeZhInput.' + $Kind + '/1')
    foreach ($key in $script:SplitIdentityKeys) { $lines.Add($key + '=' + $Identity[$key]) }
    if ($Kind -eq 'program') { $lines.Add('productVersion=' + $Version); $lines.Add('buildInfoSha256=' + $BuildHash) }
    foreach ($relative in $script:SplitModelFiles) { $lines.Add('file=' + $relative + '|' + $Rows[$relative].bytes + '|' + $Rows[$relative].sha256) }
    [IO.File]::WriteAllLines($Path,$lines,[Text.UTF8Encoding]::new($true))
}
function Assert-SplitFile([string]$Path,$Row) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -ne $Row.bytes -or (Get-SplitHash $Path) -cne $Row.sha256) { throw "文件大小或 SHA256 校验失败：$Path" }
}
function Assert-SplitModelPack([string]$Root,$Expected) {
    $files = Get-SplitFiles $Root
    if ($files.Count -ne 1 + $script:SplitModelFiles.Count -or !$files.ContainsKey('MODEL-PACK.txt')) { throw '模型包只允许元数据与白名单文件，请勿混入其他内容。' }
    $meta = Read-SplitMetadata $files['MODEL-PACK.txt'] model
    Assert-SplitIdentityEqual $meta.data $Expected.data
    foreach ($relative in $script:SplitModelFiles) {
        if (!$files.ContainsKey($relative) -or $meta.files[$relative].bytes -ne $Expected.files[$relative].bytes -or $meta.files[$relative].sha256 -cne $Expected.files[$relative].sha256) { throw "模型白名单与程序要求不符：$relative" }
        Assert-SplitFile $files[$relative] $meta.files[$relative]
    }
    return $files[$script:SplitModelPath]
}
function Assert-SplitProgram([string]$Root) {
    $files = Assert-SplitManifest $Root
    foreach ($required in @('PROGRAM-PACK.txt','BUILD-INFO.json','models/translation-llm/runtime.json','setup/install.ps1','安装输入法.cmd')) { if (!$files.ContainsKey($required)) { throw "程序包缺少：$required" } }
    $meta=Read-SplitMetadata $files['PROGRAM-PACK.txt'] program
    if ((Get-SplitHash $files['BUILD-INFO.json']) -cne $meta.data['buildInfoSha256']) { throw '程序分包与构建信息不一致。' }
    $build=Get-Content -LiteralPath $files['BUILD-INFO.json'] -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($build.version -cne $meta.data['productVersion']) { throw '程序分包版本不一致。' }
    Assert-SplitIdentityEqual (Get-SplitIdentity $build) $meta.data
    $runtime=Get-Content -LiteralPath $files['models/translation-llm/runtime.json'] -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-SplitIdentityEqual (Get-SplitIdentity @{primaryTranslation=$runtime}) $meta.data
    foreach ($relative in $script:SplitModelFiles | Where-Object { $_ -ne $script:SplitModelPath }) { Assert-SplitFile (Join-Path $Root $relative) $meta.files[$relative] }
    return $meta
}

if ($LibraryOnly) { return }
if (!$ProgramRoot) { $ProgramRoot = Split-Path -Parent $PSScriptRoot }
if ($ModelPackRoot -and $ExistingOfflinePackage) { throw '只能指定一个模型来源：-ModelPackRoot 或 -ExistingOfflinePackage。' }
$program=Get-SplitFullPath $ProgramRoot
Assert-SplitAncestors $program
$lock=$null; $lockPath=$null; $temporary=$null; $addedModel=$false; $originalManifest=$null; $manifestChanged=$false
try {
    if (!$ValidateOnly) {
        $lockPath=Join-Path (Split-Path -Parent $program) ('.' + (Split-Path -Leaf $program) + '.combine.lock')
        $lock=[IO.File]::Open($lockPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    }
    $meta=Assert-SplitProgram $program
    $target=Join-Path $program $script:SplitModelPath
    if (Test-Path -LiteralPath $target) {
        Assert-SplitFile $target $meta.files[$script:SplitModelPath]
        [pscustomobject]@{status='already-complete';program=$program;modelSha256=$meta.data['modelSha256'];installed=$false} | ConvertTo-Json -Compress
        return
    }
    if ($ExistingOfflinePackage) {
        $old=Get-SplitFullPath $ExistingOfflinePackage
        if ((Test-SplitWithin $old $program) -or (Test-SplitWithin $program $old)) { throw '旧离线包与程序包目录不能重叠。' }
        $oldFiles=Assert-SplitManifest $old
        if (!$oldFiles.ContainsKey('BUILD-INFO.json')) { throw '旧离线包缺少构建身份。' }
        $oldInfo=Get-Content -LiteralPath $oldFiles['BUILD-INFO.json'] -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert-SplitIdentityEqual (Get-SplitIdentity $oldInfo) $meta.data
        $source=Join-Path $old $script:SplitModelPath
        Assert-SplitFile $source $meta.files[$script:SplitModelPath]
    } else {
        if (!$ModelPackRoot) { $ModelPackRoot=Join-Path (Split-Path -Parent $program) ('ClaudeZhInput-model-' + $meta.data['modelSha256'].ToLowerInvariant()) }
        $pack=Get-SplitFullPath $ModelPackRoot
        if ((Test-SplitWithin $pack $program) -or (Test-SplitWithin $program $pack)) { throw '模型包与程序包目录不能重叠。' }
        $source=Assert-SplitModelPack $pack $meta
    }
    if ($ValidateOnly) {
        [pscustomobject]@{status='validated';program=$program;modelSource=$source;modelSha256=$meta.data['modelSha256'];installed=$false} | ConvertTo-Json -Compress
        return
    }
    $manifest=Join-Path $program 'RELEASE-MANIFEST.sha256'; $originalManifest=[IO.File]::ReadAllBytes($manifest)
    $temporary=Join-Path (Split-Path -Parent $target) ('.model-combine-' + [guid]::NewGuid().ToString('N') + '.tmp')
    Copy-Item -LiteralPath $source -Destination $temporary
    Assert-SplitFile $temporary $meta.files[$script:SplitModelPath]
    Assert-SplitAncestors (Split-Path -Parent $target)
    [IO.File]::Move($temporary,$target); $temporary=$null; $addedModel=$true
    Write-SplitManifest $program; $manifestChanged=$true
    [void](Assert-SplitProgram $program)
    [pscustomobject]@{status='combined';program=$program;modelSha256=$meta.data['modelSha256'];installed=$false;next='运行本目录的安装输入法.cmd'} | ConvertTo-Json -Compress
} catch {
    if ($addedModel) {
        Assert-SplitAncestors (Split-Path -Parent $target)
        if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
        if ($manifestChanged -and $originalManifest) { Write-SplitAtomic $manifest $originalManifest }
    }
    throw "组合未完成；未执行安装。$($_.Exception.Message)"
} finally {
    if ($temporary -and (Test-Path -LiteralPath $temporary -PathType Leaf)) { Remove-Item -LiteralPath $temporary -Force }
    if ($lock) { $lock.Dispose(); Remove-Item -LiteralPath $lockPath -Force }
}
