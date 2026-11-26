// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// SHARPEMU_TRANSLATION_CACHE=0 turns it off. Each game keeps the programs it translated on disk, so a later session
// takes a program's SPIR-V from disk instead of translating it again. The key is everything the translation reads: the
// translated program's instructions, every field of the request (pipeline state, specialization, binding layout, memory
// plan), the program cache key with its static state, the builds of the three assemblies that plan and translate, the
// device's translation stamp and the SHARPEMU_ variables that are not paths, dumps, logs or profiles. A change
// therefore misses; it never serves another program.
internal static class TranslationDiskCache
{
    private const int Version = 1;
    private const string Extension = ".spvc";
    private const int MaxEntryBytes = 64 * 1024 * 1024;
    // 8 GiB per game, trimmed by last use: a game's opening alone can keep hundreds of megabytes of live translations
    // and play keeps adding programs, so a small cap would soon evict what a replay of the early game needs.
    private const long MaxCacheBytes = 8L * 1024 * 1024 * 1024;
    // Each build keeps its entries in its own folder (no key of one build can hit in another). A game folder keeps the
    // builds used last (two builds used in turn, or a rollback) and deletes the rest, so a new build leaves no dead
    // entries under the cap.
    private const int KeptBuilds = 2;
    private const long TrimTargetBytes = MaxCacheBytes / 10 * 9;
    private static readonly string Mode = Environment.GetEnvironmentVariable("SHARPEMU_TRANSLATION_CACHE") ?? "1";
    public static readonly bool Enabled = Mode == "1";
    private static readonly JsonSerializerOptions Json = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 64,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    // Every request field but the program (written instruction by instruction).
    private static readonly PropertyInfo[] RequestProperties = typeof(ShaderCompileRequest)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(static property => property.GetIndexParameters().Length == 0 &&
            property.Name != nameof(ShaderCompileRequest.Program))
        .OrderBy(static property => property.Name, StringComparer.Ordinal)
        .ToArray();
    private static readonly string[] Builds =
    [
        typeof(ShaderCompileRequest).Module.ModuleVersionId.ToString("N"),
        typeof(SharpEmu.ShaderCompiler.Vulkan.Gen5SpirvTranslator).Module.ModuleVersionId.ToString("N"),
        typeof(TranslationDiskCache).Module.ModuleVersionId.ToString("N"),
    ];
    private static readonly (string Name, string Value)[] Variables = Environment.GetEnvironmentVariables()
        .Cast<DictionaryEntry>()
        .Select(static entry => (Name: ((string)entry.Key).ToUpperInvariant(), Value: (string?)entry.Value ?? ""))
        .Where(static entry => entry.Name.StartsWith("SHARPEMU_", StringComparison.Ordinal) && !IgnoredVariable(entry.Name))
        .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
        .ToArray();
    // S_GETPC reads the shader base from push data, so one module serves every load address, and the translator's one
    // other read of the address (ProgramAddress) makes a call's return address, which its control holds in the key,
    // relative to the program. Only the translator's debugging switches that change the shaders at chosen addresses
    // (each compares Program.Address in Gen5SpirvTranslator) make the address part of every key. A test of the names
    // (TITLE, CAPTURE, ADDRESS) would also take switches that never touch a shader and put each program's load address
    // in its key, so a program that loads elsewhere in each session would miss its translation.
    private static readonly string[] AddressSelectingVariables =
    [
        "SHARPEMU_CAPTURE_PIXEL_IMAGE_ADDRESS",
        "SHARPEMU_CAPTURE_PIXEL_VGPR_ADDRESS",
        "SHARPEMU_FORCE_PACKED_EXPORT_STORE_ONE",
        "SHARPEMU_FORCE_PACKED_STORE_EXEC_VALUES",
        "SHARPEMU_FORCE_PIXEL_EXPORT_ADDRESS",
        "SHARPEMU_FORCE_PIXEL_EXPORT_VGPR_ADDRESS",
        "SHARPEMU_FORCE_TITLE_COMPARE_4D4",
        "SHARPEMU_FORCE_TITLE_COMPARE_540",
        "SHARPEMU_FORCE_TITLE_EARLY_COLOR",
        "SHARPEMU_FORCE_TITLE_EXPORT_EXEC",
        "SHARPEMU_FORCE_TITLE_SINGLE_MRT",
        "SHARPEMU_FORCE_TITLE_VERTEX_OUTPUTS_ONE",
        "SHARPEMU_TRACE_PACKED_EXPORT_ADDRESS",
        "SHARPEMU_TRACE_TITLE_INTERFACE",
        "SHARPEMU_TRACE_TITLE_SHADER_STATE",
    ];
    private static readonly bool AddressInKey = Variables.Any(static variable => AddressSelectsTranslation(variable.Name));
    private static readonly object WriteLock = new();
    private static volatile string? _folder;
    private static volatile string? _stamp;
    private static long _countedBytes = -1;
    private static int _reportedFailure;

