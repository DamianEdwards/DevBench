#!/usr/bin/env dotnet
#:package Spectre.Console@0.50.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

// ============================================================================
// DevBench - Developer PC Benchmarking Tool
// ============================================================================

var verbose = args.Contains("--verbose") || args.Contains("-v");
var systemInfoOnly = args.Contains("--system-info-only");
var restoreOnly = args.Contains("--restore-only");
var specificBenchmark = GetArgValue(args, "--benchmark") ?? GetArgValue(args, "-b");

// Collect system info first
if (!restoreOnly)
{
    AnsiConsole.MarkupLine("[blue]Collecting system information...[/]");
}
var systemInfo = CollectSystemInfo();

if (systemInfoOnly)
{
    Console.WriteLine(JsonSerializer.Serialize(systemInfo, DevBenchJsonContext.Default.SystemInfo));
    return 0;
}

// Find all benchmarks
var benchmarksDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "benchmarks");
if (!Directory.Exists(benchmarksDir))
{
    benchmarksDir = Path.Combine(Environment.CurrentDirectory, "benchmarks");
}

if (!Directory.Exists(benchmarksDir))
{
    AnsiConsole.MarkupLine("[red]Error: benchmarks directory not found[/]");
    return 1;
}

var benchmarks = LoadBenchmarks(benchmarksDir);
if (benchmarks.Count == 0)
{
    AnsiConsole.MarkupLine("[red]Error: No benchmarks found[/]");
    return 1;
}

// Select benchmarks to run
List<BenchmarkManifest> selectedBenchmarks;

if (!string.IsNullOrEmpty(specificBenchmark))
{
    var found = benchmarks.FirstOrDefault(b => 
        b.Name.Equals(specificBenchmark, StringComparison.OrdinalIgnoreCase) ||
        b.FolderName.Equals(specificBenchmark, StringComparison.OrdinalIgnoreCase));
    
    if (found == null)
    {
        AnsiConsole.MarkupLine($"[red]Error: Benchmark '{specificBenchmark}' not found[/]");
        return 1;
    }
    selectedBenchmarks = [found];
}
else
{
    selectedBenchmarks = AnsiConsole.Prompt(
        new MultiSelectionPrompt<BenchmarkManifest>()
            .Title("Select [green]benchmarks[/] to run:")
            .PageSize(15)
            .MoreChoicesText("[grey](Move up and down to reveal more benchmarks)[/]")
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
            .AddChoices(benchmarks)
            .UseConverter(b => $"{b.Name} [grey]- {b.Description}[/]"));
}

if (selectedBenchmarks.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No benchmarks selected[/]");
    return 0;
}

// Run benchmarks
var results = new List<BenchmarkResult>();
var cacheDir = Path.Combine(Environment.CurrentDirectory, ".cache");
Directory.CreateDirectory(cacheDir);

foreach (var benchmark in selectedBenchmarks)
{
    AnsiConsole.MarkupLine($"\n[bold blue]Running benchmark: {benchmark.Name}[/]");
    
    // Check prerequisites
    if (!CheckPrerequisites(benchmark, verbose))
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ Skipping {benchmark.Name}: prerequisites not met[/]");
        continue;
    }
    
    // Determine working directory
    string workDir;
    if (benchmark.Type == "external-repo")
    {
        var clonedDir = await CloneExternalRepo(benchmark, cacheDir, verbose);
        if (clonedDir == null)
        {
            AnsiConsole.MarkupLine($"[red]Failed to clone {benchmark.RepoUrl}[/]");
            continue;
        }
        workDir = clonedDir;
    }
    else
    {
        workDir = Path.Combine(benchmarksDir, benchmark.FolderName);
    }
    
    if (!string.IsNullOrEmpty(benchmark.WorkingDirectory))
    {
        workDir = Path.Combine(workDir, benchmark.WorkingDirectory);
    }
    
    if (restoreOnly)
    {
        await RunRestoreOnly(benchmark, workDir, verbose);
    }
    else
    {
        var result = await RunBenchmark(benchmark, workDir, verbose);
        results.Add(result);
    }
}

