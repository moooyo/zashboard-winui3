param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform,

    [string]$ExecutableName = 'Zashboard.App.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'NativeAotPe.ps1')

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$dependencySegment = '{0}Dependencies{0}' -f [System.IO.Path]::DirectorySeparatorChar
$packages = @(
    Get-ChildItem -LiteralPath $resolvedPackageDirectory -Filter '*.msix' -File -Recurse |
        Where-Object {
            -not $_.FullName.Contains(
                $dependencySegment,
                [System.StringComparison]::OrdinalIgnoreCase)
        }
)

if ($packages.Count -ne 1) {
    throw "Expected exactly one application MSIX under '$resolvedPackageDirectory', but found $($packages.Count)."
}

$package = $packages[0]
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempRootWithSeparator = $tempRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$extractionDirectory = Join-Path $tempRoot (
    "zashboard-msix-$([System.Guid]::NewGuid().ToString('N'))")
$resolvedExtractionDirectory = [System.IO.Path]::GetFullPath($extractionDirectory)

if (-not $resolvedExtractionDirectory.StartsWith(
    $tempRootWithSeparator,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The MSIX extraction directory escaped the system temporary directory."
}

[void][System.IO.Directory]::CreateDirectory($resolvedExtractionDirectory)
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory(
        $package.FullName,
        $resolvedExtractionDirectory)

    $manifestPath = Join-Path $resolvedExtractionDirectory 'AppxManifest.xml'
    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $identity = $manifest.SelectSingleNode(
        "/*[local-name()='Package']/*[local-name()='Identity']")
    if ($null -eq $identity) {
        throw "The package manifest does not contain an Identity element."
    }

    $application = $manifest.SelectSingleNode(
        "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
    if ($null -eq $application) {
        throw "The package manifest does not contain an Application element."
    }

    $manifestExecutable = $application.GetAttribute('Executable')
    if ([System.String]::IsNullOrWhiteSpace($manifestExecutable)) {
        throw "The package Application does not declare an executable."
    }

    $entryRelativePath = $manifestExecutable.Replace(
        '\',
        [System.IO.Path]::DirectorySeparatorChar)
    $entryPath = [System.IO.Path]::GetFullPath(
        (Join-Path $resolvedExtractionDirectory $entryRelativePath))
    $extractionRootWithSeparator = $resolvedExtractionDirectory.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $entryPath.StartsWith(
        $extractionRootWithSeparator,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The package entry executable escapes the extracted package directory."
    }

    if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
        throw "The package entry executable '$manifestExecutable' does not exist."
    }

    $executable = Get-Item -LiteralPath $entryPath
    if (-not $executable.Name.Equals(
        $ExecutableName,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The package entry executable '$($executable.Name)' is not '$ExecutableName'."
    }

    $managedAssemblies = @(
        Get-ChildItem -LiteralPath $resolvedExtractionDirectory `
            -Filter 'Zashboard.App.dll' `
            -File `
            -Recurse
    )
    if ($managedAssemblies.Count -ne 0) {
        throw "The MSIX contains a managed Zashboard.App.dll and is not a Native AOT package."
    }

    Assert-NoManagedHostArtifacts `
        -Directory $resolvedExtractionDirectory `
        -ApplicationName 'Zashboard.App'

    $signaturePath = Join-Path $resolvedExtractionDirectory 'AppxSignature.p7x'
    if (Test-Path -LiteralPath $signaturePath -PathType Leaf) {
        throw "The CI package is signed, but CI artifacts are required to remain unsigned."
    }

    $expectedMachine = if ($Platform -eq 'x64') { 'Amd64' } else { 'Arm64' }
    $inspection = Assert-NativeAotPe `
        -Path $executable.FullName `
        -ExpectedMachine $expectedMachine
    $actualMachine = $inspection.Machine

    $expectedManifestArchitecture = $Platform.ToLowerInvariant()
    $actualManifestArchitecture = $identity.GetAttribute('ProcessorArchitecture')
    if (-not $actualManifestArchitecture.Equals(
        $expectedManifestArchitecture,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The package manifest architecture '$actualManifestArchitecture' does not match '$Platform'."
    }

    $hash = Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
    [PSCustomObject]@{
        Package = $package.FullName
        PackageSizeBytes = $package.Length
        Sha256 = $hash.Hash
        Executable = $executable.FullName
        Machine = $actualMachine
        HasClrHeader = $inspection.HasClrHeader
        HasNativeAotDebugHeader = $inspection.HasNativeAotDebugHeader
        HasManagedEntryAssembly = $false
        IsSigned = $false
    } | Format-List
}
finally {
    if (Test-Path -LiteralPath $resolvedExtractionDirectory) {
        Remove-Item -LiteralPath $resolvedExtractionDirectory -Recurse -Force
    }
}
