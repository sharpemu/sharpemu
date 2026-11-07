// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class CompactMemoryAccessPolicyTests
{
    private const ulong InputAddress = 0x1_0000_0000;
    private const ulong OutputAddress = 0x2_0000_0000;

    [Theory]
    [InlineData(null, CompactMemoryAccessPolicy.DefaultThreshold)]
    [InlineData("", CompactMemoryAccessPolicy.DefaultThreshold)]
    [InlineData("  ", CompactMemoryAccessPolicy.DefaultThreshold)]
    [InlineData("off", int.MaxValue)]
    [InlineData("OFF", int.MaxValue)]
    [InlineData("0", int.MaxValue)]
    [InlineData("1", 1)]
    [InlineData(" 256 ", 256)]
    public void ThresholdParses(string? value, int expected) =>
        Assert.Equal(expected, CompactMemoryAccessPolicy.ParseThreshold(value));

    [Theory]
    [InlineData("-1")]
    [InlineData("+4")]
    [InlineData("0x40")]
    [InlineData("many")]
    [InlineData("99999999999")]
    public void InvalidThresholdIsRejected(string value) =>
        Assert.Throws<FormatException>(() => CompactMemoryAccessPolicy.ParseThreshold(value));

    [Theory]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(64)]
    public void CountsGlobalDwords(int dwords) =>
        Assert.Equal(dwords, CompactMemoryAccessPolicy.CountDeviceAddressDwords(Extract(CopyProgram(dwords), userDataCount: 0)));

    // Buffers bound through user-data descriptors do not go through the page table: a program of many such accesses keeps
    // the default lowering, and the bytes of its module.
    [Fact]
    public void BoundBufferAccessesDoNotCount()
    {
        var code = new List<Gen5ShaderInstruction> { MoveVector(0x00, 0, 0) };
        for (var index = 0; index < 64; index++)
        {
            code.Add(BufferLoad(0x08 + (uint)index * 8, 20, offset: index * 4));
        }

        code.Add(EndProgram(0x08 + 64 * 8));
        var plan = Extract(Program([.. code]));
        Assert.Equal(0, CompactMemoryAccessPolicy.CountDeviceAddressDwords(plan));
        Assert.False(CompactMemoryAccessPolicy.Prefers(plan, ShaderStage.Compute, 1));
    }

    // A program that reads few dwords through device addresses keeps the default lowering, whatever its number of
    // bound-buffer accesses: compact access would not remove its branches, and a changed module misses the driver's
    // pipeline cache once.
    [Fact]
    public void ProgramWithFewDeviceAddressDwordsKeepsDefaultLowering()
    {
        var code = new List<Gen5ShaderInstruction>
        {
            MoveScalar(0x00, 12, unchecked((uint)InputAddress)), MoveScalar(0x08, 13, (uint)(InputAddress >> 32)), MoveVector(0x10, 0, 0),
            GlobalMemory(0x18, "GlobalLoadDword", 12, 0, 1, 0),
        };
        for (var index = 0; index < 600; index++)
        {
            code.Add(BufferLoad(0x20 + (uint)index * 8, 20, offset: index * 4));
        }

        code.Add(EndProgram(0x20 + 600 * 8));
        var (plan, _, layout) = Prepare(Program([.. code]));
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.False(plan.Info.UsesDeviceAddressFallback);
        Assert.Null(layout.Find(DescriptorBindingKind.DeviceAddressFallback));
    }

    [Theory]
    [InlineData(ShaderStage.Compute, 3, 4, false)]
    [InlineData(ShaderStage.Compute, 4, 4, true)]
    [InlineData(ShaderStage.Compute, 5, 4, true)]
    [InlineData(ShaderStage.Compute, 5, int.MaxValue, false)]
    [InlineData(ShaderStage.Pixel, 5, 4, false)]
    [InlineData(ShaderStage.Vertex, 5, 4, false)]
    public void SelectsOnlyComputeProgramsWithManyDeviceAddressDwords(ShaderStage stage, int dwords, int threshold, bool expected) =>
        Assert.Equal(expected, CompactMemoryAccessPolicy.Prefers(Extract(CopyProgram(dwords), userDataCount: 0), stage, threshold));

    // Before the policy, a large non-linked compute program received the per-dword branch lowering: every global dword
    // resolved its page through two branches, a conditional fault atomic and a conditional load.
    [Fact]
    public void LargeComputeProgramBindsFallbackAndCompilesWithoutPerAccessBranches()
    {
        var count = CompactMemoryAccessPolicy.DefaultThreshold;
        var program = CopyProgram(count);
        var (plan, resources, layout) = Prepare(program);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Info.UsesDeviceAddressFallback);
        Assert.NotNull(layout.Find(DescriptorBindingKind.DeviceAddressFallback));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var compact = Opcodes(shader.Spirv);
        var standard = Opcodes(Compile(program, compact: false));
        // The invocation reports its first and last missing pages when it ends, not one per dword; a load no longer branches.
        Assert.True(compact.Count(op => op == SpirvOp.AtomicOr) <= 2);
        Assert.True(standard.Count(op => op == SpirvOp.AtomicOr) >= count);
        var compactBranches = compact.Count(op => op == SpirvOp.BranchConditional);
        var standardBranches = standard.Count(op => op == SpirvOp.BranchConditional);
        Assert.True(compactBranches * 2 < standardBranches, $"{compactBranches} compact against {standardBranches} default conditional branches");
    }

    [Fact]
    public void SmallComputeProgramKeepsDefaultLowering()
    {
        var program = CopyProgram(CompactMemoryAccessPolicy.DefaultThreshold - 1);
        var (plan, _, layout) = Prepare(program);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.False(plan.Info.UsesDeviceAddressFallback);
        Assert.Null(layout.Find(DescriptorBindingKind.DeviceAddressFallback));
    }

    // Both lowerings of one program read the same words and write the same words, including an input page that is not
    // mapped: the executor's page table maps every page but the null page.
    [Theory]
    [InlineData(InputAddress)]
    [InlineData(0x3FF0ul)]
    public void CompactAndDefaultLoweringsWriteTheSameMemory(ulong input)
    {
        var program = CopyProgram(CompactMemoryAccessPolicy.DefaultThreshold, input);
        var compact = Run(Compile(program, compact: true));
        var standard = Run(Compile(program, compact: false));
        Assert.Equal(CompactMemoryAccessPolicy.DefaultThreshold / 2, compact.Count);
        Assert.Equal(standard, compact);
        foreach (var (address, value) in compact)
        {
            var source = input + (address - OutputAddress);
            Assert.Equal(source < 0x4000 ? 0u : Word(source), value);
        }
    }

    private static uint Word(ulong address) => unchecked((uint)(address * 0x9E3779B97F4A7C15ul >> 32));

    private static SortedDictionary<ulong, uint> Run(byte[] spirv)
    {
        var written = new SortedDictionary<ulong, uint>();
        // The flattened table holds the store range: the output, 64 KiB.
        ulong? HostWord(string name, string path) => name != "flattenedResourceTable" ? null : path switch
        {
            ".0.0" => unchecked((uint)OutputAddress),
            ".0.1" => OutputAddress >> 32,
            ".0.2" => 0x10000ul,
            _ => null,
        };
        new SpirvExecutor(spirv) { HostWord = HostWord }.Run(Word, (address, value) => written[address] = value);
        return written;
    }

    private static byte[] Compile(Gen5ShaderProgram program, bool compact)
    {
        var plan = Extract(program, userDataCount: 0);
        Assert.True(plan.Info.UsesDeviceAddresses);
        plan.Info.UsesDeviceAddressFallback = compact;
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        Assert.Equal(compact, layout.Find(DescriptorBindingKind.DeviceAddressFallback) is not null);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        return shader.Spirv;
    }

    // Loads dwordCount - dwordCount / 2 dwords from input through global loads and stores dwordCount / 2 of them to the same
    // offsets of the output: dwordCount device-address dwords in all.
    private static Gen5ShaderProgram CopyProgram(int dwordCount, ulong input = InputAddress)
    {
        var code = new List<Gen5ShaderInstruction>
        {
            MoveScalar(0x00, 12, unchecked((uint)input)),
            MoveScalar(0x08, 13, (uint)(input >> 32)),
            MoveScalar(0x10, 14, unchecked((uint)OutputAddress)),
            MoveScalar(0x18, 15, (uint)(OutputAddress >> 32)),
            MoveVector(0x20, 0, 0),
        };
        var pc = 0x28u;
        var copies = dwordCount / 2;
        for (var index = 0; index < dwordCount - copies; index++)
        {
            code.Add(GlobalMemory(pc, "GlobalLoadDword", 12, 0, (uint)(1 + index % 32), 0, offset: index * 4));
            pc += 8;
            if (index < copies)
            {
                code.Add(GlobalMemory(pc, "GlobalStoreDword", 14, 0, 0, (uint)(1 + index % 32), offset: index * 4));
                pc += 8;
            }
        }

        code.Add(EndProgram(pc));
        return Program([.. code]);
    }

    private static List<SpirvOp> Opcodes(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var opcodes = new List<SpirvOp>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            opcodes.Add((SpirvOp)(words[offset] & 0xFFFF));
        }

        return opcodes;
    }
}
