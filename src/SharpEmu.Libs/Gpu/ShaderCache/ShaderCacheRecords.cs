// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal sealed record VertexStageInputs(
    bool FetchEmbedded,
    int FetchAttributeRegister,
    int FetchBufferRegister,
    IReadOnlyList<ShaderVertexInput> Inputs,
    uint ScratchDwords,
    int RequiredVertexOutputCount,
    uint PositionExportControl,
    ShaderClipSpaceTransform ClipSpace);

internal sealed record PixelStageInputs(
    uint ScratchDwords,
    IReadOnlyList<Gen5PixelOutputBinding> Outputs,
    uint InputEnable,
    uint CustomInterpolationMask,
    uint InputAddress,
    IReadOnlyList<uint> InputCntl);

internal sealed class StageRecord
{
    public required ShaderStage Stage { get; init; }
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required uint UserDataBase { get; init; }
    public required uint UserDataCount { get; init; }
    public required uint PushDataCursor { get; init; }
    public ResourceSpecialization? Specialization { get; init; }
    public ComputeInputInfo? Compute { get; init; }
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; init; }
    public VertexStageInputs? Vertex { get; init; }
    public PixelStageInputs? Pixel { get; init; }

    public ShaderCodeKey CodeKey => new(Hash, CodeSize, Address);

    public StageRecord WithPushDataCursor(uint cursor) => With(Address, cursor);

    public StageRecord WithAddress(ulong address) => With(address, PushDataCursor);

    private StageRecord With(ulong address, uint cursor) => new()
    {
        Stage = Stage,
        Hash = Hash,
        CodeSize = CodeSize,
        Address = address,
        UserDataBase = UserDataBase,
        UserDataCount = UserDataCount,
        PushDataCursor = cursor,
        Specialization = Specialization,
        Compute = Compute,
        ComputeSystemRegisters = ComputeSystemRegisters,
        Vertex = Vertex,
        Pixel = Pixel,
    };
}

internal enum InventorySource : byte
{
    Created = 1,
    Scanned = 2,
}

internal sealed class InventoryProgram
{
    public required ShaderCodeKey Code { get; init; }
    public required InventorySource Source { get; init; }
    public required byte[] Header { get; init; }
}

internal readonly record struct ScannedFile(string Path, long Size, long WriteTicks, int Programs, int Scanner);

internal readonly record struct VertexAttributeFormat(BufferDescriptorWords Descriptor, int RegisterCount);

internal sealed class GraphicsPipelineRecord
{
    public required StageRecord Vertex { get; init; }
    public StageRecord? Pixel { get; init; }
    public required PipelineRenderingState Rendering { get; init; }
    public required PipelineVertexInputState VertexInput { get; init; }
    public required PipelineStaticParameters StaticParameters { get; init; }
    public required VertexAttributeFormat[] Attributes { get; init; }

    public GraphicsPipelineRecord WithoutAddresses() => new()
    {
        Vertex = Vertex.WithAddress(0),
        Pixel = Pixel?.WithAddress(0),
        Rendering = Rendering,
        VertexInput = VertexInput,
        StaticParameters = StaticParameters,
        Attributes = Attributes,
    };
}

internal readonly record struct PipelineBinaryPart(byte[] Key, byte[] Data);

internal readonly record struct LayoutBindingRecord(uint Binding, int DescriptorType, uint DescriptorCount, uint StageFlags);

internal sealed class CompiledStageRecord
{
    public required byte[] Spirv { get; init; }
    public required LayoutBindingRecord[] Bindings { get; init; }
    public required byte[] VertexFetchComponents { get; init; }
    public uint PushDataEnd { get; init; }
}

internal static class ShaderCacheSerializer
{
    public static byte[] Serialize(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    public static T Deserialize<T>(byte[] payload, Func<BinaryReader, T> read, bool allowTrailing = false)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var value = read(reader);
        if (!allowTrailing && reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new ArgumentException("The shader cache record has trailing bytes.");
        }

        return value;
    }

    public static void WriteCode(BinaryWriter writer, ShaderCodeCapture code)
    {
        writer.Write(code.Hash);
        writer.Write(code.CodeSize);
        writer.Write(code.Address);
        writer.Write((int)code.Generation);
        writer.Write(code.Fused is not null);
        if (code.Fused is { } fused)
        {
            writer.Write(fused.EntryHeaderAddress);
            writer.Write(fused.ContinuationAddress);
            writer.Write(fused.ContinuationHeaderAddress);
        }

        writer.Write(code.Ranges.Length);
        foreach (var range in code.Ranges)
        {
            writer.Write(range.Address);
            writer.Write(range.Bytes.Length);
            writer.Write(range.Bytes);
        }
    }

