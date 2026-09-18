// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Structural checks over the emitted MSL — these run on every platform because
/// translation is pure text generation; only the runtime tests need a Metal device.
/// </summary>
public sealed class MslTranslationTests
{
    [Fact]
    public void ComputeFixturesResolveDescriptorFormatsBeforeCompilation()
    {
        foreach (var fixture in new[] { Gen5ComputeFixtures.TypedLoad, Gen5ComputeFixtures.TypedStore })
        {
            var request = Gen5ComputeFixtures.CreateComputeRequest(fixture);
            Assert.NotEmpty(request.Resources.Info.Buffers);
            Assert.All(request.Resources.Info.Buffers, buffer =>
            {
                Assert.Equal(56u, buffer.DescriptorFormat);
                Assert.Equal(DescriptorConstants.IdentityDestinationSelect, buffer.DescriptorSwizzle);
            });
            _ = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
        }
    }

    [Fact]
    public void SadU32UsesUnsignedAbsoluteDifferenceAndAccumulator()
    {
        var sad = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop3,
            "VSadU32",
            [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            [Gen5Operand.Vector(3)],
            null);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0, [sad, end]), ShaderStage.Compute, localSizeX: 1);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("uint v[256]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("((max(v[0], v[1]) - min(v[0], v[1])) + (v[2]))", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void F16CompareUsesHalfOperands()
    {
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vopc,
            "VCmpLtF16",
            [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [],
            null);
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0, [compare, end]), ShaderStage.Compute, localSizeX: 1);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("as_type<half>", shader.Source, StringComparison.Ordinal);
        Assert.Contains(" < ", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFixtureTranslates()
    {
        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
            Assert.Equal(Gen5MslStage.Compute, shader.Stage);
            Assert.Equal("gen5_cs", shader.EntryPoint);
            Assert.Contains("kernel void gen5_cs(", shader.Source, StringComparison.Ordinal);
            Assert.Contains("while (active)", shader.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecMaskedStoresAreGuarded()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.ExecStore);

        // Every buffer store must sit behind the per-lane EXEC guard.
        Assert.Contains("if (exec)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_store_bytes(b0,", shader.Source, StringComparison.Ordinal);

        // s_mov_b32 exec_lo, 0 / -1 must drive the per-lane bool.
        Assert.Contains("exec = ((", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoopFixtureProducesMultipleDispatcherBlocks()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.Loop);

        // The backward branch splits the program into at least three blocks and
        // the conditional branch selects between loop head and fallthrough.
        Assert.Contains("case 0u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("case 1u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("case 2u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("pc = (scc) ?", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatcherIsBoundedByDefault()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.Fmac);
        Assert.Contains("if (++steps >=", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentBufferCarriesBufferLengthsAndUsesSeparatePushData()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.ExecStore);
        Assert.NotNull(shader.ArgumentLayout);
        Assert.Contains("buffer_bytes [[id(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_resources [[buffer(0)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_push_data [[buffer(1)]]", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpEmuUniforms", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelStageEmitsFragmentInterface()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();

        Assert.Equal(Gen5MslStage.Pixel, shader.Stage);
        Assert.Equal("gen5_ps", shader.EntryPoint);
        Assert.Equal(1u, shader.AttributeCount);
        Assert.Contains("fragment Gen5PsOut gen5_ps(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 attr0 [[user(locn0)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[color(0)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[position]]", shader.Source, StringComparison.Ordinal);

        // Interpolation reads land in VGPRs; the export writes MRT0 under EXEC
        // and inactive lanes discard at the end.
        Assert.Contains("as_type<uint>(sharpemu_in.attr0[0])", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.mrt0 = exec ?", shader.Source, StringComparison.Ordinal);
        Assert.Contains("discard_fragment();", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NullValidMaskExportControlsFragmentDiscard()
    {
        var export = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            [],
            new Gen5ExportControl(9, 0, false, true, true));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [0xBF810000],
            [],
            [],
            null);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0, [export, end]), ShaderStage.Pixel, pixelOutputs: []);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains(
            "bool pixel_valid_mask_active = true;",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "pixel_valid_mask_active = exec;",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!pixel_valid_mask_active)",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PixelOutputKindsSelectTheAttachmentType()
    {
        var uintShader = Gen5ComputeFixtures.CompilePixelOrThrow(Gen5PixelOutputKind.Uint);
        Assert.Contains("uint4 mrt0 [[color(0)]];", uintShader.Source, StringComparison.Ordinal);

        var sintShader = Gen5ComputeFixtures.CompilePixelOrThrow(Gen5PixelOutputKind.Sint);
        Assert.Contains("int4 mrt0 [[color(0)]];", sintShader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityPixelOutputKeepsGuestComponentOrder()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow();

        Assert.Contains(
            "vec<float, 4>(as_type<float>(v[0]), as_type<float>(v[1]), " +
            "as_type<float>(v[2]), as_type<float>(v[3]))",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BgraPixelOutputMapsGuestComponentsToPhysicalOrder()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow(
            componentMapping: new Gen5ColorComponentMapping(0xC6));

        Assert.Contains(
            "vec<float, 4>(as_type<float>(v[2]), as_type<float>(v[1]), " +
            "as_type<float>(v[0]), as_type<float>(v[3]))",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VertexStageEmitsVertexInterface()
    {
        var shader = Gen5ComputeFixtures.CompileVertexOrThrow();

        Assert.Equal(Gen5MslStage.Vertex, shader.Stage);
        Assert.Equal("gen5_vs", shader.EntryPoint);
        Assert.Equal(1u, shader.AttributeCount);
        Assert.Contains("vertex Gen5VsOut gen5_vs(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 sharpemu_position [[position]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 param0 [[user(locn0)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint sharpemu_vertex_id [[vertex_id]],", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v[5] = sharpemu_vertex_id;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v[8] = sharpemu_instance_id;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.sharpemu_position = exec ?", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_out.param0 = exec ?", shader.Source, StringComparison.Ordinal);
        Assert.Contains("return sharpemu_out;", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredVertexOutputsAreZeroFilledDeclarations()
    {
        // The paired fragment shader reads locations 0..2; the program only
        // exports param0, so 1 and 2 must still be declared (zero-filled).
        var shader = Gen5ComputeFixtures.CompileVertexOrThrow(requiredVertexOutputCount: 3);
        Assert.Equal(3u, shader.AttributeCount);
        Assert.Contains("float4 param1 [[user(locn1)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 param2 [[user(locn2)]];", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void FixedShadersCoverThePresenterSurface()
    {
        var fullscreen = MslFixedShaders.CreateFullscreenVertex(2);
        Assert.Contains("vertex FullscreenOut fullscreen_vs(", fullscreen, StringComparison.Ordinal);
        Assert.Contains("float4 attr1 [[user(locn1)]];", fullscreen, StringComparison.Ordinal);

        Assert.Contains("tex0.sample(smp0, in.attr0.xy)", MslFixedShaders.CreateCopyFragment(), StringComparison.Ordinal);
        Assert.Contains("float4(1.0f, 0.0f, 1.0f, 1.0f)", MslFixedShaders.CreateSolidFragment(1f, 0f, 1f, 1f), StringComparison.Ordinal);
        Assert.Contains("return in.attr3;", MslFixedShaders.CreateAttributeFragment(3), StringComparison.Ordinal);
        Assert.Contains("fragment void depth_only_fs()", MslFixedShaders.CreateDepthOnlyFragment(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFACu, "as_type<float>(v[4]), as_type<float>(v[5]), as_type<float>(v[6]), as_type<float>(v[7])")]
    [InlineData(0x9F5u, "as_type<float>(v[7]), as_type<float>(v[4]), as_type<float>(v[5]), as_type<float>(v[6])")]
    [InlineData(0xF2Eu, "as_type<float>(v[6]), as_type<float>(v[5]), as_type<float>(v[4]), as_type<float>(v[7])")]
    [InlineData(0x3ACu, "as_type<float>(v[4]), as_type<float>(v[5]), as_type<float>(v[6]), 0.0f")]
    [InlineData(0xFA4u, "as_type<float>(v[4]), 0.0f, as_type<float>(v[6]), as_type<float>(v[7])")]
    public void ImageStoreAppliesInverseDescriptorSwizzle(
        uint dstSelect,
        string expectedComponents)
    {
        var shader = CompileImageStore(dstSelect, dmask: 0xF);

        Assert.Contains(
            $".write(vec<float, 4>({expectedComponents}),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageStoreTreatsZeroDmaskAsX()
    {
        var shader = CompileImageStore(
            Gen5ShaderTranslator.IdentityImageDstSelect,
            dmask: 0);

        Assert.Contains(
            ".write(vec<float, 4>(as_type<float>(v[4]), 0.0f, 0.0f, 0.0f),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImageStoreMipAppliesInverseDescriptorSwizzle()
    {
        var shader = CompileImageStore(
            0x9F5u,
            dmask: 0xF,
            opcode: "ImageStoreMip");

        Assert.Contains(
            ".write(vec<float, 4>(as_type<float>(v[7]), as_type<float>(v[4]), as_type<float>(v[5]), as_type<float>(v[6])),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UintImageStoreUsesUintTextureAndInverseDescriptorSwizzle()
    {
        var shader = CompileImageStore(
            0xF2Eu,
            dmask: 0xF,
            unifiedFormat: 69u); // FORMAT_16_16_16_16_UINT

        Assert.Contains(
            "texture2d<uint, access::write>",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".write(vec<uint, 4>(v[6], v[5], v[4], v[7]),",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedOpcodeFailsLoudlyWithPc()
    {
        // v_cubeid_f32 is real but outside the phase-1 ALU set: the translator
        // must name the opcode and pc instead of emitting wrong code.
        var fixture = new Gen5ComputeFixture(
            "unsupported",
            [
                0xD5C40000, 0x04060501, // v_cubeid_f32 v0, v1, v2, v3
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);
        var exception = Assert.Throws<InvalidOperationException>(
            () => Gen5ComputeFixtures.CompileRequestOrThrow(fixture));
        Assert.Contains("pc=0x", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeVectorSourceUsesM0ForDynamicRead()
    {
        var fixture = new Gen5ComputeFixture(
            "relative-vector-source",
            [
                0x7E6E870C, // v_movrels_b32 v55, v12
                0xBF810000, // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("12u + (s[124])", shader.Source, StringComparison.Ordinal);
        Assert.Contains("< 256u ?", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarBlockerOpcodesCompileWithRdna2Semantics()
    {
        var fixture = new Gen5ComputeFixture(
            "scalar-blockers",
            [
                0xBF130200, // s_cmp_lg_u64 s[0:1], s[2:3]
                0xBE861404, // s_ff1_i32_b64 s6, s[4:5]
                0xBEEB106A, // s_bcnt1_i32_b64 s107, s[106:107]
                0xBE890908, // s_wqm_b32 s9, s8
                0xBF810000, // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains(" != ", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(uint)ctz(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(uint)popcount(", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0x11111111u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("* 0xFu", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void DataShareWaveCountersUseOneAtomicPerWave()
    {
        var fixture = new Gen5ComputeFixture(
            "data-share-wave-counters",
            [
                0xD8FA0014, 0x07000000, // ds_append v7 offset:20
                0xD8F60014, 0x08000000, // ds_consume v8 offset:20
                0xBF810000,             // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("popcount(", shader.Source, StringComparison.Ordinal);
        Assert.Contains(">> 16u", shader.Source, StringComparison.Ordinal);
        Assert.Contains("& 0xFFFFu", shader.Source, StringComparison.Ordinal);
        Assert.Contains("atomic_fetch_add_explicit", shader.Source, StringComparison.Ordinal);
        Assert.Contains("atomic_fetch_sub_explicit", shader.Source, StringComparison.Ordinal);
        Assert.Contains("simd_broadcast", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xBF970001u)]
    [InlineData(0xBF980001u)]
    [InlineData(0xBF990001u)]
    [InlineData(0xBF9A0001u)]
    public void DebugConditionBranchesFallThroughWithoutShaderDebugger(uint branch)
    {
        var fixture = new Gen5ComputeFixture(
            "debug-condition-branch",
            [
                branch,
                0xBF800000, // s_nop 0
                0xBF810000, // s_endpgm
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);

        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);

        Assert.Contains("pc = (false) ?", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, 22u)]
    [InlineData(false, 0u)]
    public void TypedBufferLoadUsesTheInstructionFormatAndUntypedKeepsTheDescriptorTable(bool typed, uint typedFormat)
    {
        var load = new Gen5ShaderInstruction(
            0,
            typed ? Gen5ShaderEncoding.Mtbuf : Gen5ShaderEncoding.Mubuf,
            typed ? "TBufferLoadFormatXyzw" : "BufferLoadFormatXyzw",
            [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(8), Gen5Operand.Source(128, null)],
            [Gen5Operand.Vector(4)],
            new Gen5BufferMemoryControl(
                4,
                0,
                4,
                8,
                0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false,
                Typed: typed,
                TypedFormat: typedFormat));
        var end = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);
        var scalars = new uint[256];
        scalars[8] = 0x2000;
        scalars[10] = 64;
        scalars[11] = 77u << 12;
        var shader = CompileMaterialized(new Gen5ShaderProgram(0, [load, end]), scalars[..12]);

        if (typed)
        {
            Assert.DoesNotContain("sharpemu_gfx10_formats[(", shader.Source, StringComparison.Ordinal);
            Assert.Contains("sharpemu_format_layout(4u, 0u,", shader.Source, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("sharpemu_gfx10_formats[(", shader.Source, StringComparison.Ordinal);
            Assert.Contains("== 7u ?", shader.Source, StringComparison.Ordinal);
            Assert.Contains(": true);", shader.Source, StringComparison.Ordinal);
            Assert.DoesNotContain("sharpemu_format_bytes", shader.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TypedBufferStoreEncodesAndPlacesEachComponentInOneElement()
    {
        // Format 14 is 8_8 unorm: both registers are encoded, packed into one element
        // with a two-byte mask, and stored under the descriptor and element bounds.
        var shader = CompileTypedBufferAccess("TBufferStoreFormatXy", dwordCount: 2, typedFormat: 14);

        Assert.Contains("if (exec && ", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_format_encode(v[4], 8u, 0u, 3u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_format_encode(v[5], 8u, 0u, 3u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("<< 8u), 0u, 0u, 0u), uint4(0xFFFFu, 0x0u, 0x0u, 0x0u));", shader.Source, StringComparison.Ordinal);
        Assert.Contains("2u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_store_bytes(b", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedBufferStoreOfWholeDwordsKeepsTheRegisterBits()
    {
        // Format 77 is 32x4 float: four aligned dword stores, no encoding, one bounds test.
        var shader = CompileTypedBufferAccess("TBufferStoreFormatXyzw", dwordCount: 4, typedFormat: 77);

        Assert.Contains(", v[4], 4u);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("+ 12u), v[7], 4u);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("16u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_format_encode(v[", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_store_element(b", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TBufferLoadFormatX", 1u, 4u)]
    [InlineData("TBufferLoadFormatXy", 2u, 8u)]
    [InlineData("TBufferStoreFormatX", 1u, 4u)]
    [InlineData("TBufferStoreFormatXy", 2u, 8u)]
    public void TypedBufferBoundsUseOnlyTransferredComponents(string opcode, uint componentCount, uint accessBytes)
    {
        var shader = CompileTypedBufferAccess(opcode, componentCount, typedFormat: 77);
        Assert.Contains($"{accessBytes}u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("16u <= sharpemu_resources.buffer_bytes[0] && ", shader.Source, StringComparison.Ordinal);
    }

    private static Gen5MslShader CompileTypedBufferAccess(string opcode, uint dwordCount, uint typedFormat)
    {
        var data = new Gen5Operand[dwordCount];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = Gen5Operand.Vector(4 + (uint)index);
        }

        var access = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mtbuf,
            opcode,
            [0, 0],
            [Gen5Operand.Vector(0), Gen5Operand.Scalar(8), Gen5Operand.Source(128, null)],
            data,
            new Gen5BufferMemoryControl(
                dwordCount,
                0,
                4,
                8,
                0,
                IndexEnabled: false,
                OffsetEnabled: false,
                Glc: false,
                Slc: false,
                Typed: true,
                TypedFormat: typedFormat));
        var end = new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);
        var scalars = new uint[256];
        scalars[8] = 0x2000;
        scalars[10] = 64;
        scalars[11] = 77u << 12;
        var shader = CompileMaterialized(new Gen5ShaderProgram(0, [access, end]), scalars[..12]);
        return shader;
    }

    [Fact]
    public void FormattedVertexFetchZeroFillsMissingComponents()
    {
        var fetch = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mubuf,
            "BufferLoadFormatXyz",
            [],
            [],
            [],
            new Gen5BufferMemoryControl(
                3,
                5,
                0,
                0,
                0,
                IndexEnabled: true,
                OffsetEnabled: false,
                Glc: false,
                Slc: false));
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var program = new Gen5ShaderProgram(0, [fetch, end]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, 1, 0, 0, new HashSet<uint> { 0 });
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            VertexInputs = [new ShaderVertexInput(0, 0, 2, 7, false, [])],
        };
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Contains("v[2] = 0u;", shader.Source, StringComparison.Ordinal);
    }

    private static Gen5MslShader CompileImageStore(
        uint dstSelect,
        uint dmask,
        string opcode = "ImageStore",
        uint unifiedFormat = 71u)
    {
        var control = new Gen5ImageControl(
            Dmask: dmask,
            VectorAddress: 0,
            AddressRegisters: [0, 1],
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: 1,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var store = new Gen5ShaderInstruction(
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
            [0xBF810000],
            [],
            [],
            null);
        var userData = new uint[16];
        userData[8] = 0x20;
        userData[9] = unifiedFormat << 20;
        userData[11] = (9u << 28) | dstSelect;
        return CompileMaterialized(new Gen5ShaderProgram(0x1_0000_C000, [store, end]), userData);
    }
    private static Gen5MslShader CompileMaterialized(Gen5ShaderProgram program, uint[] userData)
    {
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1, 0, (uint)userData.Length);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, (uint)userData.Length), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }

}
