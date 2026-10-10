// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter : IConcurrentResolveHost
    {
        // This partial serves the guest readers of program resolution on a thread resolving draws
        // ahead of the render thread. That thread never synchronizes GPU-owned memory: a word the
        // GPU owns abandons the resolution, which the render thread then does itself. Every
        // ownership answer it relied on is recorded and checked again when the result is taken.

        private enum ConcurrentQuery : byte
        {
            DirtyBytes,
            CleanWord,
            Resident,
            ResidentClean,
        }

        private sealed class ConcurrentAssumptions(long bufferVersion, long writebackEpoch)
        {
            public readonly long BufferVersion = bufferVersion;
            public readonly long WritebackEpoch = writebackEpoch;
            public readonly List<(ConcurrentQuery Query, ulong Address, ulong Size, bool Answer)> Answers = new();
            public bool ReadsImageOwnership;

            public void Record(ConcurrentQuery query, ulong address, ulong size, bool answer)
            {
                Answers.Add((query, address, size, answer));
                ReadsImageOwnership |= query is ConcurrentQuery.CleanWord or ConcurrentQuery.ResidentClean;
            }
        }

        // Set only on the resolving thread, around one resolution.
        [ThreadStatic]
        private static ConcurrentAssumptions? _concurrentAssumptions;

        void IConcurrentResolveHost.BeginConcurrentResolve() =>
            _concurrentAssumptions = new ConcurrentAssumptions(_bufferCache.GpuModifiedVersion, GpuWritebackEpoch.Value);

        object IConcurrentResolveHost.EndConcurrentResolve()
        {
            var assumptions = _concurrentAssumptions ?? throw new InvalidOperationException("No concurrent resolution is open.");
            _concurrentAssumptions = null;
            return assumptions;
        }

        // GPU data stored into guest memory since the resolution may have changed what it read;
        // otherwise the answers stand while the ownership they came from has not changed.
        bool IConcurrentResolveHost.IsStillCurrent(object assumptionsObject)
        {
            var assumptions = (ConcurrentAssumptions)assumptionsObject;
            if (assumptions.WritebackEpoch != GpuWritebackEpoch.Value)
            {
                return false;
            }

            if (assumptions.BufferVersion == _bufferCache.GpuModifiedVersion && !assumptions.ReadsImageOwnership)
            {
                return true;
            }

            foreach (var (query, address, size, answer) in assumptions.Answers)
            {
                var now = query switch
                {
                    ConcurrentQuery.DirtyBytes => _bufferCache.HasGpuDirtyBytes(address, size),
                    ConcurrentQuery.CleanWord => IsGpuOwnedForCleanRead(address, size),
                    ConcurrentQuery.Resident => IsGpuOwnedForResidentRead(address, size, clean: false),
                    _ => IsGpuOwnedForResidentRead(address, size, clean: true),
                };
                if (now != answer)
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsGpuOwnedForCleanRead(ulong address, ulong size) =>
            (_bufferCache.MayHaveGpuDirtyPages(address, size) && _bufferCache.HasGpuDirtyPages(address, size)) ||
            _bufferCache.HasGpuDirtyBytes(address, size) ||
            _imageCache.HasGpuModifiedImageBytes(address, size);

        private bool IsGpuOwnedForResidentRead(ulong address, ulong size, bool clean) =>
            _bufferCache.HasGpuDirtyBytes(address, size) ||
            (clean && (_bufferCache.HasGpuDirtyPages(address, size) || _imageCache.HasGpuModifiedImageBytes(address, size)));

        private bool TryReadGuestWordConcurrent(ulong address, out uint word, ConcurrentAssumptions assumptions)
        {
            word = 0;
            if (!_guestMemory.CanRead(address, sizeof(uint)))
            {
                return false;
            }

            var dirty = _bufferCache.HasGpuDirtyBytes(address, sizeof(uint));
            assumptions.Record(ConcurrentQuery.DirtyBytes, address, sizeof(uint), dirty);
            if (dirty)
            {
                throw new SubmissionScheduler.AbandonedWorkException($"The resolution reads GPU-owned memory: address=0x{address:X16}.");
            }

            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            if (!_guestMemory.TryRead(address, bytes))
            {
                return false;
            }

            word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        private bool TryReadResidentGuestBytesConcurrent(ulong address, Span<byte> destination, bool clean, ConcurrentAssumptions assumptions)
        {
            var size = (ulong)destination.Length;
            if (!_guestMemory.CanRead(address, size))
            {
                return false;
            }

            var owned = IsGpuOwnedForResidentRead(address, size, clean);
            assumptions.Record(clean ? ConcurrentQuery.ResidentClean : ConcurrentQuery.Resident, address, size, owned);
            return !owned && _guestMemory.TryRead(address, destination);
        }
    }
}
