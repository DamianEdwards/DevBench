#!/usr/bin/env dotnet
#:package Spectre.Console@0.50.0

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

// ============================================================================
// DevBench - Developer PC Benchmarking Tool
// ============================================================================

// Handle --version flag first
if (args.Contains("--version") || args.Contains("-V"))
{
    var version = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    Console.WriteLine($"DevBench {version}");
    return 0;
}

var verbose = args.Contains("--verbose") || args.Contains("-v");
var systemInfoOnly = args.Contains("--system-info-only");
var restoreOnly = args.Contains("--restore-only");
var specificBenchmark = GetArgValue(args, "--benchmark") ?? GetArgValue(args, "-b");
var benchmarksPathArg = GetArgValue(args, "--benchmarks-path");

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
string benchmarksDir;
if (!string.IsNullOrEmpty(benchmarksPathArg) && Directory.Exists(benchmarksPathArg))
{
    benchmarksDir = benchmarksPathArg;
}
else
{
    benchmarksDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "benchmarks");
    if (!Directory.Exists(benchmarksDir))
    {
        benchmarksDir = Path.Combine(Environment.CurrentDirectory, "benchmarks");
    }
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
            .AddChoices(benchmarks.OrderBy(GetMaxTimeout))
            .UseConverter(b => $"{b.Name} [grey](up to {FormatDuration(GetMaxTimeout(b))}) - {b.Description}[/]"));
}

if (selectedBenchmarks.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No benchmarks selected[/]");
    return 0;
}

// Collect toolchains from all selected benchmarks
var allToolchainConfigs = selectedBenchmarks
    .Where(b => b.Toolchains != null)
    .SelectMany(b => b.Toolchains!)
    .DistinctBy(t => t.Stack ?? t.Name ?? t.Command)
    .ToList();
