// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.IO.Hashing;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A vertex or mesh program a session translated, with what its translation read besides the code: the fields its
// static key (StageStaticKey) holds, without vertex buffer addresses and record counts. A later build's prewarm translates
// it again and compiles its pre-rasterization library for each state recorded with it, so the program's first draw links a
// stored library instead of compiling one while the command stream waits.
internal sealed class VertexPrewarmRecord
{
    public bool Mesh { get; init; }
    public ulong ContinuationAddressOffset { get; init; }
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required uint UserDataBase { get; init; }
    public required uint UserDataCount { get; init; }
    public required uint PushDataCursor { get; init; }
    public VertexInputInfo? VertexInfo { get; init; }
    public MeshDrawConfiguration? MeshInfo { get; init; }
    public required int RequiredVertexOutputCount { get; init; }
    public uint PackedParameterMask { get; init; }
    public uint? MeshOutputLocationMask { get; init; }
    public required ResourceSpecialization Specialization { get; init; }
    public bool DeviceRecordCounts { get; init; }
    // The program's image table selects more images than a draw can bind, so its plan reads them from the bindless heap
    // (ShaderProgramCache.PreferHeapRecords). The prewarm plans it the same way, or its compile would never be reused.
    public bool PreferHeapRecords { get; init; }

    public (ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset) CodeKey =>
        (Hash, CodeSize, Address, ContinuationAddressOffset);
}

// What a pre-rasterization library takes from the draw besides its program and layout, as Vulkan enum values. The part
// is built without attachment formats (CreatePreRasterizationLibrary), so this is all a prewarm needs to build it again.
internal readonly record struct PreRasterizationState(
    int PolygonMode,
    int CullMode,
    int FrontFace,
    bool DepthClampEnable,
    bool RasterizerDiscardEnable,
    bool DepthClipEnable,
    bool NegativeOneToOne);

// One unit of the graphics prewarm; its identity keys the prewarm's progress, and its first use orders it.
internal abstract record GraphicsPrewarmItem(ulong Identity)
{
    public float FirstUse { get; init; }
}

internal sealed record PixelPrewarmItem(PixelPrewarmRecord Record, ShaderCodeCapture Code, ulong Identity)
    : GraphicsPrewarmItem(Identity);

internal sealed record PreRasterizationPrewarmItem(
    VertexPrewarmRecord Program, ShaderCodeCapture Code, PreRasterizationState State, ulong Identity)
    : GraphicsPrewarmItem(Identity);

internal sealed partial class ShaderPrewarmList
{
    // Vertex prewarm records, in the side file: a vertex or mesh program, and one state its pre-rasterization library
    // was compiled with.
    private const byte VertexKind = 5;
    private const byte PreRasterizationKind = 6;
    private const int PreRasterizationPayloadBytes = sizeof(ulong) + 3 * sizeof(int) + 4;

    private readonly HashSet<ulong> _vertices = [];
    private readonly Dictionary<ulong, VertexPrewarmRecord> _loadedVertices = new();
    private readonly HashSet<ulong> _preRasterizations = [];
    private readonly List<(ulong Program, PreRasterizationState State, ulong Identity)> _loadedPreRasterizations = [];
    // The loaded pixel and pre-rasterization records in load order: record kind, index in its list, and the seconds into its
    // session at which that session first met it.
    private readonly List<(byte Kind, int Index, float FirstUse)> _graphicsOrder = [];

    // A pixel or pre-rasterization record ends with the seconds into its session at which the session first met it (a
    // float after the record's own bytes, which alone give its identity). The graphics prewarm runs items in that order
    // across sessions, so the records a later session adds go where that session needed them instead of after every
    // record of the earlier sessions.
    private const int FirstUseBytes = sizeof(float);
    private readonly long _opened = Stopwatch.GetTimestamp();

    // The session's clock, seconds since the list opened (tests replace it).
    internal Func<float>? SessionClock { get; set; }

    private float SessionSeconds() => SessionClock?.Invoke() ?? (float)Stopwatch.GetElapsedTime(_opened).TotalSeconds;

