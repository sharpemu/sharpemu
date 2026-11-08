// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iced.Intel;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

internal static partial class GuestRedZonePatcher
{
    private const int PatchCacheVersion = 1;
    private const int MaximumPatchCacheBytes = 32 << 20;
    private sealed record CachedSite(ulong Offset, int Length, bool TerminalBranch);
    private sealed record CachedPlan(int Version, string Key, PatchResult Scan, CachedSite[] Sites);

    private static string PatchCachePath(ReadOnlySpan<byte> imageData, IReadOnlyList<string>? appliedPatchIds,
        bool protectRedZone, string? directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(imageData);
        // Build identities also invalidate changes to analysis, trampolines, and the decoder.
        hash.AppendData(typeof(GuestRedZonePatcher).Module.ModuleVersionId.ToByteArray());
        hash.AppendData(typeof(Iced.Intel.Decoder).Module.ModuleVersionId.ToByteArray());
        hash.AppendData([(byte)PatchCacheVersion, (byte)(protectRedZone ? 1 : 0),
            (byte)(ShaInstructionRewrite.IsRequired ? 1 : 0), (byte)(Sse4aExtractRewrite.IsRequired ? 1 : 0)]);
        foreach (var id in appliedPatchIds ?? [])
        {
            hash.AppendData(Encoding.UTF8.GetBytes(id));
            hash.AppendData([0]);
        }
        var key = Convert.ToHexStringLower(hash.GetHashAndReset());
        return Path.Combine(directory ?? Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "redzone_cache"), key + ".bin");
    }

    private static bool TryReadPatchCache(string path, IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> headers, ulong imageBase, ulong imageSize,
        out List<PatchSite> sites, out PatchResult scan)
    {
        sites = [];
        scan = default;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length is < 32 or > MaximumPatchCacheBytes)
                return false;
            var data = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(data);
            var payload = data.AsSpan(32);
            if (!SHA256.HashData(payload).AsSpan().SequenceEqual(data.AsSpan(0, 32)))
                return false;
            var plan = JsonSerializer.Deserialize<CachedPlan>(payload);
            if (plan is null || plan.Version != PatchCacheVersion || plan.Key != Path.GetFileNameWithoutExtension(path) ||
                plan.Sites is null || plan.Scan.CandidateSites != plan.Sites.Length)
                return false;
            var executableBytes = ExecutableBytes(headers);
            var counts = new[] { plan.Scan.Functions, plan.Scan.RedZoneFunctions, plan.Scan.Instructions,
                plan.Scan.ShaInstructionCount, plan.Scan.ExtractCandidates, plan.Scan.UnrelocatableSites,
                plan.Scan.ControlFlowRefusals, plan.Scan.BranchTargetRefusals, plan.Scan.StackRefusals,
                plan.Scan.TooShortRefusals, plan.Scan.IndirectBranchRefusals };
            if ((ulong)plan.Sites.Length > executableBytes / MinimumJumpBytes ||
                counts.Any(count => count < 0 || (ulong)count > executableBytes) ||
                plan.Scan.RedZoneFunctions > plan.Scan.Functions || plan.Scan.Functions > plan.Scan.Instructions)
                return false;

            var previousEnd = 0UL;
            foreach (var entry in plan.Sites)
            {
                if (entry is null || entry.Length is < MinimumJumpBytes or > MaximumGroupedBytes ||
                    entry.Offset < previousEnd || entry.Offset > imageSize ||
                    (ulong)entry.Length > imageSize - entry.Offset || imageBase > ulong.MaxValue - entry.Offset ||
                    !headers.Any(header => header.HeaderType == ProgramHeaderType.Load &&
                        (header.Flags & ProgramHeaderFlags.Execute) != 0 && entry.Offset >= header.VirtualAddress &&
                        entry.Offset - header.VirtualAddress <= header.FileSize &&
                        (ulong)entry.Length <= header.FileSize - (entry.Offset - header.VirtualAddress)))
                    return false;
                var address = imageBase + entry.Offset;
                if (address > ulong.MaxValue - (ulong)entry.Length)
                    return false;
                var bytes = new byte[entry.Length];
                if (!memory.TryRead(address, bytes))
                    return false;
                var decoded = DecodeFunction(bytes, address, address, address + (ulong)entry.Length);
                if (decoded.Count == 0 || decoded[^1].Instruction.NextIP != address + (ulong)entry.Length)
                    return false;
                var instructions = decoded.Select(item => item.Instruction).ToList();
                Instruction? terminalBranch = null;
                if (entry.TerminalBranch)
                {
                    terminalBranch = instructions[^1];
                    if (!IsRelocatableDirectBranch(terminalBranch.Value))
                        return false;
                    instructions.RemoveAt(instructions.Count - 1);
                }
                if (instructions.Count == 0 || instructions.Any(instruction => instruction.FlowControl != FlowControl.Next) ||
                    !instructions.Any(instruction => IsFaultableGuestMemoryInstruction(instruction) || CanRewriteInstruction(instruction)) ||
                    !TryComputeShiftedCore(instructions, out var coreStart, out var coreCount))
                    return false;
                sites.Add(new PatchSite(address, entry.Length, instructions, coreStart, coreCount, terminalBranch));
                previousEnd = entry.Offset + (ulong)entry.Length;
            }
            scan = plan.Scan;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static void WritePatchCache(string path, ulong imageBase, IReadOnlyList<PatchSite> sites, PatchResult scan)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var plan = new CachedPlan(PatchCacheVersion, Path.GetFileNameWithoutExtension(path), scan,
                sites.Select(site => new CachedSite(site.Address - imageBase, site.ByteLength, site.TerminalBranch.HasValue)).ToArray());
            var payload = JsonSerializer.SerializeToUtf8Bytes(plan);
            if (payload.Length > MaximumPatchCacheBytes - 32)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(temporary))
            {
                stream.Write(SHA256.HashData(payload));
                stream.Write(payload);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unavailable cache must not prevent loading the game.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
