// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// An array read (MIMG dimension 2D array) of a 2D texture, a 2D-array texture or a null descriptor compiles as one
// program: each is bound as an array view. A storage image keeps its T#'s dimension.
public sealed class Gen5ArrayReadDimensionTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint Format32Float = 22;
    private const uint ImageType2D = 9;
    private const uint ImageType2DArray = 13;
    private const uint DimensionArray = 5;

    private static (ShaderResourcePlan Plan, ResourceSpecialization Specialization, Gen5ShaderProgram Program) Materialize(
        string opcode, uint imageType)
    {
        // x, y and the slice, then the LOD of a SAMPLE_L or the mip of a LOAD_MIP.
        var addressCount = opcode is "ImageSampleL" or "ImageLoadMip" ? 4 : 3;
        var control = new Gen5ImageControl(0xF, 0, Enumerable.Range(0, addressCount).Select(static value => (uint)value).ToArray(), 4, 8, 16,
            DimensionArray, true, false, false, false, false);
        var program = new Gen5ShaderProgram(ShaderAddress,
        [
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Mimg, opcode, [], [], [], control),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, ResourceTestProgram.Hash, 0, 20);
        var userData = new uint[20];
        if (imageType != 0)
        {
            userData[8] = 0x20;
            userData[9] = Format32Float << 20;
            userData[11] = imageType << 28;
            userData[12] = imageType == ImageType2DArray ? 3u : 0u;
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData), ref snapshot, ref specialization));
        return (plan, specialization, program);
    }

    private static List<SpirvOp> Compile(ShaderResourcePlan plan, ResourceSpecialization specialization, Gen5ShaderProgram program)
    {
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var opcodes = new List<SpirvOp>();
        for (var index = 20; index + 4 <= shader.Spirv.Length;)
        {
            var word = BitConverter.ToUInt32(shader.Spirv, index);
            opcodes.Add((SpirvOp)(word & 0xFFFF));
            index += (int)(word >> 16) * sizeof(uint);
        }

        return opcodes;
    }

    [Theory]
    [InlineData("ImageSample", ImageType2D)]
    [InlineData("ImageSample", ImageType2DArray)]
    [InlineData("ImageSample", 0u)]
    [InlineData("ImageSampleL", ImageType2D)]
    [InlineData("ImageLoad", ImageType2D)]
    [InlineData("ImageLoadMip", ImageType2D)]
    public void AnArrayReadOfAnyTextureIsCompiledAsAnArrayRead(string opcode, uint imageType)
    {
        var (plan, specialization, program) = Materialize(opcode, imageType);

        Assert.Equal(ImageDimension.Dim2DArray, Assert.Single(specialization.Images).Dimension);
        Compile(plan, specialization, program);
    }

    [Fact]
    public void AnArrayReadOfA2DTextureAndOfA2DArrayTextureIsOneProgram()
    {
        var (_, flat, _) = Materialize("ImageSample", ImageType2D);
        var (_, array, _) = Materialize("ImageSample", ImageType2DArray);
        var (_, unbound, _) = Materialize("ImageSample", 0);

        Assert.Equal(array, flat);
        Assert.Equal(array, unbound);
    }

    // A fetch past a view's last layer reads zero; the hardware clamps the slice.
    [Theory]
    [InlineData("ImageLoad", true)]
    [InlineData("ImageLoadMip", true)]
    [InlineData("ImageSample", false)]
    public void AFetchFromAnArrayClampsItsLayer(string opcode, bool clamps)
    {
        var (plan, specialization, program) = Materialize(opcode, ImageType2D);

        var opcodes = Compile(plan, specialization, program);
        Assert.Equal(clamps, opcodes.Contains(SpirvOp.ImageQuerySizeLod));
    }

    [Fact]
    public void AStorageImageKeepsItsDescriptorDimension()
    {
        var (_, specialization, _) = Materialize("ImageStore", ImageType2D);

        Assert.Equal(ImageDimension.Dim2D, Assert.Single(specialization.Images).Dimension);
    }
}
