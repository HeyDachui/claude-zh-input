#requires -Version 7.0
[CmdletBinding()]
param([string]$VisualStudioRoot = 'C:/BuildTools/VS2022', [int]$Parallel = 4)
$ErrorActionPreference = 'Stop'
$vendor = Join-Path $PSScriptRoot 'vendor-build'
$ct = Join-Path $vendor 'ctranslate2-4.6.0-cpu'
$dnnl = Join-Path $vendor 'onednn-3.1.1-cpu'
$output = Join-Path $vendor 'cpu-runtime-4.6.0'
$cmake = Join-Path $PSScriptRoot '.cmake-tools/cmake/data/bin/cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { throw 'Prepare project-private CMake 3.31.6 first.' }
foreach ($source in @(
    @{path=$ct; revision='617405f4b050e994e829d527da6caa0e0030afe7'},
    @{path=$dnnl; revision='64f6bcbcbab628e96f33a62c3e975f8535a7bde4'}
)) {
    $head = & git -C $source.path rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -ne $source.revision) { throw 'Pinned source checkout missing or mismatched.' }
    $diff = & git -C $source.path diff --name-only HEAD
    if ($LASTEXITCODE -ne 0 -or $diff) { throw 'Upstream source has local edits; review before building.' }
}
# CTranslate2's cpu_features and spdlog submodules must be initialized at the
# revisions pinned by v4.6.0. Ruy/CUDA submodules are not used in this build.
$generator = @('-G','Visual Studio 17 2022','-A','x64',"-DCMAKE_GENERATOR_INSTANCE=$VisualStudioRoot")
& $cmake -S $dnnl -B "$dnnl/build" @generator '-DONEDNN_LIBRARY_TYPE=STATIC' '-DONEDNN_BUILD_EXAMPLES=OFF' '-DONEDNN_BUILD_TESTS=OFF' '-DONEDNN_ENABLE_WORKLOAD=INFERENCE' '-DONEDNN_ENABLE_PRIMITIVE=CONVOLUTION;REORDER;MATMUL' '-DONEDNN_BUILD_GRAPH=OFF' '-DDNNL_CPU_RUNTIME=OMP' "-DCMAKE_INSTALL_PREFIX=$dnnl/install"
if ($LASTEXITCODE -ne 0) { throw 'oneDNN configuration failed.' }
& $cmake --build "$dnnl/build" --config Release --target install --parallel $Parallel
if ($LASTEXITCODE -ne 0) { throw 'oneDNN build failed.' }
$options = @('WITH_MKL=OFF','WITH_DNNL=ON','WITH_RUY=OFF','WITH_CUDA=OFF','WITH_CUDNN=OFF','OPENMP_RUNTIME=COMP','ENABLE_CPU_DISPATCH=ON')
$cmakeOptions = @($options | ForEach-Object { '-D' + $_ })
& $cmake -S $ct -B "$ct/build-onednn" @generator @cmakeOptions '-DBUILD_SHARED_LIBS=ON' '-DBUILD_CLI=OFF' '-DBUILD_TESTS=OFF' '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL' "-DDNNL_INCLUDE_DIR=$dnnl/install/include" "-DDNNL_LIBRARY=$dnnl/install/lib/dnnl.lib"
if ($LASTEXITCODE -ne 0) { throw 'CTranslate2 configuration failed.' }
& $cmake --build "$ct/build-onednn" --config Release --parallel $Parallel
if ($LASTEXITCODE -ne 0) { throw 'CTranslate2 build failed.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath "$ct/build-onednn/Release/ctranslate2.dll" -Destination $output -Force
$redist = Join-Path $VisualStudioRoot 'VC/Redist/MSVC/14.44.35112/x64'
foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')) {
    Copy-Item -LiteralPath (Join-Path "$redist/Microsoft.VC143.CRT" $name) -Destination $output -Force
}
Copy-Item -LiteralPath "$redist/Microsoft.VC143.OpenMP/vcomp140.dll" -Destination $output -Force
$runtimeFiles = @(Get-ChildItem -LiteralPath $output -File -Filter '*.dll' | Where-Object Name -ne 'ctranslate2.dll' | ForEach-Object {
    @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash; version=$_.VersionInfo.FileVersion }
})
@{
    version='4.6.0'; cuda=$false; backend='oneDNN 3.1.1'; openmp='Microsoft OpenMP'
    source='https://github.com/OpenNMT/CTranslate2/tree/v4.6.0'
    revision='617405f4b050e994e829d527da6caa0e0030afe7'
    onednnRevision='64f6bcbcbab628e96f33a62c3e975f8535a7bde4'
    sha256=(Get-FileHash -LiteralPath "$output/ctranslate2.dll").Hash
    runtimeFiles=$runtimeFiles; options=$options
    validation='Build only; validate the actual compiled worker before release.'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$output/CPU-RUNTIME.json" -Encoding utf8
Write-Output $output
