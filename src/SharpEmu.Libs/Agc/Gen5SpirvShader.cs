// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// SPIR-V-specific shader artifact types. These stay beside the SPIR-V emitter (not in
// the backend-neutral SharpEmu.ShaderCompiler project): each codegen owns its own
// compiled-shader shape.
internal enum Gen5SpirvStage
{
    Vertex,
    Pixel,
    Compute,
}

internal sealed record Gen5SpirvShader(
    byte[] Spirv,
    IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
    IReadOnlyList<Gen5ImageBinding> ImageBindings,
    uint AttributeCount,
    IReadOnlyList<Gen5VertexInputBinding> VertexInputs);
