# DevBench Bootstrap Script (PowerShell)
# Downloads the pre-built harness or builds locally if no release exists

param(
    [switch]$Force,
    [switch]$LocalBuild,
    [Parameter(ValueFromRemainingArguments)]
    [string[]]$HarnessArgs
)

$ErrorActionPreference = "Stop"

$repoOwner = "damianedwards"
$repoName = "DevBench"
$harnessName = "DevBench"
$cacheDir = Join-Path $PSScriptRoot ".devbench"
$srcFile = Join-Path $PSScriptRoot "src" "DevBench.cs"

# Determine platform
$os = if ($IsWindows -or $env:OS -eq "Windows_NT") { "win" }
      elseif ($IsMacOS) { "osx" }
      else { "linux" }

$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "arm64" }
        else { "x64" }

$rid = "$os-$arch"
$exeExt = if ($os -eq "win") { ".exe" } else { "" }
$harnessPath = Join-Path $cacheDir "$harnessName-$rid$exeExt"

function Get-LatestRelease {
    try {
        $releaseUrl = "https://api.github.com/repos/$repoOwner/$repoName/releases/latest"
        $release = Invoke-RestMethod -Uri $releaseUrl -Headers @{ "User-Agent" = "DevBench" } -ErrorAction Stop
        return $release
    }
    catch {
        return $null
    }
}

function Download-Harness {
    param($Release)
    
    $assetName = "$harnessName-$rid$exeExt"
    $asset = $Release.assets | Where-Object { $_.name -eq $assetName }
    
    if (-not $asset) {
        Write-Host "No pre-built binary for $rid" -ForegroundColor Yellow
        return $false
    }
    
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    
    Write-Host "Downloading $assetName..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $harnessPath
    
    if ($os -ne "win") {
        chmod +x $harnessPath
    }
    
    return $true
}

function Build-LocalHarness {
    Write-Host "Building harness locally..." -ForegroundColor Cyan
    
    # Check for .NET SDK
    $dotnetVersion = & dotnet --version 2>$null
    if (-not $dotnetVersion) {
        Write-Host "Error: .NET SDK not found. Install from https://dot.net" -ForegroundColor Red
        exit 1
    }
    
    $majorVersion = [int]($dotnetVersion -split '\.')[0]
    if ($majorVersion -lt 10) {
        Write-Host "Error: .NET 10 or later required (found $dotnetVersion)" -ForegroundColor Red
        exit 1
    }
    
    # Build native AOT
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    
    Push-Location $PSScriptRoot
    try {
        & dotnet publish $srcFile --output $cacheDir --runtime $rid
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build failed" -ForegroundColor Red
            exit 1
        }
    }
    finally {
        Pop-Location
    }
    
    # Find the built executable
    $builtExe = Join-Path $cacheDir "$harnessName$exeExt"
    if (Test-Path $builtExe) {
        Move-Item $builtExe $harnessPath -Force
    }
}

# Main logic
$needsBuild = $Force -or $LocalBuild -or (-not (Test-Path $harnessPath))

if ($needsBuild) {
    if (-not $LocalBuild) {
        $release = Get-LatestRelease
        if ($release) {
            $downloaded = Download-Harness -Release $release
            if ($downloaded) {
                $needsBuild = $false
            }
        }
    }
    
    if ($needsBuild -or $LocalBuild) {
        Build-LocalHarness
    }
}

# Run the harness
if (-not (Test-Path $harnessPath)) {
    Write-Host "Error: Harness not found at $harnessPath" -ForegroundColor Red
    exit 1
}

Write-Host "Running DevBench..." -ForegroundColor Green
& $harnessPath @HarnessArgs
exit $LASTEXITCODE