if (restoreOnly)
{
    AnsiConsole.MarkupLine("[green]Restore completed.[/]");
    return 0;
}

// Generate results
var submission = new BenchmarkSubmission
{
    SubmissionId = Guid.NewGuid().ToString(),
    Submitter = GetGitUserName(),
    Timestamp = DateTime.UtcNow.ToString("o"),
    Machine = systemInfo,
    Benchmarks = results
};

// Save results
var resultsDir = Path.Combine(Environment.CurrentDirectory, "results");
Directory.CreateDirectory(resultsDir);

var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss");
var hash = submission.SubmissionId[..8];
var resultsFile = Path.Combine(resultsDir, $"results-{timestamp}-{hash}.json");

await File.WriteAllTextAsync(resultsFile, JsonSerializer.Serialize(submission, DevBenchJsonContext.Default.BenchmarkSubmission));

AnsiConsole.MarkupLine($"\n[green]Results saved to:[/] {resultsFile}");
AnsiConsole.MarkupLine("\n[blue]To submit your results:[/]");
AnsiConsole.MarkupLine("  1. Fork the DevBench repository");
AnsiConsole.MarkupLine("  2. Add your results file to the results/ folder");
AnsiConsole.MarkupLine("  3. Submit a pull request");

// Display summary
DisplaySummary(results);

return 0;

// ============================================================================
// Helper Methods
// ============================================================================

static string? GetArgValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index < args.Length - 1 ? args[index + 1] : null;
}

static List<BenchmarkManifest> LoadBenchmarks(string benchmarksDir)
{
    var benchmarks = new List<BenchmarkManifest>();
    
    foreach (var dir in Directory.GetDirectories(benchmarksDir))
    {
        var manifestPath = Path.Combine(dir, "benchmark.json");
        if (!File.Exists(manifestPath)) continue;
        
        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, DevBenchJsonContext.Default.BenchmarkManifest);
            if (manifest != null)
            {
                manifest.FolderName = Path.GetFileName(dir);
                benchmarks.Add(manifest);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Failed to load {manifestPath}: {ex.Message}[/]");
        }
    }
    
    return benchmarks;
}

static bool CheckPrerequisites(BenchmarkManifest benchmark, bool verbose)
{
    if (benchmark.Prerequisites == null || benchmark.Prerequisites.Count == 0)
        return true;
    
    foreach (var prereq in benchmark.Prerequisites)
    {
        try
        {
            var output = RunCommandSync(prereq.Command, "", TimeSpan.FromSeconds(10), verbose);
            if (output == null) return false;
            
            if (!string.IsNullOrEmpty(prereq.MinVersion))
            {
                var version = ExtractVersion(output);
                if (version != null && Version.TryParse(prereq.MinVersion, out var minVer))
                {
                    if (version < minVer)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{prereq.Command}: {version} < {minVer}[/]");
                        return false;
                    }
                }
            }
        }
        catch
        {
            return false;
        }
    }
    
    return true;
}

static Version? ExtractVersion(string output)
{
    var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+\.\d+(\.\d+)?)");
    return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
}

static async Task<string?> CloneExternalRepo(BenchmarkManifest benchmark, string cacheDir, bool verbose)
{
    var repoName = Path.GetFileNameWithoutExtension(new Uri(benchmark.RepoUrl!).AbsolutePath);
    var targetDir = Path.Combine(cacheDir, repoName);
    
    if (Directory.Exists(targetDir))
    {
        if (verbose) AnsiConsole.MarkupLine($"[grey]Using cached repo: {targetDir}[/]");
        return targetDir;
    }
    
    AnsiConsole.MarkupLine($"[blue]Cloning {benchmark.RepoUrl}...[/]");
    
    var args = $"clone --depth 1";
    if (!string.IsNullOrEmpty(benchmark.RepoRef))
    {
        args += $" --branch {benchmark.RepoRef}";
    }
    args += $" {benchmark.RepoUrl} {targetDir}";
    
    var result = await RunCommandAsync("git", args, Environment.CurrentDirectory, 
        TimeSpan.FromMinutes(10), verbose);
    
    return result.Success ? targetDir : null;
}

