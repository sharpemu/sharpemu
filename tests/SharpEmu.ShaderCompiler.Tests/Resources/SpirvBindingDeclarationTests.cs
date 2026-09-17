// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class SpirvBindingDeclarationTests
{
    private const uint ImageType2D = 9;

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

    private static HashSet<(uint Set, uint Binding)> ExpectedBindings(BindingLayout layout, ShaderStage stage) =>
        layout.Descriptors.Select(descriptor => (0u, BindingLayout.NativeBindingIndex(stage, descriptor.Kind))).ToHashSet();

    [Fact]
    public void DeclaredBindingsEqualTheLayout()
    {
        var program = EveryKindProgram();
        var request = Request(program);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        var layout = request.Bindings;
        Assert.Contains(layout.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.Buffers);
        Assert.Contains(layout.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.Samplers);
        Assert.Contains(layout.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.GlobalDataShare);
        Assert.Contains(layout.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.DeviceAddressPageTable);
        Assert.Contains(layout.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.FaultBuffer);
        Assert.Contains(layout.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.FlattenedResourceTable);
        Assert.Equal(2, layout.Descriptors.Count(descriptor => ImageDescriptorBinding.ResourceClass(descriptor.Kind) != ImageResourceClass.None));
        Assert.Equal(ExpectedBindings(layout, ShaderStage.Compute), module.DescriptorBindings);
        Assert.True(layout.UsesPushData);
        Assert.True(module.HasVariableInStorageClass(SpirvStorageClass.PushConstant));
        Assert.Equal(5348u, module.AddressingModel);
        Assert.Contains((uint)SpirvCapability.PhysicalStorageBufferAddresses, module.Capabilities);
        Assert.Contains((ushort)SpirvOp.SampledImage, module.Opcodes);
        Assert.Contains((ushort)SpirvOp.ConvertUToPtr, module.Opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicOr, module.Opcodes);
    }

    [Fact]
    public void PixelStage_OffsetsEveryBindingByTheStageCount()
    {
        var program = Program(BufferLoad(0, 4), EndProgram(8));
        var request = Request(program, ShaderStage.Pixel);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Equal(ExpectedBindings(request.Bindings, ShaderStage.Pixel), module.DescriptorBindings);
        Assert.Equal((uint)DescriptorBindingKind.Count, module.BindingOf("guestBuffers"));
    }

    [Fact]
    public void ProgramWithoutDeviceAddresses_KeepsLogicalAddressing()
    {
        var request = Request(Program(BufferLoad(0, 4), EndProgram(8)));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Equal(0u, module.AddressingModel);
        Assert.DoesNotContain((uint)SpirvCapability.PhysicalStorageBufferAddresses, module.Capabilities);
        Assert.DoesNotContain(request.Bindings.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.DeviceAddressPageTable);
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
        Assert.Contains(request.Bindings.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.ShaderData);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.False(module.HasVariableInStorageClass(SpirvStorageClass.PushConstant));
        Assert.Equal((uint)DescriptorBindingKind.ShaderData, module.BindingOf("shaderData"));
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
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.True(module.HasVariableInStorageClass(SpirvStorageClass.PushConstant));
        Assert.Contains((ushort)SpirvOp.UConvert, module.Opcodes);
    }

    [Fact]
    public void DepthCompareSample_UsesADrefSampleOperation()
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
        Assert.True(request.Resources.Info.Samplers[0].DepthCompare);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.ImageSampleDrefImplicitLod, module.Opcodes);
    }

    // The materialised indirect-image request of the tracker fixture: two heap descriptors, one root.
    internal static ShaderCompileRequest IndirectImageRequest()
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        var memory = ResourceTrackerTests.LinearMemory();
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        ResourceTrackerTests.WriteImage(memory, 0x2000, descriptor);
        ResourceTrackerTests.WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2020) ^= 1;
        memory.At(0x1000 + 36) = 1;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.NotEqual(0u, resources.Info.Images[0].IndirectSearchIterations);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64),
            false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            false);
        return new ShaderCompileRequest(plan, resources, layout);
    }

    // A plain sampled image before the indirect root, so the root is image 1 and its second
    // candidate image 2: the mapping's candidate-local index 1 must select image 2.
    internal static Gen5ShaderProgram IndirectImageAfterPlainImageProgram(bool loadMip = false, bool gpuDependentSelector = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction) { instructions.Add(instruction); pc += 8; }

        Add(gpuDependentSelector
            ? Vop2(pc, "VAddI32", 1, Operand(1), Gen5Operand.Vector(0))
            : Vop1(pc, "VMovB32", 1, Gen5Operand.Scalar(8)));
        Add(ReadFirstLane(pc, 9, 1));
        Add(Sop2(pc, "SMulI32", 10, Gen5Operand.Scalar(9), Operand(224)));
        Add(Sop2(pc, "SAddU32", 11, Gen5Operand.Scalar(10), Operand(4)));
        Add(ScalarBufferLoad(pc, 0, destination: 12, dynamicOffsetRegister: 11));
        Add(Sop2(pc, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5)));
        Add(ScalarBufferLoad(pc, 4, destination: 16, count: 8, dynamicOffsetRegister: 13));
        for (uint index = 0; index < 4; index++)
        {
            Add(MoveScalar(pc, 24 + index, 0));
        }

        uint[] plain = [0x30, 77u << 20, 3 | (3 << 14), 0xFAC | (ImageType2D << 28), 0, 0, 0, 0];
        for (uint dword = 0; dword < 8; dword++)
        {
            Add(MoveScalar(pc, 40 + dword, plain[dword]));
        }

        Add(MoveVector(pc, 0, 0x3F00_0000));
        Add(MoveVector(pc, 1, 0x3F00_0000));
        Add(Image(pc, "ImageSampleLz", 40, 24, dmask: 1));
        Add(BufferAccess(pc, "BufferStoreDword", 52, 0, 1, vectorData: 4));
        if (loadMip)
        {
            // Integer texel (0, 0) of mip 1 through the mip operand after the coordinates.
            Add(MoveVector(pc, 5, 0));
            Add(MoveVector(pc, 6, 0));
            Add(MoveVector(pc, 7, 1));
            Add(Image(pc, "ImageLoadMip", 16, vectorAddress: 5, dmask: 1));
        }
        else
        {
            Add(Image(pc, "ImageSampleLz", 16, 24, dmask: 1));
        }

        Add(BufferAccess(pc, "BufferStoreDword", 52, 4, 1, vectorData: 4));
        Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    // User data and memory for that program: record 1 of the material table selects heap descriptor 1.
    internal static (uint[] UserData, TestWordMemory Memory) IndirectImageAfterPlainImageInputs(uint resultBytes, bool twoLevels = false)
    {
        var userData = new uint[64];
        uint[] tables = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 1];
        tables.CopyTo(userData, 0);
        userData[54] = resultBytes;
        var memory = ResourceTrackerTests.LinearMemory();
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        if (twoLevels)
        {
            descriptor[3] |= 1u << 16;
        }

        ResourceTrackerTests.WriteImage(memory, 0x2000, descriptor);
        ResourceTrackerTests.WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2020) ^= 1;
        memory.At(0x1000 + 228) = 1;
        return (userData, memory);
    }

    internal static (ShaderCompileRequest Request, ResourceSnapshot Snapshot) IndirectImageAfterPlainImageRequest(uint resultBytes = 64, bool loadMip = false, bool gpuDependentSelector = false)
    {
        var plan = Extract(IndirectImageAfterPlainImageProgram(loadMip, gpuDependentSelector));
        var (userData, memory) = IndirectImageAfterPlainImageInputs(resultBytes, loadMip);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(3, resources.Info.Images.Count);
        Assert.Equal(1u, resources.Info.Images[1].IndirectRoot);
        Assert.Equal([1u, 2u], resources.Info.Images[1].IndirectResources);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64),
            false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            false);
        return (new ShaderCompileRequest(plan, resources, layout), snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndirectImageAfterAPlainImage_SelectsByCandidateIndex(bool gpuDependentSelector)
    {
        var (request, snapshot) = IndirectImageAfterPlainImageRequest(gpuDependentSelector: gpuDependentSelector);
        var mapping = request.Resources.Info.Images[1].IndirectMappingOffset;
        Assert.Equal(2u, snapshot.FlattenedResourceTable[mapping]);
        Assert.Equal(1u, snapshot.FlattenedResourceTable[mapping + 4]);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains((ushort)SpirvOp.Select, new SpirvModuleInspector(shader.Spirv).Opcodes);
    }

    [Fact]
    public void GlobalDataShareAppendInAPixelStage_BroadcastsTheCounter()
    {
        var program = Program(
            MoveScalar(0, 124, 64),
            DataShare(8, "DsAppend", gds: true, [Gen5Operand.Scalar(124)], [1]),
            EndProgram(16));
        var request = Request(program, ShaderStage.Pixel);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, module.Opcodes);
        Assert.Contains((uint)SpirvCapability.GroupNonUniformShuffle, module.Capabilities);
    }

    // A storage image whose descriptor spans two mips, written through a register-selected mip.
    internal static Gen5ShaderProgram DynamicMipStoreProgram()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        uint[] words = [0x1000, 20u << 20, 3 | (3 << 14), 0xFAC | (1u << 16) | (ImageType2D << 28), 0, 0, 0, 0];
        for (uint dword = 0; dword < 8; dword++)
        {
            instructions.Add(MoveScalar(pc, 16 + dword, words[dword]));
            pc += 8;
        }

        instructions.Add(MoveVector(pc, 1, 0)); pc += 8;
        instructions.Add(MoveVector(pc, 2, 0)); pc += 8;
        instructions.Add(Vop1(pc, "VMovB32", 3, Gen5Operand.Vector(0))); pc += 8;
        instructions.Add(Vop1(pc, "VMovB32", 4, Gen5Operand.Vector(0))); pc += 8;
        instructions.Add(Image(pc, "ImageStoreMip", 16, vectorAddress: 1, dmask: 1)); pc += 8;
        instructions.Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    [Fact]
    public void IndirectImage_CompilesWithoutTheHeapDescriptorRead()
    {
        var request = IndirectImageRequest();
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.Select, module.Opcodes);
        Assert.DoesNotContain((uint)SpirvCapability.SampledImageArrayDynamicIndexing, module.Capabilities);
        Assert.Contains(request.Bindings.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.FlattenedResourceTable);
    }

    // The materialised request of the dynamic-mip program: the descriptor's two levels become two elements.
    internal static ShaderCompileRequest DynamicMipRequest()
    {
        var plan = Extract(DynamicMipStoreProgram());
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(ImageMipMode.DynamicStorage, resources.Info.Images[0].MipMode);
        Assert.Equal(2u, resources.Info.Images[0].MipCount);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64), false, false, false);
        return new ShaderCompileRequest(plan, resources, layout);
    }

    [Fact]
    public void DynamicMipStorageImage_TakesOneConstantCasePerMip()
    {
        var request = DynamicMipRequest();
        var (resources, layout) = (request.Resources, request.Bindings);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.DoesNotContain((uint)SpirvCapability.StorageImageArrayDynamicIndexing, module.Capabilities);
        Assert.Contains((ushort)SpirvOp.IEqual, module.Opcodes);
        Assert.Equal(2, layout.Find(ImageDescriptorBinding.ForImage(resources.Info.Images[0])!.Value)!.Resources.Count);
    }

    [Fact]
    public void LayoutThatDoesNotMatchTheResources_IsRejectedBeforeEmission()
    {
        var program = Program(BufferLoad(0, 4), EndProgram(8));
        var (plan, resources, _) = Prepare(program);
        var wrong = BindingLayout.Allocate(resources.Info, [0], false, false, false);
        var request = new ShaderCompileRequest(plan, resources, wrong);

        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void LaneOperationOnTheGlobalDataShare_IsRejected()
    {
        var program = Program(
            DataShare(0, "DsSwizzleB32", gds: true, [Gen5Operand.Vector(0)], [1]),
            EndProgram(8));
        var request = Request(program);

        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("not valid on the global data share", error);
    }
}
