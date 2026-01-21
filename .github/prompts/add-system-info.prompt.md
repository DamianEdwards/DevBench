# Add System Information Collection

This prompt guides you through adding new system information to DevBench.

## Overview

System information is collected in `src/DevBench.cs` and stored in the results JSON. Each piece of info helps users compare their performance against similar machines.

## Where to Add Code

In `src/DevBench.cs`, find the `CollectSystemInfo()` method. It's organized by:

1. **Cross-platform info** - Works on all OSes
2. **Windows-specific** - Inside `#if WINDOWS` block
3. **macOS-specific** - Inside `#if MACOS` block  
4. **Linux-specific** - Inside `#if LINUX` block

## Adding Cross-Platform Info

```csharp
// In CollectSystemInfo()
systemInfo.NewProperty = GetNewProperty();

// Add helper method
static string GetNewProperty()
{
    // Implementation that works on all platforms
    return "value";
}
```

## Adding Platform-Specific Info

```csharp
#if WINDOWS
    systemInfo.PlatformSpecific.WindowsProperty = GetWindowsProperty();
#elif MACOS
    systemInfo.PlatformSpecific.MacProperty = GetMacProperty();
#elif LINUX
    systemInfo.PlatformSpecific.LinuxProperty = GetLinuxProperty();
#endif
```

## Common Techniques

### Run a command and parse output
```csharp
static string RunCommand(string command, string args)
{
    var psi = new ProcessStartInfo(command, args)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var process = Process.Start(psi);
    return process?.StandardOutput.ReadToEnd().Trim() ?? "";
}
```

### Windows: Query WMI
```csharp
#if WINDOWS
static string GetWmiValue(string className, string propertyName)
{
    // Use System.Management or PowerShell
    var output = RunCommand("powershell", 
        $"-Command \"(Get-CimInstance {className}).{propertyName}\"");
    return output;
}
#endif
```

### macOS: Use system_profiler
```csharp
#if MACOS
static string GetMacSystemInfo(string dataType)
{
    return RunCommand("system_profiler", $"{dataType} -json");
}
#endif
```

### Linux: Read from /proc or /sys
```csharp
#if LINUX
static string ReadProcFile(string path)
{
    return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
}
#endif
```

## Update the Results Schema

After adding new info, update the `SystemInfo` class and `docs/benchmark-schema.md` to document the new field.

## Checklist

- [ ] Added collection code to `CollectSystemInfo()`
- [ ] Used appropriate platform guards (`#if WINDOWS` etc.)
- [ ] Handled errors gracefully (return empty string or default)
- [ ] Updated `SystemInfo` class with new property
- [ ] Updated `docs/benchmark-schema.md` with new field
- [ ] Tested on target platform(s)
- [ ] Verified results JSON includes new field

## Testing

```powershell
dotnet run src/DevBench.cs -- --system-info-only
```

This prints collected system info without running benchmarks.
