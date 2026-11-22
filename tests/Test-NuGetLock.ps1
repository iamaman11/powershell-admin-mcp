# Fail closed if central dependencies drift without a reviewed project lock update.
[CmdletBinding()]
param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('wmcp-lock-'+[guid]::NewGuid().ToString('N'))
try {
    $app=Join-Path $fixture 'src/WindowsAdminMcp'
    [void][IO.Directory]::CreateDirectory($app)
    foreach($name in @('Directory.Build.props','Directory.Packages.props')){
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $fixture
    }
    foreach($name in @('WindowsAdminMcp.csproj','packages.lock.json')){
        Copy-Item -LiteralPath (Join-Path $root "src/WindowsAdminMcp/$name") -Destination $app
    }
    $props=Join-Path $fixture 'Directory.Packages.props'
    $before=[IO.File]::ReadAllText($props)
    $changed=$before.Replace('Version="2.4.0"','Version="2.5.0"')
    if($before -ceq $changed){throw 'DEPENDENCY_MUTATION_FIXTURE_STALE'}
    [IO.File]::WriteAllText($props,$changed,[Text.UTF8Encoding]::new($false))
    $result=(& dotnet restore (Join-Path $app 'WindowsAdminMcp.csproj') -r win-x64 --locked-mode -p:EnableWindowsTargeting=true 2>&1|Out-String)
    if($LASTEXITCODE -eq 0 -or $result -notmatch 'NU1004') {throw 'CHANGED_CENTRAL_PROPS_NOT_REJECTED_BY_LOCK'}
    Write-Host 'NUGET_MUTATED_PROPS_LOCK_REJECTED=PASS (NU1004)'
    # A deliberately failing child must not become the caller's final exit code.
    $global:LASTEXITCODE=0
} finally {
    if(Test-Path -LiteralPath $fixture){Remove-Item -LiteralPath $fixture -Recurse -Force}
}
