// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Rendering;

internal sealed class DispatchChain
{
    private const int MaxSpans = 1024;
    private const ulong IndirectArgumentBytes = 12;

    // SHARPEMU_CHAIN_SAME_PROGRAM_WRITES (on unless "0"): inside a chain whose dispatches all run one program, a dispatch
    // whose only overlaps with the chain are plain writes over plain writes, or atomic accesses over atomic accesses,
    // takes no barrier. The guest orders compute dispatches only with its own wait and cache events, which end the chain
    // with a full barrier; it put none between these dispatches, so it does not rely on their order (an append through
    // an atomic counter is the common case). A read of a chain write, a write over a chain read, an atomic access against
    // a plain one, image conflicts and a different program keep the barrier.
    internal static bool SameProgramWrites { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_CHAIN_SAME_PROGRAM_WRITES"), "0", StringComparison.Ordinal);

    private readonly List<GuestSpan> _read = new();
    private readonly List<GuestSpan> _written = new();
    private readonly List<GuestSpan> _atomic = new();
    private readonly List<GuestSpan> _recordRead = new();
    private readonly List<GuestSpan> _recordWritten = new();
    private readonly List<GuestSpan> _recordAtomic = new();
    private bool _open;
    private bool _conservative;
    private bool _readImages;
    private bool _wroteImages;
    private bool _recordConservative;
    private bool _recordReadsImages;
    private bool _recordWritesImages;
    private long _promotions;
    private ulong _recordProgram;
    private ulong _program;
    private bool _oneProgram;

    // The chain's distinct spans (tests).
    internal int SpanCount => _read.Count + _written.Count + _atomic.Count;

    public bool NeedsHazardBarrier(ResolvedDispatch record, in ShaderStageResources stage, long promotions)
    {
        if (_open && promotions != _promotions)
        {
            Reset();
        }

        Collect(record, in stage);
        if (!_open)
        {
            GpuSyncProfile.CountChain(record.HasStorageWrites ? ChainDecision.Start : ChainDecision.Open);
            return record.HasStorageWrites;
        }

        if (_conservative || _recordConservative || _read.Count > MaxSpans || _written.Count > MaxSpans || _atomic.Count > MaxSpans)
        {
            GpuSyncProfile.CountChain(ChainDecision.Conservative);
            Reset();
            return true;
        }

        var images = (_recordReadsImages && _wroteImages) || (_recordWritesImages && (_wroteImages || _readImages));
        // An atomic access against a plain one is ordered like any other read and write.
        var readAfterWrite = Overlaps(_recordRead, _written) || Overlaps(_recordRead, _atomic) || Overlaps(_recordAtomic, _written);
        var writeAfterRead = Overlaps(_recordWritten, _read) || Overlaps(_recordWritten, _atomic) || Overlaps(_recordAtomic, _read);
        if (!images && !readAfterWrite && !writeAfterRead)
        {
            if (!Overlaps(_recordWritten, _written) && !Overlaps(_recordAtomic, _atomic))
            {
                GpuSyncProfile.CountChain(ChainDecision.Skipped);
                return false;
            }

            // A program without a hash is never "the same program".
            if (SameProgramWrites && _oneProgram && _recordProgram != 0 && _recordProgram == _program)
            {
                GpuSyncProfile.CountChain(ChainDecision.SameProgramWrites);
                return false;
            }
        }

        GpuSyncProfile.CountChain(ChainDecision.Overlap);
        Reset();
        return true;
    }

    public void Commit(bool deferred, long promotions)
    {
        if (!deferred)
        {
            Reset();
            return;
        }

        if (!_open)
        {
            _program = _recordProgram;
            _oneProgram = true;
        }
        else if (_recordProgram != _program)
        {
            _oneProgram = false;
        }

        AddMerged(_read, _recordRead);
        AddMerged(_written, _recordWritten);
        AddMerged(_atomic, _recordAtomic);
        _readImages |= _recordReadsImages;
        _wroteImages |= _recordWritesImages;
        _conservative |= _recordConservative;
        _open = true;
        _promotions = promotions;
    }

    private void Reset()
    {
        _read.Clear();
        _written.Clear();
        _atomic.Clear();
        _open = false;
        _conservative = false;
        _readImages = false;
        _wroteImages = false;
        _oneProgram = false;
    }

    private void Collect(ResolvedDispatch record, in ShaderStageResources stage)
    {
        _recordRead.Clear();
        _recordWritten.Clear();
        _recordAtomic.Clear();
        _recordConservative = false;
        _recordReadsImages = false;
        _recordWritesImages = false;
        _recordProgram = 0;
        if (stage.Program is not { } program)
        {
            _recordConservative = true;
            return;
        }

        _recordProgram = program.Hash;
        var resources = stage.Resources;
        // Runtime callees and heap accesses can touch resources outside the snapshot below.
        // Keep their barriers until the chain can account for that complete access set.
        if (program.CallRecordCallees.Count != 0 || program.Bindings is { UsesBindlessHeap: true } ||
            stage.CallRecordRanges is { Length: > 0 })
        {
            _recordConservative = true;
            return;
        }

        if (program.Bindings?.Find(DescriptorBindingKind.GlobalDataShare) is not null)
        {
            _recordConservative = true;
        }

        var buffers = resources.Buffers;
        for (var index = 0; index < program.Buffers.Length && index < buffers.Length; index++)
        {
            var words = buffers[index];
            if (words.Length < 4)
            {
                continue;
            }

            var descriptor = BufferDescriptorWords.From(words);
            if (descriptor.Address == 0)
            {
                continue;
            }

            if (descriptor.Footprint() is not { } footprint || footprint == 0)
            {
                _recordConservative = true;
                continue;
            }

            // An atomic binding is its own kind. Otherwise only a buffer the program purely writes is not a read:
            // scalar loads and anything not marked written count as reads.
            var span = new GuestSpan(descriptor.Address, footprint);
            var access = program.Buffers[index];
            if (access.Atomic)
            {
                _recordAtomic.Add(span);
                continue;
            }

            if (!access.Written || access.Read || access.Scalar)
            {
                _recordRead.Add(span);
            }

            if (access.Written)
            {
                _recordWritten.Add(span);
            }
        }

        var images = resources.Images;
        for (var index = 0; index < program.Images.Length && index < images.Length; index++)
        {
            var words = images[index];
            if (program.Images[index].Class == ImageResourceClass.None || words.Length < 4 || new TextureDescriptorWords(words).BaseAddress == 0)
            {
                continue;
            }

            _recordReadsImages = true;
            if (program.Images[index].Written)
            {
                _recordWritesImages = true;
            }
        }

        // Device-address ranges always count as reads; written ones as writes too.
        foreach (var range in resources.DeviceAddressRanges)
        {
            if (!range.Planned || range.Size == 0)
            {
                _recordConservative = true;
                continue;
            }

            var span = new GuestSpan(range.Base, range.Size);
            _recordRead.Add(span);
            if (range.Written)
            {
                _recordWritten.Add(span);
            }
        }

        if (program.UsesDeviceAddresses && resources.DeviceAddressRanges.Length == 0)
        {
            _recordConservative = true;
        }

        if (record.IndirectArgumentsAddress != 0)
        {
            _recordRead.Add(new GuestSpan(record.IndirectArgumentsAddress, IndirectArgumentBytes));
        }
    }

    // Adds the spans, widening an overlapping or adjacent one instead of growing the list, so a long run of dispatches
    // over the same or neighbouring bytes keeps a short list.
    private static void AddMerged(List<GuestSpan> target, List<GuestSpan> spans)
    {
        foreach (var span in spans)
        {
            var merged = false;
            for (var index = 0; index < target.Count; index++)
            {
                var existing = target[index];
                if (span.Address <= existing.End && existing.Address <= span.End)
                {
                    var begin = Math.Min(span.Address, existing.Address);
                    target[index] = new GuestSpan(begin, Math.Max(span.End, existing.End) - begin);
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                target.Add(span);
            }
        }
    }

    private static bool Overlaps(List<GuestSpan> left, List<GuestSpan> right)
    {
        foreach (var span in left)
        {
            foreach (var other in right)
            {
                if (span.Address < other.End && other.Address < span.End)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal enum ChainDecision
{
    Open,
    Start,
    Skipped,
    Overlap,
    Conservative,
    SameProgramWrites,
    Count,
}
