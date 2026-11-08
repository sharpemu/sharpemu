// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.GnmMode.Shaders;
using SharpEmu.GnmMode.Libraries.Gnm;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GcnShaderTests
{
    private const ulong Address = 0x100000000;

    [Fact]
    public void ConcurrentDecodesKeepEachShadersCapturedUserData()
    {
        var memory = Binary(0xF0800F00, 0, 0xBF810000);
        var context = new CpuContext(memory, Generation.Gen4);
        var frontend = new GcnShaderFrontend();
        uint[] flatData = [0, 0, 0, 9u << 28, 0, 0, 0, 0];
        uint[] volumeData = [0, 0, 0, 10u << 28, 0, 0, 0, 0];
        var flat = frontend.ReadShader(context, Address, "pixel", flatData);
        var volume = frontend.ReadShader(context, Address, "pixel", volumeData);
        flatData[3] = 10u << 28;
        volumeData[3] = 9u << 28;

        Parallel.For(0, 128, index =>
        {
            var shader = index % 2 == 0 ? flat : volume;
            Assert.True(frontend.TryDecode(context, shader, out var program, out var error), error);
            var sample = Assert.Single(program.Instructions, instruction => instruction.Opcode == "ImageSample");
            Assert.Equal(index % 2 == 0 ? 1u : 2u, Assert.IsType<Gen5ImageControl>(sample.Control).Dimension);
        });
    }

    [Fact]
    public void ParallelImageResolutionDefersTexelReadsAndWritesUntilBinding()
    {
        const ulong source = 0x100000, scratch = 0x200000;
        var memory = new TileImageMemory(source, 0x4000, scratch, 0x10000);
        var allocations = 0;
        var frontend = new GcnShaderFrontend(memory, _ =>
        {
            Interlocked.Increment(ref allocations);
            return scratch;
        });
        uint[] words = [(uint)(source >> 8), (4u << 20) | (7u << 26), 7u | (7u << 14),
            (9u << 28) | (13u << 20) | 0xFAC, 7u << 13, 0, 0, 0];
        Assert.Throws<SerialResolveRequiredException>(() =>
            frontend.NormalizeImageForResolution(words, ShaderStage.Pixel, 123, "test-cold", false));
        Assert.Equal(0, allocations);
        Assert.Equal(0, memory.SourceReads);
        Assert.Equal(0, memory.Writes);
        var reserved = frontend.NormalizeImageForResolution(words, ShaderStage.Pixel, 123, "test-reserve", true);
        Assert.Equal(1, allocations);
        var reads = memory.SourceReads;

        Parallel.For(0, 128, _ =>
            Assert.Equal(reserved, frontend.NormalizeImageForResolution(words, ShaderStage.Pixel, 123, "test-parallel", false)));

        Assert.Equal(reads, memory.SourceReads);
        Assert.Equal(0, memory.Writes);
        Assert.Equal(1, allocations);
        BinaryPrimitives.WriteUInt32LittleEndian(memory.Source, 0x12345678);
        Assert.True(frontend.PrepareTexture(reserved));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(memory.Scratch));
        Assert.True(memory.SourceReads > reads);
        Assert.Equal(1, memory.Writes);
        Assert.Equal(scratch, new TextureDescriptorWords(reserved).BaseAddress);
    }

    [Fact]
    public void ReadsTheRealOrbMetadataAndNotAnAgcHeader()
    {
        var memory = Binary(0x7E000280, 0xBF810000);
        var context = new CpuContext(memory, Generation.Gen4);
        var frontend = new GcnShaderFrontend();
        var registry = new ShaderHeaderRegistry(context, _ => 0, _ => null, frontend);
        var shader = registry.Require(Address, "compute");
        Assert.Equal(Address + 32, shader.HeaderAddress);
        Assert.Equal(16u, shader.CodeSizeBytes);
        Assert.Equal(0UL, shader.UserDataAddress);
        Assert.Same(frontend, shader.Frontend);
        Assert.True(frontend.TryDecode(context, shader, out var program, out var error), error);
        Assert.Equal(0u, program.VertexIndexRegister);
        Assert.Equal(new uint[] { 1, 2, 3 }, program.InstanceIndexRegisters);
        Assert.Equal(["SMovB32", "VMovB32", "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(4, GcnShaderBinary.Read(memory, Address).ShaderType);
        Assert.Equal(16, Assert.Single(registry.ReadDiagnosticCode(Address)).Code.Length);
    }

    [Fact]
    public void RegisteredAgcShadersKeepTheirExistingPathEvenWithALegacyFrontend()
    {
        var memory = new Bytes(new byte[256]);
        memory.Write(0x44, 4);
        var registry = new ShaderHeaderRegistry(new CpuContext(memory, Generation.Gen5), _ => Address, _ => null, new GcnShaderFrontend());
        var shader = registry.Require(Address + 128, "compute");
        Assert.Null(shader.Frontend);
        Assert.Equal(4u, shader.CodeSizeBytes);
    }

    [Theory]
    [InlineData(7, 8u)]
    [InlineData(7, 0u)]
    [InlineData(7, 3u)]
    [InlineData(7, 1024u)]
    [InlineData(8, 12u)]
    public void InvalidBinaryVersionOrLengthIsRejected(int version, uint size)
    {
        var memory = Binary(0xBF810000);
        var info = (int)(GcnShaderBinary.Read(memory, Address).InfoAddress - Address);
        memory.Data[info + 7] = (byte)version;
        memory.Write(info + 8, (size << 8) | 0x11);
        Assert.Throws<InvalidDataException>(() => GcnShaderBinary.Read(memory, Address));
    }

    [Fact]
    public void MissingSignatureAndOverflowingLocatorAreRejected()
    {
        var memory = Binary(0xBF810000);
        memory.Data[32] = 0;
        Assert.Throws<InvalidDataException>(() => GcnShaderBinary.Read(memory, Address));
        memory.Write(4, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => GcnShaderBinary.Read(memory, Address));
        Assert.False(GcnShaderDecoder.TryDecode(memory, ulong.MaxValue - 3, 8, out _, out _));
    }

    [Theory]
    [InlineData(0u, "VCndmaskB32")]
    [InlineData(1u, "VReadlaneB32")]
    [InlineData(2u, "VWritelaneB32")]
    [InlineData(30u, "VBfmB32")]
    [InlineData(43u, "VLdexpF32")]
    public void GcnOpcodesDoNotUseTheDifferentGen5Meaning(uint opcode, string name)
    {
        var program = Decode((opcode << 25) | (7u << 17) | (4u << 9) | 258u, 0xBF810000);
        var instruction = program.Instructions[0];
        Assert.Equal(name, instruction.Opcode);
        Assert.Single(instruction.Words);
        Assert.Equal(Gen5Operand.Vector(2), instruction.Sources[0]);
        if (opcode is 1 or 2)
            Assert.Equal(Gen5Operand.Scalar(4), instruction.Sources[1]);
        Assert.Equal(opcode == 1 ? Gen5Operand.Scalar(7) : Gen5Operand.Vector(7), instruction.Destinations[0]);
    }

    [Fact]
    public void GcnLdexpDoesNotConsumeTheFollowingInstructionAsAGen5Literal()
    {
        var program = Decode((43u << 25) | 256, 0xBF810000);
        Assert.Equal(4u, program.Instructions[1].Pc);
        Assert.Equal("SEndpgm", program.Instructions[1].Opcode);
    }

    [Fact]
    public void ScalarAndVectorLiteralWidthsPreserveTheOriginalProgramCounters()
    {
        var program = Decode(0xBE8003FF, 0x12345678, 0x7E0002FF, 0xABCDEF00, 0xBF810000);
        Assert.Equal([0u, 8u, 16u], program.Instructions.Select(instruction => instruction.Pc));
        Assert.Equal(Gen5Operand.Source(255, 0x12345678), program.Instructions[0].Sources[0]);
        Assert.Equal(Gen5Operand.Source(255, 0xABCDEF00), program.Instructions[1].Sources[0]);
        var truncated = Words(0x7E0002FF);
        Assert.False(GcnShaderDecoder.TryDecode(truncated, Address, 4, out _, out var error));
        Assert.Contains("truncated", error);
    }

    [Theory]
    [InlineData(0xBF8200FFu)]
    [InlineData(0xBF82FFFEu)]
    public void OutOfBoundsBranchesAreRejected(uint branch)
    {
        Assert.False(GcnShaderDecoder.TryDecode(Words(branch, 0xBF810000), Address, 8, out _, out var error));
        Assert.Contains("branch-outside-code", error);
    }

    [Fact]
    public void ForwardBranchesCoverOutOfLineBlocksButCannotTargetLiteralWords()
    {
        Assert.Equal(4, Decode(0xBF840001, 0xBF810000, 0x7E000280, 0xBF810000).Instructions.Count);
        var words = Words(0xBF840001, 0x7E0002FF, 0, 0xBF810000);
        Assert.False(GcnShaderDecoder.TryDecode(words, Address, 16, out _, out var error));
        Assert.Contains("inside-instruction", error);
    }

    [Theory]
    [InlineData(0xF0805F00u)]
    [InlineData(0xD2FE0000u)]
    [InlineData(0x7E0002F9u)]
    [InlineData(0x7C000000u | (0xA8u << 17))]
    public void UnimplementedImageVop3AndReservedOperandsAreNotSilentlyDecodedAsGen5(uint word)
    {
        Assert.False(GcnShaderDecoder.TryDecode(Words(word, 0, 0xBF810000), Address, 12, out _, out var error));
        Assert.Contains("unsupported-gcn-instruction", error);
    }

    [Fact]
    public void ScalarMemoryUsesGcnDwordOffsetsAndDestinationCount()
    {
        var program = Decode(0xC0800304, 0xBF810000);
        var instruction = program.Instructions[0];
        var control = Assert.IsType<Gen5ScalarMemoryControl>(instruction.Control);
        Assert.Equal("SLoadDwordx4", instruction.Opcode);
        Assert.Equal(4u, control.DestinationCount);
        Assert.Equal(16, control.ImmediateOffsetBytes);
        Assert.Equal(Gen5Operand.Scalar(2), instruction.Sources[0]);
        Assert.Equal(4, instruction.Destinations.Count);
    }

    [Fact]
    public void GcnVector3UsesNineBitOpcodesLegacyClampAndExplicitMaskDestinations()
    {
        var program = Decode(0xD2D20006, 0x00020A13, 0xD10A0004, 0x00020E80,
            0xD2000805, 0x00120F05, 0x7E0A5705, 0xBF810000);
        Assert.Equal(["VMulLoU32", "VCmpNeI32", "VCndmaskB32", "VRcpIflagF32", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(Gen5Operand.Scalar(4), program.Instructions[1].Destinations[0]);
        Assert.Equal(4u, Assert.IsType<Gen5Vop3Control>(program.Instructions[1].Control).ScalarDestination);
        Assert.True(Assert.IsType<Gen5Vop3Control>(program.Instructions[2].Control).Clamp);
        Assert.Equal(Gen5Operand.Scalar(4), program.Instructions[2].Sources[2]);
        Assert.Equal(28u, program.Instructions[4].Pc);
    }

    [Fact]
    public void BufferAddressNormalizationDoesNotTreatLegacyMemoryTypeAsAddressBits()
    {
        uint[] descriptor = [0x3943FFE0, 0x0010B001, 2, 0x00027204];
        var normalized = GcnBufferDescriptor.Normalize(descriptor);
        Assert.Equal(0x00100001u, normalized[1]);
        Assert.Equal(0x0010B001u, descriptor[1]);
    }

    [Theory]
    [InlineData(0x94800FACu)]
    [InlineData(0xF4800FACu)]
    [InlineData(uint.MaxValue)]
    public void UnboundBufferTypesDoNotBecomeSyntheticImageStores(uint flags)
    {
        uint[] descriptor = [0x0236CE39, 0x00A00000, 0x700FC03F, flags];
        var original = descriptor.ToArray();
        Assert.Equal(new uint[4], GcnBufferDescriptor.Normalize(descriptor));
        Assert.Equal(original, descriptor);

        var program = Decode(0xE00C0000, 0x80010400, 0xBF810000);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0xD530, 0, 8,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize);
        uint[] userData = [0, 0, 0, 0, .. descriptor];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(new uint[4], Assert.Single(snapshot.Buffers));
        var cache = new ResourceMaterializationCache();
        bool Resident(ulong address, Span<byte> destination, bool clean) => false;
        for (var index = 0; index < 2; index++)
        {
            Assert.True(cache.Materialize(plan, new ResourceRuntimeInputs { UserData = userData }, Resident,
                ref snapshot, ref specialization, out failure), failure.ToString());
            Assert.Equal(new uint[4], Assert.Single(snapshot.Buffers));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoweredImageProjectionsKeepTheirAddressPitchAndFormat(bool store)
    {
        var original = Decode(store ? 0xF0200F00u : 0xF0000F00u, 0x00020400, 0xBF810000);
        Assert.True(GcnStorageImageLowering.TryLower(original, out var program, out var error), error);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0xD531, 0, 16,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        uint[] userData = [0, 0, 0, 0, 0, 0, 0, 0,
            0x10, (4u << 20) | (7u << 26), 6u | (4u << 14),
            (9u << 28) | (8u << 20) | 0xFAC, 15u << 13, 0, 0, 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var buffer = BufferDescriptorWords.From(Assert.Single(snapshot.Buffers));
        Assert.Equal(0x1000UL, buffer.Address);
        Assert.Equal(16u * 5u * 4u, buffer.RecordCount);
        Assert.Equal(22u, buffer.Format);

        userData[9] = (44u << 20) | (7u << 26);
        if (store)
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                ResourceMaterializer.Materialize(plan, new ResourceRuntimeInputs { UserData = userData },
                    ref snapshot, ref specialization, out _));
            Assert.Contains("Unsupported GCN storage-image store", exception.Message);
        }
    }

    [Fact]
    public void LoweredBufferFetchRetainsTheRuntimeSplitFormatEncoding()
    {
        var original = Decode(0xE00C0000, 0x80010400, 0xBF810000);
        Assert.True(GcnStorageImageLowering.TryLower(original, out var lowered, out var error), error);
        Assert.False(Assert.IsType<Gen5BufferMemoryControl>(original.Instructions[0].Control).SplitDescriptorFormat);
        Assert.True(Assert.Single(lowered.Instructions.Select(instruction => instruction.Control)
            .OfType<Gen5BufferMemoryControl>()).SplitDescriptorFormat);
    }

    [Fact]
    public void TypedGcnFetchDecodesTheSeparateInstructionFormatFields()
    {
        var original = Decode(0xEBF32000, 0x80020C02, 0xBF810000);
        var source = Assert.IsType<Gen5BufferMemoryControl>(original.Instructions[0].Control);
        Assert.True(source.Typed);
        Assert.Equal((14u << 3) | 7u, source.TypedFormat);
        Assert.True(GcnStorageImageLowering.TryLower(original, out var lowered, out var error), error);
        var target = Assert.Single(lowered.Instructions.Select(instruction => instruction.Control)
            .OfType<Gen5BufferMemoryControl>());
        Assert.Equal(77u, target.TypedFormat);
    }

    [Fact]
    public void GcnTextureDescriptorUsesLegacyWidthFormatAndTileFields()
    {
        // 1024x512 RGBA8_UNORM, GNM linear-aligned tile index 8, pitch 1024.
        uint[] gcn = [0x00123456, 10u << 20, 1023u | (511u << 14),
            (9u << 28) | (8u << 20) | 0xFAC, 1023u << 13, 0, 0, 0];
        var original = gcn.ToArray();
        var agc = GcnImageDescriptor.Normalize(gcn, ShaderStage.Compute, 0x1234, "test-direct");
        var descriptor = new TextureDescriptorWords(agc);

        Assert.Equal(original, gcn);
        Assert.Equal(8, agc.Length);
        Assert.Equal(GuestPixelFormat.Bits8_8_8_8UNorm, descriptor.Format);
        Assert.Equal(1024u, descriptor.Width + 1);
        Assert.Equal(512u, descriptor.Height + 1);
        Assert.Equal(GuestTileMode.Linear, descriptor.TileMode);
        Assert.Equal(GuestImageType.Color2D, descriptor.Type);
        Assert.Equal(0u, descriptor.MaxMip);
    }

    [Theory]
    [InlineData(0u, "ImageLoad")]
    [InlineData(1u, "ImageLoadMip")]
    public void PixelImageLoadsUseTheSharedSampledImagePathWithoutLosingOperands(uint opcode, string name)
    {
        var original = Decode(0xF0000F00 | (opcode << 18), 0x00020400, 0xBF810000);
        Assert.True(GcnStorageImageLowering.TryLower(original, out var program, out var error, sampledLoads: true), error);
        var instruction = program.Instructions[0];
        Assert.Equal(name, instruction.Opcode);
        var image = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(8u, image.ScalarResource);
        Assert.Equal(4u, image.VectorData);
        Assert.Equal(0u, image.VectorAddress);
        Assert.Equal(15u, image.Dmask);
        Assert.Equal(original.Instructions.Select(instruction => instruction.Pc), program.Instructions.Select(instruction => instruction.Pc));

        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, 0x1239, 0, 16);
        Assert.Empty(plan.Info.Buffers);
        Assert.Equal(SharpEmu.ShaderCompiler.Resources.ImageResourceClass.Sampled, Assert.Single(plan.Info.Images).ResourceClass);
    }

    [Fact]
    public void GcnTextureDescriptorDoesNotMisinterpretTiledOrMipmappedMemoryAsAgc()
    {
        uint[] gcn = [0x00123456, 10u << 20, 1023u | (511u << 14),
            (9u << 28) | (14u << 20) | 0xFAC, 1023u << 13, 0, 0, 0];
        var original = gcn.ToArray();
        Assert.Equal(new uint[8], GcnImageDescriptor.Normalize(gcn, ShaderStage.Compute, 0x1235, "test-tiled"));
        gcn[3] = (9u << 28) | (8u << 20) | (1u << 16) | 0xFAC;
        Assert.Equal(new uint[8], GcnImageDescriptor.Normalize(gcn, ShaderStage.Compute, 0x1236, "test-mips"));
        Assert.Equal(original[0], gcn[0]);
        Assert.Equal(original[1], gcn[1]);
    }

    [Fact]
    public void GcnTile13OneDimensionalArrayDetilesEachSliceIntoTrackedLinearBacking()
    {
        const uint width = 1025, pitch = 1032, layers = 3;
        const ulong sourceAddress = 0x100000, scratchAddress = 0x200000;
        var guest = new TileImageMemory(sourceAddress, (int)(pitch * 8 * layers * 4), scratchAddress, 0x10000);
        var original = new uint[8]
        {
            (uint)(sourceAddress >> 8), (4u << 20) | (7u << 26), width - 1,
            (12u << 28) | (13u << 20) | 0xFAC,
            ((pitch - 1) << 13) | (layers - 1), (layers - 1) << 13, 0, 0,
        };
        for (var layer = 0u; layer < layers; layer++)
        for (var x = 0u; x < width; x++)
        {
            var m = x & 7;
            var morton = (m & 1) | ((m & 2) << 1) | ((m & 4) << 2);
            var offset = (int)((layer * pitch * 8 + (x >> 3) * 64 + morton) * 4);
            BinaryPrimitives.WriteUInt32LittleEndian(guest.Source.AsSpan(offset), layer * 100000 + x);
        }

        var allocations = 0;
        var frontend = new GcnShaderFrontend(guest, size =>
        {
            allocations++;
            Assert.Equal(0x4000UL, size);
            return scratchAddress + (ulong)(allocations - 1) * 0x4000;
        });
        var normalizer = frontend.NormalizeImageDescriptor;
        var first = normalizer(original, ShaderStage.Pixel, 0xB100DB, "test-tile13");
        var descriptor = new TextureDescriptorWords(first);
        Assert.Equal(sourceAddress >> 8, original[0]);
        Assert.Equal(scratchAddress, descriptor.BaseAddress);
        Assert.Equal(width, descriptor.Width + 1);
        Assert.Equal(layers - 1, descriptor.Depth);
        Assert.Equal(GuestImageType.Color1DArray, descriptor.Type);
        Assert.Equal(GuestPixelFormat.Bits32Float, descriptor.Format);
        Assert.Equal(GuestTileMode.Linear, descriptor.TileMode);
        Assert.Equal(1, allocations);

        for (var layer = 0u; layer < layers; layer++)
        for (var x = 0u; x < width; x++)
        {
            var offset = (int)((layer * 1088 + x) * 4);
            Assert.Equal(layer * 100000 + x,
                BinaryPrimitives.ReadUInt32LittleEndian(guest.Scratch.AsSpan(offset)));
        }

        // The source descriptor stays unchanged; a changed texel needs a new snapshot.
        BinaryPrimitives.WriteUInt32LittleEndian(guest.Source.AsSpan(16 * 4), 0x12345678);
        Assert.Equal(0, GnmDriverExports.SubmitDone(new CpuContext(guest, Generation.Gen4)));
        var second = normalizer(original, ShaderStage.Pixel, 0xB100DB, "test-tile13");
        Assert.NotEqual(first, second);
        Assert.Equal(2, allocations);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(guest.Scratch.AsSpan(4 * 4)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(guest.Scratch.AsSpan(0x4000 + 4 * 4)));
        Assert.Equal(second, normalizer(original, ShaderStage.Pixel, 0xB100DB, "test-tile13"));
        Assert.Equal(2, allocations);
    }

    [Fact]
    public void VertexFetchTargetsBelongToEachDrawRatherThanTheSharedShaderAddress()
    {
        var binary = Binary(0xBE802100, 0xBF810000);
        var memory = new Bytes(new byte[0x200]);
        binary.Data.CopyTo(memory.Data, 0);
        memory.Write(0x100, 0x7E0802F2); // v4 = 1.0
        memory.Write(0x104, 0xBE802000);
        memory.Write(0x180, 0x7E0802F2);
        memory.Write(0x184, 0x7E1002F2); // second layout also supplies v8
        memory.Write(0x188, 0xBE802000);
        var frontend = new GcnShaderFrontend();
        var registry = new ShaderHeaderRegistry(new CpuContext(memory, Generation.Gen4), _ => 0, _ => null, frontend);
        uint[] firstData = [unchecked((uint)(Address + 0x100)), (uint)(Address >> 32)];
        uint[] secondData = [unchecked((uint)(Address + 0x180)), (uint)(Address >> 32)];
        var first = registry.Require(Address, "vertex", firstData);
        var second = registry.Require(Address, "vertex", secondData);
        var again = registry.Require(Address, "vertex", firstData);
        Assert.Equal(Address + 0x100, first.ContinuationAddress);
        Assert.Equal(8u, first.ContinuationSizeBytes);
        Assert.Equal(Address + 0x180, second.ContinuationAddress);
        Assert.Equal(12u, second.ContinuationSizeBytes);
        Assert.Equal(first.FrontendUserData.ToArray(), again.FrontendUserData.ToArray());
        Assert.Equal(first with { FrontendUserData = default }, again with { FrontendUserData = default });
        Assert.True(frontend.TryDecode(new CpuContext(memory, Generation.Gen4), second, out var linked, out var error), error);
        Assert.Contains(linked.Instructions, i => i.Opcode == "VMovB32" && i.Destinations.Contains(Gen5Operand.Vector(8)));
        var hash = ShaderIdentity.ComputeContent(memory, second.CodeRanges, "vertex");
        memory.Write(0x184, 0x7E100280); // same pointer and size, a new fetch implementation
        Assert.NotEqual(hash, ShaderIdentity.ComputeContent(memory, second.CodeRanges, "vertex"));
    }

    [Fact]
    public void GcnTile13MipmappedVolumeRemainsUnbound()
    {
        var guest = new TileImageMemory(0x100000, 0x10000, 0x200000, 0x10000);
        var frontend = new GcnShaderFrontend(guest, _ => throw new InvalidOperationException("Unexpected detile allocation"));
        uint[] descriptor = [0x1000, (4u << 20) | (7u << 26), 1024,
            (10u << 28) | (13u << 20) | (1u << 16) | 0xFAC, (1031u << 13), 0, 0, 0];
        Assert.Equal(new uint[8], frontend.NormalizeImageDescriptor(descriptor, ShaderStage.Pixel, 0xBB, "test-3d"));
    }

    [Theory]
    [InlineData(16u, 16u, 16u, true, false)]
    [InlineData(16u, 16u, 16u, true, true)]
    [InlineData(9u, 11u, 5u, false, false)]
    [InlineData(9u, 11u, 5u, true, true)]
    public void GcnThickVolumeLutPreservesMicrotileBitsAndThreeDimensionalSampling(
        uint width, uint height, uint depth, bool pow2, bool deferred)
    {
        const ulong source = 0x100000, scratch = 0x200000;
        var memory = new TileImageMemory(source, 0x4000, scratch, 0x10000);
        for (var word = 0u; word < memory.Source.Length / 4; word++)
            BinaryPrimitives.WriteUInt32LittleEndian(memory.Source.AsSpan((int)word * 4), 0xFF000000u | word);
        var frontend = new GcnShaderFrontend(memory, _ => scratch);
        uint[] descriptor = [(uint)(source >> 8), 10u << 20, (width - 1) | ((height - 1) << 14),
            (10u << 28) | (19u << 20) | (pow2 ? 1u << 25 : 0) | 0xFAC,
            ((width - 1) << 13) | (depth - 1), 0, 0, 0];
        var normalized = deferred
            ? frontend.NormalizeImageForResolution(descriptor, ShaderStage.Pixel, 0xD520, "test-volume-lut", true)
            : frontend.NormalizeImageDescriptor(descriptor, ShaderStage.Pixel, 0xD520, "test-volume-lut");
        Assert.True(frontend.PrepareTexture(normalized));
        var translated = new TextureDescriptorWords(normalized);
        Assert.Equal(scratch, translated.BaseAddress);
        Assert.Equal(GuestImageType.Color3D, translated.Type);
        Assert.Equal(depth - 1, translated.Depth);
        Assert.Equal(GuestTileMode.Linear, translated.TileMode);
        var shape = new ShaderImageShape(true, false, false, false, false, TextureNumericClass.Float);
        var sampled = frontend.ResolveTexture(normalized, shape, 0xD520, 0);
        var description = sampled.Request.Description;
        Assert.True(description.IsVolume);
        Assert.Equal(depth, description.Extent.Depth);
        Assert.Equal(1u, description.Resources.Layers);
        Assert.Equal((ulong)description.Pitch * height * depth * 4, description.Data.Size);
        (uint X, uint Y, uint Z, uint SourceWord)[] points =
        [
            (0, 0, 0, 0), (1, 0, 0, 1), (0, 1, 0, 2), (2, 0, 0, 4),
            (0, 0, 1, 8), (0, 2, 0, 16), (0, 0, 2, 32), (4, 0, 0, 64),
            (0, 4, 0, 128), (7, 7, 3, 255), (8, 0, 0, 256), (0, 8, 0, 512),
            (8, 8, 0, 768), (0, 0, 4, 1024), (8, 8, 4, 1792), (15, 15, 15, 4095),
        ];
        foreach (var point in points)
        {
            if (point.X >= width || point.Y >= height || point.Z >= depth)
                continue;
            var offset = (int)(((point.Z * height + point.Y) * description.Pitch + point.X) * 4);
            Assert.Equal(0xFF000000u | point.SourceWord,
                BinaryPrimitives.ReadUInt32LittleEndian(memory.Scratch.AsSpan(offset)));
        }
    }

    [Theory]
    [InlineData(35u, 0u, GuestPixelFormat.Bc1UNorm, 8)]
    [InlineData(35u, 9u, GuestPixelFormat.Bc1Srgb, 8)]
    [InlineData(36u, 0u, GuestPixelFormat.Bc2UNorm, 16)]
    [InlineData(37u, 9u, GuestPixelFormat.Bc3Srgb, 16)]
    [InlineData(38u, 1u, GuestPixelFormat.Bc4SNorm, 8)]
    [InlineData(39u, 0u, GuestPixelFormat.Bc5UNorm, 16)]
    [InlineData(40u, 0u, GuestPixelFormat.Bc6UFloat, 16)]
    [InlineData(41u, 0u, GuestPixelFormat.Bc7UNorm, 16)]
    [InlineData(41u, 9u, GuestPixelFormat.Bc7Srgb, 16)]
    public void GcnCompressedMicrotilesPreserveBlocksRowsAndArraySlices(uint data, uint number, GuestPixelFormat format, int bytes)
    {
        const uint width = 68, height = 36, pitch = 96, layers = 2;
        const ulong source = 0x100000, scratch = 0x200000;
        var memory = new TileImageMemory(source, 24 * 16 * (int)layers * bytes, scratch, 0x10000);
        int[] spread = [0, 1, 4, 5, 16, 17, 20, 21];
        for (var layer = 0; layer < layers; layer++)
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 17; x++)
        {
            var tile = (y / 8 * 3 + x / 8) * 64;
            var offset = (layer * 24 * 16 + tile + spread[x % 8] + 2 * spread[y % 8]) * bytes;
            memory.Source.AsSpan(offset, bytes).Fill((byte)(layer * 100 + y * 17 + x));
        }
        var frontend = new GcnShaderFrontend(memory, _ => scratch);
        uint[] descriptor = [(uint)(source >> 8), (data << 20) | (number << 26),
            (width - 1) | ((height - 1) << 14), (13u << 28) | (13u << 20) | 0xFAC,
            (pitch - 1) << 13, (layers - 1) << 13, 0, 0];
        var translated = new TextureDescriptorWords(frontend.NormalizeImageDescriptor(descriptor, ShaderStage.Pixel, data, "test-bc"));
        Assert.Equal(format, translated.Format);
        Assert.Equal(scratch, translated.BaseAddress);
        Assert.Equal(width, translated.Width + 1);
        Assert.Equal(height, translated.Height + 1);
        Assert.Equal(layers - 1, translated.Depth);
        Assert.Equal(GuestImageType.Color2DArray, translated.Type);
        for (var layer = 0; layer < layers; layer++)
        for (var y = 0; y < 9; y++)
        for (var x = 0; x < 17; x++)
        {
            var offset = ((layer * 9 + y) * 32 + x) * bytes;
            Assert.All(memory.Scratch.AsSpan(offset, bytes).ToArray(), value => Assert.Equal((byte)(layer * 100 + y * 17 + x), value));
        }
    }

    [Theory]
    [InlineData(11u, false, 5u)]
    [InlineData(11u, true, 5u)]
    [InlineData(11u, true, 35u)]
    [InlineData(13u, false, 2u)]
    [InlineData(13u, true, 2u)]
    public void GcnLayeredMipmapsConvertMipOrderedMicrotilesIntoLinearSliceChains(uint type, bool pow2, uint lastArray)
    {
        const uint width = 32, levels = 3, blockBytes = 16;
        const ulong source = 0x500000, scratch = 0x600000;
        var layers = type == 11 ? 6u : 3u;
        var sourceLayers = pow2 ? System.Numerics.BitOperations.RoundUpToPowerOf2(layers) : layers;
        var memory = new TileImageMemory(source, (int)(levels * sourceLayers * 1024), scratch, 0x10000);
        int[] spread = [0, 1, 4, 5, 16, 17, 20, 21];
        for (var level = 0u; level < levels; level++)
        for (var layer = 0u; layer < layers; layer++)
        for (var y = 0; y < 8 >> (int)level; y++)
        for (var x = 0; x < 8 >> (int)level; x++)
        {
            var offset = (int)((level * sourceLayers + layer) * 1024) + (spread[x] + 2 * spread[y]) * (int)blockBytes;
            memory.Source.AsSpan(offset, (int)blockBytes).Fill((byte)(1 + layer * 32 + level * 8 + x + y));
        }
        var frontend = new GcnShaderFrontend(memory, _ => scratch);
        uint[] descriptor = [(uint)(source >> 8), 40u << 20, (width - 1) | ((width - 1) << 14),
            (type << 28) | (13u << 20) | (pow2 ? 1u << 25 : 0) | ((levels - 1) << 16) | (1u << 12) | 0xFAC,
            ((width - 1) << 13) | (type == 11 ? 0 : layers - 1), lastArray << 13, 0, 0];
        var normalized = frontend.NormalizeImageDescriptor(descriptor, ShaderStage.Pixel, 0xBC600, "test-layered-mips");
        var translated = new TextureDescriptorWords(normalized);
        Assert.Equal(scratch, translated.BaseAddress);
        Assert.Equal(GuestPixelFormat.Bc6UFloat, translated.Format);
        Assert.Equal(levels - 1, translated.MaxMip);
        Assert.Equal(1u, translated.BaseLevel);
        Assert.Equal(layers - 1, translated.Depth);
        var shape = new ShaderImageShape(false, true, type == 11, false, false, TextureNumericClass.Float);
        var sampled = frontend.ResolveTexture(normalized, shape, 0xBC600, 0);
        Assert.Equal(layers, sampled.Request.Description.Resources.Layers);
        Assert.Equal(levels, sampled.Request.Description.Resources.Levels);
        var sliceStride = sampled.Request.Description.Data.Size / layers;
        for (var level = 0u; level < levels; level++)
        for (var layer = 0u; layer < layers; layer++)
        for (var y = 0u; y < 8 >> (int)level; y++)
        for (var x = 0u; x < 8 >> (int)level; x++)
        {
            var mip = sampled.Request.Description.MipLayout[(int)level];
            var offset = layer * sliceStride + mip.Offset + (y * (mip.Pitch / 4) + x) * blockBytes;
            Assert.All(memory.Scratch.AsSpan((int)offset, (int)blockBytes).ToArray(),
                value => Assert.Equal((byte)(1 + layer * 32 + level * 8 + x + y), value));
        }
    }

    [Fact]
    public void ResourceMaterializationConvertsGcnImagesButPreservesNativeAgcImages()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Image(0, "ImageLoad", 0, dmask: 1),
            ResourceTestProgram.EndProgram(8));
        uint[] gcn = [0x1000, 10u << 20, 63u | (63u << 14),
            (9u << 28) | (8u << 20) | 0xFAC, 63u << 13, 0, 0, 0];
        var expected = GcnImageDescriptor.Normalize(gcn, ShaderStage.Compute, 0x1237, "test");

        var gcnPlan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0x1237, 0, 8,
            normalizeImageDescriptor: GcnImageDescriptor.Normalize);
        var gcnSnapshot = new ResourceSnapshot();
        var gcnSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(gcnPlan, new ResourceRuntimeInputs { UserData = gcn },
            ref gcnSnapshot, ref gcnSpecialization, out var gcnFailure), gcnFailure.ToString());
        Assert.Equal(expected, Assert.Single(gcnSnapshot.Images));

        // PS5 does not register the GCN hook; it must retain its descriptor unchanged.
        var agcPlan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 0x1238, 0, 8);
        var agcSnapshot = new ResourceSnapshot();
        var agcSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(agcPlan, new ResourceRuntimeInputs { UserData = expected },
            ref agcSnapshot, ref agcSpecialization, out var agcFailure), agcFailure.ToString());
        Assert.Equal(expected, Assert.Single(agcSnapshot.Images));
        Assert.Equal(10u << 20, gcn[1]);
    }

    [Fact]
    public void RawGcnBufferKernelReachesTheExistingResourcePlannerAndSpirvBackend()
    {
        var program = Decode(0x7E0802FF, 42, 0xE0700000, 0x80010400, 0xBF810000);
        Assert.Equal("BufferStoreDword", program.Instructions[1].Opcode);
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program);
        Assert.Single(plan.Info.Buffers);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Theory]
    [InlineData(0xE030812Cu, "GlobalLoadDword", 1u)]
    [InlineData(0xE038812Cu, "GlobalLoadDwordx4", 4u)]
    [InlineData(0xE070812Cu, "GlobalStoreDword", 1u)]
    [InlineData(0xE078812Cu, "GlobalStoreDwordx4", 4u)]
    public void Address64BufferAccessesReachTheGlobalMemoryBackend(uint word, string opcode, uint count)
    {
        var original = Decode(word, 0x0801080C, 0xBF810000);
        Assert.Contains(Gen5Operand.Vector(13), original.Instructions[0].Sources);
        Assert.True(GcnStorageImageLowering.TryLower(original, out var program, out var error), error);
        var memory = Assert.Single(program.Instructions, instruction => instruction.Control is Gen5GlobalMemoryControl);
        Assert.Equal(opcode, memory.Opcode);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(memory.Control);
        Assert.Equal(count, control.DwordCount);
        Assert.Equal(300, control.OffsetBytes);
        Assert.True(control.ScalarAddress >= 125);
        Assert.False(control.UsesFlatAddress);
        Assert.DoesNotContain(program.Instructions, instruction => instruction.Control is Gen5BufferMemoryControl);
        Assert.DoesNotContain(program.Instructions, instruction =>
            instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.ScalarRegister &&
                destination.Value is 106 or 107 or 126 or 127));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Theory]
    [InlineData(0xE030912Cu, 0x0801080Cu)]
    [InlineData(0xE030A12Cu, 0x0801080Cu)]
    [InlineData(0xE031812Cu, 0x0801080Cu)]
    [InlineData(0xE030812Cu, 0x080108FFu)]
    public void InvalidAddress64BufferModesAndRegisterPairsRemainRejected(uint word, uint extra)
    {
        var memory = Words(word, extra, 0xBF810000);
        Assert.False(GcnShaderDecoder.TryDecode(memory, Address, 12, out _, out var error));
        Assert.Contains("unsupported-gcn-", error);
    }

    [Fact]
    public void ImageNormalizationIsRefreshedAfterAGpuTargetBecomesAvailable()
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Image(0, "ImageLoad", 0, dmask: 1), ResourceTestProgram.EndProgram(8));
        var ready = false;
        var conversions = 0;
        uint[] Convert(ReadOnlySpan<uint> words, ShaderStage stage, ulong hash, string source)
        {
            conversions++;
            return ready ? words.ToArray() : new uint[8];
        }
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1240, 0, 8,
            normalizeImageDescriptor: Convert);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var cache = new ResourceMaterializationCache();
        uint[] descriptor = [0x1000, (22u << 20) | (3u << 30), 15u | (63u << 14),
            (9u << 28) | 0xFAC, 0, 0, 0, 0];
        var inputs = new ResourceRuntimeInputs { UserData = descriptor };
        bool Resident(ulong address, Span<byte> destination, bool clean) => false;
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(new uint[8], Assert.Single(snapshot.Images));
        var firstConversions = conversions;
        ready = true;
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(descriptor, Assert.Single(snapshot.Images));
        Assert.True(conversions > firstConversions);
        Assert.Equal(0, cache.Hits);
    }

    [Theory]
    [InlineData(4u, 4u, 20u)]
    [InlineData(4u, 7u, 22u)]
    [InlineData(14u, 7u, 77u)]
    [InlineData(0u, 1u, 0u)]
    [InlineData(0u, 7u, 0u)]
    public void BufferFormatsAreNormalizedWithoutChangingAddressStrideSwizzleOrGuestWords(uint data, uint number, uint expected)
    {
        uint[] words = [0x12345678, 0x00100001, 128, 0x00A00000 | (data << 15) | (number << 12) | 0xFAC];
        var original = words.ToArray();
        var result = GcnBufferDescriptor.Normalize(words);
        Assert.Equal(original, words);
        Assert.Equal(words[..3], result[..3]);
        Assert.Equal(expected, (result[3] >> 12) & 127);
        Assert.Equal(words[3] & ~(127u << 12), result[3] & ~(127u << 12));
    }

    [Fact]
    public void LegacyBufferFormatWithoutAnExactEquivalentIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => GcnBufferDescriptor.Normalize([0, 0, 4, (1u << 15) | (6u << 12)]));
        Assert.Throws<InvalidDataException>(() => GcnBufferDescriptor.Normalize([0, 0x80000000, 4, 0]));
    }

    [Fact]
    public void FormattedBufferMaterializationAndCacheUseNormalizedFormatsButPreserveRawUserData()
    {
        var program = Decode(0xE00C0000, 0x80010400, 0xBF810000);
        Assert.Equal(4u, Assert.IsType<Gen5BufferMemoryControl>(program.Instructions[0].Control).DwordCount);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1234, 0, 8,
            normalizeBufferDescriptor: GcnBufferDescriptor.Normalize);
        uint[] userData = [0, 0, 0, 0, 0x1000, 16u << 16, 32, (14u << 15) | (7u << 12) | 0xFAC];
        var original = userData.ToArray();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var cache = new ResourceMaterializationCache();
        bool Resident(ulong address, Span<byte> destination, bool clean) => false;
        for (var index = 0; index < 3; index++)
        {
            var inputs = new ResourceRuntimeInputs { UserData = userData };
            Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out var failure), failure.ToString());
            Assert.Equal(77u, Assert.Single(specialization.Buffers).DescriptorFormat);
            Assert.Equal(77u, (Assert.Single(snapshot.Buffers)[3] >> 12) & 127);
            Assert.Equal(original, snapshot.UserData);
        }
        Assert.True(cache.Hits > 0);
        Assert.Equal(original, userData);
        userData[7] = (14u << 15) | (4u << 12) | 0xFAC;
        Assert.True(cache.Materialize(plan, new ResourceRuntimeInputs { UserData = userData }, Resident,
            ref snapshot, ref specialization, out _));
        Assert.Equal(75u, Assert.Single(specialization.Buffers).DescriptorFormat);
        Assert.Equal(119u, (original[7] >> 12) & 127);
    }

    private static Gen5ShaderProgram Decode(params uint[] words)
    {
        Assert.True(GcnShaderDecoder.TryDecode(Words(words), Address, (uint)words.Length * 4, out var program, out var error), error);
        return program;
    }

    private static Bytes Binary(params uint[] code)
    {
        var size = 8 + code.Length * 4;
        var info = (size + 16 + 7) & ~7;
        var memory = new Bytes(new byte[info + 28]);
        memory.Write(0, 0xBEEB03FF);
        memory.Write(4, (uint)info / 8 - 1);
        for (var index = 0; index < code.Length; index++) memory.Write(8 + index * 4, code[index]);
        "OrbShdr"u8.CopyTo(memory.Data.AsSpan(info));
        memory.Data[info + 7] = 7;
        memory.Write(info + 8, ((uint)size << 8) | 0x11);
        return memory;
    }

    private static Bytes Words(params uint[] words)
    {
        var memory = new Bytes(new byte[words.Length * 4]);
        for (var i = 0; i < words.Length; i++) memory.Write(i * 4, words[i]);
        return memory;
    }

    private sealed class Bytes(byte[] data) : ICpuMemory
    {
        public byte[] Data => data;
        public void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < Address || address - Address > (ulong)data.Length ||
                (ulong)destination.Length > (ulong)data.Length - (address - Address)) return false;
            data.AsSpan((int)(address - Address), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    private sealed class TileImageMemory(ulong sourceAddress, int sourceLength, ulong scratchAddress, int scratchLength) : ICpuMemory
    {
        public byte[] Source { get; } = new byte[sourceLength];
        public byte[] Scratch { get; } = new byte[scratchLength];
        public int SourceReads;
        public int Writes;

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address >= sourceAddress && address < sourceAddress + (ulong)sourceLength)
                Interlocked.Increment(ref SourceReads);
            return Copy(address, destination, Source, sourceAddress) || Copy(address, destination, Scratch, scratchAddress);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            Interlocked.Increment(ref Writes);
            return Write(address, data, Source, sourceAddress) || Write(address, data, Scratch, scratchAddress);
        }

        private static bool Copy(ulong address, Span<byte> destination, byte[] region, ulong start)
        {
            if (address < start || address - start > (ulong)region.Length ||
                (ulong)destination.Length > (ulong)region.Length - (address - start)) return false;
            region.AsSpan((int)(address - start), destination.Length).CopyTo(destination);
            return true;
        }

        private static bool Write(ulong address, ReadOnlySpan<byte> data, byte[] region, ulong start)
        {
            if (address < start || address - start > (ulong)region.Length ||
                (ulong)data.Length > (ulong)region.Length - (address - start)) return false;
            data.CopyTo(region.AsSpan((int)(address - start)));
            return true;
        }
    }
}