    private byte[] WithFirstUse(byte[] record)
    {
        var payload = new byte[record.Length + FirstUseBytes];
        record.CopyTo(payload, 0);
        BitConverter.TryWriteBytes(payload.AsSpan(record.Length), SessionSeconds());
        return payload;
    }

    private static (byte[] Record, float FirstUse) SplitFirstUse(byte[] payload) => payload.Length < FirstUseBytes
        ? (payload, 0f)
        : (payload[..^FirstUseBytes], BitConverter.ToSingle(payload, payload.Length - FirstUseBytes));

    internal static readonly uint VertexSchemaFingerprint = SchemaFingerprintOf(
        typeof(VertexPrewarmRecord), typeof(VertexInputInfo), typeof(VertexAttributeResource), typeof(BufferDescriptorWords),
        typeof(ClipSpaceTransform), typeof(MeshDrawConfiguration), typeof(GuestGeometryConfiguration), typeof(MeshExecutionLimits),
        typeof(ResourceSpecialization), typeof(BufferSpecialization), typeof(ImageSpecialization),
        typeof(BufferCandidateTableSpecialization));

    public int LoadedPreRasterizationCount => _loadedPreRasterizations.Count;

    // Without the code address, as a pixel record's.
    public static ulong Identity(VertexPrewarmRecord record) => XxHash3.HashToUInt64(SerializeVertex(record, address: 0));

    // The recorded pixel programs and pre-rasterization states by the time into their sessions at which they were first
    // met, records of equal times in load order. A state whose program or code is missing is left out.
    public IReadOnlyList<GraphicsPrewarmItem> LoadedGraphicsPrewarm()
    {
        var items = new List<GraphicsPrewarmItem>(_graphicsOrder.Count);
        foreach (var (kind, index, firstUse) in _graphicsOrder.OrderBy(static item => item.FirstUse))
        {
            if (kind == PixelKind)
            {
                var pixel = _loadedPixels[index];
                if (_loadedCodes.TryGetValue(pixel.CodeKey, out var pixelCode))
                {
                    items.Add(new PixelPrewarmItem(pixel, pixelCode, Identity(pixel)) { FirstUse = firstUse });
                }

                continue;
            }

            var (program, state, identity) = _loadedPreRasterizations[index];
            if (_loadedVertices.TryGetValue(program, out var vertex) && _loadedCodes.TryGetValue(vertex.CodeKey, out var code))
            {
                items.Add(new PreRasterizationPrewarmItem(vertex, code, state, identity) { FirstUse = firstUse });
            }
        }

        return items;
    }

    // Returns the record's identity, under which RecordPreRasterization records the program's states.
    public ulong RecordVertex(ShaderCodeCapture code, VertexPrewarmRecord record)
    {
        NoteHeapRecords(record.PreferHeapRecords, record.Hash, record.CodeSize, record.ContinuationAddressOffset);
        var payload = SerializeVertex(record, record.Address);
        var identity = Identity(record);
        lock (_gate)
        {
            if (!_failed && _vertices.Add(identity))
            {
                if (_extensionCodes.Add(code.Key))
                {
                    Append(_extension, CodeKind, SerializeCode(code));
                }

                Append(_extension, VertexKind, payload);
            }
        }

        return identity;
    }

    // program: the identity of the recorded vertex or mesh program (Identity(VertexPrewarmRecord)).
    public void RecordPreRasterization(ulong program, PreRasterizationState state)
    {
        var payload = SerializePreRasterization(program, state);
        lock (_gate)
        {
            if (_failed || !_preRasterizations.Add(XxHash3.HashToUInt64(payload)))
            {
                return;
            }

            Append(_extension, PreRasterizationKind, WithFirstUse(payload));
        }
    }

