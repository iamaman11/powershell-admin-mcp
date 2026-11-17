param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Sha256)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'CI_ONLY'}
. (Join-Path $PSScriptRoot '../installer/NativeFileSwap.ps1')
$installer=Join-Path $PSScriptRoot '../installer/Install-Native.ps1'
$root=Join-Path $env:ProgramFiles 'WindowsAdminMCP'
if(Test-Path -LiteralPath $root){throw 'FIXTURE_ROOT_EXISTS'}
if(Get-Service WindowsAdminMCP -ErrorAction SilentlyContinue){throw 'FIXTURE_SERVICE_EXISTS'}
function Check($ok,$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
$verified=& $installer -PackagePath $Package -ExpectedSha256 $Sha256|ConvertFrom-Json
Check ($verified.status -eq 'VERIFIED' -and -not(Test-Path -LiteralPath $root)) 'r6.package_verify_no_install'
$rejected=$false
try{& $installer -PackagePath $Package -ExpectedSha256 ('0'*64)|Out-Null}catch{if($_.Exception.Message -eq 'PACKAGE_SHA256_MISMATCH'){$rejected=$true}else{throw}}
Check $rejected 'r6.wrong_package_digest_rejected'
$fixture=Join-Path $env:RUNNER_TEMP ('swap-'+[guid]::NewGuid().ToString('N'))
$old=Join-Path $fixture 'root';$new=Join-Path $fixture 'incoming'
try{
    foreach($dir in @($old,$new)){
        foreach($name in @('native','client')){[void][IO.Directory]::CreateDirectory((Join-Path $dir $name))}
        [IO.File]::WriteAllText((Join-Path $dir 'manifest.json'),$dir)
    }
    [IO.File]::WriteAllText((Join-Path $old 'client\old.txt'),'old')
    [IO.File]::WriteAllText((Join-Path $new 'client\new.txt'),'new')
    [IO.File]::WriteAllText((Join-Path $old 'sentinel.txt'),'keep')
    $rolled=$false
    try{Invoke-NativeFileSwap -Root $old -Incoming $new -Validate {throw 'FIXTURE_VALIDATION_FAILED'}}catch{if($_.Exception.Message -eq 'FIXTURE_VALIDATION_FAILED'){$rolled=$true}else{throw}}
    Check ($rolled -and (Test-Path (Join-Path $old 'client\old.txt')) -and -not(Test-Path (Join-Path $old 'client\new.txt')) -and (Test-Path (Join-Path $new 'client\new.txt')) -and -not(Test-Path (Join-Path $old '.rollback')) -and (Get-Content (Join-Path $old 'sentinel.txt') -Raw) -eq 'keep') 'r6.actual_file_swap_rollback'
}finally{Remove-Item -LiteralPath $fixture -Recurse -Force}
try{
    $result=& $installer -Action Install -PackagePath $Package -ExpectedSha256 $Sha256|ConvertFrom-Json
    $svc=Get-CimInstance Win32_Service -Filter "Name='WindowsAdminMCP'"
    Check ($result.status -eq 'INSTALLED_OFFLINE' -and $svc.StartMode -eq 'Manual' -and $svc.State -eq 'Stopped' -and $svc.StartName -eq 'LocalSystem') 'r6.actual_offline_install'
    Check ((Test-Path (Join-Path $root 'client\tunnel-client-runtime.exe')) -and (Test-Path (Join-Path $root 'native\WindowsAdminMCP.exe')) -and (Test-Path (Join-Path $root '.rollback'))) 'r6.stable_install_paths_and_backup'
    Check (-not(Get-ScheduledTask -TaskName 'WindowsAdminMCP-Tunnel' -ErrorAction SilentlyContinue)) 'r6.no_unaccepted_autostart'
}finally{
    if(Get-Service WindowsAdminMCP -ErrorAction SilentlyContinue){& sc.exe delete WindowsAdminMCP|Out-Host}
    if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
}
