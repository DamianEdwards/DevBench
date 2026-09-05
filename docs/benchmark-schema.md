# Benchmark Schema

This document describes the `benchmark.json` manifest format used to define benchmarks in DevBench.

## Example

```json
{
  "name": "Aspire Starter",
  "description": "Build the .NET Aspire starter template",
  "type": "external-repo",
  "repoUrl": "https://github.com/dotnet/aspire",
  "repoRef": "v9.0.0",
  "tags": ["dotnet", "aspire", "web"],
  "platforms": ["windows", "linux", "macos"],
  "workingDirectory": "./samples/AspireStarterApp",
  "environmentVariables": {
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1"
  },
  "prerequisites": [
    { "command": "dotnet --version", "minVersion": "10.0" }
  ],
  "restore": {
    "command": "dotnet restore",
    "timeout": 300
  },
  "clearCache": {
    "command": "dotnet clean",
    "additionalPaths": ["bin", "obj", ".vs"]
  },
  "preBuild": [
    "dotnet build-server shutdown"
  ],
  "build": {
    "clean": {
      "command": "dotnet clean -c Release",
      "timeout": 300
    },
    "full": {
      "command": "dotnet build -c Release --no-restore",
      "timeout": 300
    },
    "incremental": {
      "command": "dotnet build -c Release --no-restore",
      "touchFile": "src/Program.cs",
      "timeout": 120
    }
  },
  "warmupIterations": 2,
  "measuredIterations": 5
}
```

## Field Reference

### Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Display name for the benchmark |
| `description` | string | What the benchmark measures |
| `type` | string | `"in-repo"` or `"external-repo"` |
| `build.clean` | object | Command that removes build outputs before each warm build |
| `build.full` | object | Timed full-build command |

### Optional Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `repoUrl` | string | - | Git clone URL (required for external-repo) |
| `repoRef` | string | default branch | Commit SHA, tag, or branch to clone |
| `tags` | string[] | `[]` | Categories for filtering (e.g., `["dotnet", "web"]`) |
| `platforms` | string[] | all | Restrict to specific OS: `"windows"`, `"linux"`, `"macos"` |
| `workingDirectory` | string | root | Subdirectory for running commands |
| `environmentVariables` | object | `{}` | Environment variables to set during benchmark |
| `prerequisites` | array | `[]` | Commands to check before running |
| `restore` | object | - | Dependency restore phase configuration |
| `clearCache` | object | - | Cache clearing configuration |
| `preBuild` | string[] | `[]` | Commands to run before cold build (e.g., `["dotnet build-server shutdown"]`) |
| `build.incremental` | object | - | Incremental build configuration |
| `warmupIterations` | number | `2` | Number of warm-up runs (not measured) |
| `measuredIterations` | number | `5` | Number of timed runs to average |

## Detailed Field Descriptions

### `type`

- `"in-repo"`: Source code is in `benchmarks/<name>/src/`
- `"external-repo"`: Source is cloned from `repoUrl` to `.cache/`

### `prerequisites`

Array of prerequisite checks. Each check has:

```json
{
  "command": "dotnet --version",
  "minVersion": "10.0"
}
```

- `command`: Command to run to check prerequisite
- `minVersion`: Minimum version required (optional)

If prerequisites aren't met, the benchmark is skipped with a warning.

### `restore`

```json
{
  "command": "dotnet restore",
  "timeout": 300
}
```

- `command`: Restore command to run
- `timeout`: Maximum seconds to wait (default: 300)

The restore phase runs before benchmarking and is not timed.

### `clearCache`

```json
{
  "command": "dotnet clean",
  "additionalPaths": ["bin", "obj", ".vs"]
}
```

- `command`: Command to clear caches
- `additionalPaths`: Additional directories to delete

Runs **before restore** to ensure a completely clean state. The restore phase repopulates `obj/project.assets.json` after cleaning.

### `preBuild`

```json
["dotnet build-server shutdown"]
```

Array of commands to run before the cold build starts. Not timed. Use this for:
- Shutting down build servers (`dotnet build-server shutdown`)
- Any other setup that should happen before timing begins

**For .NET benchmarks:** Always include `"dotnet build-server shutdown"` to ensure cold builds are truly cold.

### `build.clean`

```json
{
  "command": "dotnet clean -c Release",
  "timeout": 300
}
```

- `command`: Command to remove build outputs
- `timeout`: Maximum seconds to wait (default: 300)

Runs untimed before every warmup and measured warm build. It does not run before the
incremental build, which uses the output from the final warm build. The command should
preserve restored dependencies and generated inputs, and must not shut down build
servers. For .NET benchmarks, use `dotnet clean` with the same configuration and target
as `build.full`.

### `build.full`

```json
{
  "command": "dotnet build -c Release",
  "timeout": 300
}
```

- `command`: Build command to execute
- `timeout`: Maximum seconds to wait (default: 300)

### `build.incremental`

```json
{
  "command": "dotnet build -c Release",
  "touchFile": "src/Program.cs",
  "timeout": 120
}
```

