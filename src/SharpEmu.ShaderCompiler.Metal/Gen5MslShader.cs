// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

// MSL-specific shader artifact types. These stay beside the MSL emitter (not in the
// backend-neutral SharpEmu.ShaderCompiler project): each codegen owns its own
// compiled-shader shape, mirroring Gen5SpirvShader on the Vulkan side.
public enum Gen5MslStage
{
    Vertex,
    Pixel,
    Compute,
}

/// <summary>
/// A translated Metal shader: MSL source text plus the reflection data the Metal
/// backend needs to bind it. Buffer argument indices follow the translation
/// contract documented on <see cref="Gen5MslTranslator"/>: global memory buffers
/// occupy [[buffer(globalBufferBase + i)]] in <see cref="GlobalMemoryBindings"/>
/// order, and compute shaders reserve one trailing slot for the dispatch-limit
/// uniform. Unlike SPIR-V, Metal fixes the threadgroup size at dispatch time, so
/// the size the shader was translated for is carried here.
/// </summary>
/// <remarks>
/// <para><see cref="UniformsBufferIndex"/> is the [[buffer(N)]] slot this stage's
/// SharpEmuUniforms argument was emitted at (globalBufferBase +
/// totalGlobalBufferCount, both translation-time inputs). Stages sharing a draw
/// can disagree — a vertex stage whose guest buffers sit after the pixel
/// stage's has a higher base — so the presenter must bind the uniforms buffer
/// per stage at this exact index rather than assuming one shared slot.</para>
/// <para>Texture slots are global across a draw's stages ([[texture(
/// ImageBindingBase + i)]]). Samplers live in a per-stage argument buffer
/// bound at <see cref="SamplerArgBufferIndex"/> (Metal caps direct
/// [[sampler(N)]] slots at 16 per stage, but shaders sample more), holding one
/// sampler per sampled image. <see cref="SamplerSlots"/> maps this stage's
/// image binding index to its [[id(N)]] entry in that argument buffer, -1 for
/// storage images that take none; <see cref="SamplerCount"/> is the entry
/// count.</para>
/// </remarks>
public sealed record Gen5MslShader(
    string Source,
    string EntryPoint,
    Gen5MslStage Stage,
    IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
    IReadOnlyList<Gen5ImageBinding> ImageBindings,
    uint AttributeCount,
    IReadOnlyList<Gen5VertexInputBinding> VertexInputs,
    uint ThreadgroupSizeX = 1,
    uint ThreadgroupSizeY = 1,
    uint ThreadgroupSizeZ = 1,
    int UniformsBufferIndex = -1,
    int ImageBindingBase = 0,
    IReadOnlyList<int>? SamplerSlots = null,
    int SamplerCount = 0,
    int SamplerArgBufferIndex = -1);
