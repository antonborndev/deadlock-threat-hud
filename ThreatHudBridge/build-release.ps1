cp "C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\citadel\addons\pak01_dir.vpk" pak57_dir.vpk

$ErrorActionPreference = "Stop"

Set-Location $PSScriptRoot

Write-Host ""
Write-Host "========================================"
Write-Host " Deadlock Threat HUD Bridge - FINAL BUILD"
Write-Host "========================================"
Write-Host ""

$Project =
    Join-Path `
        $PSScriptRoot `
        "ThreatHudBridge.csproj"

$Icon =
    Join-Path `
        $PSScriptRoot `
        "ThreatHudBridge.ico"

$ModVpk =
    Join-Path `
        $PSScriptRoot `
        "pak57_dir.vpk"

$Bin =
    Join-Path `
        $PSScriptRoot `
        "bin"

$Obj =
    Join-Path `
        $PSScriptRoot `
        "obj"

$Dist =
    Join-Path `
        $PSScriptRoot `
        "dist"

$Exe =
    Join-Path `
        $Dist `
        "ThreatHudBridge.exe"

if (!(Test-Path $Project)) {

    throw "ThreatHudBridge.csproj not found."
}

if (!(Test-Path $Icon)) {

    throw "ThreatHudBridge.ico not found."
}

if (!(Test-Path -LiteralPath $ModVpk -PathType Leaf)) {

    throw (
        "Threat HUD VPK not found. " +
        "Expected: $ModVpk"
    )
}

$ModVpkInfo =
    Get-Item `
        -LiteralPath $ModVpk

if ($ModVpkInfo.Length -lt 4) {

    throw "pak57_dir.vpk is empty or truncated."
}

$ModVpkStream =
    [System.IO.File]::OpenRead(
        $ModVpk
    )

try {

    $ModVpkReader =
        [System.IO.BinaryReader]::new(
            $ModVpkStream
        )

    try {

        $ModVpkSignature =
            $ModVpkReader.ReadUInt32()
    }
    finally {

        $ModVpkReader.Dispose()
    }
}
finally {

    if ($null -ne $ModVpkStream) {

        $ModVpkStream.Dispose()
    }
}

if (
    $ModVpkSignature -ne
        [UInt32]0x55AA1234
) {

    throw (
        "pak57_dir.vpk does not have a valid " +
        "Valve VPK signature."
    )
}

$UnexpectedVpkChunks =
    @(
        Get-ChildItem `
            -LiteralPath $PSScriptRoot `
            -Filter "pak57_*.vpk" `
            -File |
        Where-Object {

            $_.FullName -ne
                $ModVpk
        }
    )

if ($UnexpectedVpkChunks.Count -gt 0) {

    $UnexpectedVpkChunkNames =
        $UnexpectedVpkChunks.Name -join ", "

    throw (
        "Split VPK chunks are not supported by this build. " +
        "Create one self-contained pak57_dir.vpk. Found: " +
        $UnexpectedVpkChunkNames
    )
}

Write-Host (
    "Embedded mod payload: " +
    $ModVpkInfo.Name +
    " (" +
    [Math]::Round(
        $ModVpkInfo.Length / 1MB,
        2
    ) +
    " MB)"
)

Write-Host ""

$RunningBridgeProcesses =
    @(
        Get-Process `
            -Name "ThreatHudBridge" `
            -ErrorAction SilentlyContinue
    )

if ($RunningBridgeProcesses.Count -gt 0) {

    throw (
        "ThreatHudBridge.exe is currently running. " +
        "Close it before starting a release build."
    )
}

Write-Host "[1/4] Removing old build files..."

foreach (
    $Path in @(
        $Bin,
        $Obj,
        $Dist
    )
) {

    if (Test-Path $Path) {

        Remove-Item `
            $Path `
            -Recurse `
            -Force
    }
}

Write-Host "[2/4] Restoring Release win-x64 packages..."

dotnet restore `
    $Project `
    -r win-x64 `
    -p:Configuration=Release

if ($LASTEXITCODE -ne 0) {

    throw "dotnet restore failed."
}

Write-Host "[3/4] Publishing final single-file EXE..."

dotnet publish `
    $Project `
    -c Release `
    -r win-x64 `
    --no-restore `
    -o $Dist

if ($LASTEXITCODE -ne 0) {

    throw "dotnet publish failed."
}

Write-Host "[4/4] Verifying final release..."

if (!(Test-Path $Exe)) {

    Write-Host ""
    Write-Host "Contents of dist:"
    Write-Host ""

    if (Test-Path $Dist) {

        Get-ChildItem $Dist |
            ForEach-Object {

                Write-Host $_.FullName
            }
    }

    throw "Final ThreatHudBridge.exe was not created."
}

$UnexpectedItems =
    Get-ChildItem `
        $Dist |
    Where-Object {

        $_.FullName -ne
            $Exe
    }

if ($UnexpectedItems) {

    Write-Host ""
    Write-Host "Unexpected items were found in dist:"
    Write-Host ""

    $UnexpectedItems |
        ForEach-Object {

            Write-Host (
                "  " +
                $_.Name
            )
        }

    Write-Host ""

    throw (
        "Release is not a true single-file build."
    )
}

#
# Verify that Windows can extract an icon
# directly from the final executable.
#
Add-Type `
    -AssemblyName System.Drawing

$ExtractedIcon =
    [System.Drawing.Icon]::ExtractAssociatedIcon(
        $Exe
    )

if ($null -eq $ExtractedIcon) {

    throw (
        "Windows icon resource was not found " +
        "in ThreatHudBridge.exe."
    )
}

try {

    $IconWidth =
        $ExtractedIcon.Width

    $IconHeight =
        $ExtractedIcon.Height
}
finally {

    $ExtractedIcon.Dispose()
}

$ExeInfo =
    Get-Item $Exe

$SizeMb =
    [Math]::Round(
        $ExeInfo.Length / 1MB,
        2
    )

Write-Host ""
Write-Host "========================================"
Write-Host " BUILD SUCCESSFUL"
Write-Host "========================================"
Write-Host ""

Write-Host "EXE:"
Write-Host $Exe

Write-Host ""
Write-Host "Size: $SizeMb MB"

Write-Host ""
Write-Host "Embedded Windows icon:"
Write-Host (
    "$IconWidth x $IconHeight"
)

Write-Host ""
Write-Host "Product:"
Write-Host $ExeInfo.VersionInfo.ProductName

Write-Host ""
Write-Host "File version:"
Write-Host $ExeInfo.VersionInfo.FileVersion

Write-Host ""
Write-Host "Product version:"
Write-Host $ExeInfo.VersionInfo.ProductVersion

Write-Host ""
Write-Host "Final folder:"
Write-Host $Dist

Write-Host ""
Write-Host "Files in final release:"

Get-ChildItem `
    $Dist |
    ForEach-Object {

        Write-Host (
            "  " +
            $_.Name
        )
    }

Write-Host ""


