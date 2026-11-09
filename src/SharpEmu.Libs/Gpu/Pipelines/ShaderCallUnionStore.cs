// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The callees of each runtime-record caller's program that last became ready, in a file beside the linked programs' pipeline
// caches (VulkanPipelineCacheStorage.LinkedDirectory). A later session links the caller with these callees first: it
// translates the program whose pipeline that cache holds, which is ready in seconds where the driver's compile takes minutes.
// SHARPEMU_VK_PIPELINE_CACHE=0 keeps neither.
internal sealed class ShaderCallUnionStore(string? path)
{
    private const uint Magic = 0x55434C53;
    private const uint Version = 3;

    private readonly Dictionary<(ShaderStage Stage, ulong Hash), Dictionary<uint, UnitedCallee[]>> _callers = [];
    private bool _loaded;

    public static ShaderCallUnionStore Open() => new(
        Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE") == "0"
            ? null
            : Path.Combine(VulkanPipelineCacheStorage.LinkedDirectory(VideoOutExports.GetApplicationTitleId(),
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")), "callees.bin"));

    // The callees kept for a caller, by site pc and address.
    public Dictionary<uint, Dictionary<ulong, UnitedCallee>> Load(ShaderStage stage, ulong hash)
    {
        EnsureLoaded();
        var callees = new Dictionary<uint, Dictionary<ulong, UnitedCallee>>();
        if (_callers.TryGetValue((stage, hash), out var sites))
        {
            foreach (var (pc, kept) in sites)
            {
                callees.Add(pc, kept.ToDictionary(static callee => callee.Target.Address));
            }
        }

        return callees;
    }

    public void Save(ShaderStage stage, ulong hash, Dictionary<uint, UnitedCallee[]> sites)
    {
        if (path is null)
        {
            return;
        }

        EnsureLoaded();
        _callers[(stage, hash)] = sites;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(_callers.Count);
                foreach (var ((callerStage, callerHash), callerSites) in _callers)
                {
                    writer.Write((uint)callerStage);
                    writer.Write(callerHash);
                    writer.Write(callerSites.Count);
                    foreach (var (pc, callees) in callerSites)
                    {
                        writer.Write(pc);
                        writer.Write(callees.Length);
                        foreach (var callee in callees)
                        {
                            writer.Write(callee.Target.Address);
                            writer.Write(callee.Runs.Length);
                            foreach (var (address, bytes) in callee.Runs)
                            {
                                writer.Write(address);
                                writer.Write(bytes.Length);
                                writer.Write(bytes);
                            }
                        }
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + $".{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[GPU][WARN][LINKED_PROGRAM] The callees could not be kept: {exception.Message}");
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var reader = new BinaryReader(new MemoryStream(File.ReadAllBytes(path)));
            if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
            {
                return;
            }

            // Callees whose code is the same share one decoded program, as ShaderCallLibrary.Unite keeps them, so the module
            // shares their function.
            var programs = new Dictionary<string, Gen5ShaderProgram>();
            for (var caller = reader.ReadInt32(); caller > 0; caller--)
            {
                var key = ((ShaderStage)reader.ReadUInt32(), reader.ReadUInt64());
                var sites = new Dictionary<uint, UnitedCallee[]>();
                for (var site = reader.ReadInt32(); site > 0; site--)
                {
                    var pc = reader.ReadUInt32();
                    var callees = new List<UnitedCallee>();
                    for (var callee = reader.ReadInt32(); callee > 0; callee--)
                    {
                        var address = reader.ReadUInt64();
                        var runs = new (ulong Address, byte[] Bytes)[reader.ReadInt32()];
                        for (var run = 0; run < runs.Length; run++)
                        {
                            var start = reader.ReadUInt64();
                            runs[run] = (start, reader.ReadBytes(reader.ReadInt32()));
                        }

                        if (Decode(address, runs) is { } decoded)
                        {
                            var code = Convert.ToHexString(decoded.Code);
                            if (programs.TryGetValue(code, out var shared))
                            {
                                decoded = decoded with { Target = decoded.Target with { Program = shared } };
                            }
                            else
                            {
                                programs.Add(code, decoded.Target.Program);
                            }

                            callees.Add(decoded);
                        }
                    }

                    sites.Add(pc, [.. callees]);
                }

                _callers[key] = sites;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException or
            ArgumentException or InvalidCastException)
        {
            _callers.Clear();
            Console.Error.WriteLine($"[GPU][WARN][LINKED_PROGRAM] The kept callees could not be read: {exception.Message}");
        }
    }

    // A kept callee decoded again from the words its first decode read.
    private static UnitedCallee? Decode(ulong address, (ulong Address, byte[] Bytes)[] runs)
    {
        var words = new Dictionary<ulong, uint>();
        foreach (var (start, bytes) in runs)
        {
            for (var offset = 0; offset + sizeof(uint) <= bytes.Length; offset += sizeof(uint))
            {
                words[start + (ulong)offset] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            }
        }

        var result = ShaderFunctionReader.Read(address, (ulong wordAddress, out uint word) => words.TryGetValue(wordAddress, out word));
        return result.Complete
            ? new UnitedCallee(new ShaderCallTarget(address, 0, result.Program), SHA256.HashData(result.Bytes), result.Runs)
            : null;
    }
}
