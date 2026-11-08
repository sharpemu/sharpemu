// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed class RecordingCpuMemory(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
{
    private readonly List<(ulong Address, byte[] Bytes)> _reads = [];

    public ICpuMemory Inner => inner;

    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        if (!inner.TryRead(virtualAddress, destination))
        {
            return false;
        }

        _reads.Add((virtualAddress, destination.ToArray()));
        return true;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;

    public CodeRange[] TakeRanges()
    {
        _reads.Sort(static (left, right) => left.Address.CompareTo(right.Address));
        var ranges = new List<CodeRange>();
        ulong start = 0;
        var bytes = new List<byte>();
        foreach (var (address, data) in _reads)
        {
            var end = start + (ulong)bytes.Count;
            if (bytes.Count != 0 && address <= end)
            {
                var overlap = (int)(end - address);
                if (overlap < data.Length)
                {
                    bytes.AddRange(data.AsSpan(overlap).ToArray());
                }

                continue;
            }

            if (bytes.Count != 0)
            {
                ranges.Add(new CodeRange(start, bytes.ToArray()));
            }

            start = address;
            bytes.Clear();
            bytes.AddRange(data);
        }

        if (bytes.Count != 0)
        {
            ranges.Add(new CodeRange(start, bytes.ToArray()));
        }

        _reads.Clear();
        return ranges.ToArray();
    }
}

