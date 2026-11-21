function Test-SamePath {
    param([string]$Left, [string]$Right)

    $leftPath = [IO.Path]::GetFullPath($Left).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rightPath = [IO.Path]::GetFullPath($Right).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $leftPath.Equals($rightPath, [StringComparison]::OrdinalIgnoreCase)
}

function Get-AssemblyMvid {
    param([string]$AssemblyPath)

    $resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
    # Loading from bytes avoids locking a build output or reusing LoadFile's cached assembly
    # when the mod is rebuilt in the same PowerShell session.
    $assemblyBytes = [IO.File]::ReadAllBytes($resolvedAssembly)
    return [Reflection.Assembly]::Load($assemblyBytes).ManifestModule.ModuleVersionId.ToString('D')
}

function Test-ModPathIsCheckout {
    param([IO.FileSystemInfo]$Mod, [string]$Checkout)

    if (Test-SamePath $Mod.FullName $Checkout) { return $true }
    if (($Mod.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { return $false }
    foreach ($target in @($Mod.Target)) {
        if (!$target) { continue }
        if (![IO.Path]::IsPathRooted($target)) { $target = Join-Path $Mod.Parent.FullName $target }
        if (Test-SamePath $target $Checkout) { return $true }
    }
    return $false
}

function Add-SearchlightCheckoutToGame {
    param(
        [string]$GameDir,
        [string]$Checkout,
        [string]$LinkName
    )

    $modsDir = (Resolve-Path -LiteralPath (Join-Path $GameDir 'Mods')).Path
    $checkoutPath = (Resolve-Path -LiteralPath $Checkout).Path
    $checkoutAbout = [xml](Get-Content -LiteralPath (Join-Path $checkoutPath 'About/About.xml') -Raw)
    $packageId = [string]$checkoutAbout.ModMetaData.packageId
    if (!$packageId) { throw "Checkout package ID is missing: $checkoutPath" }

    $checkoutInstalled = $false
    $foreignCopies = [Collections.Generic.List[string]]::new()
    foreach ($candidate in Get-ChildItem -LiteralPath $modsDir -Directory -Force) {
        $aboutPath = Join-Path $candidate.FullName 'About/About.xml'
        if (!(Test-Path -LiteralPath $aboutPath -PathType Leaf)) { continue }
        try { $candidateAbout = [xml](Get-Content -LiteralPath $aboutPath -Raw) }
        catch { continue }
        if (![string]::Equals([string]$candidateAbout.ModMetaData.packageId, $packageId, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (Test-ModPathIsCheckout $candidate $checkoutPath) { $checkoutInstalled = $true }
        else { $foreignCopies.Add($candidate.FullName) }
    }
    if ($foreignCopies.Count -gt 0) {
        throw "Another installed mod uses package ID ${packageId}: $($foreignCopies -join ', ')"
    }
    if ($checkoutInstalled) { return $null }

    $link = Join-Path $modsDir $LinkName
    if (Test-Path -LiteralPath $link) { throw "Refusing to replace existing path: $link" }
    New-Item -ItemType Junction -Path $link -Target $checkoutPath | Out-Null
    return @{ Path = $link; Target = $checkoutPath }
}

function Remove-SearchlightCheckoutJunction {
    param($Junction)

    if ($null -eq $Junction) { return }
    $item = Get-Item -LiteralPath $Junction.Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or !(Test-ModPathIsCheckout $item $Junction.Target)) {
        throw "Unexpected checkout junction target; left intact: $($Junction.Path)"
    }
    [IO.Directory]::Delete($Junction.Path, $false)
}
