#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: $0 <spirv-val> <expected-version> <target-env> <module-directory>" >&2
  exit 2
fi

validator=$1
expected_version=$2
target_env=$3
module_directory=$4

if [ ! -x "$validator" ]; then
  echo "SPIR-V validator is not executable: $validator" >&2
  exit 2
fi

if [ ! -d "$module_directory" ]; then
  echo "SPIR-V module directory does not exist: $module_directory" >&2
  exit 2
fi

validator_version="$("$validator" --version | head -n 1)"
if [[ "$validator_version" != *"SPIRV-Tools $expected_version"* ]]; then
  echo "unexpected SPIRV-Tools version: $validator_version (expected $expected_version)" >&2
  exit 2
fi

echo "Validator: $validator_version"
echo "Target environment: $target_env"

mapfile -d '' modules < <(find "$module_directory" -type f -name '*.spv' -print0 | sort -z)
if [ "${#modules[@]}" -eq 0 ]; then
  echo "no SPIR-V modules found in $module_directory" >&2
  exit 1
fi

failures=0
for module in "${modules[@]}"; do
  echo "Validating module: $module"
  if ! "$validator" --target-env "$target_env" "$module"; then
    echo "SPIR-V validation failed: $module" >&2
    failures=1
  fi
done

if [ "$failures" -ne 0 ]; then
  exit 1
fi

echo "Validated ${#modules[@]} synthetic SPIR-V modules."