- `command`: Build command for incremental build
- `touchFile`: File to touch before incremental build
- `timeout`: Maximum seconds to wait (default: 120)

If `touchFile` is specified, it's touched (last modified time updated) before the incremental build.

### `environmentVariables`

```json
{
  "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
  "DOTNET_NOLOGO": "1"
}
```

Key-value pairs of environment variables to set during benchmark execution.

## Platform-Specific Commands

Commands in `restore.command`, `clearCache.command`, `build.clean.command`, `build.full.command`, `build.incremental.command`, and `preBuild` array elements can be either:

1. **A simple string** - used on all platforms:
   ```json
   "command": "dotnet build"
   ```

2. **An object with platform keys** - for platform-specific commands:
   ```json
   "command": {
     "windows": "python generate_code.py",
     "linux": "python3 generate_code.py",
     "macos": "python3 generate_code.py",
     "": "python3 generate_code.py"
   }
   ```

### Platform Keys

| Key | Platform |
|-----|----------|
| `"windows"` | Windows only |
| `"linux"` | Linux only |
| `"macos"` | macOS only |
| `""` (empty string) | Fallback/default if no platform-specific key matches |

### Resolution Logic

1. If the command is a string, it's used on all platforms
2. If the command is an object:
   - Look for the current platform key (`windows`, `linux`, or `macos`)
   - If not found, use the `""` (empty string) key as fallback
   - If no match, the command is skipped

### Example: Platform-specific preBuild

```json
{
  "preBuild": [
    "dotnet build-server shutdown",
    { "windows": "python generate_code.py", "": "python3 generate_code.py" }
  ]
}
```

This runs `dotnet build-server shutdown` on all platforms, then runs `python generate_code.py` on Windows or `python3 generate_code.py` on Linux/macOS.

## Benchmark Types

### In-Repo Benchmark

For benchmarks where source code lives in the DevBench repository:

```
benchmarks/
└── my-benchmark/
    ├── benchmark.json
    └── src/
        ├── Program.cs
        └── my-benchmark.csproj
```

```json
{
  "name": "My Benchmark",
  "type": "in-repo",
  "build": {
    "clean": { "command": "dotnet clean src/my-benchmark.csproj" },
    "full": { "command": "dotnet build src/my-benchmark.csproj" }
  }
}
```

### External-Repo Benchmark

For benchmarks that clone another repository:

```json
{
  "name": "External Project",
  "type": "external-repo",
  "repoUrl": "https://github.com/owner/repo",
  "repoRef": "v1.0.0",
  "workingDirectory": "./src/App",
  "build": {
    "clean": { "command": "dotnet clean" },
    "full": { "command": "dotnet build" }
  }
}
```

The repository is cloned to `.cache/<repo-name>/` with a shallow clone (`--depth 1`).

## Results Schema

When benchmarks run, results are saved in this format:

```json
{
  "submissionId": "uuid",
  "submitter": "from git config user.name",
  "timestamp": "2026-01-21T12:00:00Z",
  "machine": {
    "os": { "platform": "Windows", "version": "...", "architecture": "X64" },
    "cpu": { "model": "...", "cores": 8 },
    "memory": { "capacityGB": 32 },
    "storage": { "type": "SSD", "fileSystem": "ReFS", "isDevDrive": true, "freeSpaceGB": 100 },
    "toolchains": [{ "name": ".NET SDK", "versions": ["10.0.100"] }],
    "platformSpecific": { "WindowsBuild": 26200 }
  },
  "benchmarks": [
    {
      "name": "Benchmark Name",
      "coldRun": { "durationMs": 5000, "success": true },
      "warmRuns": [
        { "durationMs": 2000, "success": true },
        { "durationMs": 1900, "success": true }
      ],
      "warmRunStats": {
        "minMs": 1900,
        "maxMs": 2000,
        "meanMs": 1950,
        "medianMs": 1950,
        "stdDevMs": 50
      },
      "incrementalBuild": { "durationMs": 500, "success": true }
    }
  ]
}
```

On Windows, `memory.capacityGB` is the sum of installed physical memory module
capacities in GiB (bytes divided by 1024 cubed), including hardware-reserved RAM,
not just the memory available to the OS.

`storage.isDevDrive` indicates whether the volume containing the working directory
has Windows' `PERSISTENT_VOLUME_STATE_DEV_VOLUME` flag. ReFS alone does not imply a
Dev Drive. This query does not require administrator privileges and is independent
of whether the Dev Drive is trusted.

Windows metadata queries run PowerShell without profiles or interactive prompts.
Collection and parsing failures emit warnings to standard error rather than
silently hiding missing information.

To check a built harness against Windows' CPU and installed RAM values, run
`tests\Test-WindowsSystemInfo.ps1 -HarnessPath <path-to-DevBench.exe> -ExpectedDevDrive $true`
from a known Dev Drive in PowerShell 7. Use `$false` from a regular volume.
