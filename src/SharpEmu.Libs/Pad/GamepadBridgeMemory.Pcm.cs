// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The controller audio part of the bridge: a Bluetooth DualSense takes its audio as HID output reports, which only the
/// launcher can write, so the sandboxed child hands the PCM over in a ring and the launcher plays it.
///
/// <see cref="PcmSlotCount"/> slots, each <see cref="PcmSlotStride"/> bytes from <see cref="PcmOffset"/> (a game may open
/// the speaker stream and a haptics stream at once; the launcher mixes them like the in-process Bluetooth path does):
///   +0    control, written by the child: uint generation (bumped by every open and close), uint active, uint sample
///         rate, int max queued bytes, uint speaker routed, ulong ring position where the stream starts (+24)
///   +64   ulong bytes written, by the child (alone on its cache line)
///   +128  ulong bytes consumed, by the launcher
///   +4096 the ring, <see cref="PcmRingBytes"/> bytes of 4-channel float frames
/// One producer (the child), one consumer (the launcher), lock-free. A full ring drops its oldest audio: the producer
/// never waits and the consumer skips ahead, so the producer publishes at most <see cref="PcmWriteChunkBytes"/> at a time
/// and the consumer treats only <see cref="PcmReadableBytes"/> behind the newest published byte as safe, re-checking
/// after each copy (a copy the producer may have overwritten is discarded).
/// </summary>
internal sealed unsafe partial class GamepadBridgeMemory
{
    public const int PcmSlotCount = 2;
    public const int PcmOffset = 8192;
    public const int PcmWrittenOffset = 64;
    public const int PcmConsumedOffset = 128;
    public const int PcmRingOffset = 4096;
    public const int PcmRingBytes = 32768;
    public const int PcmSlotStride = PcmRingOffset + PcmRingBytes;
    public const int PcmFrameBytes = 16;
    public const int PcmWriteChunkBytes = 4096;

    /// <summary>What the ring holds for the consumer: the capacity less the chunk the producer may be writing.</summary>
    public const int PcmReadableBytes = PcmRingBytes - PcmWriteChunkBytes;

