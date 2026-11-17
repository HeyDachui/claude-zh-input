[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [string]$SourceLicenseRoot,
    [Parameter(Mandatory = $true)]$PrimaryTranslation
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectRoot).Path
$package = (Resolve-Path -LiteralPath $PackageRoot).Path
$runtime = Join-Path $project 'runtime'
$rimeLicenses = Join-Path $project 'rime/runtime/licenses'
$repoLicenses = if ($SourceLicenseRoot) { (Resolve-Path -LiteralPath $SourceLicenseRoot).Path } else { Join-Path $project 'licenses' }
$target = Join-Path $package 'licenses'

New-Item -ItemType Directory -Force -Path $target | Out-Null

function Copy-IfPresent([string]$source, [string]$destination) {
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        $parent = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}

Copy-IfPresent (Join-Path $runtime 'LICENSE.txt') (Join-Path $target 'Python-LICENSE.txt')
Copy-IfPresent (Join-Path $repoLicenses 'README.md') (Join-Path $target 'README.md')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoLicenses 'redistribution') -File) {
    if ($file.Name -match '^(NVIDIA|Intel|ruy|cpuinfo|thrust|cutlass|oneDNN|cpu_features|spdlog|TRANSLATION-MODEL-NOTICE|SOURCES)') { continue }
    Copy-IfPresent $file.FullName (Join-Path $target ('redistribution/' + $file.Name))
}

# librime is a separate third-party runtime. Copy the exact notices beside the
# runtime source, rather than replacing them with a project-authored summary.
$rimeLicenseNames = @(
    'Boost-Software-License-1.0.txt',
    'darts-clone-license.txt',
    'google-glog-BSD-3-Clause.txt',
    'LevelDB-BSD-3-Clause.txt',
    'librime-BSD-3-Clause.txt',
    'marisa-trie-BSD-2-Clause-LGPL-2.1.txt',
    'OpenCC-Apache-2.0.txt',
    'rime-pinyin-simp-Apache-2.0.txt',
    'THIRD-PARTY-NOTICES.md',
    'X11-keysym-notice.txt',
    'yaml-cpp-MIT.txt'
)
foreach ($name in $rimeLicenseNames) {
    $source = Join-Path $rimeLicenses $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Rime 运行时许可证缺失：$source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $target $name) -Force
}

$modelNotice = @"
Primary translation model: $($PrimaryTranslation.engine)
Original: https://huggingface.co/$($PrimaryTranslation.model.source)
Original revision: $($PrimaryTranslation.model.sourceRevision)
Quantized GGUF: https://huggingface.co/$($PrimaryTranslation.model.quantizationSource)
Quantization revision: $($PrimaryTranslation.model.quantizationRevision)
Original quantized filename: $($PrimaryTranslation.model.sourceFile)
SHA-256: $($PrimaryTranslation.model.sha256)
The quantized file is redistributed unchanged under Apache-2.0.
See Qwen3.5-4B-Apache-2.0.txt for the license. The input method author does
not claim private ownership of the model weights.

Runtime: https://github.com/ggml-org/llama.cpp/releases/tag/b11580
Runtime archive SHA-256: $($PrimaryTranslation.runtime.archiveSha256)
Only CPU DLLs and the product's own private-stdio executable are included.
See llama.cpp-b11580-MIT.txt and llama-LLVM-OpenMP.txt for licenses.
No HTTP/RPC inference server or legacy Argos/CTranslate2 payload is shipped.
"@
[IO.File]::WriteAllText((Join-Path $target 'redistribution/TRANSLATION-MODEL-NOTICE.txt'),$modelNotice,[Text.UTF8Encoding]::new($false))

$manifest = Join-Path $target 'THIRD_PARTY_MANIFEST.txt'
@"
Claude 中文输入法保护分享包的运行时第三方清单

本目录从实际运行组件的原始许可复制；版本和模型摘要见 BUILD-INFO.json。Python worker 只使用标准库，不打包旧翻译引擎及其依赖。

运行时组件：
- 主翻译模型 $($PrimaryTranslation.engine)：Apache-2.0；固定来源、版本和摘要见 redistribution/TRANSLATION-MODEL-NOTICE.txt。
- llama.cpp b11580 CPU 推理运行时：MIT；其 LLVM OpenMP 许可见 redistribution/llama-LLVM-OpenMP.txt。仅通过私有标准输入输出接收文字，没有网络服务组件。
- Python 3.11.9：Python Software Foundation License，原件见 Python-LICENSE.txt。
- Python 标准库附带的第三方声明见 redistribution/Python-3.11.9-THIRD-PARTY-NOTICES.rst；OpenSSL 3.0.13 使用 Apache-2.0，原许可随包保留。
- .NET 自包含运行时、Windows Desktop、nlohmann/json、Noto Sans SC 字体：原始许可见 redistribution/；第三方文件保持原许可，不作为本项目自有代码加密。
- librime 1.17.0：官方 Windows MSVC x64 rime.dll，BSD-3-Clause；运行时数据是 rime-pinyin-simp 的 Apache-2.0 词典。其静态依赖 Boost、google-glog、LevelDB、marisa-trie、OpenCC、yaml-cpp 和 darts-clone 的原始许可与版本来源见同目录 THIRD-PARTY-NOTICES.md。本项目不把 librime 或其公开词典宣称为私有实现。

构建工具 Obfuscar 与 Nuitka 不随分享包运行；它们只在构建机上使用，不把构建工具许可冒充运行时组件许可。
"@ | Set-Content -LiteralPath $manifest -Encoding UTF8

Write-Output (ConvertTo-Json @{ target = $target; package = $package } -Compress)
