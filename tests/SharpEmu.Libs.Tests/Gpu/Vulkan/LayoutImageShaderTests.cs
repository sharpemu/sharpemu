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

    private static DescriptorImageInfo SampledView(CachedImage image, uint levelCount = 1) =>
        new()
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with { Format = image.Backing.Format, Usage = ImageUsageFlags.SampledBit, LevelCount = levelCount }),
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        };

    private static DescriptorImageInfo StorageView(CachedImage image, uint mip) =>
        new()
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with { Format = image.Backing.Format, Usage = ImageUsageFlags.StorageBit, BaseLevel = mip, LevelCount = 1 }),
            ImageLayout = ImageLayout.General,
        };

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
    public void IndirectImage_SelectsTheMappedCandidateOnTheDevice(bool loadMip, bool gpuDependentSelector, float expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        var (request, snapshot) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageRequest(ResultBytes, loadMip, gpuDependentSelector);
        var (registers, memory) = SpirvBindingDeclarationTests.IndirectImageAfterPlainImageInputs(ResultBytes, loadMip);
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
            infos[index] = [SampledView(images[index], levels)];
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