    public static ShaderCodeCapture ReadCode(BinaryReader reader)
    {
        var hash = reader.ReadUInt64();
        var codeSize = reader.ReadUInt32();
        var address = reader.ReadUInt64();
        var generation = (Generation)reader.ReadInt32();
        FusedCodeParts? fused = reader.ReadBoolean()
            ? new FusedCodeParts(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64())
            : null;
        var ranges = new CodeRange[Count(reader)];
        for (var index = 0; index < ranges.Length; index++)
        {
            var rangeAddress = reader.ReadUInt64();
            ranges[index] = new CodeRange(rangeAddress, reader.ReadBytes(Count(reader)));
        }

        return new ShaderCodeCapture
        {
            Hash = hash,
            CodeSize = codeSize,
            Address = address,
            Generation = generation,
            Fused = fused,
            Ranges = ranges,
        };
    }

    public static void WriteStage(BinaryWriter writer, StageRecord record)
    {
        writer.Write((byte)record.Stage);
        writer.Write(record.Hash);
        writer.Write(record.CodeSize);
        writer.Write(record.Address);
        writer.Write(record.UserDataBase);
        writer.Write(record.UserDataCount);
        writer.Write(record.PushDataCursor);
        writer.Write(record.Specialization is not null);
        if (record.Specialization is { } specialization)
        {
            WriteSpecialization(writer, specialization);
        }

        switch (record.Stage)
        {
            case ShaderStage.Compute:
                WriteCompute(writer, record.Compute ?? throw new ArgumentException("A compute record has no compute inputs."),
                    record.ComputeSystemRegisters);
                break;
            case ShaderStage.Vertex:
                WriteVertex(writer, record.Vertex ?? throw new ArgumentException("A vertex record has no vertex inputs."));
                break;
            case ShaderStage.Pixel:
                WritePixel(writer, record.Pixel ?? throw new ArgumentException("A pixel record has no pixel inputs."));
                break;
            default:
                throw new ArgumentException($"The shader cache cannot record stage {record.Stage}.");
        }
    }

    public static StageRecord ReadStage(BinaryReader reader)
    {
        var stage = (ShaderStage)reader.ReadByte();
        var hash = reader.ReadUInt64();
        var codeSize = reader.ReadUInt32();
        var address = reader.ReadUInt64();
        var userDataBase = reader.ReadUInt32();
        var userDataCount = reader.ReadUInt32();
        var pushDataCursor = reader.ReadUInt32();
        var specialization = reader.ReadBoolean() ? ReadSpecialization(reader) : null;
        ComputeInputInfo? compute = null;
        Gen5ComputeSystemRegisters? registers = null;
        VertexStageInputs? vertex = null;
        PixelStageInputs? pixel = null;
        switch (stage)
        {
            case ShaderStage.Compute:
                (compute, registers) = ReadCompute(reader);
                break;
            case ShaderStage.Vertex:
                vertex = ReadVertex(reader);
                break;
            case ShaderStage.Pixel:
                pixel = ReadPixel(reader);
                break;
            default:
                throw new ArgumentException($"The shader cache record names an unknown stage {stage}.");
        }

        return new StageRecord
        {
            Stage = stage,
            Hash = hash,
            CodeSize = codeSize,
            Address = address,
            UserDataBase = userDataBase,
            UserDataCount = userDataCount,
            PushDataCursor = pushDataCursor,
            Specialization = specialization,
            Compute = compute,
            ComputeSystemRegisters = registers,
            Vertex = vertex,
            Pixel = pixel,
        };
    }

    public static StageRecord ReadLegacyCompute(BinaryReader reader)
    {
        var hash = reader.ReadUInt64();
        var codeSize = reader.ReadUInt32();
        var address = reader.ReadUInt64();
        var userDataBase = reader.ReadUInt32();
        var userDataCount = reader.ReadUInt32();
        var pushDataCursor = reader.ReadUInt32();
        var (compute, registers) = ReadCompute(reader);
        return new StageRecord
        {
            Stage = ShaderStage.Compute,
            Hash = hash,
            CodeSize = codeSize,
            Address = address,
            UserDataBase = userDataBase,
            UserDataCount = userDataCount,
            PushDataCursor = pushDataCursor,
            Specialization = ReadSpecialization(reader),
            Compute = compute,
            ComputeSystemRegisters = registers,
        };
    }

