#!/bin/bash
# DevBench Bootstrap Script (Bash)
# Downloads the pre-built harness or builds locally if no release exists

set -e

REPO_OWNER="damianedwards"
REPO_NAME="DevBench"
HARNESS_NAME="DevBench"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE_DIR="$SCRIPT_DIR/.devbench"
SRC_FILE="$SCRIPT_DIR/src/DevBench.cs"
VERSION_FILE="$CACHE_DIR/version.txt"

# Parse arguments
FORCE=false
LOCAL_BUILD=false
HARNESS_ARGS=()

while [[ $# -gt 0 ]]; do
    case $1 in
        --force)
            FORCE=true
            shift
            ;;
        --local-build)
            LOCAL_BUILD=true
            shift
            ;;
        *)
            HARNESS_ARGS+=("$1")
            shift
            ;;
    esac
done

# Determine platform
case "$(uname -s)" in
    Darwin*)
        OS="osx"
        ;;
    Linux*)
        OS="linux"
        ;;
    MINGW*|MSYS*|CYGWIN*)
        OS="win"
        ;;
    *)
        echo "Unsupported OS: $(uname -s)"
        exit 1
        ;;
esac

case "$(uname -m)" in
    arm64|aarch64)
        ARCH="arm64"
        ;;
    x86_64|amd64)
        ARCH="x64"
        ;;
    *)
        echo "Unsupported architecture: $(uname -m)"
        exit 1
        ;;
esac

RID="$OS-$ARCH"
EXE_EXT=""
if [ "$OS" = "win" ]; then
    EXE_EXT=".exe"
fi
HARNESS_PATH="$CACHE_DIR/$HARNESS_NAME-$RID$EXE_EXT"

get_latest_release() {
    curl -s "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest" 2>/dev/null
}

get_local_version() {
    if [ -f "$VERSION_FILE" ]; then
        cat "$VERSION_FILE" | tr -d '[:space:]'
    fi
}

# Compare versions: returns 0 if remote > local (needs update)
version_gt() {
    local remote="$1"
    local local_ver="$2"
    
    # Strip 'v' prefix
    remote="${remote#v}"
    local_ver="${local_ver#v}"
    
    # Use sort -V for version comparison
    [ "$(printf '%s\n' "$local_ver" "$remote" | sort -V | tail -1)" = "$remote" ] && [ "$remote" != "$local_ver" ]
}

download_harness() {
    local release="$1"
    local asset_name="$HARNESS_NAME-$RID$EXE_EXT"
    local download_url=$(echo "$release" | grep -o "\"browser_download_url\": \"[^\"]*$asset_name\"" | cut -d'"' -f4)
    
    if [ -z "$download_url" ]; then
        echo "No pre-built binary for $RID"
        return 1
    fi
    
    mkdir -p "$CACHE_DIR"
    
    # Extract version from release
    local version=$(echo "$release" | grep -o '"tag_name": "[^"]*"' | cut -d'"' -f4 | sed 's/^v//')
    
    echo "Downloading $asset_name (v$version)..."
    curl -L -o "$HARNESS_PATH" "$download_url"
    chmod +x "$HARNESS_PATH"
    
    # Save version
    echo "$version" > "$VERSION_FILE"
    
    return 0
}

build_local_harness() {
    echo "Building harness locally..."
    
    # Check for .NET SDK
    if ! command -v dotnet &> /dev/null; then
        echo "Error: .NET SDK not found. Install from https://dot.net"
        exit 1
    fi
    
    local dotnet_version=$(dotnet --version)
    local major_version=$(echo "$dotnet_version" | cut -d'.' -f1)
    
    if [ "$major_version" -lt 10 ]; then
        echo "Error: .NET 10 or later required (found $dotnet_version)"
        exit 1
    fi
    
    # Build and get the assembly path (avoids issues with dotnet run and build servers)
    cd "$SCRIPT_DIR"
    
    # Build and capture target path
    local build_output
    build_output=$(dotnet build "$SRC_FILE" -getProperty:TargetPath 2>&1)
    local build_exit_code=$?
    
    if [ $build_exit_code -ne 0 ]; then
        echo "Build failed:"
        echo "$build_output"
        exit 1
    fi
    
    # The last line should be the target path
    ASSEMBLY_PATH=$(echo "$build_output" | tail -1 | tr -d '[:space:]')
    
    if [ ! -f "$ASSEMBLY_PATH" ]; then
        echo "Error: Built assembly not found at $ASSEMBLY_PATH"
        exit 1
    fi
}

build_native_harness() {
    echo "Building native AOT harness..."
    
    mkdir -p "$CACHE_DIR"
    
    cd "$SCRIPT_DIR"
    dotnet publish "$SRC_FILE" --output "$CACHE_DIR" --runtime "$RID"
    
    # Find and rename the built executable
    if [ -f "$CACHE_DIR/$HARNESS_NAME$EXE_EXT" ]; then
        mv "$CACHE_DIR/$HARNESS_NAME$EXE_EXT" "$HARNESS_PATH"
    fi
}

# Main logic
USE_NATIVE_HARNESS=false
ASSEMBLY_PATH=""

if [ "$LOCAL_BUILD" != true ]; then
    release=$(get_latest_release)
    
    if [ "$FORCE" = true ]; then
        # Force download
        if [ -n "$release" ] && echo "$release" | grep -q '"tag_name"'; then
            if download_harness "$release"; then
                USE_NATIVE_HARNESS=true
            fi
        fi
    elif [ -f "$HARNESS_PATH" ]; then
        # Check if we need to update
        local_version=$(get_local_version)
        if [ -n "$release" ] && echo "$release" | grep -q '"tag_name"' && [ -n "$local_version" ]; then
            remote_version=$(echo "$release" | grep -o '"tag_name": "[^"]*"' | cut -d'"' -f4 | sed 's/^v//')
            if version_gt "$remote_version" "$local_version"; then
                echo "New version available: $remote_version (current: $local_version)"
                if download_harness "$release"; then
                    USE_NATIVE_HARNESS=true
                fi
            else
                echo "Using DevBench v$local_version (up to date)"
                USE_NATIVE_HARNESS=true
            fi
        else
            # No version info, just use existing binary
            USE_NATIVE_HARNESS=true
        fi
    else
        # No local binary, try to download
        if [ -n "$release" ] && echo "$release" | grep -q '"tag_name"'; then
            if download_harness "$release"; then
                USE_NATIVE_HARNESS=true
            fi
        fi
    fi
fi

if [ "$USE_NATIVE_HARNESS" != true ]; then
    # Local dev mode: build to assembly and run with dotnet exec
    # This avoids conflicts with dotnet build-server shutdown in benchmarks
    build_local_harness
fi

# Run the harness
if [ "$USE_NATIVE_HARNESS" = true ]; then
    if [ ! -f "$HARNESS_PATH" ]; then
        echo "Error: Harness not found at $HARNESS_PATH"
        exit 1
    fi
    echo "Running DevBench..."
    exec "$HARNESS_PATH" "${HARNESS_ARGS[@]}"
else
    echo "Running DevBench..."
    exec dotnet exec "$ASSEMBLY_PATH" "${HARNESS_ARGS[@]}"
fi
