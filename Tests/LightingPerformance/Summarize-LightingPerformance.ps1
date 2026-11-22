[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultPath,
    [switch]$EnforceGate
)
$ErrorActionPreference = 'Stop'
$rows = foreach ($line in Get-Content -LiteralPath $ResultPath) {
    if ($line -notmatch ' sample; (.*)$') { continue }
    $data = @{}
    foreach ($field in $Matches[1].Split(';')) {
        $key,$value = $field.Trim().Split('=',2)
        $data[$key] = $value
    }
    [pscustomobject]@{
        Lamps = [int]$data.lamps
        Workload = $data.workload
        Mode = $data.mode
        Repeat = [int]$data.repeat
        Ticks = [int]$data.ticks
        Median = [double]::Parse($data.medianMs.Replace(',','.'), [cultureinfo]::InvariantCulture)
        P95 = [double]::Parse($data.p95Ms.Replace(',','.'), [cultureinfo]::InvariantCulture)
    }
}
function Get-Median([double[]]$Samples) {
    $sorted = @($Samples | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
$pairs = foreach ($group in ($rows | Group-Object Lamps,Workload,Repeat)) {
    $legacy = @($group.Group | Where-Object Mode -eq 'LegacyEndpoint')
    $precise = @($group.Group | Where-Object Mode -eq 'PreciseField')
    if ($legacy.Count -ne 1 -or $precise.Count -ne 1 -or
        $legacy[0].Ticks -ne $precise[0].Ticks -or $legacy[0].Median -le 0 -or $legacy[0].P95 -le 0) {
        throw "Incomplete or invalid measurement pair: $($group.Name)"
    }
    [pscustomobject]@{
        Lamps = $legacy[0].Lamps
        Workload = $legacy[0].Workload
        Ticks = $legacy[0].Ticks
        MedianDelta = 100 * ($precise[0].Median / $legacy[0].Median - 1)
        P95Delta = 100 * ($precise[0].P95 / $legacy[0].P95 - 1)
    }
}
if (!$pairs) { throw "No paired tick samples in $ResultPath" }
$summary = foreach ($group in ($pairs | Group-Object Lamps,Workload)) {
    $median = Get-Median $group.Group.MedianDelta
    $p95 = Get-Median $group.Group.P95Delta
    [pscustomobject]@{
        Lamps = $group.Group[0].Lamps
        Workload = $group.Group[0].Workload
        Repeats = $group.Count
        MinimumTicks = ($group.Group.Ticks | Measure-Object -Minimum).Minimum
        MedianDeltaPercent = [Math]::Round($median,2)
        P95DeltaPercent = [Math]::Round($p95,2)
        WithinBudget = $median -lt 10 -and $p95 -lt 20
    }
}
$summary | Sort-Object Lamps,Workload | Format-Table | Out-String | Write-Output
if ($EnforceGate) {
    $valid = @($summary | Where-Object {
        $_.Lamps -eq 10 -and $_.Repeats -ge 3 -and $_.MinimumTicks -ge 3600 -and $_.WithinBudget
    })
    if (@('Static','Rotating','MovingTarget','ToggledDoor' | Where-Object { $_ -notin $valid.Workload }).Count) {
        throw "Ten-lamp release gate failed or measurements are incomplete. See $ResultPath"
    }
}