static async Task RunRestoreOnly(BenchmarkManifest benchmark, string workDir, bool verbose)
{
    var envVars = benchmark.EnvironmentVariables ?? new Dictionary<string, string>();
    
    if (benchmark.Restore == null)
    {
        AnsiConsole.MarkupLine("[yellow]  No restore command defined[/]");
        return;
    }
    
    AnsiConsole.MarkupLine($"[blue]  Restoring {benchmark.Name}...[/]");
    AnsiConsole.MarkupLine($"[grey]  Working directory: {workDir}[/]");
    AnsiConsole.MarkupLine($"[grey]  Command: {benchmark.Restore.Command}[/]");
    
    var timeout = TimeSpan.FromSeconds(benchmark.Restore.Timeout ?? 300);
    var sw = Stopwatch.StartNew();
    var result = await RunCommandAsync(benchmark.Restore.Command, "", workDir, timeout, verbose, envVars);
    sw.Stop();
    
    if (result.Success)
    {
        AnsiConsole.MarkupLine($"[green]  Restore completed in {sw.ElapsedMilliseconds}ms[/]");
    }
    else
    {
        AnsiConsole.MarkupLine($"[red]  Restore failed (exit code: {result.ExitCode})[/]");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Error)}[/]");
        }
    }
}

static async Task<BenchmarkResult> RunBenchmark(BenchmarkManifest benchmark, string workDir, bool verbose)
{
    var result = new BenchmarkResult { Name = benchmark.Name };
    var envVars = benchmark.EnvironmentVariables ?? new Dictionary<string, string>();
    
    // Restore phase
    if (benchmark.Restore != null)
    {
        AnsiConsole.MarkupLine("[grey]  Restoring dependencies...[/]");
        var timeout = TimeSpan.FromSeconds(benchmark.Restore.Timeout ?? 300);
        await RunCommandAsync(benchmark.Restore.Command, "", workDir, timeout, verbose, envVars);
    }
    
    // Clear cache
    if (benchmark.ClearCache != null)
    {
        await ClearCache(benchmark.ClearCache, workDir, verbose);
    }
    
    // Validate build configuration
    if (benchmark.Build?.Full == null)
    {
        AnsiConsole.MarkupLine("[red]  Error: No build.full command defined[/]");
        return result;
    }
    
    var buildFull = benchmark.Build.Full;
    
    // Cold build
    AnsiConsole.MarkupLine("[grey]  Running cold build...[/]");
    var coldResult = await TimedBuild(buildFull, workDir, verbose, envVars);
    result.ColdRun = coldResult;
    
    // Warm builds
    var warmIterations = benchmark.WarmupIterations ?? 2;
    var measuredIterations = benchmark.MeasuredIterations ?? 5;
    
    // Warmup (not measured)
    for (int i = 0; i < warmIterations; i++)
    {
        AnsiConsole.MarkupLine($"[grey]  Warmup {i + 1}/{warmIterations}...[/]");
        await TimedBuild(buildFull, workDir, verbose, envVars);
    }
    
    // Measured warm runs
    var warmRuns = new List<TimedRun>();
    for (int i = 0; i < measuredIterations; i++)
    {
        AnsiConsole.MarkupLine($"[grey]  Measured run {i + 1}/{measuredIterations}...[/]");
        var run = await TimedBuild(buildFull, workDir, verbose, envVars);
        warmRuns.Add(run);
    }
    result.WarmRuns = warmRuns;
    result.WarmRunStats = CalculateStats(warmRuns);
    
    // Incremental build
    if (benchmark.Build.Incremental != null)
    {
        if (benchmark.ClearCache != null)
        {
            await ClearCache(benchmark.ClearCache, workDir, verbose);
        }
        
        // Do a full build first
        await TimedBuild(buildFull, workDir, verbose, envVars);
        
        // Touch file
        if (!string.IsNullOrEmpty(benchmark.Build.Incremental.TouchFile))
        {
            var touchPath = Path.Combine(workDir, benchmark.Build.Incremental.TouchFile);
            if (File.Exists(touchPath))
            {
                File.SetLastWriteTimeUtc(touchPath, DateTime.UtcNow);
            }
        }
        
        AnsiConsole.MarkupLine("[grey]  Running incremental build...[/]");
        result.IncrementalBuild = await TimedBuild(benchmark.Build.Incremental, workDir, verbose, envVars);
    }
    
    return result;
}