    public static void WriteProgram(BinaryWriter writer, InventoryProgram program)
    {
        writer.Write(program.Code.Hash);
        writer.Write(program.Code.CodeSize);
        writer.Write(program.Code.Address);
        writer.Write((byte)program.Source);
        writer.Write(program.Header.Length);
        writer.Write(program.Header);
    }

    public static InventoryProgram ReadProgram(BinaryReader reader)
    {
        var code = new ShaderCodeKey(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt64());
        var source = (InventorySource)reader.ReadByte();
        return new InventoryProgram { Code = code, Source = source, Header = reader.ReadBytes(Count(reader)) };
    }

    public static void WriteScannedFile(BinaryWriter writer, ScannedFile file)
    {
        writer.Write(file.Path);
        writer.Write(file.Size);
        writer.Write(file.WriteTicks);
        writer.Write(file.Programs);
        writer.Write(file.Scanner);
    }

    public static ScannedFile ReadScannedFile(BinaryReader reader) =>
        new(reader.ReadString(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32());

    public static void WriteGraphics(BinaryWriter writer, GraphicsPipelineRecord record)
    {
        WriteStage(writer, record.Vertex);
        writer.Write(record.Pixel is not null);
        if (record.Pixel is { } pixel)
        {
            WriteStage(writer, pixel);
        }

        var rendering = record.Rendering;
        writer.Write(rendering.ColorCount);
        foreach (var format in rendering.ColorFormats)
        {
            writer.Write((int)format);
        }

        writer.Write((int)rendering.DepthFormat);
        writer.Write((int)rendering.StencilFormat);

        var vertexInput = record.VertexInput;
        writer.Write(vertexInput.BindingCount);
        writer.Write(vertexInput.AttributeCount);
        for (var index = 0; index < vertexInput.BindingCount; index++)
        {
            writer.Write(vertexInput.Bindings[index].Stride);
            writer.Write(vertexInput.Bindings[index].Instance);
        }

        for (var index = 0; index < vertexInput.AttributeCount; index++)
        {
            writer.Write(vertexInput.Attributes[index].Offset);
            writer.Write(vertexInput.Attributes[index].Binding);
        }

        writer.Write(record.StaticParameters.Bytes);
        writer.Write(record.Attributes.Length);
        foreach (var attribute in record.Attributes)
        {
            writer.Write(attribute.Descriptor.Word0);
            writer.Write(attribute.Descriptor.Word1);
            writer.Write(attribute.Descriptor.Word2);
            writer.Write(attribute.Descriptor.Word3);
            writer.Write(attribute.RegisterCount);
        }
    }

    public static GraphicsPipelineRecord ReadGraphics(BinaryReader reader)
    {
        var vertex = ReadStage(reader);
        var pixel = reader.ReadBoolean() ? ReadStage(reader) : null;
        if (vertex.Stage != ShaderStage.Vertex || pixel is { Stage: not ShaderStage.Pixel })
        {
            throw new ArgumentException("The graphics record pairs the wrong stages.");
        }

        var rendering = new PipelineRenderingState { ColorCount = reader.ReadUInt32() };
        for (var index = 0; index < rendering.ColorFormats.Length; index++)
        {
            rendering.ColorFormats[index] = (Format)reader.ReadInt32();
        }

        rendering.DepthFormat = (Format)reader.ReadInt32();
        rendering.StencilFormat = (Format)reader.ReadInt32();

        var vertexInput = new PipelineVertexInputState
        {
            BindingCount = reader.ReadByte(),
            AttributeCount = reader.ReadByte(),
        };
        if (vertexInput.BindingCount > VertexInputInfo.MaxBuffers || vertexInput.AttributeCount > VertexInputInfo.MaxBuffers)
        {
            throw new ArgumentException("The graphics record has too many vertex inputs.");
        }

        for (var index = 0; index < vertexInput.BindingCount; index++)
        {
            vertexInput.Bindings[index] = new PipelineVertexBinding(reader.ReadUInt32(), reader.ReadBoolean());
        }

        for (var index = 0; index < vertexInput.AttributeCount; index++)
        {
            vertexInput.Attributes[index] = new PipelineVertexAttribute(reader.ReadUInt32(), reader.ReadByte());
        }

        var staticParameters = PipelineStaticParameters.FromBytes(reader.ReadBytes(PipelineStaticParameters.ByteSize));
        var attributes = new VertexAttributeFormat[Count(reader)];
        for (var index = 0; index < attributes.Length; index++)
        {
            attributes[index] = new VertexAttributeFormat(
                new BufferDescriptorWords(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()),
                reader.ReadInt32());
        }

        return new GraphicsPipelineRecord
        {
            Vertex = vertex,
            Pixel = pixel,
            Rendering = rendering,
            VertexInput = vertexInput,
            StaticParameters = staticParameters,
            Attributes = attributes,
        };
    }

    public static void WriteBinary(BinaryWriter writer, ReadOnlySpan<byte> driverKey, UInt128 contentKey, IReadOnlyList<PipelineBinaryPart> parts)
    {
        writer.Write((byte)driverKey.Length);
        writer.Write(driverKey);
        writer.Write((ulong)contentKey);
        writer.Write((ulong)(contentKey >> 64));
        writer.Write(parts.Count);
        foreach (var part in parts)
        {
            writer.Write(part.Key.Length);
            writer.Write(part.Key);
            writer.Write(part.Data.Length);
            writer.Write(part.Data);
        }
    }

    public static (byte[] DriverKey, UInt128 ContentKey) ReadBinaryHeader(BinaryReader reader)
    {
        var driverKey = reader.ReadBytes(reader.ReadByte());
        var low = reader.ReadUInt64();
        var high = reader.ReadUInt64();
        return (driverKey, new UInt128(high, low));
    }

    public static void WriteCompiledStage(BinaryWriter writer, ulong stageIdentity, ulong stamp, CompiledStageRecord stage)
    {
        writer.Write(stageIdentity);
        writer.Write(stamp);
        writer.Write(stage.Spirv.Length);
        writer.Write(stage.Spirv);
        writer.Write(stage.Bindings.Length);
        foreach (var binding in stage.Bindings)
        {
            writer.Write(binding.Binding);
            writer.Write(binding.DescriptorType);
            writer.Write(binding.DescriptorCount);
            writer.Write(binding.StageFlags);
        }

        writer.Write(stage.VertexFetchComponents.Length);
        writer.Write(stage.VertexFetchComponents);
        writer.Write(stage.PushDataEnd);
    }

    public static (ulong StageIdentity, ulong Stamp) ReadCompiledStageHeader(BinaryReader reader) =>
        (reader.ReadUInt64(), reader.ReadUInt64());

    public static CompiledStageRecord ReadCompiledStageBody(BinaryReader reader)
    {
        var spirv = reader.ReadBytes(Count(reader));
        var bindings = new LayoutBindingRecord[Count(reader)];
        for (var index = 0; index < bindings.Length; index++)
        {
            bindings[index] = new LayoutBindingRecord(reader.ReadUInt32(), reader.ReadInt32(), reader.ReadUInt32(), reader.ReadUInt32());
        }

        return new CompiledStageRecord
        {
            Spirv = spirv,
            Bindings = bindings,
            VertexFetchComponents = reader.ReadBytes(Count(reader)),
            PushDataEnd = reader.ReadUInt32(),
        };
    }

    public static PipelineBinaryPart[] ReadBinaryParts(BinaryReader reader)
    {
        var parts = new PipelineBinaryPart[Count(reader)];
        for (var index = 0; index < parts.Length; index++)
        {
            var key = reader.ReadBytes(Count(reader));
            parts[index] = new PipelineBinaryPart(key, reader.ReadBytes(Count(reader)));
        }

        return parts;
    }

    public static void WriteSpecialization(BinaryWriter writer, ResourceSpecialization specialization)
    {
        writer.Write(specialization.BaseBufferCount);
        writer.Write(specialization.Buffers.Count);
        foreach (var buffer in specialization.Buffers)
        {
            writer.Write(buffer.PackedStride);
            writer.Write(buffer.DescriptorFormat);
            writer.Write(buffer.DescriptorSwizzle);
        }

        writer.Write(specialization.Images.Count);
        foreach (var image in specialization.Images)
        {
            writer.Write((int)image.NumericClass);
            writer.Write((int)image.Dimension);
            writer.Write(image.MipCount);
            writer.Write(image.ConversionFormat);
            writer.Write(image.ShaderSwizzle);
            writer.Write(image.IndirectRoot);
            writer.Write(image.IndirectMappingOffset);
            writer.Write(image.IndirectSearchIterations);
            writer.Write(image.Cube);
            writer.Write(image.EmulatedCompareFunction);
        }

        writer.Write(specialization.BufferCandidateTables.Count);
        foreach (var table in specialization.BufferCandidateTables)
        {
            writer.Write(table.FirstCandidate);
            writer.Write(table.CandidateCount);
            writer.Write(table.MappingOffset);
            writer.Write(table.SearchIterations);
        }
    }

    private static ResourceSpecialization ReadSpecialization(BinaryReader reader)
    {
        var specialization = new ResourceSpecialization { BaseBufferCount = reader.ReadInt32() };
        for (var count = Count(reader); count > 0; count--)
        {
            specialization.Buffers.Add(new BufferSpecialization(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        }

        for (var count = Count(reader); count > 0; count--)
        {
            specialization.Images.Add(new ImageSpecialization(
                (ImageNumericClass)reader.ReadInt32(),
                (ImageDimension)reader.ReadInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadBoolean(),
                reader.ReadInt32()));
        }

        for (var count = Count(reader); count > 0; count--)
        {
            specialization.BufferCandidateTables.Add(new BufferCandidateTableSpecialization(
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        }

        return specialization;
    }

    public static void WriteCompute(BinaryWriter writer, ComputeInputInfo info, Gen5ComputeSystemRegisters? registers)
    {
        writer.Write(info.ThreadsX);
        writer.Write(info.ThreadsY);
        writer.Write(info.ThreadsZ);
        writer.Write(info.DispatchThreadDimensions);
        writer.Write(info.GroupIdX);
        writer.Write(info.GroupIdY);
        writer.Write(info.GroupIdZ);
        writer.Write(info.ThreadIdCount);
        writer.Write(info.ThreadGroupSizeEnabled);
        writer.Write(info.WaveSize);
        writer.Write(info.LocalDataShareDwords);
        writer.Write(info.ScratchDwords);
        writer.Write(info.NeedsLocalDataShareBarriers);
        writer.Write(info.WorkgroupRegister);
        writer.Write(registers is not null);
        if (registers is { } values)
        {
            WriteOptional(writer, values.WorkGroupXRegister);
            WriteOptional(writer, values.WorkGroupYRegister);
            WriteOptional(writer, values.WorkGroupZRegister);
            WriteOptional(writer, values.ThreadGroupSizeRegister);
        }
    }

    private static (ComputeInputInfo Info, Gen5ComputeSystemRegisters? Registers) ReadCompute(BinaryReader reader)
    {
        var info = new ComputeInputInfo
        {
            ThreadsX = reader.ReadUInt32(),
            ThreadsY = reader.ReadUInt32(),
            ThreadsZ = reader.ReadUInt32(),
            DispatchThreadDimensions = reader.ReadBoolean(),
            GroupIdX = reader.ReadBoolean(),
            GroupIdY = reader.ReadBoolean(),
            GroupIdZ = reader.ReadBoolean(),
            ThreadIdCount = reader.ReadInt32(),
            ThreadGroupSizeEnabled = reader.ReadBoolean(),
            WaveSize = reader.ReadUInt32(),
            LocalDataShareDwords = reader.ReadUInt32(),
            ScratchDwords = reader.ReadUInt32(),
            NeedsLocalDataShareBarriers = reader.ReadBoolean(),
            WorkgroupRegister = reader.ReadInt32(),
        };
        Gen5ComputeSystemRegisters? registers = reader.ReadBoolean()
            ? new Gen5ComputeSystemRegisters(ReadOptional(reader), ReadOptional(reader), ReadOptional(reader), ReadOptional(reader))
            : null;
        return (info, registers);
    }

    private static void WriteVertex(BinaryWriter writer, VertexStageInputs vertex)
    {
        writer.Write(vertex.FetchEmbedded);
        writer.Write(vertex.FetchAttributeRegister);
        writer.Write(vertex.FetchBufferRegister);
        writer.Write(vertex.Inputs.Count);
        foreach (var input in vertex.Inputs)
        {
            writer.Write(input.Pc);
            writer.Write(input.Location);
            writer.Write(input.FetchComponentCount);
            writer.Write(input.ComponentCount);
            writer.Write(input.NumberFormat);
            writer.Write(input.DestinationSelect);
            writer.Write(input.PerInstance);
            writer.Write(input.AliasPcs.Count);
            foreach (var alias in input.AliasPcs)
            {
                writer.Write(alias);
            }
        }

        writer.Write(vertex.ScratchDwords);
        writer.Write(vertex.RequiredVertexOutputCount);
        writer.Write(vertex.PositionExportControl);
        var clip = vertex.ClipSpace;
        writer.Write(clip.Enabled);
        writer.Write(clip.ScaleX);
        writer.Write(clip.ScaleY);
        writer.Write(clip.OffsetX);
        writer.Write(clip.OffsetY);
        writer.Write(clip.HalfExtentX);
        writer.Write(clip.HalfExtentY);
    }

    private static VertexStageInputs ReadVertex(BinaryReader reader)
    {
        var fetchEmbedded = reader.ReadBoolean();
        var attributeRegister = reader.ReadInt32();
        var bufferRegister = reader.ReadInt32();
        var inputs = new ShaderVertexInput[Count(reader)];
        for (var index = 0; index < inputs.Length; index++)
        {
            var pc = reader.ReadUInt32();
            var location = reader.ReadUInt32();
            var fetchComponents = reader.ReadUInt32();
            var components = reader.ReadUInt32();
            var numberFormat = reader.ReadUInt32();
            var destinationSelect = reader.ReadUInt32();
            var perInstance = reader.ReadBoolean();
            var aliases = new List<uint>();
            for (var count = Count(reader); count > 0; count--)
            {
                aliases.Add(reader.ReadUInt32());
            }

            inputs[index] = new ShaderVertexInput(pc, location, fetchComponents, components, numberFormat, destinationSelect, perInstance, aliases);
        }

        var scratch = reader.ReadUInt32();
        var requiredOutputs = reader.ReadInt32();
        var positionExportControl = reader.ReadUInt32();
        var clip = new ShaderClipSpaceTransform(
            reader.ReadBoolean(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        return new VertexStageInputs(
            fetchEmbedded, attributeRegister, bufferRegister, inputs, scratch, requiredOutputs, positionExportControl, clip);
    }

    private static void WritePixel(BinaryWriter writer, PixelStageInputs pixel)
    {
        writer.Write(pixel.ScratchDwords);
        writer.Write(pixel.Outputs.Count);
        foreach (var output in pixel.Outputs)
        {
            writer.Write(output.GuestSlot);
            writer.Write(output.HostLocation);
            writer.Write((int)output.Kind);
            writer.Write(output.ComponentMapping.Packed);
            writer.Write(output.ExportTarget);
        }

        writer.Write(pixel.InputEnable);
        writer.Write(pixel.CustomInterpolationMask);
        writer.Write(pixel.InputAddress);
        writer.Write(pixel.InputCntl.Count);
        foreach (var setting in pixel.InputCntl)
        {
            writer.Write(setting);
        }
    }

    private static PixelStageInputs ReadPixel(BinaryReader reader)
    {
        var scratch = reader.ReadUInt32();
        var outputs = new Gen5PixelOutputBinding[Count(reader)];
        for (var index = 0; index < outputs.Length; index++)
        {
            outputs[index] = new Gen5PixelOutputBinding(
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                (Gen5PixelOutputKind)reader.ReadInt32(),
                new Gen5ColorComponentMapping(reader.ReadByte()))
            {
                ExportTarget = reader.ReadUInt32(),
            };
        }

        var inputEnable = reader.ReadUInt32();
        var customMask = reader.ReadUInt32();
        var inputAddress = reader.ReadUInt32();
        var cntl = new uint[Count(reader)];
        for (var index = 0; index < cntl.Length; index++)
        {
            cntl[index] = reader.ReadUInt32();
        }

        return new PixelStageInputs(scratch, outputs, inputEnable, customMask, inputAddress, cntl);
    }

    private static void WriteOptional(BinaryWriter writer, uint? value)
    {
        writer.Write(value.HasValue);
        writer.Write(value.GetValueOrDefault());
    }

    private static uint? ReadOptional(BinaryReader reader)
    {
        var hasValue = reader.ReadBoolean();
        var value = reader.ReadUInt32();
        return hasValue ? value : null;
    }

    private static int Count(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        return count >= 0 && count <= remaining
            ? count
            : throw new ArgumentException("The shader cache record count is out of range.");
    }
}
