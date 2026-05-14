param(
    [ValidateSet('Build', 'Run', 'Package', 'Deploy')]
    [string]$Action = 'Build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$Platform = 'x64',

    [switch]$Clean,
    [switch]$SelfContained,
    [switch]$InstallCertificate,
    [switch]$NoLaunch,
    [string]$CertPath,
    [string]$CertPassword = 'password'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot 'NAIGallery\NAIGallery.csproj'
$artifactsDir = Join-Path $repoRoot 'artifacts'

function Require-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command '$name' was not found on PATH."
    }
}

function Get-RuntimeIdentifier([string]$platform) {
    switch ($platform) {
        'x64' { 'win-x64' }
        'x86' { 'win-x86' }
        'ARM64' { 'win-arm64' }
    }
}

function Get-TargetFramework {
    [xml]$project = Get-Content -LiteralPath $projectPath
    $targetFramework = $project.Project.PropertyGroup |
        ForEach-Object { $_.TargetFramework } |
        Where-Object { $_ } |
        Select-Object -First 1

    if (-not $targetFramework) {
        throw 'TargetFramework was not found in NAIGallery.csproj.'
    }

    $targetFramework
}

function Get-PackageVersion([string]$manifestPath) {
    [xml]$manifest = Get-Content -LiteralPath $manifestPath
    $manifest.Package.Identity.Version
}

function Build-App {
    if ($Clean) {
        dotnet clean $projectPath -c $Configuration -p:Platform=$Platform
    }

    dotnet build $projectPath -c $Configuration -p:Platform=$Platform
}

function Get-BuildOutput {
    $targetFramework = Get-TargetFramework
    $runtimeIdentifier = Get-RuntimeIdentifier $Platform
    $outputDir = Join-Path $repoRoot "NAIGallery\bin\$Platform\$Configuration\$targetFramework\$runtimeIdentifier"
    $manifestPath = Join-Path $outputDir 'AppxManifest.xml'
    $exePath = Join-Path $outputDir 'NAIGallery.exe'

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Generated AppxManifest.xml was not found at $manifestPath. Build the app first."
    }

    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "NAIGallery.exe was not found at $exePath. Build the app first."
    }

    [pscustomobject]@{
        OutputDir = $outputDir
        Manifest = $manifestPath
        ExeName = 'NAIGallery.exe'
    }
}

function Package-App {
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

    $build = Get-BuildOutput
    $version = Get-PackageVersion $build.Manifest
    $packagePath = Join-Path $artifactsDir "NAIGallery_${version}_$Platform.msix"

    $args = @(
        'package', $build.OutputDir,
        '--manifest', $build.Manifest,
        '--exe', $build.ExeName,
        '--output', $packagePath,
        '--cert-password', $CertPassword
    )

    if ($SelfContained) {
        $args += '--self-contained'
    }

    if ($CertPath) {
        $args += @('--cert', $CertPath)
    } else {
        $args += @('--generate-cert', '--publisher', 'CN=resc8')
    }

    winapp @args

    [pscustomobject]@{
        Package = $packagePath
        Certificate = if ($CertPath) { $CertPath } else { Join-Path $artifactsDir '5d678af6-524e-4407-925c-6291eb48e9bd_cert.pfx' }
    }
}

function Stop-RepoAppInstances {
    Get-Process -Name 'NAIGallery' -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -and $_.Path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)
            } catch {
                $false
            }
        } |
        ForEach-Object {
            Write-Host "Stopping existing NAIGallery process $($_.Id) that is running from this repo."
            Stop-Process -Id $_.Id -Force
        }
}

function Run-App {
    Stop-RepoAppInstances

    $build = Get-BuildOutput
    $looseLayoutDir = Join-Path $artifactsDir "loose-layout\$Configuration-$Platform"
    New-Item -ItemType Directory -Force -Path $looseLayoutDir | Out-Null

    $args = @(
        'run', $build.OutputDir,
        '--manifest', $build.Manifest,
        '--exe', $build.ExeName,
        '--output-appx-directory', $looseLayoutDir
    )

    if ($NoLaunch) {
        $args += '--no-launch'
    } else {
        $args += '--detach'
    }

    winapp @args
}

Require-Command dotnet
Require-Command winapp

switch ($Action) {
    'Build' {
        Build-App
    }
    'Run' {
        Build-App
        Run-App
    }
    'Package' {
        if ($Configuration -ne 'Release') {
            Write-Warning 'MSIX packages are usually produced from Release builds.'
        }
        Build-App
        Package-App | Format-List
    }
    'Deploy' {
        if ($Configuration -ne 'Release') {
            Write-Warning 'Deploying a Debug package is useful for testing only.'
        }
        Stop-RepoAppInstances
        Build-App
        $package = Package-App

        if ($InstallCertificate) {
            winapp cert install $package.Certificate
        } else {
            Write-Warning "Skipping certificate trust. If Add-AppxPackage fails, rerun with -InstallCertificate from an elevated terminal or trust $($package.Certificate) manually."
        }

        Add-AppxPackage -Path $package.Package -ForceUpdateFromAnyVersion

        if (-not $NoLaunch) {
            Start-Process "shell:AppsFolder\5d678af6-524e-4407-925c-6291eb48e9bd_x4m1ht0aqcddc!App"
        }
    }
}