    // TryLoadRecord's vertex kinds; false for any other kind. A record of another schema is skipped, not refused.
    private bool TryLoadGraphicsRecord(byte kind, byte[] payload)
    {
        switch (kind)
        {
            case VertexKind:
                if (TryDeserializeVertex(payload) is { } vertex && _vertices.Add(Identity(vertex)))
                {
                    NoteHeapRecords(vertex.PreferHeapRecords, vertex.Hash, vertex.CodeSize, vertex.ContinuationAddressOffset);
                    _loadedVertices[Identity(vertex)] = vertex;
                }

                return true;

            case PreRasterizationKind:
            {
                var (state, firstUse) = SplitFirstUse(payload);
                if (state.Length == PreRasterizationPayloadBytes && _preRasterizations.Add(XxHash3.HashToUInt64(state)))
                {
                    _loadedPreRasterizations.Add((BitConverter.ToUInt64(state, 0), DeserializePreRasterization(state),
                        XxHash3.HashToUInt64(state)));
                    _graphicsOrder.Add((PreRasterizationKind, _loadedPreRasterizations.Count - 1, firstUse));
                }

                return true;
            }

            default:
                return false;
        }
    }

    // address: the record's code address, or 0 for its identity.
    private static byte[] SerializeVertex(VertexPrewarmRecord record, ulong address)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(VertexSchemaFingerprint);
        writer.Write(record.Mesh);
        writer.Write(record.Hash);
        writer.Write(record.CodeSize);
        writer.Write(address);
        writer.Write(record.ContinuationAddressOffset);
        writer.Write(record.UserDataBase);
        writer.Write(record.UserDataCount);
        writer.Write(record.PushDataCursor);
        writer.Write(record.RequiredVertexOutputCount);
        writer.Write(record.MeshOutputLocationMask.HasValue);
        writer.Write(record.MeshOutputLocationMask.GetValueOrDefault());
        if (record.Mesh)
        {
            var mesh = record.MeshInfo ?? throw new ArgumentException("A mesh prewarm record has no mesh configuration.");
            var geometry = mesh.Geometry;
            writer.Write(geometry.ThreadsPerGroup);
            writer.Write(geometry.InputTriangleStrip);
            writer.Write(geometry.InputPointList);
            writer.Write(geometry.InputLineList);
            writer.Write(geometry.InputPrimitiveCountPerWorkgroup);
            writer.Write(geometry.InputVertexCountPerWorkgroup);
            writer.Write(geometry.OutputVertexCapacity);
            writer.Write(geometry.OutputPrimitiveCapacity);
            writer.Write(geometry.ProvokingVertex);
            writer.Write(geometry.WaveSize);
            writer.Write(geometry.IeeeMode);
            writer.Write(geometry.ScratchDwords);
            writer.Write(geometry.LocalDataShareDwords);
            writer.Write(geometry.PositionExportControl);
            WriteClipSpace(writer, geometry.ClipSpace);
            writer.Write(geometry.TaskLaunched);
            writer.Write(mesh.Execution.DeviceSubgroupLaneCount);
            writer.Write(mesh.Execution.MaxGroupCountX);
            writer.Write(mesh.Execution.MaxGroupCountY);
            writer.Write(mesh.Execution.MaxGroupTotalCount);
        }
        else
        {
            var info = record.VertexInfo ?? throw new ArgumentException("A vertex prewarm record has no vertex input info.");
            writer.Write(info.FetchEmbedded);
            writer.Write(info.FetchAttributeRegister);
            writer.Write(info.FetchBufferRegister);
            writer.Write(info.IeeeMode);
            writer.Write(info.ScratchDwords);
            writer.Write(info.PositionExportControl);
            WriteClipSpace(writer, info.ClipSpace);
            writer.Write(info.Attributes.Length);
            foreach (var attribute in info.Attributes)
            {
                // The translation reads the descriptor's format, selectors and stride, not its base or record count.
                writer.Write(attribute.Descriptor.Word1 & 0xFFFF_0000u);
                writer.Write(attribute.Descriptor.Word3);
                writer.Write(attribute.RegisterStart);
                writer.Write(attribute.RegisterCount);
                writer.Write(attribute.AttributeId);
                writer.Write(attribute.FetchIndex);
                writer.Write(attribute.BufferIndex);
                writer.Write(attribute.OffsetBytes);
            }
        }

