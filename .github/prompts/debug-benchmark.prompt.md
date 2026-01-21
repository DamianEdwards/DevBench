# Debug a Benchmark

This prompt helps diagnose and fix benchmark failures.

## Common Issues

### 1. Benchmark not appearing in menu

**Check:**
- `benchmarks/<name>/benchmark.json` exists
- JSON is valid (no syntax errors)
- Required fields present: `name`, `description`, `type`, `build.full`

**Test JSON validity:**
```powershell
Get-Content benchmarks/<name>/benchmark.json | ConvertFrom-Json
```

### 2. Prerequisites check failing

**Symptoms:** Benchmark skipped with "prerequisites not met" warning

**Debug:**
```powershell
# Check what version is installed
dotnet --version
node --version

# Compare with benchmark.json prerequisites
```

**Fix:** Update `minVersion` in benchmark.json or install required tool version.

### 3. Restore phase failing

**Symptoms:** "Restore failed" error

**Debug:**
```powershell
cd benchmarks/<name>/src  # or workingDirectory for external repos
dotnet restore --verbosity detailed
```

**Common causes:**
- Network issues
- Missing NuGet sources
- Incompatible package versions

### 4. Build phase failing

**Symptoms:** "Build failed" with exit code

**Debug:**
```powershell
# Run the exact command from benchmark.json
cd benchmarks/<name>/src
dotnet build -c Release
```

**Common causes:**
- Missing SDK version
- Code compilation errors
- Missing dependencies

### 5. External repo clone failing

**Symptoms:** "Failed to clone repository" error

**Check:**
- `repoUrl` is accessible
- `repoRef` exists (tag/branch/commit)
- Network connectivity

**Debug:**
```powershell
git clone --depth 1 --branch <repoRef> <repoUrl> .cache/test-clone
```

### 6. Timeout errors

**Symptoms:** Benchmark killed after timeout

**Fix:** Increase timeout in benchmark.json:
```json
"build": {
  "full": {
    "command": "dotnet build",
    "timeout": 600  // Increase from 300
  }
}
```

## Running in Verbose Mode

```powershell
dotnet run src/DevBench.cs -- --benchmark <name> --verbose
```

This shows:
- Exact commands being run
- Full command output
- Timing for each phase

## Inspecting External Repo Clone

External repos are cloned to `.cache/<repo-name>/`. To inspect:

```powershell
ls .cache/
cd .cache/<repo-name>
```

To force re-clone:
```powershell
Remove-Item -Recurse .cache/<repo-name>
```

## Getting Help

If the issue persists:
1. Run with `--verbose` flag
2. Check the exact error message
3. Try running the commands manually
4. Check if the benchmark works on a different machine
