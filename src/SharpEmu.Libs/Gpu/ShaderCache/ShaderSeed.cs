// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal sealed class ShaderSeed
{
    public const string Extension = ".seed";

    private const uint Magic = 0x44534553;
    private const uint FormatVersion = 2;
    private const uint StatesOnlyFormatVersion = 1;

    private readonly SortedDictionary<ulong, SortedDictionary<ulong, (StaticGraphicsState State, int Uses)>> _groups = new();
    private readonly SortedSet<(ulong Vertex, ulong Pixel)> _pairs = [];
    private readonly SortedDictionary<ulong, StageRecord> _computes = new();
    private readonly SortedDictionary<ulong, GraphicsPipelineRecord> _graphics = new();

    public string TitleId { get; private set; } = string.Empty;

    public string GameVersion { get; private set; } = string.Empty;

    public int FirstVariantVotes { get; private set; }

    public int SecondVariantVotes { get; private set; }

    public bool SecondInterpolantVariant => SecondVariantVotes > FirstVariantVotes;

    public int GroupCount => _groups.Count;

    public int StateCount => _groups.Values.Sum(static group => group.Count);

    public IReadOnlyCollection<(ulong Vertex, ulong Pixel)> Pairs => _pairs;

    public IReadOnlyCollection<StageRecord> Computes => _computes.Values;

    public IReadOnlyCollection<GraphicsPipelineRecord> Graphics => _graphics.Values;

    public bool IsEmpty => StateCount == 0 && _pairs.Count == 0 && _computes.Count == 0 && _graphics.Count == 0;

    public static ulong ComputeIdentity(StageRecord record) =>
        XxHash3.HashToUInt64(ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteStage(writer, record.WithAddress(0))));

    public static ulong GraphicsIdentity(GraphicsPipelineRecord record) =>
        XxHash3.HashToUInt64(ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteGraphics(writer, record.WithoutAddresses())));

    public IReadOnlyList<StaticGraphicsState> Palette(ulong signature, int maxStates) =>
        _groups.TryGetValue(signature, out var group)
            ? group.Values
                .OrderByDescending(static entry => entry.Uses)
                .ThenBy(static entry => entry.State.Identity)
                .Take(maxStates)
                .Select(static entry => entry.State)
                .ToArray()
            : [];

    public static ShaderSeed Load(string path)
    {
        var pack = new ShaderSeed();
        try
        {
            if (!File.Exists(path))
            {
                return pack;
            }

            using var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
            var magic = reader.ReadUInt32();
            var version = reader.ReadUInt32();
            if (magic != Magic || version is not (FormatVersion or StatesOnlyFormatVersion))
            {
                return pack;
            }

            pack.TitleId = reader.ReadString();
            pack.GameVersion = reader.ReadString();

            pack.FirstVariantVotes = reader.ReadInt32();
            pack.SecondVariantVotes = reader.ReadInt32();
            for (var groups = reader.ReadInt32(); groups > 0; groups--)
            {
                var signature = reader.ReadUInt64();
                for (var states = reader.ReadInt32(); states > 0; states--)
                {
                    var uses = reader.ReadInt32();
                    pack.Add(signature, ReadState(reader), uses);
                }
            }

            for (var pairs = reader.ReadInt32(); pairs > 0; pairs--)
            {
                pack._pairs.Add((reader.ReadUInt64(), reader.ReadUInt64()));
            }

            if (version == FormatVersion)
            {
                var schema = reader.ReadUInt32();
                var records = reader.ReadBytes(reader.ReadInt32());
                if (schema == ShaderCacheFile.RecordSchema)
                {
                    pack.ReadRecords(records);
                }
            }

            return pack;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return new ShaderSeed();
        }
    }

    public static ShaderSeed Learn(ShaderCacheFile file)
    {
        var pack = new ShaderSeed();
        foreach (var compute in file.SnapshotComputes())
        {
            pack._computes.TryAdd(ComputeIdentity(compute.Record), compute.Record.WithAddress(0));
        }

        foreach (var graphics in file.SnapshotGraphics())
        {
            pack._graphics.TryAdd(GraphicsIdentity(graphics.Record), graphics.Record.WithoutAddresses());
        }

        var programsByCode = new Dictionary<(ulong Hash, uint Size), CachedProgram>();
        foreach (var program in file.SnapshotPrograms())
        {
            programsByCode.TryAdd((program.Code.Hash, program.Code.CodeSize), program);
        }

        foreach (var pair in file.SnapshotPairs())
        {
            pack._pairs.Add(pair);
        }

        foreach (var recorded in file.SnapshotGraphics())
        {
            var record = recorded.Record;
            if (record.Pixel is not { Pixel: { } pixelInputs } pixelStage || record.Vertex.Vertex is not { } vertexInputs ||
                vertexInputs.FetchEmbedded || record.VertexInput.BindingCount != 0 || record.VertexInput.AttributeCount != 0 ||
                !programsByCode.TryGetValue((record.Vertex.Hash, record.Vertex.CodeSize), out var vertex) ||
                !programsByCode.TryGetValue((pixelStage.Hash, pixelStage.CodeSize), out var pixel) ||
                !StaticStageInputs.TryParse(vertex, out var vertexHeader) ||
                !StaticStageInputs.TryParse(pixel, out var pixelHeader))
            {
                continue;
            }

            pack._pairs.Add((vertex.Identity, pixel.Identity));
            var recordedCntl = pixelInputs.InputCntl;
            if (recordedCntl.Count != 0)
            {
                var first = AgcExports.ComputeInterpolantMapping(pixelHeader.InputSemantics, vertexHeader.OutputSemantics, false);
                var second = AgcExports.ComputeInterpolantMapping(pixelHeader.InputSemantics, vertexHeader.OutputSemantics, true);
                pack.FirstVariantVotes += recordedCntl.SequenceEqual(first.Take(recordedCntl.Count)) ? 1 : 0;
                pack.SecondVariantVotes += recordedCntl.SequenceEqual(second.Take(recordedCntl.Count)) ? 1 : 0;
            }

            pack.Add(
                StaticStageInputs.PixelSignature(pixelHeader),
                CreateState(record.Rendering, record.StaticParameters, pixelInputs.Outputs, vertexInputs.ClipSpace),
                1);
        }

        return pack;
    }

    public ShaderSeed MergedWith(ShaderSeed other)
    {
        var merged = new ShaderSeed
        {
            TitleId = TitleId.Length != 0 ? TitleId : other.TitleId,
            GameVersion = GameVersion.Length != 0 ? GameVersion : other.GameVersion,
            FirstVariantVotes = Math.Max(FirstVariantVotes, other.FirstVariantVotes),
            SecondVariantVotes = Math.Max(SecondVariantVotes, other.SecondVariantVotes),
        };
        foreach (var source in (ShaderSeed[])[this, other])
        {
            foreach (var (signature, group) in source._groups)
            {
                foreach (var (state, uses) in group.Values)
                {
                    merged.Add(signature, state, uses, keepMaximum: true);
                }
            }

            merged._pairs.UnionWith(source._pairs);
            foreach (var (identity, record) in source._computes)
            {
                merged._computes.TryAdd(identity, record);
            }

            foreach (var (identity, record) in source._graphics)
            {
                merged._graphics.TryAdd(identity, record);
            }
        }

        return merged;
    }

    public byte[] Serialize() => ShaderCacheSerializer.Serialize(writer =>
    {
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(TitleId);
        writer.Write(GameVersion);
        writer.Write(FirstVariantVotes);
        writer.Write(SecondVariantVotes);
        writer.Write(_groups.Count);
        foreach (var (signature, group) in _groups)
        {
            writer.Write(signature);
            writer.Write(group.Count);
            foreach (var (state, uses) in group.Values)
            {
                writer.Write(uses);
                WriteState(writer, state);
            }
        }

        writer.Write(_pairs.Count);
        foreach (var (vertex, pixel) in _pairs)
        {
            writer.Write(vertex);
            writer.Write(pixel);
        }

        var records = ShaderCacheSerializer.Serialize(WriteRecords);
        writer.Write(ShaderCacheFile.RecordSchema);
        writer.Write(records.Length);
        writer.Write(records);
    });

    private void WriteRecords(BinaryWriter writer)
    {
        writer.Write(_computes.Count);
        foreach (var record in _computes.Values)
        {
            ShaderCacheSerializer.WriteStage(writer, record);
        }

        writer.Write(_graphics.Count);
        foreach (var record in _graphics.Values)
        {
            ShaderCacheSerializer.WriteGraphics(writer, record);
        }
    }

    private void ReadRecords(byte[] records)
    {
        using var reader = new BinaryReader(new MemoryStream(records));
        var computes = new List<StageRecord>();
        for (var count = reader.ReadInt32(); count > 0; count--)
        {
            computes.Add(ShaderCacheSerializer.ReadStage(reader));
        }

        var graphics = new List<GraphicsPipelineRecord>();
        for (var count = reader.ReadInt32(); count > 0; count--)
        {
            graphics.Add(ShaderCacheSerializer.ReadGraphics(reader));
        }

        foreach (var record in computes)
        {
            _computes.TryAdd(ComputeIdentity(record), record);
        }

        foreach (var record in graphics)
        {
            _graphics.TryAdd(GraphicsIdentity(record), record);
        }
    }

    public ShaderSeed Stamped(string titleId, string gameVersion)
    {
        var stamped = MergedWith(new ShaderSeed());
        stamped.TitleId = titleId;
        stamped.GameVersion = gameVersion;
        return stamped;
    }

    public bool TrySave(string path)
    {
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, Serialize());
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static StaticGraphicsState CreateState(
        PipelineRenderingState rendering,
        PipelineStaticParameters parameters,
        IReadOnlyList<Gen5PixelOutputBinding> outputs,
        ShaderClipSpaceTransform clipSpace)
    {
        var state = new StaticGraphicsState(rendering, parameters, outputs, clipSpace, 0);
        return state with { Identity = XxHash3.HashToUInt64(ShaderCacheSerializer.Serialize(writer => WriteState(writer, state))) };
    }

    private void Add(ulong signature, StaticGraphicsState state, int uses, bool keepMaximum = false)
    {
        if (!_groups.TryGetValue(signature, out var group))
        {
            _groups[signature] = group = new SortedDictionary<ulong, (StaticGraphicsState, int)>();
        }

        group[state.Identity] = group.TryGetValue(state.Identity, out var known)
            ? (known.State, keepMaximum ? Math.Max(known.Uses, uses) : known.Uses + uses)
            : (state, uses);
    }

    private static void WriteState(BinaryWriter writer, StaticGraphicsState state)
    {
        writer.Write(state.Rendering.ColorCount);
        foreach (var format in state.Rendering.ColorFormats)
        {
            writer.Write((int)format);
        }

        writer.Write((int)state.Rendering.DepthFormat);
        writer.Write((int)state.Rendering.StencilFormat);
        writer.Write(state.StaticParameters.Bytes);
        writer.Write(state.PixelOutputs.Count);
        foreach (var output in state.PixelOutputs)
        {
            writer.Write(output.GuestSlot);
            writer.Write(output.HostLocation);
            writer.Write((int)output.Kind);
            writer.Write(output.ComponentMapping.Packed);
            writer.Write(output.ExportTarget);
        }

        var clip = state.ClipSpace;
        writer.Write(clip.Enabled);
        writer.Write(clip.ScaleX);
        writer.Write(clip.ScaleY);
        writer.Write(clip.OffsetX);
        writer.Write(clip.OffsetY);
        writer.Write(clip.HalfExtentX);
        writer.Write(clip.HalfExtentY);
    }

    private static StaticGraphicsState ReadState(BinaryReader reader)
    {
        var rendering = new PipelineRenderingState { ColorCount = reader.ReadUInt32() };
        for (var index = 0; index < rendering.ColorFormats.Length; index++)
        {
            rendering.ColorFormats[index] = (Format)reader.ReadInt32();
        }

        rendering.DepthFormat = (Format)reader.ReadInt32();
        rendering.StencilFormat = (Format)reader.ReadInt32();
        var parameters = PipelineStaticParameters.FromBytes(reader.ReadBytes(PipelineStaticParameters.ByteSize));
        var count = reader.ReadInt32();
        if (count is < 0 or > 16)
        {
            throw new ArgumentException("The graphics state pack has too many pixel outputs.");
        }

        var outputs = new Gen5PixelOutputBinding[count];
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

        var clip = new ShaderClipSpaceTransform(
            reader.ReadBoolean(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        return CreateState(rendering, parameters, outputs, clip);
    }
}
