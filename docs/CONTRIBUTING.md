# Contributing to DevBench

Thank you for your interest in contributing to DevBench! This document provides guidelines and instructions for contributing.

## Ways to Contribute

### 1. Submit Your Benchmark Results

The easiest way to contribute is to run benchmarks on your machine and submit the results:

1. Clone the repository and run benchmarks:
   ```bash
   git clone https://github.com/damianedwards/DevBench.git
   cd DevBench
   ./run-benchmarks.sh  # or .\run-benchmarks.ps1 on Windows
   ```

2. Fork this repository on GitHub

3. Copy your results file from `results/` to your fork

4. Submit a pull request

### 2. Add New Benchmarks

We welcome new benchmarks! See [.github/prompts/add-benchmark.prompt.md](.github/prompts/add-benchmark.prompt.md) for detailed instructions.

**Guidelines for new benchmarks:**
- Should measure real-world build scenarios
- Must include a `benchmark.json` manifest
- For external repos, pin to a specific version/tag
- Test locally before submitting

### 3. Improve the Harness

The test harness is in `src/DevBench.cs`. Contributions could include:
- Better system information collection
- Performance improvements
- Bug fixes
- New features

### 4. Improve Documentation

Documentation improvements are always welcome:
- README.md
- docs/benchmark-schema.md
- docs/architecture.md
- Agent prompts in .github/prompts/

## Development Setup

### Prerequisites

- .NET 10 SDK
- Node.js 20+ (for the website)
- Git

### Building the Harness Locally

```powershell
# Run directly
dotnet run src/DevBench.cs

# Build native AOT
dotnet publish src/DevBench.cs

# Output is in src/artifacts/
```

### Running the Website Locally

```powershell
cd site
npm install
npm run dev
```

## Code Style

### C# (DevBench.cs)
- Use file-scoped namespaces
- Prefer `var` for obvious types
- Use pattern matching where appropriate
- Keep the single-file structure

### JSON (benchmark.json)
- Use camelCase for property names
- Include descriptions for new benchmarks
- Pin external repos to specific versions

### Markdown
- Use ATX-style headers (`#`)
- Include code blocks with language hints
- Keep lines under 120 characters

## Pull Request Process

1. **Fork** the repository
2. **Create a branch** for your changes
3. **Make your changes** with clear commit messages
4. **Test locally** to ensure nothing is broken
5. **Submit a PR** with a clear description

### PR Checklist

- [ ] Tested locally
- [ ] Documentation updated (if applicable)
- [ ] Commit messages are clear
- [ ] No unrelated changes included

## Reporting Issues

Use GitHub Issues to report:
- Bugs in the harness
- Problems with benchmarks
- Feature requests
- Documentation improvements

Include:
- OS and version
- .NET SDK version
- Steps to reproduce
- Expected vs actual behavior

## Code of Conduct

Be respectful and constructive. We're all here to improve developer tooling.

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
