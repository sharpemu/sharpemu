// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.GnmMode.Gpu;

internal static class GnmScatterCopy
{
    private const int BranchWord = 9;
    private const uint UserData = 0x240;
    private const uint RecordStride = 12;
    private const uint MaxGroups = 1 << 20;
    private const ulong ClusterGap = 64 * 1024;
    private const int MaxClusters = 16;
    private const int MaxTraces = 8;
    private static readonly uint[] Code =
    [
        0xBEEB03FF, 0x0000000C, 0x7E020210, 0xE0082000, 0x80020101, 0xBF8C0F70, 0x7D8C0103, 0xBE80246A,
        0x4A040500, 0xBF880000, 0xE0002000, 0x80010202, 0x4A000300, 0x7D88000E, 0xBE80246A, 0xBF8C0F70,
        0xE0102000, 0x80030200, 0xBF810000,
    ];
    private static int _traces;
    private static int _rejected;
    private static int _mismatched;

    private readonly record struct BufferWords(ulong Base, uint Stride, uint Records, uint DataFormat, bool Plain)
    {
        public static BufferWords From(ReadOnlySpan<uint> words) => new(
            words[0] | ((ulong)(words[1] & 0xFF) << 32),
            (words[1] >> 16) & 0x3FFF,
            words[2],
            (words[3] >> 15) & 15,
            (words[1] >> 30) == 0 && ((words[3] >> 23) & 1) == 0);
    }

    private readonly record struct Copy(ulong Destination, ulong Source, ulong Count);

    private readonly record struct Cluster(int First, int Count, ulong Destination, ulong DestinationRecords,
        ulong Source, ulong SourceRecords);

    internal static bool TryRun(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> payload,
        ICpuMemory memory, PacketHandler dispatch)
    {
        var compute = interpreter.TypedRegisters.Shader.Compute;
        if (payload.Length < 4 || !IsScatterCopy(memory, compute.Address))
            return false;
        var threads = compute.ThreadsX & 0xFFFF;
        var original = compute.UserScalars.ValueSpan(16).Slice(4, 12).ToArray();
        var source = BufferWords.From(original.AsSpan(0, 4));
        var records = BufferWords.From(original.AsSpan(4, 4));
        var destination = BufferWords.From(original.AsSpan(8, 4));
        var groups = payload[0];
        if (payload[1] != 1 || payload[2] != 1 || (payload[3] & 0x20) != 0 || groups == 0 || groups > MaxGroups ||
            groups > records.Records || threads == 0 || (compute.ThreadsY & 0xFFFF) != 1 || (compute.ThreadsZ & 0xFFFF) != 1 ||
            !source.Plain || !records.Plain || !destination.Plain || source.Stride != 4 || destination.Stride != 4 ||
            source.DataFormat != 4 || destination.DataFormat != 4 || records.DataFormat != 13 || records.Stride < RecordStride)
            return Reject(compute.Address, payload, original, "unsupported dispatch shape");

        var tableBytes = (ulong)groups * records.Stride;
        if (GnmGpuWrites.MayOverlap(records.Base, tableBytes))
            return Reject(compute.Address, payload, original, "the record table is GPU-written");
        var bytes = new byte[tableBytes];
        if (!memory.TryRead(records.Base, bytes))
            return Reject(compute.Address, payload, original, "the record table is unreadable");

        var copies = new Copy[groups];
        for (var group = 0; group < copies.Length; group++)
        {
            var entry = bytes.AsSpan(group * (int)records.Stride, (int)RecordStride);
            copies[group] = new Copy(BinaryPrimitives.ReadUInt32LittleEndian(entry),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                Math.Min((ulong)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) + 1, threads));
        }
        Array.Sort(copies, static (left, right) => left.Destination.CompareTo(right.Destination));

        var clusters = Partition(copies, destination.Records, source.Records);
        var table = new uint[copies.Length * 3];
        var written = 0;
        foreach (var cluster in clusters)
            for (var index = cluster.First; index < cluster.First + cluster.Count; index++)
            {
                var copy = copies[index];
                table[written++] = (uint)(copy.Destination - cluster.Destination);
                table[written++] = (uint)(copy.Source - cluster.Source);
                table[written++] = (uint)(copy.Count - 1);
            }
        if (written == 0)
            return true;
        var tableAddress = GnmCommandBufferCopies.Copy(table.AsSpan(0, written));
        if (tableAddress == 0)
            return Reject(compute.Address, payload, original, "no record table copy is available");

        Span<uint> user = stackalloc uint[12];
        Span<uint> fields = stackalloc uint[4];
        var recordAddress = tableAddress;
        ulong destinationBytes = 0;
        foreach (var cluster in clusters)
        {
            original.CopyTo(user);
            Rebase(user[..4], source.Base + cluster.Source * 4, (uint)cluster.SourceRecords);
            Rebase(user.Slice(4, 4), recordAddress, (uint)cluster.Count);
            user[5] = (user[5] & ~(0x3FFFu << 16)) | (RecordStride << 16);
            Rebase(user.Slice(8, 4), destination.Base + cluster.Destination * 4, (uint)cluster.DestinationRecords);
            WriteUserData(interpreter, in packet, user);
            fields[0] = (uint)cluster.Count;
            fields[1] = 1;
            fields[2] = 1;
            fields[3] = payload[3];
            _ = dispatch(interpreter, in packet, fields);
            recordAddress += (ulong)cluster.Count * RecordStride;
            destinationBytes += cluster.DestinationRecords * 4;
        }
        WriteUserData(interpreter, in packet, original);

