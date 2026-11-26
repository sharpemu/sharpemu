// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A store through a storage image whose destination select is shader data writes, bit for bit, the texel a module with
// the select baked in writes: every permutation of XYZW and a sample of other selects, two dmasks, and common storage
// formats (8-bit unorm, 16-bit float with 32-bit and D16 data, 32-bit uint and float). One module serves every select.
public sealed class RuntimeImageSwizzleDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ImageType2D = 9;
    private const uint ResourceRegister = 8;
    // X001, XYZ1, XXXX, 0000, 1111, ZYX0, XYXY.
    private static readonly uint[] OtherSelects = [0x204, 0x3AC, 0x924, 0x000, 0x249, 0x12E, 0xB2C];

    public static TheoryData<uint, Format, GuestPixelFormat, uint, bool> Formats() => new()
    {
        { 56, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, 4, false },
        { 71, Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, 8, false },
        { 71, Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, 8, true },
        { 75, Format.R32G32B32A32Uint, GuestPixelFormat.Bits32_32_32_32UInt, 16, false },
        { 77, Format.R32G32B32A32Sfloat, GuestPixelFormat.Bits32_32_32_32Float, 16, false },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void RuntimeSelectStoresWriteTheBakedTexelBitForBit(uint guestFormat, Format format, GuestPixelFormat pixelFormat,
        uint texelBytes, bool d16)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var harness = new ImageTestHarness(vulkan!);
        var selects = Permutations().Concat(OtherSelects).ToArray();
        var stores = 0;
        foreach (var dmask in new[] { 0xFu, 0x5u })
        {
            byte[]? shared = null;
            foreach (var select in selects)
            {
                var baked = Store(harness, guestFormat, format, pixelFormat, texelBytes, d16, dmask, select, runtime: false);
                var runtime = Store(harness, guestFormat, format, pixelFormat, texelBytes, d16, dmask, select, runtime: true);
                Assert.Equal(baked.Texel, runtime.Texel);
                Assert.NotEqual(baked.Spirv, runtime.Spirv);
                shared ??= runtime.Spirv;
                Assert.Equal(shared, runtime.Spirv);
                stores++;
                if (select == 0xF2E && dmask == 0xF)
                {
                    // ZYXW: the X channel holds the third component.
                    Assert.Equal(Texel(guestFormat, d16, dmask, select)[..(int)(texelBytes / 4)], baked.Texel[..(int)(texelBytes / 4)]);
                }
            }
        }

        harness.AssertNoValidationMessages();
        output.WriteLine($"Compared {stores} baked and runtime-select stores of format {guestFormat} (d16={d16}) on {vulkan!.DeviceName}.");
    }

    // The 24 orderings of X, Y, Z and W.
    private static IEnumerable<uint> Permutations()
    {
        foreach (var x in new uint[] { 4, 5, 6, 7 })
        foreach (var y in new uint[] { 4, 5, 6, 7 })
        foreach (var z in new uint[] { 4, 5, 6, 7 })
        foreach (var w in new uint[] { 4, 5, 6, 7 })
        {
            if (new[] { x, y, z, w }.Distinct().Count() == 4)
            {
                yield return x | (y << 3) | (z << 6) | (w << 9);
            }
        }
    }

    // The data registers' values: distinct per component, exact in the format.
    private static uint[] Data(uint guestFormat, bool d16)
    {
        if (d16)
        {
            return [Half(1.5f) | (Half(2.5f) << 16), Half(3.5f) | (Half(4.5f) << 16)];
        }

        return guestFormat switch
        {
            56 => [Bits(0.2f), Bits(0.4f), Bits(0.6f), Bits(0.8f)],
            75 => [0x1111_1111, 0x2222_2222, 0x3333_3333, 0x4444_4444],
            _ => [Bits(1.5f), Bits(2.5f), Bits(3.5f), Bits(4.5f)],
        };

        static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
        static uint Half(float value) => BitConverter.HalfToUInt16Bits((Half)value);
    }

    // The bytes of the texel a store of every component through select writes (the expectation for one known select).
    private static byte[] Texel(uint guestFormat, bool d16, uint dmask, uint select)
    {
        var bytes = new List<byte>();
        float[] floats = guestFormat == 56 ? [0.2f, 0.4f, 0.6f, 0.8f] : [1.5f, 2.5f, 3.5f, 4.5f];
        uint[] uints = [0x1111_1111, 0x2222_2222, 0x3333_3333, 0x4444_4444];
        for (var channel = 0; channel < 4; channel++)
        {
            var component = Gen5ShaderTranslator.GetImageStoreSourceIndex(select, dmask, channel);
            switch (guestFormat)
            {
                case 56:
                    bytes.Add(component < 0 ? (byte)0 : (byte)Math.Round(floats[component] * 255));
                    break;
                case 71:
                    var half = component < 0 ? (ushort)0 : BitConverter.HalfToUInt16Bits((Half)floats[component]);
                    bytes.AddRange(BitConverter.GetBytes(half));
                    break;
                case 75:
                    bytes.AddRange(BitConverter.GetBytes(component < 0 ? 0u : uints[component]));
                    break;
                default:
                    bytes.AddRange(BitConverter.GetBytes(component < 0 ? 0f : floats[component]));
                    break;
            }
        }

        _ = d16;
        return [.. bytes];
    }

    private static (byte[] Texel, byte[] Spirv) Store(ImageTestHarness harness, uint guestFormat, Format format,
        GuestPixelFormat pixelFormat, uint texelBytes, bool d16, uint dmask, uint select, bool runtime)
    {
        // v1, v2 = (0, 0); the data registers from v4; one store of the dmask's components to texel (0, 0).
        var instructions = new List<Gen5ShaderInstruction> { MoveVector(0, 1, 0), MoveVector(8, 2, 0) };
        var pc = 16u;
        foreach (var (word, index) in Data(guestFormat, d16).Select((word, index) => (word, index)))
        {
            instructions.Add(MoveVector(pc, 4 + (uint)index, word));
            pc += 8;
        }

        var control = new Gen5ImageControl(dmask, 1, [1, 2, 3], 4, ResourceRegister, 0, 1, false, Glc: false, Slc: false, A16: false, D16: d16);
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Mimg, "ImageStore", [0u, 0u],
            [Gen5Operand.Vector(1), Gen5Operand.Scalar(ResourceRegister), Gen5Operand.Scalar(0)], [], control));
        instructions.Add(EndProgram(pc + 8));
        var program = Program([.. instructions]);
        var userData = new uint[16];
        userData[ResourceRegister] = 0x1000;
        userData[ResourceRegister + 1] = guestFormat << 20;
        userData[ResourceRegister + 2] = 1 | (1 << 14);
        userData[ResourceRegister + 3] = (ImageType2D << 28) | select;
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 16, runtimeImageSwizzles: runtime);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization, out var failure),
            failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        Assert.Equal(runtime ? 1u : 0u, layout.StorageImageSwizzleCount);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ArrayBackedSpace.Base, 2 * 2 * texelBytes * 2);
        description.PixelFormat = format;
        description.GuestFormat = pixelFormat;
        description.Extent = new Extent3D(2, 2, 1);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = 2;
        description.BytesPerBlock = texelBytes;
        var image = harness.CreateImage(description);
        var copies = ImageTestHarness.WholeImageCopies(description, 0);
        var size = ImageTestHarness.WholeImageBytes(description);
        harness.UploadImage(image, Enumerable.Repeat((byte)0xA5, (int)size).ToArray(), copies);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var view = new DescriptorImageInfo
        {
            ImageView = image.GetOrCreateView(ImageViewDescription.Default with { Format = format, Usage = ImageUsageFlags.StorageBit }),
            ImageLayout = ImageLayout.General,
        };
        var images = layout.Descriptors
            .Where(binding => ImageDescriptorBinding.ResourceClass(binding.Kind) == ImageResourceClass.Storage)
            .ToDictionary(binding => binding.Kind, binding => binding.Resources.Select(_ => view).ToArray());
        harness.Run(() =>
        {
            image.Transition(AccessFlags.ShaderWriteBit, null, new CommandBuffer(harness.Scheduler.Current.Handle));
            runner.Dispatch(userData, new Dictionary<DescriptorBindingKind, GpuBuffer[]>(), 1, boundImages: images,
                imageDescriptors: snapshot.Images);
        });
        var texel = harness.ReadImage(image, copies, size)[..(int)texelBytes];
        return (texel, shader.Spirv);
    }
}
