#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/LogsDigger/LogsDigger.fsproj"
CONFIGURATION="Release"
RUNTIME_IDS=()
OUTPUT_DIR="$ROOT_DIR/artifacts/publish/logs-digger"
EXTRA_ARGS=()

usage() {
  cat <<EOF
Usage: $(basename "$0") [options]

Publishes Logs Digger as a NativeAOT executable for the host platform.

Options:
  --rid <runtime>         Runtime identifier to publish for (repeatable; must match the host OS)
  --output <dir>          Output directory (default: artifacts/publish/logs-digger)
  --configuration <name>  Build configuration (default: Release)
  -p:<Name>=<value>       Extra MSBuild property passed to dotnet publish (repeatable)
  -h, --help              Show this help

Examples:
  $(basename "$0") --rid linux-x64
  $(basename "$0") --rid win-x64 --output /tmp/logs-digger -p:Version=0.1.0
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      if [[ -z "${2:-}" ]]; then
        echo "Missing runtime identifier after --rid." >&2
        exit 1
      fi
      RUNTIME_IDS+=("$2")
      shift 2
      ;;
    --output)
      OUTPUT_DIR="${2:-}"
      shift 2
      ;;
    --configuration)
      CONFIGURATION="${2:-}"
      shift 2
      ;;
    -p:*)
      EXTRA_ARGS+=("$1")
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

host_rid() {
  local arch
  arch="$(uname -m)"
  case "$(uname -s)" in
    Linux) [[ "$arch" == "aarch64" ]] && echo "linux-arm64" || echo "linux-x64" ;;
    Darwin) [[ "$arch" == "arm64" ]] && echo "osx-arm64" || echo "osx-x64" ;;
    *) echo "win-x64" ;;
  esac
}

if [[ "${#RUNTIME_IDS[@]}" -eq 0 ]]; then
  RUNTIME_IDS=("$(host_rid)")
fi

# NativeAOT links with the host's native toolchain, so it cannot cross-compile between operating systems.
for rid in "${RUNTIME_IDS[@]}"; do
  case "$(uname -s):$rid" in
    Linux:linux-*|Darwin:osx-*|MINGW*:win-*|MSYS*:win-*|CYGWIN*:win-*) ;;
    *)
      echo "NativeAOT cannot publish $rid on $(uname -s). Publish on a matching host." >&2
      exit 1
      ;;
  esac
done

# On Windows the native linker is found through vswhere, which Visual Studio installs but does not put on PATH.
VSWHERE_DIR="/c/Program Files (x86)/Microsoft Visual Studio/Installer"
if [[ -d "$VSWHERE_DIR" ]] && ! command -v vswhere.exe >/dev/null 2>&1; then
  export PATH="$PATH:$VSWHERE_DIR"
fi

publish_one() {
  local rid="$1"
  local output_dir="$2"
  local publish_args=(
    dotnet publish "$PROJECT"
    -c "$CONFIGURATION"
    -r "$rid"
    -o "$output_dir"
    -p:PublishAot=true
  )

  if [[ "${#EXTRA_ARGS[@]}" -gt 0 ]]; then
    publish_args+=("${EXTRA_ARGS[@]}")
  fi

  mkdir -p "$output_dir"
  echo "Publishing Logs Digger for $rid to $output_dir"
  "${publish_args[@]}"
}

for rid in "${RUNTIME_IDS[@]}"; do
  if [[ "${#RUNTIME_IDS[@]}" -eq 1 ]]; then
    publish_one "$rid" "$OUTPUT_DIR"
  else
    publish_one "$rid" "$OUTPUT_DIR/$rid"
  fi
done