    // The running game's folder, beside its pipeline cache, and the device's translation stamp (the prewarm stamp); null stops.
    // Entries go to this build's folder inside it.
    public static void UseGameFolder(string? folder, string? stamp)
    {
        _stamp = stamp;
        _countedBytes = -1;
        _folder = folder is null || stamp is null || !Enabled ? null : UseBuildFolder(folder, stamp);
    }

    internal static string? Folder => _folder;

    // This build's folder, marked as used now. Other builds' folders but the newest KeptBuilds - 1 are deleted, and so are the
    // flat entries of the first layout.
    private static string UseBuildFolder(string gameFolder, string stamp)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', [Version.ToString(), .. Builds, stamp]))))[..16];
        var current = Path.Combine(gameFolder, name);
        try
        {
            Directory.CreateDirectory(current);
            Directory.SetLastWriteTimeUtc(current, DateTime.UtcNow);
            var game = new DirectoryInfo(gameFolder);
            foreach (var flat in game.GetFiles("*" + Extension))
            {
                flat.Delete();
            }

            foreach (var old in game.GetDirectories().Where(directory => directory.Name != name)
                .OrderByDescending(static directory => directory.LastWriteTimeUtc).Skip(KeptBuilds - 1))
            {
                old.Delete(recursive: true);
            }
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
        }

