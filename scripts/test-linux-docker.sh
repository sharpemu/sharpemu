#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Smoke-tests the linux-x64 build inside an amd64 container. Useful from any
# host (including Apple Silicon, where Docker runs the amd64 image under
# emulation) to confirm the cross-platform layer keeps working on Linux.
#
# Usage: scripts/test-linux-docker.sh /path/to/eboot.bin
set -euo pipefail

GAME_PATH="${1:-}"
if [[ -z "$GAME_PATH" || ! -f "$GAME_PATH" ]]; then
  echo "usage: $0 <path-to-eboot.bin>" >&2
  exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_DIR="$(cd "$(dirname "$GAME_PATH")" && pwd)"
GAME_FILE="$(basename "$GAME_PATH")"
PUBLISH_DIR="$REPO_ROOT/artifacts/publish/SharpEmu.CLI/Debug/net10.0/linux-x64"

echo ">> Publishing linux-x64 self-contained build..."
dotnet publish "$REPO_ROOT/src/SharpEmu.CLI" \
  -c Debug -r linux-x64 --self-contained -p:PublishSingleFile=false

echo ">> Running inside linux/amd64 container..."
docker run --rm --platform linux/amd64 \
  -v "$PUBLISH_DIR":/app:ro \
  -v "$GAME_DIR":/game:ro \
  mcr.microsoft.com/dotnet/runtime-deps:10.0 \
  /app/SharpEmu --log-level=info "/game/$GAME_FILE"
