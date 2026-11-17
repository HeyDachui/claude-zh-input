[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [switch]$SkipSmoke,
    [switch]$SkipWorker
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$required = @(
    'native/ClaudeZhInput.NativeCandidate.dll',
    'native/ClaudeZhInput.NativeCandidate.pipe',
    'app/ClaudeZhInput.exe',
    'app/ClaudeZhInput.Rime.dll',
    'app/runtime/rime.dll',
    'app/runtime/data/build/claude_pinyin.schema.yaml',
    'app/runtime/data/build/pinyin_simp.table.bin',
    'worker.exe',
    'phrases.dat',
    'commands/claude-commands.json',
    'licenses/README.md',
    'licenses/THIRD_PARTY_MANIFEST.txt',
    'licenses/librime-BSD-3-Clause.txt',
    'licenses/rime-pinyin-simp-Apache-2.0.txt',
    'licenses/THIRD-PARTY-NOTICES.md',
    'RELEASE-MANIFEST.sha256'
)
foreach ($relative in $required) {
    $file = Join-Path $package $relative
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "保护包缺少：$relative" }
}

$sourceFiles = Get-ChildItem -LiteralPath $package -Recurse -File |
    Where-Object { $_.Extension.ToLowerInvariant() -in @('.py','.pyc','.pyo','.pdb','.cs','.csproj','.xaml','.h','.hpp','.lib') }
if ($sourceFiles) { throw "保护包包含源码或调试文件：$($sourceFiles.FullName -join ', ')" }

$rimeRuntime = Join-Path $package 'app/runtime'
$rimeDevelopmentFiles = Get-ChildItem -LiteralPath $rimeRuntime -Recurse -File |
    Where-Object { $_.FullName -match '[\\/]include[\\/]|[\\/]tools[\\/]' -or $_.Name -eq 'MANIFEST.json' }
if ($rimeDevelopmentFiles) {
    throw "Rime 运行目录包含开发文件：$($rimeDevelopmentFiles.FullName -join ', ')"
}

$catalog = Get-Content -LiteralPath (Join-Path $package 'commands/claude-commands.json') -Raw | ConvertFrom-Json
$localExecutable = [string]$catalog.verification.localExecutable
if ($localExecutable -match '[:\\/]') { throw '命令目录仍包含构建机绝对路径。' }

$manifest = Join-Path $package 'RELEASE-MANIFEST.sha256'
$buildInfo = Get-Content -LiteralPath (Join-Path $package 'BUILD-INFO.json') -Raw | ConvertFrom-Json
if ($buildInfo.workerProfile -eq 'stdlib-llm-v1') {
    foreach($legacy in @('models/translate-zh_en-1_9','ctranslate2.dll','ctranslate2','numpy','sentencepiece')) {
        if(Test-Path -LiteralPath (Join-Path $package $legacy)){throw 'Compact package contains legacy dependencies'}
    }
} elseif(-not (Test-Path -LiteralPath (Join-Path $package 'models/translate-zh_en-1_9/model/model.bin'))) {
    throw 'Legacy package model is missing'
}
if ($buildInfo.primaryTranslation) {
    $llm=$buildInfo.primaryTranslation
    if (!$llm.cpuOnly -or $llm.transport -ne 'private-stdio') { throw 'Unexpected primary translation transport' }
    $model=Join-Path $package $llm.model.file
    if((Get-FileHash -LiteralPath $model).Hash -ne $llm.model.sha256){throw 'Primary model identity mismatch'}
    foreach($file in $llm.files){
        if((Get-FileHash -LiteralPath (Join-Path $package $file.file)).Hash -ne $file.sha256){throw 'Primary runtime identity mismatch'}
    }
    if(Get-ChildItem -LiteralPath (Join-Path $package 'translation-runtime') -File | Where-Object {$_.Name -match 'server|rpc'}){throw 'Network inference components must not be shipped'}
}
if ($buildInfo.inputLayer -ne 'native-rime-v2' -or
    $buildInfo.nativeSha256 -ne (Get-FileHash -LiteralPath (Join-Path $package 'native/ClaudeZhInput.NativeCandidate.dll')).Hash) {
    throw 'Package input-layer identity or native build hash is inconsistent.'
}
if ([IO.File]::ReadAllText((Join-Path $package 'native/ClaudeZhInput.NativeCandidate.pipe')).Trim() -ne 'ClaudeZhInput') {
    throw 'Native pipe name does not match the formal backend.'
}
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts = $line -split '\s{2,}', 2
    if ($parts.Count -ne 2) { throw "哈希清单格式错误：$line" }
    if ($parts[0] -notmatch '^[0-9A-Fa-f]{64}$') { throw "哈希清单摘要格式错误：$line" }
    $file = Join-Path $package ($parts[1] -replace '/', '\')
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "哈希清单引用不存在：$($parts[1])" }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $parts[0].ToLowerInvariant()) { throw "文件哈希不一致：$($parts[1])" }
}

