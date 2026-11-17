[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assemblyDir = Join-Path $root '1.6/Assemblies'
$libraries = @(Get-ChildItem -LiteralPath $assemblyDir -Filter '*.dll')
if ($libraries.Count -ne 1 -or $libraries[0].Name -ne 'DirectedSearchlights.dll') {
    throw 'Distribute only DirectedSearchlights.dll in the game assembly folder.'
}
$assembly = [Reflection.AssemblyName]::GetAssemblyName($libraries[0].FullName)
if ($assembly.Name -ne 'DirectedSearchlights') { throw 'Unexpected assembly identity.' }

$defs = @{}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root '1.6/Defs') -Recurse -Filter '*.xml') {
    $xml = [xml](Get-Content -LiteralPath $file.FullName -Encoding utf8 -Raw)
    foreach ($def in $xml.SelectNodes('/Defs/ThingDef[defName]')) {
        $name = $def.defName
        if ($defs.ContainsKey($name)) { throw "Duplicate ThingDef $name" }
        $defs[$name] = $def
        $texture = $def.SelectSingleNode('graphicData/texPath')
        if ($texture) {
            $path = Join-Path $root ('Textures/' + $texture.InnerText + '.png')
            if (-not (Test-Path -LiteralPath $path)) { throw "Missing texture for $name at $path" }
        }
    }
}
foreach ($name in 'DS_SearchlightTurret','DS_SearchlightTower','DS_LightSpot') {
    if (-not $defs.ContainsKey($name)) { throw "Missing $name" }
}
$about = [xml](Get-Content -LiteralPath (Join-Path $root 'About/About.xml') -Encoding utf8 -Raw)
if ('1.6' -notin $about.ModMetaData.supportedVersions.li) { throw 'Expected 1.6 support in About.xml.' }
Write-Host "Verified $($assembly.Name), $($defs.Count) concrete ThingDefs and their declared textures."
