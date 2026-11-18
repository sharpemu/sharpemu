// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
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
    public LinkedShaderPrewarm? Linked { get; init; }
    public bool DeviceRecordCounts { get; init; }
    // The program's image table selects more images than a draw can bind, so its plan reads them from the bindless heap
    // (ShaderProgramCache.PreferHeapRecords). The prewarm plans it the same way, or its compile would never be reused.
    public bool PreferHeapRecords { get; init; }

    public (ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset) CodeKey =>
        (Hash, CodeSize, Address, ContinuationAddressOffset);
}

// A pixel program a session translated, with what its translation read besides the code. A later build's prewarm
// translates it again and compiles its pixel library, so the program's first draw links a stored library instead of
// compiling one while the command stream waits.
internal sealed class PixelPrewarmRecord
{
    public ulong ContinuationAddressOffset { get; init; }
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required uint UserDataBase { get; init; }
    public required uint UserDataCount { get; init; }
    public required uint PushDataCursor { get; init; }
    public required PixelInputInfo Info { get; init; }
    public required Gen5PixelOutputBinding[] Outputs { get; init; }
    public required uint InputEnable { get; init; }
    public required uint InputAddress { get; init; }
    public required ResourceSpecialization Specialization { get; init; }
    public bool DeviceRecordCounts { get; init; }
    // The program's image table selects more images than a draw can bind, so its plan reads them from the bindless heap
    // (ShaderProgramCache.PreferHeapRecords). The prewarm plans it the same way, or its compile would never be reused.
    public bool PreferHeapRecords { get; init; }

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
    public bool PreferHeapRecords { get; init; }
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

// A whole graphics pipeline a session created, with the stage programs it links; the prewarm builds it again on a
// device that creates whole pipelines (the graphics prewarm otherwise builds pipeline library parts).
internal sealed record PipelinePrewarmItem(GraphicsPrewarmRecord Record, PrewarmStage Vertex, PrewarmStage? Pixel, ulong Identity)
    : GraphicsPrewarmItem(Identity);

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

internal sealed partial class ShaderPrewarmList : IDisposable
{
    public const string FileName = "shader-prewarm.bin";
    public const string StampFileName = "shader-prewarm.stamp";
    public const string ProgressFileName = "shader-prewarm.progress";
    // Records a build before them cannot read go to a side file such a build never opens: a build stops reading the main
    // list at the first record kind it does not know and cuts the file there, losing every record written after it.
    public const string ExtensionFileName = "shader-prewarm-ext.bin";
    // The side file's prewarms keep their own progress: the compute prewarm clears its progress when it cannot trust it.
    public const string ExtensionProgressFileName = "shader-prewarm-ext.progress";
    // Whole graphics pipelines (RecordGraphics) and the stage programs they link (RecordStage), in a file of their own.
    public const string GraphicsFileName = "shader-prewarm-graphics.bin";

    private const uint Magic = 0x57504553;
    private const uint FormatVersion = 6;
    private const byte CodeKind = 1;
    private const byte ComputeKind = 2;
    // Pixel prewarm records, in the side file (a main list written before the side file existed can hold some; they
    // still load).
    private const byte PixelKind = 3;
    // What a session learned about a linked program's module compile (side file).
    private const byte LinkedCompileKind = 4;
    // When a session first used a compute record (its identity, then the seconds into the session), in the side file,
    // since the main list's records carry no time. A session writes one for each record it uses unless a session used
    // that record sooner, and the prewarm runs records by their earliest use, so a record goes where a session needed
    // it rather than where the session that recorded it first met it. Records no session timed, and linked records,
    // whose permutations the prewarm takes newest first in list order (ShaderPrewarmQueue), keep list order after the
    // timed ones.
    private const byte ComputeFirstUseKind = 7;
    // How long a prewarm compile of a compute record took (its identity, then the seconds), in the side file, kept when
    // it took a second or more and longer than any compile recorded before. The prewarm runs each record by the time it
    // must start to be ready at its earliest use, first use minus compile time, so a long compile starts that much
    // sooner.
    private const byte ComputeCostKind = 8;
    private const float MinimumComputeCostSeconds = 1;
    // Kinds 7 and 8: a record identity, then seconds.
    private const int ComputeSecondsPayloadBytes = sizeof(ulong) + sizeof(float);
    // Kinds 5 and 6 (vertex prewarm records): ShaderPrewarmList.Vertex.cs.
    private const int LinkedCompilePayloadBytes = sizeof(ulong) + 1 + sizeof(long);
    private const uint ExtensionMagic = 0x58504553;
    // 2: graphics programs bind their images one per slot when the layout fits, so graphics records of version 1 would
    // translate differently. 3: pixel and pre-rasterization records end with the seconds into their session at which it
    // first met them. A side file of another version starts empty.
    private const uint ExtensionFormatVersion = 3;
    private const int ExtensionHeaderBytes = 2 * sizeof(uint);
    private const int RecordHeaderBytes = sizeof(uint) + sizeof(ulong);
    // The pipeline file's kinds besides CodeKind, a numbering of its own. 2: stage records hold their resource
    // specialization in the layout of the compute records (WriteSpecialization) and whether they read device record counts.
    private const uint GraphicsMagic = 0x47504553;
    private const uint GraphicsFormatVersion = 2;
    private const byte StageKind = 3;
    private const byte GraphicsKind = 4;

