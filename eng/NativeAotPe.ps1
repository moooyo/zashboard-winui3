function Get-PeExportNames {
    param(
        [Parameter(Mandatory = $true)]
        [System.Reflection.PortableExecutable.PEReader]$Reader
    )

    $peHeader = $Reader.PEHeaders.PEHeader
    if ($null -eq $peHeader) {
        throw 'The image does not contain a PE optional header.'
    }

    $directory = $peHeader.ExportTableDirectory
    if ($directory.RelativeVirtualAddress -eq 0 -or $directory.Size -lt 40) {
        return @()
    }

    $directoryBlock = $Reader.GetSectionData($directory.RelativeVirtualAddress)
    if ($directoryBlock.Length -lt 40) {
        throw 'The PE export directory is truncated.'
    }

    [byte[]]$directoryBytes = $directoryBlock.GetContent(0, 40)
    [uint32]$nameCount = [System.BitConverter]::ToUInt32($directoryBytes, 24)
    [uint32]$nameTableRva = [System.BitConverter]::ToUInt32($directoryBytes, 32)
    if ($nameCount -gt 100000) {
        throw "The PE export name count '$nameCount' is not credible."
    }

    if ($nameCount -eq 0) {
        return @()
    }

    $nameTableSize = [int]$nameCount * 4
    $nameTableBlock = $Reader.GetSectionData([int]$nameTableRva)
    if ($nameTableBlock.Length -lt $nameTableSize) {
        throw 'The PE export name table is truncated.'
    }

    [byte[]]$nameTableBytes = $nameTableBlock.GetContent(0, $nameTableSize)
    $names = [System.Collections.Generic.List[string]]::new([int]$nameCount)
    for ($index = 0; $index -lt $nameCount; $index++) {
        [uint32]$nameRva = [System.BitConverter]::ToUInt32($nameTableBytes, $index * 4)
        $nameBlock = $Reader.GetSectionData([int]$nameRva)
        $maximumLength = [System.Math]::Min($nameBlock.Length, 1024)
        [byte[]]$nameBytes = $nameBlock.GetContent(0, $maximumLength)
        $terminator = [System.Array]::IndexOf($nameBytes, [byte]0)
        if ($terminator -lt 0) {
            throw 'A PE export name is not null-terminated within 1024 bytes.'
        }

        $names.Add([System.Text.Encoding]::ASCII.GetString($nameBytes, 0, $terminator))
    }

    return $names.ToArray()
}

function Assert-NativeAotPe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$ExpectedMachine
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            if ($reader.PEHeaders.CorHeader -ne $null) {
                throw "The executable '$Path' contains a CLR header."
            }

            $machine = $reader.PEHeaders.CoffHeader.Machine.ToString()
            if (-not [string]::IsNullOrWhiteSpace($ExpectedMachine) -and
                $machine -ne $ExpectedMachine) {
                throw "The executable machine '$machine' does not match '$ExpectedMachine'."
            }

            $exports = @(Get-PeExportNames -Reader $reader)
            if ($exports -notcontains 'DotNetRuntimeDebugHeader') {
                throw "The executable '$Path' does not export DotNetRuntimeDebugHeader and is not proven to be a Native AOT image."
            }

            return [PSCustomObject]@{
                Machine = $machine
                HasClrHeader = $false
                HasNativeAotDebugHeader = $true
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-NoManagedHostArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$ApplicationName
    )

    $forbiddenNames = @(
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        "$ApplicationName.deps.json",
        "$ApplicationName.runtimeconfig.json"
    )
    foreach ($name in $forbiddenNames) {
        $match = Get-ChildItem -LiteralPath $Directory -Filter $name -File -Recurse |
            Select-Object -First 1
        if ($null -ne $match) {
            throw "A managed-host artifact remains in the Native AOT output: '$($match.FullName)'."
        }
    }
}
