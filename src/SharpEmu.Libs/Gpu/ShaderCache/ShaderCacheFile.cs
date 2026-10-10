// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal sealed record CachedCompute(StageRecord Record, ShaderCodeCapture Code, ulong Identity);

internal sealed record CachedGraphics(
    GraphicsPipelineRecord Record,
    ShaderCodeCapture VertexCode,
    ShaderCodeCapture? PixelCode,
    ulong Identity,
    ulong VertexIdentity,
    ulong PixelIdentity);

internal sealed record CachedProgram(InventoryProgram Program, ShaderCodeCapture Code, ulong Identity);

internal readonly record struct ShaderCacheCompaction(long BytesBefore, long BytesAfter, int RecordsDropped);

internal sealed class ShaderCacheFile : IDisposable
{
    public const string FileName = "shader-cache.bin";

    private const string LegacyListName = "shader-prewarm.bin";
    private static readonly string[] LegacyFileNames = [LegacyListName, "shader-prewarm.stamp", "shader-prewarm.progress"];

    private const uint Magic = 0x48435353;
    private const uint FormatVersion = 4;
    private const uint LegacyMagic = 0x57504553;
    private const uint LegacyFormatVersion = 1;
    private const byte CodeKind = 1;
    private const byte ComputeKind = 2;
    private const byte GraphicsKind = 3;
    private const byte CompiledStageKind = 4;
    private const byte BinaryKind = 5;
    private const byte ProgramKind = 6;
    private const byte PairKind = 7;
    private const byte ScannedFileKind = 8;
    private const byte DoneKind = 9;
    private const int RecordHeaderBytes = sizeof(uint) + sizeof(ulong);
    private const int FileHeaderBytes = 3 * sizeof(uint);
    private const int CopyChunkBytes = 1 << 20;
    private const int LazyHeadBytes = 64;
    private const int DoneHeadBytes = 2 * sizeof(ulong);
    private const int ContentKeyBytes = 16;
    private const long MinCompactionBytes = 64L << 20;

    private readonly record struct RecordSpan(long Offset, int Length, ulong Checksum);

    private readonly record struct RecordEntry(
        long Offset, int TotalLength, byte Kind, ulong Stamp, ulong DriverKey, UInt128 ContentKey, bool Duplicate, ulong Checksum);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly bool _readOnly;
    private readonly long _lengthLimit;
    private SafeFileHandle _handle;
    private long _length;
    private bool _failed;

    private readonly Dictionary<ShaderCodeKey, ShaderCodeCapture> _codes = new();
    private readonly HashSet<ulong> _pipelineIdentities = [];
    private readonly List<CachedCompute> _computes = [];
    private readonly List<CachedGraphics> _graphics = [];
    private readonly Dictionary<(ulong Identity, ulong Stamp), RecordSpan> _compiledStages = new();
    private readonly Dictionary<(ulong DriverKey, UInt128 ContentKey), RecordSpan> _binaries = new();
    private readonly List<CachedProgram> _programs = [];
    private readonly Dictionary<ulong, int> _programIndex = new();
    private readonly List<(ulong Vertex, ulong Pixel)> _pairs = [];
    private readonly HashSet<(ulong Vertex, ulong Pixel)> _pairSet = [];
    private readonly Dictionary<string, (ScannedFile File, int Entry)> _scannedFiles = new(StringComparer.Ordinal);
    private readonly HashSet<(ulong Item, ulong DriverStamp)> _done = [];
    private readonly Dictionary<ulong, HashSet<UInt128>> _doneContentKeys = new();
    private readonly List<RecordEntry> _entries = [];

    private ShaderCacheFile(string path, SafeFileHandle handle, bool readOnly = false, long lengthLimit = long.MaxValue)
    {
        _path = path;
        _handle = handle;
        _readOnly = readOnly;
        _lengthLimit = lengthLimit;
    }

