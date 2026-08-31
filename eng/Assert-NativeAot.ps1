param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [string]$ExecutableName = 'Zashboard.App.exe',

    [string]$ApplicationName,

    [ValidateSet('Amd64', 'Arm64')]
    [string]$ExpectedMachine
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'NativeAotPe.ps1')

$resolvedDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$resolvedApplicationName = if ([string]::IsNullOrWhiteSpace($ApplicationName)) {
    [System.IO.Path]::GetFileNameWithoutExtension($ExecutableName)
}
else {
    $ApplicationName
}
$executable = Get-ChildItem -LiteralPath $resolvedDirectory -Filter $ExecutableName -File -Recurse |
    Select-Object -First 1

if ($null -eq $executable) {
    throw "The published executable '$ExecutableName' was not found under '$resolvedDirectory'."
}

$managedAssembly = Get-ChildItem -LiteralPath $resolvedDirectory -Filter "$resolvedApplicationName.dll" -File -Recurse |
    Select-Object -First 1

if ($null -ne $managedAssembly) {
    throw "A managed application assembly remains in the publish output: '$($managedAssembly.FullName)'."
}

$inspection = Assert-NativeAotPe -Path $executable.FullName -ExpectedMachine $ExpectedMachine
Assert-NoManagedHostArtifacts -Directory $resolvedDirectory -ApplicationName $resolvedApplicationName

$hash = Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256
[PSCustomObject]@{
    Executable = $executable.FullName
    SizeBytes = $executable.Length
    Sha256 = $hash.Hash
    Machine = $inspection.Machine
    HasClrHeader = $inspection.HasClrHeader
    HasNativeAotDebugHeader = $inspection.HasNativeAotDebugHeader
} | Format-List
