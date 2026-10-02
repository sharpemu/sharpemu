<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# pipebench

Times `vkCreateComputePipelines` on dumped SPIR-V, without running a game.

1. Dump the shaders: run with `SHARPEMU_DUMP_SPIRV=1` and
   `SHARPEMU_DUMP_SPIRV_HASH=<hash,...>`. The dumps land in `win-x64\shader-dumps\`.
   The slowest hashes are in the `[PERF] vkCreate*Pipelines ms=` log lines.
2. Write the reflection JSON next to each module:
   `spirv-cross <file.spv> --reflect --output <file.spv>.json`
3. Run the benchmark:
   `dotnet run -c Release -- samples\ffd.spv samples\571.spv`

Each module gets a random generator word, so the NVIDIA disk cache never hits and every timing
is a cold compile.

Reference numbers on an RTX 5070, 2026-09-29 (Yōtei compute shaders):

| Sample | Compile time | Notes |
|---|---|---|
| `571.spv` | ~0.67 s | |
| `f19.spv` | ~2.4 s | |
| `ffd.spv` | ~5.8 s | 5 576 blocks, 180k instructions |
| `ffd_O.spv` | ~5.3–5.5 s | `spirv-opt -O` output: registers promoted, 66k instructions, same blocks |

Promoting the register arrays saves only about 10 %. The cost is the PC dispatcher's block
structure, so structured control flow is the real fix.
