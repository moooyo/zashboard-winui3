param(
    [string]$ProjectDirectory = (Join-Path $PSScriptRoot '..\src\Zashboard.App')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedProject = (Resolve-Path -LiteralPath $ProjectDirectory).Path
$manifestPath = Join-Path $resolvedProject 'Package.appxmanifest'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw

$assetPaths = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$attributeNames = @(
    'Logo',
    'Square150x150Logo',
    'Square44x44Logo',
    'Wide310x150Logo',
    'Image'
)

foreach ($attributeName in $attributeNames) {
    $nodes = $manifest.SelectNodes("//@*[local-name()='$attributeName']")
    foreach ($node in $nodes) {
        if (-not [string]::IsNullOrWhiteSpace($node.Value)) {
            [void]$assetPaths.Add($node.Value.Replace('\', [System.IO.Path]::DirectorySeparatorChar))
        }
    }
}

$logoElements = $manifest.SelectNodes("//*[local-name()='Logo']")
foreach ($element in $logoElements) {
    if (-not [string]::IsNullOrWhiteSpace($element.InnerText)) {
        [void]$assetPaths.Add(
            $element.InnerText.Replace('\', [System.IO.Path]::DirectorySeparatorChar))
    }
}

if ($assetPaths.Count -eq 0) {
    throw "No package visual assets were found in '$manifestPath'."
}

[void]$assetPaths.Add(
    ('Assets\AppIcon.ico').Replace('\', [System.IO.Path]::DirectorySeparatorChar))

$results = foreach ($relativePath in $assetPaths | Sort-Object) {
    $fullPath = Join-Path $resolvedProject $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "The package manifest references a missing asset: '$relativePath'."
    }

    $file = Get-Item -LiteralPath $fullPath
    if ($file.Length -eq 0) {
        throw "The package asset is empty: '$relativePath'."
    }

    [PSCustomObject]@{
        Asset = $relativePath
        SizeBytes = $file.Length
    }
}

$results | Format-Table -AutoSize