static async Task ClearCache(ClearCacheConfig config, string workDir, bool verbose)
{
    if (!string.IsNullOrEmpty(config.Command))
    {
        await RunCommandAsync(config.Command, "", workDir, TimeSpan.FromMinutes(2), verbose);
    }
    
    if (config.AdditionalPaths != null)
    {
        foreach (var path in config.AdditionalPaths)
        {
            var fullPath = Path.Combine(workDir, path);
            if (Directory.Exists(fullPath))
            {
                try { Directory.Delete(fullPath, true); } catch { }
            }
        }
    }
}

static async Task<TimedRun> TimedBuild(BuildCommand cmd, string workDir, bool verbose, 
    Dictionary<string, string>? envVars = null)
{
    var timeout = TimeSpan.FromSeconds(cmd.Timeout ?? 300);
    var sw = Stopwatch.StartNew();
    var result = await RunCommandAsync(cmd.Command, "", workDir, timeout, verbose, envVars);
    sw.Stop();
    
    return new TimedRun
    {
        DurationMs = sw.ElapsedMilliseconds,
        Success = result.Success
    };
}

static RunStats CalculateStats(List<TimedRun> runs)
{
    var durations = runs.Where(r => r.Success).Select(r => r.DurationMs).OrderBy(d => d).ToList();
    if (durations.Count == 0) return new RunStats();
    
    var mean = durations.Average();
    var median = durations.Count % 2 == 0
        ? (durations[durations.Count / 2 - 1] + durations[durations.Count / 2]) / 2.0
        : durations[durations.Count / 2];
    
    var variance = durations.Sum(d => Math.Pow(d - mean, 2)) / durations.Count;
    
    return new RunStats
    {
        MinMs = durations.Min(),
        MaxMs = durations.Max(),
        MeanMs = mean,
        MedianMs = median,
        StdDevMs = Math.Sqrt(variance)
    };
}

static string? RunCommandSync(string command, string args, TimeSpan timeout, bool verbose)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetShell(),
            Arguments = GetShellArgs($"{command} {args}"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = Process.Start(psi);
        if (process == null) return null;
        
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit((int)timeout.TotalMilliseconds);
        
        if (verbose) AnsiConsole.MarkupLine($"[grey]{output}[/]");
        
        return process.ExitCode == 0 ? output : null;
    }
    catch
    {
        return null;
    }
}

static async Task<CommandResult> RunCommandAsync(string command, string args, string workDir, 
    TimeSpan timeout, bool verbose, Dictionary<string, string>? envVars = null)
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetShell(),
            Arguments = GetShellArgs($"{command} {args}"),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        if (envVars != null)
        {
            foreach (var (key, value) in envVars)
            {
                psi.Environment[key] = value;
            }
        }
        
        using var process = Process.Start(psi);
        if (process == null) return new CommandResult { Success = false };
        
        using var cts = new CancellationTokenSource(timeout);
        
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            AnsiConsole.MarkupLine("[red]Command timed out[/]");
            return new CommandResult { Success = false };
        }
        
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        
        if (verbose)
        {
            if (!string.IsNullOrWhiteSpace(output)) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output)}[/]");
            if (!string.IsNullOrWhiteSpace(error)) AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        }
        
        return new CommandResult
        {
            Success = process.ExitCode == 0,
            Output = output,
            Error = error,
            ExitCode = process.ExitCode
        };
    }
    catch (Exception ex)
    {
        if (verbose) AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        return new CommandResult { Success = false };
    }
}

