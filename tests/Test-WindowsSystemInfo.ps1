param(
    [Parameter(Mandatory)]
    [string]$HarnessPath,

    [Parameter(Mandatory)]
    [bool]$ExpectedDevDrive
)

$ErrorActionPreference = 'Stop'

if (-not [OperatingSystem]::IsWindows()) {
    throw 'These system information tests require Windows.'
}

$harness = (Resolve-Path -LiteralPath $HarnessPath).Path
$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = if ([IO.Path]::GetExtension($harness) -eq '.dll') { 'dotnet' } else { $harness }
if ([IO.Path]::GetExtension($harness) -eq '.dll') {
    $startInfo.ArgumentList.Add($harness)
}
$startInfo.ArgumentList.Add('--system-info-only')
$startInfo.WorkingDirectory = (Get-Location).Path
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [System.Diagnostics.Process]::Start($startInfo)
try {
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'System information collection timed out.'
    }
    $output = $outputTask.GetAwaiter().GetResult()
    $errors = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or -not [string]::IsNullOrWhiteSpace($errors)) {
        throw "System information collection failed: $errors"
    }
} finally {
    $process.Dispose()
}

$jsonStart = $output.IndexOf('{')
if ($jsonStart -lt 0) {
    throw 'The harness did not return system information JSON.'
}
$info = $output.Substring($jsonStart) | ConvertFrom-Json
$modules = @(Get-CimInstance Win32_PhysicalMemory)
$installedGiB = ($modules | Measure-Object Capacity -Sum).Sum / 1GB
$cpuModel = ((Get-CimInstance Win32_Processor).Name -join ', ').Trim()

if ($info.cpu.model -cne $cpuModel) {
    throw "CPU model differs from Windows: '$($info.cpu.model)' vs '$cpuModel'."
}
if ($info.memory.capacityGB -ne $installedGiB -or $installedGiB -le 0) {
    throw "Installed RAM differs from Windows: $($info.memory.capacityGB) vs $installedGiB GiB."
}
if ($info.memory.dimmCount -ne $modules.Count) {
    throw "DIMM count differs from Windows: $($info.memory.dimmCount) vs $($modules.Count)."
}
if ($info.storage.isDevDrive -isnot [bool] -or $info.storage.isDevDrive -ne $ExpectedDevDrive) {
    throw "Dev Drive status differs from expected: $($info.storage.isDevDrive) vs $ExpectedDevDrive."
}
if ($info.cpu.model.Contains([char]27)) {
    throw 'CPU model contains terminal escape sequences.'
}

Write-Output "System information passed: $installedGiB GiB RAM, $($modules.Count) DIMMs, Dev Drive=$ExpectedDevDrive."