    private byte* PcmSlot(int slot)
    {
        if ((uint)slot >= PcmSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return _base + PcmOffset + slot * PcmSlotStride;
    }

    // ---- child ---------------------------------------------------------------------------------------------------

    /// <summary>Starts a stream in the slot: parameters first, then the generation that tells the launcher to look.</summary>
    public void OpenPcm(int slot, uint sampleRate, int maxQueuedBytes, bool speaker)
    {
        var control = PcmSlot(slot);
        *(uint*)(control + 8) = sampleRate;
        *(int*)(control + 12) = maxQueuedBytes;
        *(uint*)(control + 16) = speaker ? 1u : 0u;
        // Audio from before this open belongs to an earlier stream; the launcher starts reading here.
        *(ulong*)(control + 24) = Volatile.Read(ref *(ulong*)(control + PcmWrittenOffset));
        Volatile.Write(ref *(uint*)(control + 4), 1);
        Interlocked.Increment(ref *(int*)control);
    }

    public void ClosePcm(int slot)
    {
        var control = PcmSlot(slot);
        Volatile.Write(ref *(uint*)(control + 4), 0);
        Interlocked.Increment(ref *(int*)control);
    }

    /// <summary>Appends whole frames; a full ring loses its oldest audio. Single producer.</summary>
    public void WritePcm(int slot, ReadOnlySpan<byte> pcm)
    {
        var control = PcmSlot(slot);
        var ring = control + PcmRingOffset;
        var written = Volatile.Read(ref *(ulong*)(control + PcmWrittenOffset));
        var remaining = pcm.Length / PcmFrameBytes * PcmFrameBytes;
        var offset = 0;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, PcmWriteChunkBytes);
            CopyIntoRing(ring, written, pcm.Slice(offset, chunk));
            written += (ulong)chunk;
            Volatile.Write(ref *(ulong*)(control + PcmWrittenOffset), written);
            offset += chunk;
            remaining -= chunk;
        }
    }

    /// <summary>How much the launcher has not consumed yet (what the child's stream reports as queued).</summary>
    public int PcmQueuedBytes(int slot)
    {
        var control = PcmSlot(slot);
        var consumed = Volatile.Read(ref *(ulong*)(control + PcmConsumedOffset));
        var written = Volatile.Read(ref *(ulong*)(control + PcmWrittenOffset));
        return written <= consumed ? 0 : (int)Math.Min(written - consumed, (ulong)PcmReadableBytes);
    }

    // ---- launcher ------------------------------------------------------------------------------------------------

    public PcmControl ReadPcmControl(int slot)
    {
        var control = PcmSlot(slot);
        var generation = (uint)Volatile.Read(ref *(int*)control);
        return new PcmControl(
            generation,
            Volatile.Read(ref *(uint*)(control + 4)) != 0,
            *(uint*)(control + 8),
            *(int*)(control + 12),
            *(uint*)(control + 16) != 0,
            *(ulong*)(control + 24));
    }

    public ulong PcmWritten(int slot) => Volatile.Read(ref *(ulong*)(PcmSlot(slot) + PcmWrittenOffset));

    public void PublishPcmConsumed(int slot, ulong consumed) =>
        Volatile.Write(ref *(ulong*)(PcmSlot(slot) + PcmConsumedOffset), consumed);

    /// <summary>
    /// Copies the audio after <paramref name="readIndex"/> in order (whole frames, at most <paramref name="destination"/>)
    /// and moves the index forward. Audio the ring has overwritten is skipped.
    /// </summary>
    public int ReadPcm(int slot, ref ulong readIndex, Span<byte> destination, Action? afterCopy = null)
    {
        var control = PcmSlot(slot);
        var ring = control + PcmRingOffset;
        var count = 0;
        while (destination.Length - count >= PcmFrameBytes)
        {
            var written = Volatile.Read(ref *(ulong*)(control + PcmWrittenOffset));
            if (readIndex >= written)
            {
                break;
            }

            if (written - readIndex > PcmReadableBytes)
            {
                readIndex = written - PcmReadableBytes;
            }

            var available = (int)Math.Min(written - readIndex, (ulong)(destination.Length - count));
            available = available / PcmFrameBytes * PcmFrameBytes;
            CopyFromRing(ring, readIndex, destination.Slice(count, available));
            afterCopy?.Invoke();
            Interlocked.MemoryBarrier();
            var after = Volatile.Read(ref *(ulong*)(control + PcmWrittenOffset));
            if (after - readIndex > PcmReadableBytes)
            {
                // The producer may have overwritten what was just copied: take the data that is left.
                readIndex = after - PcmReadableBytes;
                continue;
            }

            count += available;
            readIndex += (ulong)available;
        }

        return count;
    }

    private static void CopyIntoRing(byte* ring, ulong position, ReadOnlySpan<byte> source)
    {
        var start = (int)(position % PcmRingBytes);
        var first = Math.Min(source.Length, PcmRingBytes - start);
        source[..first].CopyTo(new Span<byte>(ring + start, first));
        source[first..].CopyTo(new Span<byte>(ring, source.Length - first));
    }

    private static void CopyFromRing(byte* ring, ulong position, Span<byte> destination)
    {
        var start = (int)(position % PcmRingBytes);
        var first = Math.Min(destination.Length, PcmRingBytes - start);
        new ReadOnlySpan<byte>(ring + start, first).CopyTo(destination);
        new ReadOnlySpan<byte>(ring, destination.Length - first).CopyTo(destination[first..]);
    }
}

/// <summary>What the child asked of a controller audio slot.</summary>
internal readonly record struct PcmControl(
    uint Generation, bool Active, uint SampleRate, int MaxQueuedBytes, bool Speaker, ulong StartPosition);