internal sealed class ReplayCpuMemory(IReadOnlyList<CodeRange> ranges) : ICpuMemory
{
    public bool TryRead(ulong virtualAddress, Span<byte> destination)
    {
        foreach (var range in ranges)
        {
            if (virtualAddress < range.Address)
            {
                continue;
            }

            var offset = virtualAddress - range.Address;
            if (offset + (ulong)destination.Length <= (ulong)range.Bytes.Length)
            {
                range.Bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        return false;
    }

    public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
}

internal readonly record struct CodeRange(ulong Address, byte[] Bytes);

internal readonly record struct FusedCodeParts(ulong EntryHeaderAddress, ulong ContinuationAddress, ulong ContinuationHeaderAddress);

internal sealed class ShaderCodeCapture
{
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required Generation Generation { get; init; }
    public FusedCodeParts? Fused { get; init; }
    public required CodeRange[] Ranges { get; init; }
    public ulong FrontendDomain { get; init; }
    public uint[] FrontendUserData { get; init; } = [];
    public ulong FrontendContinuationOffset { get; init; }

    public (ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset) Key =>
        (Hash, CodeSize, Address, Fused is { } fused ? unchecked(fused.ContinuationAddress - Address) : FrontendContinuationOffset);

    public CpuContext CreateContext()
    {
        var context = new CpuContext(new ReplayCpuMemory(Ranges), Generation);
        if (Fused is { } fused)
        {
            Gen5ShaderTranslator.RegisterFusedProgram(
                context, Address, fused.EntryHeaderAddress, fused.ContinuationAddress, fused.ContinuationHeaderAddress);
        }

        return context;
    }
}

internal sealed class ComputePrewarmRecord
{
    public ulong ContinuationAddressOffset { get; init; }
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required uint UserDataBase { get; init; }
    public required uint UserDataCount { get; init; }
    public required uint PushDataCursor { get; init; }
    public required ComputeInputInfo Info { get; init; }
    public Gen5ComputeSystemRegisters? SystemRegisters { get; init; }
    public required ResourceSpecialization Specialization { get; init; }

    public (ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset) CodeKey =>
        (Hash, CodeSize, Address, ContinuationAddressOffset);
}

internal sealed class StagePrewarmRecord
{
    public required ShaderStage Stage { get; init; }
    public required (ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset) CodeKey { get; init; }
    public required RegisteredShader Registered { get; init; }
    public required ulong Hash { get; init; }
    public required uint UserDataBase { get; init; }
    public required uint UserDataCount { get; init; }
    public required uint PushDataCursor { get; init; }
    public required StageCompileOptions Options { get; init; }
    public required ResourceSpecialization Specialization { get; init; }
}

internal sealed class GraphicsPrewarmRecord
{
    public required ulong VertexStage { get; init; }
    public required ulong PixelStage { get; init; }
    public required PipelineRenderingState Rendering { get; init; }
    public required PipelineVertexInputState VertexInput { get; init; }
    public required VertexInputInfo VertexInfo { get; init; }
    public required PipelineStaticParameters StaticParameters { get; init; }
}

internal sealed record PrewarmStage(ulong Identity, StagePrewarmRecord Record, ShaderCodeCapture Code);

internal sealed record GraphicsPrewarmItem(ulong Identity, GraphicsPrewarmRecord Record, PrewarmStage Vertex, PrewarmStage? Pixel);

internal sealed class PrewarmRecordFile : IDisposable
{
    private const int RecordHeaderBytes = sizeof(uint) + sizeof(ulong);

    private readonly FileStream _file;
    private readonly uint _magic;
    private readonly uint _version;
    private readonly uint _fingerprint;

    private PrewarmRecordFile(FileStream file, uint magic, uint version, uint fingerprint)
    {
        _file = file;
        _magic = magic;
        _version = version;
        _fingerprint = fingerprint;
    }

    public bool Failed { get; private set; }
    public string Coverage { get; private set; } = string.Empty;

    public static PrewarmRecordFile Open(string path, uint magic, uint version, uint fingerprint, Func<byte, byte[], bool> loadRecord)
    {
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var records = new PrewarmRecordFile(file, magic, version, fingerprint);
        try
        {
            records.Load(loadRecord);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        return records;
    }

    public void Append(byte kind, byte[] payload)
    {
        if (Failed)
        {
            return;
        }

        try
        {
            var record = new byte[RecordHeaderBytes + 1 + payload.Length];
            record[RecordHeaderBytes] = kind;
            payload.CopyTo(record, RecordHeaderBytes + 1);
            var body = record.AsSpan(RecordHeaderBytes);
            BitConverter.TryWriteBytes(record.AsSpan(0, sizeof(uint)), (uint)body.Length);
            BitConverter.TryWriteBytes(record.AsSpan(sizeof(uint), sizeof(ulong)), XxHash3.HashToUInt64(body));
            _file.Write(record);
            _file.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Failed = true;
            Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm list write failed: {exception.Message}");
        }
    }

    public void Dispose()
    {
        Failed = true;
        _file.Dispose();
    }

    private void Load(Func<byte, byte[], bool> loadRecord)
    {
        var content = new byte[_file.Length];
        _file.Position = 0;
        _file.ReadExactly(content);
        var validEnd = 0L;
        if (content.Length >= 3 * sizeof(uint) &&
            BitConverter.ToUInt32(content, 0) == _magic &&
            BitConverter.ToUInt32(content, sizeof(uint)) == _version &&
            BitConverter.ToUInt32(content, 2 * sizeof(uint)) == _fingerprint)
        {
            validEnd = 3 * sizeof(uint);
            var offset = (int)validEnd;
            while (content.Length - offset >= RecordHeaderBytes + 1)
            {
                var length = BitConverter.ToUInt32(content, offset);
                var checksum = BitConverter.ToUInt64(content, offset + sizeof(uint));
                if (length == 0 || length > content.Length - offset - RecordHeaderBytes)
                {
                    break;
                }

                var body = content.AsSpan(offset + RecordHeaderBytes, (int)length);
                if (XxHash3.HashToUInt64(body) != checksum || !loadRecord(body[0], body[1..].ToArray()))
                {
                    break;
                }

                offset += RecordHeaderBytes + (int)length;
                validEnd = offset;
            }
        }

        Coverage = Convert.ToHexString(SHA256.HashData(content.AsSpan(0, checked((int)validEnd))));
        if (validEnd == 0)
        {
            _file.SetLength(0);
            _file.Position = 0;
            Span<byte> header = stackalloc byte[3 * sizeof(uint)];
            BitConverter.TryWriteBytes(header, _magic);
            BitConverter.TryWriteBytes(header[sizeof(uint)..], _version);
            BitConverter.TryWriteBytes(header[(2 * sizeof(uint))..], _fingerprint);
            _file.Write(header);
            _file.Flush();
            return;
        }

        _file.SetLength(validEnd);
        _file.Position = validEnd;
    }
}

internal sealed class ShaderPrewarmList : IDisposable
{
    public const string FileName = "shader-prewarm.bin";
    public const string GraphicsFileName = "shader-prewarm-graphics.bin";
    public const string StampFileName = "shader-prewarm.stamp";
    public const string ProgressFileName = "shader-prewarm.progress";

    private const uint Magic = 0x57504553;
    private const uint GraphicsMagic = 0x47504553;
    private const uint FormatVersion = 4;
    private const uint GraphicsFormatVersion = 1;
    private const byte CodeKind = 1;
    private const byte ComputeKind = 2;
    private const byte StageKind = 3;
    private const byte GraphicsKind = 4;

    private readonly object _gate = new();
    private PrewarmRecordFile? _computeFile;
    private PrewarmRecordFile? _graphicsFile;
    private readonly string _stampPath;
    private readonly string _progressPath;
    private readonly object _progressGate = new();
    private readonly HashSet<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset)> _codes = [];
    private readonly HashSet<ulong> _computes = [];
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset), ShaderCodeCapture> _loadedCodes = new();
    private readonly List<ComputePrewarmRecord> _loadedComputes = [];
    private readonly HashSet<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset)> _graphicsCodes = [];
    private readonly HashSet<ulong> _stages = [];
    private readonly HashSet<ulong> _graphics = [];
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset), ShaderCodeCapture> _loadedGraphicsCodes = new();
    private readonly Dictionary<ulong, StagePrewarmRecord> _loadedStages = new();
    private readonly List<(ulong Identity, GraphicsPrewarmRecord Record)> _loadedGraphics = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, ulong> _stageByModule = new();
    private bool _failed;

    private ShaderPrewarmList(string stampPath, string progressPath)
    {
        _stampPath = stampPath;
        _progressPath = progressPath;
    }

    public int LoadedComputeCount => _loadedComputes.Count;
    public int LoadedGraphicsCount => _loadedGraphics.Count;
    public string StampPath => _stampPath;

    public string CoverageStamp(string stamp) =>
        $"{stamp}|coverage-v1|{_computeFile?.Coverage}|graphics-v1|{_graphicsFile?.Coverage}";

    public static string CacheStamp(string coverageStamp, ReadOnlySpan<byte> cache) =>
        $"{coverageStamp}|cache-v1|{Convert.ToHexString(SHA256.HashData(cache))}";

    public static ShaderPrewarmList? Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var list = new ShaderPrewarmList(Path.Combine(directory, StampFileName), Path.Combine(directory, ProgressFileName));
            list._computeFile = PrewarmRecordFile.Open(
                Path.Combine(directory, FileName), Magic, FormatVersion, SchemaFingerprint, list.TryLoadRecord);
            try
            {
                list._graphicsFile = PrewarmRecordFile.Open(
                    Path.Combine(directory, GraphicsFileName), GraphicsMagic, GraphicsFormatVersion, GraphicsSchemaFingerprint,
                    list.TryLoadGraphicsRecord);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Graphics shader prewarm list unavailable: {exception.Message}");
            }

            return list;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm list unavailable: {exception.Message}");
            return null;
        }
    }

    public IReadOnlyList<(ComputePrewarmRecord Record, ShaderCodeCapture Code)> LoadedComputes()
    {
        var result = new List<(ComputePrewarmRecord, ShaderCodeCapture)>(_loadedComputes.Count);
        foreach (var record in _loadedComputes)
        {
            if (_loadedCodes.TryGetValue(record.CodeKey, out var code))
            {
                result.Add((record, code));
            }
        }

        return result;
    }

    public IReadOnlyList<GraphicsPrewarmItem> LoadedGraphics()
    {
        var stages = new Dictionary<ulong, PrewarmStage?>();
        PrewarmStage? Stage(ulong identity)
        {
            if (!stages.TryGetValue(identity, out var stage))
            {
                stage = _loadedStages.TryGetValue(identity, out var record) &&
                    _loadedGraphicsCodes.TryGetValue(record.CodeKey, out var code)
                    ? new PrewarmStage(identity, record, code)
                    : null;
                stages.Add(identity, stage);
            }

            return stage;
        }

        var result = new List<GraphicsPrewarmItem>(_loadedGraphics.Count);
        foreach (var (identity, record) in _loadedGraphics)
        {
            var pixel = record.PixelStage == 0 ? null : Stage(record.PixelStage);
            if (Stage(record.VertexStage) is { } vertex && (record.PixelStage == 0 || pixel is not null))
            {
                result.Add(new GraphicsPrewarmItem(identity, record, vertex, pixel));
            }
        }

        return result;
    }

    public bool IsStampCurrent(string stamp)
    {
        try
        {
            return File.Exists(_stampPath) && File.ReadAllText(_stampPath) == stamp;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void WriteStamp(string stamp)
        => WriteStamp(_stampPath, stamp);

    public static void WriteStamp(string path, string stamp)
    {
        try
        {
            var temporaryPath = path + $".{Environment.ProcessId}.tmp";
            File.WriteAllText(temporaryPath, stamp);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm stamp write failed: {exception.Message}");
        }
    }

    public static ulong Identity(ComputePrewarmRecord record) => XxHash3.HashToUInt64(SerializeCompute(record));

    public HashSet<ulong> ReadProgress(string stamp)
    {
        var completed = new HashSet<ulong>();
        lock (_progressGate)
        {
            try
            {
                if (!File.Exists(_progressPath))
                {
                    return completed;
                }

                var lines = File.ReadAllLines(_progressPath);
                if (lines.Length == 0 || lines[0] != ProgressKey(stamp))
                {
                    File.Delete(_progressPath);
                    return completed;
                }

                for (var index = 1; index < lines.Length; index++)
                {
                    if (ulong.TryParse(lines[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var identity))
                    {
                        completed.Add(identity);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                completed.Clear();
            }
        }

        return completed;
    }

    public void AppendProgress(string stamp, IReadOnlyCollection<ulong> identities)
    {
        if (identities.Count == 0)
        {
            return;
        }

        lock (_progressGate)
        {
            try
            {
                var lines = new List<string>(identities.Count + 1);
                if (!File.Exists(_progressPath))
                {
                    lines.Add(ProgressKey(stamp));
                }

                foreach (var identity in identities)
                {
                    lines.Add(identity.ToString("X16", CultureInfo.InvariantCulture));
                }

                File.AppendAllLines(_progressPath, lines);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm progress write failed: {exception.Message}");
            }
        }
    }

    public void ClearProgress()
    {
        lock (_progressGate)
        {
            try
            {
                File.Delete(_progressPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm progress clear failed: {exception.Message}");
            }
        }
    }

    private static string ProgressKey(string stamp) =>
        XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(stamp)).ToString("X16", CultureInfo.InvariantCulture);

    public void RecordCompute(ShaderCodeCapture code, ComputePrewarmRecord record)
    {
        var payload = SerializeCompute(record);
        var identity = XxHash3.HashToUInt64(payload);
        lock (_gate)
        {
            if (_failed || _computeFile is not { } file || !_computes.Add(identity))
            {
                return;
            }

            if (_codes.Add(code.Key))
            {
                file.Append(CodeKind, SerializeCode(code));
            }

            file.Append(ComputeKind, payload);
        }
    }

    public void RecordStage(ShaderCodeCapture code, ulong module, StagePrewarmRecord record)
    {
        var payload = SerializeStage(record);
        var identity = XxHash3.HashToUInt64(payload);
        _stageByModule[module] = identity;
        lock (_gate)
        {
            if (_failed || _graphicsFile is not { } file || !_stages.Add(identity))
            {
                return;
            }

            if (_graphicsCodes.Add(code.Key))
            {
                file.Append(CodeKind, SerializeCode(code));
            }

            file.Append(StageKind, payload);
        }
    }

    public void RecordGraphics(GraphicsPipelineDescription description)
    {
        var pixel = 0UL;
        if (!_stageByModule.TryGetValue(description.VertexProgram.Module, out var vertex) ||
            (description.PixelStage is not null && !_stageByModule.TryGetValue(description.PixelProgram.Module, out pixel)))
        {
            return;
        }

        var payload = SerializeGraphics(new GraphicsPrewarmRecord
        {
            VertexStage = vertex,
            PixelStage = pixel,
            Rendering = description.Rendering,
            VertexInput = description.VertexInput,
            VertexInfo = description.VertexInfo,
            StaticParameters = description.StaticParameters,
        });
        var identity = XxHash3.HashToUInt64(payload);
        lock (_gate)
        {
            if (_failed || _graphicsFile is not { } file || !_graphics.Add(identity))
            {
                return;
            }

            file.Append(GraphicsKind, payload);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _failed = true;
            _computeFile?.Dispose();
            _graphicsFile?.Dispose();
        }
    }

    private bool TryLoadGraphicsRecord(byte kind, byte[] payload)
    {
        try
        {
            switch (kind)
            {
                case CodeKind:
                {
                    var code = DeserializeCode(payload);
                    _graphicsCodes.Add(code.Key);
                    _loadedGraphicsCodes[code.Key] = code;
                    return true;
                }

                case StageKind:
                {
                    var identity = XxHash3.HashToUInt64(payload);
                    if (_stages.Add(identity))
                    {
                        _loadedStages[identity] = DeserializeStage(payload);
                    }

                    return true;
                }

                case GraphicsKind:
                {
                    var identity = XxHash3.HashToUInt64(payload);
                    if (_graphics.Add(identity))
                    {
                        _loadedGraphics.Add((identity, DeserializeGraphics(payload)));
                    }

                    return true;
                }

                default:
                    return false;
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
        {
            return false;
        }
    }

    private bool TryLoadRecord(byte kind, byte[] payload)
    {
        try
        {
            switch (kind)
            {
                case CodeKind:
                {
                    var code = DeserializeCode(payload);
                    _codes.Add(code.Key);
                    _loadedCodes[code.Key] = code;
                    return true;
                }

                case ComputeKind:
                    if (_computes.Add(XxHash3.HashToUInt64(payload)))
                    {
                        _loadedComputes.Add(DeserializeCompute(payload));
                    }

                    return true;

                default:
                    return false;
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
        {
            return false;
        }
    }

    private static readonly uint SchemaFingerprint = ComputeSchemaFingerprint(
        typeof(ComputeInputInfo), typeof(Gen5ComputeSystemRegisters), typeof(ResourceSpecialization),
        typeof(BufferSpecialization), typeof(ImageSpecialization), typeof(BufferCandidateTableSpecialization));

    private static readonly uint GraphicsSchemaFingerprint = ComputeSchemaFingerprint(
        typeof(StageCompileOptions), typeof(VertexInputInfo), typeof(VertexInputBuffer), typeof(VertexAttributeResource),
        typeof(ClipSpaceTransform), typeof(PixelInputInfo), typeof(Gen5PixelOutputBinding), typeof(RegisteredShader),
        typeof(PipelineRenderingState), typeof(PipelineVertexInputState), typeof(PipelineStaticParameters),
        typeof(ResourceSpecialization), typeof(BufferSpecialization), typeof(ImageSpecialization),
        typeof(BufferCandidateTableSpecialization));

    private static uint ComputeSchemaFingerprint(params Type[] types)
    {
        var builder = new StringBuilder();
        foreach (var type in types)
        {
            builder.Append(type.FullName).Append('{');
            foreach (var property in type.GetProperties().OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                builder.Append(property.PropertyType.FullName).Append(' ').Append(property.Name).Append(';');
            }

            builder.Append('}');
        }

        return (uint)XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static byte[] SerializeCode(ShaderCodeCapture code)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
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

        if (code.FrontendDomain != 0)
        {
            writer.Write(code.FrontendDomain);
            writer.Write(code.FrontendUserData.Length);
            foreach (var word in code.FrontendUserData)
            {
                writer.Write(word);
            }

            writer.Write(code.FrontendContinuationOffset);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static ShaderCodeCapture DeserializeCode(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var hash = reader.ReadUInt64();
        var codeSize = reader.ReadUInt32();
        var address = reader.ReadUInt64();
        var generation = (Generation)reader.ReadInt32();
        FusedCodeParts? fused = reader.ReadBoolean()
            ? new FusedCodeParts(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64())
            : null;
        var ranges = new CodeRange[CheckedCount(reader.ReadInt32(), payload.Length)];
        for (var index = 0; index < ranges.Length; index++)
        {
            var rangeAddress = reader.ReadUInt64();
            var bytes = reader.ReadBytes(CheckedCount(reader.ReadInt32(), payload.Length));
            ranges[index] = new CodeRange(rangeAddress, bytes);
        }

        var frontendDomain = 0UL;
        uint[] frontendUserData = [];
        var frontendContinuationOffset = 0UL;
        if (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            frontendDomain = reader.ReadUInt64();
            frontendUserData = new uint[CheckedCount(reader.ReadInt32(), payload.Length)];
            for (var index = 0; index < frontendUserData.Length; index++)
            {
                frontendUserData[index] = reader.ReadUInt32();
            }

            frontendContinuationOffset = reader.ReadUInt64();
        }

        return new ShaderCodeCapture
        {
            Hash = hash,
            CodeSize = codeSize,
            Address = address,
            Generation = generation,
            Fused = fused,
            Ranges = ranges,
            FrontendDomain = frontendDomain,
            FrontendUserData = frontendUserData,
            FrontendContinuationOffset = frontendContinuationOffset,
        };
    }

    private static byte[] SerializeCompute(ComputePrewarmRecord record)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(record.Hash);
        writer.Write(record.CodeSize);
        writer.Write(record.Address);
        writer.Write(record.ContinuationAddressOffset);
        writer.Write(record.UserDataBase);
        writer.Write(record.UserDataCount);
        writer.Write(record.PushDataCursor);

        var info = record.Info;
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
        writer.Write(info.IeeeMode);
        writer.Write(info.NeedsLocalDataShareBarriers);
        writer.Write(info.WorkgroupRegister);

        writer.Write(record.SystemRegisters is not null);
        if (record.SystemRegisters is { } registers)
        {
            WriteOptional(writer, registers.WorkGroupXRegister);
            WriteOptional(writer, registers.WorkGroupYRegister);
            WriteOptional(writer, registers.WorkGroupZRegister);
            WriteOptional(writer, registers.ThreadGroupSizeRegister);
        }

        WriteSpecialization(writer, record.Specialization);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteSpecialization(BinaryWriter writer, ResourceSpecialization specialization)
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
            writer.Write(image.MipStatistics);
        }

        writer.Write(specialization.BufferCandidateTables.Count);
        foreach (var table in specialization.BufferCandidateTables)
        {
            writer.Write(table.FirstCandidate);
            writer.Write(table.CandidateCount);
            writer.Write(table.MappingOffset);
            writer.Write(table.SearchIterations);
        }

        WriteWords(writer, specialization.ImageDescriptorGroups);
        WriteWords(writer, specialization.SamplerDescriptorGroups);
    }

    private static ResourceSpecialization ReadSpecialization(BinaryReader reader, int limit)
    {
        var specialization = new ResourceSpecialization { BaseBufferCount = reader.ReadInt32() };
        for (var count = CheckedCount(reader.ReadInt32(), limit); count > 0; count--)
        {
            specialization.Buffers.Add(new BufferSpecialization(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        }

        for (var count = CheckedCount(reader.ReadInt32(), limit); count > 0; count--)
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
                reader.ReadInt32(),
                reader.ReadBoolean()));
        }

        for (var count = CheckedCount(reader.ReadInt32(), limit); count > 0; count--)
        {
            specialization.BufferCandidateTables.Add(new BufferCandidateTableSpecialization(
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        }

        specialization.ImageDescriptorGroups.AddRange(ReadWords(reader, limit));
        specialization.SamplerDescriptorGroups.AddRange(ReadWords(reader, limit));
        return specialization;
    }

    private static void WriteWords(BinaryWriter writer, IReadOnlyCollection<uint> words)
    {
        writer.Write(words.Count);
        foreach (var word in words)
        {
            writer.Write(word);
        }
    }

    private static uint[] ReadWords(BinaryReader reader, int limit)
    {
        var words = new uint[CheckedCount(reader.ReadInt32(), limit)];
        for (var index = 0; index < words.Length; index++)
        {
            words[index] = reader.ReadUInt32();
        }

        return words;
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader, int limit) => reader.ReadBytes(CheckedCount(reader.ReadInt32(), limit));

    private static byte[] SerializeStage(StagePrewarmRecord record)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)record.Stage);
        writer.Write(record.CodeKey.Hash);
        writer.Write(record.CodeKey.CodeSize);
        writer.Write(record.CodeKey.Address);
        writer.Write(record.CodeKey.ContinuationAddressOffset);

        var shader = record.Registered;
        writer.Write(shader.CodeAddress);
        writer.Write(shader.CodeSizeBytes);
        writer.Write(shader.ScratchDwords);
        writer.Write(shader.ContinuationAddress);
        writer.Write(shader.ContinuationSizeBytes);
        writer.Write(shader.ContinuationScratchDwords);
        writer.Write(shader.AdditionalRanges.Length);
        foreach (var (address, sizeBytes) in shader.AdditionalRanges)
        {
            writer.Write(address);
            writer.Write(sizeBytes);
        }

        writer.Write(record.Hash);
        writer.Write(record.UserDataBase);
        writer.Write(record.UserDataCount);
        writer.Write(record.PushDataCursor);

        var options = record.Options;
        writer.Write(options.RequiredVertexOutputCount);
        writer.Write(options.VertexInfo is not null);
        if (options.VertexInfo is { } vertex)
        {
            WriteVertexInfo(writer, vertex);
        }

        writer.Write(options.PixelInfo is not null);
        if (options.PixelInfo is { } pixel)
        {
            WritePixelInfo(writer, pixel);
        }

        writer.Write(options.PixelOutputs.Count);
        foreach (var output in options.PixelOutputs)
        {
            writer.Write(output.GuestSlot);
            writer.Write(output.HostLocation);
            writer.Write((int)output.Kind);
            writer.Write(output.ComponentMapping.Packed);
            writer.Write(output.DualSourceAlpha);
            writer.Write(output.ExportTarget);
            writer.Write(output.Index);
            writer.Write((byte)output.ExportFormat);
        }

        writer.Write(options.PixelInputEnable);
        writer.Write(options.PixelInputAddress);
        WriteSpecialization(writer, record.Specialization);
        writer.Flush();
        return stream.ToArray();
    }

    private static StagePrewarmRecord DeserializeStage(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var limit = payload.Length;
        var stage = (ShaderStage)reader.ReadByte();
        if (stage is not (ShaderStage.Vertex or ShaderStage.Pixel))
        {
            throw new ArgumentException("The prewarm stage is not a vertex or pixel stage.");
        }

        var codeKey = (reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt64());
        var codeAddress = reader.ReadUInt64();
        var codeSizeBytes = reader.ReadUInt32();
        var scratchDwords = reader.ReadUInt32();
        var continuationAddress = reader.ReadUInt64();
        var continuationSizeBytes = reader.ReadUInt32();
        var continuationScratchDwords = reader.ReadUInt32();
        var additionalRanges = new (ulong Address, uint SizeBytes)[CheckedCount(reader.ReadInt32(), limit)];
        for (var index = 0; index < additionalRanges.Length; index++)
        {
            additionalRanges[index] = (reader.ReadUInt64(), reader.ReadUInt32());
        }

        var registered = new RegisteredShader(codeAddress, 0, codeSizeBytes, scratchDwords, 0, 0, 0, continuationAddress, continuationSizeBytes)
        {
            ContinuationScratchDwords = continuationScratchDwords,
            AdditionalRanges = additionalRanges,
        };
        var hash = reader.ReadUInt64();
        var userDataBase = reader.ReadUInt32();
        var userDataCount = reader.ReadUInt32();
        var pushDataCursor = reader.ReadUInt32();
        var requiredVertexOutputCount = reader.ReadInt32();
        var vertexInfo = reader.ReadBoolean() ? ReadVertexInfo(reader, limit) : null;
        var pixelInfo = reader.ReadBoolean() ? ReadPixelInfo(reader, limit) : null;
        var outputs = new Gen5PixelOutputBinding[CheckedCount(reader.ReadInt32(), limit)];
        for (var index = 0; index < outputs.Length; index++)
        {
            outputs[index] = new Gen5PixelOutputBinding(
                reader.ReadUInt32(), reader.ReadUInt32(), (Gen5PixelOutputKind)reader.ReadInt32(), new Gen5ColorComponentMapping(reader.ReadByte()))
            {
                DualSourceAlpha = reader.ReadBoolean(),
                ExportTarget = reader.ReadUInt32(),
                Index = reader.ReadUInt32(),
                ExportFormat = (Gen5PixelExportFormat)reader.ReadByte(),
            };
        }

        var options = new StageCompileOptions
        {
            VertexInfo = vertexInfo,
            RequiredVertexOutputCount = requiredVertexOutputCount,
            PixelInfo = pixelInfo,
            PixelOutputs = outputs,
            PixelInputEnable = reader.ReadUInt32(),
            PixelInputAddress = reader.ReadUInt32(),
        };
        if ((stage == ShaderStage.Vertex && vertexInfo is null) || (stage == ShaderStage.Pixel && pixelInfo is null))
        {
            throw new ArgumentException("The prewarm stage has no input info.");
        }

        return new StagePrewarmRecord
        {
            Stage = stage,
            CodeKey = codeKey,
            Registered = registered,
            Hash = hash,
            UserDataBase = userDataBase,
            UserDataCount = userDataCount,
            PushDataCursor = pushDataCursor,
            Options = options,
            Specialization = ReadSpecialization(reader, limit),
        };
    }

    private static void WriteVertexInfo(BinaryWriter writer, VertexInputInfo info)
    {
        writer.Write(info.Buffers.Length);
        foreach (var buffer in info.Buffers)
        {
            writer.Write(buffer.Stride);
            writer.Write(buffer.PerInstance);
        }

        writer.Write(info.Attributes.Length);
        foreach (var attribute in info.Attributes)
        {
            writer.Write(attribute.Descriptor.Word1 & 0xFFFF_0000u);
            writer.Write(attribute.Descriptor.Word3);
            writer.Write(attribute.RegisterStart);
            writer.Write(attribute.RegisterCount);
            writer.Write(attribute.AttributeId);
            writer.Write(attribute.FetchIndex);
            writer.Write(attribute.BufferIndex);
            writer.Write(attribute.OffsetBytes);
        }

        writer.Write(info.FetchEmbedded);
        writer.Write(info.FetchAttributeRegister);
        writer.Write(info.FetchBufferRegister);
        writer.Write(info.IeeeMode);
        writer.Write(info.ScratchDwords);
        writer.Write(info.PositionExportControl);
        var clip = info.ClipSpace;
        writer.Write(clip.Enabled);
        writer.Write(clip.ScaleX);
        writer.Write(clip.ScaleY);
        writer.Write(clip.OffsetX);
        writer.Write(clip.OffsetY);
        writer.Write(clip.HalfExtentX);
        writer.Write(clip.HalfExtentY);
        writer.Write(info.StartInstanceUserData);
    }

    private static VertexInputInfo ReadVertexInfo(BinaryReader reader, int limit)
    {
        var buffers = new VertexInputBuffer[CheckedCount(reader.ReadInt32(), limit)];
        for (var index = 0; index < buffers.Length; index++)
        {
            buffers[index] = new VertexInputBuffer(0, reader.ReadUInt32(), 0, reader.ReadBoolean());
        }

        var attributes = new VertexAttributeResource[CheckedCount(reader.ReadInt32(), limit)];
        for (var index = 0; index < attributes.Length; index++)
        {
            attributes[index] = new VertexAttributeResource(
                new BufferDescriptorWords(0, reader.ReadUInt32(), 0, reader.ReadUInt32()),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadUInt32(),
                reader.ReadInt32(),
                reader.ReadUInt32());
        }

        return new VertexInputInfo
        {
            Buffers = buffers,
            Attributes = attributes,
            FetchEmbedded = reader.ReadBoolean(),
            FetchAttributeRegister = reader.ReadInt32(),
            FetchBufferRegister = reader.ReadInt32(),
            IeeeMode = reader.ReadBoolean(),
            ScratchDwords = reader.ReadUInt32(),
            PositionExportControl = reader.ReadUInt32(),
            ClipSpace = new ClipSpaceTransform(
                reader.ReadBoolean(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            StartInstanceUserData = reader.ReadInt32(),
        };
    }

    private static void WritePixelInfo(BinaryWriter writer, PixelInputInfo info)
    {
        writer.Write(info.InputCount);
        writer.Write(info.SystemInputBase);
        writer.Write(info.CustomInterpolationMask);
        writer.Write(info.PerspectiveCenterRegister);
        WriteWords(writer, info.InterpolatorSettings);
        WriteBytes(writer, info.TargetOutputModes);
        WriteBytes(writer, info.TargetExportFormats);
        WriteBytes(writer, info.TargetExportMappings.Select(static mapping => mapping.Packed).ToArray());
        writer.Write(info.IeeeMode);
        writer.Write(info.ScratchDwords);
        writer.Write(info.PositionX);
        writer.Write(info.PositionY);
        writer.Write(info.PositionZ);
        writer.Write(info.PositionW);
        writer.Write(info.FrontFace);
        writer.Write(info.NoPerspective);
        writer.Write(info.KillEnable);
        writer.Write(info.DepthExportEnable);
        writer.Write(info.SampleMaskExportEnable);
        writer.Write(info.SampleShading);
        writer.Write(info.EarlyDepth);
        writer.Write(info.ExecuteOnNoop);
    }

    private static PixelInputInfo ReadPixelInfo(BinaryReader reader, int limit) => new()
    {
        InputCount = reader.ReadUInt32(),
        SystemInputBase = reader.ReadUInt32(),
        CustomInterpolationMask = reader.ReadUInt32(),
        PerspectiveCenterRegister = reader.ReadUInt32(),
        InterpolatorSettings = ReadWords(reader, limit),
        TargetOutputModes = ReadBytes(reader, limit),
        TargetExportFormats = ReadBytes(reader, limit),
        TargetExportMappings = ReadBytes(reader, limit).Select(static packed => new ColorComponentMap(packed)).ToArray(),
        IeeeMode = reader.ReadBoolean(),
        ScratchDwords = reader.ReadUInt32(),
        PositionX = reader.ReadBoolean(),
        PositionY = reader.ReadBoolean(),
        PositionZ = reader.ReadBoolean(),
        PositionW = reader.ReadBoolean(),
        FrontFace = reader.ReadBoolean(),
        NoPerspective = reader.ReadBoolean(),
        KillEnable = reader.ReadBoolean(),
        DepthExportEnable = reader.ReadBoolean(),
        SampleMaskExportEnable = reader.ReadBoolean(),
        SampleShading = reader.ReadBoolean(),
        EarlyDepth = reader.ReadBoolean(),
        ExecuteOnNoop = reader.ReadBoolean(),
    };

    private static byte[] SerializeGraphics(GraphicsPrewarmRecord record)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(record.VertexStage);
        writer.Write(record.PixelStage);
        var rendering = record.Rendering;
        writer.Write(rendering.ColorCount);
        foreach (var format in rendering.ColorFormats)
        {
            writer.Write((int)format);
        }

        writer.Write((int)rendering.DepthFormat);
        writer.Write((int)rendering.StencilFormat);
        var input = record.VertexInput;
        writer.Write(input.BindingCount);
        for (var index = 0; index < input.BindingCount; index++)
        {
            writer.Write(input.Bindings[index].Stride);
            writer.Write(input.Bindings[index].Instance);
        }

        writer.Write(input.AttributeCount);
        for (var index = 0; index < input.AttributeCount; index++)
        {
            writer.Write(input.Attributes[index].Offset);
            writer.Write(input.Attributes[index].Binding);
        }

        WriteVertexInfo(writer, record.VertexInfo);
        writer.Write(record.StaticParameters.Bytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static GraphicsPrewarmRecord DeserializeGraphics(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var vertexStage = reader.ReadUInt64();
        var pixelStage = reader.ReadUInt64();
        var rendering = new PipelineRenderingState { ColorCount = reader.ReadUInt32() };
        if (rendering.ColorCount > PipelineStaticParameters.ColorAttachmentCount)
        {
            throw new ArgumentException("The prewarm pipeline has too many color attachments.");
        }

        for (var index = 0; index < rendering.ColorFormats.Length; index++)
        {
            rendering.ColorFormats[index] = (Silk.NET.Vulkan.Format)reader.ReadInt32();
        }

        rendering.DepthFormat = (Silk.NET.Vulkan.Format)reader.ReadInt32();
        rendering.StencilFormat = (Silk.NET.Vulkan.Format)reader.ReadInt32();
        var input = new PipelineVertexInputState { BindingCount = reader.ReadByte() };
        CheckedCount(input.BindingCount, VertexInputInfo.MaxBuffers);
        for (var index = 0; index < input.BindingCount; index++)
        {
            input.Bindings[index] = new PipelineVertexBinding(reader.ReadUInt32(), reader.ReadBoolean());
        }

        input.AttributeCount = reader.ReadByte();
        CheckedCount(input.AttributeCount, VertexInputInfo.MaxBuffers);
        for (var index = 0; index < input.AttributeCount; index++)
        {
            input.Attributes[index] = new PipelineVertexAttribute(reader.ReadUInt32(), reader.ReadByte());
        }

        var vertexInfo = ReadVertexInfo(reader, payload.Length);
        var parameters = reader.ReadBytes(PipelineStaticParameters.ByteSize);
        if (parameters.Length != PipelineStaticParameters.ByteSize)
        {
            throw new EndOfStreamException();
        }

        return new GraphicsPrewarmRecord
        {
            VertexStage = vertexStage,
            PixelStage = pixelStage,
            Rendering = rendering,
            VertexInput = input,
            VertexInfo = vertexInfo,
            StaticParameters = PipelineStaticParameters.FromBytes(parameters),
        };
    }

    private static ComputePrewarmRecord DeserializeCompute(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var hash = reader.ReadUInt64();
        var codeSize = reader.ReadUInt32();
        var address = reader.ReadUInt64();
        var continuationAddressOffset = reader.ReadUInt64();
        var userDataBase = reader.ReadUInt32();
        var userDataCount = reader.ReadUInt32();
        var pushDataCursor = reader.ReadUInt32();
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
            IeeeMode = reader.ReadBoolean(),
            NeedsLocalDataShareBarriers = reader.ReadBoolean(),
            WorkgroupRegister = reader.ReadInt32(),
        };

        Gen5ComputeSystemRegisters? registers = reader.ReadBoolean()
            ? new Gen5ComputeSystemRegisters(ReadOptional(reader), ReadOptional(reader), ReadOptional(reader), ReadOptional(reader))
            : null;

        var specialization = ReadSpecialization(reader, payload.Length);
        return new ComputePrewarmRecord
        {
            Hash = hash,
            CodeSize = codeSize,
            Address = address,
            UserDataBase = userDataBase,
            ContinuationAddressOffset = continuationAddressOffset,
            UserDataCount = userDataCount,
            PushDataCursor = pushDataCursor,
            Info = info,
            SystemRegisters = registers,
            Specialization = specialization,
        };
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

    private static int CheckedCount(int count, int limit) =>
        count >= 0 && count <= limit ? count : throw new ArgumentException("The prewarm record count is out of range.");
}
