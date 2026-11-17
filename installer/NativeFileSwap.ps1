# Installer's local file transaction. Only the caller's quiescent protected root
# may be used. Keep old directories on success until separate physical acceptance.
function Invoke-NativeFileSwap {
    param([Parameter(Mandatory)][string]$Root,
          [Parameter(Mandatory)][string]$Incoming,
          [Parameter(Mandatory)][scriptblock]$Validate)
    $backup=Join-Path $Root '.rollback'
    if(Test-Path -LiteralPath $backup){throw 'ROLLBACK_ALREADY_EXISTS'}
    $names=@('native','client','manifest.json')
    $moved=[Collections.Generic.List[string]]::new()
    $installed=[Collections.Generic.List[string]]::new()
    [void][IO.Directory]::CreateDirectory($backup)
    try{
        foreach($name in $names){
            $target=Join-Path $Root $name
            if(Test-Path -LiteralPath $target){
                Move-Item -LiteralPath $target -Destination (Join-Path $backup $name)
                $moved.Add($name)
            }
            Move-Item -LiteralPath (Join-Path $Incoming $name) -Destination $target
            $installed.Add($name)
        }
        & $Validate
    }catch{
        $failure=$_
        # Restore only paths moved by this attempt; never touch data/credentials.
        for($i=$installed.Count-1;$i -ge 0;$i--){
            $name=$installed[$i]
            Move-Item -LiteralPath (Join-Path $Root $name) -Destination (Join-Path $Incoming $name)
        }
        for($i=$moved.Count-1;$i -ge 0;$i--){
            $name=$moved[$i]
            Move-Item -LiteralPath (Join-Path $backup $name) -Destination (Join-Path $Root $name)
        }
        Remove-Item -LiteralPath $backup
        throw $failure
    }
}
