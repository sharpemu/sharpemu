// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DirectImageTableTests
{
    [Theory]
    [InlineData(0u, false, true, 0)]
    [InlineData(0u, true, true, 0)]
    [InlineData(1u, false, true, 1)]
    [InlineData(6u, false, true, 6)]
    [InlineData(256u, false, true, 256)]
    [InlineData(257u, false, false, 0)]
    [InlineData(uint.MaxValue, false, false, 0)]
    [InlineData(1u, true, false, 0)]
    public void RuntimeGuardUsesItsCurrentBoundAndRejectsUnreadableOrOversizedTables(
        uint count, bool unreadable, bool accepted, int expectedCandidates)
    {
        var program = Program([
            MoveScalar(0, 8, 0),
            Sopc(4, "SCmpLtU32", Gen5Operand.Scalar(8), Gen5Operand.Scalar(2)),
            Sop2(8, "SCselectB64", 106, Gen5Operand.Scalar(126), Operand(0)),
            Branch(12, "SCbranchVccz", 10),
            Sop2(16, "SMulI32", 10, Gen5Operand.Scalar(8), Operand(64)),
            ScalarLoad(20, 0, 16, 8, dynamicOffsetRegister: 10),
            ScalarLoad(28, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 10),
            Image(36, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1),
            Sop2(44, "SAddI32", 8, Gen5Operand.Scalar(8), Operand(1)),
            Branch(48, "SBranch", -12), EndProgram(56),
        ]);
        var plan = Extract(program, userDataCount: 3);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.NotNull(source.IndirectImage?.CandidateCountSource);
        bool Read(ulong address, out uint word)
        {
            Assert.NotEqual(0u, count);
            word = 0;
            if (unreadable || address < 0x1000 || address >= 0x1000 + count * 64UL) return false;
            var record = (uint)(address - 0x1000) / 64;
            var component = (uint)(address - 0x1000) % 64 / 4;
            word = component switch
            {
                0 => 0x2000u + record * 0x100,
                1 => 20u << 20,
                3 => 0xFACu | (9u << 28),
                9 => 0xFFF000,
                _ => 0,
            };
            return true;
        }
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var prior = snapshot;
        var succeeded = ResourceMaterializer.Materialize(plan,
            Inputs([0x1000, 0, count], readCleanMemory: Read), ref snapshot, ref specialization);
        Assert.Equal(accepted, succeeded);
        if (!accepted)
        {
            Assert.Same(prior, snapshot);
            return;
        }
        Assert.Equal(expectedCandidates > 1, specialization.Images[0].IndirectSearchIterations != 0);
        Assert.Equal(count == 0 ? 0u : 0xFFF000u, snapshot.Samplers[0][1]);
        if (count == 0) Assert.All(snapshot.Images[0], word => Assert.Equal(0u, word));
        if (expectedCandidates == 1) Assert.Equal(0x2000u, snapshot.Images[0][0]);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 3), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out _, out var error), error);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void RuntimeBoundRequiresTheDescriptorLoadOnOnlyTheAdmittedGuardPath(
        bool wrongKey, bool rejectedEdgeRejoins, bool inverted, bool accepted)
    {
        var program = Program([
            MoveScalar(0, 8, 0),
            Sopc(4, "SCmpLtU32", Gen5Operand.Scalar(wrongKey ? 9u : 8u), Gen5Operand.Scalar(2)),
            Sop2(8, "SCselectB64", 106, Gen5Operand.Scalar(126), Operand(0)),
            Branch(12, inverted ? "SCbranchVccnz" : "SCbranchVccz", (short)(rejectedEdgeRejoins ? 0 : 10)),
            Sop2(16, "SMulI32", 10, Gen5Operand.Scalar(8), Operand(64)),
            ScalarLoad(20, 0, 16, 8, dynamicOffsetRegister: 10),
            ScalarLoad(28, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 10),
            Image(36, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1),
            Sop2(44, "SAddI32", 8, Gen5Operand.Scalar(8), Operand(1)),
            Branch(48, "SBranch", -12), EndProgram(56),
        ]);
        if (!accepted)
        {
            Assert.Throws<ResourcePlanException>(() => Extract(program, userDataCount: 3));
            return;
        }
        var plan = Extract(program, userDataCount: 3);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.NotNull(source.IndirectImage?.CandidateCountSource);
    }

    [Fact]
    public void RuntimeBoundCacheRechecksChangedDescriptorWords()
    {
        var program = Program([
            MoveScalar(0, 8, 0),
            Sopc(4, "SCmpLtU32", Gen5Operand.Scalar(8), Gen5Operand.Scalar(2)),
            Sop2(8, "SCselectB64", 106, Gen5Operand.Scalar(126), Operand(0)),
            Branch(12, "SCbranchVccz", 10),
            Sop2(16, "SMulI32", 10, Gen5Operand.Scalar(8), Operand(64)),
            ScalarLoad(20, 0, 16, 8, dynamicOffsetRegister: 10),
            ScalarLoad(28, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 10),
            Image(36, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1),
            Sop2(44, "SAddI32", 8, Gen5Operand.Scalar(8), Operand(1)),
            Branch(48, "SBranch", -12), EndProgram(56),
        ]);
        var plan = Extract(program, userDataCount: 3);
        var cache = new ResourceMaterializationCache();
        var firstDescriptorBase = 0x2000u;
        var secondSamplerWord = 0xFFF000u;
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x1080) return false;
            var record = (uint)(address - 0x1000) / 64;
            var component = (uint)(address - 0x1000) % 64 / 4;
            word = component switch
            {
                0 => record == 0 ? firstDescriptorBase : 0x2100u,
                1 => 20u << 20,
                3 => 0xFACu | (9u << 28),
                9 => record == 1 ? secondSamplerWord : 0xFFF000,
                _ => 0,
            };
            return true;
        }
        bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            for (var offset = 0; offset < destination.Length; offset += sizeof(uint))
            {
                if (!Read(address + (uint)offset, out var word)) return false;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }
            return true;
        }
        ResourceSnapshot Materialize()
        {
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(cache.Materialize(plan, Inputs([0x1000, 0, 2], readCleanMemory: Read), ReadResident,
                ref snapshot, ref specialization, out _));
            return snapshot;
        }

        var first = Materialize();
        Assert.Equal(0x2000u, first.Images[0][0]);
        Assert.Same(first, Materialize());
        firstDescriptorBase = 0x2200;
        var changed = Materialize();
        Assert.NotSame(first, changed);
        Assert.Equal(0x2200u, changed.Images[0][0]);
        Assert.Equal((1, 2), (cache.Hits, cache.Misses));
        Assert.Equal(0xFFF000u, changed.Samplers[0][1]);
        secondSamplerWord = 0xFFE000;
        var rejected = changed;
        var rejectedSpecialization = new ResourceSpecialization();
        Assert.False(cache.Materialize(plan, Inputs([0x1000, 0, 2], readCleanMemory: Read), ReadResident,
            ref rejected, ref rejectedSpecialization, out _));
        Assert.Same(changed, rejected);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void RuntimeBoundsRejectPotentialDescriptorWrites(bool shaderWrites, bool otherStageWrites, bool accepted)
    {
        var plan = ShaderResourcePlan.Extract(RuntimeMemoryBoundProgram(shaderWrites), ShaderStage.Pixel, Hash, 0, 6);
        Assert.Contains(plan.DescriptorSources, source => source.IndirectImage?.CandidateCountSource is not null);
        bool Read(ulong address, out uint word) => ReadRuntimeBoundMemory(address, 2, out word);
        var inputs = new ResourceRuntimeInputs
        {
            UserData = [0x1000, 0, 0, 0, 0x8000, 0], ReadMemory = Read, ReadCleanMemory = Read,
            OtherStageMayWriteMemory = otherStageWrites,
        };
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var previous = snapshot;
        Assert.Equal(accepted, ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        if (!accepted) Assert.Same(previous, snapshot);
    }

    [Fact]
    public void RuntimeCountCacheRechecksMemoryBoundsAndWriterContext()
    {
        var plan = ShaderResourcePlan.Extract(RuntimeMemoryBoundProgram(), ShaderStage.Pixel, Hash, 0, 6);
        var count = 1u;
        var unreadable = false;
        bool Read(ulong address, out uint word)
        {
            if (unreadable && address == 0x8000) { word = 0; return false; }
            return ReadRuntimeBoundMemory(address, count, out word);
        }
        ResourceRuntimeInputs Inputs(bool writes = false) => new()
        {
            UserData = [0x1000, 0, 0, 0, 0x8000, 0], ReadMemory = Read, ReadCleanMemory = Read,
            OtherStageMayWriteMemory = writes,
        };
        bool Resident(ulong address, Span<byte> bytes, bool clean)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!Read(address + (uint)offset, out var word)) return false;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], word);
            }
            return true;
        }
        var cache = new ResourceMaterializationCache();
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        Assert.True(cache.Materialize(plan, Inputs(), Resident, ref snapshot, ref specialization, out _));
        Assert.Single(snapshot.Images);
        Assert.True(cache.Materialize(plan, Inputs(), Resident, ref snapshot, ref specialization, out _));
        count = 2;
        Assert.True(cache.Materialize(plan, Inputs(), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(2, snapshot.Images.Length);
        count = 0;
        Assert.True(cache.Materialize(plan, Inputs(), Resident, ref snapshot, ref specialization, out _));
        Assert.All(snapshot.Images[0], word => Assert.Equal(0u, word));
        count = 3;
        Assert.True(cache.Materialize(plan, Inputs(), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(3, snapshot.Images.Length);
        var verified = snapshot;
        Assert.False(cache.Materialize(plan, Inputs(true), Resident, ref snapshot, ref specialization, out _));
        Assert.Same(verified, snapshot);
        unreadable = true;
        Assert.False(cache.Materialize(plan, Inputs(), Resident, ref snapshot, ref specialization, out _));
        Assert.Same(verified, snapshot);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void RuntimeBoundsRejectInvalidDescriptorsInsteadOfSubstitutingNull()
    {
        var plan = ShaderResourcePlan.Extract(RuntimeMemoryBoundProgram(), ShaderStage.Pixel, Hash, 0, 6);
        bool Read(ulong address, out uint word)
        {
            if (!ReadRuntimeBoundMemory(address, 2, out word)) return false;
            if (address == 0x1044) word = 0;
            return true;
        }
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        Assert.False(ResourceMaterializer.Materialize(plan,
            Inputs([0x1000, 0, 0, 0, 0x8000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        Assert.Empty(snapshot.Images);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeBoundPreservesConstantFieldOffsetsAndRejectsVaryingOffsets(bool varying)
    {
        var program = Program([
            ScalarLoad(0, 4, 2), MoveScalar(8, 8, 0),
            Sopc(12, "SCmpLtU32", Gen5Operand.Scalar(8), Gen5Operand.Scalar(2)),
            Sop2(16, "SCselectB64", 106, Gen5Operand.Scalar(126), Operand(0)),
            Branch(20, "SCbranchVccz", 10),
            Sop2(24, "SMulI32", 10, Gen5Operand.Scalar(8), Operand(64)),
            Sop2(28, "SAddI32", 10, Gen5Operand.Scalar(10), varying ? Gen5Operand.Scalar(3) : Operand(32)),
            ScalarLoad(32, 0, 16, 8, dynamicOffsetRegister: 10),
            ScalarLoad(40, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 10),
            Image(48, "ImageSampleLz", 16, 24, dmask: 1),
            Sop2(56, "SAddI32", 8, Gen5Operand.Scalar(8), Operand(1)),
            Branch(60, "SBranch", -13), EndProgram(64),
        ]);
        if (varying)
        {
            Assert.Throws<ResourcePlanException>(() => Extract(program, userDataCount: 6));
            return;
        }
        var plan = Extract(program, userDataCount: 6);
        bool Read(ulong address, out uint word) =>
            ReadRuntimeBoundMemory(address == 0x8000 ? address : address - 32, 2, out word);
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan,
            Inputs([0x1000, 0, 0, 0, 0x8000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        Assert.Equal(2, snapshot.Images.Length);
        Assert.Equal(0xFFF000u, snapshot.Samplers[0][1]);
    }

    [Theory]
    [InlineData(0x9000UL, false, false, true)]
    [InlineData(0x9000UL, true, false, true)]
    [InlineData(0x1001UL, false, false, false)]
    [InlineData(0x1001UL, true, false, false)]
    [InlineData(0x8001UL, false, false, false)]
    [InlineData(0x8041UL, false, false, false)]
    [InlineData(0x9000UL, false, true, false)]
    public void RuntimeDescriptorsRequireDisjointReadableImageWrites(
        ulong outputAddress, bool cached, bool unreadableOutput, bool accepted)
    {
        var instructions = RuntimeMemoryBoundProgram().Instructions;
        var program = Program([
            .. instructions.Take(instructions.Count - 1),
            ScalarLoad(60, 4, 32, 8, immediateOffset: 64),
            Image(68, "ImageStore", 32, dmask: 1), EndProgram(76),
        ]);
        var plan = Extract(program, userDataCount: 6);
        bool Read(ulong address, out uint word)
        {
            if (address >= 0x8040 && address < 0x8060)
            {
                word = (address - 0x8040) switch
                {
                    0 => 0x90, 4 => 20u << 20, 12 => 0xFACu | (9u << 28), _ => 0,
                };
                return !unreadableOutput;
            }
            return ReadRuntimeBoundMemory(address, 2, out word);
        }
        bool Range(ReadOnlySpan<uint> words, out ulong address, out ulong size)
        {
            Assert.Equal(0x90u, words[0]);
            address = outputAddress;
            size = 32;
            return true;
        }
        bool Resident(ulong address, Span<byte> bytes, bool clean)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!Read(address + (uint)offset, out var word)) return false;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], word);
            }
            return true;
        }
        var inputs = new ResourceRuntimeInputs
        {
            UserData = [0x1000, 0, 0, 0, 0x8000, 0], ReadMemory = Read, ReadCleanMemory = Read,
            ReadImageWriteRange = Range,
        };
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var original = snapshot;
        var cache = new ResourceMaterializationCache();
        var result = cached
            ? cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _)
            : ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization);
        Assert.Equal(accepted, result);
        if (!accepted) Assert.Same(original, snapshot);
        else if (cached)
        {
            // A changed allocation result must be checked even when words match.
            outputAddress = 0x1001;
            original = snapshot;
            Assert.False(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
            Assert.Same(original, snapshot);
            Assert.Equal(0, cache.Hits);
        }
    }

    [Theory]
    [InlineData(0x9000UL, 0u, false, true)]
    [InlineData(0x1001UL, 0u, false, false)]
    [InlineData(0x8001UL, 0u, false, false)]
    [InlineData(0x8041UL, 0u, false, false)]
    [InlineData(0x0FC0UL, 0u, false, false)]
    [InlineData(0x9000UL, 0x80000000u, false, false)]
    [InlineData(0x9000UL, 0u, true, false)]
    public void RuntimeDescriptorsRequireDisjointReadableBufferWrites(
        ulong outputAddress, uint flags, bool unreadable, bool accepted)
    {
        var instructions = RuntimeMemoryBoundProgram().Instructions;
        var program = Program([
            .. instructions.Take(instructions.Count - 1),
            ScalarLoad(60, 4, 32, 4, immediateOffset: 64),
            BufferAccess(68, "BufferStoreDwordx4", 32, offset: 12, dwords: 4,
                indexEnabled: true, vectorAddress: 0), EndProgram(76),
        ]);
        var plan = Extract(program, userDataCount: 6);
        bool Read(ulong address, out uint word)
        {
            if (address >= 0x8040 && address < 0x8050)
            {
                word = (address - 0x8040) switch
                {
                    0 => (uint)outputAddress,
                    4 => (uint)(outputAddress >> 32) | (4u << 16) | flags,
                    8 => 16,
                    12 => 0xFAC,
                    _ => 0,
                };
                return !unreadable;
            }
            return ReadRuntimeBoundMemory(address, 2, out word);
        }
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var original = snapshot;
        Assert.Equal(accepted, ResourceMaterializer.Materialize(plan,
            new ResourceRuntimeInputs { UserData = [0x1000, 0, 0, 0, 0x8000, 0], ReadMemory = Read, ReadCleanMemory = Read },
            ref snapshot, ref specialization));
        if (!accepted) Assert.Same(original, snapshot);
    }

    [Fact]
    public void ZeroRuntimeCountCannotHideAnImageConsumerAfterTheLoop()
    {
        var instructions = RuntimeMemoryBoundProgram().Instructions;
        var program = Program([
            .. instructions.Take(instructions.Count - 1),
            Image(60, "ImageSampleLz", 16, 24, dmask: 1), EndProgram(68),
        ]);
        ShaderResourcePlan plan;
        try { plan = Extract(program, userDataCount: 6); }
        catch (ResourcePlanException exception)
        {
            Assert.Contains("not a valid runtime value", exception.Message);
            return;
        }
        bool Read(ulong address, out uint word) => ReadRuntimeBoundMemory(address, 0, out word);
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var original = snapshot;
        Assert.False(ResourceMaterializer.Materialize(plan,
            Inputs([0x1000, 0, 0, 0, 0x8000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        Assert.Same(original, snapshot);
    }

    [Theory]
    [InlineData(255u, 31u, true)]
    [InlineData(uint.MaxValue, 31u, true)]
    [InlineData(254u, 31u, false)]
    [InlineData(255u, 64u, true)]
    [InlineData(255u, uint.MaxValue, true)]
    public void ReductionInputRequiresFullWaveInitializationAndAValidLane(uint neutral, uint lane, bool accepted)
    {
        var plan = Extract(Program([
            Sop2(0, "SAndB64", 12, Gen5Operand.Scalar(126), Gen5Operand.Scalar(10)),
            Sop1(4, "SOrn2SaveexecB64", 106, Gen5Operand.Scalar(126)),
            Vop3(8, "VCndmaskB32", 5, Operand(neutral), Gen5Operand.Vector(3), Gen5Operand.Scalar(12)),
            Vop2(16, "VMinU32", 5, Gen5Operand.Vector(5), Gen5Operand.Vector(5)),
            Sop1(20, "SMovB64", 126, Gen5Operand.Scalar(106)),
            new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(5), Operand(lane), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(20)],
                new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            EndProgram(32),
        ]));
        Assert.Equal(accepted, IndirectSelectorValues.TryGetInitializedReductionInput(plan, 24, out var input, out var before));
        if (accepted) { Assert.Equal(Gen5Operand.Vector(3), input); Assert.Equal(4u, before); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ReductionCanReadFullyInitializedLanesBeforeRestoringExecution(bool matchingRestore, bool accepted)
    {
        var plan = Extract(Program([
            Sop2(0, "SAndB64", 12, Gen5Operand.Scalar(126), Gen5Operand.Scalar(10)),
            Sop1(4, "SOrn2SaveexecB64", 106, Gen5Operand.Scalar(126)),
            Vop3(8, "VCndmaskB32", 5, Operand(255), Gen5Operand.Vector(3), Gen5Operand.Scalar(12)),
            Vop2(16, "VMinU32", 5, Gen5Operand.Vector(5), Gen5Operand.Vector(5)),
            new Gen5ShaderInstruction(20, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(5), Operand(31), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(20)],
                new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            Sop1(28, "SMovB64", 126, Gen5Operand.Scalar(matchingRestore ? 106u : 104u)),
            EndProgram(32),
        ]));
        Assert.Equal(accepted, IndirectSelectorValues.TryGetInitializedReductionInput(plan, 20, out _, out _));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void PackedLoopDomainRejectsArithmeticAndEntryBypasses(bool arithmetic, bool bypass, bool accepted)
    {
        var plan = Extract(Program([
            Vop1(0, "VMovB32", 0, Operand(0xFF332211)),
            bypass ? Branch(4, "SCbranchScc1", 1) : Nop(4),
            Nop(8),
            Vop2(12, arithmetic ? "VAddU32" : "VLshrrevB32", 0, Operand(8), Gen5Operand.Vector(0)),
            Branch(16, "SBranch", -3),
            EndProgram(20),
        ]));
        Assert.Equal(accepted, IndirectSelectorValues.TryGetPackedLoopByteOrigins(plan, [0], 8, 20, out var origins));
        if (accepted) Assert.Equal(new uint[] { 0, 0x11, 0x22, 0x33, 255 }, origins.Select(origin => origin.Constant).Order().ToArray());
    }

    [Theory]
    [InlineData("ImageSampleA")]
    [InlineData("ImageSampleAO")]
    public void PackedByteOriginsKeepSampleChannelsSeparateFromTheSentinel(string opcode)
    {
        var plan = Extract(Program([
            Image(0, opcode, 0, samplerRegister: 8),
            Vop2(8, "VLshlrevB32", 9, Operand(8), Gen5Operand.Vector(5)),
            Vop3(12, "VOr3U32", 10, Operand(0xFF000000), Gen5Operand.Vector(4), Gen5Operand.Vector(9)),
            Vop3(20, "VLshlOrU32", 11, Gen5Operand.Vector(6), Operand(16), Gen5Operand.Vector(10)),
            EndProgram(28),
        ]));
        for (var component = 0; component < 4; component++)
        {
            Assert.True(IndirectSelectorValues.TryReadPackedByteOrigin(plan, Gen5Operand.Vector(11), component, 28, out var origin));
            if (component == 3) { Assert.True(origin.IsConstant); Assert.Equal(255u, origin.Constant); }
            else { Assert.False(origin.IsConstant); Assert.Equal((uint)component, origin.Channel); Assert.Equal(0, origin.MemoryIndex); }
        }
    }

    [Fact]
    public void PackedDomainFiltersByOriginalSelectorAndFallsBackWhenCleanProofIsUnavailable()
    {
        var plan = Extract(Program([EndProgram(0)]));
        var proof = new IndirectSelectorValues.PackedTextureDomain([new(-1, 0, 7)]);
        DirectImageCandidate[] candidates = [new(2380, 2) { SelectorValue = 7 }, new(2720, 3) { SelectorValue = 8 }];
        var inputs = new ResourceRuntimeInputs
        {
            ReadCleanMemory = (ulong address, out uint value) => { value = 0; return true; },
            ReadPointSampledByteDomain = (ReadOnlySpan<uint> image, ReadOnlySpan<uint> sampler, uint channels, bool gathered,
                GuestWordReader reader, out uint[] values) => { values = []; return false; },
        };
        Assert.True(proof.TryFilterCandidates(plan, inputs, candidates, out var selected));
        Assert.Equal(candidates.Take(1), selected);
        Assert.False(proof.TryFilterCandidates(plan, new ResourceRuntimeInputs(), candidates, out selected));
        Assert.Same(candidates, selected);
        Assert.False(proof.TryFilterCandidates(plan, inputs, [new(2380, 2)], out _));
        Assert.True(proof.TryFilterCandidates(plan, inputs, [candidates[1]], out selected));
        Assert.Empty(selected);
    }

    [Fact]
    public void PackedTextureDomainRechecksContentsAndRejectsUnavailableOrOtherStageWrites()
    {
        var plan = Extract(Program([Image(0, "ImageSampleA", 0, 8), EndProgram(8)]), userDataCount: 12);
        var proof = new IndirectSelectorValues.PackedTextureDomain([new(0, 0, 0)]);
        DirectImageCandidate[] candidates = [new(0, 0) { SelectorValue = 2 }, new(32, 1) { SelectorValue = 3 }];
        uint content = 2;
        bool readable = true;
        var scans = 0;
        bool Read(ulong address, out uint word) { word = content; return readable; }
        bool Domain(ReadOnlySpan<uint> image, ReadOnlySpan<uint> sampler, uint channels,
            bool gathered, GuestWordReader reader, out uint[] values)
        {
            scans++;
            Assert.Equal(1u, channels);
            values = [];
            if (!reader(0x9000, out var word)) return false;
            values = [word];
            return true;
        }
        ResourceRuntimeInputs InputsFor(bool writer = false) => new()
        {
            UserData = new uint[12], ReadCleanMemory = Read, ReadMemory = Read,
            ReadPointSampledByteDomain = Domain, OtherStageMayWriteMemory = writer,
        };
        Assert.True(proof.TryFilterCandidates(plan, InputsFor(), candidates, out var selected));
        Assert.Equal(candidates.Take(1), selected);
        content = 3;
        Assert.True(proof.TryFilterCandidates(plan, InputsFor(), candidates, out selected));
        Assert.Equal(candidates.Skip(1), selected);
        readable = false;
        Assert.False(proof.TryFilterCandidates(plan, InputsFor(), candidates, out selected));
        Assert.Same(candidates, selected);
        Assert.Equal(3, scans);
        Assert.False(proof.TryFilterCandidates(plan, InputsFor(true), candidates, out _));
        Assert.Equal(3, scans);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    public void PackedTextureReductionRejectsReactivatedUninitializedLanes(bool reactivate, bool constant, bool unknownSymbolicMask, bool accepted)
    {
        var plan = Extract(Program([
            Sop1(0, "SAndSaveexecB64", 16, Gen5Operand.Scalar(12)),
            .. constant ? new[] { Vop1(4, "VMovB32", 4, Operand(2)), Nop(8) }
                : new[] { Image(4, "ImageSampleA", 0, 8) },
            Vop1(12, "VMovB32", 0, Gen5Operand.Vector(4)),
            Sop1(16, "SMovB64", 18, Gen5Operand.Scalar(126)),
            Sop1(20, "SMovB64", 126, Gen5Operand.Scalar(reactivate ? 16u : 18u)),
            Vop2(24, "VAndB32", 1, Operand(255), Gen5Operand.Vector(0)),
            Sop2(28, "SAndB64", 24, Gen5Operand.Scalar(126), Gen5Operand.Scalar(126)),
            Sop1(32, "SOrn2SaveexecB64", 106, Gen5Operand.Scalar(126)),
            Vop3(36, "VCndmaskB32", 5, Operand(255), Gen5Operand.Vector(1), Gen5Operand.Scalar(24)),
            Vop2(44, "VMinU32", 5, Gen5Operand.Vector(5), Gen5Operand.Vector(5)),
            Sop1(48, "SMovB64", 126, Gen5Operand.Scalar(106)),
            new Gen5ShaderInstruction(52, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(5), Operand(31), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(26)],
                new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            Vop2(60, "VLshrrevB32", 0, Operand(8), Gen5Operand.Vector(0)),
            Branch(64, "SBranch", -12), EndProgram(68),
        ]), userDataCount: 14);
        if (unknownSymbolicMask) plan.Graph.LaneSelectionMasks[36] = ScalarValue.Undefined(ScalarValueType.Bool);
        Assert.Equal(accepted, IndirectSelectorValues.TryGetPackedReductionOrigins(plan, 52, 20, 68, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlatParameterDescriptorTableRequiresTheHostDomainAndPreservesScalarLoads(bool provideDomain)
    {
        var program = Program([
            Sop1(0, "SMovB64", 6, Gen5Operand.Scalar(126)),
            new(4, Gen5ShaderEncoding.Vintrp, "VInterpMovF32", [2u], [Gen5Operand.Vector(2)],
                [Gen5Operand.Vector(2)], new Gen5InterpolationControl(3, 1)),
            Sop1(8, "SFF1I32B64", 9, Gen5Operand.Scalar(6)),
            new(12, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(2), Gen5Operand.Scalar(9), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(10)], null),
            Sop2(20, "SMulI32", 9, Gen5Operand.Scalar(10), Operand(64)),
            ScalarBufferLoad(24, 0, 16, 8, dynamicOffsetRegister: 9),
            ScalarBufferLoad(32, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 9),
            Image(40, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1), EndProgram(48),
        ]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 8);
        var original = WorkgroupImageInputs();
        bool Domain(uint attribute, uint channel, GuestWordReader reader, out uint[] values)
        {
            Assert.Equal(3u, attribute); Assert.Equal(1u, channel);
            values = [0, 1]; return true;
        }
        var inputs = new ResourceRuntimeInputs { UserData = original.UserData,
            ReadMemory = original.ReadMemory, ReadCleanMemory = original.ReadCleanMemory,
            ReadFlatParameterDomain = provideDomain ? Domain : null };
        var snapshot = new ResourceSnapshot(); var specialization = new ResourceSpecialization();
        Assert.Equal(provideDomain, ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        if (provideDomain)
        {
            Assert.True(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!
                .TryEvaluate(plan, inputs, out var keys, out _));
            Assert.Equal(new uint[] { 0, 64 }, keys);
            Assert.Equal(2, snapshot.Images.Length);
            Assert.Contains(plan.Graph.Program.Instructions, instruction => instruction.Pc == 24 && instruction.Opcode == "SBufferLoadDwordx8");
            Assert.NotEmpty(plan.DynamicReads);
        }
    }

    [Theory]
    [InlineData(false, false, "", true)]
    [InlineData(true, false, "", false)]
    [InlineData(false, true, "", false)]
    [InlineData(false, false, "capture", false)]
    [InlineData(false, false, "overwrite", false)]
    [InlineData(false, false, "write", false)]
    [InlineData(false, false, "other-stage", false)]
    public void IndexedBufferDescriptorTableRequiresEverySourceRecord(bool unreadable, bool swizzled, string mutation, bool expected)
    {
        var program = Program([
            new(0, Gen5ShaderEncoding.Vintrp, "VInterpMovF32", [2u], [Gen5Operand.Vector(2)],
                [Gen5Operand.Vector(1)], new Gen5InterpolationControl(3, 0)),
            new(4, Gen5ShaderEncoding.Mubuf, "BufferLoadDwordx2", [0u, 0u],
                [Gen5Operand.Vector(1), Gen5Operand.Scalar(4), Operand(0)], [Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
                new Gen5BufferMemoryControl(2, 1, 2, 4, 0, true, false, false, false)),
            Sop1(12, "SMovB64", 6, Gen5Operand.Scalar(126)),
            Sop1(16, "SFF1I32B64", 9, Gen5Operand.Scalar(6)),
            new(20, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(3), Gen5Operand.Scalar(9), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(10)], null),
            Sop2(28, "SMulI32", 9, Gen5Operand.Scalar(10), Operand(64)),
            ScalarBufferLoad(32, 0, 16, 8, dynamicOffsetRegister: 9),
            ScalarBufferLoad(40, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 9),
            Image(48, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1), EndProgram(56),
        ]);
        if (mutation == "capture") program = program with { Instructions = program.Instructions.Select(instruction =>
            instruction.Pc == 12 ? Sop1(12, "SMovB64", 6, Gen5Operand.Scalar(12)) : instruction).ToArray() };
        if (mutation == "overwrite") program = program with { Instructions = program.Instructions.Select(instruction =>
            instruction.Pc == 16 ? new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Vop1, "VMovB32", [0u],
                [Operand(42)], [Gen5Operand.Vector(3)], null) : instruction).ToArray() };
        if (mutation == "write") program = program with { Instructions = [.. program.Instructions.Where(instruction => instruction.Pc < 56),
            BufferAccess(56, "BufferStoreDword", 4), EndProgram(64)] };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 8);
        var original = WorkgroupImageInputs();
        uint second = 1;
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address == 0x4004) return true;
            if (address == 0x400C) { word = second; return !unreadable; }
            return original.ReadMemory!(address, out word);
        }
        var inputs = new ResourceRuntimeInputs { UserData = [.. original.UserData.Take(4),
            0x4000, (8u << 16) | (swizzled ? 0x80000000u : 0), 2, (20u << 12) | 0xFAC],
            ReadMemory = Read, ReadCleanMemory = Read, OtherStageMayWriteMemory = mutation == "other-stage" };
        var snapshot = new ResourceSnapshot(); var specialization = new ResourceSpecialization();
        var cache = new ResourceMaterializationCache();
        bool Resident(ulong address, Span<byte> bytes, bool clean) => false;
        Assert.Equal(expected, cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        if (!expected) return;
        Assert.Equal(2, snapshot.Images.Length);
        second = 0;
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Single(snapshot.Images);
        second = 2; // The source is readable, but its selected descriptor is outside the bound heap.
        Assert.False(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
    }

    private static Gen5ShaderProgram RuntimeMemoryBoundProgram(bool writes = false) => Program([
        ScalarLoad(0, 4, 2), MoveScalar(8, 8, 0),
        Sopc(12, "SCmpLtU32", Gen5Operand.Scalar(8), Gen5Operand.Scalar(2)),
        Sop2(16, "SCselectB64", 106, Gen5Operand.Scalar(126), Operand(0)),
        Branch(20, "SCbranchVccz", writes ? (short)12 : (short)9),
        Sop2(24, "SMulI32", 10, Gen5Operand.Scalar(8), Operand(64)),
        ScalarLoad(28, 0, 16, 8, dynamicOffsetRegister: 10),
        ScalarLoad(36, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 10),
        Image(44, "ImageSampleLz", 16, 24, dmask: 1),
        .. writes ? new[] { MoveVector(52, 0, 0), GlobalAccess(56, "GlobalStoreDword", 0) } : [],
        Sop2(writes ? 64u : 52u, "SAddI32", 8, Gen5Operand.Scalar(8), Operand(1)),
        Branch(writes ? 68u : 56u, "SBranch", writes ? (short)-15 : (short)-12),
        EndProgram(writes ? 72u : 60u),
    ]);

    private static bool ReadRuntimeBoundMemory(ulong address, uint count, out uint word)
    {
        word = 0;
        if (address == 0x8000) { word = count; return true; }
        if (address < 0x1000 || address >= 0x1000 + count * 64UL) return false;
        var record = (uint)(address - 0x1000) / 64;
        word = ((uint)(address - 0x1000) % 64 / 4) switch
        {
            0 => 0x2000 + record * 0x100,
            1 => 20u << 20,
            3 => 0xFACu | (9u << 28),
            9 => 0xFFF000,
            _ => 0,
        };
        return true;
    }

    [Theory]
    [InlineData(5u, 0u, false, false, true)]
    [InlineData(60u, 0u, false, false, true)]
    [InlineData(3u, 0u, false, false, false)]
    [InlineData(5u, 3u, false, false, false)]
    [InlineData(5u, 0u, true, false, false)]
    [InlineData(5u, 0u, false, true, false)]
    public void GatheredByteSelectorRequiresUnsignedByteDescriptorsAndNoWriteHazard(
        uint format, uint borderType, bool otherStageWrites, bool shaderWrites, bool accepted)
    {
        var program = GatheredSelectorProgram(shaderWrites ? "write" : "valid");
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 6);
        var source = plan.DescriptorSources[(int)plan.Info.Images.Last().Source];
        var proof = source.IndirectImage?.GatheredByteSelectorProof;
        Assert.NotNull(proof);
        Assert.Equal(256, source.IndirectImage!.DirectCandidates!.Count);
        Assert.Equal(Enumerable.Range(0, 256).Select(index => (uint)index * 32),
            source.IndirectImage.DirectCandidates.Select(candidate => candidate.Offset));

        var imageWords = new uint[] { 0x10, format << 20, 0, 0x90000920, 0, 0, 0, 0 };
        bool Read(ulong address, out uint word)
        {
            if (address is >= 0x1000 and < 0x1020)
            {
                word = imageWords[(int)((address - 0x1000) / 4)];
                return true;
            }
            if (address is >= 0x1020 and < 0x1030)
            {
                word = address == 0x102C ? borderType << 30 : 0;
                return true;
            }
            if (address is >= 0x2000 and < 0x4000)
            {
                word = imageWords[(int)(((address - 0x2000) % 32) / 4)];
                return true;
            }
            word = 0;
            return false;
        }
        var inputs = new ResourceRuntimeInputs
        {
            UserData = [0x2000, 32u << 16, 256, 0x5204, 0x1000, 0],
            ReadMemory = Read,
            ReadCleanMemory = Read,
            OtherStageMayWriteMemory = otherStageWrites,
        };
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        Assert.Equal(accepted, ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        if (!accepted)
        {
            Assert.Same(priorSnapshot, snapshot);
            Assert.Same(priorSpecialization, specialization);
        }
        else
        {
            var resources = ResourceMaterializer.ApplyTo(plan, specialization);
            var layout = BindingLayout.Allocate(resources.Info,
                BindingLayout.CollectUserDataRegisters(program, 0, 6), false,
                ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
            var request = new ShaderCompileRequest(plan, resources, layout);
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
        }
    }

    [Fact]
    public void GatheredByteProofFailsWhenItsDescriptorBecomesUnreadableOrChangesFormat()
    {
        var program = GatheredSelectorProgram();
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 6);
        var imageWords = new uint[] { 0x10, 5u << 20, 0, 0x90000920, 0, 0, 0, 0 };
        bool unreadable = false;
        bool Read(ulong address, out uint word)
        {
            if (unreadable && address == 0x1004)
            {
                word = 0;
                return false;
            }
            if (address is >= 0x1000 and < 0x1020)
            {
                word = imageWords[(int)((address - 0x1000) / 4)];
                return true;
            }
            if (address is >= 0x1020 and < 0x1030)
            {
                word = 0;
                return true;
            }
            if (address is >= 0x2000 and < 0x4000)
            {
                word = imageWords[(int)(((address - 0x2000) % 32) / 4)];
                return true;
            }
            word = 0;
            return false;
        }
        ResourceRuntimeInputs Inputs() => new()
        {
            UserData = [0x2000, 32u << 16, 256, 0x5204, 0x1000, 0],
            ReadMemory = Read,
            ReadCleanMemory = Read,
        };
        bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            for (var offset = 0; offset < destination.Length; offset += sizeof(uint))
            {
                if (!Read(address + (uint)offset, out var word)) return false;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], word);
            }
            return true;
        }

        var cache = new ResourceMaterializationCache();
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        Assert.True(cache.Materialize(plan, Inputs(), ReadResident, ref snapshot, ref specialization, out _));
        var cachedSnapshot = snapshot;
        var cachedSpecialization = specialization;
        imageWords[1] = 3u << 20;
        Assert.False(cache.Materialize(plan, Inputs(), ReadResident, ref snapshot, ref specialization, out _));
        Assert.Same(cachedSnapshot, snapshot);
        Assert.Same(cachedSpecialization, specialization);

        unreadable = true;
        ResourceSnapshot freshSnapshot = new();
        ResourceSpecialization freshSpecialization = new();
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs(), ref freshSnapshot, ref freshSpecialization));
        Assert.Empty(freshSnapshot.Images);
    }

    [Theory]
    [InlineData("unknown-arm")]
    [InlineData("missing-guard")]
    [InlineData("unconditional-repeat")]
    [InlineData("changed-mask")]
    [InlineData("gather-d16")]
    [InlineData("bypass-gather")]
    [InlineData("exec-expansion")]
    public void UnreachableOrChangedGatherDefinitionsDoNotCreateAByteProof(string variation)
    {
        try
        {
            var plan = ShaderResourcePlan.Extract(GatheredSelectorProgram(variation), ShaderStage.Pixel, Hash, 0, 6);
            Assert.All(plan.DescriptorSources, source => Assert.Null(source.IndirectImage?.GatheredByteSelectorProof));
        }
        catch (ResourcePlanException)
        {
            // Rejecting the whole unsupported shader plan is also fail-closed.
        }
    }

    [Theory]
    [InlineData("fourth-result")]
    [InlineData("narrowing")]
    public void GatheredByteProofUsesAllFourResultsAfterExecutionNarrows(string variation)
    {
        var plan = ShaderResourcePlan.Extract(GatheredSelectorProgram(variation), ShaderStage.Pixel, Hash, 0, 6);
        Assert.NotNull(plan.DescriptorSources[(int)plan.Info.Images.Last().Source].IndirectImage?.GatheredByteSelectorProof);
    }

    [Fact]
    public void GatheredTextureContentsRestrictCandidatesAndAreRecheckedOnEachMaterialization()
    {
        var plan = ShaderResourcePlan.Extract(GatheredSelectorProgram(), ShaderStage.Pixel, Hash, 0, 6);
        uint selectedValue = 0;
        bool readableContents = true;
        bool readableCandidate = true;
        uint candidateLimit = 2;
        uint[] gather = [0x10, 5u << 20, 0, 0x90000924, 0, 0, 0, 0];
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address >= 0x1000 && address < 0x1020) { word = gather[(int)((address - 0x1000) / 4)]; return true; }
            if (address >= 0x1020 && address < 0x1030) return true;
            if (address < 0x2000 || address >= 0x2000 + candidateLimit * 32UL) return false;
            var record = (uint)(address - 0x2000) / 32;
            word = ((uint)(address - 0x2000) % 32) switch
            {
                0 => 0x2000 + record * 0x100, 4 => 20u << 20, 12 => 0x90000FAC, _ => 0,
            };
            return readableCandidate || record == 0;
        }
        bool Domain(ReadOnlySpan<uint> image, ReadOnlySpan<uint> sampler, uint channels, bool gathered, GuestWordReader reader, out uint[] values)
        {
            Assert.Equal(gather, image.ToArray());
            Assert.Equal(1u, channels);
            values = [selectedValue];
            return readableContents;
        }
        bool Resident(ulong address, Span<byte> bytes, bool clean) => false;
        var inputs = new ResourceRuntimeInputs
        {
            UserData = [0x2000, 32u << 16, 256, 0x5204, 0x1000, 0], ReadMemory = Read, ReadCleanMemory = Read,
            ReadPointSampledByteDomain = Domain,
        };
        ResourceSnapshot snapshot = new();
        ResourceSpecialization specialization = new();
        var cache = new ResourceMaterializationCache();
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(0x2000u, snapshot.Images.Last()[0]);
        selectedValue = 1;
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(0x2100u, snapshot.Images.Last()[0]);
        var original = snapshot;
        readableCandidate = false;
        Assert.False(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Same(original, snapshot);
        readableCandidate = true;
        readableContents = false;
        Assert.False(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Same(original, snapshot);
        Assert.Equal(0, cache.Hits);
        candidateLimit = 256;
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
    }

    private static Gen5ShaderProgram GatheredSelectorProgram(string variation = "valid")
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ScalarLoad(0, 4, 16, 8),
            ScalarLoad(8, 4, 24, 4, 32),
            MoveVector(16, 7, 0),
            Image(20, "ImageGather4Lz", 16, 24, dmask: 1),
            Vop2(28, "VCndmaskB32", 6, Gen5Operand.Vector(4), Gen5Operand.Vector(7)),
            Branch(32, "SCbranchExecz", variation == "write" ? (short)18 : (short)15),
            Sop1(36, "SMovB64", 8, Gen5Operand.Scalar(126)),
            Sop1(40, "SFF1I32B64", 10, Gen5Operand.Scalar(8)),
            new(44, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(6), Gen5Operand.Scalar(10), Gen5Operand.Scalar(0)],
                [Gen5Operand.Scalar(12)], null),
            new(52, Gen5ShaderEncoding.Vopc, "VCmpEqU32", [0u],
                [Gen5Operand.Scalar(12), Gen5Operand.Vector(6)], [Gen5Operand.Scalar(20)],
                new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, 20)),
            Sop1(56, "SAndSaveexecB64", 22, Gen5Operand.Scalar(20)),
            Branch(60, "SCbranchExecz", 5),
            Sop2(64, "SMulI32", 10, Gen5Operand.Scalar(12), Operand(32)),
            ScalarBufferLoad(68, 0, 32, 8, dynamicOffsetRegister: 10),
            Image(76, "ImageSampleLz", 32, 24, dmask: 1),
        };

        if (variation == "write")
        {
            // s[4:5] points at the gather's image descriptor; v0 is a zero byte offset.
            instructions.Add(MoveVector(84, 0, 0));
            instructions.Add(GlobalAccess(88, "GlobalStoreDword", 4, vectorAddress: 0));
        }

        var reductionPc = variation == "write" ? 96u : 84u;
        var restorePc = reductionPc + 4;
        var repeatPc = restorePc + 4;
        var endPc = repeatPc + 4;
        instructions.Add(Sop2(reductionPc, "SAndn2B64", 8, Gen5Operand.Scalar(8), Gen5Operand.Scalar(20)));
        instructions.Add(Sop1(restorePc, "SMovB64", 126, Gen5Operand.Scalar(22)));
        var backWords = checked((short)((40 - (int)(repeatPc + 4)) / 4));
        instructions.Add(Branch(repeatPc, "SCbranchScc1", backWords));
        instructions.Add(EndProgram(endPc));

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            instructions[index] = variation switch
            {
                "fourth-result" when instruction.Pc == 16 => MoveVector(16, 7, 256),
                "narrowing" when instruction.Pc == 28 => Vopc(28, "VCmpxEqU32", Operand(0), 0),
                "unknown-arm" when instruction.Pc == 28 => instruction with
                    { Sources = [Gen5Operand.Vector(42), Gen5Operand.Vector(7)] },
                "missing-guard" when instruction.Pc == 32 => Sop1(32, "SMovB32", 10, Operand(0)),
                "unconditional-repeat" when instruction.Pc == repeatPc => Branch(repeatPc, "SBranch", backWords),
                "changed-mask" when instruction.Pc == reductionPc => Sop1(reductionPc, "SMovB64", 8, Operand(0)),
                "gather-d16" when instruction.Pc == 20 => instruction with
                    { Control = ((Gen5ImageControl)instruction.Control!) with { D16 = true } },
                "bypass-gather" when instruction.Pc == 0 => Branch(0, "SCbranchScc1", 6),
                _ => instruction,
            };
        }

        if (variation == "exec-expansion")
        {
            for (var index = 0; index < instructions.Count; index++)
                if (instructions[index].Pc >= 32)
                    instructions[index] = instructions[index] with { Pc = instructions[index].Pc + 4 };
            instructions.Insert(5, Sop1(32, "SMovB64", 126, Operand(uint.MaxValue)));
        }

        return Program(instructions.ToArray());
    }

    [Theory]
    [InlineData(6u, 0u, 1u, true)]
    [InlineData(1u, 0u, 1u, true)]
    [InlineData(256u, 0u, 1u, true)]
    [InlineData(0u, 0u, 1u, false)]
    [InlineData(257u, 0u, 1u, false)]
    [InlineData(6u, 1u, 1u, false)]
    [InlineData(6u, 0u, 2u, false)]
    public void PostTestedOuterLoopKeepsDescriptorAcrossInnerLoop(uint count, uint start, uint step, bool accepted)
    {
        var program = Program([
            MoveScalar(0, 8, start),
            Sop2(4, "SLshlB32", 9, Gen5Operand.Scalar(8), Operand(6)),
            ScalarLoad(8, 0, 16, 8, dynamicOffsetRegister: 9),
            MoveScalar(16, 10, 0),
            Image(20, "ImageLoad", 16, dmask: 1),
            Sop2(28, "SAddI32", 10, Gen5Operand.Scalar(10), Operand(1)),
            Sopc(32, "SCmpLtI32", Gen5Operand.Scalar(10), Operand(2)),
            Branch(36, "SCbranchScc1", -5),
            Sop2(40, "SAddI32", 8, Gen5Operand.Scalar(8), Operand(step)),
            Sopc(44, "SCmpLtI32", Gen5Operand.Scalar(8), Operand(count)),
            Branch(48, "SCbranchScc1", -12), EndProgram(52),
        ]);
        if (!accepted)
        {
            try { var rejected = Extract(program, userDataCount: 2); Assert.Null(rejected.DescriptorSources[(int)rejected.Info.Images[0].Source].IndirectImage); }
            catch (ResourcePlanException) { }
            return;
        }
        var plan = Extract(program, userDataCount: 2);
        var indirect = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage;
        Assert.NotNull(indirect);
        Assert.Equal(Enumerable.Range(0, (int)count).Select(index => (uint)index * 64), indirect!.DirectCandidates!.Select(candidate => candidate.Offset));
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x1000 + count * 64UL) return false;
            var component = (address - 0x1000) % 64 / 4;
            word = component switch { 0 => 0x2000u, 1 => 20u << 20, 3 => 0xFACu | (9u << 28), _ => 0u };
            return true;
        }
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
    }
    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(0x10000000u, false)]
    public void ImageCandidatesDistinguishResourceLevelFromReservedBits(uint bit, bool valid)
    {
        var plan = ShaderResourcePlan.Extract(CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 2);
        bool Read(ulong address, out uint word)
        {
            if (!ReadWaveIndexedMemory(address, out word)) return false;
            if (address >= 0x1100 && (address - 0x1100) % 32 == 8) word |= bit;
            return true;
        }
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        Assert.Equal(valid, snapshot.Images.Any(image => image[0] != 0));
        if (valid) Assert.All(snapshot.Images, image => Assert.Equal(bit, image[2] & bit));
    }

    internal static Gen5ShaderProgram WorkgroupImageProgram(bool unknownWrite = false, bool split = false) => Program([
        Sop2(0, "SLshrB32", 9, Gen5Operand.Scalar(8), Operand(1)),
        Sop2(4, "SMulI32", 9, Gen5Operand.Scalar(9), Operand(64)),
        ScalarBufferLoad(8, 0, 16, split ? 4u : 8u, dynamicOffsetRegister: 9),
        .. split ? new[] { ScalarBufferLoad(16, 0, 20, 4, immediateOffset: 16, dynamicOffsetRegister: 9) } : Array.Empty<Gen5ShaderInstruction>(),
        ScalarBufferLoad(split ? 24u : 16u, 0, 24, 4, immediateOffset: 32, dynamicOffsetRegister: 9),
        Image(split ? 32u : 24u, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1),
        unknownWrite ? GlobalAccess(split ? 40u : 32u, "FlatStoreDword", 0) : BufferStore(split ? 40u : 32u, 4), EndProgram(split ? 48u : 40u),
    ]);

    private static ShaderResourcePlan WorkgroupImagePlan(bool unknownWrite = false) =>
        ShaderResourcePlan.Extract(WorkgroupImageProgram(unknownWrite), ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));

    private static ResourceRuntimeInputs WorkgroupImageInputs(uint groups = 4, bool alias = false,
        bool differentSampler = false, bool unreadable = false, bool provideClean = true, uint imageFormat = 20)
    {
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x1080 || unreadable && address == 0x1040) return false;
            var record = (uint)(address - 0x1000) / 64;
            var component = (uint)(address - 0x1000) % 64 / 4;
            word = component switch
            {
                0 => record == 0 ? 0x2000u : 0x3000u,
                1 => imageFormat << 20,
                3 => 0xFACu | (9u << 28),
                8 => differentSampler && record != 0 ? 1u : 0u,
                9 => 0xFFF000,
                _ => 0,
            };
            return true;
        }
        return new ResourceRuntimeInputs
        {
            UserData = [0x1000, 64u << 16, 2, (20u << 12) | 0xFAC,
                alias ? 0x1040u : 0x4000u, 4u << 16, 16, (20u << 12) | 0xFAC],
            ReadMemory = Read, ReadCleanMemory = provideClean ? Read : null,
            ComputeState = new(64, 64, 1, 1, false, 0, 1, groups, 1, 1),
        };
    }

    public static (ResourceSnapshot Snapshot, ShaderCompileRequest Request, uint[] Registers, byte[] Table)
        PrepareWorkgroupImages(bool split = false)
    {
        var program = Program([.. WorkgroupImageProgram(split: split).Instructions.Where(instruction => instruction.Pc < (split ? 32u : 24u)),
            Vop1(32, "VMovB32", 0, Operand(0x3F000000)),
            Vop1(36, "VMovB32", 1, Operand(0x3F000000)),
            Vop1(40, "VMovB32", 2, Gen5Operand.Scalar(8)),
            Image(44, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1),
            BufferAccess(52, "BufferStoreDword", 4, vectorData: 4, indexEnabled: true, vectorAddress: 2), EndProgram(60)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        var inputs = WorkgroupImageInputs(imageFormat: 22);
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 8),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, ThreadCountX = 4, ComputeSystemRegisters = new(8, null, null, null),
        };
        var table = new byte[128];
        for (uint offset = 0; offset < table.Length; offset += 4)
        {
            Assert.True(inputs.ReadCleanMemory!(0x1000 + offset, out var word));
            BitConverter.TryWriteBytes(table.AsSpan((int)offset), word);
        }
        return (snapshot, request, inputs.UserData.ToArray(), table);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void GuardedDynamicImageRequiresTheAdmittedEdge(bool wrongKey, bool rejectedEdgeRejoins, bool inverted, bool accepted)
    {
        var prefix = FiniteBufferImageProgram(unknown: true).Instructions.Where(instruction => instruction.Pc < 36);
        var program = Program(prefix.Concat(new Gen5ShaderInstruction[]
        {
            Sopc(36, "SCmpGeU32", Gen5Operand.Scalar(wrongKey ? 105u : 106u), Operand(3)),
            Branch(40, inverted ? "SCbranchScc0" : "SCbranchScc1", (short)(rejectedEdgeRejoins ? 0 : 7)),
            Sop2(44, "SMulI32", 106, Gen5Operand.Scalar(106), Operand(384)),
            ScalarBufferLoad(48, 0, 16, 8, dynamicOffsetRegister: 106),
            Image(56, "ImageLoad", 16, dmask: 1, vectorAddress: 4),
            Nop(64), Nop(68), EndProgram(72),
        }).ToArray());
        var plan = Extract(program, userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        if (accepted)
        {
            Assert.NotNull(source.IndirectImage?.DirectCandidates);
            Assert.Equal(new uint[] { 0, 384, 768 }, source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset));
            Assert.Null(source.ZeroExtentBufferSource);
        }
        else Assert.Null(source.IndirectImage);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void GuardedDescriptorKeepsItsProofAfterScalarReadsAreFlattened(bool wrongKey, bool rejoins, bool inverted, bool accepted)
    {
        var program = Program([
            ScalarLoad(0, 4, 10),
            Vopc(8, "VCmpEqU32", Operand(0), 0),
            Vop3(12, "VCndmaskB32", 1, Operand(0), Gen5Operand.Scalar(10), Gen5Operand.Scalar(106)),
            ReadFirstLane(20, 106, 1),
            Sopc(24, "SCmpGeU32", Gen5Operand.Scalar(wrongKey ? 10u : 106u), Operand(3)),
            Branch(28, inverted ? "SCbranchScc0" : "SCbranchScc1", (short)(rejoins ? 0 : 5)),
            Sop2(32, "SMulI32", 106, Gen5Operand.Scalar(106), Operand(64)),
            ScalarBufferLoad(36, 0, 16, 8, dynamicOffsetRegister: 106),
            Image(44, "ImageLoad", 16, dmask: 1), EndProgram(52),
        ]);
        var plan = Extract(program, userDataCount: 6);
        Assert.NotEmpty(plan.TableReads);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Equal(accepted, source.IndirectImage?.DirectCandidates is not null);
        if (accepted)
        {
            Assert.Null(source.ZeroExtentBufferSource);
            Assert.Equal(new uint[] { 0, 64, 128 }, source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset));
        }
    }

    [Fact]
    public void GuardedWideScalarLoadPlansBothDescriptorsWithTheOriginalOffset()
    {
        var prefix = FiniteBufferImageProgram(unknown: true).Instructions.Where(instruction => instruction.Pc < 36);
        var program = Program(prefix.Concat(new Gen5ShaderInstruction[]
        {
            Sopc(36, "SCmpGeU32", Gen5Operand.Scalar(106), Operand(3)),
            Branch(40, "SCbranchScc1", 9),
            Sop2(44, "SMulI32", 106, Gen5Operand.Scalar(106), Operand(384)),
            ScalarBufferLoad(48, 0, 16, 16, dynamicOffsetRegister: 106),
            Image(56, "ImageLoad", 16, dmask: 1, vectorAddress: 4),
            Image(64, "ImageLoad", 24, dmask: 1, vectorAddress: 4),
            Nop(72), Nop(76), EndProgram(80),
        }).ToArray());
        var plan = Extract(program, userDataCount: 4);
        Assert.Equal(2, plan.Info.Images.Count);
        foreach (var image in plan.Info.Images)
        {
            var source = plan.DescriptorSources[(int)image.Source];
            Assert.NotNull(source.IndirectImage?.DirectCandidates);
            Assert.Equal(new uint[] { 0, 384, 768 }, source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset));
            Assert.Null(source.ZeroExtentBufferSource);
        }
        Assert.Equal(new uint[] { 0, 8 }, plan.IndirectImages.Select(access => plan.Memory[access.Key.MemoryIndex].ComponentIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkgroupTablePreservesLoadsAndMapsRealOffsetsToDifferentImages(bool split)
    {
        var plan = ShaderResourcePlan.Extract(WorkgroupImageProgram(split: split), ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        Assert.NotNull(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup);
        Assert.All(plan.IndirectImages, access => Assert.True(access.KeyIsAddressOffset));
        Assert.All(plan.Memory.Entries.Where(memory => memory.Pc == 8), memory => Assert.False(memory.PlanningOnly));
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, WorkgroupImageInputs(), ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.True(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!
            .TryEvaluate(plan, WorkgroupImageInputs(), out var keys, out var descriptors));
        Assert.Equal(new uint[] { 0, 64 }, keys);
        Assert.Equal(new uint[] { 0x2000, 0x3000 }, descriptors.Select(words => words[0]));
        Assert.Equal(new uint[] { 0x2000, 0x3000 }, snapshot.Images.Select(words => words[0]));
        Assert.Single(snapshot.Samplers);
        Assert.Equal(0xFFF000u, snapshot.Samplers[0][1]);
    }

    [Theory]
    [InlineData(0u, false, false, false, true)]
    [InlineData(65537u, false, false, false, true)]
    [InlineData(4u, true, false, false, true)]
    [InlineData(4u, false, true, false, true)]
    [InlineData(4u, false, false, true, true)]
    [InlineData(4u, false, false, false, false)]
    [InlineData(5u, false, false, false, true)]
    public void WorkgroupTableDeclinesMissingBoundsAliasDifferentSamplersOrUnreadableRecords(
        uint groups, bool alias, bool differentSampler, bool unreadable, bool clean)
    {
        var plan = WorkgroupImagePlan();
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        var original = snapshot;
        Assert.False(ResourceMaterializer.Materialize(plan,
            WorkgroupImageInputs(groups, alias, differentSampler, unreadable, clean), ref snapshot, ref specialization));
        Assert.Same(original, snapshot);
    }

    [Fact]
    public void WorkgroupTableDeclinesUnknownWriteAddresses()
    {
        var plan = WorkgroupImagePlan(unknownWrite: true);
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.False(ResourceMaterializer.Materialize(plan, WorkgroupImageInputs(), ref snapshot, ref specialization));
    }

    [Fact]
    public void WorkgroupTableChecksWritesAcrossDifferentGroups()
    {
        var program = Program([.. WorkgroupImageProgram().Instructions.Where(instruction => instruction.Pc < 24),
            ScalarBufferLoad(24, 0, 4, 4, immediateOffset: 48, dynamicOffsetRegister: 9),
            Image(32, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1), BufferStore(40, 4), EndProgram(48)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!;
        ResourceRuntimeInputs Inputs(uint groups)
        {
            var original = WorkgroupImageInputs(groups);
            bool Read(ulong address, out uint word)
            {
                var offset = (uint)(address - 0x1000) % 64;
                if (address is >= 0x1000 and < 0x1080 && offset >= 48)
                {
                    word = offset switch { 48 => address < 0x1040 ? 0x4000u : 0x1000u,
                        52 => 4u << 16, 56 => 16, _ => (20u << 12) | 0xFAC };
                    return true;
                }
                return original.ReadCleanMemory!(address, out word);
            }
            return new() { UserData = original.UserData, ReadMemory = Read, ReadCleanMemory = Read,
                ComputeState = original.ComputeState };
        }
        Assert.True(proof.TryEvaluate(plan, Inputs(2), out _, out _));
        Assert.False(proof.TryEvaluate(plan, Inputs(4), out _, out _));
    }

    [Fact]
    public void WorkgroupTableCacheRebuildsWhenTheActualDispatchGrows()
    {
        var plan = WorkgroupImagePlan();
        var cache = new ResourceMaterializationCache();
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        foreach (var groups in new uint[] { 2, 2, 4 })
        {
            var inputs = WorkgroupImageInputs(groups);
            bool ReadResident(ulong address, Span<byte> bytes, bool clean)
            {
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    if (!inputs.ReadCleanMemory!(address + (uint)offset, out var word)) return false;
                    BitConverter.TryWriteBytes(bytes[offset..], word);
                }
                return true;
            }
            Assert.True(cache.Materialize(plan, inputs, ReadResident, ref snapshot, ref specialization, out _));
            Assert.Equal(groups == 2 ? 1 : 2, snapshot.Images.Length);
        }
        Assert.Equal((1, 2), (cache.Hits, cache.Misses));
    }

    [Theory]
    [InlineData(0x9000UL, 32UL, true, true)]
    [InlineData(0x1001UL, 32UL, true, false)]
    [InlineData(0x101FUL, 1UL, true, false)]
    [InlineData(0x103FUL, 1UL, true, true)]
    [InlineData(0x9000UL, 0UL, true, false)]
    [InlineData(0xFFFFFFFFFFFFUL, 2UL, true, false)]
    [InlineData(0x9000UL, 32UL, false, false)]
    public void WorkgroupTableChecksImageWritesAgainstEveryGroupsReads(
        ulong outputAddress, ulong size, bool readableRange, bool accepted)
    {
        var program = Program([.. WorkgroupImageProgram().Instructions.Where(instruction => instruction.Pc < 32),
            Image(32, "ImageStore", 16, dmask: 1), EndProgram(40)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!;
        var original = WorkgroupImageInputs();
        bool Range(ReadOnlySpan<uint> words, out ulong address, out ulong length)
        {
            // The later group's image can overwrite an earlier group's table.
            address = words[0] == 0x3000 ? outputAddress : 0xA000;
            length = size;
            return readableRange;
        }
        var inputs = new ResourceRuntimeInputs { UserData = original.UserData,
            ReadMemory = original.ReadMemory, ReadCleanMemory = original.ReadCleanMemory,
            ComputeState = original.ComputeState, ReadImageWriteRange = Range };
        Assert.Equal(accepted, proof.TryEvaluate(plan, inputs, out _, out _));
        var missingRange = new ResourceRuntimeInputs { UserData = original.UserData,
            ReadMemory = original.ReadMemory, ReadCleanMemory = original.ReadCleanMemory,
            ComputeState = original.ComputeState };
        Assert.False(proof.TryEvaluate(plan, missingRange, out _, out _));
    }

    [Fact]
    public void WorkgroupImageWriteRangesAreRevalidatedOnRepeatedMaterialization()
    {
        var program = Program([.. WorkgroupImageProgram().Instructions.Where(instruction => instruction.Pc < 32),
            Image(32, "ImageStore", 16, dmask: 1), EndProgram(40)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        var original = WorkgroupImageInputs();
        ulong outputAddress = 0x9000;
        bool Range(ReadOnlySpan<uint> words, out ulong address, out ulong size)
        {
            address = outputAddress; size = 32; return true;
        }
        bool Resident(ulong address, Span<byte> bytes, bool clean)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!original.ReadCleanMemory!(address + (uint)offset, out var word)) return false;
                BitConverter.TryWriteBytes(bytes[offset..], word);
            }
            return true;
        }
        var inputs = new ResourceRuntimeInputs { UserData = original.UserData,
            ReadMemory = original.ReadMemory, ReadCleanMemory = original.ReadCleanMemory,
            ComputeState = original.ComputeState, ReadImageWriteRange = Range };
        var cache = new ResourceMaterializationCache();
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        outputAddress = 0x1000;
        Assert.False(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void WorkgroupSamplerResourcesAreReusedForRepeatedInstructions()
    {
        var program = Program([.. WorkgroupImageProgram().Instructions.Where(instruction => instruction.Pc < 40),
            Image(40, "ImageSampleLz", 16, samplerRegister: 24, dmask: 1), EndProgram(48)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        Assert.Single(plan.Info.Samplers);
        Assert.Single(plan.Info.Images);
    }

    private static ShaderResourcePlan WorkgroupLoopImagePlan(uint increment = 1, bool inverted = false,
        uint? writeRegister = null, bool exitImageWrite = false) =>
        ShaderResourcePlan.Extract(Program([
            Sop2(0, "SMulI32", 15, Gen5Operand.Scalar(12), Operand(8)),
            ScalarBufferLoad(4, 0, 13, 2, dynamicOffsetRegister: 15),
            Sopc(12, "SCmpLtI32", Gen5Operand.Scalar(13), Gen5Operand.Scalar(14)),
            Branch(16, inverted ? "SCbranchScc1" : "SCbranchScc0", 10),
            Sop2(20, "SMulI32", 15, Gen5Operand.Scalar(13), Operand(4)),
            ScalarBufferLoad(24, 4, 24, dynamicOffsetRegister: 15),
            Sop2(32, "SMulI32", 15, Gen5Operand.Scalar(24), Operand(32)),
            ScalarBufferLoad(36, 8, 16, 8, dynamicOffsetRegister: 15),
            Image(44, "ImageLoad", 16, dmask: 1),
            Sop2(52, "SAddI32", 13, Gen5Operand.Scalar(13), Operand(increment)),
            Branch(56, "SBranch", -12),
            .. exitImageWrite ? new[] {
                Sop2(60, "SMulI32", 15, Gen5Operand.Scalar(13), Operand(32)),
                ScalarBufferLoad(64, 8, 16, 8, dynamicOffsetRegister: 15),
                Image(72, "ImageStore", 16, dmask: 1), EndProgram(80),
            } : writeRegister is { } output ? new[] { BufferStore(60, output), EndProgram(68) } : new[] { EndProgram(60) },
        ]), ShaderStage.Compute, Hash, 0, 12,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(12, null, null, null));

    private static ResourceRuntimeInputs WorkgroupLoopImageInputs(uint first = 1, uint end = 3,
        ulong unreadable = 0, uint firstRecord = 3)
    {
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address == unreadable) return false;
            if (address is >= 0x1000 and < 0x1010)
            {
                word = address % 8 == 0 ? first : end; return true;
            }
            if (address is >= 0x2000 and < 0x2040)
            {
                word = address == 0x2004 ? firstRecord : 1; return true;
            }
            if (address is >= 0x3000 and < 0x3200)
            {
                word = ((address - 0x3000) % 32) switch
                {
                    0 => 0x6000u + (uint)(address - 0x3000) / 32 * 0x100,
                    4 => 20u << 20, 12 => 0xFACu | (9u << 28), _ => 0,
                };
                return true;
            }
            return false;
        }
        return new() { UserData = [0x1000, 0, 16, (20u << 12) | 0xFAC,
            0x2000, 0, 64, (20u << 12) | 0xFAC, 0x3000, 0, 512, (20u << 12) | 0xFAC],
            ReadMemory = Read, ReadCleanMemory = Read,
            ComputeState = new(64, 64, 1, 1, false, 0, 1, 2, 1, 1) };
    }

    [Theory]
    [InlineData(1u, 3u, 0UL, 3u, true)]
    [InlineData(2u, 3u, 0UL, 3u, true)]
    [InlineData(1u, 3u, 0x1004UL, 3u, false)]
    [InlineData(1u, 3u, 0x2004UL, 3u, false)]
    [InlineData(1u, 3u, 0x3060UL, 3u, false)]
    [InlineData(1u, 3u, 0UL, 16u, false)]
    [InlineData(3u, 3u, 0UL, 3u, false)]
    [InlineData(4u, 3u, 0UL, 3u, false)]
    [InlineData(uint.MaxValue, 3u, 0UL, 3u, false)]
    [InlineData(1u, uint.MaxValue, 0UL, 3u, false)]
    [InlineData(1u, 258u, 0UL, 3u, false)]
    public void WorkgroupLoopsUseActualBoundsAndFollowReadableIndexTables(
        uint first, uint end, ulong unreadable, uint firstRecord, bool accepted)
    {
        var plan = WorkgroupLoopImagePlan();
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!;
        Assert.NotNull(proof.LoopCounter);
        Assert.Equal(accepted, proof.TryEvaluate(plan, WorkgroupLoopImageInputs(first, end, unreadable, firstRecord),
            out var keys, out var descriptors));
        if (accepted)
        {
            Assert.Equal(first == 1 ? new uint[] { 96, 32 } : new uint[] { 32 }, keys);
            Assert.Equal(first == 1 ? new uint[] { 0x6300, 0x6100 } : new uint[] { 0x6100 },
                descriptors.Select(words => words[0]));
        }
    }

    [Theory]
    [InlineData(2u, false)]
    [InlineData(1u, true)]
    public void WorkgroupLoopDeclinesUnsupportedIncrementOrInvertedGuard(uint increment, bool inverted)
    {
        var plan = WorkgroupLoopImagePlan(increment, inverted);
        Assert.Null(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage?.Workgroup);
    }

    [Fact]
    public void WorkgroupLoopDoesNotSubstituteIterationValuesForAnExitImageWrite()
    {
        var plan = WorkgroupLoopImagePlan(exitImageWrite: true);
        Assert.Null(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage?.Workgroup);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void WorkgroupLoopsRejectWritesToBoundsIndexOrDescriptorMemory(uint writeRegister)
    {
        var plan = WorkgroupLoopImagePlan(writeRegister: writeRegister);
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!;
        Assert.False(proof.TryEvaluate(plan, WorkgroupLoopImageInputs(), out _, out _));
    }

    [Fact]
    public void WorkgroupLoopCacheTracksBoundsIndexRecordsAndUnreadableDependencies()
    {
        var plan = WorkgroupLoopImagePlan();
        var inputs = WorkgroupLoopImageInputs();
        bool Resident(ulong address, Span<byte> bytes, bool clean)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!inputs.ReadCleanMemory!(address + (uint)offset, out var word)) return false;
                BitConverter.TryWriteBytes(bytes[offset..], word);
            }
            return true;
        }
        var cache = new ResourceMaterializationCache();
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(2, snapshot.Images.Length);
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(1, cache.Hits);
        inputs = WorkgroupLoopImageInputs(end: 2);
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Single(snapshot.Images);
        inputs = WorkgroupLoopImageInputs(end: 2, firstRecord: 4);
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(0x6400u, snapshot.Images[0][0]);
        inputs = WorkgroupLoopImageInputs(end: 2, firstRecord: 4, unreadable: 0x3080);
        Assert.False(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(1, cache.Hits);
    }

    private static (ShaderResourcePlan Plan, ResourceRuntimeInputs Inputs) WorkgroupLoopSamplers(
        string opcode = "ImageSampleLz", ulong unreadable = 0, uint firstSampler = 2, uint firstRecord = 3)
    {
        var prefix = WorkgroupLoopImagePlan().Graph.Program.Instructions.Where(instruction => instruction.Pc < 44)
            .Select(instruction => instruction.Pc == 16 ? Branch(16, "SCbranchScc0", 12) : instruction);
        var program = Program([.. prefix,
            ScalarBufferLoad(44, 8, 24, 4, immediateOffset: 512, dynamicOffsetRegister: 15),
            Image(52, opcode, 16, samplerRegister: 24, dmask: 1),
            Sop2(60, "SAddI32", 13, Gen5Operand.Scalar(13), Operand(1)),
            Branch(64, "SBranch", -14), EndProgram(68)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 12,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(12, null, null, null));
        var original = WorkgroupLoopImageInputs(firstRecord: firstRecord);
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address == unreadable) return false;
            if (address is >= 0x3200 and < 0x3400)
            {
                word = ((address - 0x3200) % 32) switch
                {
                    0 => (address - 0x3200) / 32 == firstRecord ? firstSampler : 1u,
                    4 => 0xFFF000, _ => 0,
                };
                return true;
            }
            return original.ReadCleanMemory!(address, out word);
        }
        var userData = original.UserData.ToArray(); userData[10] = 1024;
        return (plan, new() { UserData = userData, ReadMemory = Read, ReadCleanMemory = Read,
            ComputeState = original.ComputeState });
    }

    [Fact]
    public void WorkgroupLoopPreservesDifferentSamplersAndCompilesBothBackends()
    {
        var (plan, inputs) = WorkgroupLoopSamplers();
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(new uint[] { 2, 1 }, snapshot.Samplers.Select(words => words[0]));
        Assert.Equal(new uint[] { 96, 32 }, specialization.RuntimeSamplers.Select(candidate => candidate.Offset));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(2, resources.Info.Samplers.Count);
        Assert.Equal(new uint[] { 0, 1 }, resources.FiniteSamplersByMemoryIndex.Values.Single().Candidates!.Select(candidate => candidate.Sampler));
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
            { LocalSizeX = 1, ThreadCountX = 2, ComputeSystemRegisters = new(12, null, null, null) };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out error), error);
        Assert.Contains(resources.FiniteSamplersByMemoryIndex.Values.Single().SelectorMemoryIndex, request.IndirectOffsetKeyMemoryIndices);
        Assert.Contains(plan.Memory.Entries, memory => memory.Pc == 44 && !memory.PlanningOnly);
    }

    [Theory]
    [InlineData("ImageSampleLz", 0x3260UL, false)]
    [InlineData("ImageSampleLz", 0UL, true)]
    [InlineData("ImageSampleD", 0UL, true)]
    [InlineData("ImageSampleC", 0UL, false)]
    public void WorkgroupLoopSamplerSelectionRejectsUnreadableOrUnsupportedPaths(string opcode, ulong unreadable, bool accepted)
    {
        var (plan, inputs) = WorkgroupLoopSamplers(opcode, unreadable);
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.Equal(accepted, ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
    }

    [Fact]
    public void WorkgroupLoopSamplerKeysParticipateInSpecializationIdentity()
    {
        ResourceSnapshot snapshot = new(); ResourceSpecialization first = new(), changedPayload = new(), changedKey = new();
        var (plan, inputs) = WorkgroupLoopSamplers();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref first));
        Assert.Equal(first, first.Clone());
        var (_, payloadInputs) = WorkgroupLoopSamplers(firstSampler: 3);
        Assert.True(ResourceMaterializer.Materialize(plan, payloadInputs, ref snapshot, ref changedPayload));
        Assert.Equal(first, changedPayload);
        Assert.Equal(3u, snapshot.Samplers[0][0]);
        var (_, keyInputs) = WorkgroupLoopSamplers(firstRecord: 4);
        Assert.True(ResourceMaterializer.Materialize(plan, keyInputs, ref snapshot, ref changedKey));
        Assert.NotEqual(first, changedKey);
    }

    [Fact]
    public void RepeatedSamplerKeysShareTheirImageOperationBodies()
    {
        var (plan, inputs) = WorkgroupLoopSamplers();
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var finite = resources.FiniteSamplersByMemoryIndex.Values.Single();
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
            { LocalSizeX = 1, ThreadCountX = 2, ComputeSystemRegisters = new(12, null, null, null) };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var original, out var error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var originalMetal, out error), error);
        finite.Candidates = Enumerable.Range(0, 32)
            .Select(index => new FiniteSamplerCandidate((uint)index * 32, (uint)index % 2)).ToArray();
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var repeated, out error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var repeatedMetal, out error), error);
        // The extra comparisons grow linearly; the sampling bodies must not be
        // duplicated sixteen times for the same two native samplers.
        Assert.True(repeated.Spirv.Length < original.Spirv.Length * 2);
        Assert.True(repeatedMetal.Source.Length < originalMetal.Source.Length * 2);
        Assert.Contains("992u", repeatedMetal.Source);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WorkgroupLoopReusesMaskedInputsWithoutOmittingDistinctOutputRanges(bool changingOutput, bool accepted)
    {
        var originalPlan = WorkgroupLoopImagePlan();
        var prefix = originalPlan.Graph.Program.Instructions.Where(instruction => instruction.Pc < 60)
            .Select(instruction => instruction.Pc == 0
                ? Sop2(0, "SAndB32", 15, Gen5Operand.Scalar(12), Operand(8)) : instruction);
        var program = Program([.. prefix, .. changingOutput ? new[] {
            Sop2(60, "SMulI32", 15, Gen5Operand.Scalar(12), Operand(32)),
            ScalarBufferLoad(64, 8, 16, 8, dynamicOffsetRegister: 15),
            Image(72, "ImageStore", 16, dmask: 1), EndProgram(80),
        } : new[] { EndProgram(60) }]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 12,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(12, null, null, null));
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!;
        var original = WorkgroupLoopImageInputs(end: 4);
        bool Read(ulong address, out uint word)
        {
            if (address is >= 0x3200 and < 0x4000)
            {
                word = ((address - 0x3000) % 32) switch {
                    0 => 0x6000u + (uint)(address - 0x3000) / 32 * 0x100,
                    4 => 20u << 20, 12 => 0xFACu | (9u << 28), _ => 0,
                };
                return true;
            }
            return original.ReadCleanMemory!(address, out word);
        }
        bool Range(ReadOnlySpan<uint> words, out ulong address, out ulong size)
        {
            address = words[0] == 0x7000 ? 0x1000UL : 0x10000UL;
            size = 32; return true;
        }
        var userData = original.UserData.ToArray(); userData[10] = 4096;
        var inputs = new ResourceRuntimeInputs { UserData = userData, ReadMemory = Read,
            ReadCleanMemory = Read, ReadImageWriteRange = Range,
            ComputeState = new(64, 64, 1, 1, false, 0, 1, changingOutput ? 128u : 32768u, 1, 1) };
        Assert.Equal(accepted, proof.TryEvaluate(plan, inputs, out var keys, out _));
        if (accepted) Assert.Equal(new uint[] { 96, 32 }, keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkgroupLoopAcceptsOnlyCounterPreservingControlFlowJoins(bool modifiesCounter)
    {
        var prefix = WorkgroupLoopImagePlan().Graph.Program.Instructions.Where(instruction => instruction.Pc < 36)
            .Select(instruction => instruction.Pc == 16 ? Branch(16, "SCbranchScc0", 14) : instruction);
        var program = Program([.. prefix,
            Sopc(36, "SCmpEqU32", Gen5Operand.Scalar(12), Operand(0)),
            Branch(40, "SCbranchScc0", 2),
            modifiesCounter ? Sop2(44, "SAddI32", 13, Gen5Operand.Scalar(13), Operand(1)) : Nop(44),
            Branch(48, "SBranch", 0),
            ScalarBufferLoad(52, 8, 16, 8, dynamicOffsetRegister: 15),
            Image(60, "ImageLoad", 16, dmask: 1),
            Sop2(68, "SAddI32", 13, Gen5Operand.Scalar(13), Operand(1)),
            Branch(72, "SBranch", -16), EndProgram(76)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 12,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(12, null, null, null));
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage?.Workgroup;
        if (modifiesCounter) Assert.Null(proof);
        else
        {
            Assert.NotNull(proof);
            Assert.True(proof.TryEvaluate(plan, WorkgroupLoopImageInputs(), out var keys, out _));
            Assert.Equal(new uint[] { 96, 32 }, keys);
            Assert.True(IndirectSelectorValues.IsLoopCounterAlias(proof.LoopCounter!, proof.LoopCounter!));
        }
    }

    [Fact]
    public void LoopCounterAliasesPreserveCyclesAndRejectOtherIncomingValues()
    {
        var plan = WorkgroupLoopImagePlan();
        var proof = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.Workgroup!;
        var first = ScalarValue.Phi(1, ScalarValueType.U32);
        var second = ScalarValue.Phi(2, ScalarValueType.U32);
        first.SetPhiOperands([0, 2], [proof.LoopCounter!, second]);
        second.SetPhiOperands([0, 1], [proof.LoopCounter!, first]);
        Assert.True(IndirectSelectorValues.IsLoopCounterAlias(first, proof.LoopCounter!));
        var aliases = new Dictionary<ScalarValue, bool>();
        var evaluator = new RuntimeValueEvaluator(plan, WorkgroupLoopImageInputs(), proof.Input, 0, proof.LoopCounter, 7, aliases);
        Assert.True(evaluator.Evaluate(first, out var value));
        Assert.Equal(7u, value);
        var next = new RuntimeValueEvaluator(plan, WorkgroupLoopImageInputs(), proof.Input, 1, proof.LoopCounter, 9, aliases);
        Assert.True(next.Evaluate(first, out value));
        Assert.Equal(9u, value);
        second.SetPhiOperands([0, 1], [ScalarValue.ConstantOf(8u), first]);
        Assert.False(IndirectSelectorValues.IsLoopCounterAlias(first, proof.LoopCounter!));
        var rejected = new RuntimeValueEvaluator(plan, WorkgroupLoopImageInputs(), proof.Input, 0, proof.LoopCounter, 7);
        Assert.False(rejected.Evaluate(first, out _));
    }

    [Fact]
    public void RuntimeSamplerSelectorUsesTheLoadBehindInvariantJoins()
    {
        var (original, inputs) = WorkgroupLoopSamplers();
        int selectorMemory = -1;
        var plan = ShaderResourcePlan.Extract(original.Graph.Program, ShaderStage.Compute, Hash, 0, 12,
            beforeResourceTracking: candidate =>
            {
                var index = Enumerable.Range(0, candidate.Memory.Count).Single(index => candidate.Memory[index].Pc == 52);
                var access = candidate.Graph.Accesses[index]!;
                selectorMemory = access.SamplerHandle!.Operands[0].MemoryIndex;
                var joined = access.SamplerHandle.Operands.Select(word =>
                {
                    var phi = ScalarValue.Phi(1, ScalarValueType.U32);
                    phi.SetPhiOperands([0, 1], [word, phi]);
                    return phi;
                }).ToArray();
                candidate.Graph.Accesses[index] = access with {
                    SamplerHandle = ScalarValue.Handle(ScalarValueKind.SamplerHandle, joined) };
            }, computeSystemRegisters: new Gen5ComputeSystemRegisters(12, null, null, null));
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.All(specialization.RuntimeSamplers, candidate => Assert.Equal(selectorMemory, candidate.SelectorMemoryIndex));
        Assert.Equal(new uint[] { 2, 1 }, snapshot.Samplers.Select(words => words[0]));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(selectorMemory, resources.FiniteSamplersByMemoryIndex.Values.Single().SelectorMemoryIndex);
    }

    [Theory]
    [InlineData("ImageSampleA", false)]
    [InlineData("ImageSampleLz", false)]
    [InlineData("ImageGather4Lz", false)]
    [InlineData("ImageLoad", true)]
    public void MultisampleDescriptorsRequireLoadsInsteadOfInvalidSamplingModules(string opcode, bool accepted)
    {
        var sampled = opcode != "ImageLoad";
        var program = Program([Image(0, opcode, 0, samplerRegister: 8, dmask: 1), EndProgram(8)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 12);
        var inputs = new ResourceRuntimeInputs { UserData = [0x1000, GuestImageFormat.Format32Float << 20, 0,
            0xFAC | (1u << 16) | (GuestImageFormat.ImageType2DMsaa << 28), 0, 0x10, 0, 0,
            0, 0xFFF000, 0, 0] };
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(ImageDimension.Dim2DMsaa, resources.Info.Images[0].Dimension);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.Equal(accepted, Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out _, out var error));
        if (sampled) Assert.Contains("multisample sampling semantics", error);
    }

    [Fact]
    public void WorkgroupTableKeepsGdsWritesInTheirSeparateAddressSpace()
    {
        var program = Program([.. WorkgroupImageProgram().Instructions.Where(instruction => instruction.Pc < 40),
            DataShareWrite(40, gds: true), EndProgram(48)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, null, null, null));
        Assert.Contains(plan.Memory.Entries, memory => memory.Kind == MemoryResourceKind.GlobalDataShare && !memory.PlanningOnly);
        ResourceSnapshot snapshot = new(); ResourceSpecialization specialization = new();
        Assert.True(ResourceMaterializer.Materialize(plan, WorkgroupImageInputs(), ref snapshot, ref specialization));
    }

    [Fact]
    public void WorkgroupTableDoesNotInventABoundForAnotherAxis()
    {
        var program = WorkgroupImageProgram() with
        {
            Instructions = WorkgroupImageProgram().Instructions.Select(instruction => instruction.Pc == 0
                ? Sop2(0, "SAddU32", 10, Gen5Operand.Scalar(8), Gen5Operand.Scalar(9))
                : instruction.Pc == 4 ? Sop2(4, "SMulI32", 9, Gen5Operand.Scalar(10), Operand(64)) : instruction).ToArray(),
        };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 8,
            computeSystemRegisters: new Gen5ComputeSystemRegisters(8, 9, null, null));
        Assert.Null(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage);
    }

    private static Gen5ShaderProgram FiniteLaneReadProgram(bool unknownLane = false, bool partialWrite = false,
        bool loopWrite = false, bool wrongRestore = false, bool unknownPartial = false)
    {
        var prefix = FiniteBufferImageProgram().Instructions.Where(instruction => instruction.Pc < 20).ToList();
        prefix.AddRange([
            Sop1(20, "SMovB64", 106, Gen5Operand.Scalar(126)),
            Vop3(24, "VMadU32U24", 3, Operand(3), Gen5Operand.Vector(1), Gen5Operand.Vector(2)),
            Vopc(32, "VCmpxEqU32", Operand(0), 0),
            Sop1(36, "SMovB64", 126, Gen5Operand.Scalar(wrongRestore ? 104u : 106u)),
            Sop1(40, "SMovB64", 68, Gen5Operand.Scalar(106)),
            Sop1(44, "SFF1I32B64", 17, Gen5Operand.Scalar(68)),
            new(48, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(3), Gen5Operand.Scalar(unknownLane ? 12u : 17u), Gen5Operand.Scalar(0)],
                [Gen5Operand.Scalar(17)], new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            new(56, Gen5ShaderEncoding.Vop3, "VCmpEqU32", [0u, 0u],
                [Gen5Operand.Scalar(17), Gen5Operand.Vector(3)], [Gen5Operand.Scalar(70)], new Gen5Vop3Control(0, 0, 0, false, 0, 70)),
            Sop1(64, "SAndSaveexecB64", 64, Gen5Operand.Scalar(70)),
            Branch(68, "SCbranchExecz", 6),
            Sop2(72, "SMulI32", 106, Gen5Operand.Scalar(17), Operand(384)),
            ScalarBufferLoad(76, 0, 16, 8, dynamicOffsetRegister: 106),
            Image(84, "ImageLoad", 16, dmask: 1, vectorAddress: 4),
            loopWrite ? Vop1(92, "VMovB32", 3, Operand(999)) : Sop2(92, "SAndn2B64", 68, Gen5Operand.Scalar(68), Gen5Operand.Scalar(70)),
            Sop1(96, "SMovB64", 126, Gen5Operand.Scalar(64)), Branch(100, "SCbranchScc1", -15), EndProgram(104),
        ]);
        if (partialWrite)
        {
            prefix = prefix.Select(instruction => instruction.Pc >= 36 ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
            prefix.Add(Vop1(36, "VMovB32", 3, unknownPartial ? Gen5Operand.Vector(0) : Operand(999)));
            prefix.Sort((left, right) => left.Pc.CompareTo(right.Pc));
        }
        return Program([.. prefix]);
    }

    [Fact]
    public void FiniteLaneReadWaterfallKeepsTheSelectorOpaqueButProvesItsCandidates()
    {
        var plan = Extract(FiniteLaneReadProgram(), userDataCount: 4);
        var laneReads = plan.Graph.Values.Where(value => value.Kind == ScalarValueKind.FirstLane && value.Payload == 48).ToArray();
        Assert.NotEmpty(laneReads);
        Assert.All(laneReads, value => Assert.False(plan.ValidateRuntimeValue(value)));
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Null(source.ZeroExtentBufferSource);
        Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void FiniteLaneReadWaterfallRejectsUnprovenMasksAndModifiedValues(bool unknownLane, bool partialWrite, bool loopWrite, bool wrongRestore)
    {
        var plan = Extract(FiniteLaneReadProgram(unknownLane, partialWrite, loopWrite, wrongRestore, unknownPartial: partialWrite), userDataCount: 4);
        Assert.Null(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    public void RestoredMaskRetainsBothUntouchedAndPartiallyWrittenSelectorValues(bool andSave, bool negated = false)
    {
        var program = FiniteLaneReadProgram(partialWrite: true);
        if (andSave) program = program with { Instructions = program.Instructions.Select(instruction => instruction.Pc == 32
            ? Sop1(32, negated ? "SAndn1SaveexecB64" : "SAndSaveexecB64", 102, Gen5Operand.Scalar(100)) : instruction).ToArray() };
        var plan = Extract(program, userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Equal(new uint[] { 0, 768, 1152, 1920, 999 * 384 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedMaskAliasRequiresAnUnchangedMatchingExecRestore(bool overwriteAlias)
    {
        var instructions = FiniteLaneReadProgram().Instructions.Select(instruction =>
            instruction with { Pc = instruction.Pc >= 32 ? instruction.Pc + 8 : instruction.Pc }).ToList();
        instructions[instructions.FindIndex(instruction => instruction.Pc == 20)] = Sop1(20, "SMovB64", 104, Gen5Operand.Scalar(126));
        instructions.Add(Sop1(32, "SMovB64", 126, Gen5Operand.Scalar(104)));
        instructions.Add(Sop1(36, "SMovB64", 106, Gen5Operand.Scalar(104)));
        if (overwriteAlias)
        {
            instructions = instructions.Select(instruction => instruction.Pc >= 36 ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
            instructions.Add(Sop1(36, "SMovB32", 104, Operand(0)));
        }
        var plan = Extract(Program(instructions.OrderBy(instruction => instruction.Pc).ToArray()), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        if (overwriteAlias) Assert.Null(source.IndirectImage);
        else Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    private static Gen5ShaderProgram CapturedMaskLaneProgram(bool wrongRestore = false, bool negated = false)
    {
        var instructions = FiniteLaneReadProgram().Instructions.Select(instruction =>
            instruction with { Pc = instruction.Pc >= 20 ? instruction.Pc + 12 : instruction.Pc }).ToList();
        instructions.AddRange([Sop1(20, negated ? "SAndn1SaveexecB64" : "SAndSaveexecB64", 104, Gen5Operand.Scalar(100)),
            Sop1(24, "SMovB64", 126, Gen5Operand.Scalar(104)), Nop(28)]);
        instructions[instructions.FindIndex(instruction => instruction.Pc == 32)] = Sop1(32, "SMovB64", 106, Gen5Operand.Scalar(104));
        instructions[instructions.FindIndex(instruction => instruction.Pc == 48)] = Sop1(48, "SMovB64", 126, Gen5Operand.Scalar(wrongRestore ? 102u : 104u));
        instructions[instructions.FindIndex(instruction => instruction.Pc == 52)] = Nop(52);
        instructions[instructions.FindIndex(instruction => instruction.Pc == 56)] = Sop1(56, "SFF1I32B64", 17, Gen5Operand.Scalar(104));
        instructions[instructions.FindIndex(instruction => instruction.Pc == 104)] = Sop2(104, "SAndn2B64", 104, Gen5Operand.Scalar(104), Gen5Operand.Scalar(70));
        return Program(instructions.OrderBy(instruction => instruction.Pc).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InvariantSelectorSurvivesAWaterfallRestoreUnlessItsValueOrSavedMaskChanges(bool changeValue, bool changeMask)
    {
        var plan = Extract(InvariantAfterWaterfallProgram(changeValue, changeMask), userDataCount: 4);
        var source = plan.DescriptorSources[(int)Assert.Single(plan.Info.Images).Source];
        if (changeValue || changeMask) Assert.Null(source.IndirectImage);
        else Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    private static Gen5ShaderProgram InvariantAfterWaterfallProgram(bool changeValue = false, bool changeMask = false)
    {
        var instructions = FiniteLaneReadProgram().Instructions.Where(instruction => instruction.Pc < 104)
            .Select(instruction => instruction.Pc is 72 or 76 ? Nop(instruction.Pc) : instruction.Pc == 84
                ? changeValue ? Vop1(84, "VMovB32", 3, Gen5Operand.Vector(0))
                    : changeMask ? Sop1(84, "SMovB64", 64, Gen5Operand.Scalar(102)) : Nop(84)
                : instruction).ToList();
        instructions.AddRange([Sop1(104, "SMovB64", 126, Gen5Operand.Scalar(64)),
            ReadFirstLane(108, 106, 3), Sop2(112, "SMulI32", 106, Gen5Operand.Scalar(106), Operand(384)),
            ScalarBufferLoad(116, 0, 16, 8, dynamicOffsetRegister: 106),
            Image(124, "ImageLoad", 16, dmask: 1, vectorAddress: 4), EndProgram(132)]);
        return Program(instructions.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemainingMaskCopyAllowsIndependentInstructionsButRejectsChangedMask(bool changeMask)
    {
        var instructions = FiniteLaneReadProgram().Instructions.Select(instruction => instruction.Pc >= 40
            ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
        instructions.Add(changeMask ? Vopc(40, "VCmpEqU32", Operand(0), 0) : Vop1(40, "VMovB32", 10, Operand(0)));
        var plan = Extract(Program(instructions.OrderBy(instruction => instruction.Pc).ToArray()), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        if (changeMask) Assert.Null(source.IndirectImage);
        else Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    [Fact]
    public void RestoredMaskUnionsValuesWrittenUnderAnArbitraryIntermediateMask()
    {
        var program = FiniteLaneReadProgram(partialWrite: true);
        program = program with { Instructions = program.Instructions.Select(instruction => instruction.Pc == 32
            ? Sop2(32, "SAndn2B64", 126, Gen5Operand.Scalar(100), Gen5Operand.Scalar(102)) : instruction).ToArray() };
        var plan = Extract(program, userDataCount: 4);
        Assert.Equal(new uint[] { 0, 768, 1152, 1920, 999 * 384 }, plan.DescriptorSources[(int)plan.Info.Images[0].Source]
            .IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    [Fact]
    public void SavedMaskProofAllowsAForwardExitThatSkipsTheDescriptorAccess()
    {
        var instructions = FiniteLaneReadProgram().Instructions.Select(instruction => instruction.Pc >= 32
            ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
        instructions.Add(Branch(32, "SCbranchVccz", 18));
        var plan = Extract(Program(instructions.OrderBy(instruction => instruction.Pc).ToArray()), userDataCount: 4);
        var selector = plan.Graph.Values.Last(value => value.Kind == ScalarValueKind.FirstLane && value.Payload == 52);
        Assert.True(ResourceTracker.TryGetStableLaneReadStart(plan.Graph.Program.Instructions,
            plan.Graph.Program.Instructions.ToList().FindIndex(instruction => instruction.Pc == 52), out _));
        Assert.True(IndirectSelectorValues.TryGetConstantValues(plan, selector, out var values));
        Assert.Equal(new uint[] { 0, 2, 3, 5 }, values.Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CapturedOldExecMaskRequiresAnExactRestoreBeforeTheLaneScan(bool wrongRestore, bool negated = false)
    {
        var plan = Extract(CapturedMaskLaneProgram(wrongRestore, negated), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        if (wrongRestore) Assert.Null(source.IndirectImage);
        else Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    public static Gen5ShaderProgram FiniteBufferImageProgram(bool unknown = false, bool expandExec = false,
        uint sourceSelect = 6, uint multiplier = 3, uint compare = 0, bool splitMultiply = false, bool packedWord = false)
    {
        var fullWord = new Gen5SdwaControl(6, 0, sourceSelect, 6, false, false, 0, 0, 0, false, null);
        return Program(
            Vopc(0, "VCmpEqU32", Operand(compare), 0),
            new(4, Gen5ShaderEncoding.Vop2, "VCndmaskB32", [0u, 0u],
                [Operand(0), unknown ? Gen5Operand.Vector(0) : Operand(packedWord ? 0x10000u : 1u)], [Gen5Operand.Vector(1)], fullWord),
            new(12, Gen5ShaderEncoding.Vop2, "VCndmaskB32", [0u, 0u],
                [Operand(0), Operand(packedWord ? 0x20000u : 2u)], [Gen5Operand.Vector(2)], fullWord),
            splitMultiply ? Vop3(20, "VMulU32U24", 3, Operand(multiplier), Gen5Operand.Vector(1)) :
                Vop3(20, "VMadU32U24", 3, Operand(multiplier), Gen5Operand.Vector(1), Gen5Operand.Vector(2)),
            packedWord ? new(28, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(3)], [Gen5Operand.Vector(3)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)) :
            expandExec ? Sop1(28, "SMovB64", 126, Gen5Operand.Scalar(12)) : splitMultiply ?
                Vop2(28, "VAddI32", 3, Gen5Operand.Vector(3), Gen5Operand.Vector(2)) : Nop(28),
            ReadFirstLane(32, 106, 3),
            Sop2(36, "SMulI32", 106, Gen5Operand.Scalar(106), Operand(384)),
            ScalarBufferLoad(40, 0, 16, 8, dynamicOffsetRegister: 106),
            Image(48, "ImageLoad", 16, dmask: 1, vectorAddress: 4),
            EndProgram(56));
    }

    internal static (ResourceSnapshot Snapshot, ShaderCompileRequest Request, uint[] Registers) PrepareFiniteBufferImages(uint compare, bool laneRead = false, bool partialSelector = false, bool capturedMask = false, bool splitMultiply = false, bool afterWaterfall = false, bool negated = false, bool packedWord = false, bool wideSecondHalf = false)
    {
        var program = FiniteBufferImageProgram(compare: compare) with
        {
            Instructions = FiniteBufferImageProgram(compare: compare, splitMultiply: splitMultiply, packedWord: packedWord).Instructions.Where(instruction => instruction.Pc < 48).Concat([
                Vop1(48, "VMovB32", 4, Operand(0)), Vop1(52, "VMovB32", 5, Operand(0)),
                Image(56, "ImageLoad", 16, dmask: 1, vectorAddress: 4),
                BufferAccess(64, "BufferStoreDword", 8, vectorData: 4), EndProgram(72),
            ]).ToArray(),
        };
        if (wideSecondHalf)
        {
            program = program with { Instructions = program.Instructions.Select(instruction => instruction.Pc == 40
                ? ScalarBufferLoad(40, 0, 16, 16, dynamicOffsetRegister: 106)
                : instruction.Pc == 56 ? Image(56, "ImageLoad", 24, dmask: 1, vectorAddress: 4) : instruction).ToArray() };
        }
        if (laneRead)
        {
            var laneProgram = afterWaterfall ? InvariantAfterWaterfallProgram() : capturedMask ? CapturedMaskLaneProgram(negated: negated) : FiniteLaneReadProgram();
            var imagePc = laneProgram.Instructions.Single(instruction => instruction.Opcode == "ImageLoad").Pc;
            var instructions = laneProgram.Instructions.Select(instruction =>
                instruction with { Pc = instruction.Pc + (instruction.Pc > imagePc ? 16u : 8u) }).ToList();
            instructions[instructions.FindIndex(instruction => instruction.Pc == 8)] = Vopc(8, "VCmpEqU32", Operand(compare), 0);
            var skipIndex = instructions.FindIndex(instruction => instruction.Opcode == "SCbranchExecz");
            instructions[skipIndex] = Branch(instructions[skipIndex].Pc, "SCbranchExecz", (short)(afterWaterfall ? 6 : 8));
            var backIndex = instructions.FindIndex(instruction => instruction.Opcode == "SCbranchScc1");
            instructions[backIndex] = Branch(instructions[backIndex].Pc, "SCbranchScc1", (short)(afterWaterfall ? -15 : -17));
            instructions.AddRange([Vop1(0, "VMovB32", 4, Operand(0)), Vop1(4, "VMovB32", 5, Operand(0)),
                BufferAccess(imagePc + 16, "BufferStoreDword", 8, vectorData: 4)]);
            program = program with { Instructions = instructions.OrderBy(instruction => instruction.Pc).ToArray() };
        }
        if (partialSelector)
        {
            var instructions = program.Instructions.Select(instruction => instruction with
                { Pc = instruction.Pc + (instruction.Pc >= 32 ? 12u : instruction.Pc >= 20 ? 4u : 0u) }).ToList();
            instructions[instructions.FindIndex(instruction => instruction.Pc == 32)] = Vopc(32, "VCmpxEqU32", Operand(compare), 0);
            instructions.AddRange([Sop1(20, "SMovB64", 106, Gen5Operand.Scalar(126)),
                Vop1(36, "VMovB32", 3, Operand(2)), Sop1(40, "SMovB64", 126, Gen5Operand.Scalar(106))]);
            program = program with { Instructions = instructions.OrderBy(instruction => instruction.Pc).ToArray() };
        }
        uint[] registers = [0x1000, 0, 2048, 0, 0, 0, 0, 0, 0, 0, 64, 0];
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x1800) return false;
            var relative = address - 0x1000;
            if (wideSecondHalf) relative = relative >= 32 ? relative - 32 : 0;
            word = (relative % 384 / 4) switch
            {
                0 => 0x2000u + (uint)(relative / 384) * 0x100,
                1 => 20u << 20,
                3 => 0xFACu | (9u << 28),
                _ => 0,
            };
            return true;
        }
        var plan = Extract(program, userDataCount: 12);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers, readCleanMemory: Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        return (snapshot, new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 }, registers);
    }

    [Theory]
    [InlineData(160)]
    [InlineData(164)]
    public void FiniteBufferDescriptorMayStartInsideAndSpanScalarLoads(int secondOffset)
    {
        var original = FiniteBufferImageProgram();
        var program = original with { Instructions = original.Instructions.Where(instruction => instruction.Pc < 40)
            .Concat([
                ScalarBufferLoad(40, 0, 12, 8, immediateOffset: 128, dynamicOffsetRegister: 106),
                ScalarBufferLoad(48, 0, 20, 4, immediateOffset: secondOffset, dynamicOffsetRegister: 106),
                Image(56, "ImageLoad", 16, dmask: 1, vectorAddress: 4), EndProgram(64),
            ]).ToArray() };
        var plan = Extract(program, userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Null(source.ZeroExtentBufferSource);
        Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x2000) return false;
            var relative = address - 0x1000;
            var offset = relative % 384;
            word = offset switch
            {
                144 => 0x2000u + (uint)(relative / 384) * 0x100u,
                148 => 20u << 20,
                156 => 0xFACu | (9u << 28),
                _ => 0,
            };
            return true;
        }
        var snapshot = new ResourceSnapshot(); var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0, 4096, 0], readCleanMemory: Read),
            ref snapshot, ref specialization));
        Assert.Equal(new uint[] { 0x2000, 0x2200, 0x2300, 0x2500 }, snapshot.Images.Select(image => image[0]).Order());
        var prior = snapshot;
        bool Missing(ulong address, out uint word)
        {
            if (address == 0x1000ul + (uint)secondOffset) { word = 0; return false; }
            return Read(address, out word);
        }
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0, 4096, 0], readCleanMemory: Missing),
            ref snapshot, ref specialization));
        Assert.Same(prior, snapshot);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 4),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout)
            { LocalSizeX = 64, ThreadCountX = 64 }, out _, out var error), error);
    }
    [Fact]
    public void PackedUpperWordPreservesTheFiniteSelectorDomain()
    {
        var plan = Extract(FiniteBufferImageProgram(packedWord: true), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    [Theory]
    [InlineData(true, 6u)]
    [InlineData(false, 4u)]
    public void ModifiedOrPartialPackedWordDeclinesTheProof(bool signed, uint destinationSelect)
    {
        var program = FiniteBufferImageProgram(packedWord: true);
        program = program with { Instructions = program.Instructions.Select(instruction => instruction.Pc == 28
            ? instruction with { Control = new Gen5SdwaControl(destinationSelect, 0, 5, 6, signed, false, 0, 0, 0, false, null) }
            : instruction).ToArray() };
        var plan = Extract(program, userDataCount: 4);
        Assert.Null(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage);
    }

    [Fact]
    public void FiniteScalarBufferImagesKeepEveryConditionalCandidate()
    {
        var plan = Extract(FiniteBufferImageProgram(), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Null(source.ZeroExtentBufferSource);
        var candidates = Assert.IsType<List<DirectImageCandidate>>(source.IndirectImage!.DirectCandidates);
        Assert.Equal(new uint[] { 0, 768, 1152, 1920 }, candidates.Select(candidate => candidate.Offset).Order().ToArray());
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x1800) return false;
            var relative = address - 0x1000;
            word = (relative % 384 / 4) switch
            {
                0 => 0x2000u + (uint)(relative / 384) * 0x100u,
                1 => 20u << 20,
                3 => 0xFACu | (9u << 28),
                _ => 0,
            };
            return true;
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0, 2048, 0], readCleanMemory: Read),
            ref snapshot, ref specialization));
        Assert.Equal(4, snapshot.Images.Length);
        Assert.Equal(new uint[] { 0x2000, 0x2200, 0x2300, 0x2500 }, snapshot.Images.Select(item => item[0]).Order().ToArray());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 4),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);

        var prior = snapshot;
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0, 32, 0], readCleanMemory: Read),
            ref snapshot, ref specialization));
        Assert.Same(prior, snapshot);
    }

    [Theory]
    [InlineData(true, false, 6u)]
    [InlineData(false, true, 6u)]
    [InlineData(false, false, 0u)]
    public void FiniteScalarBufferImagesRejectUnknownOrUnprovenLaneValues(bool unknown, bool expandExec, uint select)
    {
        var plan = Extract(FiniteBufferImageProgram(unknown, expandExec, select), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Null(source.IndirectImage);
        Assert.NotNull(source.ZeroExtentBufferSource);
    }

    [Fact]
    public void FiniteSelectorMadUsesOnlyLow24BitsOfMultiplicands()
    {
        var plan = Extract(FiniteBufferImageProgram(multiplier: 0x01000003), userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Equal(new uint[] { 0, 768, 1152, 1920 },
            source.IndirectImage!.DirectCandidates!.Select(candidate => candidate.Offset).Order().ToArray());
    }

    [Fact]
    public void FiniteSamplersPreserveDifferentReadableCandidatesAndRejectUnreadableOnes()
    {
        var program = FiniteBufferImageProgram() with
        {
            Instructions = FiniteBufferImageProgram().Instructions.Where(instruction => instruction.Pc < 48).Concat([
                ScalarBufferLoad(48, 0, 28, 4, immediateOffset: 256, dynamicOffsetRegister: 106),
                Image(56, "ImageSampleLz", 16, 28, dmask: 1, vectorAddress: 4), EndProgram(64),
            ]).ToArray(),
        };
        var plan = Extract(program, userDataCount: 4);
        var source = plan.DescriptorSources[(int)plan.Info.Samplers[0].Source];
        Assert.Null(source.ZeroExtentBufferSource);
        Assert.Equal(4, source.FiniteSamplerSources!.Count);
        var mismatch = false;
        var unreadable = false;
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x2000) return false;
            var relative = address - 0x1000;
            var component = relative % 384 / 4;
            if (relative / 384 == 5 && component == 64 && unreadable) return false;
            word = component switch
            {
                0 => 0x2000u + (uint)(relative / 384) * 0x100,
                1 => 22u << 20,
                3 => 0xFACu | (9u << 28),
                64 => mismatch && relative / 384 == 5 ? 1u : 0u,
                66 => 0x10,
                _ => 0,
            };
            return true;
        }
        var dirtyReads = 0;
        bool Dirty(ulong address, out uint word) { dirtyReads++; word = 0; return false; }
        var inputs = Inputs([0x1000, 0, 4096, 0], readMemory: Dirty, readCleanMemory: Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal(4, snapshot.Samplers.Length);
        Assert.All(snapshot.Samplers, sampler => Assert.Equal(new uint[] { 0, 0, 0x10, 0 }, sampler));
        Assert.Equal(0, dirtyReads);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 4),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout)
            { LocalSizeX = 64, ThreadCountX = 64 }, out _, out var error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout)
            { LocalSizeX = 64, ThreadCountX = 64 }, out _, out var metalError), metalError);
        var prior = snapshot;
        mismatch = true;
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal(1u, snapshot.Samplers[^1][0]);
        Assert.Equal(0u, snapshot.Samplers[0][0]);
        prior = snapshot;
        mismatch = false;
        unreadable = true;
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Same(prior, snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedUintFloatCandidatesRequireLegalSamplersAndKeepTheirNativeTypes(bool linear)
    {
        var program = FiniteBufferImageProgram() with
        {
            Instructions = FiniteBufferImageProgram().Instructions.Where(instruction => instruction.Pc < 48).Concat([
                ScalarBufferLoad(48, 0, 28, 4, immediateOffset: 256, dynamicOffsetRegister: 106),
                Image(56, "ImageSampleLz", 16, 28, dmask: 1, vectorAddress: 4), EndProgram(64),
            ]).ToArray(),
        };
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 || address >= 0x2000) return false;
            var offset = address - 0x1000;
            word = (offset % 384 / 4) switch
            {
                0 => 0x2000u + (uint)(offset / 384) * 256,
                1 => (offset / 384 == 5 ? 22u : 20u) << 20,
                3 => 0xFAC | (9u << 28),
                66 => linear ? 0x05500000u : 0x05000000u,
                _ => 0,
            };
            return true;
        }
        var plan = Extract(program, userDataCount: 4);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        Assert.Equal(!linear, ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0, 4096, 0], readCleanMemory: Read),
            ref snapshot, ref specialization));
        if (linear)
        {
            Assert.Same(priorSnapshot, snapshot);
            Assert.Same(priorSpecialization, specialization);
            return;
        }
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Contains(resources.Info.Images, image => image.NumericClass == ImageNumericClass.Uint);
        Assert.Contains(resources.Info.Images, image => image.NumericClass == ImageNumericClass.Float);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 4),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out error), error);
    }

    internal static Gen5ShaderProgram CreateWaveIndexedDescriptorProgram()
    {
        return Program(
            ScalarLoad(0, 0, 16, immediateOffset: 0x80),
            Sop1(8, "SFF1I32B32", 18, Gen5Operand.Scalar(16)),
            Sop2(12, "SMulI32", 106, Gen5Operand.Scalar(18), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x90)),
            MoveVectorFromScalar(16, 34, 18),
            Vop2(20, "VLshlrevB32", 15, Operand(4), Gen5Operand.Vector(34)),
            Vop3(24, "VLshlAddU32", 16, Gen5Operand.Vector(15), Operand(3), Gen5Operand.Vector(15)),
            Sop1(32, "SBitset0B32", 16, Gen5Operand.Scalar(18)),
            Vop2(36, "VAddI32", 15, new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x40), Gen5Operand.Vector(16)),
            GlobalMemory(40, "GlobalLoadDword", 0, 15, 22, 0),
            ReadFirstLane(48, 106, 22),
            Sop2(52, "SLshlB32", 106, Gen5Operand.Scalar(106), Operand(5)),
            ScalarLoad(56, 0, 4, 8, immediateOffset: 0x100, dynamicOffsetRegister: 106),
            Image(64, "ImageLoad", 4, dmask: 1, vectorAddress: 1),
            EndProgram(72));
    }

    internal static Gen5ShaderProgram CreateWaveIndexedReadLaneProgram(bool selfAddressed, bool restoreExec = true)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ScalarLoad(0, 0, 16, immediateOffset: 0x80),
            Sop1(8, "SFF1I32B32", 18, Gen5Operand.Scalar(16)),
            Sop2(12, "SMulI32", 19, Gen5Operand.Scalar(18), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x90)),
            MoveVectorFromScalar(16, 34, 18),
            Vop2(20, "VLshlrevB32", 15, Operand(4), Gen5Operand.Vector(34)),
            Vop3(24, "VLshlAddU32", selfAddressed ? 15u : 16u, Gen5Operand.Vector(15), Operand(3), Gen5Operand.Vector(15)),
            Sop2(32, "SLshlB32", 20, Operand(1), Gen5Operand.Scalar(18)),
            Sop2(36, "SXorB32", 16, Gen5Operand.Scalar(20), Gen5Operand.Scalar(16)),
            Vop2(40, "VAddI32", 15, new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x40), Gen5Operand.Vector(selfAddressed ? 15u : 16u)),
            GlobalMemory(44, "GlobalLoadDword", 0, 15, 22, 0),
            Sop1(52, "SMovB64", 12, Gen5Operand.Scalar(126)),
            Sop1(56, "SFF1I32B64", 24, Gen5Operand.Scalar(12)),
            new(60, Gen5ShaderEncoding.Vop3, "VReadlaneB32", [0u, 0u],
                [Gen5Operand.Vector(22), Gen5Operand.Scalar(24), Gen5Operand.Scalar(24)], [Gen5Operand.Scalar(106)], null),
            new(68, Gen5ShaderEncoding.Vop3, "VCmpEqU32", [0u, 0u],
                [Gen5Operand.Scalar(106), Gen5Operand.Vector(22)], [Gen5Operand.Scalar(14)], new Gen5Vop3Control(0, 0, 0, false, 0, 14)),
            Sop1(76, "SAndSaveexecB64", 28, Gen5Operand.Scalar(14)),
            Branch(80, "SCbranchExecz", 8),
            Sop2(84, "SLshlB32", 106, Gen5Operand.Scalar(106), Operand(5)),
            Sop2(88, "SAddI32", 107, Gen5Operand.Scalar(106), Operand(16)),
            ScalarLoad(92, 0, 4, 4, immediateOffset: 0x100, dynamicOffsetRegister: 106),
            ScalarLoad(100, 0, 8, 4, immediateOffset: 0x100, dynamicOffsetRegister: 107),
            Image(108, "ImageLoad", 4, dmask: 1, vectorAddress: 1),
            Sop2(116, "SAndn2B64", 12, Gen5Operand.Scalar(12), Gen5Operand.Scalar(14)),
            restoreExec ? Sop1(120, "SMovB64", 126, Gen5Operand.Scalar(28)) : Nop(120),
            Branch(124, "SCbranchScc1", -18),
            EndProgram(128),
        };
        return Program([.. instructions]);
    }

    public static Gen5ShaderProgram CreateProgram(uint mask = 1, bool split = true, bool bitScan = true)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            Vop2(0, "VAddU32", 12, Operand(mask), Gen5Operand.Vector(0)),
            ReadFirstLane(4, 18, 12),
            Sop1(8, bitScan ? "SFF1I32B32" : "SMovB32", 19, Gen5Operand.Scalar(18)),
            Sop2(12, "SLshlB32", 106, Gen5Operand.Scalar(19), Operand(5)),
            new(16, Gen5ShaderEncoding.Sopk, "SAddkI32", [0xB7EA0158], [new(Gen5OperandKind.LiteralConstant, 344)], [Gen5Operand.Scalar(106)], null),
            Sop2(20, "SAddI32", 107, Gen5Operand.Scalar(106), Operand(16)),
            ScalarLoad(24, 0, 4, split ? 4u : 8u, dynamicOffsetRegister: 106),
        };
        if (split) instructions.Add(ScalarLoad(32, 0, 8, 4, dynamicOffsetRegister: 107));
        instructions.AddRange([
            MoveScalar(40, 106, 999),
            MoveVector(44, 1, 0), MoveVector(48, 2, 0),
            Image(52, "ImageLoad", 4, dmask: 1, vectorAddress: 1),
            MoveScalar(60, 20, 0), MoveScalar(64, 21, 0), MoveScalar(68, 22, 64), MoveScalar(72, 23, 0),
            BufferAccess(76, "BufferStoreDword", 20, vectorData: 4), EndProgram(84),
        ]);
        return Program([.. instructions]);
    }

    public static bool ReadDescriptor(ulong address, out uint word)
    {
        word = 0;
        if (address < 0x1000 + 312 || address >= 0x1000 + 344 + 32 * 32) return false;
        var offset = address - 0x1000 - 312;
        var record = offset / 32;
        var component = offset % 32 / 4;
        word = component switch
        {
            0 => (record & 1) == 0 ? 0x2000u : 0x1000u,
            1 => 20u << 20,
            3 => 0xFACu | (9u << 28),
            _ => 0,
        };
        return true;
    }

    public static Gen5ShaderProgram CreateGuardedProgram(uint mask = 1, bool split = true)
    {
        var program = CreateProgram(mask, split);
        return program with
        {
            Instructions = program.Instructions.Where(instruction => instruction.Pc < 8)
                .Concat([
                    Sopc(8, "SCmpLgU32", Operand(0), Gen5Operand.Scalar(18)),
                    Branch(12, "SCbranchScc0", 19),
                ])
                .Concat(program.Instructions.Where(instruction => instruction.Pc >= 8)
                    .Select(instruction => instruction with { Pc = instruction.Pc + 8 })).ToArray(),
        };
    }

    public static (ShaderResourcePlan Plan, ResourceSnapshot Snapshot, ShaderCompileRequest Request) PrepareDirect(uint mask = 1, bool split = true, bool guarded = false)
    {
        var program = guarded ? CreateGuardedProgram(mask, split) : CreateProgram(mask, split);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: ReadDescriptor), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 2),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        return (plan, snapshot, new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 });
    }

    [Fact]
    public void WaveIndexedDescriptorTableMaterializesOnlyActiveMaskKeys()
    {
        var plan = ShaderResourcePlan.Extract(CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 2);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.True(selector.Dense);
        Assert.Equal(0u, selector.KeyBound);
        Assert.Equal(new WaveIndexedImageSelector(0x80, 0x40, 0x90), selector.WaveIndexed);

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: ReadWaveIndexedMemory), ref snapshot, ref specialization));
        Assert.Equal(2, snapshot.Images.Length);
    }

    [Fact]
    public void CandidateWithAnUnsupportedFormatIsMaterializedAsNull()
    {
        var plan = ShaderResourcePlan.Extract(CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 2);
        bool Read(ulong address, out uint word)
        {
            var success = ReadWaveIndexedMemory(address, out word);
            if (address == 0x1000 + 0x100 + 5 * 32 + 4) word = 139u << 20;
            return success;
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization, out var failure), $"materialize {failure}");
        Assert.Equal(2, snapshot.Images.Length);
        Assert.Single(snapshot.Images, image => image.All(word => word == 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLaneWaterfallSelectsWaveIndexedDescriptors(bool selfAddressed)
    {
        var program = CreateWaveIndexedReadLaneProgram(selfAddressed);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.True(selector.Dense, "dense");
        Assert.Equal(0x100u, selector.TableOffset);
        Assert.Equal(new WaveIndexedImageSelector(0x80, 0x40, 0x90), selector.WaveIndexed);
        Assert.True(Assert.Single(plan.IndirectImages).KeyIsAddressOffset, "address-offset key");

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: ReadWaveIndexedMemory), ref snapshot, ref specialization, out var failure), $"materialize {failure}");
        Assert.Equal(2, snapshot.Images.Length);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 2),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 64 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    [Fact]
    public void ReadLaneWithoutRestoredExecutionIsNotWaveIndexed()
    {
        Assert.Throws<ResourcePlanException>(() =>
            ShaderResourcePlan.Extract(CreateWaveIndexedReadLaneProgram(selfAddressed: true, restoreExec: false), ShaderStage.Compute, Hash, 0, 2));
    }

    private static bool ReadWaveIndexedMemory(ulong address, out uint word)
    {
        word = 0;
        if (address == 0x1000 + 0x80)
        {
            word = (1u << 1) | (1u << 4);
            return true;
        }

        if (address == 0x1000 + 0x40 + 0x90 || address == 0x1000 + 0x40 + 4 * 0x90)
        {
            word = address == 0x1000 + 0x40 + 0x90 ? 2u : 5u;
            return true;
        }

        if (address < 0x1000 + 0x100 || address >= 0x1000 + 0x100 + 6 * 32)
            return false;
        var relative = address - 0x1000 - 0x100;
        var record = relative / 32;
        if (record is not (2 or 5))
            return false;
        word = (relative % 32 / 4) switch
        {
            0 => (record & 1) == 0 ? 0x2000u : 0x1000u,
            1 => 20u << 20,
            3 => 0xFACu | (9u << 28),
            _ => 0,
        };
        return true;
    }

    public static (ResourceSnapshot Snapshot, ShaderCompileRequest Request) PrepareMixedDimensions(uint mask, bool arrayFirst)
    {
        var program = CreateGuardedProgram(mask);
        program = program with
        {
            Instructions = program.Instructions.Select(instruction => instruction.Pc switch
            {
                48 => MoveVector(48, 3, 1),
                60 => Image(60, "ImageLoad", 4, dimension: 5, dmask: 1, vectorAddress: 1),
                _ => instruction,
            }).ToArray(),
        };
        bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address < 0x1000 + 344 || address >= 0x1000 + 344 + 32 * 32) return false;
            var offset = address - 0x1000 - 344;
            var record = offset / 32;
            if (record is not (0 or 31)) return true;
            var array = (record == 0) == arrayFirst;
            word = (offset % 32 / 4) switch
            {
                0 => array ? 0x1000u : 0x2000u,
                1 => 20u << 20,
                3 => 0xFACu | ((array ? 13u : 9u) << 28),
                4 => array ? 1u : 0u,
                _ => 0,
            };
            return true;
        }
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 2),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        return (snapshot, new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedDimensionsRetainEveryCandidateAndUseSeparateBindings(bool arrayFirst)
    {
        var (snapshot, request) = PrepareMixedDimensions(1, arrayFirst);
        Assert.Equal(3, snapshot.Images.Length);
        var images = request.Resources.Info.Images;
        Assert.Equal(3, images[0].IndirectResources.Count);
        Assert.Contains(images, image => image.Dimension == ImageDimension.Dim2D);
        Assert.Contains(images, image => image.Dimension == ImageDimension.Dim2DArray);
        var mapping = (int)images[0].IndirectMappingOffset;
        Assert.Equal(32u, snapshot.FlattenedResourceTable[mapping]);
        Assert.Equal(344u + 31 * 32, snapshot.FlattenedResourceTable[mapping + 63]);
        Assert.Equal(2u, snapshot.FlattenedResourceTable[mapping + 64]);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out error));
        Assert.Contains("not supported on Metal", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectTableRetainsAllBitScanResultsAndCompiles(bool split)
    {
        var (plan, snapshot, request) = PrepareDirect(split: split);
        Assert.Single(plan.IndirectImages);
        Assert.True(plan.IndirectImages[0].KeyIsAddressOffset);
        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.Empty(plan.DynamicReads);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.Equal(33, selector.DirectCandidates!.Count);
        Assert.Contains(selector.DirectCandidates, candidate => candidate.Offset == 312);
        Assert.Contains(selector.DirectCandidates, candidate => candidate.Offset == 344 + 31 * 32);
        Assert.Equal(2, snapshot.Images.Length);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public void DirectImageCapacityPreservesTheLimitAndPublishedState(int distinctCount)
    {
        var (plan, snapshot, _) = PrepareDirect();
        var specialization = new ResourceSpecialization();
        var previousSnapshot = snapshot;
        var previousSpecialization = specialization;
        bool Read(ulong address, out uint word)
        {
            var success = ReadDescriptor(address, out word);
            if (success && (address - 0x1000 - 312) % 32 == 0)
                word = 0x2000 + (uint)(((address - 0x1000 - 312) / 32) % (ulong)distinctCount);
            return success;
        }

        var success = ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read),
            ref snapshot, ref specialization, out var failure);
        Assert.Equal(distinctCount <= ShaderResourceInfo.MaxImages, success);
        if (success)
        {
            Assert.Equal(ResourceMaterializationFailure.None, failure);
            Assert.Equal(distinctCount, snapshot.Images.Length);
        }
        else
        {
            Assert.Equal(ResourceMaterializationFailure.ImageCapacityExceeded, failure);
            Assert.Same(previousSnapshot, snapshot);
            Assert.Same(previousSpecialization, specialization);
        }
    }

    [Fact]
    public void UnboundedDirectTableIsStillRejected()
    {
        Assert.Throws<ResourcePlanException>(() => ShaderResourcePlan.Extract(CreateProgram(bitScan: false), ShaderStage.Compute, Hash, 0, 2));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NonZeroGuardExcludesTheUnreachableDescriptor(bool split, bool equality)
    {
        var program = CreateGuardedProgram(split: split);
        if (equality)
            program = program with
            {
                Instructions = program.Instructions.Select(instruction => instruction.Pc switch
                {
                    8 => Sopc(8, "SCmpEqI32", Gen5Operand.Scalar(18), Operand(0)),
                    12 => Branch(12, "SCbranchScc1", 19),
                    _ => instruction,
                }).ToArray(),
            };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.True(selector.Dense);
        Assert.Equal(32u, selector.KeyBound);
        Assert.Equal(344u, selector.TableOffset);
        Assert.True(Assert.Single(plan.IndirectImages).KeyIsAddressOffset);
        bool Read(ulong address, out uint word)
        {
            Assert.True(address >= 0x1000 + 344);
            return ReadDescriptor(address, out word);
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization));
        var (_, _, request) = PrepareDirect(split: split, guarded: true);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [InlineData(600u, 8u)]
    [InlineData(444u, 3u)]
    [InlineData(312u, 32u)]
    [InlineData(1400u, 32u)]
    public void DenseTableEndsBeforeASamplerLoadedFromTheSameBase(uint samplerOffset, uint expectedBound)
    {
        var program = CreateGuardedProgram();
        program = program with
        {
            Instructions = program.Instructions.Where(instruction => instruction.Pc != 52).Select(instruction => instruction.Pc switch
            {
                48 => ScalarLoad(48, 0, 12, 4, immediateOffset: (int)samplerOffset),
                60 => Image(60, "ImageSample", 4, samplerRegister: 12, dmask: 1, vectorAddress: 1),
                _ => instruction,
            }).ToArray(),
        };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.True(selector.Dense);
        Assert.Equal(344u, selector.TableOffset);
        Assert.Equal(expectedBound, selector.KeyBound);

        var highest = 0ul;
        bool Read(ulong address, out uint word)
        {
            if (address >= 0x1000 + samplerOffset && address < 0x1000 + samplerOffset + 16)
            {
                word = 0;
                return true;
            }

            if (address >= 0x1000 + 344) highest = Math.Max(highest, address);
            return ReadDescriptor(address, out word);
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], Read, Read), ref snapshot, ref specialization, out var failure), $"materialize {failure}");
        Assert.True(highest < 0x1000 + 344 + expectedBound * 32, $"read 0x{highest:X} beyond the table");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopMustRecheckTheBitScanInput(bool bypassGuard)
    {
        var program = CreateGuardedProgram();
        program = program with
        {
            Instructions = program.Instructions.Where(instruction => instruction.Pc < 92)
                .Select(instruction => instruction.Pc == 12 ? Branch(12, "SCbranchScc0", 21) : instruction)
                .Concat([
                    ReadFirstLane(92, 18, 13),
                    Branch(96, "SBranch", bypassGuard ? (short)-21 : (short)-23),
                    EndProgram(100),
                ]).ToArray(),
        };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        if (bypassGuard)
        {
            Assert.Equal(33, selector.DirectCandidates!.Count);
            Assert.Contains(selector.DirectCandidates, candidate => candidate.Offset == 312);
        }
        else
        {
            Assert.True(selector.Dense);
            Assert.Equal(32u, selector.KeyBound);
            Assert.Equal(344u, selector.TableOffset);
        }
    }

    [Theory]
    [InlineData("opposite-branch")]
    [InlineData("different-register")]
    [InlineData("guard-bypass")]
    [InlineData("condition-write")]
    [InlineData("input-write")]
    public void UnprovenGuardRetainsTheZeroInputDescriptor(string variation)
    {
        var program = CreateGuardedProgram();
        var instructions = program.Instructions.ToList();
        switch (variation)
        {
            case "opposite-branch":
                instructions[instructions.FindIndex(instruction => instruction.Pc == 12)] = Branch(12, "SCbranchScc1", 19);
                break;
            case "different-register":
                instructions[instructions.FindIndex(instruction => instruction.Pc == 8)] =
                    Sopc(8, "SCmpLgU32", Operand(0), Gen5Operand.Scalar(17));
                break;
            case "guard-bypass":
                instructions[0] = Branch(0, "SCbranchScc1", 3);
                break;
            case "condition-write":
                instructions = instructions.Select(instruction => instruction.Pc >= 12
                    ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
                instructions.Add(Sopc(12, "SCmpEqU32", Operand(0), Operand(0)));
                break;
            case "input-write":
                instructions = instructions.Select(instruction => instruction.Pc >= 16
                    ? instruction with { Pc = instruction.Pc + 4 } : instruction).ToList();
                instructions[instructions.FindIndex(instruction => instruction.Pc == 12)] = Branch(12, "SCbranchScc0", 20);
                instructions.Add(ReadFirstLane(16, 18, 13));
                break;
        }
        program = program with { Instructions = instructions.OrderBy(instruction => instruction.Pc).ToArray() };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var candidates = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.DirectCandidates!;
        Assert.Equal(33, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Offset == 312);
    }

    [Fact]
    public void DescriptorWordsUsedByArithmeticAreNotRemoved()
    {
        var program = CreateProgram();
        program = program with
        {
            Instructions = program.Instructions.Select(instruction => instruction.Pc == 40
                ? Vop1(40, "VMovB32", 15, Gen5Operand.Scalar(4)) : instruction).ToArray(),
        };
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        Assert.Single(plan.IndirectImages);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory.TryGetIndex(24, 0, out var memoryIndex));
        Assert.False(plan.Memory[memoryIndex].PlanningOnly);
    }

    [Fact]
    public void DirectTableSlotWithAnUnsupportedFormatIsMaterializedAsNull()
    {
        var program = CreateProgram();
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 2);
        var unsupported = true;
        bool Read(ulong address, out uint word)
        {
            var success = ReadDescriptor(address, out word);
            if (unsupported && address == 0x1000 + 344 + 3 * 32 + 4) word = 139u << 20;
            return success;
        }

        var clean = new ResourceSnapshot();
        var cleanSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: ReadDescriptor), ref clean, ref cleanSpecialization));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization, out var failure), $"materialize {failure}");
        Assert.Equal(clean.Images.Length + 1, snapshot.Images.Length);
        Assert.Single(snapshot.Images, image => image.All(word => word == 0));
        unsupported = false;
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization, out failure), $"materialize {failure}");
        Assert.Equal(clean.Images.Length, snapshot.Images.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedReadOrIncompatibleCandidateDoesNotPublish(bool incompatible, bool guarded)
    {
        var (plan, original, _) = PrepareDirect(guarded: guarded);
        var snapshot = original;
        var specialization = new ResourceSpecialization();
        var originalSpecialization = specialization;
        bool Read(ulong address, out uint word)
        {
            var success = ReadDescriptor(address, out word);
            if (address == 0x1000 + 344 + 12)
            {
                if (!incompatible) return false;
                word = (word & 0x0FFFFFFF) | (11u << 28);
            }
            return success;
        }
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: Read), ref snapshot, ref specialization,
            out var failure));
        Assert.Equal(incompatible ? ResourceMaterializationFailure.IncompatibleImageCandidates : ResourceMaterializationFailure.Other, failure);
        Assert.Same(original, snapshot);
        Assert.Same(originalSpecialization, specialization);
    }
}
