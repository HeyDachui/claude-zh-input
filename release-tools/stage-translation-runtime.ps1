[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputRoot,
    [ValidateSet('Q5_K_M','Q4_K_M','UD-IQ2_M','Q3_K_S')][string]$Quantization='Q5_K_M')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(-not $output.StartsWith($project+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Translation staging must remain inside the project.'}
$vendor=Join-Path $PSScriptRoot 'vendor-build'
$archive=Join-Path $vendor 'llama-b11580-cpu.zip'
$model=Join-Path $vendor ('translation-models\Qwen3.5-4B-'+$Quantization+'.gguf')
$archiveHash='430EFE5B4F10DB617AE0F5732F7E9360FA7BE4344D1D97688E09BBD3B5B084DA'
$profiles=@{
    'Q5_K_M'=@{hash='8814232B85594DCD46C50E5B8B29324A7EFE9E746EDBE8A3D1DF3D3FCE7AAD39';bytes=3143656608}
    'Q4_K_M'=@{hash='00FE7986FF5F6B463E62455821146049DB6F9313603938A70800D1FB69EF11A4';bytes=2740937888}
    'UD-IQ2_M'=@{hash='CB4C45CA42C0D7777667E94E3963F8E59E939B3665098328CE601FEB5BE129C1';bytes=1759997088}
    'Q3_K_S'=@{hash='7E2DD96B6141226B62FA88C775DB06E0E9FF0AA84130805D7091A768868A3353';bytes=2105791648}
}
$modelHash=$profiles[$Quantization].hash
if((Get-FileHash -LiteralPath $archive).Hash -ne $archiveHash){throw 'Pinned llama.cpp archive identity mismatch'}
if((Get-FileHash -LiteralPath $model).Hash -ne $modelHash){throw 'Pinned model identity mismatch'}
& 'C:\BuildTools\VS2022\MSBuild\Current\Bin\MSBuild.exe' (Join-Path $project 'translation-native\Translator.vcxproj') /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo | Out-Host
if($LASTEXITCODE -ne 0){throw 'Native translation host build failed'}
$runtime=Join-Path $output 'translation-runtime'
$modelRoot=Join-Path $output 'models\translation-llm'
New-Item -ItemType Directory -Force -Path $runtime,$modelRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{
    foreach($entry in $zip.Entries){
        if($entry.Name -notmatch '^(llama|ggml|ggml-base|ggml-cpu-[a-z0-9]+|libomp)\.dll$'){continue}
        $target=Join-Path $runtime $entry.Name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
    }
}finally{$zip.Dispose()}
Copy-Item -LiteralPath (Join-Path $project 'translation-native\bin\ClaudeZhInput.Translate.exe') -Destination $runtime -Force
Copy-Item -LiteralPath $model -Destination (Join-Path $modelRoot 'model.gguf') -Force
$info=[ordered]@{
    engine=('Qwen3.5-4B '+$Quantization);offline=$true;transport='private-stdio';cpuOnly=$true;threads=4;promptThreads='auto-physical-performance-cores-max8'
    contextTokens=4096;maxOutputTokens=768
    model=[ordered]@{source='Qwen/Qwen3.5-4B';sourceRevision='851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a';quantizationSource='unsloth/Qwen3.5-4B-GGUF';quantizationRevision='e87f176479d0855a907a41277aca2f8ee7a09523';quantization=$Quantization;sourceFile=('Qwen3.5-4B-'+$Quantization+'.gguf');file='models/translation-llm/model.gguf';bytes=$profiles[$Quantization].bytes;sha256=$modelHash;license='Apache-2.0'}
    runtime=[ordered]@{source='https://github.com/ggml-org/llama.cpp/releases/tag/b11580';version='b11580';archiveSha256=$archiveHash;license='MIT'}
    files=@(Get-ChildItem -LiteralPath $runtime -File|ForEach-Object{[ordered]@{file=('translation-runtime/'+$_.Name);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
[IO.File]::WriteAllText((Join-Path $modelRoot 'runtime.json'),($info|ConvertTo-Json -Depth 7),[Text.UTF8Encoding]::new($false))
return $info
