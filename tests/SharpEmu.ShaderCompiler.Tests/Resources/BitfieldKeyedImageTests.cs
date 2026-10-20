// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A descriptor read at (key << 5) from a table, where the key is a bitfield of a user-data word: a finite set of
// candidates, so it is bound ahead of time like any other dense table, loop or not.
public sealed class BitfieldKeyedImageTests
{
    private static Gen5ShaderProgram Program(bool inLoop, string imageOpcode, uint width = 5)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Func<uint, Gen5ShaderInstruction> make)
        {
            var instruction = make(pc);
            instructions.Add(instruction);
            pc += (uint)instruction.Words.Count * sizeof(uint);
        }

        // s[0:1] is the table base, s2 holds the key in bits 16..16+width.
        Add(at => Sop2(at, "SBfeU32", 8, Gen5Operand.Scalar(2), Operand(0x0000_0010u | (width << 16))));
        Add(at => Sop2(at, "SLshlB32", 9, Gen5Operand.Scalar(8), Operand(5)));
        Add(at => ScalarLoad(at, 0, destination: 16, count: 8, immediateOffset: 16, dynamicOffsetRegister: 9));
        Add(at => MoveScalar(at, 20 + 8, 0));
        Add(at => MoveVector(at, 1, 0));
        Add(at => MoveVector(at, 2, 0));
        var loopHead = pc;
        Add(at => Image(at, imageOpcode, 16, vectorAddress: 1, dmask: 1));
        if (inLoop)
        {
            Add(at => Sop2(at, "SAddI32", 28, Gen5Operand.Scalar(28), Operand(1)));
            Add(at => Sopc(at, "SCmpLtI32", Gen5Operand.Scalar(28), Operand(4)));
            Add(at => Branch(at, "SCbranchScc1", (short)((int)loopHead - (int)(at + 4) >> 2)));
        }

        Add(EndProgram);
        return ResourceTestProgram.Program([.. instructions]);
    }

    [Theory]
    [InlineData(false, "ImageStore")]
    [InlineData(true, "ImageStore")]
    [InlineData(true, "ImageLoad")]
    public void TheKeyBitfieldBoundsTheTable(bool inLoop, string opcode)
    {
        var plan = ShaderResourcePlan.Extract(Program(inLoop, opcode), ShaderStage.Compute, Hash, 0, 4);

        var image = Assert.Single(plan.Info.Images);
        var selector = plan.DescriptorSources[(int)image.Source].IndirectImage!;
        Assert.True(selector.Dense);
        Assert.Equal(32u, selector.KeyBound);
        Assert.Equal(16u, selector.TableOffset);
        Assert.False(plan.Info.UsesRuntimeDescriptors);
    }

    [Fact]
    public void ABitfieldTooWideToEnumerateIsNotBoundedAsADenseTable()
    {
        // 2^13 candidates exceed what a draw binds; the planner does not pretend to bound them.
        var plan = ShaderResourcePlan.Extract(Program(inLoop: false, "ImageStore", width: 13), ShaderStage.Compute, Hash, 0, 4);

        Assert.DoesNotContain(plan.DescriptorSources, source => source.IndirectImage is { Dense: true, KeyBound: > 0 });
    }

    private const uint ImageType1D = 8;

    [Theory]
    [InlineData("ImageStore")]
    [InlineData("ImageLoad")]
    public void AStorageTableMaterializesPastCandidatesOfAnotherImageFamily(string opcode)
    {
        var plan = ShaderResourcePlan.Extract(Program(inLoop: false, opcode), ShaderStage.Compute, Hash, 0, 4);
        var memory = ResourceTrackerTests.LinearMemory();
        for (var key = 0; key < 32; key++)
        {
            var descriptor = ResourceTrackerTests.ImageDescriptor();
            descriptor[0] += (uint)key * 0x10;
            if (key == 5)
            {
                descriptor[3] = (descriptor[3] & 0x0FFFFFFF) | (ImageType1D << 28);
            }

            ResourceTrackerTests.WriteImage(memory, 0x1010 + (ulong)key * 32, descriptor);
        }

        uint[] userData = [0x1000, 0, 0, 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
            ref snapshot, ref specialization, out var failure));
        Assert.Equal(ResourceMaterializationFailure.None, failure);
        Assert.Equal(32, snapshot.Images.Length);
        // The 1D candidate can never be addressed by the declared 2D access: it is bound as a typed null.
        Assert.All(snapshot.Images[5], word => Assert.Equal(0u, word));
        Assert.NotEqual(0u, snapshot.Images[6][0]);
    }

    [Fact]
    public void ABoundedStorageImageTableCompilesToAValidModule()
    {
        var request = Request(Program(inLoop: true, "ImageStore"), userDataCount: 4);

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
    }
}