static string GetShell() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/sh";
static string GetShellArgs(string command) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
    ? $"/c {command}" 
    : $"-c \"{command.Replace("\"", "\\\"")}\"";

static string GetGitUserName()
{
    try
    {
        var result = RunCommandSync("git", "config user.name", TimeSpan.FromSeconds(5), false);
        return result?.Trim() ?? "anonymous";
    }
    catch
    {
        return "anonymous";
    }
}

static void DisplaySummary(List<BenchmarkResult> results)
{
    AnsiConsole.MarkupLine("\n[bold]Benchmark Summary[/]");
    
    var table = new Table();
    table.AddColumn("Benchmark");
    table.AddColumn("Cold Build");
    table.AddColumn("Warm (median)");
    table.AddColumn("Incremental");
    
    foreach (var result in results)
    {
        var cold = result.ColdRun?.Success == true ? $"{result.ColdRun.DurationMs:N0}ms" : "[red]failed[/]";
        var warm = result.WarmRunStats != null ? $"{result.WarmRunStats.MedianMs:N0}ms" : "-";
        var incr = result.IncrementalBuild?.Success == true ? $"{result.IncrementalBuild.DurationMs:N0}ms" : "-";
        
        table.AddRow(result.Name, cold, warm, incr);
    }
    
    AnsiConsole.Write(table);
}

// ============================================================================
// System Information Collection
// ============================================================================

static SystemInfo CollectSystemInfo()
{
    var info = new SystemInfo
    {
        Os = new OsInfo
        {
            Platform = GetPlatformName(),
            Version = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.OSArchitecture.ToString()
        },
        Cpu = GetCpuInfo(),
        Memory = GetMemoryInfo(),
        Storage = GetStorageInfo(),
        DotNetSdks = GetDotNetSdks(),
        PlatformSpecific = GetPlatformSpecificInfo()
    };
    
    return info;
}

static string GetPlatformName()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
    return "Unknown";
}

static CpuInfo GetCpuInfo()
{
    var info = new CpuInfo
    {
        Cores = Environment.ProcessorCount
    };
    
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        info.Model = RunCommandSync("powershell", 
            "-Command \"(Get-CimInstance Win32_Processor).Name\"", 
            TimeSpan.FromSeconds(10), false)?.Trim();
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        info.Model = RunCommandSync("sysctl", "-n machdep.cpu.brand_string", 
            TimeSpan.FromSeconds(5), false)?.Trim();
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        var cpuinfo = File.Exists("/proc/cpuinfo") ? File.ReadAllText("/proc/cpuinfo") : "";
        var match = System.Text.RegularExpressions.Regex.Match(cpuinfo, @"model name\s*:\s*(.+)");
        info.Model = match.Success ? match.Groups[1].Value.Trim() : null;
    }
    
    return info;
}

static MemoryInfo GetMemoryInfo()
{
    var info = new MemoryInfo();
    
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var output = RunCommandSync("powershell", 
            "-Command \"(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory\"",
            TimeSpan.FromSeconds(10), false);
        if (long.TryParse(output?.Trim(), out var bytes))
        {
            info.CapacityGB = bytes / (1024.0 * 1024 * 1024);
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        var output = RunCommandSync("sysctl", "-n hw.memsize", TimeSpan.FromSeconds(5), false);
        if (long.TryParse(output?.Trim(), out var bytes))
        {
            info.CapacityGB = bytes / (1024.0 * 1024 * 1024);
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        var meminfo = File.Exists("/proc/meminfo") ? File.ReadAllText("/proc/meminfo") : "";
        var match = System.Text.RegularExpressions.Regex.Match(meminfo, @"MemTotal:\s*(\d+)\s*kB");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var kb))
        {
            info.CapacityGB = kb / (1024.0 * 1024);
        }
    }
    
    return info;
}

