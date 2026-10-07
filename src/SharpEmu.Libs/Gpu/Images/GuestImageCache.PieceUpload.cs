// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Kernel;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Large layered textures are pools the game streams into one slot at a time. One CPU write
// makes the whole image dirty, and re-uploading a 256-layer BC3 array (~350 MB) cost
// ~570 ms of GPU detiling from host memory in Demon's Souls. Each (level, layer) piece's
// guest bytes are hashed before an upload, so a later refresh transfers only the pieces
// whose bytes changed.
public sealed partial class GuestImageCache
{
    private const ulong PieceHashMinimumImageSize = 16UL << 20;
    private const int PieceHashMinimumPieces = 16;
    private const ulong PieceRunMergeGap = 1UL << 20;
    private const int PieceHashChunkBytes = 1 << 20;
    private const ulong PieceLinearAlignment = 256;

    // Hashes are only worth their cost on large, many-piece textures uploaded from guest memory.
    private static ColorTransferPlan? PieceHashPlan(CachedImage image, in ImageRequest request)
    {
        ref readonly var info = ref image.Description;
        if (request.Role is not (ImageRole.Texture or ImageRole.StorageImage) || info.IsVolume || info.IsDepth ||
            info.Data.Size < PieceHashMinimumImageSize)
        {
            return null;
        }

        var plan = PlanColorTransfer(image, request.Role, TransferDirection.Upload);
        if (!plan.Valid || !plan.Tiled || plan.SwapBgra16 || plan.Tiles.Count < PieceHashMinimumPieces || plan.Tiles.Count != plan.Regions.Count)
        {
            return null;
        }

        return plan;
    }

    // Null when a piece's guest bytes cannot be read; the caller then keeps no hashes.
    private ulong[]? HashGuestPieces(in GuestSpan data, List<TileTransfer> tiles)
    {
        var hashes = new ulong[tiles.Count];
        var failed = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
        var address = data.Address;
        Parallel.For(0, tiles.Count, options,
            () => new byte[PieceHashChunkBytes],
            (index, state, chunk) =>
            {
                var tile = tiles[index];
                var hasher = new XxHash3();
                for (ulong done = 0; done < tile.TiledSize;)
                {
                    var length = (int)Math.Min((ulong)chunk.Length, tile.TiledSize - done);
                    var span = chunk.AsSpan(0, length);
                    var pieceAddress = address + tile.TiledOffset + done;
                    if (!_backing.TryReadBacking(pieceAddress, span) && !KernelMemoryCompatExports.TryReadPrtBacking(_backing, pieceAddress, span))
                    {
                        Interlocked.Exchange(ref failed, 1);
                        state.Stop();
                        return chunk;
                    }

                    hasher.Append(span);
                    done += (ulong)length;
                }

                hashes[index] = hasher.GetCurrentHashAsUInt64();
                return chunk;
            },
            _ => { });
        return failed != 0 ? null : hashes;
    }

    // True when the refresh was handled by transferring only the changed pieces.
    private bool TryUploadChangedPieces(CachedImage image, in ImageRequest request)
    {
        var previous = image.GuestPieceHashes;
        if (previous == null || image.IsBufferModified || image.IsGpuModified)
        {
            return false;
        }

        ref readonly var info = ref image.Description;
        var plan = PieceHashPlan(image, request);
        if (plan == null || plan.Tiles.Count != previous.Length || _bufferCache.HasGpuDirtyBytes(info.Data.Address, info.Data.Size))
        {
            return false;
        }

        // Hash before staging: a write racing the copy then shows up as a changed piece next time.
        var hashes = HashGuestPieces(info.Data, plan.Tiles);
        if (hashes == null)
        {
            return false;
        }

        var changed = new List<int>();
        for (var index = 0; index < hashes.Length; index++)
        {
            if (hashes[index] != previous[index])
            {
                changed.Add(index);
            }
        }

        changed.Sort((left, right) => plan.Tiles[left].TiledOffset.CompareTo(plan.Tiles[right].TiledOffset));
        for (var start = 0; start < changed.Count;)
        {
            var runBegin = plan.Tiles[changed[start]].TiledOffset;
            var runEnd = runBegin + plan.Tiles[changed[start]].TiledSize;
            var end = start + 1;
            while (end < changed.Count && plan.Tiles[changed[end]].TiledOffset <= runEnd + PieceRunMergeGap)
            {
                var tile = plan.Tiles[changed[end]];
                runEnd = Math.Max(runEnd, tile.TiledOffset + tile.TiledSize);
                end++;
            }

            UploadPieceRun(image, plan, changed.GetRange(start, end - start), runBegin, runEnd);
            start = end;
        }

        image.SetGuestPieceHashes(hashes);
        return true;
    }

    // Stages one contiguous tiled range and detiles its pieces into a compact linear scratch.
    private void UploadPieceRun(CachedImage image, ColorTransferPlan plan, List<int> pieces, ulong runBegin, ulong runEnd)
    {
        var tiles = new List<TileTransfer>(pieces.Count);
        var regions = new List<BufferImageCopy>(pieces.Count);
        ulong linear = 0;
        foreach (var index in pieces)
        {
            linear = (linear + PieceLinearAlignment - 1) & ~(PieceLinearAlignment - 1);
            var tile = plan.Tiles[index];
            tile.TiledOffset -= runBegin;
            tile.LinearOffset = linear;
            var region = plan.Regions[index];
            region.BufferOffset = linear;
            tiles.Add(tile);
            regions.Add(region);
            linear += tile.LinearSize;
        }

        var runSize = runEnd - runBegin;
        var (source, sourceOffset) = _bufferCache.ObtainBufferForImage(image.Description.Data.Address + runBegin, runSize);
        var detiled = _tiler.Detile(source.Handle, sourceOffset, runSize, linear, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tiles));
        UploadRegions(image, regions, detiled);
    }
}
