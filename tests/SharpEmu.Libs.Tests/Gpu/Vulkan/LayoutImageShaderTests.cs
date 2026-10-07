// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Images and samplers bound through the class arrays of a compile request: element
// selection, the duplicated point sampler, and the per-mip storage descriptors.
public sealed class LayoutImageShaderTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(1u, true)]
    public void ExplicitLodComparisonGatherRejectsUnsupportedSamplerModes(uint mipFilter, bool unnormalized)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, FirstImageRegister, FirstImageAddress, Format32Float, lastLevel: 1));
        instructions.Add(MoveScalar(pc, FirstImageRegister + 5, 1u << 4)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister, (1u << 12) | (unnormalized ? 1u << 15 : 0))); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister + 1, 0xFFFu << 12)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister + 2, mipFilter << 26)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister + 3, 0)); pc += 8;
        var gather = Image(pc, "ImageGather4CL", FirstImageRegister, SamplerRegister);
        gather = gather with { Control = ((Gen5ImageControl)gather.Control!) with { AddressRegisters = [0, 1, 2, 3] } };
        instructions.Add(gather); pc += 8;
        instructions.Add(BufferAccess(pc, "BufferStoreDwordx4", ResultRegister, dwords: 4, vectorData: 4)); pc += 8;
        instructions.Add(EndProgram(pc));
        var plan = ShaderResourcePlan.Extract(Program([.. instructions]), ShaderStage.Compute, 1, 0, 64);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs(UserData()), ref snapshot, ref specialization));
    }

    [Fact]
    public unsafe void DebugMessengerReceivesSubmittedMessages()
    {
        var vulkan = fixture.Vulkan;
        if (vulkan is null || !vulkan.ValidationEnabled) return;
        _ = vulkan.TakeValidationMessages();
        const string messageText = "SharpEmu debug messenger callback test";
        var pointer = Silk.NET.Core.Native.SilkMarshal.StringToPtr(messageText);
        try
        {
            var data = new DebugUtilsMessengerCallbackDataEXT
            {
                SType = StructureType.DebugUtilsMessengerCallbackDataExt,
                PMessage = (byte*)pointer,
            };
            var submit = (delegate* unmanaged<Instance, DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT, DebugUtilsMessengerCallbackDataEXT*, void>)
                vulkan.Vk.GetInstanceProcAddr(vulkan.Instance, "vkSubmitDebugUtilsMessageEXT").Handle;
            Assert.True(submit != null);
            submit(vulkan.Instance, DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt, DebugUtilsMessageTypeFlagsEXT.GeneralBitExt, &data);
            Assert.Contains(vulkan.TakeValidationMessages(), message => message.Contains(messageText, StringComparison.Ordinal));
        }
        finally { Silk.NET.Core.Native.SilkMarshal.Free(pointer); }
    }
    [Theory]
    [InlineData(0u, 0.5f)]
    [InlineData(1u, 1f)]
    [InlineData(0u, 1f, true)]
    [InlineData(1u, 0f, true)]
    [InlineData(0u, 0.5f, true, true)]
    [InlineData(1u, 0f, true, true)]
    public void FiniteSamplersUseTheFilterSelectedAtTheDescriptorLoad(uint compare, float expected, bool mixedTypes = false, bool signed = false)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var prefix = DirectImageTableTests.FiniteBufferImageProgram(compare: compare).Instructions.Where(i => i.Pc < 48);
        var program = Program([.. prefix,
            ScalarBufferLoad(48, 0, 28, 4, immediateOffset: 256, dynamicOffsetRegister: 106),
            // A later write must not change the descriptor selected by the earlier load.
            MoveScalar(56, 106, 12345),
            MoveVector(64, 6, BitConverter.SingleToUInt32Bits(0.5f)),
            MoveVector(72, 7, BitConverter.SingleToUInt32Bits(0.5f)),
            Image(80, "ImageSampleLz", 16, 28, dmask: 1, vectorAddress: 6),
            BufferAccess(88, "BufferStoreDword", ResultRegister, vectorData: 4), EndProgram(96)]);
        var registers = UserData();
        registers[0] = 0x1000; registers[2] = 4096;
        var words = new uint[4096 / 4];
        for (var candidate = 0; candidate <= 5; candidate++)
        {
            var offset = candidate * 384 / 4;
            new uint[] { 0x2000, (mixedTypes && candidate != 5 ? signed ? Format32Sint : Format32Uint : Format32Float) << 20,
                3, 0xFAC | (9u << 28), 0, 0, 0, 0 }.CopyTo(words, offset);
            new uint[] { 0x92, 0xFFF000, signed || !mixedTypes && candidate == 5 ? 0x05500000u : 0x05000000u, 0 }.CopyTo(words, offset + 64);
        }
        var memory = new TestWordMemory { Base = 0x1000, Words = words, RequireAlignment = true };
        var plan = Extract(program, userDataCount: 12);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers, readMemory: memory.Read, readCleanMemory: memory.Read),
            ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var guest = runner.CreateBuffer(MemoryMarshal.AsBytes(words.AsSpan()));
        var result = runner.CreateBuffer(ResultBytes);
        var description = Describe(0, Format.R32Sfloat, GuestPixelFormat.Bits32Float, 4, 1, 1);
        var image = harness.CreateImage(description);
        float[] texels = [0, 0, 1, 1];
        harness.UploadImage(image, MemoryMarshal.AsBytes<float>(texels), ImageTestHarness.WholeImageCopies(description, 0));
        var uintDescription = Describe(0x10000, signed ? Format.R32Sint : Format.R32Uint,
            signed ? GuestPixelFormat.Bits32SInt : GuestPixelFormat.Bits32UInt, 4, 1, 1);
        var uintImage = harness.CreateImage(uintDescription);
        uint[] uintTexels = [0, 0, 17, 17];
        harness.UploadImage(uintImage, MemoryMarshal.AsBytes<uint>(uintTexels), ImageTestHarness.WholeImageCopies(uintDescription, 0));
        var bound = layout.Descriptors.Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) != ImageResourceClass.None)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(index =>
                SampledView(resources.Info.Images[(int)index].NumericClass != ImageNumericClass.Float ? uintImage : image)).ToArray());
        bound[DescriptorBindingKind.Samplers] = snapshot.Samplers.Select((sampler, index) => new DescriptorImageInfo
            { Sampler = runner.CreateSampler(!resources.Info.Samplers[index].ForcePointFiltering &&
                (sampler[2] & 0x00500000) != 0 ? Filter.Linear : Filter.Nearest) }).ToArray();
        var buffers = snapshot.Buffers.Select(buffer => buffer[0] == 0x1000 ? guest : result).ToArray();
        harness.Run(() =>
        {
            image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, new CommandBuffer(harness.Scheduler.Current.Handle));
            uintImage.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, new CommandBuffer(harness.Scheduler.Current.Handle));
            runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers },
                1, flattenedTable: snapshot.FlattenedResourceTable, boundImages: bound);
        });
        var bytes = runner.ReadBack(result, 0, ResultBytes);
        if (mixedTypes && compare == 1) Assert.Equal(17u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        else Assert.Equal(expected, BitConverter.ToSingle(bytes));
        harness.AssertNoValidationMessages();
        output.WriteLine($"Finite sampler filter readback on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkgroupSelectedImagesUseTheExecutedDescriptorOffset(bool split)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (snapshot, request, registers, tableBytes) = DirectImageTableTests.PrepareWorkgroupImages(split);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var table = runner.CreateBuffer(tableBytes);
        var result = runner.CreateBuffer(new byte[ResultBytes]);
        var images = new CachedImage[snapshot.Images.Length];
        var views = new DescriptorImageInfo[images.Length];
        for (var index = 0; index < images.Length; index++)
        {
            var description = Describe((ulong)index * 0x10000, Format.R32Sfloat, GuestPixelFormat.Bits32Float, 1, 1, 1);
            images[index] = harness.CreateImage(description);
            float[] texel = [snapshot.Images[index][0] == 0x2000 ? 0.25f : 0.75f];
            harness.UploadImage(images[index], MemoryMarshal.AsBytes<float>(texel), ImageTestHarness.WholeImageCopies(description, 0));
            views[index] = SampledView(images[index]);
        }
        var sampler = runner.CreateSampler(Filter.Nearest);
        var bound = request.Bindings.Descriptors
            .Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) != ImageResourceClass.None)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(index => views[index]).ToArray());
        bound[DescriptorBindingKind.Samplers] = Enumerable.Repeat(new DescriptorImageInfo { Sampler = sampler },
            request.Bindings.Find(DescriptorBindingKind.Samplers)!.Resources.Count).ToArray();
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            foreach (var image in images) image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [table, result] },
                4, flattenedTable: snapshot.FlattenedResourceTable, boundImages: bound);
        });
        var bytes = runner.ReadBack(result, 0, ResultBytes);
        Assert.Equal(new float[] { 0.25f, 0.25f, 0.75f, 0.75f },
            Enumerable.Range(0, 4).Select(index => BitConverter.ToSingle(bytes, index * 4)).ToArray());
        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified workgroup descriptor offsets on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(8f, 1f)]
    [InlineData(-2f, 0f)]
    [InlineData(1f, 0f, true)]
    [InlineData(0.49f, 0f)]
    [InlineData(0.51f, 1f)]
    [InlineData(1f, 0f, false, 0u)]
    public void ExplicitLodComparisonGatherSelectsTheRequestedMip(float lod, float expected, bool mixedQuad = false, uint mipFilter = 1)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, FirstImageRegister, FirstImageAddress, Format32Float, lastLevel: 1));
        // MAX_MIP must describe the same accessible mip range as LAST_LEVEL.
        instructions.Add(MoveScalar(pc, FirstImageRegister + 5, 1u << 4)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister, 1u << 12)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister + 1, 0xFFFu << 12)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister + 2, mipFilter << 26)); pc += 8;
        instructions.Add(MoveScalar(pc, SamplerRegister + 3, 0)); pc += 8;
        instructions.Add(MoveVector(pc, 0, BitConverter.SingleToUInt32Bits(0.5f))); pc += 8;
        instructions.Add(MoveVector(pc, 1, BitConverter.SingleToUInt32Bits(0.5f))); pc += 8;
        instructions.Add(MoveVector(pc, 2, BitConverter.SingleToUInt32Bits(0.5f))); pc += 8;
        instructions.Add(MoveVector(pc, 3, BitConverter.SingleToUInt32Bits(lod))); pc += 8;
        var gather = Image(pc, "ImageGather4CL", FirstImageRegister, SamplerRegister);
        gather = gather with { Control = ((Gen5ImageControl)gather.Control!) with { AddressRegisters = [0, 1, 2, 3] } };
        instructions.Add(gather); pc += 8;
        instructions.Add(BufferAccess(pc, "BufferStoreDwordx4", ResultRegister, dwords: 4, vectorData: 4)); pc += 8;
        instructions.Add(EndProgram(pc));
        using var run = new Run(vulkan, Program([.. instructions]), UserData(), 1);
        var resource = run.ResourceAt(FirstImageAddress);
        Assert.Equal(2u, run.Resources.Info.Images[resource].MipCount);
        var description = Describe(0, Format.D32Sfloat, GuestPixelFormat.Bits32Float, 4, 4, 2);
        var image = run.Harness.CreateImage(description);
        float[] texels = [.. Enumerable.Repeat(0.25f, 16), .. Enumerable.Repeat(0.75f, 4)];
        if (mixedQuad) new float[] { 0.25f, 0.75f, 0.5f, 1f }.CopyTo(texels, 16);
        run.Harness.UploadImage(image, MemoryMarshal.AsBytes<float>(texels), ImageTestHarness.WholeImageCopies(description, 0));
        var views = Enumerable.Range(0, 2).Select(mip => new DescriptorImageInfo
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with
            {
                Format = Format.D32Sfloat, Usage = ImageUsageFlags.SampledBit,
                BaseLevel = (uint)mip, LevelCount = 1,
            }),
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        }).ToArray();
        var sampler = run.Runner.CreateSampler(Filter.Nearest, CompareOp.Less);
        run.Dispatch(run.BindImages(new Dictionary<int, DescriptorImageInfo[]> { [resource] = views }, [sampler]),
            command => image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command));
        for (var index = 0; index < 4; index++)
            Assert.Equal(mixedQuad ? new float[] { 0f, 1f, 1f, 0f }[index] : expected,
                BitConverter.UInt32BitsToSingle(run.ResultWord(index)));
        run.Harness.AssertNoValidationMessages();
        output.WriteLine($"Explicit-LOD comparison gather readback on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    [Theory]
    [InlineData(0u, 25u)]
    [InlineData(1u, 50u)]
    public void JoinedTableBuffersReadTheDescriptorChosenByTheExecutedVccBranch(uint condition, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var instructions = ResourceBranchTests.JoinedTableBuffer().Instructions.Where(i => i.Opcode != "SEndpgm").ToList();
        instructions.Add(BufferAccess(40, "BufferStoreDword", ResultRegister, vectorData: 4));
        instructions.Add(EndProgram(48));
        var program = Program([.. instructions]);
        var registers = UserData();
        registers[0] = 0x3000;
        registers[2] = condition;
        registers[ResultRegister] = 0x4000;
        var memory = new TestWordMemory { Base = 0, Words = new uint[0x4000 / 4], RequireAlignment = true };
        memory.At(0x1000) = 25;
        memory.At(0x2000) = 50;
        uint[] descriptors = [0x1000, 0, 4, 0, 0x2000, 0, 4, 0];
        descriptors.CopyTo(memory.Words, 0x3000 / 4);
        var plan = Extract(program, userDataCount: 12);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers, readMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 12),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var guest = runner.CreateBuffer(MemoryMarshal.AsBytes(memory.Words.AsSpan()));
        var result = runner.CreateBuffer(ResultBytes);
        var pageTable = runner.CreatePageTable(1, [(0ul, guest, 0ul)]);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result], [DescriptorBindingKind.DeviceAddressPageTable] = [pageTable] },
            1, flattenedTable: snapshot.FlattenedResourceTable));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, ResultBytes)));
        harness.AssertNoValidationMessages();
        output.WriteLine($"VCC descriptor join readback on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    private const uint Format32Uint = 20;
    private const uint Format32Sint = 21;
    private const uint Format32Float = 22;
    private const uint ImageType2D = 9;
    private const uint ResultRegister = 8;
    private const uint ResultBytes = 64;
    private const uint FirstImageRegister = 16;
    private const uint SecondImageRegister = 24;
    private const uint SamplerRegister = 32;
    private const ulong FirstImageAddress = 0x1000;
    private const ulong SecondImageAddress = 0x2000;

    private static IEnumerable<Gen5ShaderInstruction> ImageWords(ref uint pc, uint register, ulong address, uint format, uint lastLevel = 0)
    {
        uint[] words = [(uint)address, format << 20, 3 | (3 << 14), 0xFAC | (lastLevel << 16) | (ImageType2D << 28), 0, 0, 0, 0];
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint dword = 0; dword < 8; dword++)
        {
            instructions.Add(MoveScalar(pc, register + dword, words[dword]));
            pc += 8;
        }

        return instructions;
    }

    private static IEnumerable<Gen5ShaderInstruction> SamplerWords(ref uint pc, uint register)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint dword = 0; dword < 4; dword++)
        {
            instructions.Add(MoveScalar(pc, register + dword, 0));
            pc += 8;
        }

        return instructions;
    }

    // Two images sampled at their centre through one sampler, each result stored to its own dword.
    private static Gen5ShaderProgram SampleTwoImagesProgram(uint secondFormat)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, FirstImageRegister, FirstImageAddress, Format32Float));
        instructions.AddRange(ImageWords(ref pc, SecondImageRegister, SecondImageAddress, secondFormat));
        instructions.AddRange(SamplerWords(ref pc, SamplerRegister));
        instructions.Add(MoveVector(pc, 0, 0x3F00_0000)); pc += 8;
        instructions.Add(MoveVector(pc, 1, 0x3F00_0000)); pc += 8;
        instructions.Add(Image(pc, "ImageSampleLz", FirstImageRegister, SamplerRegister, dmask: 1)); pc += 8;
        instructions.Add(BufferAccess(pc, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 4)); pc += 8;
        instructions.Add(Image(pc, "ImageSampleLz", SecondImageRegister, SamplerRegister, dmask: 1)); pc += 8;
        instructions.Add(BufferAccess(pc, "BufferStoreDword", ResultRegister, 4, 1, vectorData: 4)); pc += 8;
        instructions.Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    // Each lane writes its index into texel (0, 0) of the mip its index selects.
    private static Gen5ShaderProgram StoreLaneIntoMipProgram()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, FirstImageRegister, FirstImageAddress, Format32Uint, lastLevel: 1));
        instructions.Add(MoveVector(pc, 1, 0)); pc += 8;
        instructions.Add(MoveVector(pc, 2, 0)); pc += 8;
        instructions.Add(Vop1(pc, "VMovB32", 3, Gen5Operand.Vector(0))); pc += 8;
        instructions.Add(Vop1(pc, "VMovB32", 4, Gen5Operand.Vector(0))); pc += 8;
        instructions.Add(Image(pc, "ImageStoreMip", FirstImageRegister, vectorAddress: 1, dmask: 1)); pc += 8;
        instructions.Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    private static ImageDescription Describe(ulong guestOffset, Format format, GuestPixelFormat guestFormat, uint width, uint height, uint levels)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ArrayBackedSpace.Base + guestOffset, (ulong)width * height * 4 * 2);
        description.PixelFormat = format;
        description.GuestFormat = guestFormat;
        description.Extent = new Extent3D(width, height, 1);
        description.Resources = new SubresourceCount(levels, 1);
        description.Pitch = width;
        description.BytesPerBlock = 4;
        return description;
    }

    private sealed class Run : IDisposable
    {
        public Run(HeadlessVulkan vulkan, Gen5ShaderProgram program, uint[] registers, uint threadCount)
        {
            Plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0x5348_4152_5045_4D55, 0, 64);
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(Plan, Inputs(registers), ref snapshot, ref specialization));
            Snapshot = snapshot;
            Resources = ResourceMaterializer.ApplyTo(Plan, specialization);
            var layout = BindingLayout.Allocate(
                Resources.Info,
                BindingLayout.CollectUserDataRegisters(program, 0, 64),
                false,
                ShaderCompileRequest.RequiresFlattenedTable(Plan, Resources),
                false);
            Request = new ShaderCompileRequest(Plan, Resources, layout) { LocalSizeX = threadCount, ThreadCountX = threadCount };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request, out var shader, out var error), error);
            Harness = new ImageTestHarness(vulkan);
            Runner = new LayoutComputeRunner(Harness, Request, shader.Spirv);
            Result = Runner.CreateBuffer(ResultBytes);
            Registers = registers;
        }

        public ShaderResourcePlan Plan { get; }
        public ResourceSnapshot Snapshot { get; }
        public SpecializedResourceInfo Resources { get; }
        public ShaderCompileRequest Request { get; }
        public ImageTestHarness Harness { get; }
        public LayoutComputeRunner Runner { get; }
        public GpuBuffer Result { get; }
        public uint[] Registers { get; }

        // The image resource whose materialised descriptor starts at a guest address.
        public int ResourceAt(ulong address)
        {
            for (var index = 0; index < Snapshot.Images.Length; index++)
            {
                if (Snapshot.Images[index][0] == (uint)address)
                {
                    return index;
                }
            }

            throw new InvalidOperationException($"no image descriptor at 0x{address:X}");
        }

        // The class-array entries in layout order, each resource mapped to its view info.
        public Dictionary<DescriptorBindingKind, DescriptorImageInfo[]> BindImages(IReadOnlyDictionary<int, DescriptorImageInfo[]> viewsByResource, Sampler[]? samplers = null)
        {
            var bound = new Dictionary<DescriptorBindingKind, DescriptorImageInfo[]>();
            foreach (var descriptor in Request.Bindings.Descriptors)
            {
                if (descriptor.Kind == DescriptorBindingKind.Samplers)
                {
                    bound[descriptor.Kind] = descriptor.Resources.Select(index => new DescriptorImageInfo { Sampler = samplers![index] }).ToArray();
                    continue;
                }

                if (ImageDescriptorBinding.ResourceClass(descriptor.Kind) == ImageResourceClass.None)
                {
                    continue;
                }

                var infos = new List<DescriptorImageInfo>();
                var mipByResource = new Dictionary<uint, int>();
                foreach (var resource in descriptor.Resources)
                {
                    mipByResource.TryGetValue(resource, out var mip);
                    infos.Add(viewsByResource[(int)resource][mip]);
                    mipByResource[resource] = mip + 1;
                }

                bound[descriptor.Kind] = infos.ToArray();
            }

            return bound;
        }

        public void Dispatch(Dictionary<DescriptorBindingKind, DescriptorImageInfo[]> images, Action<CommandBuffer> prepare) =>
            Harness.Run(() =>
            {
                prepare(new CommandBuffer(Harness.Scheduler.Current.Handle));
                Runner.Dispatch(Registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [Result] }, 1, boundImages: images);
            });

        public uint ResultWord(int index) => BinaryPrimitives.ReadUInt32LittleEndian(Runner.ReadBack(Result, 0, ResultBytes).AsSpan(index * 4));

        public void Dispose()
        {
            Runner.Dispose();
            Harness.Dispose();
        }
    }

    private static uint[] UserData()
    {
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        return registers;
    }

    private static DescriptorImageInfo SampledView(CachedImage image, uint levelCount = 1, bool arrayed = false) =>
        new()
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with { Format = image.Backing.Format, Usage = ImageUsageFlags.SampledBit, LevelCount = levelCount, Type = arrayed ? ImageViewType.Type2DArray : ImageViewType.Type2D }),
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

    private static DescriptorImageInfo StorageView(CachedImage image, uint mip) =>
        new()
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with { Format = image.Backing.Format, Usage = ImageUsageFlags.StorageBit, BaseLevel = mip, LevelCount = 1 }),
            ImageLayout = ImageLayout.General,
        };

    [Theory]
    [InlineData(0u, 25u)]
    [InlineData(1u, 20u)]
    [InlineData(0u, 25u, true)]
    [InlineData(1u, 20u, true)]
    [InlineData(0u, 22u, false, true)]
    [InlineData(1u, 20u, false, true)]
    [InlineData(0u, 25u, true, false, true)]
    [InlineData(1u, 20u, true, false, true)]
    [InlineData(0u, 25u, false, false, false, true)]
    [InlineData(1u, 20u, false, false, false, true)]
    [InlineData(0u, 25u, true, false, false, false, true)]
    [InlineData(1u, 20u, true, false, false, false, true)]
    [InlineData(0u, 25u, true, false, true, false, false, true)]
    [InlineData(1u, 20u, true, false, true, false, false, true)]
    [InlineData(0u, 25u, false, false, false, false, false, false, true)]
    [InlineData(1u, 20u, false, false, false, false, false, false, true)]
    [InlineData(0u, 25u, false, false, false, false, false, false, false, true)]
    [InlineData(1u, 20u, false, false, false, false, false, false, false, true)]
    public void FiniteScalarBufferImageSelectorUsesTheRuntimeOffset(uint compare, uint expected, bool laneRead = false, bool partialSelector = false, bool capturedMask = false, bool splitMultiply = false, bool afterWaterfall = false, bool negated = false, bool packedWord = false, bool wideSecondHalf = false)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (snapshot, request, registers) = DirectImageTableTests.PrepareFiniteBufferImages(compare, laneRead, partialSelector, capturedMask, splitMultiply, afterWaterfall, negated, packedWord, wideSecondHalf);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(new byte[ResultBytes]);
        var guest = runner.CreateBuffer(new byte[2048]);
        var buffers = snapshot.Buffers.Select(buffer => buffer[0] == 0x1000 ? guest : result).ToArray();
        var images = new CachedImage[snapshot.Images.Length];
        var views = new DescriptorImageInfo[images.Length];
        for (var index = 0; index < images.Length; index++)
        {
            var description = Describe((ulong)index * 0x10000, Format.R32Uint, GuestPixelFormat.Bits32UInt, 1, 1, 1);
            images[index] = harness.CreateImage(description);
            uint[] texel = [20 + (snapshot.Images[index][0] - 0x2000) / 0x100];
            harness.UploadImage(images[index], MemoryMarshal.AsBytes<uint>(texel), ImageTestHarness.WholeImageCopies(description, 0));
            views[index] = SampledView(images[index]);
        }
        var bound = request.Bindings.Descriptors
            .Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) != ImageResourceClass.None)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(index => views[index]).ToArray());
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            foreach (var image in images) image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers },
                1, flattenedTable: snapshot.FlattenedResourceTable, boundImages: bound);
        });
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, ResultBytes)));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(0u, 0x5000u)]
    [InlineData(1u, 0x6000u)]
    public void PackedPointerDescriptorsPreserveRuntimeMemoryLoads(uint selected, uint expectedPointer)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            Sop1(0, "SMovB64", 40, Gen5Operand.Scalar(126)),
            Vop1(4, "VMovB32", 5, Operand(0)),
            Vop2(8, "VAddU32", 7, Gen5Operand.Scalar(12), Gen5Operand.Vector(0)),
            BufferAccess(12, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true, vectorAddress: 7),
            Sop1(20, "SMovB64", 126, Gen5Operand.Scalar(40)),
            new(24, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u],
                [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(32, 16, 6),
            Sop2(36, "SMulI32", 17, Operand(16), Gen5Operand.Scalar(16)),
            ScalarBufferLoad(40, 4, 20, 2, dynamicOffsetRegister: 17),
            ScalarLoad(48, 20, 24, 8),
            Vop1(56, "VMovB32", 0, Operand(0)), Vop1(60, "VMovB32", 1, Operand(0)),
            Image(64, "ImageLoad", 24, dmask: 1, vectorAddress: 0),
            BufferAccess(72, "BufferStoreDword", 8, vectorData: 4),
            Vop1(80, "VMovB32", 8, Gen5Operand.Scalar(20)),
            BufferAccess(84, "BufferStoreDword", 8, offset: 4, vectorData: 8), EndProgram(92));
        uint[] registers = [0x1000, 8u << 16, 2, 1u << 12,
            0x3000, 16u << 16, 2, 1u << 12,
            0x8000, 0, ResultBytes, 1u << 12, selected];
        var memory = new TestWordMemory { Base = 0, Words = new uint[0x10000 / 4], RequireAlignment = true };
        memory.At(0x1004) = 0;
        memory.At(0x100C) = 1u << 16;
        memory.At(0x3000) = 0x5000;
        memory.At(0x3010) = 0x6000;
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        ResourceTrackerTests.WriteImage(memory, 0x5000, descriptor);
        ResourceTrackerTests.WriteImage(memory, 0x6000, descriptor);
        var plan = Extract(program, userDataCount: 13);
        IndirectSelectorValuesTests.AssertPackedPointerEvaluates(plan, Inputs(registers, readCleanMemory: memory.Read));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 13),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultBytes);
        var guest = runner.CreateBuffer(MemoryMarshal.AsBytes(memory.Words.AsSpan()));
        var pageTable = runner.CreatePageTable(4, Enumerable.Range(0, 4)
            .Select(index => ((ulong)index << 14, guest, (ulong)index << 14)));
        var buffers = snapshot.Buffers.Select(words => words[0] == 0x8000 ? result :
            runner.CreateBuffer(MemoryMarshal.AsBytes(memory.Words.AsSpan((int)words[0] / 4, (int)(words[2] * Math.Max((words[1] >> 16) & 0x3FFF, 1u)) / 4)))).ToArray();
        var description = Describe(0, Format.R32Uint, GuestPixelFormat.Bits32UInt, 1, 1, 1);
        var image = harness.CreateImage(description);
        harness.UploadImage(image, MemoryMarshal.AsBytes<uint>([51u]), ImageTestHarness.WholeImageCopies(description, 0));
        var boundImages = request.Bindings.Descriptors.Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) != ImageResourceClass.None)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(_ => SampledView(image)).ToArray());
        harness.Run(() =>
        {
            image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, new CommandBuffer(harness.Scheduler.Current.Handle));
            runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> {
                [DescriptorBindingKind.Buffers] = buffers, [DescriptorBindingKind.DeviceAddressPageTable] = [pageTable] },
                1, flattenedTable: snapshot.FlattenedResourceTable, boundImages: boundImages);
        });
        var actual = runner.ReadBack(result, 0, ResultBytes);
        Assert.Equal(51u, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        Assert.Equal(expectedPointer, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(0u, 0u, 6u, 0xFFFFFF38u, 0x80u, 1)]
    [InlineData(1u, 2u, 6u, 0xFFFFFF38u, 0x80u, 1)]
    [InlineData(1u, 4u, 6u, 0xFFFFFF38u, 0x80u, 1)]
    [InlineData(1u, 1u, 1u, 0x3F000000u, 0x80u, 1)]
    [InlineData(0u, 2u, 5u, 300u, 0xFFu, 1)]
    [InlineData(1u, 0u, 13u, 0x3F800000u, 0x3C00u, 2)]
    [InlineData(1u, 1u, 22u, 0xBF800000u, 0xBF800000u, 4)]
    public void PackedFormattedStoresUseRuntimeAddressesAndPreserveNeighborBytes(
        uint selected, uint index, uint format, uint input, uint expectedValue, int expectedBytes)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            Sop1(0, "SMovB64", 40, Gen5Operand.Scalar(126)), Vop1(4, "VMovB32", 5, Operand(0)),
            Vop2(8, "VAddU32", 7, Gen5Operand.Scalar(8), Gen5Operand.Vector(0)),
            BufferAccess(12, "BufferLoadDwordx2", 0, dwords: 2, vectorData: 4, indexEnabled: true, vectorAddress: 7),
            Sop1(20, "SMovB64", 126, Gen5Operand.Scalar(40)),
            new(24, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u], [Gen5Operand.Vector(5)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(6, 0, 5, 6, false, false, 0, 0, 0, false, null)),
            ReadFirstLane(32, 16, 6), Sop2(36, "SMulI32", 17, Operand(16), Gen5Operand.Scalar(16)),
            ScalarBufferLoad(40, 4, 20, 4, dynamicOffsetRegister: 17),
            Vop1(48, "VMovB32", 4, Operand(input)), Vop1(52, "VMovB32", 8, Operand(index)),
            BufferAccess(56, "BufferStoreFormatX", 20, vectorData: 4, indexEnabled: true, vectorAddress: 8), EndProgram(64));
        uint[] registers = [0x1000, 8u << 16, 2, 1u << 12, 0x3000, 16u << 16, 2, 1u << 12, selected];
        var memory = new TestWordMemory { Base = 0, Words = new uint[0x10000 / 4], RequireAlignment = true };
        memory.At(0x100C) = 1u << 16;
        for (uint candidate = 0; candidate < 2; candidate++)
        {
            var address = 0x3000ul + candidate * 16;
            memory.At(address) = 0x8001u + candidate * 0x1002;
            memory.At(address + 4) = 4u << 16;
            memory.At(address + 8) = 4;
            memory.At(address + 12) = (format << 12) | 4;
        }
        for (ulong address = 0x8000; address < 0xA000; address += 4) memory.At(address) = 0xCDCDCDCD;
        var expected = MemoryMarshal.AsBytes(memory.Words.AsSpan()).ToArray();
        if (index < 4)
            for (var part = 0; part < expectedBytes; part++)
                expected[0x8001 + selected * 0x1002 + index * 4 + part] = (byte)(expectedValue >> (part * 8));
        var plan = Extract(program, userDataCount: 9);
        Assert.Single(plan.Info.DeviceStoreValidationSources);
        var snapshot = new ResourceSnapshot(); var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 9),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var guest = runner.CreateBuffer(MemoryMarshal.AsBytes(memory.Words.AsSpan()));
        var pageTable = runner.CreatePageTable(4, Enumerable.Range(0, 4).Select(page => ((ulong)page << 14, guest, (ulong)page << 14)));
        var buffers = snapshot.Buffers.Select(words => runner.CreateBuffer(MemoryMarshal.AsBytes(memory.Words.AsSpan(
            (int)words[0] / 4, (int)(words[2] * Math.Max((words[1] >> 16) & 0x3FFF, 1u)) / 4)))).ToArray();
        harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> {
            [DescriptorBindingKind.Buffers] = buffers, [DescriptorBindingKind.DeviceAddressPageTable] = [pageTable] },
            1, flattenedTable: snapshot.FlattenedResourceTable));
        Assert.Equal(expected, runner.ReadBack(guest, 0, (ulong)expected.Length));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(-32768)]
    [InlineData(-1)]
    [InlineData(32767)]
    public void Signed16StorageImage_PreservesSignedLoadsAndStores(short input)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, FirstImageRegister, FirstImageAddress, 12));
        instructions.Add(MoveVector(pc, 1, 0)); pc += 8;
        instructions.Add(MoveVector(pc, 2, 0)); pc += 8;
        instructions.Add(Image(pc, "ImageLoad", FirstImageRegister, vectorAddress: 1, dmask: 1)); pc += 8;
        instructions.Add(BufferAccess(pc, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 4)); pc += 8;
        instructions.Add(MoveVector(pc, 4, unchecked((uint)-12345))); pc += 8;
        instructions.Add(Image(pc, "ImageStore", FirstImageRegister, vectorAddress: 1, dmask: 1)); pc += 8;
        instructions.Add(EndProgram(pc));
        using var run = new Run(vulkan, Program([.. instructions]), UserData(), 1);
        Assert.All(run.Resources.Info.Images, resource => Assert.Equal(ImageNumericClass.Sint, resource.NumericClass));
        var image = run.Harness.CreateImage(Describe(0, Format.R16Sint, GuestPixelFormat.Bits16SInt, 4, 4, 1));
        var pixels = Enumerable.Repeat(input, 16).ToArray();
        var copies = ImageTestHarness.WholeImageCopies(image.Description, 0);
        run.Harness.UploadImage(image, MemoryMarshal.AsBytes(pixels.AsSpan()), copies);
        var view = new DescriptorImageInfo
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with { Format = Format.R16Sint, Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit, LevelCount = 1 }),
            ImageLayout = ImageLayout.General,
        };
        var views = Enumerable.Range(0, run.Snapshot.Images.Length).ToDictionary(index => index, _ => new[] { view });
        run.Dispatch(run.BindImages(views), command => image.Transition(ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, null, command));
        Assert.Equal(unchecked((uint)(int)input), run.ResultWord(0));
        var actual = MemoryMarshal.Cast<byte, short>(run.Harness.ReadImage(image, copies, 32)).ToArray();
        pixels[0] = -12345;
        Assert.Equal(pixels, actual);
        run.Harness.AssertNoValidationMessages();
    }

    [Fact]
    public void SampledClassArray_SamplesEachElementThroughTheLayout()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var run = new Run(vulkan, SampleTwoImagesProgram(Format32Float), UserData(), 1);
        var floatClass = ImageDescriptorBinding.ForImage(run.Resources.Info.Images[0])!.Value;
        Assert.Equal(2, run.Request.Bindings.Find(floatClass)!.Resources.Count);
        Assert.Single(run.Resources.Info.Samplers);

        var first = run.Harness.CreateImage(Describe(0, Format.R32Sfloat, GuestPixelFormat.Bits32Float, 1, 1, 1));
        var second = run.Harness.CreateImage(Describe(0x10000, Format.R32Sfloat, GuestPixelFormat.Bits32Float, 1, 1, 1));
        run.Harness.UploadImage(first, MemoryMarshal.AsBytes<float>([0.25f]), ImageTestHarness.WholeImageCopies(first.Description, 0));
        run.Harness.UploadImage(second, MemoryMarshal.AsBytes<float>([0.75f]), ImageTestHarness.WholeImageCopies(second.Description, 0));
        var sampler = run.Runner.CreateSampler(Filter.Linear);
        var views = new Dictionary<int, DescriptorImageInfo[]>
        {
            [run.ResourceAt(FirstImageAddress)] = [SampledView(first)],
            [run.ResourceAt(SecondImageAddress)] = [SampledView(second)],
        };
        run.Dispatch(run.BindImages(views, [sampler]), command =>
        {
            first.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            second.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
        });

        Assert.Equal(BitConverter.SingleToUInt32Bits(0.25f), run.ResultWord(0));
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.75f), run.ResultWord(1));
        run.Harness.AssertNoValidationMessages();
        output.WriteLine($"Verified sampled class arrays on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    [Fact]
    public void PointOnlyImage_SamplesThroughTheDuplicatedSampler()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var run = new Run(vulkan, SampleTwoImagesProgram(Format32Sint), UserData(), 1);
        var samplers = run.Resources.Info.Samplers;
        Assert.Equal(2, samplers.Count);
        Assert.Equal(1, samplers.Count(sampler => sampler.ForcePointFiltering));

        // Between the two texels a linear sampler averages; the integer image needs its point duplicate.
        var floatImage = run.Harness.CreateImage(Describe(0, Format.R32Sfloat, GuestPixelFormat.Bits32Float, 2, 1, 1));
        var integerImage = run.Harness.CreateImage(Describe(0x10000, Format.R32Sint, GuestPixelFormat.Bits32SInt, 2, 1, 1));
        run.Harness.UploadImage(floatImage, MemoryMarshal.AsBytes<float>([1.0f, 3.0f]), ImageTestHarness.WholeImageCopies(floatImage.Description, 0));
        run.Harness.UploadImage(integerImage, MemoryMarshal.AsBytes<int>([5, 9]), ImageTestHarness.WholeImageCopies(integerImage.Description, 0));
        var hostSamplers = samplers.Select(sampler => run.Runner.CreateSampler(sampler.ForcePointFiltering ? Filter.Nearest : Filter.Linear)).ToArray();
        var views = new Dictionary<int, DescriptorImageInfo[]>
        {
            [run.ResourceAt(FirstImageAddress)] = [SampledView(floatImage)],
            [run.ResourceAt(SecondImageAddress)] = [SampledView(integerImage)],
        };
        run.Dispatch(run.BindImages(views, hostSamplers), command =>
        {
            floatImage.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            integerImage.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
        });

        Assert.Equal(BitConverter.SingleToUInt32Bits(2.0f), run.ResultWord(0));
        Assert.Equal(9u, run.ResultWord(1));
        run.Harness.AssertNoValidationMessages();
        output.WriteLine($"Verified the duplicated point sampler on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    // Image 0 is plain, image 1 the indirect root, image 2 its candidate for key 1; a mip load
    // reads texel (0, 0) of mip 1 of two-level images.
    [Theory]
    [InlineData(false, false, 2.0f)]
    [InlineData(true, false, 2.5f)]
    [InlineData(false, true, 2.0f)]
    [InlineData(true, true, 2.5f)]
    [InlineData(false, false, 1.0f, 9u, 13u, 0u)]
    [InlineData(false, false, 2.0f, 9u, 13u, 1u)]
    [InlineData(false, false, 1.0f, 13u, 9u, 0u)]
    [InlineData(false, false, 2.0f, 13u, 9u, 1u)]
    public void IndirectImage_SelectsTheMappedCandidateOnTheDevice(bool loadMip, bool gpuDependentSelector, float expected,
        uint firstImageType = 9, uint secondImageType = 9, uint selectedRecord = 1)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        var (request, snapshot) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageRequest(ResultBytes, loadMip, gpuDependentSelector,
            firstImageType, secondImageType, firstImageType == 13 || secondImageType == 13 ? 5u : 1u);
        var (registers, memory) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageInputs(ResultBytes, loadMip, firstImageType, secondImageType);
        registers[8] = selectedRecord;
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultBytes);
        var material = runner.CreateBuffer(MemoryMarshal.AsBytes(memory.Words.AsSpan(0, 448 / 4)));
        var buffers = snapshot.Buffers.Select(descriptor => descriptor[0] == 0x1000 ? material : result).ToArray();

        // Two-level images hold their base value in mip 0 and that value plus one half in mip 1.
        var levels = loadMip ? 2u : 1u;
        var images = new CachedImage[3];
        var infos = new Dictionary<int, DescriptorImageInfo[]>();
        float[] texels = [0.5f, 1.0f, 2.0f];
        for (var index = 0; index < 3; index++)
        {
            var description = Describe((ulong)index * 0x10000, Format.R32Sfloat, GuestPixelFormat.Bits32Float, levels, levels, levels);
            images[index] = harness.CreateImage(description);
            var data = Enumerable.Repeat(texels[index], (int)(levels * levels)).Concat(levels > 1 ? [texels[index] + 0.5f] : Array.Empty<float>()).ToArray();
            harness.UploadImage(images[index], MemoryMarshal.AsBytes<float>(data), ImageTestHarness.WholeImageCopies(description, 0));
            infos[index] = [SampledView(images[index], levels, request.Resources.Info.Images[index].Dimension == ImageDimension.Dim2DArray)];
        }

        Assert.Equal(0x20u, snapshot.Images[1][0]);
        Assert.Equal(0x21u, snapshot.Images[2][0]);
        var samplers = request.Resources.Info.Samplers.Select(_ => runner.CreateSampler(Filter.Nearest)).ToArray();
        var bound = new Dictionary<DescriptorBindingKind, DescriptorImageInfo[]>();
        foreach (var descriptor in request.Bindings.Descriptors)
        {
            if (descriptor.Kind == DescriptorBindingKind.Samplers)
            {
                bound[descriptor.Kind] = descriptor.Resources.Select(index => new DescriptorImageInfo { Sampler = samplers[index] }).ToArray();
            }
            else if (ImageDescriptorBinding.ResourceClass(descriptor.Kind) != ImageResourceClass.None)
            {
                bound[descriptor.Kind] = descriptor.Resources.Select(index => infos[(int)index][0]).ToArray();
            }
        }

        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            foreach (var image in images)
            {
                image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            }

            runner.Dispatch(
                registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers },
                1,
                flattenedTable: snapshot.FlattenedResourceTable,
                boundImages: bound);
        });

        var words = runner.ReadBack(result, 0, ResultBytes);
        Assert.Equal(BitConverter.SingleToUInt32Bits(0.5f), BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(0)));
        Assert.Equal(BitConverter.SingleToUInt32Bits(expected), BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(4)));
        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified indirect image selection (loadMip={loadMip}) on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }

    [Theory]
    [InlineData(0u, 22u, false)]
    [InlineData(1u, 11u, false)]
    [InlineData(2u, 22u, false)]
    [InlineData(0x80000000u, 22u, false)]
    [InlineData(0u, 22u, true)]
    [InlineData(1u, 11u, true)]
    [InlineData(2u, 22u, true)]
    [InlineData(0x80000000u, 22u, true)]
    [InlineData(0u, 0u, false, true)]
    [InlineData(1u, 11u, false, true)]
    [InlineData(2u, 22u, false, true)]
    [InlineData(0x80000000u, 22u, false, true)]
    [InlineData(0u, 0u, true, true)]
    [InlineData(1u, 11u, true, true)]
    [InlineData(2u, 22u, true, true)]
    [InlineData(0x80000000u, 22u, true, true)]
    public void DirectImageTableUsesCapturedOffset(uint mask, uint expected, bool split, bool guarded = false)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (_, snapshot, request) = DirectImageTableTests.PrepareDirect(mask, split, guarded);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(new byte[ResultBytes]);
        var images = new CachedImage[snapshot.Images.Length];
        var views = new DescriptorImageInfo[images.Length];
        for (var index = 0; index < images.Length; index++)
        {
            var description = Describe((ulong)index * 0x10000, Format.R32Uint, GuestPixelFormat.Bits32UInt, 1, 1, 1);
            images[index] = harness.CreateImage(description);
            uint[] texel = [snapshot.Images[index][0] == 0x1000 ? 11u : 22u];
            harness.UploadImage(images[index], MemoryMarshal.AsBytes<uint>(texel), ImageTestHarness.WholeImageCopies(description, 0));
            views[index] = SampledView(images[index]);
        }
        var bound = request.Bindings.Descriptors
            .Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) != ImageResourceClass.None)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(index => views[index]).ToArray());
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            foreach (var image in images)
                image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            runner.Dispatch([0x1000, 0], new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] },
                1, flattenedTable: snapshot.FlattenedResourceTable, boundImages: bound);
        });
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, sizeof(uint))));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(1u, true, 37u)]
    [InlineData(0x80000000u, true, 91u)]
    [InlineData(1u, false, 91u)]
    [InlineData(0x80000000u, false, 37u)]
    public void MixedImageDimensionsSelectTheirOwnBinding(uint mask, bool arrayFirst, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (snapshot, request) = DirectImageTableTests.PrepareMixedDimensions(mask, arrayFirst);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(new byte[ResultBytes]);
        var images = new CachedImage[snapshot.Images.Length];
        var views = new DescriptorImageInfo[images.Length];
        for (var index = 0; index < images.Length; index++)
        {
            var array = request.Resources.Info.Images[index].Dimension == ImageDimension.Dim2DArray;
            var description = Describe((ulong)index * 0x10000, Format.R32Uint, GuestPixelFormat.Bits32UInt, 1, 1, 1);
            description.Resources = new SubresourceCount(1, array ? 2u : 1u);
            images[index] = harness.CreateImage(description);
            uint[] texels = array ? [13u, 37u] : [91u];
            harness.UploadImage(images[index], MemoryMarshal.AsBytes<uint>(texels), ImageTestHarness.WholeImageCopies(description, 0));
            views[index] = new DescriptorImageInfo
            {
                ImageView = images[index].GetOrCreateView(ImageViewDescription.Default with
                {
                    Format = Format.R32Uint,
                    Type = array ? ImageViewType.Type2DArray : ImageViewType.Type2D,
                    LayerCount = description.Resources.Layers,
                }),
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
        }
        var bound = request.Bindings.Descriptors
            .Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) != ImageResourceClass.None)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(index => views[index]).ToArray());
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            foreach (var image in images)
                image.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            runner.Dispatch([0x1000, 0], new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] },
                1, flattenedTable: snapshot.FlattenedResourceTable, boundImages: bound);
        });
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, sizeof(uint))));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void DynamicMipStorageImage_WritesEachMipThroughItsOwnDescriptor()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var run = new Run(vulkan, StoreLaneIntoMipProgram(), UserData(), 2);
        var image = run.Resources.Info.Images[0];
        Assert.Equal(ImageMipMode.DynamicStorage, image.MipMode);
        Assert.Equal(2u, image.MipCount);
        var storageClass = ImageDescriptorBinding.ForImage(image)!.Value;
        Assert.Equal(2, run.Request.Bindings.Find(storageClass)!.Resources.Count);

        var description = Describe(0, Format.R32Uint, GuestPixelFormat.Bits32UInt, 2, 2, 2);
        var target = run.Harness.CreateImage(description);
        var copies = ImageTestHarness.WholeImageCopies(description, 0);
        var texelCount = (int)(ImageTestHarness.WholeImageBytes(description) / 4);
        var sentinel = Enumerable.Repeat(0xFFFF_FFFFu, texelCount).ToArray();
        run.Harness.UploadImage(target, MemoryMarshal.AsBytes<uint>(sentinel), copies);
        var views = new Dictionary<int, DescriptorImageInfo[]> { [0] = [StorageView(target, 0), StorageView(target, 1)] };
        run.Dispatch(run.BindImages(views), command => target.Transition(ImageLayout.General, AccessFlags.ShaderWriteBit, null, command));

        var texels = MemoryMarshal.Cast<byte, uint>(run.Harness.ReadImage(target, copies, (ulong)texelCount * 4)).ToArray();
        Assert.Equal(0u, texels[0]);
        Assert.Equal(sentinel[1], texels[1]);
        Assert.Equal(sentinel[2], texels[2]);
        Assert.Equal(sentinel[3], texels[3]);
        Assert.Equal(1u, texels[4]);
        run.Harness.AssertNoValidationMessages();
        output.WriteLine($"Verified per-mip storage descriptors on {vulkan.DeviceName}; validation={vulkan.ValidationEnabled}.");
    }
}
