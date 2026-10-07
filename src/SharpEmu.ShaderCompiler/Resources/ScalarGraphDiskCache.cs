// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpEmu.ShaderCompiler.Resources;

internal static class ScalarGraphDiskCache
{
    private const int Version = 2;
    private const int MaxEntryBytes = 32 * 1024 * 1024;
    private const long MaxCacheBytes = 256L * 1024 * 1024;
    private static readonly string CachePath = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_ANALYSIS_CACHE_PATH") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpEmu", "shader-analysis");
    private static readonly bool Trace = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SHADER_PLANNING") == "1";
    private static readonly object WriteLock = new();

    internal static ScalarValueGraph Build(Gen5ShaderProgram program, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads, uint waveSize, string? cachePath = null)
    {
        var directory = cachePath ?? CachePath;
        if (directory == "0") return ScalarValueGraph.Build(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        var key = Key(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        var path = Path.Combine(directory, key + ".graph");
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is > 32 and <= MaxEntryBytes)
            {
                var digest = new byte[32];
                file.ReadExactly(digest);
                var bytes = new byte[checked((int)file.Length - 32)];
                file.ReadExactly(bytes);
                if (CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(bytes)))
                {
                    using var reader = new BinaryReader(new MemoryStream(bytes, false));
                    if (reader.ReadString() != key) throw new InvalidDataException("Graph cache key mismatch.");
                    var cached = ScalarValueGraph.ReadSnapshot(reader, program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
                    if (Trace) Console.Error.WriteLine($"[SHADER_ANALYSIS_CACHE] hit key={key} bytes={bytes.Length}");
                    return cached;
                }
            }
        }
        catch (Exception error) when (IsCacheFailure(error)) { }

        var graph = ScalarValueGraph.Build(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(key);
                graph.WriteSnapshot(writer);
            }
            if (stream.Length <= MaxEntryBytes - 32)
            {
                var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
                lock (WriteLock)
                {
                    Directory.CreateDirectory(directory);
                    // Keep a bounded cache across compiler versions too. No live
                    // analysis objects or guest memory are retained by the cache.
                    EvictOldest(directory, stream.Length + 32);
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            file.Write(SHA256.HashData(bytes));
                            file.Write(bytes);
                        }
                        File.Move(temporary, path, true);
                        _entries!.Enqueue((path, stream.Length + 32));
                        _totalBytes += stream.Length + 32;
                    }
                    finally { File.Delete(temporary); }
                }
                if (Trace) Console.Error.WriteLine($"[SHADER_ANALYSIS_CACHE] miss key={key} bytes={bytes.Length}");
            }
        }
        catch (Exception error) when (IsCacheFailure(error)) { }
        return graph;
    }

    // The entries of the cache directory, oldest first, and their total size. The directory is
    // listed once per process: a new build misses on every shader, and listing it per miss cost
    // more than the analysis it saved. Caller holds WriteLock.
    private static string? _entriesDirectory;
    private static Queue<(string Path, long Bytes)>? _entries;
    private static long _totalBytes;

    private static void EvictOldest(string directory, long incoming)
    {
        if (_entries is null || _entriesDirectory != directory)
        {
            var files = new DirectoryInfo(directory).GetFiles("*.graph").OrderBy(f => f.LastWriteTimeUtc).ToArray();
            _entries = new Queue<(string Path, long Bytes)>(files.Select(f => (f.FullName, f.Length)));
            _totalBytes = files.Sum(f => f.Length);
            _entriesDirectory = directory;
        }

        while (_totalBytes + incoming > MaxCacheBytes && _entries.TryDequeue(out var entry))
        {
            _totalBytes -= entry.Bytes;
            File.Delete(entry.Path);
        }
    }

    internal static string Key(Gen5ShaderProgram program, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads, uint waveSize)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Version);
            writer.Write(typeof(ScalarValueGraph).Module.ModuleVersionId.ToByteArray());
            writer.Write(userDataBase); writer.Write(userDataCount); writer.Write(waveSize);
            var loads = fixedFunctionVertexLoads?.Order().ToArray() ?? [];
            writer.Write(loads.Length);
            foreach (var load in loads) writer.Write(load);
            writer.Write(program.Instructions.Count);
            foreach (var instruction in program.Instructions)
            {
                writer.Write(instruction.Pc); writer.Write(instruction.ProgramOffset); writer.Write((int)instruction.Encoding); writer.Write(instruction.Opcode);
                writer.Write(instruction.Words.Count);
                foreach (var word in instruction.Words) writer.Write(word);
                writer.Write(instruction.Sources.Count);
                foreach (var operand in instruction.Sources) { writer.Write((int)operand.Kind); writer.Write(operand.Value); }
                writer.Write(instruction.Destinations.Count);
                foreach (var operand in instruction.Destinations) { writer.Write((int)operand.Kind); writer.Write(operand.Value); }
                // Controls are a closed set of decoded record types. Include the
                // runtime type and every property, including variable NSA arrays.
                writer.Write(instruction.Control?.GetType().FullName ?? "");
                writer.Write(instruction.Control is { } control ? JsonSerializer.Serialize(control, control.GetType()) : "");
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static bool IsCacheFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or OverflowException;
}
