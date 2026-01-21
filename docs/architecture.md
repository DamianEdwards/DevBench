# DevBench Architecture

## Overview

DevBench is a developer PC benchmarking tool that measures build performance for .NET projects.

```mermaid
graph TB
    subgraph "User's Machine"
        Script[run-benchmarks.ps1/.sh]
        Harness[DevBench Native Binary]
        Benchmarks[benchmarks/*/benchmark.json]
        Cache[.cache/ - Cloned Repos]
        Results[results/*.json]
    end
    
    subgraph "GitHub"
        Repo[DevBench Repository]
        Releases[GitHub Releases]
        Pages[GitHub Pages]
    end
    
    Script -->|Downloads| Releases
    Script -->|Runs| Harness
    Harness -->|Reads| Benchmarks
    Harness -->|Clones to| Cache
    Harness -->|Writes| Results
    Results -->|PR to| Repo
    Repo -->|Builds| Pages
```

## Components

### Bootstrap Scripts (`run-benchmarks.ps1`, `run-benchmarks.sh`)

Thin wrappers that:
1. Detect OS and architecture
2. Check for pre-built harness in GitHub Releases
3. Download native binary OR build locally (dev mode)
4. Execute harness with forwarded arguments

### Test Harness (`src/DevBench.cs`)

File-based .NET 10 app compiled to native AOT. Responsibilities:
- Parse command-line arguments
- Display interactive benchmark selection menu
- Collect system information
- Read benchmark manifests
- Execute benchmark phases (prereqs → restore → build)
- Measure and record timings
- Generate results JSON

### Benchmark Manifests (`benchmarks/*/benchmark.json`)

JSON files defining:
- Benchmark metadata (name, description, tags)
- Type (in-repo or external-repo)
- Prerequisites to check
- Commands for restore, cache clear, build
- Iteration counts and timeouts

### Results (`results/*.json`)

User-submitted benchmark results containing:
- System information (OS, CPU, memory, storage)
- Benchmark timings (cold, warm, incremental)
- Statistics (min, max, mean, median, stddev)

### Website (`site/`)

Eleventy static site that:
- Reads all results JSON files
- Displays filterable/sortable results table
- Deployed to GitHub Pages

## Data Flow

```mermaid
sequenceDiagram
    participant User
    participant Script
    participant Harness
    participant Benchmark
    participant Results
    
    User->>Script: ./run-benchmarks.ps1
    Script->>Script: Download harness (or build)
    Script->>Harness: Execute
    Harness->>Harness: Collect system info
    Harness->>User: Show benchmark menu
    User->>Harness: Select benchmarks
    
    loop Each Benchmark
        Harness->>Benchmark: Read benchmark.json
        Harness->>Harness: Check prerequisites
        Harness->>Benchmark: Run restore
        Harness->>Benchmark: Clear cache
        Harness->>Benchmark: Cold build (timed)
        loop Warm iterations
            Harness->>Benchmark: Warm build (timed)
        end
        opt Incremental
            Harness->>Benchmark: Touch file
            Harness->>Benchmark: Incremental build (timed)
        end
    end
    
    Harness->>Results: Write results JSON
    Harness->>User: Display summary
```

## Benchmark Execution

Each benchmark goes through these phases:

1. **Prerequisites Check** - Verify required tools installed
2. **Clone** (external repos only) - Shallow clone to `.cache/`
3. **Restore** - Download dependencies (not timed)
4. **Cache Clear** - Clean build artifacts
5. **Cold Build** - First build, timed
6. **Warm Builds** - Multiple iterations, timed
7. **Incremental Build** - Touch file, rebuild, timed

## Extension Points

### Adding New Benchmarks
- Create `benchmarks/<name>/benchmark.json`
- For in-repo: add source to `benchmarks/<name>/src/`
- For external: specify `repoUrl` and `repoRef`

### Adding New Stacks (Future)
The schema supports non-.NET benchmarks:
- Different `prerequisites` (node, cargo, go)
- Different `build.full.command` values
- Stack-specific `tags` for filtering

### Adding System Info
- Modify `CollectSystemInfo()` in `DevBench.cs`
- Use platform conditionals (`#if WINDOWS`)
- Update results schema documentation

## Release Process

```mermaid
graph LR
    Push[Push to main] -->|build-harness.yml| Draft[Update Draft Release]
    Draft -->|Manual promote| Release[Published Release]
    Release -->|Bootstrap scripts| Users[User Downloads]
```

1. Every push to `main` triggers `build-harness.yml`
2. Builds native AOT for 6 platforms
3. Updates standing draft release with CI binaries
4. Manual promotion creates versioned release
5. Bootstrap scripts download from latest release
