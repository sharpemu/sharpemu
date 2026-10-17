// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// A program whose descriptors cannot be planned. The message names the shader hash,
// the stage and the program counter.
public sealed class ResourcePlanException(string message) : Exception(message);
