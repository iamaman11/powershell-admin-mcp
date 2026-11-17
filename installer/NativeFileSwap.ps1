# A journal records the original presence of the three fixed installation paths.
# Same-volume renames are the only mutations. The caller owns exclusive offline maintenance.
function Get-NativeSwapJournal {
    param([Parameter(Mandatory)][string]$Root)
    $backup=Join-Path $Root '.rollback'
    $journal=Join-Path $backup 'journal.json'
    if(-not(Test-Path -LiteralPath $journal -PathType Leaf)){throw 'ROLLBACK_JOURNAL_MISSING'}
    $file=Get-Item -LiteralPath $journal
    if($file.Length -gt 4096 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'ROLLBACK_JOURNAL_INVALID'}
    $j=Get-Content -LiteralPath $journal -Raw|ConvertFrom-Json -AsHashtable
    if($j.schema -ne 1 -or $j.service_existed -isnot [bool] -or $j.original -isnot [array]){throw 'ROLLBACK_JOURNAL_INVALID'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($name in $j.original){
        if($name -cnotin @('native','client','manifest.json') -or -not $seen.Add($name)){throw 'ROLLBACK_JOURNAL_INVALID'}
    }
    return $j
}
function Move-NativePath($source,$destination){
    if(Test-Path -LiteralPath $destination){throw 'MOVE_DESTINATION_EXISTS'}
    if(Test-Path -LiteralPath $source -PathType Container){[IO.Directory]::Move($source,$destination)}
    else{[IO.File]::Move($source,$destination)}
}
function Assert-NativeSwapTree($path){
    if(-not(Test-Path -LiteralPath $path)){return}
    $item=Get-Item -LiteralPath $path -Force
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'ROLLBACK_REPARSE_POINT'}
    if($item.PSIsContainer){
        foreach($child in Get-ChildItem -LiteralPath $path -Force){Assert-NativeSwapTree $child.FullName}
    }
}
function Restore-NativeFileSwap {
    param([Parameter(Mandatory)][string]$Root,
          [scriptblock]$BeforeRestore={param($journal)})
    $backup=Join-Path $Root '.rollback'
    Assert-NativeSwapTree $backup
    $j=Get-NativeSwapJournal $Root
    $discard=Join-Path $backup 'discard'
    foreach($name in @('native','client','manifest.json')){
        $target=Join-Path $Root $name
        $saved=Join-Path $backup $name
        $gone=Join-Path $discard $name
        Assert-NativeSwapTree $target
        if($name -cin $j.original){
            if(-not(Test-Path -LiteralPath $saved) -and -not(Test-Path -LiteralPath $target)){throw 'ORIGINAL_MISSING'}
            if((Test-Path -LiteralPath $saved) -and (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $gone)){throw 'ROLLBACK_AMBIGUOUS'}
        }elseif(Test-Path -LiteralPath $saved){throw 'ROLLBACK_UNEXPECTED_ORIGINAL'}
        elseif((Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $gone)){throw 'ROLLBACK_AMBIGUOUS'}
    }
    & $BeforeRestore $j
    [void][IO.Directory]::CreateDirectory($discard)
    foreach($name in @('manifest.json','client','native')){
        $target=Join-Path $Root $name
        $saved=Join-Path $backup $name
        $gone=Join-Path $discard $name
        if($name -cin $j.original){
            # Missing saved path + existing target means not moved yet or already restored.
            if(Test-Path -LiteralPath $saved){
                if(Test-Path -LiteralPath $target){Move-NativePath $target $gone}
                Move-NativePath $saved $target
            }
        }elseif(Test-Path -LiteralPath $target){Move-NativePath $target $gone}
    }
    # Retire atomically before cleanup: a process exit during cleanup cannot
    # leave a partial journal that would be interpreted as another transaction.
    $retired=Join-Path $Root ('.rolled-back-'+[guid]::NewGuid().ToString('N'))
    Move-NativePath $backup $retired
    Remove-Item -LiteralPath $retired -Recurse -Force
}
function Invoke-NativeFileSwap {
    param([Parameter(Mandatory)][string]$Root,
          [Parameter(Mandatory)][string]$Incoming,
          [Parameter(Mandatory)][scriptblock]$Validate,
          [bool]$ServiceExisted=$false,
          [scriptblock]$BeforeRestore={param($journal)})
    $backup=Join-Path $Root '.rollback'
    if(Test-Path -LiteralPath $backup){throw 'ROLLBACK_ALREADY_EXISTS'}
    $names=@('native','client','manifest.json')
    $original=@($names|Where-Object {Test-Path -LiteralPath (Join-Path $Root $_)})
    foreach($name in $names){
        Assert-NativeSwapTree (Join-Path $Root $name)
        if(-not(Test-Path -LiteralPath (Join-Path $Incoming $name))){throw 'INCOMING_PATH_MISSING'}
    }
    # Publish the immutable journal before moving any live file.
    $prepare=Join-Path $Root ('.prepare-'+[guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($prepare)
    $journal=@{schema=1;original=$original;service_existed=$ServiceExisted}|ConvertTo-Json -Compress
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes($journal)
    $file=[IO.FileStream]::new((Join-Path $prepare 'journal.json'),[IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
    Move-NativePath $prepare $backup
    try{
        foreach($name in $names){
            $target=Join-Path $Root $name
            if(Test-Path -LiteralPath $target){Move-NativePath $target (Join-Path $backup $name)}
            Move-NativePath (Join-Path $Incoming $name) $target
        }
        & $Validate
    }catch{
        $failure=$_
        Restore-NativeFileSwap -Root $Root -BeforeRestore $BeforeRestore
        throw $failure
    }
}