    private readonly object _gate = new();
    private readonly FileStream _file;
    private readonly FileStream _extension;
    private PrewarmRecordFile? _graphicsFile;
    private readonly string _stampPath;
    private readonly string _progressPath;
    private readonly string _extensionProgressPath;
    private readonly object _progressGate = new();
    private readonly HashSet<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset)> _codes = [];
    private readonly HashSet<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset)> _extensionCodes = [];
    // The last recorded outcome of each linked program's module compile (true: it compiled for minutes).
    private readonly ConcurrentDictionary<ulong, bool> _linkedCompiles = new();
    // The programs a record says plan their image tables as heap records, by hash, code size and continuation offset.
    private readonly ConcurrentDictionary<(ulong Hash, uint CodeSize, ulong ContinuationAddressOffset), byte> _heapRecordPrograms = new();
    private readonly HashSet<ulong> _computes = [];
    private readonly Dictionary<ulong, float> _computeFirstUses = new();
    private readonly Dictionary<ulong, float> _computeCosts = new();
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset), ShaderCodeCapture> _loadedCodes = new();
    private readonly List<ComputePrewarmRecord> _loadedComputes = [];
    private readonly HashSet<ulong> _pixels = [];
    private readonly List<PixelPrewarmRecord> _loadedPixels = [];
    private readonly HashSet<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset)> _graphicsCodes = [];
    private readonly HashSet<ulong> _stages = [];
    private readonly HashSet<ulong> _graphics = [];
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong Address, ulong ContinuationAddressOffset), ShaderCodeCapture> _loadedGraphicsCodes = new();
    private readonly Dictionary<ulong, StagePrewarmRecord> _loadedStages = new();
    private readonly List<(ulong Identity, GraphicsPrewarmRecord Record)> _loadedGraphics = [];
    private readonly ConcurrentDictionary<ulong, ulong> _stageByModule = new();
    private bool _failed;

    private ShaderPrewarmList(FileStream file, FileStream extension, string stampPath, string progressPath, string extensionProgressPath)
    {
        _file = file;
        _extension = extension;
        _extensionProgressPath = extensionProgressPath;
        _stampPath = stampPath;
        _progressPath = progressPath;
    }

    private string _loadedCoverage = string.Empty;

    public int LoadedComputeCount => _loadedComputes.Count;
    public int LoadedPixelCount => _loadedPixels.Count;
    public int LoadedPipelineCount => _loadedGraphics.Count;
    public string StampPath => _stampPath;

    // The main list's coverage; with pipelines also the pipeline file's, for a prewarm that builds whole pipelines.
    public string CoverageStamp(string stamp, bool pipelines = false) => pipelines
        ? $"{stamp}|coverage-v1|{_loadedCoverage}|graphics-v1|{_graphicsFile?.Coverage}"
        : $"{stamp}|coverage-v1|{_loadedCoverage}";

    public static string CacheStamp(string coverageStamp, ReadOnlySpan<byte> cache) =>
        $"{coverageStamp}|cache-v1|{Convert.ToHexString(SHA256.HashData(cache))}";

    public static ShaderPrewarmList? Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            FileStream extension;
            try
            {
                extension = new FileStream(Path.Combine(directory, ExtensionFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch
            {
                file.Dispose();
                throw;
            }

            var list = new ShaderPrewarmList(file, extension, Path.Combine(directory, StampFileName), Path.Combine(directory, ProgressFileName),
                Path.Combine(directory, ExtensionProgressFileName));
            try
            {
                list.Load();
            }
            catch
            {
                file.Dispose();
                extension.Dispose();
                throw;
            }

            try
            {
                list._graphicsFile = PrewarmRecordFile.Open(
                    Path.Combine(directory, GraphicsFileName), GraphicsMagic, GraphicsFormatVersion, GraphicsSchemaFingerprint,
                    list.TryLoadPipelineRecord);
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

    // The recorded compute programs in prewarm order (LoadedComputeStarts).
    public IReadOnlyList<(ComputePrewarmRecord Record, ShaderCodeCapture Code)> LoadedComputes() =>
        [.. LoadedComputeStarts().Select(static item => (item.Record, item.Code))];

    // The recorded compute programs by the time each must start to be ready at its earliest use (the recorded first use
    // minus the recorded compile time; infinite for an untimed record), then the untimed ones, each group in list
    // order. Sessions record first uses and the prewarm its compile times while the list is open, so the times are read
    // under the gate; the loaded records and codes are written only while the file loads.
    public IReadOnlyList<(ComputePrewarmRecord Record, ShaderCodeCapture Code, float StartBy)> LoadedComputeStarts()
    {
        var identities = _loadedComputes.Select(static record => Identity(record)).ToArray();
        var startBy = new float[identities.Length];
        lock (_gate)
        {
            for (var index = 0; index < identities.Length; index++)
            {
                startBy[index] = StartBy(identities[index]);
            }
        }

        var result = new List<(ComputePrewarmRecord, ShaderCodeCapture, float)>(identities.Length);
        foreach (var index in Enumerable.Range(0, identities.Length).OrderBy(index => startBy[index]))
        {
            var record = _loadedComputes[index];
            if (_loadedCodes.TryGetValue(record.CodeKey, out var code))
            {
                result.Add((record, code, startBy[index]));
            }
        }

        return result;
    }

    private float StartBy(ulong identity) =>
        _computeFirstUses.TryGetValue(identity, out var firstUse)
            ? firstUse - (_computeCosts.TryGetValue(identity, out var cost) ? cost : 0)
            : float.PositiveInfinity;

    // The recorded whole pipelines whose stage programs and code loaded, in the order sessions created them.
    public IReadOnlyList<PipelinePrewarmItem> LoadedPipelines()
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

        var result = new List<PipelinePrewarmItem>(_loadedGraphics.Count);
        foreach (var (identity, record) in _loadedGraphics)
        {
            var pixel = record.PixelStage == 0 ? null : Stage(record.PixelStage);
            if (Stage(record.VertexStage) is { } vertex && (record.PixelStage == 0 || pixel is not null))
            {
                result.Add(new PipelinePrewarmItem(record, vertex, pixel, identity));
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

    // The global cache stamp cannot certify a separate caller cache or pipeline binary file.
    public bool HasLinkedComputes => _loadedComputes.Any(record => record.Linked is not null);

    public static ulong Identity(ComputePrewarmRecord record) => XxHash3.HashToUInt64(SerializeCompute(record, address: 0));

    // A graphics program loaded at another address translates the same (the program cache keys omit the address), so
    // its record's identity leaves the address out and a session that meets the program elsewhere records nothing new.
    public static ulong Identity(PixelPrewarmRecord record) => XxHash3.HashToUInt64(SerializePixel(record, address: 0));

    private string ProgressPath(bool extension) => extension ? _extensionProgressPath : _progressPath;

    public HashSet<ulong> ReadProgress(string stamp, bool extension = false)
    {
        var completed = new HashSet<ulong>();
        var path = ProgressPath(extension);
        lock (_progressGate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return completed;
                }

                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != ProgressKey(stamp))
                {
                    File.Delete(path);
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

    public void AppendProgress(string stamp, IReadOnlyCollection<ulong> identities, bool extension = false)
    {
        if (identities.Count == 0)
        {
            return;
        }

        var path = ProgressPath(extension);
        lock (_progressGate)
        {
            try
            {
                var lines = new List<string>(identities.Count + 1);
                if (!File.Exists(path))
                {
                    lines.Add(ProgressKey(stamp));
                }

                foreach (var identity in identities)
                {
                    lines.Add(identity.ToString("X16", CultureInfo.InvariantCulture));
                }

                File.AppendAllLines(path, lines);
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

    // Whether a session found that the program must plan its image table as heap records, so a later session plans it so from
    // the start instead of failing to materialize it first and replanning.
    public bool PrefersHeapRecords(ulong hash, uint codeSize, ulong continuationAddressOffset) =>
        _heapRecordPrograms.ContainsKey((hash, codeSize, continuationAddressOffset));

    private void NoteHeapRecords(bool preferHeapRecords, ulong hash, uint codeSize, ulong continuationAddressOffset)
    {
        if (preferHeapRecords)
        {
            _heapRecordPrograms[(hash, codeSize, continuationAddressOffset)] = 0;
        }
    }

    public void RecordCompute(ShaderCodeCapture code, ComputePrewarmRecord record)
    {
        NoteHeapRecords(record.PreferHeapRecords, record.Hash, record.CodeSize, record.ContinuationAddressOffset);
        var payload = SerializeCompute(record);
        var identity = Identity(record);
        var seconds = SessionSeconds();
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            if (_computes.Add(identity))
            {
                if (_codes.Add(code.Key))
                {
                    Append(CodeKind, SerializeCode(code));
                }

                Append(ComputeKind, payload);
            }

            if (record.Linked is null && (!_computeFirstUses.TryGetValue(identity, out var known) || seconds < known))
            {
                _computeFirstUses[identity] = seconds;
                Append(_extension, ComputeFirstUseKind, IdentitySeconds(identity, seconds));
            }
        }
    }

    // A prewarm compile of the record took this long.
    public void RecordComputeCost(ComputePrewarmRecord record, float seconds)
    {
        if (seconds < MinimumComputeCostSeconds)
        {
            return;
        }

        var identity = Identity(record);
        lock (_gate)
        {
            if (_failed || (_computeCosts.TryGetValue(identity, out var known) && known >= seconds))
            {
                return;
            }

            _computeCosts[identity] = seconds;
            Append(_extension, ComputeCostKind, IdentitySeconds(identity, seconds));
        }
    }

    private static byte[] IdentitySeconds(ulong identity, float seconds)
    {
        var payload = new byte[ComputeSecondsPayloadBytes];
        BitConverter.TryWriteBytes(payload, identity);
        BitConverter.TryWriteBytes(payload.AsSpan(sizeof(ulong)), seconds);
        return payload;
    }

    public void RecordPixel(ShaderCodeCapture code, PixelPrewarmRecord record)
    {
        NoteHeapRecords(record.PreferHeapRecords, record.Hash, record.CodeSize, record.ContinuationAddressOffset);
        var payload = SerializePixel(record, record.Address);
        var identity = Identity(record);
        lock (_gate)
        {
            if (_failed || !_pixels.Add(identity))
            {
                return;
            }

            if (_extensionCodes.Add(code.Key))
            {
                Append(_extension, CodeKind, SerializeCode(code));
            }

            Append(_extension, PixelKind, WithFirstUse(payload));
        }
    }

    // Linked programs whose module a session saw compile for minutes; their dispatches wait only briefly for it.
    public bool IsSlowLinkedCompile(ulong hash) => _linkedCompiles.TryGetValue(hash, out var slow) && slow;

    // Records a linked program's compile outcome when it changes what later sessions know: slow when its module was
    // still compiling a minute after its first dispatch waited, fast again when a compile of a slow one ended within the
    // normal wait.
    public void RecordLinkedCompile(ulong hash, bool slow, long milliseconds)
    {
        lock (_gate)
        {
            var known = _linkedCompiles.TryGetValue(hash, out var current);
            if (_failed || (known ? current == slow : !slow))
            {
                return;
            }

            _linkedCompiles[hash] = slow;
            var payload = new byte[LinkedCompilePayloadBytes];
            BitConverter.TryWriteBytes(payload, hash);
            payload[sizeof(ulong)] = slow ? (byte)1 : (byte)0;
            BitConverter.TryWriteBytes(payload.AsSpan(sizeof(ulong) + 1), milliseconds);
            Append(_extension, LinkedCompileKind, payload);
        }
    }

    public void RecordStage(ShaderCodeCapture code, ulong module, StagePrewarmRecord record)
    {
        NoteHeapRecords(record.PreferHeapRecords, record.CodeKey.Hash, record.CodeKey.CodeSize, record.CodeKey.ContinuationAddressOffset);
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
            _file.Dispose();
            _extension.Dispose();
            _graphicsFile?.Dispose();
        }
    }

    private void Append(byte kind, byte[] payload) => Append(_file, kind, payload);

    private void Append(FileStream file, byte kind, byte[] payload)
    {
        try
        {
            var record = new byte[RecordHeaderBytes + 1 + payload.Length];
            record[RecordHeaderBytes] = kind;
            payload.CopyTo(record, RecordHeaderBytes + 1);
            var body = record.AsSpan(RecordHeaderBytes);
            BitConverter.TryWriteBytes(record.AsSpan(0, sizeof(uint)), (uint)body.Length);
            BitConverter.TryWriteBytes(record.AsSpan(sizeof(uint), sizeof(ulong)), XxHash3.HashToUInt64(body));
            file.Write(record);
            file.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _failed = true;
            Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm list write failed: {exception.Message}");
        }
    }

    private void Load()
    {
        LoadMain();
        LoadExtension();
    }

    private void LoadMain()
    {
        var content = new byte[_file.Length];
        _file.Position = 0;
        _file.ReadExactly(content);
        var validEnd = 0L;
        if (content.Length >= 3 * sizeof(uint) &&
            BitConverter.ToUInt32(content, 0) == Magic &&
            BitConverter.ToUInt32(content, sizeof(uint)) == FormatVersion &&
            BitConverter.ToUInt32(content, 2 * sizeof(uint)) == SchemaFingerprint)
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
                if (XxHash3.HashToUInt64(body) != checksum || !TryLoadRecord(body[0], body[1..].ToArray(), extension: false))
                {
                    break;
                }

                offset += RecordHeaderBytes + (int)length;
                validEnd = offset;
            }
        }

        _loadedCoverage = Convert.ToHexString(SHA256.HashData(content.AsSpan(0, checked((int)validEnd))));
        if (validEnd == 0)
        {
            _file.SetLength(0);
            _file.Position = 0;
            Span<byte> header = stackalloc byte[3 * sizeof(uint)];
            BitConverter.TryWriteBytes(header, Magic);
            BitConverter.TryWriteBytes(header[sizeof(uint)..], FormatVersion);
            BitConverter.TryWriteBytes(header[(2 * sizeof(uint))..], SchemaFingerprint);
            _file.Write(header);
            _file.Flush();
            return;
        }

        _file.SetLength(validEnd);
        _file.Position = validEnd;
    }

    // The side file: a record this build does not know or cannot read is skipped instead of ending the load, so the records
    // after it still load and stay in the file. A torn write still ends the load, and the file is cut there.
    private void LoadExtension()
    {
        var content = new byte[_extension.Length];
        _extension.Position = 0;
        _extension.ReadExactly(content);
        var validEnd = 0L;
        if (content.Length >= ExtensionHeaderBytes &&
            BitConverter.ToUInt32(content, 0) == ExtensionMagic &&
            BitConverter.ToUInt32(content, sizeof(uint)) == ExtensionFormatVersion)
        {
            validEnd = ExtensionHeaderBytes;
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
                if (XxHash3.HashToUInt64(body) != checksum)
                {
                    break;
                }

                _ = TryLoadRecord(body[0], body[1..].ToArray(), extension: true);
                offset += RecordHeaderBytes + (int)length;
                validEnd = offset;
            }
        }

        if (validEnd == 0)
        {
            _extension.SetLength(0);
            _extension.Position = 0;
            Span<byte> header = stackalloc byte[ExtensionHeaderBytes];
            BitConverter.TryWriteBytes(header, ExtensionMagic);
            BitConverter.TryWriteBytes(header[sizeof(uint)..], ExtensionFormatVersion);
            _extension.Write(header);
            _extension.Flush();
            return;
        }

        _extension.SetLength(validEnd);
        _extension.Position = validEnd;
    }

    private bool TryLoadRecord(byte kind, byte[] payload, bool extension)
    {
        try
        {
            switch (kind)
            {
                case CodeKind:
                {
                    var code = DeserializeCode(payload);
                    (extension ? _extensionCodes : _codes).Add(code.Key);
                    _loadedCodes[code.Key] = code;
                    return true;
                }

                case ComputeKind:
                {
                    // By the record's identity, not its bytes: records that differ only in a buffer's stride word load
                    // as one.
                    var record = DeserializeCompute(payload);
                    NoteHeapRecords(record.PreferHeapRecords, record.Hash, record.CodeSize, record.ContinuationAddressOffset);
                    if (_computes.Add(Identity(record)))
                    {
                        _loadedComputes.Add(record);
                    }

                    return true;
                }

                case PixelKind:
                {
                    // A pixel record of another schema is skipped, not refused, so the records after it still load. The
                    // pixel records of a main list written before the side file existed have no first use.
                    var (bytes, firstUse) = extension ? SplitFirstUse(payload) : (payload, 0f);
                    var pixel = TryDeserializePixel(bytes);
                    if (pixel is not null)
                    {
                        NoteHeapRecords(pixel.PreferHeapRecords, pixel.Hash, pixel.CodeSize, pixel.ContinuationAddressOffset);
                    }

                    if (pixel is not null && _pixels.Add(Identity(pixel)))
                    {
                        _loadedPixels.Add(pixel);
                        _graphicsOrder.Add((PixelKind, _loadedPixels.Count - 1, firstUse));
                    }

                    return true;
                }

                case LinkedCompileKind:
                    if (payload.Length != LinkedCompilePayloadBytes)
                    {
                        return false;
                    }

                    _linkedCompiles[BitConverter.ToUInt64(payload, 0)] = payload[sizeof(ulong)] != 0;
                    return true;

                case ComputeFirstUseKind:
                case ComputeCostKind:
                {
                    if (payload.Length != ComputeSecondsPayloadBytes)
                    {
                        return false;
                    }

                    var identity = BitConverter.ToUInt64(payload, 0);
                    var seconds = BitConverter.ToSingle(payload, sizeof(ulong));
                    // The earliest first use, the longest compile.
                    var times = kind == ComputeFirstUseKind ? _computeFirstUses : _computeCosts;
                    if (!times.TryGetValue(identity, out var known) || (kind == ComputeFirstUseKind ? seconds < known : seconds > known))
                    {
                        times[identity] = seconds;
                    }

                    return true;
                }

                default:
                    return TryLoadGraphicsRecord(kind, payload);
            }
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
        {
            return false;
        }
    }

    // The pipeline file's records; a record of an unknown kind ends the load there.
    private bool TryLoadPipelineRecord(byte kind, byte[] payload)
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
                        var stage = DeserializeStage(payload);
                        _loadedStages[identity] = stage;
                        NoteHeapRecords(stage.PreferHeapRecords, stage.CodeKey.Hash, stage.CodeKey.CodeSize, stage.CodeKey.ContinuationAddressOffset);
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

    internal static readonly uint SchemaFingerprint = ComputeSchemaFingerprint();

    private static uint ComputeSchemaFingerprint() => SchemaFingerprintOf(
        typeof(ComputeInputInfo), typeof(Gen5ComputeSystemRegisters), typeof(ResourceSpecialization),
        typeof(BufferSpecialization), typeof(ImageSpecialization), typeof(BufferCandidateTableSpecialization),
        typeof(LinkedShaderPrewarm), typeof(LinkedPrewarmFunction), typeof(LinkedPrewarmSite), typeof(LinkedPrewarmTarget));

    internal static readonly uint PixelSchemaFingerprint = SchemaFingerprintOf(
        typeof(PixelPrewarmRecord), typeof(PixelInputInfo), typeof(Gen5PixelOutputBinding), typeof(Gen5ColorComponentMapping),
        typeof(ColorComponentMap), typeof(ResourceSpecialization), typeof(BufferSpecialization), typeof(ImageSpecialization),
        typeof(BufferCandidateTableSpecialization));

    internal static readonly uint GraphicsSchemaFingerprint = SchemaFingerprintOf(
        typeof(StageCompileOptions), typeof(VertexInputInfo), typeof(VertexInputBuffer), typeof(VertexAttributeResource),
        typeof(ClipSpaceTransform), typeof(PixelInputInfo), typeof(Gen5PixelOutputBinding), typeof(RegisteredShader),
        typeof(PipelineRenderingState), typeof(PipelineVertexInputState), typeof(PipelineStaticParameters),
        typeof(ResourceSpecialization), typeof(BufferSpecialization), typeof(ImageSpecialization),
        typeof(BufferCandidateTableSpecialization));

    private static uint SchemaFingerprintOf(params Type[] types) =>
        (uint)XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(SchemaText(types)));

    // Each type's public properties in name order, with their types. Types are named by Type.ToString, which qualifies
    // generic type arguments by namespace but not by assembly; FullName names each argument's assembly with its version, so
    // every release and every runtime version would change the fingerprints and discard the recorded lists.
    internal static string SchemaText(params Type[] types)
    {
        var vertexRecord = types.Contains(typeof(VertexPrewarmRecord));
        var builder = new StringBuilder();
        foreach (var type in types)
        {
            if (type == typeof(BufferSpecialization))
            {
                // Records keep a stride word in front of each buffer's format (written as 0, skipped on load) that the
                // specialization has no property for, so the schema lists it with the specialization's properties.
                builder.Append(type.ToString()).Append("{System.UInt32 DescriptorFormat;System.UInt32 DescriptorSwizzle;System.UInt32 PackedStride;}");
                continue;
            }

            builder.Append(type.ToString()).Append('{');
            foreach (var property in type.GetProperties().OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                // A vertex record holds none: tessellated programs are not recorded, and the start instance and base vertex
                // registers follow the record as optional fields (SerializeVertex), so the lists recorded before them still load.
                if (vertexRecord && type == typeof(VertexInputInfo) &&
                    property.Name is nameof(VertexInputInfo.Tessellation) or nameof(VertexInputInfo.StartInstanceUserData)
                        or nameof(VertexInputInfo.BaseVertexUserData))
                {
                    continue;
                }

                // A pixel or vertex record's heap flag follows the record as an optional byte (SerializePixel, SerializeVertex), so
                // the lists recorded before it still load.
                if (type == typeof(PixelPrewarmRecord) || type == typeof(VertexPrewarmRecord))
                {
                    if (property.Name == nameof(PixelPrewarmRecord.PreferHeapRecords)) continue;
                }

                builder.Append(property.PropertyType.ToString()).Append(' ').Append(property.Name).Append(';');
            }

            builder.Append('}');
        }

        return builder.ToString();
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

    private static byte[] SerializeCompute(ComputePrewarmRecord record) => SerializeCompute(record, record.Address);

    private static byte[] SerializeCompute(ComputePrewarmRecord record, ulong address)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(record.Hash);
        writer.Write(record.CodeSize);
        writer.Write(address);
        writer.Write(record.ContinuationAddressOffset);
        writer.Write(record.UserDataBase);
        writer.Write(record.UserDataCount);
        writer.Write(record.PushDataCursor);

        var info = record.Info;
        writer.Write(info.ThreadsX);
        writer.Write(info.ThreadsY);
        writer.Write(info.ThreadsZ);
        // 1 counts threads, 2 counts threads the GPU wrote (an older reader reads either as true).
        writer.Write((byte)(info.IndirectThreadCounts ? 2 : info.DispatchThreadDimensions ? 1 : 0));
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
        writer.Write(record.Linked is not null);
        record.Linked?.Write(writer);
        // Optional extension, a bit set (1: device record counts, 2: heap records): old records retain their ordinary
        // resource plan and byte identity.
        var extension = (record.DeviceRecordCounts ? 1 : 0) | (record.PreferHeapRecords ? 2 : 0);
        if (extension != 0) writer.Write((byte)extension);

        writer.Flush();
        return stream.ToArray();
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
        var threadsX = reader.ReadUInt32();
        var threadsY = reader.ReadUInt32();
        var threadsZ = reader.ReadUInt32();
        var threadDimensions = reader.ReadByte();
        var info = new ComputeInputInfo
        {
            ThreadsX = threadsX,
            ThreadsY = threadsY,
            ThreadsZ = threadsZ,
            DispatchThreadDimensions = threadDimensions != 0,
            IndirectThreadCounts = threadDimensions == 2,
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
        var linked = reader.ReadBoolean() ? LinkedShaderPrewarm.Read(reader) : null;
        var deviceRecordCounts = false;
        var preferHeapRecords = false;
        if (reader.BaseStream.Position + 1 == reader.BaseStream.Length)
        {
            var extension = reader.ReadByte();
            if (extension is 0 or > 3) throw new ArgumentException("Unknown prewarm compute extension.");
            deviceRecordCounts = (extension & 1) != 0;
            preferHeapRecords = (extension & 2) != 0;
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new ArgumentException("Trailing prewarm compute data.");

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
            Linked = linked,
            DeviceRecordCounts = deviceRecordCounts,
            PreferHeapRecords = preferHeapRecords,
        };
    }

    private static void WriteSpecialization(BinaryWriter writer, ResourceSpecialization specialization)
    {
        writer.Write(specialization.BaseBufferCount);
        writer.Write(specialization.StableImageBindings);
        writer.Write(specialization.Buffers.Count);
        foreach (var buffer in specialization.Buffers)
        {
            // The stride word, written as 0: strides are read at run time, and the word keeps the record layout.
            writer.Write(0u);
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

        writer.Write(specialization.ImageDescriptorGroups.Count);
        foreach (var group in specialization.ImageDescriptorGroups) writer.Write(group);
        writer.Write(specialization.SamplerDescriptorGroups.Count);
        foreach (var group in specialization.SamplerDescriptorGroups) writer.Write(group);
    }

    private static ResourceSpecialization ReadSpecialization(BinaryReader reader, int limit)
    {
        var specialization = new ResourceSpecialization
        {
            BaseBufferCount = reader.ReadInt32(), StableImageBindings = reader.ReadBoolean(),
        };
        for (var count = CheckedCount(reader.ReadInt32(), limit); count > 0; count--)
        {
            _ = reader.ReadUInt32();
            specialization.Buffers.Add(new BufferSpecialization(reader.ReadUInt32(), reader.ReadUInt32()));
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

        for (var count = CheckedCount(reader.ReadInt32(), limit); count > 0; count--)
            specialization.ImageDescriptorGroups.Add(reader.ReadUInt32());
        for (var count = CheckedCount(reader.ReadInt32(), limit); count > 0; count--)
            specialization.SamplerDescriptorGroups.Add(reader.ReadUInt32());
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

    // address: the record's code address, or 0 for its identity.
    private static byte[] SerializePixel(PixelPrewarmRecord record, ulong address)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(PixelSchemaFingerprint);
        writer.Write(record.Hash);
        writer.Write(record.CodeSize);
        writer.Write(address);
        writer.Write(record.ContinuationAddressOffset);
        writer.Write(record.UserDataBase);
        writer.Write(record.UserDataCount);
        writer.Write(record.PushDataCursor);
        var info = record.Info;
        writer.Write(info.InputCount);
        writer.Write(info.SystemInputBase);
        writer.Write(info.CustomInterpolationMask);
        writer.Write(info.PerspectiveCenterRegister);
        writer.Write(info.InterpolatorSettings.Length);
        foreach (var setting in info.InterpolatorSettings) writer.Write(setting);
        writer.Write(info.TargetOutputModes.Length);
        writer.Write(info.TargetOutputModes);
        writer.Write(info.TargetExportFormats.Length);
        writer.Write(info.TargetExportFormats);
        writer.Write(info.TargetExportMappings.Length);
        foreach (var mapping in info.TargetExportMappings) writer.Write(mapping.Packed);
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
        writer.Write(record.Outputs.Length);
        foreach (var output in record.Outputs)
        {
            writer.Write(output.GuestSlot);
            writer.Write(output.HostLocation);
            writer.Write((int)output.Kind);
            writer.Write(output.ComponentMapping.Packed);
            writer.Write(output.DualSourceAlpha);
            writer.Write(output.ExportTarget);
            writer.Write(output.Index);
            writer.Write((int)output.ExportFormat);
        }

        writer.Write(record.InputEnable);
        writer.Write(record.InputAddress);
        WriteSpecialization(writer, record.Specialization);
        writer.Write(record.DeviceRecordCounts);
        if (record.PreferHeapRecords) writer.Write((byte)1);
        writer.Flush();
        return stream.ToArray();
    }

    // Null for a record of another pixel schema or a damaged one.
    private static PixelPrewarmRecord? TryDeserializePixel(byte[] payload)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload));
            if (reader.ReadUInt32() != PixelSchemaFingerprint)
            {
                return null;
            }

            var hash = reader.ReadUInt64();
            var codeSize = reader.ReadUInt32();
            var address = reader.ReadUInt64();
            var continuationAddressOffset = reader.ReadUInt64();
            var userDataBase = reader.ReadUInt32();
            var userDataCount = reader.ReadUInt32();
            var pushDataCursor = reader.ReadUInt32();
            var inputCount = reader.ReadUInt32();
            var systemInputBase = reader.ReadUInt32();
            var customInterpolationMask = reader.ReadUInt32();
            var perspectiveCenterRegister = reader.ReadUInt32();
            var interpolators = new uint[CheckedCount(reader.ReadInt32(), payload.Length)];
            for (var index = 0; index < interpolators.Length; index++) interpolators[index] = reader.ReadUInt32();
            var outputModes = reader.ReadBytes(CheckedCount(reader.ReadInt32(), payload.Length));
            var exportFormats = reader.ReadBytes(CheckedCount(reader.ReadInt32(), payload.Length));
            var mappings = new ColorComponentMap[CheckedCount(reader.ReadInt32(), payload.Length)];
            for (var index = 0; index < mappings.Length; index++) mappings[index] = new ColorComponentMap(reader.ReadByte());
            // Member initializers run in order, so the reads below follow the written layout.
            var info = new PixelInputInfo
            {
                InputCount = inputCount,
                SystemInputBase = systemInputBase,
                CustomInterpolationMask = customInterpolationMask,
                PerspectiveCenterRegister = perspectiveCenterRegister,
                InterpolatorSettings = interpolators,
                TargetOutputModes = outputModes,
                TargetExportFormats = exportFormats,
                TargetExportMappings = mappings,
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
            var outputs = new Gen5PixelOutputBinding[CheckedCount(reader.ReadInt32(), payload.Length)];
            for (var index = 0; index < outputs.Length; index++)
            {
                var guestSlot = reader.ReadUInt32();
                var hostLocation = reader.ReadUInt32();
                var kind = (Gen5PixelOutputKind)reader.ReadInt32();
                var mapping = new Gen5ColorComponentMapping(reader.ReadByte());
                var dualSourceAlpha = reader.ReadBoolean();
                var exportTarget = reader.ReadUInt32();
                var outputIndex = reader.ReadUInt32();
                var exportFormat = (Gen5PixelExportFormat)reader.ReadInt32();
                outputs[index] = new Gen5PixelOutputBinding(guestSlot, hostLocation, kind, mapping)
                {
                    DualSourceAlpha = dualSourceAlpha,
                    ExportTarget = exportTarget,
                    Index = outputIndex,
                    ExportFormat = exportFormat,
                };
            }

            var inputEnable = reader.ReadUInt32();
            var inputAddress = reader.ReadUInt32();
            var specialization = ReadSpecialization(reader, payload.Length);
            var deviceRecordCounts = reader.ReadBoolean();
            var preferHeapRecords = false;
            if (reader.BaseStream.Position + 1 == reader.BaseStream.Length)
            {
                if (reader.ReadByte() != 1)
                {
                    return null;
                }

                preferHeapRecords = true;
            }

            if (reader.BaseStream.Position != reader.BaseStream.Length)
            {
                return null;
            }

            return new PixelPrewarmRecord
            {
                Hash = hash,
                CodeSize = codeSize,
                Address = address,
                ContinuationAddressOffset = continuationAddressOffset,
                UserDataBase = userDataBase,
                UserDataCount = userDataCount,
                PushDataCursor = pushDataCursor,
                Info = info,
                Outputs = outputs,
                InputEnable = inputEnable,
                InputAddress = inputAddress,
                Specialization = specialization,
                DeviceRecordCounts = deviceRecordCounts,
                PreferHeapRecords = preferHeapRecords,
            };
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or IOException)
        {
            return null;
        }
    }

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
        writer.Write(options.DeviceRecordCounts);
        WriteSpecialization(writer, record.Specialization);
        if (record.PreferHeapRecords) writer.Write((byte)1);
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
        if (!reader.ReadBoolean())
        {
            options = options.WithoutDeviceRecordCounts();
        }

        if ((stage == ShaderStage.Vertex && vertexInfo is null) || (stage == ShaderStage.Pixel && pixelInfo is null))
        {
            throw new ArgumentException("The prewarm stage has no input info.");
        }

        var specialization = ReadSpecialization(reader, limit);
        var preferHeapRecords = false;
        if (reader.BaseStream.Position + 1 == reader.BaseStream.Length)
        {
            if (reader.ReadByte() != 1) throw new ArgumentException("Unknown prewarm stage extension.");
            preferHeapRecords = true;
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
            Specialization = specialization,
            PreferHeapRecords = preferHeapRecords,
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
        writer.Write(info.BaseVertexUserData);
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
            BaseVertexUserData = reader.ReadInt32(),
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