    public string Path => _path;

    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _length;
            }
        }
    }

    public IReadOnlyList<CachedCompute> Computes => _computes;

    public CachedCompute[] SnapshotComputes()
    {
        lock (_gate)
        {
            return _computes.ToArray();
        }
    }

    public IReadOnlyList<CachedGraphics> Graphics => _graphics;

    public IReadOnlyList<CachedProgram> Programs => _programs;

    public CachedProgram[] SnapshotPrograms()
    {
        lock (_gate)
        {
            return _programs.ToArray();
        }
    }

    public CachedGraphics[] SnapshotGraphics()
    {
        lock (_gate)
        {
            return _graphics.ToArray();
        }
    }

    public (ulong Vertex, ulong Pixel)[] SnapshotPairs()
    {
        lock (_gate)
        {
            return _pairs.ToArray();
        }
    }

    public IReadOnlyList<(ulong Vertex, ulong Pixel)> Pairs => _pairs;

    public bool TryGetProgram(ulong identity, out CachedProgram program)
    {
        lock (_gate)
        {
            if (_programIndex.TryGetValue(identity, out var index))
            {
                program = _programs[index];
                return true;
            }
        }

        program = null!;
        return false;
    }

    public int BinaryCount
    {
        get
        {
            lock (_gate)
            {
                return _binaries.Count;
            }
        }
    }

    public int ImportedLegacyComputes { get; private set; }

    public static ShaderCacheFile? Open(string directory, Action<long, long>? progress = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, FileName);
            var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var file = new ShaderCacheFile(path, handle);
            try
            {
                file.Load(progress);
                file.ImportLegacyList(System.IO.Path.Combine(directory, LegacyListName));
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            foreach (var legacy in LegacyFileNames)
            {
                File.Delete(System.IO.Path.Combine(directory, legacy));
            }

            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader cache is unavailable: {exception.Message}");
            return null;
        }
    }

    public static ShaderCacheFile? OpenSnapshot(string path, long length)
    {
        try
        {
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var file = new ShaderCacheFile(path, handle, readOnly: true, lengthLimit: length);
            try
            {
                file.Load();
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader cache snapshot is unavailable: {exception.Message}");
            return null;
        }
    }

    public static ShaderCacheFile? OpenPart(string path)
    {
        try
        {
            var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var file = new ShaderCacheFile(path, handle);
            file.Load();
            return file;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader cache part is unavailable: {exception.Message}");
            return null;
        }
    }

    public static ulong ProgramIdentity(InventoryProgram program) =>
        ProgramIdentity(program.Code.Hash, program.Code.CodeSize, program.Header);

    public static ulong ProgramIdentity(ulong codeHash, uint codeSize, ReadOnlySpan<byte> header)
    {
        var hash = new XxHash3();
        Span<byte> head = stackalloc byte[sizeof(ulong) + sizeof(uint)];
        BinaryPrimitives.WriteUInt64LittleEndian(head, codeHash);
        BinaryPrimitives.WriteUInt32LittleEndian(head[sizeof(ulong)..], codeSize);
        hash.Append(head);
        hash.Append(header);
        return hash.GetCurrentHashAsUInt64();
    }

    public ulong? RecordProgram(InventoryProgram program, ShaderCodeCapture code)
    {
        var identity = ProgramIdentity(program);
        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteProgram(writer, program));
        lock (_gate)
        {
            if (_failed || _programIndex.ContainsKey(identity))
            {
                return null;
            }

            EnsureCodeLocked(code);
            if (AppendLocked(ProgramKind, payload) is null)
            {
                return null;
            }

            _programIndex[identity] = _programs.Count;
            _programs.Add(new CachedProgram(program, code, identity));
            return identity;
        }
    }

    public void RecordPair(ulong vertex, ulong pixel)
    {
        var payload = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, vertex);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(sizeof(ulong)), pixel);
        lock (_gate)
        {
            if (!_failed && !_pairSet.Contains((vertex, pixel)) && AppendLocked(PairKind, payload) is not null)
            {
                _pairSet.Add((vertex, pixel));
                _pairs.Add((vertex, pixel));
            }
        }
    }

    public bool TryGetScannedFile(string path, out ScannedFile file)
    {
        lock (_gate)
        {
            if (_scannedFiles.TryGetValue(path, out var known))
            {
                file = known.File;
                return true;
            }
        }

        file = default;
        return false;
    }

    public void RecordScannedFile(ScannedFile file)
    {
        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteScannedFile(writer, file));
        lock (_gate)
        {
            if (!_failed && AppendLocked(ScannedFileKind, payload) is not null)
            {
                ReplaceScannedFileLocked(file, _entries.Count - 1);
            }
        }
    }

    public bool IsDone(ulong item, ulong driverStamp)
    {
        lock (_gate)
        {
            return _done.Contains((item, driverStamp));
        }
    }

    public void AddDone(ulong item, ulong driverStamp, IReadOnlyCollection<UInt128>? contentKeys = null)
    {
        contentKeys ??= [];
        var payload = new byte[DoneHeadBytes + contentKeys.Count * ContentKeyBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, item);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(sizeof(ulong)), driverStamp);
        var offset = DoneHeadBytes;
        foreach (var key in contentKeys)
        {
            BinaryPrimitives.WriteUInt128LittleEndian(payload.AsSpan(offset), key);
            offset += ContentKeyBytes;
        }

        lock (_gate)
        {
            if (!_failed && !_done.Contains((item, driverStamp)) && AppendLocked(DoneKind, payload, stamp: driverStamp) is not null)
            {
                IndexDoneLocked(payload, driverStamp);
            }
        }
    }

    private bool IndexDoneLocked(ReadOnlySpan<byte> payload, ulong driverStamp)
    {
        if (!_done.Add((BinaryPrimitives.ReadUInt64LittleEndian(payload), driverStamp)))
        {
            return false;
        }

        if (payload.Length > DoneHeadBytes)
        {
            if (!_doneContentKeys.TryGetValue(driverStamp, out var keys))
            {
                _doneContentKeys[driverStamp] = keys = [];
            }

            for (var offset = DoneHeadBytes; offset < payload.Length; offset += ContentKeyBytes)
            {
                keys.Add(BinaryPrimitives.ReadUInt128LittleEndian(payload[offset..]));
            }
        }

        return true;
    }

    private static bool IsDonePayload(ReadOnlySpan<byte> payload) =>
        payload.Length >= DoneHeadBytes && (payload.Length - DoneHeadBytes) % ContentKeyBytes == 0;

    public int MergePart(string partPath)
    {
        using var part = OpenSnapshot(partPath, long.MaxValue);
        if (part is null)
        {
            return 0;
        }

        var merged = 0;
        foreach (var entry in part._entries)
        {
            if (entry.Duplicate || entry.Kind is not (CompiledStageKind or BinaryKind or DoneKind))
            {
                continue;
            }

            var body = part.ReadBody(new RecordSpan(entry.Offset + RecordHeaderBytes, entry.TotalLength - RecordHeaderBytes, entry.Checksum));
            if (body is null)
            {
                continue;
            }

            lock (_gate)
            {
                if (_failed)
                {
                    break;
                }

                var known = entry.Kind switch
                {
                    CompiledStageKind => _compiledStages.ContainsKey(ReadCompiledKey(body)),
                    BinaryKind => _binaries.ContainsKey((entry.DriverKey, entry.ContentKey)),
                    _ => _done.Contains((BinaryPrimitives.ReadUInt64LittleEndian(body), entry.Stamp)),
                };
                if (known || AppendLocked(entry.Kind, body, entry.Stamp, entry.DriverKey, entry.ContentKey) is not { } span)
                {
                    continue;
                }

                switch (entry.Kind)
                {
                    case CompiledStageKind:
                        _compiledStages[ReadCompiledKey(body)] = span;
                        break;
                    case BinaryKind:
                        _binaries[(entry.DriverKey, entry.ContentKey)] = span;
                        break;
                    default:
                        IndexDoneLocked(body, entry.Stamp);
                        break;
                }

                merged++;
            }
        }

        return merged;
    }

    private static (ulong Identity, ulong Stamp) ReadCompiledKey(byte[] body) =>
        (BinaryPrimitives.ReadUInt64LittleEndian(body), BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(sizeof(ulong))));

    private void ReplaceScannedFileLocked(ScannedFile file, int entry)
    {
        if (_scannedFiles.TryGetValue(file.Path, out var previous))
        {
            _entries[previous.Entry] = _entries[previous.Entry] with { Duplicate = true };
        }

        _scannedFiles[file.Path] = (file, entry);
    }

    public static ulong StageIdentity(StageRecord record) =>
        XxHash3.HashToUInt64(ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteStage(writer, record)));

    public static ulong DriverKeyHash(ReadOnlySpan<byte> driverKey) => XxHash3.HashToUInt64(driverKey);

    public void RecordCompute(StageRecord record, ShaderCodeCapture code)
    {
        lock (_gate)
        {
            if (RecordComputeLocked(record, code) is { } identity)
            {
                _computes.Add(new CachedCompute(record, code, identity));
            }
        }
    }

    private ulong? RecordComputeLocked(StageRecord record, ShaderCodeCapture code)
    {
        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteStage(writer, record));
        var identity = XxHash3.HashToUInt64(payload);
        if (_failed || !_pipelineIdentities.Add(identity))
        {
            return null;
        }

        EnsureCodeLocked(code);
        AppendLocked(ComputeKind, payload);
        return identity;
    }

    private void ImportLegacyList(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllBytes(path);
        if (content.Length < FileHeaderBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(content) != LegacyMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(sizeof(uint))) != LegacyFormatVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(2 * sizeof(uint))) != LegacySchemaFingerprint)
        {
            if (content.Length > FileHeaderBytes)
            {
                Console.Error.WriteLine("[SHADER CACHE][WARN] The old shader prewarm list has an unknown layout and was dropped.");
            }

            return;
        }

        var codes = new Dictionary<ShaderCodeKey, ShaderCodeCapture>();
        var imported = 0;
        lock (_gate)
        {
            for (var offset = FileHeaderBytes; content.Length - offset >= RecordHeaderBytes + 1;)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(offset));
                var checksum = BinaryPrimitives.ReadUInt64LittleEndian(content.AsSpan(offset + sizeof(uint)));
                if (length == 0 || length > content.Length - offset - RecordHeaderBytes)
                {
                    break;
                }

                var body = content.AsSpan(offset + RecordHeaderBytes, (int)length);
                if (XxHash3.HashToUInt64(body) != checksum)
                {
                    break;
                }

                offset += RecordHeaderBytes + (int)length;
                var payload = body[1..].ToArray();
                try
                {
                    if (body[0] == CodeKind)
                    {
                        var code = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadCode);
                        codes[code.Key] = code;
                    }
                    else if (body[0] == ComputeKind)
                    {
                        var record = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadLegacyCompute);
                        if (codes.TryGetValue(record.CodeKey, out var code) && RecordComputeLocked(record, code) is { } identity)
                        {
                            _computes.Add(new CachedCompute(record, code, identity));
                            imported++;
                        }
                    }
                }
                catch (Exception exception) when (exception is EndOfStreamException or ArgumentException)
                {
                    break;
                }
            }
        }

        ImportedLegacyComputes = imported;
    }

    public void RecordGraphics(GraphicsPipelineRecord record, ShaderCodeCapture vertexCode, ShaderCodeCapture? pixelCode)
    {
        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteGraphics(writer, record));
        var identity = XxHash3.HashToUInt64(payload);
        lock (_gate)
        {
            if (_failed || !_pipelineIdentities.Add(identity))
            {
                return;
            }

            EnsureCodeLocked(vertexCode);
            if (pixelCode is not null)
            {
                EnsureCodeLocked(pixelCode);
            }

            if (AppendLocked(GraphicsKind, payload) is not null)
            {
                _graphics.Add(new CachedGraphics(
                    record,
                    vertexCode,
                    pixelCode,
                    identity,
                    StageIdentity(record.Vertex),
                    record.Pixel is null ? 0 : StageIdentity(record.Pixel)));
            }
        }
    }

    public bool TryGetCompiledStage(ulong stageIdentity, ulong stamp, out CompiledStageRecord stage)
    {
        stage = null!;
        RecordSpan span;
        lock (_gate)
        {
            if (_failed || !_compiledStages.TryGetValue((stageIdentity, stamp), out span))
            {
                return false;
            }
        }

        var body = ReadBody(span);
        if (body is null)
        {
            return false;
        }

        try
        {
            stage = ShaderCacheSerializer.Deserialize(body, reader =>
            {
                ShaderCacheSerializer.ReadCompiledStageHeader(reader);
                return ShaderCacheSerializer.ReadCompiledStageBody(reader);
            });
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }

    public void AddCompiledStage(ulong stageIdentity, ulong stamp, CompiledStageRecord stage)
    {
        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteCompiledStage(writer, stageIdentity, stamp, stage));
        lock (_gate)
        {
            if (_failed || _compiledStages.ContainsKey((stageIdentity, stamp)))
            {
                return;
            }

            if (AppendLocked(CompiledStageKind, payload, stamp: stamp) is { } span)
            {
                _compiledStages[(stageIdentity, stamp)] = span;
            }
        }
    }

    public bool HasBinary(ReadOnlySpan<byte> driverKey, UInt128 contentKey)
    {
        var driver = DriverKeyHash(driverKey);
        lock (_gate)
        {
            return !_failed && _binaries.ContainsKey((driver, contentKey));
        }
    }

    public bool TryReadBinary(ReadOnlySpan<byte> driverKey, UInt128 contentKey, out PipelineBinaryPart[] parts)
    {
        parts = [];
        RecordSpan span;
        var driver = DriverKeyHash(driverKey);
        lock (_gate)
        {
            if (_failed || !_binaries.TryGetValue((driver, contentKey), out span))
            {
                return false;
            }
        }

        var body = ReadBody(span);
        if (body is null)
        {
            return false;
        }

        try
        {
            parts = ShaderCacheSerializer.Deserialize(body, reader =>
            {
                ShaderCacheSerializer.ReadBinaryHeader(reader);
                return ShaderCacheSerializer.ReadBinaryParts(reader);
            });
            return parts.Length != 0;
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }

    public void AddBinary(ReadOnlySpan<byte> driverKey, UInt128 contentKey, IReadOnlyList<PipelineBinaryPart> parts)
    {
        if (parts.Count == 0)
        {
            return;
        }

        var driverBytes = driverKey.ToArray();
        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteBinary(writer, driverBytes, contentKey, parts));
        var key = (DriverKeyHash(driverKey), contentKey);
        lock (_gate)
        {
            if (_failed || _binaries.ContainsKey(key))
            {
                return;
            }

            if (AppendLocked(BinaryKind, payload, driverKey: key.Item1, contentKey: contentKey) is { } span)
            {
                _binaries[key] = span;
            }
        }
    }

    public ShaderCacheCompaction? Compact(
        ulong stamp, ReadOnlySpan<byte> driverKey, ulong driverStamp, bool force = false, bool dropUnreferencedBinaries = false)
    {
        var driver = driverKey.IsEmpty ? 0 : DriverKeyHash(driverKey);
        lock (_gate)
        {
            if (_failed || _readOnly)
            {
                return null;
            }

            var referenced = dropUnreferencedBinaries ? _doneContentKeys.GetValueOrDefault(driverStamp) ?? [] : null;
            var kept = new List<RecordEntry>(_entries.Count);
            var droppedBytes = 0L;
            foreach (var entry in _entries)
            {
                var keep = !entry.Duplicate && entry.Kind switch
                {
                    CompiledStageKind => false,
                    BinaryKind => driver != 0 && entry.DriverKey == driver && (referenced is null || referenced.Contains(entry.ContentKey)),
                    DoneKind => entry.Stamp == driverStamp,
                    _ => true,
                };
                if (keep)
                {
                    kept.Add(entry);
                }
                else
                {
                    droppedBytes += entry.TotalLength;
                }
            }

            var dropped = _entries.Count - kept.Count;
            if (dropped == 0 || (!force && droppedBytes < Math.Max(MinCompactionBytes, _length / 4)))
            {
                return null;
            }

            var before = _length;
            var temporary = _path + ".compact";
            try
            {
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    output.Write(CreateFileHeader());
                    var buffer = ArrayPool<byte>.Shared.Rent(CopyChunkBytes);
                    try
                    {
                        foreach (var entry in kept)
                        {
                            for (var copied = 0; copied < entry.TotalLength;)
                            {
                                var chunk = Math.Min(CopyChunkBytes, entry.TotalLength - copied);
                                ReadExactly(_handle, buffer.AsSpan(0, chunk), entry.Offset + copied);
                                output.Write(buffer, 0, chunk);
                                copied += chunk;
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                _handle.Dispose();
                File.Move(temporary, _path, overwrite: true);
                _handle = File.OpenHandle(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                ResetIndexLocked();
                Load();
                return new ShaderCacheCompaction(before, _length, dropped);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader cache could not be compacted: {exception.Message}");
                try
                {
                    File.Delete(temporary);
                    if (_handle.IsClosed)
                    {
                        _handle = File.OpenHandle(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                        ResetIndexLocked();
                        Load();
                    }
                }
                catch (Exception reopen) when (reopen is IOException or UnauthorizedAccessException)
                {
                    _failed = true;
                    Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader cache was closed: {reopen.Message}");
                }

                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _failed = true;
            _handle.Dispose();
        }
    }

    private void ResetIndexLocked()
    {
        _codes.Clear();
        _pipelineIdentities.Clear();
        _computes.Clear();
        _graphics.Clear();
        _compiledStages.Clear();
        _binaries.Clear();
        _programs.Clear();
        _programIndex.Clear();
        _pairs.Clear();
        _pairSet.Clear();
        _scannedFiles.Clear();
        _done.Clear();
        _doneContentKeys.Clear();
        _entries.Clear();
    }

    private void EnsureCodeLocked(ShaderCodeCapture code)
    {
        if (_codes.TryAdd(code.Key, code))
        {
            AppendLocked(CodeKind, ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteCode(writer, code)));
        }
    }

    private RecordSpan? AppendLocked(byte kind, byte[] payload, ulong stamp = 0, ulong driverKey = 0, UInt128 contentKey = default)
    {
        if (_readOnly)
        {
            return null;
        }

        try
        {
            var record = new byte[RecordHeaderBytes + 1 + payload.Length];
            record[RecordHeaderBytes] = kind;
            payload.CopyTo(record, RecordHeaderBytes + 1);
            var body = record.AsSpan(RecordHeaderBytes);
            var checksum = XxHash3.HashToUInt64(body);
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)body.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(sizeof(uint)), checksum);
            var offset = _length;
            RandomAccess.Write(_handle, record, offset);
            _length += record.Length;
            _entries.Add(new RecordEntry(offset, record.Length, kind, stamp, driverKey, contentKey, false, checksum));
            return new RecordSpan(offset + RecordHeaderBytes, body.Length, checksum);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _failed = true;
            Console.Error.WriteLine($"[SHADER CACHE][WARN] The shader cache write failed: {exception.Message}");
            return null;
        }
    }

    private byte[]? ReadBody(RecordSpan span)
    {
        try
        {
            var body = new byte[span.Length];
            ReadExactly(_handle, body, span.Offset);
            return XxHash3.HashToUInt64(body) == span.Checksum ? body.AsSpan(1).ToArray() : null;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or EndOfStreamException)
        {
            return null;
        }
    }

    private static bool IsLazyKind(byte kind) => kind is CompiledStageKind or BinaryKind;

    private void Load(Action<long, long>? progress = null)
    {
        var fileLength = RandomAccess.GetLength(_handle);
        var length = Math.Min(fileLength, _lengthLimit);
        progress?.Invoke(0, length);
        var indexed = 0;
        var validEnd = 0L;
        var header = new byte[FileHeaderBytes];
        if (length >= FileHeaderBytes &&
            TryReadExactly(_handle, header, 0) &&
            header.AsSpan().SequenceEqual(CreateFileHeader()))
        {
            validEnd = FileHeaderBytes;
            var recordHeader = new byte[RecordHeaderBytes + 1];
            while (length - validEnd >= RecordHeaderBytes + 1 && TryReadExactly(_handle, recordHeader, validEnd))
            {
                var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader);
                var checksum = BinaryPrimitives.ReadUInt64LittleEndian(recordHeader.AsSpan(sizeof(uint)));
                if (bodyLength == 0 || bodyLength > length - validEnd - RecordHeaderBytes)
                {
                    break;
                }

                var lazy = IsLazyKind(recordHeader[RecordHeaderBytes]);
                var body = new byte[lazy ? Math.Min(bodyLength, LazyHeadBytes) : bodyLength];
                if (!TryReadExactly(_handle, body, validEnd + RecordHeaderBytes) ||
                    (!lazy && XxHash3.HashToUInt64(body) != checksum) ||
                    !TryIndex(body, validEnd, RecordHeaderBytes + (int)bodyLength, checksum))
                {
                    break;
                }

                validEnd += RecordHeaderBytes + bodyLength;
                if (progress is not null && (++indexed & 127) == 0)
                {
                    progress?.Invoke(validEnd, length);
                }
            }
        }

        if (validEnd == 0)
        {
            if (!_readOnly)
            {
                RandomAccess.SetLength(_handle, 0);
                RandomAccess.Write(_handle, CreateFileHeader(), 0);
            }

            _length = _readOnly ? 0 : FileHeaderBytes;
            progress?.Invoke(length, length);
            return;
        }

        if (!_readOnly && validEnd != fileLength)
        {
            RandomAccess.SetLength(_handle, validEnd);
        }

        _length = validEnd;
        progress?.Invoke(length, length);
    }

    private bool TryIndex(byte[] body, long offset, int totalLength, ulong checksum)
    {
        var kind = body[0];
        var payload = body.AsSpan(1).ToArray();
        var bodySpan = new RecordSpan(offset + RecordHeaderBytes, totalLength - RecordHeaderBytes, checksum);
        RecordEntry Entry(bool duplicate, ulong stamp = 0, ulong driver = 0, UInt128 content = default) =>
            new(offset, totalLength, kind, stamp, driver, content, duplicate, checksum);
        try
        {
            switch (kind)
            {
                case CodeKind:
                {
                    var code = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadCode);
                    _entries.Add(Entry(!_codes.TryAdd(code.Key, code)));
                    return true;
                }

                case ComputeKind:
                {
                    var identity = XxHash3.HashToUInt64(payload);
                    var duplicate = !_pipelineIdentities.Add(identity);
                    _entries.Add(Entry(duplicate));
                    var record = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadStage);
                    if (!duplicate && record.Stage == ShaderStage.Compute && _codes.TryGetValue(record.CodeKey, out var code))
                    {
                        _computes.Add(new CachedCompute(record, code, identity));
                    }

                    return true;
                }

                case GraphicsKind:
                {
                    var identity = XxHash3.HashToUInt64(payload);
                    var duplicate = !_pipelineIdentities.Add(identity);
                    _entries.Add(Entry(duplicate));
                    var record = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadGraphics);
                    if (!duplicate && _codes.TryGetValue(record.Vertex.CodeKey, out var vertexCode))
                    {
                        ShaderCodeCapture? pixelCode = null;
                        if (record.Pixel is null || _codes.TryGetValue(record.Pixel.CodeKey, out pixelCode))
                        {
                            _graphics.Add(new CachedGraphics(
                                record,
                                vertexCode,
                                pixelCode,
                                identity,
                                StageIdentity(record.Vertex),
                                record.Pixel is null ? 0 : StageIdentity(record.Pixel)));
                        }
                    }

                    return true;
                }

                case CompiledStageKind:
                {
                    var (identity, stamp) = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadCompiledStageHeader, allowTrailing: true);
                    _entries.Add(Entry(!_compiledStages.TryAdd((identity, stamp), bodySpan), stamp));
                    return true;
                }

                case BinaryKind:
                {
                    var (driverKey, contentKey) = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadBinaryHeader, allowTrailing: true);
                    var driver = DriverKeyHash(driverKey);
                    _entries.Add(Entry(!_binaries.TryAdd((driver, contentKey), bodySpan), driver: driver, content: contentKey));
                    return true;
                }

                case ProgramKind:
                {
                    var program = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadProgram);
                    var identity = ProgramIdentity(program);
                    var duplicate = _programIndex.ContainsKey(identity) || !_codes.TryGetValue(program.Code, out var code);
                    _entries.Add(Entry(duplicate));
                    if (!duplicate)
                    {
                        _programIndex[identity] = _programs.Count;
                        _programs.Add(new CachedProgram(program, _codes[program.Code], identity));
                    }

                    return true;
                }

                case PairKind:
                {
                    if (payload.Length != 2 * sizeof(ulong))
                    {
                        return false;
                    }

                    var pair = (BinaryPrimitives.ReadUInt64LittleEndian(payload), BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(ulong))));
                    var added = _pairSet.Add(pair);
                    _entries.Add(Entry(!added));
                    if (added)
                    {
                        _pairs.Add(pair);
                    }

                    return true;
                }

                case ScannedFileKind:
                {
                    var file = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadScannedFile);
                    _entries.Add(Entry(false));
                    ReplaceScannedFileLocked(file, _entries.Count - 1);
                    return true;
                }

                case DoneKind:
                {
                    if (!IsDonePayload(payload))
                    {
                        return false;
                    }

                    var stamp = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(ulong)));
                    _entries.Add(Entry(!IndexDoneLocked(payload, stamp), stamp));
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

    private static byte[] CreateFileHeader()
    {
        var header = new byte[FileHeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(uint)), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2 * sizeof(uint)), SchemaFingerprint);
        return header;
    }

    private static readonly Type[] ComputeSchemaTypes =
    [
        typeof(ComputeInputInfo), typeof(Gen5ComputeSystemRegisters), typeof(ResourceSpecialization),
        typeof(BufferSpecialization), typeof(ImageSpecialization), typeof(BufferCandidateTableSpecialization),
    ];

    private static readonly uint SchemaFingerprint = HashSchema(
        DescribeTypes(
            [
                .. ComputeSchemaTypes,
                typeof(ShaderVertexInput), typeof(ShaderClipSpaceTransform), typeof(Gen5PixelOutputBinding),
                typeof(PipelineRenderingState), typeof(PipelineVertexInputState), typeof(PipelineVertexBinding),
                typeof(PipelineVertexAttribute), typeof(BufferDescriptorWords), typeof(StageRecord),
                typeof(VertexStageInputs), typeof(PixelStageInputs), typeof(GraphicsPipelineRecord),
            ])
            .Append(PipelineStaticParameters.ByteSize));

    private static readonly uint LegacySchemaFingerprint = HashSchema(DescribeTypes(ComputeSchemaTypes));

    public static uint RecordSchema => SchemaFingerprint;

    private static StringBuilder DescribeTypes(IEnumerable<Type> types)
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

        return builder;
    }

    private static uint HashSchema(StringBuilder description) =>
        (uint)XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(description.ToString()));

    private static bool TryReadExactly(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        try
        {
            ReadExactly(handle, destination, offset);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(handle, destination, offset);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            destination = destination[read..];
            offset += read;
        }
    }
}
