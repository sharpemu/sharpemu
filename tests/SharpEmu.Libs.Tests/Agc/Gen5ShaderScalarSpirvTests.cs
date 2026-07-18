// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderScalarSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Theory]
    [InlineData("SCmpkEqU32", 0x0000_FFFFu)]
    [InlineData("SCmpkEqI32", 0xFFFF_FFFFu)]
    public void SopkCompare_UsesArchitecturalImmediateExtension(
        string opcode,
        uint expectedImmediate)
    {
        var module = Compile(
            [
                Instruction(
                    0,
                    Gen5ShaderEncoding.Sopk,
                    opcode,
                    [new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xFFFF)],
                    [Gen5Operand.Scalar(24)],
                    [0xFFFF]),
                End(4),
            ]);
        var constants = Constants(module);

        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.IEqual &&
                instruction.Operands.Length == 4 &&
                ResolveConstant(
                    module,
                    constants,
                    instruction.Operands[3]) == expectedImmediate);
    }

    [Fact]
    public void SAddkI32_EmitsSignedOverflowAndStoresScc()
    {
        var module = Compile(
            [
                Instruction(
                    0,
                    Gen5ShaderEncoding.Sopk,
                    "SAddkI32",
                    [new Gen5Operand(Gen5OperandKind.EncodedConstant, 1)],
                    [Gen5Operand.Scalar(24)],
                    [1]),
                End(4),
            ]);

        Assert.Contains(module, instruction => instruction.Opcode == SpirvOp.LogicalAnd);
        Assert.Equal(2, CountStoresToNamedVariable(module, "scc"));
    }

    [Theory]
    [InlineData("SBrevB32")]
    [InlineData("SFF1I32B32")]
    public void ScalarUnaryWithoutSccResult_DoesNotStoreScc(string opcode)
    {
        var module = Compile(
            [
                Instruction(
                    0,
                    Gen5ShaderEncoding.Sop1,
                    opcode,
                    [Gen5Operand.Scalar(24)],
                    [Gen5Operand.Scalar(25)]),
                End(4),
            ]);

        Assert.Equal(1, CountStoresToNamedVariable(module, "scc"));
    }

    [Fact]
    public void SWqmB64_EmitsQuadExpansionAndStoresScc()
    {
        var module = Compile(
            [
                Instruction(
                    0,
                    Gen5ShaderEncoding.Sop1,
                    "SWqmB64",
                    [Gen5Operand.Scalar(24)],
                    [Gen5Operand.Scalar(26)]),
                End(4),
            ]);

        Assert.True(module.Count(instruction =>
            instruction.Opcode == SpirvOp.ShiftRightLogical) >= 3);
        Assert.True(module.Count(instruction =>
            instruction.Opcode == SpirvOp.ShiftLeftLogical) >= 3);
        Assert.Contains(
            Constants(module).Values,
            value => value == 0x1111_1111u);
        Assert.Equal(2, CountStoresToNamedVariable(module, "scc"));
    }

    [Fact]
    public void Scalar64NegativeInlineInteger_IsSignExtended()
    {
        var module = Compile(
            [
                Instruction(
                    0,
                    Gen5ShaderEncoding.Sop1,
                    "SMovB64",
                    [new Gen5Operand(Gen5OperandKind.EncodedConstant, 193)],
                    [Gen5Operand.Scalar(24)]),
                End(4),
            ]);
        var unsigned64Type = FindIntegerType(module, 64, signed: false);

        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[0] == unsigned64Type &&
                instruction.Operands[2] == uint.MaxValue &&
                instruction.Operands[3] == uint.MaxValue);
    }

    [Fact]
    public void Scalar64LiteralInteger_RemainsZeroExtended()
    {
        var module = Compile(
            [
                Instruction(
                    0,
                    Gen5ShaderEncoding.Sop1,
                    "SMovB64",
                    [new Gen5Operand(Gen5OperandKind.LiteralConstant, uint.MaxValue)],
                    [Gen5Operand.Scalar(24)]),
                End(4),
            ]);
        var unsigned64Type = FindIntegerType(module, 64, signed: false);
        var constants = Constants(module);

        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.UConvert &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[0] == unsigned64Type &&
                constants.GetValueOrDefault(instruction.Operands[2]) == uint.MaxValue);
    }

    [Fact]
    public void ImageLoadMip_UsesRuntimeVgprLod()
    {
        var module = Compile(
            [
                ImageLoadMip(0),
                End(4),
            ]);
        var imageFetch = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageFetch);
        var lod = imageFetch.Operands[^1];
        var safeLod = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Select &&
                instruction.Operands.Length == 5 &&
                instruction.Operands[1] == lod);
        var requestedLod = safeLod.Operands[3];
        var lodBitcast = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Bitcast &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == requestedLod);

        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == lodBitcast.Operands[2]);
        Assert.DoesNotContain(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length >= 2 &&
                instruction.Operands[1] == lod);
    }

    [Fact]
    public void ImageLoadMip2DArray_DeclaresArrayed2DImage()
    {
        var module = Compile(
            [
                ImageLoadMip2DArray(0),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    baseArray: 2,
                    lastArray: 5)));
        var imageType = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim2D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[4]);
        Assert.Equal(1u, imageType.Operands[6]);

        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySizeLod);
        AssertIvec3Result(module, sizeQuery);
        var imageFetch = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageFetch);
        var layerCondition = AssertIvec3AccessHasLayerBounds(
            module,
            imageFetch.Operands[3],
            sizeQuery.Operands[1]);
        var fetchedComponent = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.CompositeExtract &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[2] == imageFetch.Operands[1]);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Select &&
                instruction.Operands.Length == 5 &&
                instruction.Operands[3] == fetchedComponent.Operands[1] &&
                DependsOn(module, instruction.Operands[2], layerCondition));
    }

    [Fact]
    public void ImageLoadMip2DViewOfArray_DeclaresNonArrayedImage()
    {
        var module = Compile(
            [
                ImageLoadMip(0),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    baseArray: 3,
                    lastArray: 7)));
        var imageType = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim2D, imageType.Operands[2]);
        Assert.Equal(0u, imageType.Operands[4]);
        Assert.Equal(1u, imageType.Operands[6]);

        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySizeLod);
        var sizeType = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.TypeVector &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[0] == sizeQuery.Operands[0]);
        Assert.Equal(2u, sizeType.Operands[2]);
    }

    [Fact]
    public void ImageStoreMip2DArray_UsesArrayLayerAndFourthAddressAsMip()
    {
        var module = Compile(
            [
                VectorMoveLiteral(0, 3, 2),
                ImageStoreMip2DArray(4),
                End(8),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    baseArray: 0,
                    lastArray: 31,
                    width: 30,
                    height: 17,
                    unifiedFormat: 71,
                    lastLevel: 3,
                    maxMip: 3)));
        var imageType = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim2D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[4]);
        Assert.Equal(2u, imageType.Operands[6]);
        Assert.Equal((uint)SpirvImageFormat.Rgba16f, imageType.Operands[7]);

        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySize);
        AssertIvec3Result(module, sizeQuery);
        var imageWrite = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageWrite);
        var layerCondition = AssertIvec3AccessHasLayerBounds(
            module,
            imageWrite.Operands[1],
            sizeQuery.Operands[1]);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.BranchConditional &&
                DependsOn(module, instruction.Operands[0], layerCondition));
    }

    [Fact]
    public void ImageAtomic2DArray_DiscardsOutOfBoundsLayers()
    {
        var module = Compile(
            [
                ImageAtomicAdd2DArray(0),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    lastArray: 7,
                    width: 30,
                    height: 17,
                    unifiedFormat: 20)));
        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySize);
        AssertIvec3Result(module, sizeQuery);
        var texelPointer = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageTexelPointer);
        var layerCondition = AssertIvec3AccessHasLayerBounds(
            module,
            texelPointer.Operands[3],
            sizeQuery.Operands[1]);

        Assert.Contains(
            module,
            instruction => instruction.Opcode == SpirvOp.AtomicIAdd);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.BranchConditional &&
                DependsOn(module, instruction.Operands[0], layerCondition));
    }

    [Fact]
    public void ImageGetResinfo2DArray_ReturnsTheViewLayerCount()
    {
        var module = Compile(
            [
                ImageGetResinfo2DArray(0),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    baseArray: 4,
                    lastArray: 11)));
        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySizeLod);

        AssertIvec3Result(module, sizeQuery);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.CompositeExtract &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[2] == sizeQuery.Operands[1] &&
                instruction.Operands[3] == 2u);
    }

    [Fact]
    public void ImageStoreMip3D_DeclaresStorage3DAndUsesFourthAddressAsMip()
    {
        var module = Compile(
            [
                VectorMoveLiteral(0, 3, 2),
                ImageInstruction(
                    4,
                    "ImageStoreMip",
                    dimension: 2,
                    addressRegisters: [0, 1, 2, 3],
                    dmask: 0xF),
                End(8),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 10,
                    width: 15,
                    height: 8,
                    unifiedFormat: 71,
                    lastLevel: 3,
                    maxMip: 3)));

        AssertImageType(
            module,
            SpirvImageDim.Dim3D,
            arrayed: false,
            sampled: 2);
        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySize);
        AssertIvec3Result(module, sizeQuery);
        var imageWrite = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageWrite);
        var depthCondition = AssertIvec3AccessHasLayerBounds(
            module,
            imageWrite.Operands[1],
            sizeQuery.Operands[1]);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.BranchConditional &&
                DependsOn(module, instruction.Operands[0], depthCondition));
    }

    [Fact]
    public void ImageSampleL3D_UsesXyzThenFourthAddressAsLod()
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageSampleL",
                    dimension: 2,
                    addressRegisters: [0, 1, 2, 3]),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType: 10)));

        AssertImageType(
            module,
            SpirvImageDim.Dim3D,
            arrayed: false,
            sampled: 1);
        var sample = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageSampleExplicitLod);
        var coordinate = AssertCompositeVector(
            module,
            sample.Operands[3],
            componentCount: 3);
        for (var component = 0; component < 3; component++)
        {
            AssertValueLoadsVectorRegister(
                module,
                coordinate.Operands[2 + component],
                (uint)component);
        }

        AssertValueLoadsVectorRegister(module, sample.Operands[^1], 3);
    }

    [Fact]
    public void ImageSampleCube_ReconstructsVulkanDirectionFromFaceLocalCoordinates()
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension: 3,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType: 11)));

        AssertImageType(
            module,
            SpirvImageDim.Cube,
            arrayed: false,
            sampled: 1);
        var sample = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageSampleExplicitLod);
        var direction = AssertCompositeVector(
            module,
            sample.Operands[3],
            componentCount: 3);
        Assert.All(
            direction.Operands.Skip(2),
            component => Assert.Contains(
                module,
                instruction =>
                    instruction.Opcode == SpirvOp.Select &&
                    instruction.Operands.Length == 5 &&
                    instruction.Operands[1] == component));
        Assert.True(module.Count(instruction => instruction.Opcode == SpirvOp.Select) >= 15);
        Assert.True(module.Count(instruction => instruction.Opcode == SpirvOp.IEqual) >= 5);
        Assert.Contains(module, instruction => instruction.Opcode == SpirvOp.ConvertFToU);
        Assert.Contains(
            Constants(module).Values,
            value => value == 7u);
        Assert.Contains(
            Constants(module).Values,
            value => value == BitConverter.SingleToUInt32Bits(1.5f));
        Assert.Contains(
            Constants(module).Values,
            value => value == BitConverter.SingleToUInt32Bits(2f));
    }

    [Fact]
    public void ImageGetResinfoCube_ReturnsIvec2()
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageGetResinfo",
                    dimension: 3,
                    addressRegisters: [0],
                    dmask: 0x7),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType: 11)));
        var sizeQuery = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageQuerySizeLod);

        AssertVectorWidth(module, sizeQuery.Operands[0], 2);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void Type9DescriptorWithNon2DInstructionShape_UsesFallbackShape(
        uint dimension)
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 9,
                    tileMode: 9)));

        var expectedDimension = dimension switch
        {
            2 => SpirvImageDim.Dim3D,
            3 => SpirvImageDim.Cube,
            _ => SpirvImageDim.Dim2D,
        };
        AssertImageType(
            module,
            expectedDimension,
            arrayed: dimension == 5,
            sampled: 1);

        if (dimension == 5)
        {
            var sample = Assert.Single(
                module,
                instruction => instruction.Opcode == SpirvOp.ImageSampleExplicitLod);
            var coordinates = AssertCompositeVector(
                module,
                sample.Operands[3],
                componentCount: 3);
            Assert.Equal(
                0u,
                ResolveConstant(
                    module,
                    Constants(module),
                    coordinates.Operands[4]));
        }
    }

    [Fact]
    public void Type9PlaceholderArrayGather_UsesNormalizedProxyLayer()
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageGather4Lz",
                    dimension: 5,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 9,
                    tileMode: 9)));

        AssertImageType(
            module,
            SpirvImageDim.Dim2D,
            arrayed: true,
            sampled: 1);
        var gather = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageGather);
        var coordinates = AssertCompositeVector(
            module,
            gather.Operands[3],
            componentCount: 3);
        Assert.Equal(
            0u,
            ResolveConstant(
                module,
                Constants(module),
                coordinates.Operands[4]));
    }

    [Fact]
    public void Physical2DArrayDescriptor_PreservesGuestLayerCoordinate()
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension: 5,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    lastArray: 3)));
        var sample = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageSampleExplicitLod);
        var coordinates = AssertCompositeVector(
            module,
            sample.Operands[3],
            componentCount: 3);

        AssertValueLoadsVectorRegister(module, coordinates.Operands[4], 2);
    }

    [Theory]
    [InlineData(2u, 1u, 0u, 0u, 0u)]
    [InlineData(1u, 2u, 0u, 0u, 0u)]
    [InlineData(1u, 1u, 1u, 1u, 1u)]
    [InlineData(1u, 1u, 0u, 1u, 1u)]
    [InlineData(1u, 1u, 0u, 0u, 1u)]
    public void NonPlaceholderType9ShapeMismatch_IsRejected(
        uint width,
        uint height,
        uint baseLevel,
        uint lastLevel,
        uint maxMip)
    {
        var error = CompileFailure(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension: 5,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 9,
                    width: width,
                    height: height,
                    baseLevel: baseLevel,
                    lastLevel: lastLevel,
                    maxMip: maxMip)));

        Assert.Contains("unsupported image resource shape", error, StringComparison.Ordinal);
        Assert.Contains("type=9 dim=5", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10u, 5u)]
    [InlineData(11u, 2u)]
    [InlineData(13u, 2u)]
    public void GenericSampledShapeMismatch_IsRejected(
        uint resourceType,
        uint dimension)
    {
        var error = CompileFailure(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType)));

        Assert.Contains("unsupported image resource shape", error, StringComparison.Ordinal);
        Assert.Contains(
            $"type={resourceType} dim={dimension}",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(5u)]
    public void StorageType9PlaceholderShapeMismatch_IsRejected(uint dimension)
    {
        var error = CompileFailure(
            [
                ImageInstruction(
                    0,
                    "ImageStore",
                    dimension,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType: 9)));

        Assert.Contains("unsupported image resource shape", error, StringComparison.Ordinal);
        Assert.Contains($"type=9 dim={dimension}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedTileType9PlaceholderShapeMismatch_IsRejected()
    {
        var error = CompileFailure(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension: 5,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 9,
                    tileMode: 27)));

        Assert.Contains("unsupported image resource shape", error, StringComparison.Ordinal);
        Assert.Contains("type=9 dim=5", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2u, 10u)]
    [InlineData(3u, 11u)]
    public void Non2DImageOffset_IsRejected(uint dimension, uint resourceType)
    {
        var error = CompileFailure(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLzO",
                    dimension,
                    addressRegisters: [0, 1, 2, 3]),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType)));

        Assert.Contains("offset", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImageSampleOffset2DArray_PreservesThreeCoordinatesAndTwoDimensionalOffset()
    {
        var module = Compile(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLzO",
                    dimension: 5,
                    addressRegisters: [0, 1, 2, 3]),
                End(4),
            ],
            ImageUserData(
                ResourceDescriptor(
                    resourceType: 13,
                    lastArray: 3)));

        AssertImageType(
            module,
            SpirvImageDim.Dim2D,
            arrayed: true,
            sampled: 1);
        var sample = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.ImageSampleExplicitLod);
        AssertCompositeVector(module, sample.Operands[3], componentCount: 3);
        Assert.True(module.Count(instruction => instruction.Opcode == SpirvOp.BitFieldSExtract) >= 2);
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(7u)]
    public void MultisampledImageDimension_IsRejected(uint dimension)
    {
        var error = CompileFailure(
            [
                ImageInstruction(
                    0,
                    "ImageSampleLz",
                    dimension,
                    addressRegisters: [0, 1, 2]),
                End(4),
            ],
            ImageUserData(ResourceDescriptor(resourceType: 9)));

        Assert.Contains("MSAA", error, StringComparison.Ordinal);
        Assert.Contains($"dim={dimension}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownTakenBranch_DoesNotCompileDeadFallthroughImage()
    {
        var module = Compile(
            [
                ScalarCompareEqual(0),
                Branch(4, "SCbranchScc1", 16),
                ImageSampleWithUnknownDescriptor(8),
                Branch(12, "SBranch", 20),
                Instruction(16, Gen5ShaderEncoding.Sopp, "SNop"),
                End(20),
            ]);

        Assert.DoesNotContain(
            module,
            instruction => instruction.Opcode == SpirvOp.TypeImage);
    }

    [Fact]
    public void KnownFallthrough_DoesNotCompileDeadBranchTargetImage()
    {
        var module = Compile(
            [
                ScalarCompareEqual(0),
                Branch(4, "SCbranchScc0", 16),
                Instruction(8, Gen5ShaderEncoding.Sopp, "SNop"),
                Branch(12, "SBranch", 20),
                ImageSampleWithUnknownDescriptor(16),
                End(20),
            ]);

        Assert.DoesNotContain(
            module,
            instruction => instruction.Opcode == SpirvOp.TypeImage);
    }

    private static IReadOnlyList<ParsedInstruction> Compile(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IReadOnlyList<uint>? userData = null)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, instructions),
            userData ?? ImageUserData(ResourceDescriptor(resourceType: 9)),
            null,
            null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out var shader,
                out error),
            error);
        return ParseModule(shader.Spirv);
    }

    private static string CompileFailure(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IReadOnlyList<uint>? userData = null)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, instructions),
            userData ?? ImageUserData(ResourceDescriptor(resourceType: 9)),
            null,
            null);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.False(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out _,
                out error));
        return error;
    }

    private static Gen5ShaderInstruction ImageLoadMip(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageLoadMip",
            [0],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(16),
            ],
            [Gen5Operand.Vector(4)],
            new Gen5ImageControl(
                1,
                0,
                [0, 1, 2],
                4,
                0,
                16,
                1,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction ImageLoadMip2DArray(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageLoadMip",
            [0],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(16),
            ],
            [Gen5Operand.Vector(4)],
            new Gen5ImageControl(
                1,
                0,
                [0, 1, 2, 3],
                4,
                0,
                16,
                5,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction ImageStoreMip2DArray(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageStoreMip",
            [0],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(16),
            ],
            [],
            new Gen5ImageControl(
                0xF,
                0,
                [0, 1, 2, 3],
                4,
                0,
                16,
                5,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction ImageGetResinfo2DArray(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageGetResinfo",
            [0],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(16),
            ],
            [Gen5Operand.Vector(4)],
            new Gen5ImageControl(
                0x7,
                0,
                [0],
                4,
                0,
                16,
                5,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction ImageAtomicAdd2DArray(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageAtomicAdd",
            [0],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(16),
            ],
            [Gen5Operand.Vector(4)],
            new Gen5ImageControl(
                1,
                0,
                [0, 1, 2],
                4,
                0,
                16,
                5,
                false,
                true,
                false,
                false,
                false));

    private static Gen5ShaderInstruction ImageInstruction(
        uint pc,
        string opcode,
        uint dimension,
        IReadOnlyList<uint> addressRegisters,
        uint dmask = 1,
        uint vectorData = 8)
    {
        var sources = addressRegisters
            .Select(Gen5Operand.Vector)
            .Append(Gen5Operand.Scalar(0))
            .Append(Gen5Operand.Scalar(16))
            .ToArray();
        IReadOnlyList<Gen5Operand> destinations =
            Gen5ShaderTranslator.IsStorageImageOperation(opcode)
                ? []
                : [Gen5Operand.Vector(vectorData)];
        return new Gen5ShaderInstruction(
            pc,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [0],
            sources,
            destinations,
            new Gen5ImageControl(
                dmask,
                addressRegisters.Count == 0 ? 0 : addressRegisters[0],
                addressRegisters,
                vectorData,
                0,
                16,
                dimension,
                dimension == 5,
                false,
                false,
                false,
                false));
    }

    private static Gen5ShaderInstruction VectorMoveLiteral(
        uint pc,
        uint destination,
        uint value) =>
        Instruction(
            pc,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, value)],
            [Gen5Operand.Vector(destination)]);

    private static uint[] ImageUserData(uint[] descriptor)
    {
        var userData = Enumerable.Range(0, 32)
            .Select(index => 0x1000_0000u + (uint)index)
            .ToArray();
        descriptor.CopyTo(userData, 0);
        return userData;
    }

    private static uint[] ResourceDescriptor(
        uint resourceType,
        uint baseArray = 0,
        uint lastArray = 0,
        uint width = 1,
        uint height = 1,
        uint unifiedFormat = 56,
        uint baseLevel = 0,
        uint lastLevel = 0,
        uint maxMip = 0,
        uint tileMode = 0) =>
        [
            0x0010_0000,
            (unifiedFormat << 20) | (((width - 1) & 0x3u) << 30),
            (((width - 1) >> 2) & 0xFFFu) |
                (((height - 1) & 0x3FFFu) << 14),
            (resourceType << 28) |
                ((baseLevel & 0xFu) << 12) |
                ((lastLevel & 0xFu) << 16) |
                ((tileMode & 0x1Fu) << 20) |
                0xFACu,
            (lastArray & 0x1FFFu) | ((baseArray & 0x1FFFu) << 16),
            (maxMip & 0xFu) << 4,
            0,
            0,
        ];

    private static Gen5ShaderInstruction ImageSampleWithUnknownDescriptor(uint pc) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageSample",
            [0],
            [],
            [Gen5Operand.Vector(0)],
            new Gen5ImageControl(
                1,
                0,
                [0, 1],
                0,
                40,
                48,
                2,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction ScalarCompareEqual(uint pc) =>
        Instruction(
            pc,
            Gen5ShaderEncoding.Sopc,
            "SCmpEqU32",
            [Gen5Operand.Scalar(24), Gen5Operand.Scalar(24)]);

    private static Gen5ShaderInstruction Branch(
        uint pc,
        string opcode,
        uint targetPc)
    {
        var delta = (long)targetPc - pc - sizeof(uint);
        Assert.Equal(0, delta % sizeof(uint));
        var offset = checked((short)(delta / sizeof(uint)));
        return Instruction(
            pc,
            Gen5ShaderEncoding.Sopp,
            opcode,
            words: [unchecked((ushort)offset)]);
    }

    private static Gen5ShaderInstruction End(uint pc) =>
        Instruction(pc, Gen5ShaderEncoding.Sopp, "SEndpgm");

    private static Gen5ShaderInstruction Instruction(
        uint pc,
        Gen5ShaderEncoding encoding,
        string opcode,
        IReadOnlyList<Gen5Operand>? sources = null,
        IReadOnlyList<Gen5Operand>? destinations = null,
        IReadOnlyList<uint>? words = null) =>
        new(
            pc,
            encoding,
            opcode,
            words ?? [0],
            sources ?? [],
            destinations ?? [],
            null);

    private static int CountStoresToNamedVariable(
        IReadOnlyList<ParsedInstruction> module,
        string name)
    {
        var variable = module
            .Where(instruction => instruction.Opcode == SpirvOp.Name)
            .Single(instruction => DecodeString(instruction.Operands, 1) == name)
            .Operands[0];
        return module.Count(instruction =>
            instruction.Opcode == SpirvOp.Store &&
            instruction.Operands.Length >= 1 &&
            instruction.Operands[0] == variable);
    }

    private static IReadOnlyDictionary<uint, uint> Constants(
        IReadOnlyList<ParsedInstruction> module) =>
        module
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length >= 3)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);

    private static uint? ResolveConstant(
        IReadOnlyList<ParsedInstruction> module,
        IReadOnlyDictionary<uint, uint> constants,
        uint value)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            if (constants.TryGetValue(value, out var constant))
            {
                return constant;
            }

            var bitcast = module.SingleOrDefault(instruction =>
                instruction.Opcode == SpirvOp.Bitcast &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == value);
            if (bitcast is null)
            {
                return null;
            }

            value = bitcast.Operands[2];
        }

        return null;
    }

    private static uint FindIntegerType(
        IReadOnlyList<ParsedInstruction> module,
        uint width,
        bool signed) =>
        module.Single(instruction =>
            instruction.Opcode == SpirvOp.TypeInt &&
            instruction.Operands.Length == 3 &&
            instruction.Operands[1] == width &&
            instruction.Operands[2] == (signed ? 1u : 0u)).Operands[0];

    private static void AssertImageType(
        IReadOnlyList<ParsedInstruction> module,
        SpirvImageDim dimension,
        bool arrayed,
        uint sampled)
    {
        var imageType = Assert.Single(
            module,
            instruction => instruction.Opcode == SpirvOp.TypeImage);
        Assert.Equal((uint)dimension, imageType.Operands[2]);
        Assert.Equal(arrayed ? 1u : 0u, imageType.Operands[4]);
        Assert.Equal(0u, imageType.Operands[5]);
        Assert.Equal(sampled, imageType.Operands[6]);
    }

    private static ParsedInstruction AssertCompositeVector(
        IReadOnlyList<ParsedInstruction> module,
        uint value,
        uint componentCount)
    {
        var construct = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.CompositeConstruct &&
                instruction.Operands.Length == checked((int)componentCount + 2) &&
                instruction.Operands[1] == value);
        AssertVectorWidth(module, construct.Operands[0], componentCount);
        return construct;
    }

    private static void AssertVectorWidth(
        IReadOnlyList<ParsedInstruction> module,
        uint vectorType,
        uint componentCount)
    {
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.TypeVector &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[0] == vectorType &&
                instruction.Operands[2] == componentCount);
    }

    private static void AssertValueLoadsVectorRegister(
        IReadOnlyList<ParsedInstruction> module,
        uint value,
        uint expectedRegister)
    {
        var bitcast = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Bitcast &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == value);
        var load = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] == bitcast.Operands[2]);
        var access = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.AccessChain &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[1] == load.Operands[2]);
        var vectorRegisters = module
            .Where(instruction => instruction.Opcode == SpirvOp.Name)
            .Single(instruction => DecodeString(instruction.Operands, 1) == "vgpr")
            .Operands[0];
        Assert.Equal(vectorRegisters, access.Operands[2]);
        Assert.Equal(
            expectedRegister,
            Constants(module).GetValueOrDefault(access.Operands[3], uint.MaxValue));
    }

    private static void AssertIvec3Result(
        IReadOnlyList<ParsedInstruction> module,
        ParsedInstruction instruction)
    {
        Assert.True(instruction.Operands.Length >= 2);
        var vectorType = Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.TypeVector &&
                candidate.Operands.Length == 3 &&
                candidate.Operands[0] == instruction.Operands[0]);
        Assert.Equal(3u, vectorType.Operands[2]);
        Assert.Contains(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.TypeInt &&
                candidate.Operands.Length == 3 &&
                candidate.Operands[0] == vectorType.Operands[1] &&
                candidate.Operands[1] == 32u &&
                candidate.Operands[2] == 1u);
    }

    private static uint AssertIvec3AccessHasLayerBounds(
        IReadOnlyList<ParsedInstruction> module,
        uint coordinates,
        uint imageSize)
    {
        var coordinateConstruct = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.CompositeConstruct &&
                instruction.Operands.Length == 5 &&
                instruction.Operands[1] == coordinates);
        AssertIvec3Result(module, coordinateConstruct);
        var safeLayer = coordinateConstruct.Operands[4];
        var layerSelect = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.Select &&
                instruction.Operands.Length == 5 &&
                instruction.Operands[1] == safeLayer);
        var layerCondition = layerSelect.Operands[2];
        var layerBounds = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.LogicalAnd &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[1] == layerCondition);
        var lowerBound = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.SGreaterThanEqual &&
                instruction.Operands.Length == 4 &&
                layerBounds.Operands.Skip(2).Contains(instruction.Operands[1]));
        var upperBound = Assert.Single(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.SLessThan &&
                instruction.Operands.Length == 4 &&
                layerBounds.Operands.Skip(2).Contains(instruction.Operands[1]));

        Assert.Equal(lowerBound.Operands[2], upperBound.Operands[2]);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.CompositeExtract &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[1] == upperBound.Operands[3] &&
                instruction.Operands[2] == imageSize &&
                instruction.Operands[3] == 2u);
        Assert.Contains(
            module,
            instruction =>
                instruction.Opcode == SpirvOp.CompositeExtract &&
                instruction.Operands.Length == 4 &&
                instruction.Operands[1] == lowerBound.Operands[2] &&
                instruction.Operands[3] == 2u);
        return layerCondition;
    }

    private static bool DependsOn(
        IReadOnlyList<ParsedInstruction> module,
        uint value,
        uint dependency) =>
        DependsOn(module, value, dependency, []);

    private static bool DependsOn(
        IReadOnlyList<ParsedInstruction> module,
        uint value,
        uint dependency,
        HashSet<uint> visited)
    {
        if (value == dependency)
        {
            return true;
        }

        if (!visited.Add(value))
        {
            return false;
        }

        var producer = module.SingleOrDefault(instruction =>
            instruction.Opcode == SpirvOp.LogicalAnd &&
            instruction.Operands.Length == 4 &&
            instruction.Operands[1] == value);
        return producer is not null &&
            producer.Operands
                .Skip(2)
                .Any(operand =>
                    DependsOn(module, operand, dependency, visited));
    }

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(header >> 16), 1);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint)), sizeof(uint)));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static string DecodeString(uint[] operands, int startIndex)
    {
        var bytes = new List<byte>();
        foreach (var word in operands.Skip(startIndex))
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(word >> shift);
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
