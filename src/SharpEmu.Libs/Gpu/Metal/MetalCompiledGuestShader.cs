// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// The Metal backend's compiled shader: MSL source plus the reflection data
/// (<see cref="Gen5MslShader"/>) the presenter needs to create and bind pipeline
/// states. The diagnostics payload is the source text — Metal has no portable
/// binary form until an MTLBinaryArchive is introduced.
/// </summary>
internal sealed class MetalCompiledGuestShader(Gen5MslShader shader) : IGuestCompiledShader
{
    private byte[]? _payload;

    public Gen5MslShader Shader { get; } = shader;

    /// <summary>MTLLibrary handle cached by the presenter after the first
    /// runtime compile; the render loop is its only reader and writer.</summary>
    internal nint CachedLibrary;

    public byte[] Payload => _payload ??= Encoding.UTF8.GetBytes(Shader.Source);

    public string PayloadFileExtension => "msl";
}
