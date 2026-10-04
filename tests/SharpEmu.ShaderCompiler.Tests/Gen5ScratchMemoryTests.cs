// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScratchMemoryTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    public static TheoryData<uint, Gen5ScratchAddressMode, Gen5Operand[]> AddressModes => new()
    {
        // scratch_load_dword v5, v7, off
        { 0x057D0007u, Gen5ScratchAddressMode.Vector, [Gen5Operand.Vector(7)] },
        // scratch_load_dword v5, off, s3
        { 0x05030000u, Gen5ScratchAddressMode.Scalar, [Gen5Operand.Scalar(3)] },
        // GFX10.3 ST form: scratch_load_dword v5, off, off
        { 0x057F0000u, Gen5ScratchAddressMode.Immediate, [] },
    };

    [Theory]
    [MemberData(nameof(AddressModes))]
    public void ExactScratchLoadUsesVdstAndArchitecturalAddressMode(
        uint secondWord,
        Gen5ScratchAddressMode expectedMode,
        Gen5Operand[] expectedSources)
    {
        var instruction = DecodeProgram(0xDC304000u, secondWord, SEndpgm)
            .Instructions[0];

        Assert.Equal("ScratchLoadDword", instruction.Opcode);
        var control = Assert.IsType<Gen5ScratchMemoryControl>(instruction.Control);
        Assert.Equal(expectedMode, control.AddressMode);
        Assert.Equal(5u, control.DestinationVectorRegister);
        Assert.Equal(0u, control.SourceVectorRegister);
        Assert.Equal(1u, control.DwordCount);
        Assert.Equal(expectedSources, instruction.Sources);
        Assert.Equal([Gen5Operand.Vector(5)], instruction.Destinations);
    }

    public static TheoryData<uint, string, uint, bool> ScratchOpcodes => new()
    {
        { 0x08, "ScratchLoadUbyte", 1, true },
        { 0x09, "ScratchLoadSbyte", 1, true },
        { 0x0A, "ScratchLoadUshort", 1, true },
        { 0x0B, "ScratchLoadSshort", 1, true },
        { 0x0C, "ScratchLoadDword", 1, true },
        { 0x0D, "ScratchLoadDwordx2", 2, true },
        { 0x0E, "ScratchLoadDwordx4", 4, true },
        { 0x0F, "ScratchLoadDwordx3", 3, true },
        { 0x18, "ScratchStoreByte", 1, false },
        { 0x19, "ScratchStoreByteD16Hi", 1, false },
        { 0x1A, "ScratchStoreShort", 1, false },
        { 0x1B, "ScratchStoreShortD16Hi", 1, false },
        { 0x1C, "ScratchStoreDword", 1, false },
        { 0x1D, "ScratchStoreDwordx2", 2, false },
        { 0x1E, "ScratchStoreDwordx4", 4, false },
        { 0x1F, "ScratchStoreDwordx3", 3, false },
        { 0x20, "ScratchLoadUbyteD16", 1, true },
        { 0x21, "ScratchLoadUbyteD16Hi", 1, true },
        { 0x22, "ScratchLoadSbyteD16", 1, true },
        { 0x23, "ScratchLoadSbyteD16Hi", 1, true },
        { 0x24, "ScratchLoadShortD16", 1, true },
        { 0x25, "ScratchLoadShortD16Hi", 1, true },
    };

    [Theory]
    [MemberData(nameof(ScratchOpcodes))]
    public void ScratchLoadStoreFamilyDecodes(
        uint opcode,
        string expectedName,
        uint expectedDwords,
        bool load)
    {
        var firstWord = 0xDC000000u | (opcode << 18) | (1u << 14);
        var instruction = DecodeProgram(firstWord, 0x057D0307u, SEndpgm)
            .Instructions[0];

        Assert.Equal(expectedName, instruction.Opcode);
        var control = Assert.IsType<Gen5ScratchMemoryControl>(instruction.Control);
        Assert.Equal(expectedDwords, control.DwordCount);
        Assert.Equal(7u, control.VectorAddress);
        Assert.Equal(3u, control.SourceVectorRegister);
        Assert.Equal(5u, control.DestinationVectorRegister);
        Assert.Equal(load ? expectedDwords : 0, (uint)instruction.Destinations.Count);
    }

    [Fact]
    public void ScratchOffsetIsSignedTwelveBitsAndDlcIsNotPartOfIt()
    {
        var instruction = DecodeProgram(
            0xDC304000u | (1u << 12) | 0xFFFu,
            0x057D0007u,
            SEndpgm).Instructions[0];

        var control = Assert.IsType<Gen5ScratchMemoryControl>(instruction.Control);
        Assert.Equal(-1, control.OffsetBytes);
    }

    [Fact]
    public void ScratchNeedsNoDescriptorAndCompilesAsPrivateMemory()
    {
        var program = DecodeProgram(
            // scratch_store_dword v7, v3, off
            0xDC704000u, 0x007D0307u,
            // scratch_load_dword v5, v7, off
            0xDC304000u, 0x057D0007u,
            SEndpgm);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program);
        Assert.All(plan.Memory.Entries, entry =>
            Assert.Equal(MemoryResourceKind.Scratch, entry.Kind));
        Assert.Empty(resources.Info.Buffers);
        Assert.Empty(resources.Info.Images);

        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ScratchDwords = 16,
        };
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var spirv,
                out var spirvError),
            spirvError);
        var spirvOpcodes = new SpirvModuleInspector(spirv.Spirv).Opcodes;
        Assert.Contains((ushort)SpirvOp.AccessChain, spirvOpcodes);
        Assert.Contains((ushort)SpirvOp.Load, spirvOpcodes);
        Assert.Contains((ushort)SpirvOp.Store, spirvOpcodes);

        Assert.True(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out var metal,
                out var metalError),
            metalError);
        Assert.Contains(
            "thread uint sharpemu_scratch[16] = {};",
            metal.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "sharpemu_load_scratch_bytes",
            metal.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "sharpemu_store_scratch_bytes",
            metal.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroSizedScratchKeepsBackingArrayButUsesZeroLogicalBound()
    {
        var program = DecodeProgram(0xDC304000u, 0x057D0007u, SEndpgm);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ScratchDwords = 0,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError),
            spirvError);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError),
            metalError);
        Assert.Contains(
            "thread uint sharpemu_scratch[1] = {};",
            metal.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "sharpemu_load_scratch_bytes(sharpemu_scratch, 0u",
            metal.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedScratchSaddrFailsBackendCompilation()
    {
        var program = DecodeProgram(0xDC304000u, 0x057E0007u, SEndpgm);
        var control = Assert.IsType<Gen5ScratchMemoryControl>(
            program.Instructions[0].Control);
        Assert.Equal(Gen5ScratchAddressMode.Invalid, control.AddressMode);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ScratchDwords = 16,
        };

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out _,
                out var spirvError));
        Assert.Contains("invalid scratch", spirvError, StringComparison.Ordinal);
        Assert.False(
            Gen5MslTranslator.TryCompileProgram(
                request,
                out _,
                out var metalError));
        Assert.Contains("invalid scratch", metalError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xDCC84000u, "unknown-flat")]
    [InlineData(0xDC306000u, "unsupported-flat-lds")]
    public void InvalidScratchFormsFailClosed(uint firstWord, string expectedError)
    {
        var memory = CreateMemory(firstWord, 0x057D0007u, SEndpgm);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.False(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out _,
                out var error));
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    private static Gen5ShaderProgram DecodeProgram(params uint[] words)
    {
        var context = new CpuContext(CreateMemory(words), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static InstructionMemory CreateMemory(params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        return new InstructionMemory(bytes);
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < ShaderAddress ||
                destination.Length > bytes.Length ||
                address - ShaderAddress > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - ShaderAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
