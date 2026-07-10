#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Downloads the official (universal x86_64+arm64) MoltenVK dylib and stages
# it next to a SharpEmu build as libvulkan.1.dylib. The macOS build runs as
# an x86-64 process under Rosetta 2, so Homebrew's arm64-only Vulkan
# libraries cannot be used; the presenter looks for this app-local copy.
#
# Usage: scripts/fetch-macos-moltenvk.sh [output-dir]
#        (default output: artifacts/bin/Debug/net10.0/osx-x64)
set -euo pipefail

MVK_VERSION="${MVK_VERSION:-v1.4.0}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-$REPO_ROOT/artifacts/bin/Debug/net10.0/osx-x64}"

if [[ ! -d "$OUT_DIR" ]]; then
  echo "output directory does not exist: $OUT_DIR (build first?)" >&2
  exit 2
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo ">> Downloading MoltenVK $MVK_VERSION..."
curl -sL -o "$WORK_DIR/mvk.tar" \
  "https://github.com/KhronosGroup/MoltenVK/releases/download/$MVK_VERSION/MoltenVK-macos.tar"
tar -xf "$WORK_DIR/mvk.tar" -C "$WORK_DIR" \
  MoltenVK/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib

DYLIB="$WORK_DIR/MoltenVK/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib"
file "$DYLIB" | grep -q x86_64 || { echo "downloaded dylib lacks x86_64 slice" >&2; exit 3; }

cp "$DYLIB" "$OUT_DIR/libMoltenVK.dylib"
cp "$DYLIB" "$OUT_DIR/libvulkan.1.dylib"
echo ">> Staged libMoltenVK.dylib + libvulkan.1.dylib in $OUT_DIR"
