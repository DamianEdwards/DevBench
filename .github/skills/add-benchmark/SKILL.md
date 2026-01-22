---
name: add-benchmark
description: Guide for adding a new benchmark to DevBench. Use this when asked to add, create, or set up a new benchmark.
---

# Add a New Benchmark

This skill guides you through adding a new benchmark to DevBench.

## Information Needed

1. **Benchmark name**: A short identifier (e.g., `aspire-starter`)
2. **Description**: What this benchmark measures
3. **Type**: `in-repo` (code in this repo) or `external-repo` (clones another repo)
4. **For external repos**: Repository URL and ref (tag/branch/commit)

## Steps

### 1. Create the benchmark folder

```
benchmarks/<benchmark-name>/
├── benchmark.json
└── src/              # Only for in-repo benchmarks
    └── ...
```

### 2. Create benchmark.json

Use this template:

```json
{
  "name": "Benchmark Display Name",
  "description": "What this benchmark measures",
  "type": "in-repo",
  "tags": ["dotnet"],
  "prerequisites": [
    { "command": "dotnet --version", "minVersion": "10.0" }
  ],
  "restore": {
    "command": "dotnet restore",
    "timeout": 300
  },
  "clearCache": {
    "command": "dotnet clean",
    "additionalPaths": ["bin", "obj"]
  },
  "preBuild": [
    "dotnet build-server shutdown"
  ],
  "build": {
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

**Important for .NET benchmarks:**
- Always use `--no-restore` in build commands. The restore phase runs separately and is not timed.
- Always include `"preBuild": ["dotnet build-server shutdown"]` to ensure build servers are stopped before timing. This ensures cold builds are truly cold.

### For external-repo benchmarks

```json
{
  "name": "Aspire Starter",
  "description": "Build the .NET Aspire starter template",
  "type": "external-repo",
  "repoUrl": "https://github.com/dotnet/aspire",
  "repoRef": "v9.0.0",
  "workingDirectory": "./samples/AspireStarterApp",
  "tags": ["dotnet", "aspire", "web"],
  ...
}
```

### 3. For in-repo benchmarks, add source code

Create the project files in `benchmarks/<name>/src/`.

### 4. Test locally

```powershell
dotnet run src/DevBench.cs -- --benchmark <benchmark-name>
```

### 5. Verify the benchmark appears in the menu

```powershell
dotnet run src/DevBench.cs
```

## Checklist

- [ ] `benchmark.json` created with all required fields
- [ ] For in-repo: source code added to `src/`
- [ ] For external-repo: `repoUrl` and `repoRef` specified
- [ ] Prerequisites list all required tools
- [ ] Tested locally with `dotnet run src/DevBench.cs`
- [ ] Benchmark appears in interactive menu

## Schema Reference

See [docs/benchmark-schema.md](../../../docs/benchmark-schema.md) for full schema documentation.
