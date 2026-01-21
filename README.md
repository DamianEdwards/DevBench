# DevBench

Benchmark your developer PC's performance with real-world .NET build scenarios.

## Quick Start

### Windows (PowerShell)
```powershell
git clone https://github.com/damianedwards/DevBench.git
cd DevBench
.\run-benchmarks.ps1
```

### macOS / Linux (Bash)
```bash
git clone https://github.com/damianedwards/DevBench.git
cd DevBench
./run-benchmarks.sh
```

The interactive menu will guide you through selecting which benchmarks to run.

## What It Measures

DevBench measures build performance across different scenarios:

- **Cold builds** - First build after clearing caches
- **Warm builds** - Subsequent builds with warm caches  
- **Incremental builds** - Rebuilding after a small code change

Results capture detailed system information so you can compare performance across different hardware and configurations.

## System Information Collected

- **OS**: Platform, version, architecture
- **CPU**: Model, cores, frequency
- **Memory**: Capacity, speed
- **Storage**: Type (SSD/HDD), model, free space
- **Windows-specific**: Dev Drive status, Windows Defender exclusions
- **macOS-specific**: Chip model (Apple Silicon/Intel), Rosetta 2 status
- **Linux-specific**: Distribution, kernel version

## Submitting Results

After running benchmarks, your results are saved to `results/results-<timestamp>-<hash>.json`.

To share your results:

1. Fork this repository
2. Copy your results file to the `results/` folder
3. Submit a pull request

Your results will appear on the [DevBench Results](https://damianedwards.github.io/DevBench) page after merging.

## Adding New Benchmarks

See [docs/benchmark-schema.md](docs/benchmark-schema.md) for the benchmark manifest format.

Benchmarks can be:
- **In-repo**: Source code included in the `benchmarks/` folder
- **External-repo**: Points to another GitHub repository

## Project Structure

```
DevBench/
├── run-benchmarks.ps1      # Windows bootstrapper
├── run-benchmarks.sh       # Unix bootstrapper
├── src/
│   └── DevBench.cs         # Test harness (file-based .NET 10 app)
├── benchmarks/             # Benchmark definitions
│   └── dotnet-hello-world/
│       ├── benchmark.json
│       └── src/
├── results/                # Submitted benchmark results
├── site/                   # Results website (Eleventy)
└── docs/                   # Documentation
```

## Requirements

- **To run benchmarks**: Just clone and run the script (downloads pre-built harness)
- **To develop the harness**: .NET 10 SDK

## License

MIT License - see [LICENSE](LICENSE)
