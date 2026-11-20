param(
    [int]$TimeoutSeconds = 420,
    [string]$RimWorldDir = (Join-Path $PSScriptRoot '..\..\..\..')
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $RimWorldDir).Path
$mod = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot '../RunnerModStaging.ps1')
$exe = Join-Path $game 'RimWorld.exe'
if ((Test-Path -LiteralPath $exe -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $game 'Assembly-CSharp.dll') -PathType Leaf)) {
    $gameVersion = '1.7'
} else {
    $exe = Join-Path $game 'RimWorldWin64.exe'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $game 'RimWorldWin64_Data/Managed/Assembly-CSharp.dll') -PathType Leaf)) {
        throw "The optics harness requires a RimWorld 1.6 or 1.7 installation: $game"
    }
    $gameVersion = '1.6'
}
$expectedMvid = Get-AssemblyMvid (Join-Path $mod "$gameVersion/Assemblies/DirectedSearchlights.dll")
$profile = Join-Path $env:TEMP ('SearchlightOptics-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $profile 'Config'
$harness = Join-Path $profile 'HarnessMod'
$assemblyDir = Join-Path $harness 'Assemblies'
New-Item -ItemType Directory -Path $config, (Join-Path $harness 'About'), $assemblyDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $config 'Prefs.xml'), '<PrefsData><runInBackground>true</runInBackground></PrefsData>')
Copy-Item (Join-Path $PSScriptRoot 'HarnessMod/About/About.xml') (Join-Path $harness 'About/About.xml')
dotnet build (Join-Path $PSScriptRoot 'OpticsHarness.csproj') "-p:RimWorldDir=$game" "-p:GameVersion=$gameVersion" "-p:OutputPath=$assemblyDir/" "-p:BaseIntermediateOutputPath=$profile/obj/" -v:q
if ($LASTEXITCODE -ne 0) { throw 'Optics harness build failed' }
$mods = @('brrainz.harmony', 'ludeon.rimworld', 'CETeam.CombatExtended', 'DirectedSearchlights.Searchlight', 'searchlight.opticsharness')
$items = ($mods | ForEach-Object { '    <li>' + $_ + '</li>' }) -join "`r`n"
$xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<ModsConfigData>`r`n  <version>$gameVersion</version>`r`n  <activeMods>`r`n$items`r`n  </activeMods>`r`n</ModsConfigData>`r`n"
[IO.File]::WriteAllText((Join-Path $config 'ModsConfig.xml'), $xml, [Text.UTF8Encoding]::new($false))
$link = Join-Path $game 'Mods/SearchlightOpticsHarness'
$resolvedHarness = (Resolve-Path -LiteralPath $harness).Path
$createdLink = $false
$checkoutJunction = $null
$proc = $null
try {
    $checkoutJunction = Add-SearchlightCheckoutToGame $game $mod 'SearchlightOpticsMod'
    if (Test-Path -LiteralPath $link) { throw "Refusing to replace existing path: $link" }
    New-Item -ItemType Junction -Path $link -Target $resolvedHarness | Out-Null
    $createdLink = $true
    $result = Join-Path $profile 'optics-results.txt'
    $argLine = '-quicktest -savedatafolder="' + $profile + '" -ds-optics-result="' + $result + '"'
    if ($gameVersion -eq '1.6') {
        $argLine += ' -logFile "' + (Join-Path $profile 'Player.log') + '" -screen-fullscreen 0 -screen-width 1280 -screen-height 800'
    }
    Write-Output "Profile: $profile"
    $proc = Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $game -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $profile 'console.log') -RedirectStandardError (Join-Path $profile 'console-error.log')
    $null = $proc.Handle
    if ($gameVersion -eq '1.7') { & (Join-Path $PSScriptRoot '../Enable-TestRendering.ps1') -GameProcess $proc }
    if (!$proc.WaitForExit($TimeoutSeconds * 1000)) {
        $proc.Kill()
        $proc.WaitForExit()
        throw "Game timeout after $TimeoutSeconds seconds; profile $profile"
    }
    Write-Output "Exit code: $($proc.ExitCode)"
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    $resultText = if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Raw } else { '' }
    if ($proc.ExitCode -ne 0 -or $resultText -notmatch ' PASS;' -or
        $resultText -notmatch ('assemblyMvid=' + [regex]::Escape($expectedMvid) + '(?:\s|;)')) {
        throw "Optics game did not pass with this checkout's assembly; profile $profile"
    }
}
finally {
    if ($proc -and -not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
    if ($createdLink -and (Test-Path -LiteralPath $link)) {
        $item = Get-Item -LiteralPath $link -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or $item.Target -ne $resolvedHarness) { throw "Unexpected junction target; left intact: $link" }
        [IO.Directory]::Delete($link, $false)
    }
    Remove-SearchlightCheckoutJunction $checkoutJunction
}
