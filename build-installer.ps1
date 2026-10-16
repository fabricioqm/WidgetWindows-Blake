<#
.SYNOPSIS
    Genera Widget Manager para distribuir: instalador MSI + versión portable (ZIP).

.DESCRIPTION
    Atajo sobre el proyecto del instalador (Installer\WidgetManager.Installer.wixproj), que por sí solo publica
    la app autocontenida (incluye .NET 10) y crea el MSI. Este script añade la versión portable en ZIP y permite
    generar varias arquitecturas de una vez.

    Resultados en .\artifacts:
        WidgetManager-<versión>-<arq>.msi    instalador
        WidgetManager-<versión>-<arq>.zip    portable (descomprimir y abrir WidgetWindowDemo.exe)

    La versión sale de <Version> en WidgetWindowDemo\WidgetWindowDemo.csproj.

.EXAMPLE
    .\build-installer.ps1
.EXAMPLE
    .\build-installer.ps1 -Platforms x64,arm64
#>
param(
    [ValidateSet("x64", "arm64")]
    [string[]] $Platforms = @("x64")
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$installer = Join-Path $root "Installer\WidgetManager.Installer.wixproj"

[xml] $csproj = Get-Content (Join-Path $root "WidgetWindowDemo\WidgetWindowDemo.csproj")
$version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]

foreach ($platform in $Platforms) {
    Write-Host "`n=== Widget Manager $version ($platform) ===" -ForegroundColor Cyan

    Write-Host "Publicando la app y generando el MSI..."
    dotnet build $installer -c Release -p:InstallerPlatform=$platform -p:Platform=$platform
    if ($LASTEXITCODE -ne 0) { throw "Falló la generación del instalador ($platform)." }

    Write-Host "Creando la versión portable (ZIP)..."
    $zip = Join-Path $artifacts "WidgetManager-$version-$platform.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $artifacts "publish\win-$platform\*") -DestinationPath $zip -CompressionLevel Optimal
}

Write-Host "`nListo. Archivos en $artifacts :" -ForegroundColor Green
Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in ".msi", ".zip" } |
    Format-Table Name, @{ n = "Tamaño (MB)"; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
