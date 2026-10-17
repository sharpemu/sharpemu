// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// The Metal request path declares one argument buffer per stage whose fields follow the layout.
public sealed class MslArgumentBufferDeclarationTests
{
    private const uint ImageType2D = 9;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnsignedMaximumUsesTheDecodedGlobalOpcode(bool flat, bool returnsValue)
    {
        var program = Program(
            GlobalMemory(0, flat ? "FlatAtomicUMax" : "GlobalAtomicUMax",
                scalarAddress: 0, vectorAddress: 2, destination: 8, source: 4, glc: returnsValue),
            EndProgram(8));
        var request = Request(program);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("atomic_fetch_max_explicit", shader.Source);
        Assert.Contains("device atomic_uint*", shader.Source);
    }

    private static IEnumerable<Gen5ShaderInstruction> ImageWords(ref uint pc, uint register, uint address, uint format)
    {
        uint[] words = [address, format << 20, 3 | (3 << 14), 0xFAC | (ImageType2D << 28), 0, 0, 0, 0];
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint dword = 0; dword < 8; dword++)
        {
            instructions.Add(MoveScalar(pc, register + dword, words[dword]));
            pc += 8;
        }

        return instructions;
    }

    // A program touching every binding kind: a buffer, a sampled image with its sampler,
    // a storage image, a flattened scalar read, a device-address load and a GDS write.
    private static Gen5ShaderProgram EveryKindProgram()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, 16, 0x1000, 22));
        instructions.AddRange(ImageWords(ref pc, 24, 0x2000, 22));
        instructions.Add(MoveScalar(pc, 32, 0x100)); pc += 8;
        instructions.Add(MoveScalar(pc, 33, 0)); pc += 8;
        instructions.Add(MoveScalar(pc, 34, 0)); pc += 8;
        instructions.Add(MoveScalar(pc, 35, 0)); pc += 8;
        instructions.Add(ScalarLoad(pc, 0, destination: 8, count: 4, immediateOffset: 32)); pc += 8;
        instructions.Add(BufferLoad(pc, 8)); pc += 8;
        instructions.Add(BufferLoad(pc, 4)); pc += 8;
        instructions.Add(Image(pc, "ImageSample", 16, 32)); pc += 8;
        instructions.Add(Image(pc, "ImageStore", 24)); pc += 8;
        instructions.Add(GlobalMemory(pc, "GlobalLoadDword", 2, 0, 4, 4)); pc += 8;
        instructions.Add(DataShareWrite(pc, gds: true)); pc += 8;
        instructions.Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    private static Gen5MslShader Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }

    [Fact]
    public void ArgumentBufferFields_FollowTheLayout()
    {
        var request = Request(EveryKindProgram());
        var shader = Compile(request);
        var layout = Gen5MslArgumentLayout.Build(request.Bindings);

        Assert.NotNull(shader.ArgumentLayout);
        Assert.Equal(layout.Fields, shader.ArgumentLayout!.Fields);
        foreach (var field in layout.Fields)
        {
            Assert.Contains($" {field.Name} [[id({field.FirstId})]];", shader.Source);
        }

        Assert.Contains($"constant {Gen5MslArgumentLayout.StructName}& sharpemu_resources [[buffer({Gen5MslArgumentLayout.ResourcesBufferIndex})]]", shader.Source);
        Assert.Contains($"constant uint* sharpemu_push_data [[buffer({Gen5MslArgumentLayout.PushDataBufferIndex})]]", shader.Source);
        Assert.Contains("sharpemu_resolve_device_address(", shader.Source);
        Assert.Contains("atomic_fetch_or_explicit(", shader.Source);
        Assert.Contains(".sample(", shader.Source);
        Assert.Contains("sharpemu_gds[", shader.Source);
        var bufferCount = request.Resources.Info.Buffers.Count;
        Assert.Contains($"array<device uint*, {bufferCount}> buffers [[id(0)]];", shader.Source);
        Assert.Contains($"array<uint, {bufferCount}> buffer_bytes [[id({bufferCount})]];", shader.Source);
        Assert.Contains("array<sampler, 1> samplers", shader.Source);
        Assert.Contains("array<texture2d<float>, 1> sampled_float_2d", shader.Source);
        Assert.Contains("array<texture2d<float, access::write>, 1> storage_float_2d", shader.Source);
    }

    [Fact]
    public void ArgumentLayout_PacksFieldsWithNaturalAlignment()
    {
        var request = Request(EveryKindProgram());
        var layout = Gen5MslArgumentLayout.Build(request.Bindings);

        uint expectedId = 0;
        uint cursor = 0;
        foreach (var field in layout.Fields)
        {
            var elementBytes = field.FieldKind is MslArgumentFieldKind.BufferByteCounts or MslArgumentFieldKind.Word ? 4u : 8u;
            cursor = (cursor + elementBytes - 1) & ~(elementBytes - 1);
            Assert.Equal(expectedId, field.FirstId);
            Assert.Equal(cursor, field.ByteOffset);
            Assert.Equal(field.Count * elementBytes, field.ByteSize);
            expectedId += field.Count;
            cursor += field.ByteSize;
        }

        Assert.Equal(0u, layout.ByteSize % 8);
        Assert.True(layout.ByteSize >= cursor);
        Assert.NotNull(layout.Find(DescriptorBindingKind.Buffers, MslArgumentFieldKind.BufferPointers));
        Assert.NotNull(layout.Find(DescriptorBindingKind.DeviceAddressPageTable, MslArgumentFieldKind.Pointer));
    }

    [Fact]
    public void PixelStage_BindsTheSameArgumentBufferSlots()
    {
        var request = Request(Program(BufferLoad(0, 4), EndProgram(8)), ShaderStage.Pixel);
        var shader = Compile(request);

        Assert.Contains("[[buffer(0)]]", shader.Source);
        Assert.Contains("fragment Gen5PsOut gen5_ps(", shader.Source);
        Assert.NotNull(shader.ArgumentLayout);
        Assert.DoesNotContain("SharpEmuUniforms", shader.Source);
    }

    [Fact]
    public void ProgramWithoutDeviceAddresses_HasNoRangeTable()
    {
        var request = Request(Program(BufferLoad(0, 4), EndProgram(8)));
        var shader = Compile(request);

        Assert.DoesNotContain("address_ranges", shader.Source);
        Assert.DoesNotContain("sharpemu_uniforms", shader.Source);
        Assert.Contains("sharpemu_resources.buffer_bytes[0]", shader.Source);
    }

    [Fact]
    public void SpilledUserData_ReadsTheShaderDataBufferInsteadOfPushData()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (uint register = 0; register < 36; register += 4)
        {
            instructions.Add(BufferLoad(pc, register));
            pc += 8;
        }

        instructions.Add(EndProgram(pc));
        var request = Request(Program([.. instructions]), pushDataStartDword: 0);
        Assert.False(request.Bindings.UsesPushData);
        var shader = Compile(request);

        Assert.DoesNotContain("sharpemu_push_data", shader.Source);
        Assert.Contains("sharpemu_resources.shader_data[", shader.Source);
    }

    [Fact]
    public void ShaderBaseRead_TakesTheBaseFromPushData()
    {
        var program = Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sop1, "SGetpcB64", [0u], [], [Gen5Operand.Scalar(4)], null),
            Sop2(4, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(0x100)),
            Sop2(8, "SAddcU32", 5, Gen5Operand.Scalar(5), Operand(0)),
            ScalarLoad(12, 4, destination: 8, count: 4),
            BufferLoad(20, 8),
            EndProgram(28));
        var request = Request(program);
        Assert.True(request.Bindings.UsesShaderBase);
        var shader = Compile(request);

        var baseDword = request.Bindings.PushDataStartDword + request.Bindings.ShaderBaseDword;
        Assert.Contains($"((ulong)sharpemu_push_data[{baseDword}] | ((ulong)sharpemu_push_data[{baseDword + 1}] << 32)) + 4ul", shader.Source);
        Assert.DoesNotContain("0x100000", shader.Source);
    }

    [Fact]
    public void ComputeThreadLimits_AreConstantsFromTheRequest()
    {
        var (plan, resources, layout) = Prepare(Program(BufferLoad(0, 4), EndProgram(8)));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, ThreadCountX = 100 };
        var shader = Compile(request);

        Assert.Contains("(sharpemu_group_id.x * 64u + sharpemu_local_id.x) < 0x64u", shader.Source);
        Assert.Contains("(sharpemu_group_id.y * 1u + sharpemu_local_id.y) < 0xFFFFFFFFu", shader.Source);
        Assert.Equal(64u, shader.ThreadgroupSizeX);
    }

    [Fact]
    public void DepthCompareSample_KeepsTheManualCompare()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, 16, 0x1000, 22));
        instructions.Add(MoveScalar(pc, 32, 0)); pc += 8;
        instructions.Add(MoveScalar(pc, 33, 0)); pc += 8;
        instructions.Add(MoveScalar(pc, 34, 0)); pc += 8;
        instructions.Add(MoveScalar(pc, 35, 0)); pc += 8;
        instructions.Add(Image(pc, "ImageSampleC", 16, 32)); pc += 8;
        instructions.Add(EndProgram(pc));
        var request = Request(Program([.. instructions]), ShaderStage.Pixel);
        var shader = Compile(request);

        Assert.DoesNotContain("sample_compare", shader.Source);
        Assert.Contains("<= (float)", shader.Source);
        Assert.Contains("sampler t", shader.Source);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(32u)]
    public void RuntimeComputeLimitsReadTheCurrentShaderData(uint pushCursor)
    {
        var (plan, resources, originalLayout) = Prepare(Program(BufferLoad(0, 4), EndProgram(8)));
        var layout = BindingLayout.Allocate(resources.Info, originalLayout.UserDataRegisters, false,
            originalLayout.Find(DescriptorBindingKind.FlattenedResourceTable) is not null, false,
            pushCursor, usesDispatchThreadLimits: true);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64 };
        var shader = Compile(request);
        var offset = layout.DispatchThreadLimitsDword;
        if (layout.UsesPushData)
        {
            Assert.Contains($"(sharpemu_group_id.x * 64u + sharpemu_local_id.x) < sharpemu_push_data[{offset}]", shader.Source);
        }
        else
        {
            Assert.Contains($"shader_data[{offset}u]", shader.Source);
            Assert.Contains($"shader_data[{offset + 2}u]", shader.Source);
        }

        Assert.DoesNotContain("< 0xFFFFFFFFu", shader.Source);
    }

    [Fact]
    public void GlobalDataShareAppend_CountsTheActiveLanesWithADeviceAtomic()
    {
        var program = Program(
            MoveScalar(0, 124, 64),
            DataShare(8, "DsAppend", gds: true, [Gen5Operand.Scalar(124)], [1]),
            EndProgram(16));
        var shader = Compile(Request(program));

        Assert.Contains("atomic_fetch_add_explicit((device atomic_uint*)(sharpemu_gds + ", shader.Source);
        Assert.Contains("simd_broadcast(", shader.Source);
    }

    [Fact]
    public void WrittenGlobalStore_IsBoundedByItsRange()
    {
        var program = Program(
            MoveScalar(0, 4, 0x1000),
            MoveScalar(8, 5, 0),
            MoveVector(16, 0, 0),
            MoveVector(24, 1, 7),
            GlobalMemory(32, "GlobalStoreDword", 4, 0, 1, 1),
            EndProgram(40));
        var request = Request(program);
        Assert.NotEmpty(request.WrittenRangeSlotByMemoryIndex);
        var shader = Compile(request);

        Assert.Contains("sharpemu_store_device_dword(", shader.Source);
        Assert.Contains("sharpemu_resources.flattened_table[", shader.Source);
        Assert.Contains("4ul <= ", shader.Source);
    }

    [Fact]
    public void IndirectImage_CompilesWithoutTheHeapDescriptorRead()
    {
        var request = SpirvBindingDeclarationTests.IndirectImageRequest();
        var shader = Compile(request);

        Assert.Contains("sharpemu_resources.flattened_table[", shader.Source);
        Assert.Contains("indirect_image_key", shader.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectImageAfterAPlainImage_SelectsByCandidateIndex(bool gpuDependentSelector)
    {
        var (request, _) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageRequest(gpuDependentSelector: gpuDependentSelector);
        var shader = Compile(request);

        // Candidate 0 is the root at element 1, candidate 1 the second image at element 2; no dynamic index.
        Assert.Contains("== 0u)", shader.Source);
        Assert.Contains("== 1u)", shader.Source);
        Assert.Contains("sampled_float_2d[1u]", shader.Source);
        Assert.Contains("sampled_float_2d[2u]", shader.Source);
        Assert.DoesNotContain("sampled_float_2d[t", shader.Source);
    }

    [Fact]
    public void IndirectImageMipLoad_KeepsTheMipOperandInEveryCase()
    {
        var (request, _) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageRequest(loadMip: true);
        var shader = Compile(request);

        // Both candidate cases read with the register mip, never the base level.
        var reads = shader.Source.Split('\n').Where(line => line.Contains(".read(uint2(", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, line => Assert.DoesNotContain(", 0u)", line));
        Assert.All(reads, line => Assert.Matches(@"\), t\d+\);", line));
    }

    [Fact]
    public void DynamicMipStorageImage_TakesOneConstantCasePerMip()
    {
        var shader = Compile(SpirvBindingDeclarationTests.DynamicMipRequest());

        Assert.Contains("== 0u)", shader.Source);
        Assert.Contains("== 1u)", shader.Source);
        Assert.Contains("storage_uint_2d[0u]", shader.Source);
        Assert.Contains("storage_uint_2d[1u]", shader.Source);
        Assert.DoesNotContain("min(", shader.Source);
    }

    [Fact]
    public void LayoutThatDoesNotMatchTheResources_IsRejectedBeforeEmission()
    {
        var program = Program(BufferLoad(0, 4), EndProgram(8));
        var (plan, resources, _) = Prepare(program);
        var wrong = BindingLayout.Allocate(resources.Info, [0], false, false, false);
        var request = new ShaderCompileRequest(plan, resources, wrong);

        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void LaneOperationOnTheGlobalDataShare_IsRejected()
    {
        var program = Program(
            DataShare(0, "DsSwizzleB32", gds: true, [Gen5Operand.Vector(0)], [1]),
            EndProgram(8));
        var request = Request(program);

        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("not valid on the global data share", error);
    }
}