systemInfo.Toolchains = GetToolchains(allToolchainConfigs);

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
        var prereqCmd = ResolvePlatformCommand(prereq.Command);
        if (string.IsNullOrEmpty(prereqCmd))
            continue; // Skip prerequisites that don't apply to this platform
            
        try
        {
            var output = RunCommandSync(prereqCmd, "", TimeSpan.FromSeconds(10), verbose);
            if (output == null) return false;
            
            if (!string.IsNullOrEmpty(prereq.MinVersion))
            {
                var version = ExtractVersion(output);
                if (version != null && Version.TryParse(prereq.MinVersion, out var minVer))
                {
                    if (version < minVer)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{prereqCmd}: {version} < {minVer}[/]");
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

static int GetMaxTimeout(BenchmarkManifest benchmark)
{
    var timeouts = new List<int>();
    if (benchmark.Restore?.Timeout != null) timeouts.Add(benchmark.Restore.Timeout.Value);
    if (benchmark.Build?.Full?.Timeout != null) timeouts.Add(benchmark.Build.Full.Timeout.Value);
    if (benchmark.Build?.Incremental?.Timeout != null) timeouts.Add(benchmark.Build.Incremental.Timeout.Value);
    return timeouts.Count > 0 ? timeouts.Max() : 300; // Default 5 min
}

static string FormatDuration(int seconds)
{
    var ts = TimeSpan.FromSeconds(seconds);
    if (ts.TotalMinutes < 1) return $"{ts.Seconds}s";
    if (ts.Seconds == 0) return $"{(int)ts.TotalMinutes}m";
    return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
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
    
    var restoreCmd = ResolvePlatformCommand(benchmark.Restore.Command);
    if (string.IsNullOrEmpty(restoreCmd))
    {
        AnsiConsole.MarkupLine("[yellow]  No restore command for this platform[/]");
        return;
    }
    
    AnsiConsole.MarkupLine($"[blue]  Restoring {benchmark.Name}...[/]");
    AnsiConsole.MarkupLine($"[grey]  Working directory: {workDir}[/]");
    AnsiConsole.MarkupLine($"[grey]  Command: {restoreCmd}[/]");
    
    var timeout = TimeSpan.FromSeconds(benchmark.Restore.Timeout ?? 300);
    var sw = Stopwatch.StartNew();
    var result = await RunCommandAsync(restoreCmd, "", workDir, timeout, verbose, envVars);
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
    
    // Clear cache first (before restore, so restore repopulates what's needed)
    if (benchmark.ClearCache != null)
    {
        await ClearCache(benchmark.ClearCache, workDir, verbose);
    }
    
    // Restore phase
    if (benchmark.Restore != null)
    {
        var restoreCmd = ResolvePlatformCommand(benchmark.Restore.Command);
        if (!string.IsNullOrEmpty(restoreCmd))
        {
            AnsiConsole.MarkupLine("[grey]  Restoring dependencies...[/]");
            var timeout = TimeSpan.FromSeconds(benchmark.Restore.Timeout ?? 300);
            await RunCommandAsync(restoreCmd, "", workDir, timeout, verbose, envVars);
        }
    }
    
    // Run pre-build steps (e.g., dotnet build-server shutdown)
    if (benchmark.PreBuild != null)
    {
        foreach (var stepElement in benchmark.PreBuild)
        {
            var step = ResolvePlatformCommand(stepElement);
            if (!string.IsNullOrEmpty(step))
            {
                if (verbose) AnsiConsole.MarkupLine($"[grey]  Pre-build: {step}[/]");
                var preBuildResult = await RunCommandAsync(step, "", workDir, TimeSpan.FromMinutes(5), verbose, envVars);
                if (!preBuildResult.Success)
                {
                    AnsiConsole.MarkupLine($"[red]  Pre-build step failed: {step}[/]");
                    return result;
                }
            }
        }
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
    
    // Incremental build - tests rebuild after a small change
    // No cache clear - we want to measure incremental build from a warm state
    if (benchmark.Build.Incremental != null)
    {
        // Touch file to simulate a code change
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
    var clearCmd = ResolvePlatformCommand(config.Command);
    if (!string.IsNullOrEmpty(clearCmd))
    {
        await RunCommandAsync(clearCmd, "", workDir, TimeSpan.FromMinutes(2), verbose);
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
    var buildCmd = ResolvePlatformCommand(cmd.Command);
    if (string.IsNullOrEmpty(buildCmd))
    {
        return new TimedRun { DurationMs = 0, Success = false };
    }
    
    var timeout = TimeSpan.FromSeconds(cmd.Timeout ?? 300);
    var sw = Stopwatch.StartNew();
    var result = await RunCommandAsync(buildCmd, "", workDir, timeout, verbose, envVars);
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
        
        // Read stdout/stderr before WaitForExit to avoid deadlock
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit((int)timeout.TotalMilliseconds);
        
        if (verbose) 
        {
            if (!string.IsNullOrWhiteSpace(output)) AnsiConsole.MarkupLine($"[grey]{Markup.Escape(output)}[/]");
            if (!string.IsNullOrWhiteSpace(error)) AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        }
        
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
        
        // Read stdout/stderr concurrently to avoid deadlock when buffers fill
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        
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
        
        var output = await outputTask;
        var error = await errorTask;
        
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

static string GetCurrentPlatformKey()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
    return "";
}

static string? ResolvePlatformCommand(JsonElement? commandElement)
{
    if (commandElement == null) return null;
    
    var element = commandElement.Value;
    
    // If it's a string, use it directly
    if (element.ValueKind == JsonValueKind.String)
    {
        return element.GetString();
    }
    
    // If it's an object, look up platform-specific command
    if (element.ValueKind == JsonValueKind.Object)
    {
        var platformKey = GetCurrentPlatformKey();
        
        // Try platform-specific key first
        if (element.TryGetProperty(platformKey, out var platformCommand) && 
            platformCommand.ValueKind == JsonValueKind.String)
        {
            return platformCommand.GetString();
        }
        
        // Fall back to empty string key (default)
        if (element.TryGetProperty("", out var defaultCommand) && 
            defaultCommand.ValueKind == JsonValueKind.String)
        {
            return defaultCommand.GetString();
        }
    }
    
    return null;
}
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
        Toolchains = new List<ToolchainInfo>(),
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
        // Get total capacity
        var output = RunCommandSync("powershell", 
            "-Command \"(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory\"",
            TimeSpan.FromSeconds(10), false);
        if (long.TryParse(output?.Trim(), out var bytes))
        {
            info.CapacityGB = bytes / (1024.0 * 1024 * 1024);
        }
        
        // Get detailed memory info from Win32_PhysicalMemory
        var memDetailsOutput = RunCommandSync("powershell",
            "-Command \"Get-CimInstance Win32_PhysicalMemory | ConvertTo-Json\"",
            TimeSpan.FromSeconds(10), false);
        if (!string.IsNullOrWhiteSpace(memDetailsOutput))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(memDetailsOutput);
                var root = doc.RootElement;
                
                // Could be array or single object
                var memories = root.ValueKind == System.Text.Json.JsonValueKind.Array 
                    ? root.EnumerateArray().ToList() 
                    : new List<System.Text.Json.JsonElement> { root };
                
                info.DimmCount = memories.Count;
                
                if (memories.Count > 0)
                {
                    var first = memories[0];
                    
                    if (first.TryGetProperty("Speed", out var speedProp) && speedProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        info.SpeedMHz = speedProp.GetInt32();
                    }
                    
                    if (first.TryGetProperty("Manufacturer", out var mfgProp) && mfgProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var mfg = mfgProp.GetString();
                        if (!string.IsNullOrWhiteSpace(mfg) && mfg != "Unknown")
                        {
                            info.Manufacturer = mfg.Trim();
                        }
                    }
                    
                    if (first.TryGetProperty("PartNumber", out var partProp) && partProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var part = partProp.GetString();
                        if (!string.IsNullOrWhiteSpace(part))
                        {
                            info.PartNumber = part.Trim();
                        }
                    }
                    
                    if (first.TryGetProperty("FormFactor", out var ffProp) && ffProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var ff = ffProp.GetInt32();
                        info.FormFactor = ff switch
                        {
                            8 => "DIMM",
                            12 => "SODIMM",
                            _ => ff.ToString()
                        };
                    }
                }
            }
            catch { }
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        var output = RunCommandSync("sysctl", "-n hw.memsize", TimeSpan.FromSeconds(5), false);
        if (long.TryParse(output?.Trim(), out var bytes))
        {
            info.CapacityGB = bytes / (1024.0 * 1024 * 1024);
        }
        
        // Try to get memory details from system_profiler
        var memProfile = RunCommandSync("system_profiler", "SPMemoryDataType", TimeSpan.FromSeconds(10), false);
        if (!string.IsNullOrWhiteSpace(memProfile))
        {
            var speedMatch = System.Text.RegularExpressions.Regex.Match(memProfile, @"Speed:\s*(\d+)\s*MHz");
            if (speedMatch.Success && int.TryParse(speedMatch.Groups[1].Value, out var speed))
            {
                info.SpeedMHz = speed;
            }
            
            var typeMatch = System.Text.RegularExpressions.Regex.Match(memProfile, @"Type:\s*(\S+)");
            if (typeMatch.Success)
            {
                info.FormFactor = typeMatch.Groups[1].Value;
            }
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
        
        // Try dmidecode (requires root, may not work)
        var dmiOutput = RunCommandSync("dmidecode", "-t memory", TimeSpan.FromSeconds(5), false);
        if (!string.IsNullOrWhiteSpace(dmiOutput))
        {
            var speedMatch = System.Text.RegularExpressions.Regex.Match(dmiOutput, @"Speed:\s*(\d+)\s*MT/s");
            if (speedMatch.Success && int.TryParse(speedMatch.Groups[1].Value, out var speed))
            {
                info.SpeedMHz = speed;
            }
            
            var mfgMatch = System.Text.RegularExpressions.Regex.Match(dmiOutput, @"Manufacturer:\s*(.+)");
            if (mfgMatch.Success)
            {
                var mfg = mfgMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(mfg) && mfg != "Unknown")
                {
                    info.Manufacturer = mfg;
                }
            }
            
            var partMatch = System.Text.RegularExpressions.Regex.Match(dmiOutput, @"Part Number:\s*(.+)");
            if (partMatch.Success)
            {
                info.PartNumber = partMatch.Groups[1].Value.Trim();
            }
            
            // Count DIMMs
            var dimmMatches = System.Text.RegularExpressions.Regex.Matches(dmiOutput, @"Size:\s*\d+\s*(MB|GB)");
            if (dimmMatches.Count > 0)
            {
                info.DimmCount = dimmMatches.Count;
            }
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
        info.FileSystem = drive.DriveFormat;
    }
    catch { }
    
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        // Get detailed disk info for the drive containing cwd using encoded command
        var escapedCwd = cwd.Replace("'", "''");
        var script = $@"
$volume = Get-Volume -FilePath '{escapedCwd}'
$partition = Get-Partition -DriveLetter $volume.DriveLetter
$disk = Get-PhysicalDisk | Where-Object {{ $_.DeviceId -eq $partition.DiskNumber }}
@{{
    MediaType = $disk.MediaType
    Model = $disk.Model
    Manufacturer = $disk.Manufacturer
    BusType = $disk.BusType
    FileSystemType = $volume.FileSystemType
}} | ConvertTo-Json -Compress
";
        var bytes = System.Text.Encoding.Unicode.GetBytes(script);
        var encodedCommand = Convert.ToBase64String(bytes);
        
        var output = RunCommandSync("powershell", $"-EncodedCommand {encodedCommand}", TimeSpan.FromSeconds(15), false);
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(output);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("MediaType", out var mtProp) && mtProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    info.Type = mtProp.GetString();
                }
                
                if (root.TryGetProperty("Model", out var modelProp) && modelProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    info.Model = modelProp.GetString()?.Trim();
                }
                
                // Try to get manufacturer, fall back to extracting from model
                string? manufacturer = null;
                if (root.TryGetProperty("Manufacturer", out var mfgProp) && mfgProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    manufacturer = mfgProp.GetString()?.Trim();
                }
                
                if (!string.IsNullOrWhiteSpace(manufacturer))
                {
                    info.Manufacturer = manufacturer;
                }
                else if (!string.IsNullOrWhiteSpace(info.Model))
                {
                    // Extract manufacturer from model name
                    var firstWord = info.Model.Split(' ').FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(firstWord))
                    {
                        info.Manufacturer = firstWord;
                    }
                }
                
                if (root.TryGetProperty("BusType", out var busProp) && busProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    info.BusType = busProp.GetString();
                }
                
                if (root.TryGetProperty("FileSystemType", out var fsProp) && fsProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var fs = fsProp.GetString();
                    if (!string.IsNullOrWhiteSpace(fs))
                    {
                        info.FileSystem = fs;
                        info.IsDevDrive = fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        // Get file system type from df
        var dfOutput = RunCommandSync("df", $"-T \"{cwd}\"", TimeSpan.FromSeconds(5), false);
        if (!string.IsNullOrWhiteSpace(dfOutput))
        {
            var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                var parts = lines[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    info.FileSystem = parts[1];
                }
            }
        }
        
        // Get disk model from lsblk
        var lsblkOutput = RunCommandSync("lsblk", "-o NAME,MODEL,TRAN -d", TimeSpan.FromSeconds(5), false);
        if (!string.IsNullOrWhiteSpace(lsblkOutput))
        {
            var lines = lsblkOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
            {
                var parts = lines[1].Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    info.Model = parts[1].Trim();
                    if (!string.IsNullOrWhiteSpace(info.Model))
                    {
                        info.Manufacturer = info.Model.Split(' ').FirstOrDefault();
                    }
                }
                if (parts.Length >= 3)
                {
                    info.BusType = parts[2].Trim().ToUpperInvariant();
                }
            }
        }
        
        // Check if SSD
        var rotOutput = RunCommandSync("lsblk", "-o ROTA -d", TimeSpan.FromSeconds(5), false);
        if (!string.IsNullOrWhiteSpace(rotOutput) && rotOutput.Contains("0"))
        {
            info.Type = "SSD";
        }
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        // Get disk info from diskutil
        var diskOutput = RunCommandSync("diskutil", "info /", TimeSpan.FromSeconds(5), false);
        if (!string.IsNullOrWhiteSpace(diskOutput))
        {
            var fsMatch = System.Text.RegularExpressions.Regex.Match(diskOutput, @"Type \(Bundle\):\s*(\w+)");
            if (fsMatch.Success)
            {
                info.FileSystem = fsMatch.Groups[1].Value.ToUpperInvariant();
            }
            
            var solidMatch = System.Text.RegularExpressions.Regex.Match(diskOutput, @"Solid State:\s*(\w+)");
            if (solidMatch.Success && solidMatch.Groups[1].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase))
            {
                info.Type = "SSD";
            }
        }
        
        // Get storage profile
        var storageProfile = RunCommandSync("system_profiler", "SPStorageDataType", TimeSpan.FromSeconds(10), false);
        if (!string.IsNullOrWhiteSpace(storageProfile))
        {
            var modelMatch = System.Text.RegularExpressions.Regex.Match(storageProfile, @"Device Name:\s*(.+)");
            if (modelMatch.Success)
            {
                info.Model = modelMatch.Groups[1].Value.Trim();
                info.Manufacturer = info.Model.Split(' ').FirstOrDefault();
            }
            
            var protoMatch = System.Text.RegularExpressions.Regex.Match(storageProfile, @"Protocol:\s*(\w+)");
            if (protoMatch.Success)
            {
                info.BusType = protoMatch.Groups[1].Value;
            }
        }
    }
    
    return info;
}

