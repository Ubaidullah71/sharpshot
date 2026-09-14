<#
.SYNOPSIS
    Builds release zips of Sharpshot into ./dist.

.DESCRIPTION
    For each runtime it produces two builds:
      * Sharpshot-<version>-<rid>.zip             < 1 MB, needs the .NET 10 Desktop Runtime
      * Sharpshot-<version>-<rid>-standalone.zip  ~40 MB, runs on any Windows 10/11 PC
    plus SHA256SUMS.txt with a checksum for each zip.

.EXAMPLE
    ./tools/publish.ps1 -Version 1.0.0
#>
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = "1.0.0",
    [string[]] $Runtimes = @("win-x64", "win-arm64")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Sharpshot/Sharpshot.csproj"
$dist = Join-Path $root "dist"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

foreach ($rid in $Runtimes) {
    foreach ($standalone in @($false, $true)) {
        $name = if ($standalone) { "Sharpshot-$Version-$rid-standalone" } else { "Sharpshot-$Version-$rid" }
        $out = Join-Path $dist $name
        $publishArgs = @(
            "publish", $project,
            "-c", "Release",
            "-r", $rid,
            "--self-contained", $standalone.ToString().ToLowerInvariant(),
            "-p:PublishSingleFile=true",
            "-p:DebugType=none",
            "-p:Version=$Version",
            "-o", $out
        )
        if ($standalone) {
            $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
            $publishArgs += "-p:EnableCompressionInSingleFile=true"
        }

        Write-Host "Publishing $name" -ForegroundColor Cyan
        dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name" }

        Copy-Item (Join-Path $root "LICENSE") $out
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath "$out.zip" -Force
        Remove-Item $out -Recurse -Force
    }
}

$zips = Get-ChildItem $dist -Filter *.zip | Sort-Object Name
$zips | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
    Set-Content -Path (Join-Path $dist "SHA256SUMS.txt") -Encoding ascii

$zips | ForEach-Object { "{0,-50} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }