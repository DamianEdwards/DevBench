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

download_harness() {
    local release="$1"
    local asset_name="$HARNESS_NAME-$RID$EXE_EXT"
    local download_url=$(echo "$release" | grep -o "\"browser_download_url\": \"[^\"]*$asset_name\"" | cut -d'"' -f4)
    
    if [ -z "$download_url" ]; then
        echo "No pre-built binary for $RID"
        return 1
    fi
    
    mkdir -p "$CACHE_DIR"
    
    echo "Downloading $asset_name..."
    curl -L -o "$HARNESS_PATH" "$download_url"
    chmod +x "$HARNESS_PATH"
    
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
    
    mkdir -p "$CACHE_DIR"
    
    # Build native AOT
    cd "$SCRIPT_DIR"
    dotnet publish "$SRC_FILE" --output "$CACHE_DIR" --runtime "$RID"
    
    # Find and rename the built executable
    if [ -f "$CACHE_DIR/$HARNESS_NAME$EXE_EXT" ]; then
        mv "$CACHE_DIR/$HARNESS_NAME$EXE_EXT" "$HARNESS_PATH"
    fi
}

# Main logic
NEEDS_BUILD=false

if [ "$FORCE" = true ] || [ "$LOCAL_BUILD" = true ] || [ ! -f "$HARNESS_PATH" ]; then
    NEEDS_BUILD=true
fi

if [ "$NEEDS_BUILD" = true ]; then
    if [ "$LOCAL_BUILD" != true ]; then
        release=$(get_latest_release)
        if [ -n "$release" ] && echo "$release" | grep -q '"tag_name"'; then
            if download_harness "$release"; then
                NEEDS_BUILD=false
            fi
        fi
    fi
    
    if [ "$NEEDS_BUILD" = true ]; then
        build_local_harness
    fi
fi

# Run the harness
if [ ! -f "$HARNESS_PATH" ]; then
    echo "Error: Harness not found at $HARNESS_PATH"
    exit 1
fi

echo "Running DevBench..."
exec "$HARNESS_PATH" "${HARNESS_ARGS[@]}"
