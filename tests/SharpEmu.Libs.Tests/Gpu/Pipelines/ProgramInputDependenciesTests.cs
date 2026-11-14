// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Cross-draw reuse must prove more than a shader's addresses or its code identity.
[Collection(SchedulingStateCollection.Name)]
public sealed class ProgramInputDependenciesTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong Code = MemoryBase + 0x100;
    private const ulong Semantics = MemoryBase + 0x200;
    private const ulong UserBlock = MemoryBase + 0x300;
    private const ulong DirectOffsets = MemoryBase + 0x400;
    private readonly FatalScope _fatal = new();
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public ProgramInputDependenciesTests() => _context = new CpuContext(_memory, Generation.Gen5);
    public void Dispose() => _fatal.Dispose();

    private static RegisteredShader Shader() => new(Code, MemoryBase, 16, 0, UserBlock, Semantics, 1, 0, 0);

    private PixelInputInfo ResolvePixel(RegisterBanks banks, RegisteredShader shader)
    {
        var mappings = new ColorComponentMap[PixelInputInfo.TargetCount];
        Array.Fill(mappings, ColorComponentMap.Identity);
        return PixelStageInputResolver.Resolve(_context, shader, banks.Context.ShaderInterface,
            new byte[PixelInputInfo.TargetCount], mappings, 1);
    }

    private static uint[] StaticKey(PixelInputInfo info)
    {
        var key = new List<uint>();
        StageStaticKey.Build(info, key);
        return key.ToArray();
    }

    private void Write32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private void Write16(ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private ulong CodeIdentity(RegisteredShader shader) =>
        ShaderIdentity.Compute(_memory, shader.CodeAddress, shader.CodeRanges, "test");

    [Theory]
    [InlineData("interpolator")]
    [InlineData("system-inputs")]
    [InlineData("export-format")]
    public void EqualShaderAddressesDoNotProveEqualPixelStaticKeys(string changedField)
    {
        var live = Banks();
        var before = live.Snapshot();
        var changed = live.ShaderInterfaceForWrite;
        switch (changedField)
        {
            case "interpolator":
                changed.PixelInterpolatorWritten = 1;
                changed.PixelInterpolatorSettings[0] = 7;
                break;
            case "system-inputs":
                changed.PixelInputEnable = changed.PixelInputAddress = 2;
                break;
            case "export-format":
                changed.TargetOutputModes[0] = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedField));
        }
        var after = live.Snapshot();
        Assert.Equal(before.Shader.Vertex.ExportAddress, after.Shader.Vertex.ExportAddress);
        Assert.Equal(before.Shader.Pixel.Address, after.Shader.Pixel.Address);
        Assert.NotSame(before.Context.ShaderInterface, after.Context.ShaderInterface);

        var shader = Shader();
        var first = ResolvePixel(before, shader);
        var second = ResolvePixel(after, shader);
        Assert.False(StaticKey(first).SequenceEqual(StaticKey(second)));
    }

    [Fact]
    public void SameShaderAndCodeIdentityDoNotProveEqualPixelSemantics()
    {
        var banks = Banks();
        var shader = Shader();
        var identity = CodeIdentity(shader);
        var before = ResolvePixel(banks, shader);
        Write32(Semantics, 1u << 24); // Custom full-precision interpolation.
        var after = ResolvePixel(banks, shader);

        Assert.Equal(identity, CodeIdentity(shader));
        Assert.Equal(0u, before.CustomInterpolationMask);
        Assert.Equal(1u, after.CustomInterpolationMask);
        Assert.False(StaticKey(before).SequenceEqual(StaticKey(after)));
    }

    [Fact]
    public void SameShaderAndCodeIdentityDoNotProveEqualVertexTableRegisters()
    {
        var shader = Shader();
        Write32(UserBlock, unchecked((uint)DirectOffsets));
        Write32(UserBlock + 4, (uint)(DirectOffsets >> 32));
        Write16(UserBlock + 0x2C, 11);
        for (uint type = 0; type < 11; type++) Write16(DirectOffsets + type * 2, 0xFFFF);
        Write16(DirectOffsets + 8 * 2, 10);
        Write16(DirectOffsets + 10 * 2, 8);
        Write32(Semantics, 1u << 16);
        var identity = CodeIdentity(shader);
        Assert.True(VertexInputResolver.TryReadTables(_context, shader, 32, out var before, out var firstError), firstError);

        Write16(DirectOffsets + 8 * 2, 12);
        Assert.True(VertexInputResolver.TryReadTables(_context, shader, 32, out var after, out var secondError), secondError);

        Assert.Equal(identity, CodeIdentity(shader));
        Assert.Equal(10, before.VertexBufferRegister);
        Assert.Equal(12, after.VertexBufferRegister);
        Assert.Equal(before.InputSemantics, after.InputSemantics);
    }
}
