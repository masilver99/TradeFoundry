[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$InnoSetupPath,
    [switch]$CompileInstaller
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\windows'
}
else {
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

$publishDirectory = Join-Path $OutputRoot 'publish'
$installerDirectory = Join-Path $OutputRoot 'installer'
$projectPath = Join-Path $repoRoot 'TradeFoundry.csproj'
$desktopProjectPath = Join-Path $repoRoot 'DesktopShell\TradeFoundry.Desktop.csproj'
$publishProfile = Join-Path $repoRoot 'Properties\PublishProfiles\WindowsDesktop.pubxml'
$desktopPublishProfile = Join-Path $repoRoot 'DesktopShell\Properties\PublishProfiles\WindowsDesktop.pubxml'
$installerScript = Join-Path $repoRoot 'installer\TradeFoundry.iss'

if (-not (Test-Path -LiteralPath $publishProfile)) {
    throw "Windows publish profile not found: $publishProfile"
}

if (-not (Test-Path -LiteralPath $desktopPublishProfile)) {
    throw "Windows desktop shell publish profile not found: $desktopPublishProfile"
}

New-Item -ItemType Directory -Force -Path $publishDirectory, $installerDirectory | Out-Null

$publishDirectoryWithSlash = $publishDirectory.TrimEnd('\') + '\'
$publishArguments = @(
    'publish',
    $projectPath,
    '--nologo',
    '-c',
    $Configuration,
    '-p:PublishProfile=WindowsDesktop',
    "-p:PublishDir=$publishDirectoryWithSlash"
)

Write-Host "Publishing TradeFoundry to $publishDirectory"
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$desktopPublishArguments = @(
    'publish',
    $desktopProjectPath,
    '--nologo',
    '-c',
    $Configuration,
    '-p:PublishProfile=WindowsDesktop',
    "-p:PublishDir=$publishDirectoryWithSlash"
)

Write-Host "Publishing native Windows shell to $publishDirectory"
& dotnet @desktopPublishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Windows desktop shell publish failed with exit code $LASTEXITCODE."
}

if ($CompileInstaller) {
    if (-not [string]::IsNullOrWhiteSpace($InnoSetupPath)) {
        $isccPath = [System.IO.Path]::GetFullPath($InnoSetupPath)
        if (-not (Test-Path -LiteralPath $isccPath -PathType Leaf)) {
            throw "Inno Setup compiler was not found at: $isccPath"
        }
    }
    else {
        $candidateIsccPaths = [System.Collections.Generic.List[string]]::new()

        foreach ($programFilesRoot in @(
            [Environment]::GetEnvironmentVariable('ProgramFiles'),
            [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        )) {
            if (-not [string]::IsNullOrWhiteSpace($programFilesRoot)) {
                $candidateIsccPaths.Add((Join-Path $programFilesRoot 'Inno Setup 7\ISCC.exe'))
            }
        }

        $pathIscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
        if ($null -ne $pathIscc) {
            $candidateIsccPaths.Add($pathIscc.Source)
        }

        $isccPath = $candidateIsccPaths |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1

        if ([string]::IsNullOrWhiteSpace($isccPath)) {
            throw 'Inno Setup 7 was not found. Install it, add ISCC.exe to PATH, pass -InnoSetupPath, or run without -CompileInstaller.'
        }
    }

    Write-Host "Compiling installer with $isccPath"
    $installerArguments = @(
        "/DPublishDir=$publishDirectory",
        "/DInstallerOutputDir=$installerDirectory",
        $installerScript
    )
    & $isccPath @installerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Windows publish complete: $publishDirectory"
if ($CompileInstaller) {
    Write-Host "Installer output: $installerDirectory"
}