        return current;
    }

    internal static bool AddressSelectsTranslation(string name) =>
        Array.IndexOf(AddressSelectingVariables, name.ToUpperInvariant()) >= 0;

    // Paths, dumps, logs and profiles never change a translation; neither does this cache's own mode.
    internal static bool IgnoredVariable(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper == "SHARPEMU_TRANSLATION_CACHE" ||
            upper.EndsWith("_PATH", StringComparison.Ordinal) || upper.EndsWith("_FILE", StringComparison.Ordinal) ||
            upper.EndsWith("_DIR", StringComparison.Ordinal) || upper.EndsWith("_FOLDER", StringComparison.Ordinal) ||
            upper.Contains("DUMP", StringComparison.Ordinal) ||
            upper.StartsWith("SHARPEMU_PROFILE", StringComparison.Ordinal) ||
            upper.StartsWith("SHARPEMU_LOG", StringComparison.Ordinal);
    }

    // The key of one translation; null while the cache is off, before a game names its folder, or when a request field
    // cannot be written (that request is translated without the cache).
    public static string? Key(ProgramKey programKey, uint userDataBase, ResourceSpecialization specialization, uint pushDataCursor,
        ShaderCompileRequest request)
    {
        if (!Enabled || _folder is null || _stamp is not { } stamp)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Version);
                foreach (var build in Builds) writer.Write(build);
                writer.Write(stamp);
                writer.Write(Variables.Length);
                foreach (var (name, value) in Variables)
                {
                    writer.Write(name);
                    writer.Write(value);
                }

                writer.Write((int)programKey.Stage);
                writer.Write(programKey.Hash);
                writer.Write(programKey.UserDataCount);
                writer.Write(programKey.CodeSize);
                writer.Write(programKey.ContinuationAddressOffset);
                writer.Write(programKey.StaticState.Length);
                foreach (var word in programKey.StaticState) writer.Write(word);
                writer.Write(userDataBase);
                writer.Write(pushDataCursor);
                writer.Write(JsonSerializer.Serialize(specialization, Json));
                WriteProgram(writer, request.Program);
                foreach (var property in RequestProperties)
                {
                    var value = property.GetValue(request);
                    writer.Write(property.Name);
                    writer.Write(value is null ? "null" : JsonSerializer.Serialize(value, value.GetType(), Json));
                }
            }

            var bytes = stream.GetBuffer().AsSpan(0, (int)stream.Length);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (Exception error)
        {
            // A cache never fails a translation: a request it cannot write is translated without the cache.
            if (Interlocked.Exchange(ref _reportedFailure, 1) == 0)
            {
                Console.Error.WriteLine($"[PERF][TRANSLATION_CACHE] key failed: {error.GetType().Name}: {error.Message}");
            }

            return null;
        }
    }

    private static void WriteProgram(BinaryWriter writer, Gen5ShaderProgram program)
    {
        writer.Write(AddressInKey ? program.Address : 0);
        writer.Write(program.IsFusedProgram);
        var functionAccesses = program.FunctionBufferAccesses.Order().ToArray();
        writer.Write(functionAccesses.Length);
        foreach (var access in functionAccesses) writer.Write(access);
        writer.Write(program.Instructions.Count);
        foreach (var instruction in program.Instructions)
        {
            writer.Write(instruction.Pc);
            writer.Write(instruction.ProgramOffset);
            writer.Write((int)instruction.Encoding);
            writer.Write(instruction.Opcode);
            writer.Write(instruction.Words.Count);
            foreach (var word in instruction.Words) writer.Write(word);
            writer.Write(instruction.Sources.Count);
            foreach (var operand in instruction.Sources)
            {
                writer.Write((int)operand.Kind);
                writer.Write(operand.Value);
            }

            writer.Write(instruction.Destinations.Count);
            foreach (var operand in instruction.Destinations)
            {
                writer.Write((int)operand.Kind);
                writer.Write(operand.Value);
            }

            // Controls are decoded records: their runtime type and every property.
            writer.Write(instruction.Control?.GetType().FullName ?? "");
            writer.Write(instruction.Control is { } control ? JsonSerializer.Serialize(control, control.GetType(), Json) : "");
        }
    }

    // Translates request, or takes its program from the cache under key (null: the cache does not take part).
    public static bool TryTranslate(IGuestGpuBackend compiler, ShaderCompileRequest request, string? key,
        out IGuestCompiledShader? compiled, out string error)
    {
        if (key is null)
        {
            return compiler.TryCompileProgram(request, out compiled, out error);
        }

        var cached = TryLoad(key, compiler);
        if (cached is not null)
        {
            compiled = cached;
            error = string.Empty;
            return true;
        }

        if (!compiler.TryCompileProgram(request, out compiled, out error) || compiled is null)
        {
            return false;
        }

        Store(key, compiled);
        return true;
    }

    // The stored program for key as the backend's shader; null on a miss or a damaged entry.
    internal static IGuestCompiledShader? TryLoad(string key, IGuestGpuBackend backend)
    {
        if (_folder is not { } folder)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(folder, key + Extension);
            if (!File.Exists(path))
            {
                return null;
            }

            var file = File.ReadAllBytes(path);
            if (file.Length is <= 32 or > MaxEntryBytes ||
                !CryptographicOperations.FixedTimeEquals(file.AsSpan(0, 32), SHA256.HashData(file.AsSpan(32))))
            {
                return null;
            }

            using var reader = new BinaryReader(Inflate(file, 32));
            if (reader.ReadInt32() != Version || reader.ReadString() != key)
            {
                return null;
            }

            uint? mask = reader.ReadBoolean() ? reader.ReadUInt32() : null;
            var payloadLength = Checked(reader.ReadInt32());
            var payload = reader.ReadBytes(payloadLength);
            if (payload.Length != payloadLength)
            {
                return null;
            }

            var shader = backend.CreateCachedShader(payload, mask);
            if (shader is not null)
            {
                MarkUsed(path);
            }

            return shader;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static void Store(string key, IGuestCompiledShader shader)
    {
        if (_folder is not { } folder)
        {
            return;
        }

        try
        {
            using var content = new MemoryStream();
            using (var writer = new BinaryWriter(content, Encoding.UTF8, true))
            {
                writer.Write(Version);
                writer.Write(key);
                writer.Write(shader.InputLocationMask.HasValue);
                if (shader.InputLocationMask is { } mask) writer.Write(mask);
                writer.Write(shader.Payload.Length);
                writer.Write(shader.Payload);
            }

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(content.GetBuffer(), 0, (int)content.Length);
            }

            var bytes = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
            if (bytes.Length + 32L > MaxEntryBytes)
            {
                return;
            }

            var path = Path.Combine(folder, key + Extension);
            lock (WriteLock)
            {
                Directory.CreateDirectory(folder);
                var entryBytes = bytes.Length + 32L;
                if (_countedBytes < 0 || _countedBytes + entryBytes > MaxCacheBytes)
                {
                    _countedBytes = Trim(folder, entryBytes, MaxCacheBytes, TrimTargetBytes);
                }

                _countedBytes += entryBytes;
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        file.Write(SHA256.HashData(bytes));
                        file.Write(bytes);
                    }

                    File.Move(temporary, path, true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static int Checked(int count) =>
        count is >= 0 and <= MaxEntryBytes ? count : throw new InvalidDataException("Translation cache entry count out of range.");

    private static MemoryStream Inflate(byte[] compressed, int offset)
    {
        var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(compressed, offset, compressed.Length - offset, false), CompressionMode.Decompress))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (inflated.Length + read > MaxEntryBytes) throw new InvalidDataException("Translation cache entry too large.");
                inflated.Write(buffer, 0, read);
            }
        }

        inflated.Position = 0;
        return inflated;
    }

    // A hit marks its entry used: Trim drops the entries used longest ago.
    private static void MarkUsed(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception error) when (IsCacheFailure(error))
        {
        }
    }

    // When an entry of entryBytes would take the folder past capBytes, deletes the entries used longest ago until it fits
    // within targetBytes. Returns the bytes that remain.
    internal static long Trim(string directory, long entryBytes, long capBytes, long targetBytes)
    {
        var entries = new DirectoryInfo(directory).GetFiles("*" + Extension);
        var total = entries.Sum(static entry => entry.Length);
        if (total + entryBytes <= capBytes)
        {
            return total;
        }

        foreach (var entry in entries.OrderBy(LastUse))
        {
            if (total + entryBytes <= targetBytes) break;
            total -= entry.Length;
            entry.Delete();
        }

        return total;
    }

    private static DateTime LastUse(FileInfo entry) =>
        entry.LastAccessTimeUtc > entry.LastWriteTimeUtc ? entry.LastAccessTimeUtc : entry.LastWriteTimeUtc;

    private static bool IsCacheFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or OverflowException or EndOfStreamException;
}
