// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Tier 2 and 3: the emitted MSL must compile with the OS runtime Metal compiler, and
/// the executable fixtures must produce bit-exact results on the GPU, including EXEC
/// masking and dispatcher control flow. These tests no-op (with a note) on hosts
/// without a Metal device so the suite stays green on Windows/Linux CI; the golden
/// and structural tiers still run everywhere.
/// </summary>
public sealed class MetalRuntimeTests(ITestOutputHelper output)
{
    [Fact]
    public void AllFixturesCompileWithTheRuntimeMetalCompiler()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileOrThrow(fixture);
            Assert.True(
                MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
                $"[{fixture.Name}] Metal rejected the emitted MSL: {error}\n{shader.Source}");
        }
    }

    [Fact]
    public void ExecStoreProgramExecutesWithExecMasking()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; execution test skipped.");
            return;
        }

        // Sentinel-filled buffer: any dword the program does not store must survive.
        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[64];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var result = ExecuteOrThrow(Gen5ComputeFixtures.ExecStore, buffer);

        // Reference results computed with the same semantics the program encodes.
        var fmac = BitConverter.SingleToUInt32Bits(MathF.FusedMultiplyAdd(1.5f, 2.25f, 10.0f));
        var mulHiSigned = (uint)(((long)0x7FFFFFFF * 0x00010003) >> 32);
        var mulLoSigned = unchecked(0x7FFFFFFFu * 0x00010003u);
        var movBits = BitConverter.SingleToUInt32Bits(1.5f);

        Assert.Equal(fmac, ReadDword(result, 0));
        Assert.Equal(mulHiSigned, ReadDword(result, 4));
        Assert.Equal(mulLoSigned, ReadDword(result, 8));
        Assert.Equal(Sentinel, ReadDword(result, 12)); // EXEC=0: the store must not land.
        Assert.Equal(movBits, ReadDword(result, 16));
        for (var offset = 20; offset < result.Length; offset += sizeof(uint))
        {
            Assert.Equal(Sentinel, ReadDword(result, offset));
        }
    }

    [Fact]
    public void LoopProgramIteratesThroughTheDispatcher()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; execution test skipped.");
            return;
        }

        var result = ExecuteOrThrow(Gen5ComputeFixtures.Loop, new byte[16]);

        // 5 + 4 + 3 + 2 + 1, accumulated across five dispatcher round trips.
        Assert.Equal(15u, ReadDword(result, 0));
    }

    [Fact]
    public void PixelShaderCompilesWithTheRuntimeMetalCompiler()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"[pixel] Metal rejected the emitted MSL: {error}\n{shader.Source}");
    }

    [Fact]
    public void VertexShaderCompilesWithTheRuntimeMetalCompiler()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        var shader = Gen5ComputeFixtures.CompileVertexOrThrow(requiredVertexOutputCount: 2);
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"[vertex] Metal rejected the emitted MSL: {error}\n{shader.Source}");
    }

    [Fact]
    public void FixedShadersCompileWithTheRuntimeMetalCompiler()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        var sources = new (string Name, string Source)[]
        {
            ("fullscreen", MslFixedShaders.CreateFullscreenVertex(3)),
            ("copy", MslFixedShaders.CreateCopyFragment()),
            ("present", MslFixedShaders.CreatePresentFragment()),
            ("solid", MslFixedShaders.CreateSolidFragment(0.25f, 0.5f, 0.75f, 1f)),
            ("attribute", MslFixedShaders.CreateAttributeFragment(1)),
            ("depth-only", MslFixedShaders.CreateDepthOnlyFragment()),
        };
        foreach (var (name, source) in sources)
        {
            Assert.True(
                MetalNative.TryCompileLibrary(source, out _, out var error),
                $"[{name}] Metal rejected the fixed shader: {error}\n{source}");
        }
    }

    [Fact]
    public void LdsRoundTripExecutesThroughThreadgroupMemory()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; execution test skipped.");
            return;
        }

        var result = ExecuteOrThrow(Gen5ComputeFixtures.Lds, new byte[16]);

        // Written to LDS, barriered, read back, stored to the buffer.
        Assert.Equal(0x1234u, ReadDword(result, 0));
    }

    [Fact]
    public void Wave64CrossLaneEmitsValidMsl()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; compile validation skipped.");
            return;
        }

        // A wave64 program with a cross-lane op emits the threadgroup-scratch
        // bridge and its barriers; the runtime Metal compiler must accept it.
        var shader = Gen5ComputeFixtures.CompileOrThrow(
            Gen5ComputeFixtures.Wave64Broadcast, waveLaneCount: 64, localSizeX: 64);
        Assert.Contains("sharpemu_wave_scratch", shader.Source);
        Assert.Contains("threadgroup_barrier", shader.Source);
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"Metal rejected the wave64 MSL: {error}\n{shader.Source}");
    }

    [Fact]
    public void Wave64BroadcastExecutesAcrossBothHalvesWithoutDeadlock()
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; execution test skipped.");
            return;
        }

        // 64 threads = one guest wave (two 32-wide simdgroups). The read-first-
        // lane bridge takes a threadgroup_barrier reached by all 64 lanes; if
        // the two halves did not rendezvous this would deadlock (the command
        // buffer would never complete). Every lane holds 42, so the broadcast
        // result is 42 — proving the bridge runs to completion and returns the
        // published value.
        var shader = Gen5ComputeFixtures.CompileOrThrow(
            Gen5ComputeFixtures.Wave64Broadcast, waveLaneCount: 64, localSizeX: 64);
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError),
            $"{compileError}\n{shader.Source}");

        var buffer = new byte[16];
        var uniforms = new byte[16 + sizeof(uint)];
        // Dispatch limit 64: all 64 lanes active (see WriteDispatchLimit contract).
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(0), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(16), (uint)buffer.Length);

        Assert.True(
            MetalNative.TryExecuteThreadgroup(
                library,
                shader.EntryPoint,
                buffer,
                uniforms,
                dataIndex: 0,
                uniformsIndex: (nuint)shader.GlobalMemoryBindings.Count,
                threadsPerThreadgroup: 64,
                out var result,
                out var runError),
            runError);

        Assert.Equal(42u, ReadDword(result, 0));
    }

    private static byte[] ExecuteOrThrow(Gen5ComputeFixture fixture, byte[] buffer)
    {
        var shader = Gen5ComputeFixtures.CompileOrThrow(fixture);
        if (!MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] {compileError}\n{shader.Source}");
        }

        // Per the translator contract, global buffers occupy indices
        // 0..count-1 and SharpEmuUniforms sits at index count; the executable
        // fixtures bind exactly one data buffer.
        var bufferCount = shader.GlobalMemoryBindings.Count;
        Assert.Equal(1, bufferCount);

        // SharpEmuUniforms: dispatch limit (one thread), reserved, then the
        // byte length of each bound buffer (the struct's array never has fewer
        // than one entry).
        var uniforms = new byte[16 + (Math.Max(bufferCount, 1) * sizeof(uint))];
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(uniforms.AsSpan(16), (uint)buffer.Length);

        if (!MetalNative.TryExecuteSingleThread(
                library,
                shader.EntryPoint,
                buffer,
                uniforms,
                dataIndex: 0,
                uniformsIndex: (nuint)bufferCount,
                out var result,
                out var runError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] {runError}");
        }

        return result;
    }

    private static uint ReadDword(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
}