static List<ToolchainInfo> GetToolchains(List<ToolchainConfig>? configs)
{
    var toolchains = new List<ToolchainInfo>();
    if (configs == null || configs.Count == 0) return toolchains;
    
    foreach (var config in configs)
    {
        if (!string.IsNullOrEmpty(config.Stack))
        {
            var stackToolchains = GetStackToolchains(config.Stack);
            toolchains.AddRange(stackToolchains);
        }
        else if (!string.IsNullOrEmpty(config.Command) && !string.IsNullOrEmpty(config.Name))
        {
            var version = GetCustomToolVersion(config.Command);
            if (!string.IsNullOrEmpty(version))
            {
                toolchains.Add(new ToolchainInfo { Name = config.Name, Versions = new List<string> { version } });
            }
        }
    }
    
    return toolchains;
}

static List<ToolchainInfo> GetStackToolchains(string stack)
{
    var toolchains = new List<ToolchainInfo>();
    
    switch (stack.ToLowerInvariant())
    {
        case "dotnet":
        case ".net":
            var dotnetSdks = GetDotNetSdks();
            if (dotnetSdks.Count > 0)
            {
                toolchains.Add(new ToolchainInfo { Name = ".NET SDK", Versions = dotnetSdks });
            }
            break;
            
        case "node":
        case "nodejs":
            var nodeVersion = RunCommandSync("node", "--version", TimeSpan.FromSeconds(5), false)?.Trim().TrimStart('v');
            var npmVersion = RunCommandSync("npm", "--version", TimeSpan.FromSeconds(5), false)?.Trim();
            if (!string.IsNullOrEmpty(nodeVersion))
            {
                toolchains.Add(new ToolchainInfo { Name = "Node.js", Versions = new List<string> { nodeVersion } });
            }
            if (!string.IsNullOrEmpty(npmVersion))
            {
                toolchains.Add(new ToolchainInfo { Name = "npm", Versions = new List<string> { npmVersion } });
            }
            break;
            
        case "python":
            var pyVersion = RunCommandSync("python", "--version", TimeSpan.FromSeconds(5), false)?.Trim();
            if (string.IsNullOrEmpty(pyVersion))
            {
                pyVersion = RunCommandSync("python3", "--version", TimeSpan.FromSeconds(5), false)?.Trim();
            }
            if (!string.IsNullOrEmpty(pyVersion))
            {
                var version = pyVersion.Replace("Python ", "");
                toolchains.Add(new ToolchainInfo { Name = "Python", Versions = new List<string> { version } });
            }
            break;
            
        case "rust":
            var rustcVersion = RunCommandSync("rustc", "--version", TimeSpan.FromSeconds(5), false)?.Trim();
            var cargoVersion = RunCommandSync("cargo", "--version", TimeSpan.FromSeconds(5), false)?.Trim();
            if (!string.IsNullOrEmpty(rustcVersion))
            {
                var version = System.Text.RegularExpressions.Regex.Match(rustcVersion, @"\d+\.\d+\.\d+").Value;
                toolchains.Add(new ToolchainInfo { Name = "rustc", Versions = new List<string> { version } });
            }
            if (!string.IsNullOrEmpty(cargoVersion))
            {
                var version = System.Text.RegularExpressions.Regex.Match(cargoVersion, @"\d+\.\d+\.\d+").Value;
                toolchains.Add(new ToolchainInfo { Name = "cargo", Versions = new List<string> { version } });
            }
            break;
            
        case "go":
        case "golang":
            var goVersion = RunCommandSync("go", "version", TimeSpan.FromSeconds(5), false)?.Trim();
            if (!string.IsNullOrEmpty(goVersion))
            {
                var version = System.Text.RegularExpressions.Regex.Match(goVersion, @"go(\d+\.\d+(\.\d+)?)").Groups[1].Value;
                toolchains.Add(new ToolchainInfo { Name = "Go", Versions = new List<string> { version } });
            }
            break;
            
        case "java":
            var javaVersion = RunCommandSync("java", "--version", TimeSpan.FromSeconds(5), false)?.Trim();
            if (!string.IsNullOrEmpty(javaVersion))
            {
                var firstLine = javaVersion.Split('\n').FirstOrDefault() ?? "";
                var version = System.Text.RegularExpressions.Regex.Match(firstLine, @"\d+(\.\d+)*").Value;
                toolchains.Add(new ToolchainInfo { Name = "Java", Versions = new List<string> { version } });
            }
            break;
    }
    
    return toolchains;
}

