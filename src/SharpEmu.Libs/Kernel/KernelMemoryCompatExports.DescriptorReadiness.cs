// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    internal static bool TryGetFileReadEventState(
        int fileDescriptor,
        ulong lowWater,
        out bool ready,
        out ulong availableBytes,
        out ushort eventFlags)
    {
        ready = false;
        availableBytes = 0;
        eventFlags = 0;

        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(fileDescriptor, out var stream) ||
                !stream.CanRead)
            {
                return false;
            }

            try
            {
                var remaining = Math.Max(0L, stream.Length - stream.Position);
                availableBytes = unchecked((ulong)remaining);
                if (remaining == 0)
                {
                    ready = true;
                    eventFlags = KernelEventQueueCompatExports.KernelEventFlagEof;
                }
                else
                {
                    ready = availableBytes >= Math.Max(1UL, lowWater);
                }
            }
            catch (IOException)
            {
                // The descriptor is still registered. A later poll can observe
                // it again after the host finishes the concurrent operation.
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return true;
    }
}
