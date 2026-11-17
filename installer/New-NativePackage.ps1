# Build the one offline deployment package from the already tested publish tree.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,
      [Parameter(Mandatory)][string]$Output,
      [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$Commit)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'PACKAGE_ALREADY_EXISTS'}
$parent=Split-Path -Parent $Output
[void][IO.Directory]::CreateDirectory($parent)
$stage=Join-Path $parent ('build-'+[guid]::NewGuid().ToString('N'))
$zip=$stage+'.vendor.zip'
try{
    [void][IO.Directory]::CreateDirectory($stage)
    $native=Join-Path $stage 'native';$client=Join-Path $stage 'client'
    [void][IO.Directory]::CreateDirectory($native);[void][IO.Directory]::CreateDirectory($client)
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $native -Recurse
    if(-not(Test-Path -LiteralPath (Join-Path $native 'WindowsAdminMCP.exe') -PathType Leaf)){throw 'PUBLISH_EXE_MISSING'}
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NativeStartup.ps1') -Destination $native
    Invoke-WebRequest -Uri 'https://github.com/openai/tunnel-client/releases/download/v0.0.21/tunnel-client-runtime-v0.0.21-windows-amd64.zip' -OutFile $zip
    if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne 'D991623BD171049E2100B402D72AF7EBC8EDF558051AE8FC924173373D1611A8'){throw 'VENDOR_ZIP_HASH_INVALID'}
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try{
        $entries=@($archive.Entries|Where-Object Name -ceq 'tunnel-client-runtime.exe')
        if($entries.Count -ne 1){throw 'VENDOR_EXE_MISSING'}
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0],(Join-Path $client 'tunnel-client-runtime.exe'))
    }finally{$archive.Dispose()}
    if((Get-FileHash -LiteralPath (Join-Path $client 'tunnel-client-runtime.exe')).Hash -ne 'DB590A52993C830A4C806108C3F25345EA986C6248210B86DF640E5081FF6A0B'){throw 'VENDOR_EXE_HASH_INVALID'}
    $files=[ordered]@{}
    foreach($file in (Get-ChildItem -LiteralPath $stage -File -Recurse|Sort-Object FullName)){
        $relative=[IO.Path]::GetRelativePath($stage,$file.FullName).Replace('\','/')
        $files[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $metadata=[ordered]@{schema=1;commit=$Commit;vendor_version='0.0.21';files=$files}
    [IO.File]::WriteAllText((Join-Path $stage 'manifest.json'),($metadata|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    [IO.Compression.ZipFile]::CreateFromDirectory($stage,$Output,[IO.Compression.CompressionLevel]::Optimal,$false)
    [pscustomobject]@{package=$Output;sha256=(Get-FileHash -LiteralPath $Output).Hash;files=$files.Count;commit=$Commit}|ConvertTo-Json -Compress
}finally{
    if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}
    if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
}