static StorageInfo GetStorageInfo()
{
    var info = new StorageInfo();
    var cwd = Environment.CurrentDirectory;
    
    try
    {
        var drive = new DriveInfo(Path.GetPathRoot(cwd) ?? cwd);
        info.FreeSpaceGB = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
        info.Type = drive.DriveType.ToString();
        info.FileSystem = drive.DriveFormat; // NTFS, FAT32, exFAT, etc. on Windows
    }
    catch { }
    
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Try to detect SSD vs HDD
        var output = RunCommandSync("powershell",
            "-Command \"(Get-PhysicalDisk | Where-Object { $_.DeviceId -eq 0 }).MediaType\"",
            TimeSpan.FromSeconds(10), false);
        if (!string.IsNullOrWhiteSpace(output))
        {
            info.Type = output.Trim();
        }
        
        // Get file system type (more reliable for ReFS/Dev Drive)
        var fsOutput = RunCommandSync("powershell",
            $"-Command \"(Get-Volume -FilePath '{cwd}').FileSystemType\"",
            TimeSpan.FromSeconds(10), false);
        if (!string.IsNullOrWhiteSpace(fsOutput))
        {
            info.FileSystem = fsOutput.Trim();
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        // Get file system type from df or /proc/mounts
        var output = RunCommandSync("df", $"-T \"{cwd}\"", TimeSpan.FromSeconds(5), false);
        if (!string.IsNullOrWhiteSpace(output))
        {
            // df -T output: Filesystem Type 1K-blocks Used Available Use% Mounted
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                var parts = lines[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    info.FileSystem = parts[1]; // ext4, btrfs, xfs, etc.
                }
            }
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Get file system type from diskutil or mount
        var output = RunCommandSync("df", $"-T \"{cwd}\"", TimeSpan.FromSeconds(5), false);
        if (string.IsNullOrWhiteSpace(output))
        {
            // macOS df doesn't have -T, use mount instead
            output = RunCommandSync("mount", "", TimeSpan.FromSeconds(5), false);
            if (!string.IsNullOrWhiteSpace(output))
            {
                // Find the mount point for cwd
                var cwdRoot = Path.GetPathRoot(cwd) ?? "/";
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains($" on {cwdRoot} ") || line.Contains(" on / "))
                    {
                        // Format: /dev/disk1s1 on / (apfs, local, journaled)
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"\((\w+),");
                        if (match.Success)
                        {
                            info.FileSystem = match.Groups[1].Value.ToUpperInvariant(); // APFS, HFS, etc.
                        }
                        break;
                    }
                }
            }
        }
    }
    
    return info;
}

static List<string> GetDotNetSdks()
{
    var output = RunCommandSync("dotnet", "--list-sdks", TimeSpan.FromSeconds(10), false);
    if (string.IsNullOrWhiteSpace(output)) return new List<string>();
    
    return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split(' ')[0])
        .ToList();
}

static Dictionary<string, object> GetPlatformSpecificInfo()
{
    var info = new Dictionary<string, object>();
    
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Dev Drive detection
        var cwd = Environment.CurrentDirectory;
        var devDriveOutput = RunCommandSync("powershell",
            $"-Command \"(Get-Volume -FilePath '{cwd}').FileSystemType\"",
            TimeSpan.FromSeconds(10), false);
        info["IsDevDrive"] = devDriveOutput?.Trim().Equals("ReFS", StringComparison.OrdinalIgnoreCase) == true;
        
        // Windows build
        info["WindowsBuild"] = Environment.OSVersion.Version.Build;
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Apple Silicon detection
        var arch = RuntimeInformation.ProcessArchitecture;
        info["IsAppleSilicon"] = arch == Architecture.Arm64;
        
        var chipOutput = RunCommandSync("sysctl", "-n machdep.cpu.brand_string", 
            TimeSpan.FromSeconds(5), false);
        info["ChipModel"] = chipOutput?.Trim() ?? "Unknown";
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        // Distribution
        if (File.Exists("/etc/os-release"))
        {
            var osRelease = File.ReadAllText("/etc/os-release");
            var nameMatch = System.Text.RegularExpressions.Regex.Match(osRelease, @"PRETTY_NAME=""(.+)""");
            if (nameMatch.Success)
            {
                info["Distribution"] = nameMatch.Groups[1].Value;
            }
        }
        
        // Kernel version
        var kernelOutput = RunCommandSync("uname", "-r", TimeSpan.FromSeconds(5), false);
        info["KernelVersion"] = kernelOutput?.Trim() ?? "Unknown";
    }
    
    return info;
}

