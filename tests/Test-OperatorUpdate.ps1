$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../installer/Update-Native.ps1')
function Check($ok,$name){if(-not $ok){throw "FAIL $name"};Write-Host "PASS $name"}
function Reject([scriptblock]$body,[string]$prefix){try{& $body;return $false}catch{return $_.Exception.Message.StartsWith($prefix,[StringComparison]::Ordinal)}}
$sha='a'*40
$base=[pscustomobject]@{id=12;head_sha=$sha;head_branch='main';event='push';status='completed';conclusion='success'}
$runs=@($base,[pscustomobject]@{id=13;head_sha=$sha;head_branch='main';event='pull_request';status='completed';conclusion='success'},
    [pscustomobject]@{id=14;head_sha=$sha;head_branch='main';event='push';status='completed';conclusion='failure'},
    [pscustomobject]@{id=15;head_sha=('b'*40);head_branch='main';event='push';status='completed';conclusion='success'})
Check ((Select-UpdateRun $runs $sha) -eq 12) 'h5.update_requires_exact_successful_main_push'
Check (Reject {Select-UpdateRun @($runs[1],$runs[2],$runs[3]) $sha} 'UPDATE_MAIN_CI_NOT_ACCEPTED') 'h5.no_unaccepted_fallback'
$path=Join-Path ([IO.Path]::GetTempPath()) ('update-receipt-'+[guid]::NewGuid().ToString('N')+'.json')
try{
    [IO.File]::WriteAllText($path,(@{schema=1;commit=$sha;run_id='12';sha256=('A'*64)}|ConvertTo-Json -Compress))
    Check ((Read-UpdateReceipt $path $sha '12') -ceq ('A'*64)) 'h5.receipt_exact_run'
    Check (Reject {Read-UpdateReceipt $path $sha '13'} 'UPDATE_RECEIPT_MISMATCH') 'h5.receipt_wrong_run_denied'
    Check (Reject {Read-UpdateReceipt $path ('b'*40) '12'} 'UPDATE_RECEIPT_MISMATCH') 'h5.receipt_wrong_commit_denied'
}finally{Remove-Item -LiteralPath $path -Force}
$report=[pscustomobject]@{service=[pscustomobject]@{status='OK'};store=[pscustomobject]@{schemaVersion=4;running=0;queued=0;cancelRequested=0}}
Assert-IdleStore $report
foreach($field in @('running','queued','cancelRequested')){
    $report.store.$field=1
    Check (Reject {Assert-IdleStore $report} 'UPDATE_ACTIVE_JOBS') "h5.block_$field"
    $report.store.$field=0
}
Check (Reject {Assert-IdleStore ([pscustomobject]@{service=$null;store=$null})} 'UPDATE_STORE_UNCONFIRMED') 'h5.fail_closed_store_unconfirmed'
