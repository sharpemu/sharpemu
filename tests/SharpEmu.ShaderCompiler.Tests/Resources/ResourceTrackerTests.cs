// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceTrackerTests
{
    private const uint Format32x4Float = 77;
    private const uint Format32x2Float = 64;
    private const uint ImageType2D = 9;
    private const uint IdentitySwizzle = 0xFAC;

    [Theory]
    [InlineData("ImageAtomicUmax", 3u, true)]
    [InlineData("ImageAtomicUmax", 1u, false)]
    [InlineData("ImageAtomicCmpswap", 0xFu, true)]
    [InlineData("ImageAtomicCmpswap", 3u, false)]
    public void ImageAtomicOnTwoDataDwordsIsMarkedWide(string opcode, uint dmask, bool wide)
    {
        // An atomic names its texel width by the data dwords: two (compare-swap: four) are a 64-bit texel.
        var plan = Extract(Program(Image(0, opcode, 0, dmask: dmask), EndProgram(8)));

        var image = Assert.Single(plan.Info.Images);
        Assert.True(image.Atomic);
        Assert.Equal(wide, image.Atomic64);
    }

    // A descriptor word that 40 branches each change on both sides is a web with 2^40 paths. Planning and evaluating
    // it must look at each node once: walking every path did not finish.
    [Fact]
    public void ADescriptorWordMergedThroughManyBranchesIsPlannedQuickly()
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            // s5..s7 are loaded words; s4 starts as user data.
            ScalarLoad(0, 0, 8, count: 4),
            MoveScalarRegister(8, 5, 9),
            MoveScalarRegister(12, 6, 10),
            MoveScalarRegister(16, 7, 11),
            MoveScalarRegister(20, 4, 2),
        };
        var pc = 24u;
        for (var branch = 0; branch < 40; branch++, pc += 20)
        {
            // if (s3 == 0) s4 += 1; else s4 += 2;
            instructions.Add(Sopc(pc, "SCmpEqU32", Gen5Operand.Scalar(3), Operand(0)));
            instructions.Add(Branch(pc + 4, "SCbranchScc0", 2));
            instructions.Add(Sop2(pc + 8, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(1)));
            instructions.Add(Branch(pc + 12, "SBranch", 1));
            instructions.Add(Sop2(pc + 16, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(2)));
        }

        instructions.Add(BufferLoad(pc, 4));
        instructions.Add(EndProgram(pc + 8));
        var program = Program([.. instructions]);

        // The V# words s5..s7 are read at 0x1000; s2 = 0x2000 and s3 = 0, so every branch adds 1.
        var memory = new TestWordMemory { Words = [0, 0, 16, 0] };
        var userData = new uint[64];
        userData[0] = 0x1000;
        userData[2] = 0x2000;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var materialized = false;
        Exception? failure = null;
        var planning = new Thread(() =>
        {
            try
            {
                var plan = Extract(program);
                materialized = ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read),
                    ref snapshot, ref specialization);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        planning.Start();

        Assert.True(planning.Join(TimeSpan.FromSeconds(30)), "planning did not finish");
        Assert.Null(failure);
        Assert.True(materialized);
        Assert.Equal(0x2000u + 40u, Assert.Single(snapshot.Buffers)[0]);
    }

    // Behind exec branches the same word is a web of phis, which the runtime-descriptor check walks for a memory read.
    // The V# the branches choose has no single source to bind, so the load reads it from its registers when it runs;
    // the walk still finishes at once.
    [Fact]
    public void ADescriptorWordMergedThroughManyExecBranchesIsPlannedQuickly()
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ScalarLoad(0, 0, 8, count: 4),
            MoveScalarRegister(8, 5, 9),
            MoveScalarRegister(12, 6, 10),
            MoveScalarRegister(16, 7, 11),
            MoveScalarRegister(20, 4, 2),
            // One more exec branch makes s5..s7 phis too, which the check accepts as runtime words.
            Vopc(24, "VCmpxEqU32", Operand(1), 24),
            Branch(28, "SCbranchExecz", 3),
            Sop2(32, "SAddU32", 5, Gen5Operand.Scalar(5), Operand(1)),
            Sop2(36, "SAddU32", 6, Gen5Operand.Scalar(6), Operand(1)),
            Sop2(40, "SAddU32", 7, Gen5Operand.Scalar(7), Operand(1)),
        };
        var pc = 44u;
        for (var branch = 0; branch < 40; branch++, pc += 12)
        {
            // Lanes where v24 == 1 add 1 to s4; with none, the block is skipped.
            instructions.Add(Vopc(pc, "VCmpxEqU32", Operand(1), 24));
            instructions.Add(Branch(pc + 4, "SCbranchExecz", 1));
            instructions.Add(Sop2(pc + 8, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(1)));
        }

        instructions.Add(BufferLoad(pc, 4));
        instructions.Add(EndProgram(pc + 8));
        var program = Program([.. instructions]);

        Exception? failure = null;
        var planning = new Thread(() =>
        {
            try
            {
                Extract(program);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        planning.Start();

        Assert.True(planning.Join(TimeSpan.FromSeconds(30)), "planning did not finish");
        Assert.Null(failure);
        Assert.True(Extract(program).Memory.Entries.Single(memory => memory.Pc == pc).DeviceDescriptor);
    }

    [Fact]
    public void DenseBufferTracking()
    {
        var program = Program(
            BufferLoad(4, 0, offset: 4, formatted: true),
            BufferStore(8, 0, offset: 12, formatted: true),
            BufferAtomicAdd(12, 0),
            BufferLoad(28, 4, offset: 4, formatted: true),
            EndProgram(36));
        var plan = Extract(program);

        Assert.Equal(2, plan.Info.Buffers.Count);
        Assert.Equal(2, plan.DescriptorSources.Count);
        var resource = plan.Info.Buffers[0];
        Assert.True(resource.Read && resource.Written && resource.Atomic && resource.Formatted);
        Assert.Equal(16u, resource.MaxByteExtent);
        Assert.Equal(4u, resource.FirstUsePc);
        Assert.Equal(0u, plan.Memory[0].Resource);
        Assert.Equal(0u, plan.Memory[1].Resource);
        Assert.Equal(0u, plan.Memory[2].Resource);
        Assert.Equal(1u, plan.Memory[3].Resource);
    }

    [Fact]
    public void ScalarAndVectorBufferAlias()
    {
        var program = Program(
            ScalarBufferLoad(4, 0, destination: 8, dynamicOffsetRegister: 4),
            BufferLoad(8, 0),
            EndProgram(12));
        var plan = Extract(program);

        var buffer = Assert.Single(plan.Info.Buffers);
        Assert.True(buffer.Scalar);
        Assert.Equal(0u, plan.Memory[0].Resource);
        Assert.Equal(0u, plan.Memory[1].Resource);
    }

    [Fact]
    public void RuntimeUnsignedMinDescriptor()
    {
        var program = Program(
            MoveScalar(0, 8, 0),
            MoveScalar(4, 9, 0),
            MoveScalar(8, 10, 64),
            Sop2(12, "SMinU32", 11, Gen5Operand.Scalar(0), Operand(0x100)),
            BufferLoad(0x330, 8),
            EndProgram(0x338));
        var plan = Extract(program);
        var source = plan.Info.Buffers[0].Source;

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source, Inputs([0xFFFFFFFF]), out var clamped));
        Assert.Equal(0x100u, clamped.Dwords[3]);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source, Inputs([0x80]), out var kept));
        Assert.Equal(0x80u, kept.Dwords[3]);
    }

    [Fact]
    public void ImagesSamplersAndAliases()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        foreach (var (register, value) in new (uint, uint)[] { (16, 0), (17, 1), (18, 2), (19, 0x1111), (20, 0), (21, 1), (22, 2), (23, 0x2222) })
        {
            instructions.Add(MoveScalar(pc, register, value));
            pc += 4;
        }

        instructions.Add(Image(0x40, "ImageSample", 0, 16));
        instructions.Add(Image(0x48, "ImageSample", 0, 20));
        instructions.Add(Image(0x50, "ImageSampleCLz", 0, 16));
        instructions.Add(Image(0x58, "ImageAtomicAdd", 0));
        instructions.Add(BufferLoad(0x60, 0));
        instructions.Add(EndProgram(0x68));
        var plan = Extract(Program([.. instructions]));

        Assert.Equal(3, plan.Info.Images.Count);
        Assert.Single(plan.Info.Samplers);
        Assert.Equal(2, plan.Info.SampledPairs.Count);
        var normal = plan.Memory.Find(0x40)!;
        var repeated = plan.Memory.Find(0x48)!;
        var compare = plan.Memory.Find(0x50)!;
        Assert.Equal(normal.Resource, repeated.Resource);
        Assert.NotEqual(normal.Resource, compare.Resource);
        Assert.Equal(0u, normal.Sampler);
        Assert.Equal(0u, repeated.Sampler);
        var samplerSource = plan.DescriptorSources[(int)plan.Info.Samplers[0].Source];
        Assert.True(samplerSource.Dwords[3].IsConstant);
        Assert.Equal(0u, samplerSource.Dwords[3].ConstantU32);
        Assert.Equal(0u, plan.Info.Buffers[0].ImageAlias);
    }

    [Fact]
    public void ScalarBufferWithDeviceOnlyDescriptor_ReadsThroughRegisters()
    {
        // The descriptor base comes from a lane value: the host cannot bind it, the device reads through it.
        var program = Program(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, 12, destination: 20),
            EndProgram(24));

        var plan = Extract(program);

        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory.TryGetIndex(16, 0, out var index));
        Assert.True(plan.Memory[index].DeviceDescriptor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferLoadedFromDeviceAddress_UsesTheDeviceDescriptor(bool invalidRecordCount)
    {
        var program = Program(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            ScalarLoad(8, 12, 16, count: 4),
            invalidRecordCount ? MoveScalarRegister(16, 18, 90) : Nop(16),
            BufferLoad(20, 16),
            EndProgram(28));

        if (invalidRecordCount)
        {
            Assert.Throws<ResourcePlanException>(() => Extract(program));
            return;
        }

        var plan = Extract(program);
        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory.Find(20)!.DeviceDescriptor);
        Assert.Contains(plan.DeviceAddressRanges, range => range.MemoryIndices.Any(index => plan.Memory[index].Pc == 8));
    }

    // A waterfall loop picks the descriptor per wave: v_readfirstlane reads a per-lane index, S_BUFFER_LOAD fetches the T# from a
    // table at that offset and the sample uses it. Such a descriptor has no single source, so it reads as a null image like the
    // control-dependent lookups instead of failing the whole shader.
    [Fact]
    public void ImageDescriptorLoadedAtALaneChosenIndex_ReadsAsANullImage()
    {
        var program = LaneIndexedImageProgram();

        var plan = Extract(program, stage: ShaderStage.Pixel);

        var image = Assert.Single(plan.DescriptorSources, source => source.DwordCount == 8);
        Assert.All(image.Dwords, dword => Assert.True(dword.IsConstant && dword.ConstantU32 == 0));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, ShaderStage.Pixel), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    // Compute keeps rejecting an index no bound was proved for (see DirectImageTableTests).
    [Fact]
    public void ImageDescriptorLoadedAtALaneChosenIndexInCompute_IsStillRejected()
    {
        var program = LaneIndexedImageProgram();

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program, stage: ShaderStage.Compute));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    // The fallback is for images only: a sampler is still rejected when a lane chose its bits.
    [Fact]
    public void SamplerLoadedAtALaneChosenIndex_IsStillRejected()
    {
        var program = LaneIndexedSamplerProgram();

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program, stage: ShaderStage.Pixel));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    // Opted in to the bindless fallback, the same heap entry reads as a null image in compute too, and the plan names
    // the access.
    [Fact]
    public void ImageDescriptorLoadedAtALaneChosenIndexInCompute_ReadsAsNullWhenOptedIn()
    {
        var program = LaneIndexedImageProgram();

        var plan = Extract(program, stage: ShaderStage.Compute, bindlessNullDescriptors: true);

        var image = Assert.Single(plan.DescriptorSources, source => source.DwordCount == 8);
        Assert.All(image.Dwords, dword => Assert.True(dword.IsConstant && dword.ConstantU32 == 0));
        Assert.Equal([48u], plan.NullBindlessDescriptorPcs);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, bindlessNullDescriptors: true), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    // A sampler read from a heap at a lane-chosen index reads as a null sampler when opted in.
    [Fact]
    public void SamplerLoadedAtALaneChosenIndex_ReadsAsNullWhenOptedIn()
    {
        var program = LaneIndexedSamplerProgram();

        var plan = Extract(program, stage: ShaderStage.Pixel, bindlessNullDescriptors: true);

        Assert.Single(plan.DescriptorSources, source =>
            source.DwordCount == 4 && source.Dwords.All(dword => dword.IsConstant && dword.ConstantU32 == 0));
        Assert.Equal([32u], plan.NullBindlessDescriptorPcs);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, ShaderStage.Pixel, bindlessNullDescriptors: true), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    // The plan lists only what the opt-in added: a pixel shader's lane-chosen image reads as null without it.
    [Fact]
    public void ThePlanListsOnlyTheOptedInFallbacks()
    {
        var plan = Extract(LaneIndexedImageProgram(), stage: ShaderStage.Pixel, bindlessNullDescriptors: true);

        Assert.Empty(plan.NullBindlessDescriptorPcs);
    }

    // A T# whose two halves a shader reads from one scalar buffer at a uniform record offset, but not next to each other
    // (a terrain material pixel shader builds s88..s95 from record bytes 48..63 and 192..207): each
    // word is a scalar buffer read the host evaluates when the draw is bound, so the image is a runtime descriptor.
    [Fact]
    public void AnImageAssembledFromTwoScalarBufferReadsIsEvaluatedPerWord()
    {
        var program = Program(
            // The record table V# in s16..s19 and the record index in s4 come from user data.
            MoveScalarRegister(0, 16, 0),
            MoveScalarRegister(4, 17, 1),
            MoveScalarRegister(8, 18, 2),
            MoveScalarRegister(12, 19, 3),
            Sop2(16, "SLshlB32", 106, Gen5Operand.Scalar(4), Operand(7)),
            ScalarBufferLoad(20, 16, destination: 20, count: 4, immediateOffset: 16, dynamicOffsetRegister: 106),
            ScalarBufferLoad(28, 16, destination: 24, count: 4, immediateOffset: 96, dynamicOffsetRegister: 106),
            Image(36, "ImageLoad", 20),
            EndProgram(44));
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[128] };
        for (var word = 0; word < memory.Words.Length; word++)
            memory.Words[word] = 0xA000u + (uint)word;

        var plan = Extract(program, stage: ShaderStage.Pixel);

        var image = Assert.Single(plan.Info.Images);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, image.Source,
            Inputs([0x1000, 0, 0x200, 0, 2], memory.Read), out var words));
        // Record 2 starts at byte 256 (word 64): words 4..7 and 24..27 of it.
        Assert.Equal([0xA044u, 0xA045u, 0xA046u, 0xA047u, 0xA058u, 0xA059u, 0xA05Au, 0xA05Bu], words.Dwords.ToArray());
    }

    internal static Gen5ShaderProgram LaneIndexedSamplerProgram() => Program(
        MoveScalarRegister(0, 16, 0),
        MoveScalarRegister(4, 17, 1),
        MoveScalarRegister(8, 18, 2),
        MoveScalarRegister(12, 19, 3),
        Vop2(16, "VLshlrevB32", 1, Operand(12), Gen5Operand.Vector(0)),
        ReadFirstLane(20, 106, 1),
        ScalarBufferLoad(24, 16, destination: 24, count: 4, dynamicOffsetRegister: 106),
        Image(32, "ImageSample", 8, 24),
        EndProgram(40));

    internal static Gen5ShaderProgram LaneIndexedImageProgram(uint dimension = 1, string opcode = "ImageSample", bool r128 = false) => Program(
        // The table V# in s16..s19 and the sampler in s24..s27 come from user data.
        MoveScalarRegister(0, 16, 0),
        MoveScalarRegister(4, 17, 1),
        MoveScalarRegister(8, 18, 2),
        MoveScalarRegister(12, 19, 3),
        MoveScalarRegister(16, 24, 4),
        MoveScalarRegister(20, 25, 5),
        MoveScalarRegister(24, 26, 6),
        MoveScalarRegister(28, 27, 7),
        // s106 = readfirstlane(v7) << 5: the byte offset of the T# the first lane asks for.
        ReadFirstLane(32, 106, 7),
        Sop2(36, "SLshlB32", 106, Gen5Operand.Scalar(106), Operand(5)),
        ScalarBufferLoad(40, 16, destination: 0, count: 8, dynamicOffsetRegister: 106),
        Image(48, opcode, 0, 24, dimension: dimension, r128: r128),
        EndProgram(56));
    // A shader that selects among states with v_cmpx and s_cbranch_execz sets a constant sampler up in the first state's block and samples
    // with it in a later state's block. The skip edge of the first branch reaches the join with the sampler registers unwritten, so the host
    // cannot name the sampler; it reads as a null sampler instead of failing the shader.
    [Fact]
    public void SamplerBuiltBehindASkippedBlock_ReadsAsANullSampler()
    {
        var program = SamplerBehindSkippedBlockProgram(definitionsBeforeTheBranch: false);

        var plan = Extract(program, userDataCount: 8);

        var sampler = Assert.Single(plan.DescriptorSources, source => source.DwordCount == 4);
        Assert.All(sampler.Dwords, dword => Assert.True(dword.IsConstant && dword.ConstantU32 == 0));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 8), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    // Only a sampler none of whose words is known reads as null: three words written before the branch make a partly known sampler.
    [Fact]
    public void SamplerWithOneWordBehindASkippedBlock_IsStillRejected()
    {
        var program = SamplerBehindSkippedBlockProgram(definitionsBeforeTheBranch: true);

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program, userDataCount: 8));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    private static Gen5ShaderProgram SamplerBehindSkippedBlockProgram(bool definitionsBeforeTheBranch) => Program(
        // The sampler is s36..s39: the last three words before the branch when asked for, the first word (or all four) inside the skipped block.
        definitionsBeforeTheBranch ? MoveScalar(0, 37, 0xFFF000) : Nop(0),
        definitionsBeforeTheBranch ? MoveScalar(4, 38, 0x0A500000) : Nop(4),
        definitionsBeforeTheBranch ? MoveScalar(8, 39, 0) : Nop(8),
        Vopc(12, "VCmpxEqU32", Operand(1), 24),
        Branch(16, "SCbranchExecz", 4),
        MoveScalar(20, 36, 0),
        definitionsBeforeTheBranch ? Nop(24) : MoveScalar(24, 37, 0xFFF000),
        definitionsBeforeTheBranch ? Nop(28) : MoveScalar(28, 38, 0x0A500000),
        definitionsBeforeTheBranch ? Nop(32) : MoveScalar(32, 39, 0),
        // The join, then the second state's block that samples with s36..s39.
        Vopc(36, "VCmpxEqU32", Operand(3), 24),
        Branch(40, "SCbranchExecz", 1),
        Image(44, "ImageSampleLz", 0, 36, dmask: 1, vectorAddress: 22),
        EndProgram(48));

    [Fact]
    public void SamplerWithDivergentBits_IsRejected()
    {
        var program = Program(
            MoveScalarRegister(0, 16, 0),
            MoveScalarRegister(4, 17, 1),
            MoveScalarRegister(8, 18, 2),
            Vop2(12, "VLshlrevB32", 1, Operand(12), Gen5Operand.Vector(0)),
            ReadFirstLane(16, 20, 1),
            Sop2(20, "SOrB32", 19, Gen5Operand.Scalar(3), Gen5Operand.Scalar(20)),
            Image(0x200, "ImageSample", 8, 16),
            EndProgram(0x208));

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program));
        Assert.Contains("not a valid runtime value", error.Message);
        Assert.Contains("pc=0x00000200", error.Message);
    }

    [Fact]
    public void FullDwordSdwaDescriptor_PreservesTheHostValue()
    {
        var program = Program(
            Vop2(0, "VOrB32", 1, Gen5Operand.Scalar(1), Operand(0x40000)) with
            {
                Control = new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, null),
            },
            ReadFirstLane(8, 1, 1),
            BufferLoad(12, 0),
            EndProgram(20));
        var plan = Extract(program);

        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs([0x1000, 0x20, 8, 0x16204]), out var descriptor));
        Assert.Equal(0x40020u, descriptor.Dwords[1]);
    }

    [Fact]
    public void BufferWithDeviceRecordCount_KeepsTheCountOnTheDevice()
    {
        var plan = Extract(Program(
            BufferLoad(0, 0),
            ReadFirstLane(8, 10, 4),
            MoveScalarRegister(12, 8, 0),
            MoveScalarRegister(16, 9, 1),
            MoveScalar(20, 11, 0x16204),
            BufferStore(24, 8),
            EndProgram(32)));

        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.Single(plan.Info.Buffers);
        Assert.True(plan.Memory.Find(24)!.DeviceDescriptor);
    }

    [Theory]
    [InlineData(0u, 5u, 0x1234u, 0x12340000u)]
    [InlineData(1u, 5u, 0x8001u, 0x80010000u)]
    [InlineData(1u, 4u, 0x8001u, 0xFFFF8001u)]
    [InlineData(2u, 5u, 0x1234u, 0x12345678u)]
    public void SdwaConditionalDescriptor_PlacesTheDestinationOnce(
        uint unused, uint destination, uint source, uint expected)
    {
        var program = Program(
            Vop1(0, "VMovB32", 1, Operand(0x5678)),
            Vop2(4, "VCndmaskB32", 1, Operand(source), Operand(source)) with
            {
                Control = new Gen5SdwaControl(destination, unused, 4, 4,
                    false, false, 0, 0, 0, false, null),
            },
            ReadFirstLane(12, 2, 1),
            BufferLoad(16, 0),
            EndProgram(24));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs([0x1000, 0, 8, 0x16204]), out var descriptor));
        Assert.Equal(expected, descriptor.Dwords[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeviceRecordCountLoop_ValidatesEveryIncomingValue(bool invalidIncomingValue)
    {
        var program = Program(
            BufferLoad(0, 0),
            ReadFirstLane(8, 10, 4),
            MoveScalarRegister(12, 8, 0),
            MoveScalarRegister(16, 9, 1),
            MoveScalar(20, 11, 0x16204),
            MoveScalar(24, 12, 0),
            BufferStore(28, 8),
            invalidIncomingValue ? MoveScalarRegister(36, 10, 90) : Nop(36),
            Sop2(40, "SAddU32", 12, Gen5Operand.Scalar(12), Operand(1)),
            Sopc(44, "SCmpLtU32", Gen5Operand.Scalar(12), Operand(2)),
            Branch(48, "SCbranchScc1", -6),
            EndProgram(52));

        if (invalidIncomingValue)
        {
            Assert.Throws<ResourcePlanException>(() => Extract(program));
        }
        else
        {
            var plan = Extract(program);
            Assert.True(plan.Memory.Find(28)!.DeviceDescriptor);
            Assert.True(plan.Info.UsesDeviceAddresses);
        }
    }

    private static uint[] StorageDescriptorUserData(uint mipBase, uint mipLast) =>
    [
        0x1000, Format32x4Float << 20, 3 | (3 << 14), IdentitySwizzle | (mipBase << 12) | (mipLast << 16) | (ImageType2D << 28), 0, 3 << 4, 0, 0, 2,
    ];

    [Fact]
    public void DynamicStorageMipTracking()
    {
        var program = Program(
            Image(4, "ImageStore", 0),
            Vop1(12, "VMovB32", 2, Operand(1)),
            Image(16, "ImageStoreMip", 0),
            Vop1(24, "VMovB32", 2, Operand(2)),
            Image(28, "ImageStoreMip", 0),
            Vop1(36, "VMovB32", 2, Gen5Operand.Scalar(8)),
            Image(40, "ImageStoreMip", 0),
            EndProgram(48));
        var plan = Extract(program);

        var images = plan.Info.Images;
        Assert.Equal(2, images.Count);
        Assert.Equal(ImageMipMode.None, images[0].MipMode);
        Assert.Equal(1u, images[0].MipCount);
        Assert.Equal(ImageMipMode.DynamicStorage, images[1].MipMode);
        Assert.Equal(1u, images[1].MipCount);
        Assert.Equal(0u, plan.Memory.Find(4)!.Resource);
        Assert.Equal(1u, plan.Memory.Find(16)!.Resource);
        Assert.Equal(1u, plan.Memory.Find(28)!.Resource);
        Assert.Equal(1u, plan.Memory.Find(40)!.Resource);

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(StorageDescriptorUserData(1, 3)), ref snapshot, ref specialization));
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(3u, applied.Info.Images[1].MipCount);
        Assert.Equal(applied.Info.Images.Count, snapshot.Images.Length);
        var layout = BindingLayout.Allocate(applied.Info, [], false, false, false);
        var storageKind = ImageDescriptorBinding.ForImage(applied.Info.Images[0]);
        Assert.NotNull(storageKind);
        var storageBinding = layout.Find(storageKind!.Value);
        Assert.NotNull(storageBinding);
        Assert.Equal([0u, 1u, 1u, 1u], storageBinding!.Resources);

        var nullProgram = Program(
            [.. Enumerable.Range(0, 8).Select(index => MoveScalar((uint)index * 4, 16 + (uint)index, 0)),
             Vop1(32, "VMovB32", 2, Gen5Operand.Scalar(0)),
             Image(36, "ImageStoreMip", 16),
             EndProgram(44)]);
        var nullPlan = Extract(nullProgram);
        var nullSnapshot = new ResourceSnapshot();
        var nullSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(nullPlan, Inputs([0]), ref nullSnapshot, ref nullSpecialization));
        Assert.Equal(1u, ResourceMaterializer.ApplyTo(nullPlan, nullSpecialization).Info.Images[0].MipCount);
        Assert.Single(nullSnapshot.Images);

        var changedSnapshot = new ResourceSnapshot();
        var changedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(StorageDescriptorUserData(1, 2)), ref changedSnapshot, ref changedSpecialization));
        Assert.NotEqual(specialization, changedSpecialization);

        var validSnapshot = changedSnapshot;
        var validSpecialization = changedSpecialization;
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs(StorageDescriptorUserData(4, 3)), ref changedSnapshot, ref changedSpecialization));
        Assert.Same(validSnapshot, changedSnapshot);
        Assert.Same(validSpecialization, changedSpecialization);
    }

    // The material table s[0:3], the heap s[4:7], the key selector in s8; the image words
    // come from the heap record the key selects.
    internal static Gen5ShaderProgram IndirectImageProgram(bool malformed, int materialImmediate = 0, bool memoryBackedMaterial = false, bool laneKey = false, (string Opcode, uint Value)? selectorClamp = null)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0x1000;
        void Add(Gen5ShaderInstruction instruction) { instructions.Add(instruction); pc += 8; }
        Gen5ShaderInstruction At(Func<uint, Gen5ShaderInstruction> make) => make(pc);

        if (memoryBackedMaterial)
        {
            Add(At(current => ScalarLoad(current, 9, destination: 28)));
            Add(At(current => MoveScalar(current, 29, 0)));
            Add(At(current => ScalarLoad(current, 28, destination: 0)));
        }

        Add(At(current => Vop1(current, "VMovB32", 1, laneKey ? Gen5Operand.Vector(0) : Gen5Operand.Scalar(8))));
        if (selectorClamp is { } clamp)
        {
            Add(At(current => clamp.Opcode == "VMed3U32" ?
                Vop3(current, "VMed3U32", 1, Operand(0), Operand(clamp.Value), Gen5Operand.Vector(1)) :
                Vop2(current, clamp.Opcode, 1, Operand(clamp.Value), Gen5Operand.Vector(1))));
        }

        Add(At(current => ReadFirstLane(current, 9, 1)));
        Add(At(current => Sop2(current, "SMulI32", 10, Gen5Operand.Scalar(9), Operand(224))));
        Add(At(current => Sop2(current, "SAddU32", 11, Gen5Operand.Scalar(10), Operand(4))));
        Add(At(current => ScalarBufferLoad(current, 0, destination: 12, immediateOffset: materialImmediate, dynamicOffsetRegister: 11)));
        Add(At(current => Sop2(current, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5))));
        if (malformed)
        {
            Add(At(current => ScalarBufferLoad(current, 4, destination: 16, count: 4, dynamicOffsetRegister: 13)));
            Add(At(current => ScalarBufferLoad(current, 4, destination: 20, count: 2, immediateOffset: 16, dynamicOffsetRegister: 13)));
            Add(At(current => ScalarBufferLoad(current, 4, destination: 22, immediateOffset: 24, dynamicOffsetRegister: 13)));
            Add(At(current => ScalarBufferLoad(current, 4, destination: 23, immediateOffset: 32, dynamicOffsetRegister: 13)));
        }
        else
        {
            Add(At(current => ScalarBufferLoad(current, 4, destination: 16, count: 8, dynamicOffsetRegister: 13)));
        }

        for (uint index = 0; index < 4; index++)
        {
            Add(At(current => MoveScalar(current, 24 + index, 0)));
        }

        Add(At(current => Image(current, "ImageSample", 16, 24)));
        Add(At(EndProgram));
        return Program([.. instructions]);
    }

    internal static uint[] ImageDescriptor() =>
        [0x20, Format32x4Float << 20, 3 | (3 << 14), IdentitySwizzle | (ImageType2D << 28), 0, 0, 0, 0];

    internal static TestWordMemory LinearMemory() => new() { Base = 0x1000, Words = new uint[0x2200 / 4], RequireAlignment = true };

    internal static void WriteImage(TestWordMemory memory, ulong address, uint[] descriptor)
    {
        for (var dword = 0; dword < descriptor.Length; dword++)
        {
            memory.At(address + (ulong)dword * 4) = descriptor[dword];
        }
    }

    [Fact]
    public void InvariantIndirectImageMaterialization()
    {
        var plan = Extract(IndirectImageProgram(false));
        Assert.Single(plan.Info.Buffers);
        Assert.Single(plan.Info.Images);
        Assert.Single(plan.DynamicReads);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.NotNull(source.IndirectImage);
        Assert.Single(plan.IndirectImages);
        Assert.Equal(ScalarValueKind.ScalarBufferWord, plan.IndirectImages[0].Key.Kind);

        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        var memory = LinearMemory();
        var descriptor = ImageDescriptor();
        WriteImage(memory, 0x2000, descriptor);
        WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2020) ^= 1;
        var inputs = Inputs(userData, readCleanMemory: memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Single(snapshot.Images);
        Assert.Equal(descriptor, snapshot.Images[0]);

        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        memory.FailAddress = 0x1004;
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Same(priorSnapshot, snapshot);
        Assert.Same(priorSpecialization, specialization);
        memory.FailAddress = ulong.MaxValue;

        memory.At(0x1000 + 36) = 1;
        for (uint dword = 0; dword < 8; dword++)
        {
            memory.At(0x2000 + dword * 4) = 0;
            memory.At(0x2020 + dword * 4) = 0;
        }

        memory.At(0x2004) = descriptor[1];
        memory.At(0x200C) = descriptor[3];
        memory.At(0x2024) = descriptor[1];
        memory.At(0x202C) = descriptor[3] ^ (1u << 28);
        var nullSnapshot = new ResourceSnapshot();
        var nullSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref nullSnapshot, ref nullSpecialization));
        Assert.All(nullSnapshot.Images[0], word => Assert.Equal(0u, word));

        WriteImage(memory, 0x2000, descriptor);
        WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2020) ^= 1;
        memory.At(0x1000 + 36) = 1;
        var dynamicSnapshot = new ResourceSnapshot();
        var dynamicSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref dynamicSnapshot, ref dynamicSpecialization));
        Assert.Equal(2, dynamicSnapshot.Images.Length);
        Assert.Equal(2, dynamicSpecialization.Images.Count);
        var applied = ResourceMaterializer.ApplyTo(plan, dynamicSpecialization);
        Assert.Equal(2, applied.Info.Images.Count);
        Assert.Equal(0u, applied.Info.Images[0].IndirectRoot);
        Assert.NotEqual(0u, applied.Info.Images[0].IndirectSearchIterations);
        Assert.Equal(2, applied.Info.Images[0].IndirectResources.Count);
        var mapping = dynamicSpecialization.Images[0];
        var keyCount = dynamicSnapshot.FlattenedResourceTable[mapping.IndirectMappingOffset];
        Assert.Equal(2u, mapping.IndirectSearchIterations);
        Assert.Equal((uint)dynamicSnapshot.FlattenedResourceTable.Length, mapping.IndirectMappingOffset + 1 + keyCount * 2);

        WriteImage(memory, 0x2000, descriptor);
        WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2000) += 0x100;
        memory.At(0x2020) += 0x101;
        var reboundSnapshot = new ResourceSnapshot();
        var reboundSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reboundSnapshot, ref reboundSpecialization));
        Assert.Equal(dynamicSpecialization, reboundSpecialization);
        memory.At(0x2020) = memory.At(0x2000);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reboundSnapshot, ref reboundSpecialization));
        Assert.NotEqual(dynamicSpecialization, reboundSpecialization);
        var collapsedSpecialization = reboundSpecialization;

        foreach (var records in new uint[] { 1, 3 })
        {
            userData[2] = records;
            var capacitySnapshot = new ResourceSnapshot();
            var capacitySpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref capacitySnapshot, ref capacitySpecialization));
        }

        userData[2] = 2;
        memory.At(0x2020) = memory.At(0x2000) + 1;
        memory.At(0x2040) = memory.At(0x2000) + 2;
        for (uint dword = 1; dword < 8; dword++)
        {
            memory.At(0x2040 + dword * 4) = descriptor[dword];
        }

        memory.At(0x1000 + 68) = 2;
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref reboundSnapshot, ref reboundSpecialization));
        Assert.NotEqual(collapsedSpecialization, reboundSpecialization);
    }

    [Fact]
    public void IndirectImage_MemoryBackedMaterialReadsThroughTheCleanReader()
    {
        var plan = Extract(IndirectImageProgram(false, memoryBackedMaterial: true));
        Assert.Equal(2, plan.TableReads.Count);
        Assert.All(plan.CleanFlatSlots, slot => Assert.Equal(1, slot));

        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7, 0x3100, 0];
        var memory = LinearMemory();
        WriteImage(memory, 0x2000, ImageDescriptor());
        memory.At(0x3100) = 0x3000;
        memory.At(0x3000) = 0x1000;
        var dirtyReads = 0;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var inputs = Inputs(userData, readMemory: (ulong address, out uint word) => { dirtyReads++; return memory.Read(address, out word); }, readCleanMemory: memory.Read);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal(0, dirtyReads);
        Assert.Equal(ImageDescriptor(), snapshot.Images[0]);

        memory.FailAddress = 0x3100;
        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Same(priorSnapshot, snapshot);
        Assert.Same(priorSpecialization, specialization);
    }

    // A heap record whose last word is not next to the others is not an indirect heap image; its words are still scalar
    // buffer reads at a uniform offset, so the host reads them one by one when the draw is bound.
    [Fact]
    public void MalformedIndirectImage_IsReadWordByWord()
    {
        var plan = Extract(IndirectImageProgram(true));

        var image = Assert.Single(plan.Info.Images);
        var source = plan.DescriptorSources[(int)image.Source];
        Assert.Null(source.IndirectImage);
        Assert.All(source.Dwords, dword => Assert.Equal(ScalarValueKind.ScalarBufferWord, dword.Kind));
    }

    [Fact]
    public void MaterialKeyImmediateIsReadAfterTheWrappedSelectorOffset()
    {
        var plan = Extract(IndirectImageProgram(false, materialImmediate: 4));
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.Equal(4u, selector.SelectorOffset);
        Assert.Equal(4u, selector.MaterialImmediate);

        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        var memory = LinearMemory();
        var first = ImageDescriptor();
        var second = ImageDescriptor();
        second[0] += 1;
        WriteImage(memory, 0x2000, first);
        WriteImage(memory, 0x2020, second);
        // The dynamic offset selects byte 4 of the record; the immediate moves the key to byte 8.
        memory.At(0x1000 + 224 + 8) = 1;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        Assert.Equal([first, second], snapshot.Images);
    }

    [Fact]
    public void ResourceTableFlatteningAndRuntimeMemoization()
    {
        var program = Program(
            ScalarLoad(4, 0, destination: 8, immediateOffset: 4),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 64),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            MoveScalarRegister(28, 12, 8),
            MoveScalar(32, 13, 0),
            MoveScalar(36, 14, 64),
            MoveScalar(40, 15, 0),
            BufferLoad(44, 12),
            EndProgram(48));
        var plan = Extract(program);

        Assert.Single(plan.TableReads);
        Assert.Single(plan.Info.Buffers);
        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory[0].PlanningOnly);

        var memory = new TestWordMemory();
        memory.Words[1] = 0xDEADBEEF;
        var inputs = Inputs([0x1000, 0], readMemory: memory.Read);
        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, [plan.Info.Buffers[0].Source], inputs, [], evaluateTable: true, out var descriptors, out var table));
        Assert.Equal(0xDEADBEEFu, Assert.Single(descriptors).Dwords[0]);
        Assert.Equal([0xDEADBEEFu], table);
        Assert.Equal(1u, memory.Reads);

        memory.Reads = 0;
        memory.FailAfter = 0;
        var snapshot = new ResourceSnapshot { UserData = [1] };
        var specialization = new ResourceSpecialization { Buffers = [new BufferSpecialization(7, 0)] };
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal([1u], snapshot.UserData);
        Assert.Equal(7u, specialization.Buffers[0].DescriptorFormat);

        var layout = BindingLayout.Allocate(plan.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64), false, plan.TableReads.Count != 0, false);
        Assert.NotNull(layout.Find(DescriptorBindingKind.FlattenedResourceTable));
    }

    // An indexed buffer access (idxen) makes the program read its buffers' strides at run time; offsets alone do not.
    [Fact]
    public void AnIndexedBufferAccessReadsTheBufferStridesAtRunTime()
    {
        Assert.True(Extract(Program(BufferAccess(0, "BufferLoadDword", 0, indexEnabled: true, vectorAddress: 1), EndProgram(8)))
            .Info.UsesIndexedBuffers);
        Assert.False(Extract(Program(BufferLoad(0, 0), EndProgram(8))).Info.UsesIndexedBuffers);
    }

    [Fact]
    public void DynamicResourceTableReadRemainsExplicit()
    {
        var program = Program(
            ScalarLoad(4, 0, destination: 8, dynamicOffsetRegister: 2),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 64),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.Empty(plan.TableReads);
        Assert.Single(plan.DynamicReads);
        Assert.True(plan.Info.UsesDeviceAddresses);
        var memory = new TestWordMemory();
        memory.Words[1] = 0xABCDEF01;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0x1000, 0, 4], readMemory: memory.Read), out var descriptor));
        Assert.Equal(0xABCDEF01u, descriptor.Dwords[0]);
        Assert.Equal(1u, memory.Reads);

        var layout = BindingLayout.Allocate(plan.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64), false, plan.TableReads.Count != 0, false);
        Assert.Null(layout.Find(DescriptorBindingKind.FlattenedResourceTable));
        Assert.NotNull(layout.Find(DescriptorBindingKind.DeviceAddressPageTable));
        Assert.NotNull(layout.Find(DescriptorBindingKind.FaultBuffer));
        Assert.Equal((uint)layout.UserDataRegisters.Count, layout.MemoryOffsetDword);
        Assert.Equal(1u, layout.MemoryOffsetCount);
        Assert.Equal(layout.MemoryOffsetDword + 1, layout.ShaderDataDwordCount);
    }

    [Fact]
    public void RuntimeScalarBufferLoad_UsesDeviceAddresses()
    {
        var program = Program(
            ScalarLoad(0, 0, destination: 8, count: 4, dynamicOffsetRegister: 2),
            ScalarBufferLoad(8, 8, destination: 12, count: 1),
            EndProgram(16));
        var plan = Extract(program);

        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.Empty(plan.BufferCandidateTables);
        Assert.True(plan.Memory.TryGetIndex(8, 0, out var index));
        Assert.Equal(BufferDescriptorProvenance.Runtime, plan.Memory[index].BufferDescriptor!.Provenance);
        Assert.Equal(MemoryAccessInfo.NoResource, plan.Memory[index].Resource);

        var request = Request(program);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Fact]
    public void UnboundedRuntimeFormattedBufferLoad_UsesNullReadFallback()
    {
        var program = Program(
            ScalarLoad(0, 0, destination: 8, count: 4, dynamicOffsetRegister: 2),
            ScalarBufferLoad(8, 8, destination: 12, count: 1),
            BufferLoad(16, 8, formatted: true),
            EndProgram(20));
        var plan = Extract(program);

        Assert.Empty(plan.BufferCandidateTables);
        var request = Request(program);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Fact]
    public void PhiValidation()
    {
        var program = Program(
            Branch(0, "SCbranchScc0", 2),
            MoveScalar(4, 12, 1),
            Branch(8, "SBranch", 1),
            MoveScalar(12, 12, 2),
            Sop2(16, "SMinU32", 11, Gen5Operand.Scalar(12), Operand(0x100)),
            MoveScalar(20, 8, 0),
            MoveScalar(24, 9, 0),
            MoveScalar(28, 10, 0),
            BufferLoad(32, 8),
            EndProgram(36));

        // The unevaluated branch picks the V#, so the access reads it from its registers.
        var plan = Extract(program);
        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Memory.TryGetIndex(32, 0, out var index));
        Assert.True(plan.Memory[index].DeviceDescriptor);
    }

    // A branch the plan cannot evaluate picks the V#: a descriptor loaded from memory, or one built from a lane value.
    private static Gen5ShaderProgram PhiSelectedDescriptor(string opcode = "BufferLoadDwordx3") => Program(
        Branch(0, "SCbranchScc1", 3),
        ScalarLoad(4, 2, 8, count: 4), Branch(12, "SBranch", 4),
        ReadFirstLane(16, 8, 0), MoveScalar(20, 9, 0), MoveScalar(24, 10, 64), MoveScalar(28, 11, 0),
        BufferAccess(32, opcode, 8, dwords: 3),
        EndProgram(40));

    [Fact]
    public void DescriptorChosenByControlFlow_ReadsThroughRegisters()
    {
        var plan = Extract(PhiSelectedDescriptor());

        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory.TryGetIndex(32, 0, out var index));
        Assert.True(plan.Memory[index].DeviceDescriptor);
    }

    [Fact]
    public void DescriptorWalkedByALoop_ReadsThroughRegisters()
    {
        // Each iteration loads the next V# through the base of the current one.
        var plan = Extract(Program(
            ScalarLoad(0, 2, 8, count: 4),
            BufferLoad(8, 8, dwords: 3),
            ScalarLoad(16, 8, 8, count: 4),
            Branch(24, "SCbranchScc1", -5),
            EndProgram(32)));

        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory.TryGetIndex(8, 0, out var index));
        // main reads a V# loaded from scalar-buffer data through its registers (IsDeviceLoadedBufferHandle).
        Assert.True(plan.Memory[index].DeviceDescriptor);
    }

    [Fact]
    public void DescriptorWalkedByALoopFromALaneValue_IsNeverBoundStatically()
    {
        var plan = Extract(Program(
            ReadFirstLane(0, 8, 0), MoveScalar(4, 9, 0), MoveScalar(8, 10, 64), MoveScalar(12, 11, 0),
            BufferLoad(16, 8, dwords: 3),
            ScalarLoad(24, 8, 8, count: 4),
            Branch(32, "SCbranchScc1", -5),
            EndProgram(36)));

        Assert.Empty(plan.Info.Buffers);
        Assert.True(plan.Memory.TryGetIndex(16, 0, out var index));
        Assert.True(plan.Memory[index].DeviceDescriptor ||
            plan.Memory[index].BufferDescriptor?.Provenance == BufferDescriptorProvenance.Runtime);
    }

    [Fact]
    public void DescriptorChosenByControlFlow_KeepsTypedAccessRejected()
    {
        var error = Assert.ThrowsAny<ResourcePlanException>(() => Extract(PhiSelectedDescriptor("TBufferLoadFormatXyz")));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    [Fact]
    public void StaticDescriptor_StaysHostBound()
    {
        var plan = Extract(Program(
            MoveScalar(0, 8, 0x1000), MoveScalar(4, 9, 0), MoveScalar(8, 10, 256), MoveScalar(12, 11, 0),
            BufferLoad(16, 8, dwords: 3), EndProgram(24)));
        var userDataPlan = Extract(Program(BufferLoad(0, 0, dwords: 3), EndProgram(8)));

        foreach (var candidate in new[] { plan, userDataPlan })
        {
            Assert.Single(candidate.Info.Buffers);
            Assert.DoesNotContain(candidate.Memory.Entries, memory => memory.DeviceDescriptor);
        }
    }

    [Fact]
    public void LoopCycleEnteredThroughRuntimeValue()
    {
        var program = Program(
            Sop2(0, "SAndB32", 0, Gen5Operand.Scalar(0), Operand(0xFFFFFFFF)),
            Branch(4, "SCbranchScc1", -2),
            BufferLoad(8, 0),
            EndProgram(12));

        var graph = ScalarValueGraph.Build(program, 0, 4);
        var reads = ResourceTableReadPlanner.Plan(graph, ShaderStage.Compute, Hash);
        Assert.Empty(reads.Reads);
    }

    [Fact]
    public void InvariantLoopPhi()
    {
        var program = Program(
            Nop(0),
            Branch(4, "SCbranchScc1", -2),
            BufferLoad(8, 0),
            EndProgram(12));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0x12345678, 0, 0, 0]), out var descriptor));
        Assert.Equal(0x12345678u, descriptor.Dwords[0]);
    }

    [Fact]
    public void DeviceAddressMaterialization()
    {
        var program = Program(
            GlobalAccess(4, "GlobalLoadDword", 0, offset: -8),
            GlobalAccess(8, "FlatStoreDword", 0, vectorAddress: 2),
            EndProgram(16));
        var plan = Extract(program);

        Assert.True(plan.Info.UsesDeviceAddresses);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x2008, 0]), ref snapshot, ref specialization));
        ResourceMaterializer.ApplyTo(plan, specialization);
    }

    [Fact]
    public void DynamicFlatAddressesUseDeviceAddresses()
    {
        var program = Program(
            Vop1(0, "VMovB32", 10, Operand(0)),
            Vopc(4, "VCmpNeU32", Gen5Operand.Scalar(2), 10),
            Sop1(8, "SAndSaveexecB64", 6, Gen5Operand.Scalar(106)),
            Vop1(12, "VMovB32", 0, Gen5Operand.Scalar(0)),
            Vop1(16, "VMovB32", 1, Gen5Operand.Scalar(1)),
            GlobalAccess(0xA4, "FlatLoadUbyte", 0),
            EndProgram(0xAC));
        var plan = Extract(program);

        Assert.True(plan.Info.UsesDeviceAddresses);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x23456780, 1, 1]), ref snapshot, ref specialization));
    }

    [Fact]
    public void BufferSwizzleSpecialization()
    {
        var program = Program(BufferLoad(4, 0, formatted: true), EndProgram(12));
        var plan = Extract(program);
        const uint swizzle = 4 | (5 << 3) | (0 << 6) | (1 << 9);
        uint[] userData = [0, 16 << 16, 1, swizzle | (Format32x2Float << 12) | (1u << 24)];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(swizzle, applied.Info.Buffers[0].DescriptorSwizzle);
        Assert.Equal(swizzle, specialization.Buffers[0].DescriptorSwizzle);

        userData[3] ^= 1u << 9;
        var changedSnapshot = new ResourceSnapshot();
        var changedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref changedSnapshot, ref changedSpecialization));
        Assert.NotEqual(specialization, changedSpecialization);
    }

    [Fact]
    public void BuffersPastTheBindingBudget_ReadTheirDescriptorsOnTheDevice()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (uint index = 0; index <= ShaderResourceInfo.MaxBuffers; index++)
        {
            for (uint dword = 0; dword < 4; dword++)
            {
                instructions.Add(MoveScalar(pc, 8 + dword, index + dword + 0x100));
                pc += 8;
            }

            instructions.Add(BufferLoad(pc, 8));
            pc += 8;
        }

        instructions.Add(EndProgram(pc));
        var plan = Extract(Program([.. instructions]));

        Assert.Equal(ShaderResourceInfo.MaxBuffers, plan.Info.Buffers.Count);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.Single(plan.Memory.Entries, memory => memory.DeviceDescriptor);
    }

    [Fact]
    public void GlobalDataShareAccess_AllocatesTheBinding()
    {
        var program = Program(DataShareWrite(0, gds: true), EndProgram(8));
        var plan = Extract(program);
        var layout = BindingLayout.Allocate(plan.Info, [], BindingLayout.UsesGlobalDataShare(program), false, false);

        Assert.NotNull(layout.Find(DescriptorBindingKind.GlobalDataShare));
        Assert.Null(BindingLayout.Allocate(plan.Info, [], BindingLayout.UsesGlobalDataShare(Program(DataShareWrite(0, gds: false), EndProgram(8))), false, false).Find(DescriptorBindingKind.GlobalDataShare));
    }
}