        if (GnmDiagnostics.Log && Interlocked.Increment(ref _traces) <= MaxTraces)
            Console.Error.WriteLine($"[GNMMODE][TRACE] Gnmx scatter copy 0x{compute.Address:X16} narrowed: groups={groups} " +
                $"clusters={clusters.Count} written=0x{destinationBytes:X} of destination=0x{destination.Base:X16}+0x{(ulong)destination.Records * 4:X} " +
                $"source=0x{source.Base:X16}+0x{(ulong)source.Records * 4:X}.");
        return true;
    }

    private static List<Cluster> Partition(Copy[] copies, uint destinationRecords, uint sourceRecords)
    {
        var splits = new List<(ulong Gap, int Index)>();
        var end = copies[0].Destination + copies[0].Count;
        for (var index = 1; index < copies.Length; index++)
        {
            if (copies[index].Destination > end && copies[index].Destination - end > ClusterGap)
                splits.Add((copies[index].Destination - end, index));
            end = Math.Max(end, copies[index].Destination + copies[index].Count);
        }
        if (splits.Count >= MaxClusters)
        {
            splits.Sort(static (left, right) => right.Gap.CompareTo(left.Gap));
            splits.RemoveRange(MaxClusters - 1, splits.Count - (MaxClusters - 1));
        }
        splits.Sort(static (left, right) => left.Index.CompareTo(right.Index));

        var clusters = new List<Cluster>(splits.Count + 1);
        var first = 0;
        for (var split = 0; split <= splits.Count; split++)
        {
            var next = split < splits.Count ? splits[split].Index : copies.Length;
            ulong destinationStart = ulong.MaxValue, destinationEnd = 0, sourceStart = ulong.MaxValue, sourceEnd = 0;
            for (var index = first; index < next; index++)
            {
                var copy = copies[index];
                destinationStart = Math.Min(destinationStart, copy.Destination);
                destinationEnd = Math.Max(destinationEnd, copy.Destination + copy.Count);
                sourceStart = Math.Min(sourceStart, copy.Source);
                sourceEnd = Math.Max(sourceEnd, copy.Source + copy.Count);
            }
            if (destinationStart < destinationRecords)
            {
                var sourceCount = sourceStart < sourceRecords ? Math.Min(sourceEnd, sourceRecords) - sourceStart : 0;
                if (sourceCount == 0)
                {
                    sourceStart = 0;
                    sourceCount = sourceRecords;
                }
                clusters.Add(new Cluster(first, next - first, destinationStart,
                    Math.Min(destinationEnd, destinationRecords) - destinationStart, sourceStart, sourceCount));
            }
            first = next;
        }
        return clusters;
    }

    private static void Rebase(Span<uint> words, ulong address, uint records)
    {
        words[0] = (uint)address;
        words[1] = (words[1] & ~0xFFu) | (uint)((address >> 32) & 0xFF);
        words[2] = records;
    }

    private static void WriteUserData(GpuCommandInterpreter interpreter, in PacketContext packet, ReadOnlySpan<uint> values)
    {
        Span<uint> fields = stackalloc uint[13];
        fields[0] = UserData + 4;
        values.CopyTo(fields[1..]);
        var normalized = packet with { Header = PacketHeader.Make(14, PacketOpcode.SetShaderRegister) };
        _ = PacketDispatchTable.Opcodes[PacketOpcode.SetShaderRegister]!(interpreter, in normalized, fields);
    }

    private static bool Reject(ulong address, ReadOnlySpan<uint> payload, ReadOnlySpan<uint> user, string reason)
    {
        if (Interlocked.Exchange(ref _rejected, 1) == 0)
            Console.Error.WriteLine($"[GNMMODE][WARN] Gnmx scatter copy 0x{address:X16} keeps its full ranges ({reason}): " +
                $"dims={string.Join("x", payload.ToArray())} user=[{string.Join(" ", user.ToArray().Select(value => value.ToString("X8")))}]. " +
                "Further occurrences are omitted.");
        return false;
    }

    private static bool IsScatterCopy(ICpuMemory memory, ulong address)
    {
        if (address == 0)
            return false;
        Span<uint> words = stackalloc uint[Code.Length];
        if (!memory.TryRead(address, MemoryMarshal.AsBytes(words)))
            return false;
        for (var index = 0; index < Code.Length; index++)
        {
            var word = index == BranchWord ? words[index] & 0xFFFF0000 : words[index];
            if (word == Code[index])
                continue;
            if (index >= 3 && Interlocked.Exchange(ref _mismatched, 1) == 0)
                Console.Error.WriteLine($"[GNMMODE][WARN] Gnmx scatter copy candidate 0x{address:X16} differs at word {index}: " +
                    $"[{string.Join(" ", words.ToArray().Select(value => value.ToString("X8")))}]. Further occurrences are omitted.");
            return false;
        }
        return true;
    }
}
