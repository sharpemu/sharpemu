// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using SharpEmu.HLE;
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

    public (ulong Hash, uint CodeSize, ulong Address) Key => (Hash, CodeSize, Address);

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
    public required ulong Hash { get; init; }
    public required uint CodeSize { get; init; }
    public required ulong Address { get; init; }
    public required uint UserDataBase { get; init; }
    public required uint UserDataCount { get; init; }
    public required uint PushDataCursor { get; init; }
    public required ComputeInputInfo Info { get; init; }
    public Gen5ComputeSystemRegisters? SystemRegisters { get; init; }
    public required ResourceSpecialization Specialization { get; init; }

    public (ulong Hash, uint CodeSize, ulong Address) CodeKey => (Hash, CodeSize, Address);
}

internal sealed class ShaderPrewarmList : IDisposable
{
    public const string FileName = "shader-prewarm.bin";
    public const string StampFileName = "shader-prewarm.stamp";
    public const string ProgressFileName = "shader-prewarm.progress";

    private const uint Magic = 0x57504553;
    private const uint FormatVersion = 1;
    private const byte CodeKind = 1;
    private const byte ComputeKind = 2;
    private const int RecordHeaderBytes = sizeof(uint) + sizeof(ulong);

    private readonly object _gate = new();
    private readonly FileStream _file;
    private readonly string _stampPath;
    private readonly string _progressPath;
    private readonly object _progressGate = new();
    private readonly HashSet<(ulong Hash, uint CodeSize, ulong Address)> _codes = [];
    private readonly HashSet<ulong> _computes = [];
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong Address), ShaderCodeCapture> _loadedCodes = new();
    private readonly List<ComputePrewarmRecord> _loadedComputes = [];
    private bool _failed;

    private ShaderPrewarmList(FileStream file, string stampPath, string progressPath)
    {
        _file = file;
        _stampPath = stampPath;
        _progressPath = progressPath;
    }

    public int LoadedComputeCount => _loadedComputes.Count;

    public static ShaderPrewarmList? Open(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var list = new ShaderPrewarmList(file, Path.Combine(directory, StampFileName), Path.Combine(directory, ProgressFileName));
            try
            {
                list.Load();
            }
            catch
            {
                file.Dispose();
                throw;
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
    {
        try
        {
            File.WriteAllText(_stampPath, stamp);
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
            if (_failed || !_computes.Add(identity))
            {
                return;
            }

            if (_codes.Add(code.Key))
            {
                Append(CodeKind, SerializeCode(code));
            }

            Append(ComputeKind, payload);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _failed = true;
            _file.Dispose();
        }
    }

    private void Append(byte kind, byte[] payload)
    {
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
            _failed = true;
            Console.Error.WriteLine($"[LOADER][WARN] Shader prewarm list write failed: {exception.Message}");
        }
    }

    private void Load()
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
                if (XxHash3.HashToUInt64(body) != checksum || !TryLoadRecord(body[0], body[1..].ToArray()))
                {
                    break;
                }

                offset += RecordHeaderBytes + (int)length;
                validEnd = offset;
            }
        }

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

    private static readonly uint SchemaFingerprint = ComputeSchemaFingerprint();

    private static uint ComputeSchemaFingerprint()
    {
        var builder = new StringBuilder();
        foreach (var type in new[]
        {
            typeof(ComputeInputInfo), typeof(Gen5ComputeSystemRegisters), typeof(ResourceSpecialization),
            typeof(BufferSpecialization), typeof(ImageSpecialization), typeof(BufferCandidateTableSpecialization),
        })
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

    private static byte[] SerializeCompute(ComputePrewarmRecord record)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(record.Hash);
        writer.Write(record.CodeSize);
        writer.Write(record.Address);
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

        var specialization = record.Specialization;
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

        writer.Flush();
        return stream.ToArray();
    }

    private static ComputePrewarmRecord DeserializeCompute(byte[] payload)
    {
        using var reader = new BinaryReader(new MemoryStream(payload));
        var hash = reader.ReadUInt64();
        var codeSize = reader.ReadUInt32();
        var address = reader.ReadUInt64();
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
            NeedsLocalDataShareBarriers = reader.ReadBoolean(),
            WorkgroupRegister = reader.ReadInt32(),
        };

        Gen5ComputeSystemRegisters? registers = reader.ReadBoolean()
            ? new Gen5ComputeSystemRegisters(ReadOptional(reader), ReadOptional(reader), ReadOptional(reader), ReadOptional(reader))
            : null;

        var specialization = new ResourceSpecialization { BaseBufferCount = reader.ReadInt32() };
        for (var count = CheckedCount(reader.ReadInt32(), payload.Length); count > 0; count--)
        {
            specialization.Buffers.Add(new BufferSpecialization(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        }

        for (var count = CheckedCount(reader.ReadInt32(), payload.Length); count > 0; count--)
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

        for (var count = CheckedCount(reader.ReadInt32(), payload.Length); count > 0; count--)
        {
            specialization.BufferCandidateTables.Add(new BufferCandidateTableSpecialization(
                reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        }

        return new ComputePrewarmRecord
        {
            Hash = hash,
            CodeSize = codeSize,
            Address = address,
            UserDataBase = userDataBase,
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