static string? GetCustomToolVersion(string command)
{
    var parts = command.Split(' ', 2);
    var tool = parts[0];
    var args = parts.Length > 1 ? parts[1] : "";
    
    var output = RunCommandSync(tool, args, TimeSpan.FromSeconds(10), false)?.Trim();
    if (string.IsNullOrEmpty(output)) return null;
    
    // Try to extract version number
    var versionMatch = System.Text.RegularExpressions.Regex.Match(output, @"(\d+\.\d+(\.\d+)*)");
    return versionMatch.Success ? versionMatch.Groups[1].Value : output.Split('\n').FirstOrDefault();
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
    public List<ToolchainConfig>? Toolchains { get; set; }
    public RestoreConfig? Restore { get; set; }
    public ClearCacheConfig? ClearCache { get; set; }
    public List<JsonElement>? PreBuild { get; set; }
    public BuildConfig? Build { get; set; }
    public int? WarmupIterations { get; set; }
    public int? MeasuredIterations { get; set; }
    
    [JsonIgnore]
    public string FolderName { get; set; } = "";
}

class ToolchainConfig
{
    public string? Stack { get; set; }
    public string? Name { get; set; }
    public string? Command { get; set; }
}

class Prerequisite
{
    public JsonElement? Command { get; set; }
    public string? MinVersion { get; set; }
}

class RestoreConfig
{
    public JsonElement? Command { get; set; }
    public int? Timeout { get; set; }
}

class ClearCacheConfig
{
    public JsonElement? Command { get; set; }
    public List<string>? AdditionalPaths { get; set; }
}

class BuildConfig
{
    public BuildCommand? Full { get; set; }
    public BuildCommand? Incremental { get; set; }
}

class BuildCommand
{
    public JsonElement? Command { get; set; }
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
    public List<ToolchainInfo> Toolchains { get; set; } = new();
    public Dictionary<string, object> PlatformSpecific { get; set; } = new();
}

class ToolchainInfo
{
    public string Name { get; set; } = "";
    public List<string> Versions { get; set; } = new();
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
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public int? DimmCount { get; set; }
    public string? FormFactor { get; set; }
}

class StorageInfo
{
    public string? Type { get; set; }
    public string? FileSystem { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? BusType { get; set; }
    public bool IsDevDrive { get; set; }
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
[JsonSerializable(typeof(ToolchainInfo))]
[JsonSerializable(typeof(ToolchainConfig))]
partial class DevBenchJsonContext : JsonSerializerContext { }
