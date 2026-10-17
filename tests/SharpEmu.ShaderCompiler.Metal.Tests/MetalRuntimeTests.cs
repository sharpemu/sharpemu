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
        if (SkipWithoutMetalDevice("compile validation")) return;

        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
            Assert.True(
                MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
                $"[{fixture.Name}] Metal rejected the emitted MSL: {error}\n{shader.Source}");
        }
    }

    [Fact]
    public void ExecStoreProgramExecutesWithExecMasking()
    {
        if (SkipWithoutMetalDevice("the execution test")) return;

        // Sentinel-filled buffer: any dword the program does not store must survive.
        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[64];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.ExecStore, buffer);

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
        if (SkipWithoutMetalDevice("the execution test")) return;

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.Loop, new byte[16]);

        // 5 + 4 + 3 + 2 + 1, accumulated across five dispatcher round trips.
        Assert.Equal(15u, ReadDword(result, 0));
    }

    [Fact]
    public void PixelShaderCompilesWithTheRuntimeMetalCompiler()
    {
        if (SkipWithoutMetalDevice("compile validation")) return;

        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"[pixel] Metal rejected the emitted MSL: {error}\n{shader.Source}");
    }

    [Fact]
    public void VertexShaderCompilesWithTheRuntimeMetalCompiler()
    {
        if (SkipWithoutMetalDevice("compile validation")) return;

        var shader = Gen5ComputeFixtures.CompileVertexOrThrow(requiredVertexOutputCount: 2);
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"[vertex] Metal rejected the emitted MSL: {error}\n{shader.Source}");
    }

    [Fact]
    public void FixedShadersCompileWithTheRuntimeMetalCompiler()
    {
        if (SkipWithoutMetalDevice("compile validation")) return;

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
        if (SkipWithoutMetalDevice("the execution test")) return;

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.Lds, new byte[16]);

        // Written to LDS, barriered, read back, stored to the buffer.
        Assert.Equal(0x1234u, ReadDword(result, 0));
    }

    [Fact]
    public void Wave64CrossLaneEmitsValidMsl()
    {
        if (SkipWithoutMetalDevice("compile validation")) return;

        // A wave64 program with a cross-lane op emits the threadgroup-scratch
        // bridge and its barriers; the runtime Metal compiler must accept it.
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(
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
        if (SkipWithoutMetalDevice("the execution test")) return;

        // 64 threads = one guest wave (two 32-wide simdgroups). The read-first-
        // lane bridge takes a threadgroup_barrier reached by all 64 lanes; if
        // the two halves did not rendezvous this would deadlock (the command
        // buffer would never complete). Every lane holds 42, so the broadcast
        // result is 42 — proving the bridge runs to completion and returns the
        // published value.
        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.Wave64Broadcast, new byte[16],
            waveLaneCount: 64, localSizeX: 64, threadsPerThreadgroup: 64);

        Assert.Equal(42u, ReadDword(result, 0));
    }

    [Fact]
    public void TypedLoadFixtureReturnsInstructionFormatComponents()
    {
        if (SkipWithoutMetalDevice("the execution test")) return;

        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[32];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var first = BitConverter.SingleToUInt32Bits(1.5f);
        var second = BitConverter.SingleToUInt32Bits(-2.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), first);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), second);

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.TypedLoad, buffer);

        // The descriptor says unorm bytes; the instruction's 32_32 float format wins.
        Assert.Equal(first, ReadDword(result, 16));
        Assert.Equal(second, ReadDword(result, 20));
        Assert.Equal(Sentinel, ReadDword(result, 8));
        Assert.Equal(Sentinel, ReadDword(result, 12));
        Assert.Equal(Sentinel, ReadDword(result, 24));
        Assert.Equal(Sentinel, ReadDword(result, 28));
    }

    [Fact]
    public void TypedStoreFixtureEncodesComponentsAtTheirFormatOffsets()
    {
        if (SkipWithoutMetalDevice("the execution test")) return;

        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[32];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.TypedStore, buffer);

        // Format 8_8 unorm: 1.0 and 0.5 encode to 0xFF and 0x80 in their own bytes; the rest is untouched.
        Assert.Equal(0xDEAD80FFu, ReadDword(result, 16));
        for (var offset = 0; offset < result.Length; offset += sizeof(uint))
        {
            if (offset != 16) Assert.Equal(Sentinel, ReadDword(result, offset));
        }
    }

    [Theory]
    [InlineData(true, false, 1u, 28u, 0xFACu, true)]
    [InlineData(true, false, 2u, 24u, 0xFACu, true)]
    [InlineData(true, false, 2u, 28u, 0xFACu, false)]
    [InlineData(false, false, 1u, 28u, 0xFACu, true)]
    [InlineData(false, false, 2u, 28u, 4u | (4u << 3), true)]
    [InlineData(false, false, 2u, 28u, 0xFACu, false)]
    [InlineData(true, true, 1u, 28u, 0xFACu, true)]
    [InlineData(true, true, 2u, 24u, 0xFACu, true)]
    [InlineData(true, true, 2u, 28u, 0xFACu, false)]
    public void FormattedAccessBoundsUseOnlyRequiredComponents(
        bool typed, bool store, uint componentCount, uint offset, uint selectors, bool inBounds)
    {
        if (SkipWithoutMetalDevice("the component bounds test")) return;

        var buffer = new byte[32];
        for (var position = 0; position < buffer.Length; position += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(position), 0xDEADBEEF);
        var expected = (byte[])buffer.Clone();
        var words = new List<uint> { 0xBE8B03FF, (77u << 12) | selectors };
        if (store)
        {
            words.AddRange([0x7E0402FF, 0x3F800000, 0x7E0602FF, 0x40000000]);
        }
        var opcode = componentCount - 1 + (store ? 4u : 0u);
        words.Add(typed ? 0xE8000000 | (77u << 19) | (opcode << 16) | offset
            : 0xE0000000 | (opcode << 18) | offset);
        words.Add(0x80020200);
        for (uint component = 0; component < componentCount; component++)
        {
            if (store)
            {
                if (inBounds)
                    BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan((int)(offset + component * 4)),
                        component == 0 ? 0x3F800000u : 0x40000000u);
            }
            else
            {
                words.Add(0xE0700000 | (component * 4));
                words.Add(0x80020000 | ((2 + component) << 8));
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan((int)component * 4), inBounds ? 0xDEADBEEFu : 0u);
            }
        }
        words.Add(0xBF810000);
        var fixture = new Gen5ComputeFixture("component-bounds", words.ToArray(), StoreScalarResourceBase: 8, StoreBackingBytes: 32);
        Assert.Equal(expected, ExecuteRequestOrThrow(fixture, buffer));
    }

    // ---- the request path: the argument buffer replaces the uniforms and per-slot buffers ----

    [Fact]
    public void AllFixturesCompileThroughTheArgumentBuffer()
    {
        if (SkipWithoutMetalDevice("compile validation")) return;

        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
            Assert.True(
                MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
                $"[{fixture.Name}] Metal rejected the request-path MSL: {error}\n{shader.Source}");
        }

        var pixel = Gen5ComputeFixtures.CompileStageRequestOrThrow(Gen5ComputeFixtures.PixelWords, Resources.ShaderStage.Pixel);
        Assert.True(MetalNative.TryCompileLibrary(pixel.Source, out _, out var pixelError), $"[pixel] {pixelError}\n{pixel.Source}");
        var vertex = Gen5ComputeFixtures.CompileStageRequestOrThrow(Gen5ComputeFixtures.VertexWords, Resources.ShaderStage.Vertex, requiredVertexOutputCount: 2);
        Assert.True(MetalNative.TryCompileLibrary(vertex.Source, out _, out var vertexError), $"[vertex] {vertexError}\n{vertex.Source}");
    }

    [Fact]
    public void ExecStoreProgramExecutesThroughTheArgumentBuffer()
    {
        if (SkipWithoutMetalDevice("the execution test")) return;

        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[64];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.ExecStore, buffer);

        var fmac = BitConverter.SingleToUInt32Bits(MathF.FusedMultiplyAdd(1.5f, 2.25f, 10.0f));
        var mulHiSigned = (uint)(((long)0x7FFFFFFF * 0x00010003) >> 32);
        var mulLoSigned = unchecked(0x7FFFFFFFu * 0x00010003u);
        var movBits = BitConverter.SingleToUInt32Bits(1.5f);

        Assert.Equal(fmac, ReadDword(result, 0));
        Assert.Equal(mulHiSigned, ReadDword(result, 4));
        Assert.Equal(mulLoSigned, ReadDword(result, 8));
        Assert.Equal(Sentinel, ReadDword(result, 12));
        Assert.Equal(movBits, ReadDword(result, 16));
    }

    [Fact]
    public void LoopProgramIteratesThroughTheArgumentBuffer()
    {
        if (SkipWithoutMetalDevice("the execution test")) return;

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.Loop, new byte[16]);
        Assert.Equal(15u, ReadDword(result, 0));
    }

    [Fact]
    public void TypedLoadFixtureReturnsInstructionFormatComponentsThroughTheArgumentBuffer()
    {
        if (SkipWithoutMetalDevice("the execution test")) return;

        const uint Sentinel = 0xDEADBEEFu;
        var buffer = new byte[32];
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        var first = BitConverter.SingleToUInt32Bits(1.5f);
        var second = BitConverter.SingleToUInt32Bits(-2.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), first);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), second);

        var result = ExecuteRequestOrThrow(Gen5ComputeFixtures.TypedLoad, buffer);

        Assert.Equal(first, ReadDword(result, 16));
        Assert.Equal(second, ReadDword(result, 20));
        Assert.Equal(Sentinel, ReadDword(result, 8));
    }

    private static byte[] ExecuteRequestOrThrow(Gen5ComputeFixture fixture, byte[] buffer,
        uint waveLaneCount = 32, uint localSizeX = 32, uint threadsPerThreadgroup = 1)
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture, waveLaneCount, localSizeX);
        if (!MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] {compileError}\n{shader.Source}");
        }

        // The user data registers hold zero; bounds come from the argument buffer lengths.
        var pushData = new byte[Resources.PushData.ByteSize];
        if (!MetalNative.TryExecuteWithArgumentBuffer(
                library,
                shader.EntryPoint,
                buffer,
                pushData,
                shader.ArgumentLayout ?? throw new InvalidOperationException($"[{fixture.Name}] no argument layout"),
                threadsPerThreadgroup,
                out var result,
                out var runError))
        {
            throw new InvalidOperationException($"[{fixture.Name}] {runError}");
        }

        return result;
    }

    // Without a Metal device the test skips, unless the required gate runs on a Mac.
    private bool SkipWithoutMetalDevice(string activity)
    {
        if (MetalNative.IsAvailable) return false;
        Assert.False(
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
            $"The required gate cannot run {activity} without a Metal device.");
        output.WriteLine($"No Metal device on this host; {activity} skipped.");
        return true;
    }

    private static uint ReadDword(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
}