$workerEngine = 'not executed'
if (!$SkipWorker) {
$worker = Join-Path $package 'worker.exe'
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $worker
$psi.WorkingDirectory = $package
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$psi.Environment['PYTHONUTF8'] = '1'
$psi.Environment['PYTHONIOENCODING'] = 'utf-8'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
if (!$process.Start()) { throw '无法启动保护后的 worker.exe' }
try {
    # Keep requests ASCII-only: JSON Unicode escapes exercise Chinese input while
    # avoiding the host PowerShell console code page at the pipe boundary.
    $requests = @(
        '{"text":"\u5148\u68c0\u67e5\u767b\u5f55\u5931\u8d25\u7684\u539f\u56e0\uff0c\u4e0d\u8981\u4fee\u6539\u4ee3\u7801\u3002"}',
        '{"text":"\u628a `src/\u767b\u5f55.ts` \u91cc\u7684 `timeoutMs` \u6539\u621030\u79d2\u3002"}'
    )
    foreach ($request in $requests) { $process.StandardInput.WriteLine($request) }
    $process.StandardInput.Flush()
    $results = @()
    foreach ($request in $requests) {
        $readTask = $process.StandardOutput.ReadLineAsync()
        if (!$readTask.Wait(30000)) { throw 'worker.exe 30 秒内没有返回 JSON。' }
        $line = $readTask.Result
        if ([string]::IsNullOrWhiteSpace($line)) { throw 'worker.exe 没有返回 JSON。' }
        $results += ($line | ConvertFrom-Json)
    }
    $result = $results[0]
    foreach ($item in $results) {
        if ([string]::IsNullOrWhiteSpace($item.text) -or $item.error) { throw "worker.exe 返回错误：$($item | ConvertTo-Json -Compress)" }
    }
    $literalResult = $results[1].text
    $expectedFile = 'src/' + [string][char]0x767B + [string][char]0x5F55 + '.ts'
    if (!$literalResult.Contains($expectedFile) -or !$literalResult.Contains('timeoutMs')) {
        throw "worker.exe 未精确保留文件名或代码标识符：$literalResult"
    }
    $workerEngine = $result.engine
} finally {
    if (!$process.HasExited) { $process.Kill($true) }
    $process.Dispose()
}
}

if (!$SkipSmoke) {
    $snapshot = Join-Path $package 'verify-smoke.png'
    $p = Start-Process -FilePath (Join-Path $package 'app/ClaudeZhInput.exe') -ArgumentList @('--snapshot',$snapshot) -WorkingDirectory $package -WindowStyle Hidden -Wait -PassThru
    if ($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $snapshot -PathType Leaf) -or (Get-Item -LiteralPath $snapshot).Length -le 0) {
        throw 'WPF 保护包 snapshot 失败。'
    }
}

Write-Output (ConvertTo-Json @{ package = $package; worker = $workerEngine; smoke = !$SkipSmoke } -Compress)
