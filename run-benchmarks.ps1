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
    
    # Build and get the assembly path (avoids issues with dotnet run and build servers)
    Push-Location $PSScriptRoot
    try {
        # Build and get the target path
        $buildOutput = & dotnet build $srcFile -getProperty:TargetPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build failed:" -ForegroundColor Red
            Write-Host $buildOutput -ForegroundColor Red
            exit 1
        }
        
        # The last line should be the target path
        $targetPath = ($buildOutput | Select-Object -Last 1).Trim()
        
        if (-not (Test-Path $targetPath)) {
            Write-Host "Error: Built assembly not found at $targetPath" -ForegroundColor Red
            exit 1
        }
        
        # Store the path for running with dotnet exec
        return $targetPath
    }
    finally {
        Pop-Location
    }
}

function Build-NativeHarness {
    Write-Host "Building native AOT harness..." -ForegroundColor Cyan
    
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
$useNativeHarness = $false
$assemblyPath = $null

if (-not $LocalBuild) {
    # Try to use pre-built native binary
    if ($Force -or -not (Test-Path $harnessPath)) {
        $release = Get-LatestRelease
        if ($release) {
            $downloaded = Download-Harness -Release $release
            if ($downloaded) {
                $useNativeHarness = $true
            }
        }
    }
    else {
        $useNativeHarness = $true
    }
}

if (-not $useNativeHarness) {
    # Local dev mode: build to assembly and run with dotnet exec
    # This avoids conflicts with dotnet build-server shutdown in benchmarks
    $assemblyPath = Build-LocalHarness
}

# Run the harness
if ($useNativeHarness) {
    if (-not (Test-Path $harnessPath)) {
        Write-Host "Error: Harness not found at $harnessPath" -ForegroundColor Red
        exit 1
    }
    Write-Host "Running DevBench..." -ForegroundColor Green
    & $harnessPath @HarnessArgs
}
else {
    Write-Host "Running DevBench..." -ForegroundColor Green
    & dotnet exec $assemblyPath @HarnessArgs
}

exit $LASTEXITCODE
