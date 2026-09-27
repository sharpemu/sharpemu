// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{

    private const ulong ShaderAddress = 0x1_0000_C000;

    [Theory]
    [InlineData("ImageStore", SpirvOp.ImageWrite, 1)]
    [InlineData("ImageLoad", SpirvOp.ImageFetch, 3)]
    public void ArrayImageAccess_PreservesTheLayerCoordinate(string opcode, SpirvOp operation, int coordinateOperand)
    {
        var instructions = ReadSpirvInstructions(CompileImageOperation(opcode, 5,
            descriptorType: 13, arrayed: true));
        var imageType = Assert.Single(instructions, item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal(1u, imageType.Operands[4]);
        AssertCoordinateVectorWidth(instructions, operation, coordinateOperand, 3);
    }

    [Theory]
    [InlineData("ImageSampleLz", 2u, 9u, SpirvImageDim.Dim2D)]
    [InlineData("ImageStore", 2u, 9u, SpirvImageDim.Dim2D)]
    [InlineData("ImageSampleLz", 1u, 10u, SpirvImageDim.Dim3D)]
    [InlineData("ImageStore", 1u, 10u, SpirvImageDim.Dim3D)]
    public void BoundDescriptorControlsVolumeImageType(
        string opcode, uint instructionDimension, uint descriptorType, SpirvImageDim expected)
    {
        var instructions = ReadSpirvInstructions(CompileImageOperation(opcode, instructionDimension,
            descriptorType: descriptorType));
        var imageType = Assert.Single(instructions, item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal((uint)expected, imageType.Operands[2]);
        if (opcode == "ImageStore")
            AssertCoordinateVectorWidth(instructions, SpirvOp.ImageWrite, 1, expected == SpirvImageDim.Dim3D ? 3u : 2u);
    }
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(1u, SpirvImageDim.Dim2D, 2u)]
    [InlineData(2u, SpirvImageDim.Dim3D, 3u)]
    public void ImageStoreDimensionControlsImageAndCoordinateTypes(
        uint dimension,
        SpirvImageDim expectedImageDimension,
        uint expectedCoordinateComponents)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageStore", dimension));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)expectedImageDimension, imageType.Operands[2]);
        Assert.Equal(2u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageWrite,
            coordinateOperand: 1,
            expectedComponents: expectedCoordinateComponents);

        // Image stores outside the image bounds are discarded by the host API.
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageQuerySize);
    }

    [Fact]
    public void ImageSampleDim3DUsesThreeComponentSampleCoordinates()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageSampleLz", dimension: 2));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim3D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageSampleExplicitLod,
            coordinateOperand: 3,
            expectedComponents: 3);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    public void ImageSampleCompareUsesNativeDepthComparison(
        uint compareFunction)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageSampleCLz",
                dimension: 1,
                samplerWord0: compareFunction << 12,
                unifiedFormat: 22u));

        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
    }

    [Theory]
    [InlineData(1u, SpirvOp.FOrdLessThan)]
    [InlineData(4u, SpirvOp.FOrdGreaterThan)]
    [InlineData(6u, SpirvOp.FOrdGreaterThanEqual)]
    public void ImageSampleCompareOnAColorFormatComparesInTheShader(
        uint compareFunction,
        SpirvOp expectedComparison)
    {
        // RGBA8 has no Vulkan depth-compare view, so the shader samples and compares.
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageSampleCLz",
                dimension: 1,
                samplerWord0: compareFunction << 12));

        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageSampleExplicitLod);
        Assert.Contains(instructions, item => item.Opcode == expectedComparison);
    }

    [Fact]
    public void ImageSampleCompareLzUsesNativeFilteringWithTheSampler()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageSampleCLz",
                dimension: 1,
                samplerWord0: 0x00006012,
                samplerWord2: 0x00500000,
                unifiedFormat: 22u));

        Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageSampleDrefExplicitLod);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageFetch);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageSampleExplicitLod);
    }

    [Fact]
    public void ImageGatherCompareLzUsesNativeDepthGather()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageGather4CLz", dimension: 1, unifiedFormat: 22u));

        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ImageDrefGather);
    }

    [Theory]
    [InlineData(0xFACu, 0xFu, 4, 5, 6, 7)]
    [InlineData(0x9F5u, 0xFu, 7, 4, 5, 6)]
    [InlineData(0xF2Eu, 0xFu, 6, 5, 4, 7)]
    [InlineData(0x3ACu, 0xFu, 4, 5, 6, -1)]
    [InlineData(0xFA4u, 0xFu, 4, -1, 6, 7)]
    [InlineData(0xFACu, 0x0u, 4, -1, -1, -1)]
    public void ImageStoreAppliesInverseDescriptorSwizzle(
        uint dstSelect,
        uint dmask,
        int physicalX,
        int physicalY,
        int physicalZ,
        int physicalW)
    {
        var spirv = CompileImageOperation(
            "ImageStore",
            dimension: 1,
            dmask,
            dstSelect);

        Assert.Equal(
            [physicalX, physicalY, physicalZ, physicalW],
            GetStoredVgprRegisters(spirv));
    }

    [Fact]
    public void ImageStoreMipAppliesInverseDescriptorSwizzle()
    {
        var spirv = CompileImageOperation(
            "ImageStoreMip",
            dimension: 1,
            dmask: 0xF,
            dstSelect: 0x9F5u);

        Assert.Equal([7, 4, 5, 6], GetStoredVgprRegisters(spirv));
    }

    [Fact]
    public void UintImageStoreUsesUintTexelAndInverseDescriptorSwizzle()
    {
        var spirv = CompileImageOperation(
            "ImageStore",
            dimension: 1,
            dmask: 0xF,
            dstSelect: 0xF2Eu,
            unifiedFormat: 69u); // FORMAT_16_16_16_16_UINT
        var instructions = ReadSpirvInstructions(spirv);
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        var scalarType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeInt &&
                item.Operands[0] == imageType.Operands[1]);

        Assert.Equal(0u, scalarType.Operands[2]);
        Assert.Equal([6, 5, 4, 7], GetStoredVgprRegisters(spirv));
    }

    private static byte[] CompileImageOperation(
        string opcode,
        uint dimension,
        uint dmask = 0xF,
        uint dstSelect = Gen5ShaderTranslator.IdentityImageDstSelect,
        uint unifiedFormat = 71u,
        uint samplerWord0 = 0u,
        uint samplerWord2 = 0u,
        uint? descriptorType = null,
        bool arrayed = false)
    {
        var coordinateCount = dimension == 2 || arrayed ? 3 : 2;
        var addressCount = coordinateCount +
            (opcode.Contains("SampleC", StringComparison.Ordinal) ? 1 : 0);
        var addressRegisters = Enumerable.Range(0, addressCount)
            .Select(static value => (uint)value)
            .ToArray();
        var control = new Gen5ImageControl(
            Dmask: dmask,
            VectorAddress: 0,
            AddressRegisters: addressRegisters,
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: dimension,
            IsArray: arrayed,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var imageInstruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(ShaderAddress, [imageInstruction, end]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, ResourceTestProgram.Hash, 0, 20);
        var userData = new uint[20];
        userData[8] = 0x20;
        userData[9] = unifiedFormat << 20;
        userData[11] = ((descriptorType ?? (dimension == 2 ? 10u : 9u)) << 28) | dstSelect;
        userData[16] = samplerWord0;
        userData[18] = samplerWord2;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData),
            ref snapshot, ref specialization));
        if (opcode.Contains("Sample", StringComparison.Ordinal))
        {
            Assert.Equal(samplerWord0, Assert.Single(snapshot.Samplers)[0]);
            Assert.Equal(samplerWord2, snapshot.Samplers[0][2]);
        }

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 20), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static int[] GetStoredVgprRegisters(byte[] spirv)
    {
        var instructions = ReadSpirvInstructions(spirv);
        var write = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageWrite);
        var texel = FindResult(
            instructions,
            SpirvOp.CompositeConstruct,
            write.Operands[2]);
        Assert.Equal(6, texel.Operands.Length);

        var result = new int[4];
        for (var component = 0; component < result.Length; component++)
        {
            var valueId = texel.Operands[component + 2];
            var value = FindValueDefinition(instructions, valueId);
            if (value.Opcode == SpirvOp.Constant)
            {
                result[component] = -1;
                continue;
            }

            if (value.Opcode == SpirvOp.Bitcast)
            {
                value = FindValueDefinition(instructions, value.Operands[2]);
            }

            Assert.Equal(SpirvOp.Load, value.Opcode);
            var pointer = FindResult(
                instructions,
                SpirvOp.AccessChain,
                value.Operands[2]);
            var register = FindResult(
                instructions,
                SpirvOp.Constant,
                pointer.Operands[^1]);
            result[component] = checked((int)register.Operands[2]);
        }

        return result;
    }

    private static ParsedSpirvInstruction FindValueDefinition(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        uint resultId) =>
        Assert.Single(
            instructions,
            item =>
                item.Operands.Length > 1 &&
                item.Operands[1] == resultId &&
                item.Opcode is SpirvOp.Bitcast or SpirvOp.Load or SpirvOp.Constant);

    private static ParsedSpirvInstruction FindResult(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        SpirvOp opcode,
        uint resultId) =>
        Assert.Single(
            instructions,
            item =>
                item.Opcode == opcode &&
                item.Operands.Length > 1 &&
                item.Operands[1] == resultId);

    private static IReadOnlyList<ParsedSpirvInstruction> ReadSpirvInstructions(
        byte[] spirv)
    {
        var instructions = new List<ParsedSpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(
                new ParsedSpirvInstruction((SpirvOp)(ushort)instruction, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static void AssertCoordinateVectorWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        SpirvOp operation,
        int coordinateOperand,
        uint expectedComponents)
    {
        var imageOperation = Assert.Single(
            instructions,
            item => item.Opcode == operation);
        var coordinateId = imageOperation.Operands[coordinateOperand];
        var coordinate = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.CompositeConstruct &&
                item.Operands.Length >= 2 &&
                item.Operands[1] == coordinateId);
        AssertVectorTypeWidth(
            instructions,
            coordinate.Operands[0],
            expectedComponents);
    }

    private static void AssertVectorTypeWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        uint vectorTypeId,
        uint expectedComponents)
    {
        var vectorType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeVector &&
                item.Operands[0] == vectorTypeId);
        Assert.Equal(expectedComponents, vectorType.Operands[2]);
    }

    private readonly record struct ParsedSpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);
}
