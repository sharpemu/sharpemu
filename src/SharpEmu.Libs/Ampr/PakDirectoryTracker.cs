// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ampr;

/// <summary>
/// sceAmprAprCommandBufferReadFile carries -1 instead of an absolute offset whenever the guest
/// wants "the next sequential chunk" of a file. For an id Software PACK archive that sequence is
/// always header -> directory table -> individual archived files, and the guest never tells us
/// which archived file it wants next - only the byte count it expects, taken from the very
/// directory entry it already parsed on its side. This tracks a per-fileId read cursor, and once
/// the directory table has streamed past, parses it so later reads can be matched back to their
/// real absolute offset by the size the guest asks for.
/// </summary>
internal static class PakDirectoryTracker
{
    private const int DirectoryEntrySize = 64;
    private const int DirectoryEntryNameLength = 56;

    private static readonly bool _trace =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR_READS"), "1", StringComparison.Ordinal);

    private sealed class DirectoryEntry(string name, uint filePos, uint fileLen)
    {
        public string Name { get; } = name;
        public uint FilePos { get; } = filePos;
        public uint FileLen { get; } = fileLen;
        public bool Consumed { get; set; }
    }

    private sealed class FileState
    {
        public ulong NextOffset;
        public bool ExpectingDirectory;
        public List<DirectoryEntry>? Directory;
    }

    private static readonly ConcurrentDictionary<uint, FileState> _state = new();

    /// <summary>
    /// Resolves what "read the next sequential chunk of size <paramref name="requestedSize"/>"
    /// really means for this fileId: an unconsumed directory entry whose length matches, if the
    /// directory has already been parsed, otherwise the tracked linear cursor.
    /// </summary>
    public static ulong ResolveSequentialOffset(uint fileId, ulong requestedSize)
    {
        var state = _state.GetOrAdd(fileId, static _ => new FileState());

        if (state.Directory is { } entries)
        {
            // Several unrelated archived files can share a byte count (e.g. a head model and a bot
            // navigation file both 0x3A34 bytes). Directory order alone then picks whichever comes
            // first, which is wrong when the game reads them out of order. id archives cluster
            // related assets, and the guest streams them with locality, so disambiguate collisions
            // by choosing the unconsumed match nearest the running read cursor.
            DirectoryEntry? best = null;
            var bestDistance = ulong.MaxValue;
            foreach (var entry in entries)
            {
                if (entry.Consumed || entry.FileLen != requestedSize)
                {
                    continue;
                }

                var distance = entry.FilePos >= state.NextOffset
                    ? entry.FilePos - state.NextOffset
                    : state.NextOffset - entry.FilePos;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = entry;
                }
            }

            if (best is not null)
            {
                best.Consumed = true;
                if (_trace)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] pak.directory_match: id=0x{fileId:X8} name='{best.Name}' " +
                        $"filepos=0x{best.FilePos:X8} filelen=0x{best.FileLen:X8}");
                }

                return best.FilePos;
            }
        }

        return state.NextOffset;
    }

    /// <summary>
    /// Feeds back a completed read so the tracker can advance its cursor, recognize a PACK
    /// header and jump straight to its directory table, or parse the directory table itself
    /// once it streams past.
    /// </summary>
    public static void OnReadCompleted(CpuContext ctx, uint fileId, ulong destination, ulong fileOffset, ulong bytesRead)
    {
        if (bytesRead == 0)
        {
            return;
        }

        var state = _state.GetOrAdd(fileId, static _ => new FileState());

        if (state.ExpectingDirectory && fileOffset == state.NextOffset)
        {
            state.Directory = TryParseDirectory(ctx, destination, bytesRead);
            state.ExpectingDirectory = false;
            state.NextOffset = fileOffset + bytesRead;
            if (_trace)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] pak.directory_parsed: id=0x{fileId:X8} entries={state.Directory?.Count ?? 0}");
            }

            return;
        }

        if (fileOffset == 0 && bytesRead >= 12 && TryReadPackHeader(ctx, destination, out var dirOffset))
        {
            state.NextOffset = dirOffset;
            state.ExpectingDirectory = true;
            return;
        }

        state.NextOffset = fileOffset + bytesRead;
    }

    private static bool TryReadPackHeader(CpuContext ctx, ulong destination, out ulong dirOffset)
    {
        dirOffset = 0;
        Span<byte> header = stackalloc byte[12];
        if (!ctx.Memory.TryRead(destination, header) ||
            header[0] != (byte)'P' || header[1] != (byte)'A' || header[2] != (byte)'C' || header[3] != (byte)'K')
        {
            return false;
        }

        dirOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        return true;
    }

    private static List<DirectoryEntry>? TryParseDirectory(CpuContext ctx, ulong destination, ulong length)
    {
        var count = (int)(length / DirectoryEntrySize);
        if (count <= 0)
        {
            return null;
        }

        var entries = new List<DirectoryEntry>(count);
        Span<byte> record = stackalloc byte[DirectoryEntrySize];
        for (var i = 0; i < count; i++)
        {
            if (!ctx.Memory.TryRead(destination + (ulong)(i * DirectoryEntrySize), record))
            {
                break;
            }

            var nameBytes = record[..DirectoryEntryNameLength];
            var nullIndex = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(nullIndex >= 0 ? nameBytes[..nullIndex] : nameBytes);
            var filePos = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(56, 4));
            var fileLen = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(60, 4));
            entries.Add(new DirectoryEntry(name, filePos, fileLen));
        }

        return entries;
    }
}