// ============================================================================
// Data Models
// ============================================================================

#region Models

class BenchmarkManifest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "in-repo";
    public string? RepoUrl { get; set; }
    public string? RepoRef { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? Platforms { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
    public List<Prerequisite>? Prerequisites { get; set; }
    public RestoreConfig? Restore { get; set; }
    public ClearCacheConfig? ClearCache { get; set; }
    public BuildConfig? Build { get; set; }
    public int? WarmupIterations { get; set; }
    public int? MeasuredIterations { get; set; }
    
    [JsonIgnore]
    public string FolderName { get; set; } = "";
}

class Prerequisite
{
    public string Command { get; set; } = "";
    public string? MinVersion { get; set; }
}

class RestoreConfig
{
    public string Command { get; set; } = "";
    public int? Timeout { get; set; }
}

class ClearCacheConfig
{
    public string? Command { get; set; }
    public List<string>? AdditionalPaths { get; set; }
}

class BuildConfig
{
    public BuildCommand? Full { get; set; }
    public BuildCommand? Incremental { get; set; }
}

class BuildCommand
{
    public string Command { get; set; } = "";
    public int? Timeout { get; set; }
    public string? TouchFile { get; set; }
}

class BenchmarkSubmission
{
    public string SubmissionId { get; set; } = "";
    public string Submitter { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public SystemInfo Machine { get; set; } = new();
    public List<BenchmarkResult> Benchmarks { get; set; } = new();
}

class BenchmarkResult
{
    public string Name { get; set; } = "";
    public TimedRun? ColdRun { get; set; }
    public List<TimedRun>? WarmRuns { get; set; }
    public RunStats? WarmRunStats { get; set; }
    public TimedRun? IncrementalBuild { get; set; }
}

class TimedRun
{
    public long DurationMs { get; set; }
    public bool Success { get; set; }
}

class RunStats
{
    public long MinMs { get; set; }
    public long MaxMs { get; set; }
    public double MeanMs { get; set; }
    public double MedianMs { get; set; }
    public double StdDevMs { get; set; }
}

class SystemInfo
{
    public OsInfo Os { get; set; } = new();
    public CpuInfo Cpu { get; set; } = new();
    public MemoryInfo Memory { get; set; } = new();
    public StorageInfo Storage { get; set; } = new();
    public List<string> DotNetSdks { get; set; } = new();
    public Dictionary<string, object> PlatformSpecific { get; set; } = new();
}

class OsInfo
{
    public string Platform { get; set; } = "";
    public string Version { get; set; } = "";
    public string Architecture { get; set; } = "";
}

class CpuInfo
{
    public string? Model { get; set; }
    public int Cores { get; set; }
    public string? Frequency { get; set; }
}

class MemoryInfo
{
    public double CapacityGB { get; set; }
    public int? SpeedMHz { get; set; }
}

class StorageInfo
{
    public string? Type { get; set; }
    public string? FileSystem { get; set; }
    public string? Model { get; set; }
    public double FreeSpaceGB { get; set; }
}

class CommandResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public int ExitCode { get; set; }
}

#endregion

// ============================================================================
// JSON Source Generator for Native AOT
// ============================================================================

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BenchmarkManifest))]
[JsonSerializable(typeof(BenchmarkSubmission))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(List<BenchmarkManifest>))]
partial class DevBenchJsonContext : JsonSerializerContext { }