        WriteSpecialization(writer, record.Specialization);
        writer.Write(record.DeviceRecordCounts);
        // The user data registers of the draw's start instance and base vertex, last and only when the program reads
        // one, so a record without them keeps its bytes and identity.
        if (!record.Mesh && record.VertexInfo is { } drawInfo && (drawInfo.StartInstanceUserData != -1 || drawInfo.BaseVertexUserData != -1))
        {
            writer.Write(drawInfo.StartInstanceUserData);
            if (drawInfo.BaseVertexUserData != -1)
            {
                writer.Write(drawInfo.BaseVertexUserData);
            }
        }

        if (record.PackedParameterMask != 0) writer.Write(record.PackedParameterMask);
        // The trailing byte identifies optional state after the draw registers.
        var extension = (record.PreferHeapRecords ? 1 : 0) | (record.PackedParameterMask != 0 ? 2 : 0);
        if (extension != 0) writer.Write((byte)extension);
        writer.Flush();
        return stream.ToArray();
    }

    // Null for a record of another vertex schema or a damaged one.
    private static VertexPrewarmRecord? TryDeserializeVertex(byte[] payload)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload));
            if (reader.ReadUInt32() != VertexSchemaFingerprint)
            {
                return null;
            }

            var mesh = reader.ReadBoolean();
            var hash = reader.ReadUInt64();
            var codeSize = reader.ReadUInt32();
            var address = reader.ReadUInt64();
            var continuationAddressOffset = reader.ReadUInt64();
            var userDataBase = reader.ReadUInt32();
            var userDataCount = reader.ReadUInt32();
            var pushDataCursor = reader.ReadUInt32();
            var requiredOutputCount = reader.ReadInt32();
            var hasOutputMask = reader.ReadBoolean();
            var outputMask = reader.ReadUInt32();
            VertexInputInfo? vertexInfo = null;
            MeshDrawConfiguration? meshInfo = null;
            // Member initializers run in order, so the reads below follow the written layout.
            if (mesh)
            {
                var geometry = new GuestGeometryConfiguration
                {
                    ThreadsPerGroup = reader.ReadUInt32(),
                    InputTriangleStrip = reader.ReadBoolean(),
                    InputPointList = reader.ReadBoolean(),
                    InputLineList = reader.ReadBoolean(),
                    InputPrimitiveCountPerWorkgroup = reader.ReadUInt32(),
                    InputVertexCountPerWorkgroup = reader.ReadUInt32(),
                    OutputVertexCapacity = reader.ReadUInt32(),
                    OutputPrimitiveCapacity = reader.ReadUInt32(),
                    ProvokingVertex = reader.ReadUInt32(),
                    WaveSize = reader.ReadUInt32(),
                    IeeeMode = reader.ReadBoolean(),
                    ScratchDwords = reader.ReadUInt32(),
                    LocalDataShareDwords = reader.ReadUInt32(),
                    PositionExportControl = reader.ReadUInt32(),
                    ClipSpace = ReadClipSpace(reader),
                    TaskLaunched = reader.ReadBoolean(),
                };
                meshInfo = new MeshDrawConfiguration
                {
                    Geometry = geometry,
                    Execution = new MeshExecutionLimits
                    {
                        DeviceSubgroupLaneCount = reader.ReadUInt32(),
                        MaxGroupCountX = reader.ReadUInt32(),
                        MaxGroupCountY = reader.ReadUInt32(),
                        MaxGroupTotalCount = reader.ReadUInt32(),
                    },
                };
            }
            else
            {
                var fetchEmbedded = reader.ReadBoolean();
                var fetchAttributeRegister = reader.ReadInt32();
                var fetchBufferRegister = reader.ReadInt32();
                var ieeeMode = reader.ReadBoolean();
                var scratchDwords = reader.ReadUInt32();
                var positionExportControl = reader.ReadUInt32();
                var clipSpace = ReadClipSpace(reader);
                var attributes = new VertexAttributeResource[CheckedCount(reader.ReadInt32(), payload.Length)];
                for (var index = 0; index < attributes.Length; index++)
                {
                    var word1 = reader.ReadUInt32();
                    var word3 = reader.ReadUInt32();
                    attributes[index] = new VertexAttributeResource(new BufferDescriptorWords(0, word1, 0, word3),
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadUInt32(), reader.ReadInt32(),
                        reader.ReadUInt32());
                }

                vertexInfo = new VertexInputInfo
                {
                    Attributes = attributes,
                    FetchEmbedded = fetchEmbedded,
                    FetchAttributeRegister = fetchAttributeRegister,
                    FetchBufferRegister = fetchBufferRegister,
                    IeeeMode = ieeeMode,
                    ScratchDwords = scratchDwords,
                    PositionExportControl = positionExportControl,
                    ClipSpace = clipSpace,
                };
            }

            var specialization = ReadSpecialization(reader, payload.Length);
            var deviceRecordCounts = reader.ReadBoolean();
            var trailing = reader.BaseStream.Length - reader.BaseStream.Position;
            var hasExtension = trailing % sizeof(int) == 1;
            var extension = hasExtension ? payload[^1] : 0;
            if (hasExtension && extension is 0 or > 3) return null;
            var hasPackedParameters = (extension & 2) != 0;
            var drawParameters = trailing - (hasExtension ? 1 : 0) - (hasPackedParameters ? sizeof(uint) : 0);
            if (vertexInfo is not null && drawParameters is sizeof(int) or 2 * sizeof(int))
            {
                vertexInfo.StartInstanceUserData = reader.ReadInt32();
                if (drawParameters == 2 * sizeof(int))
                {
                    vertexInfo.BaseVertexUserData = reader.ReadInt32();
                }
            }

            var packedParameterMask = hasPackedParameters ? reader.ReadUInt32() : 0u;
            if (hasExtension)
            {
                reader.ReadByte();
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                return null;
            }

            return new VertexPrewarmRecord
            {
                Mesh = mesh,
                Hash = hash,
                CodeSize = codeSize,
                Address = address,
                ContinuationAddressOffset = continuationAddressOffset,
                UserDataBase = userDataBase,
                UserDataCount = userDataCount,
                PushDataCursor = pushDataCursor,
                VertexInfo = vertexInfo,
                MeshInfo = meshInfo,
                RequiredVertexOutputCount = requiredOutputCount,
                PackedParameterMask = packedParameterMask,
                MeshOutputLocationMask = hasOutputMask ? outputMask : null,
                Specialization = specialization,
                DeviceRecordCounts = deviceRecordCounts,
                PreferHeapRecords = (extension & 1) != 0,
            };
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static void WriteClipSpace(BinaryWriter writer, ClipSpaceTransform clipSpace)
    {
        writer.Write(clipSpace.Enabled);
        writer.Write(clipSpace.ScaleX);
        writer.Write(clipSpace.ScaleY);
        writer.Write(clipSpace.OffsetX);
        writer.Write(clipSpace.OffsetY);
        writer.Write(clipSpace.HalfExtentX);
        writer.Write(clipSpace.HalfExtentY);
    }

    private static ClipSpaceTransform ReadClipSpace(BinaryReader reader) => new(
        reader.ReadBoolean(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle());

    private static byte[] SerializePreRasterization(ulong program, PreRasterizationState state)
    {
        var payload = new byte[PreRasterizationPayloadBytes];
        BitConverter.TryWriteBytes(payload, program);
        BitConverter.TryWriteBytes(payload.AsSpan(8), state.PolygonMode);
        BitConverter.TryWriteBytes(payload.AsSpan(12), state.CullMode);
        BitConverter.TryWriteBytes(payload.AsSpan(16), state.FrontFace);
        payload[20] = state.DepthClampEnable ? (byte)1 : (byte)0;
        payload[21] = state.RasterizerDiscardEnable ? (byte)1 : (byte)0;
        payload[22] = state.DepthClipEnable ? (byte)1 : (byte)0;
        payload[23] = state.NegativeOneToOne ? (byte)1 : (byte)0;
        return payload;
    }

    private static PreRasterizationState DeserializePreRasterization(byte[] payload) => new(
        BitConverter.ToInt32(payload, 8), BitConverter.ToInt32(payload, 12), BitConverter.ToInt32(payload, 16),
        payload[20] != 0, payload[21] != 0, payload[22] != 0, payload[23] != 0);
}
