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

    private readonly List<GuestSpan> _touched = new();
    private readonly List<GuestSpan> _written = new();
    private readonly List<GuestSpan> _recordTouched = new();
    private readonly List<GuestSpan> _recordWritten = new();
    private bool _open;
    private bool _conservative;
    private bool _readImages;
    private bool _wroteImages;
    private bool _recordConservative;
    private bool _recordReadsImages;
    private bool _recordWritesImages;
    private long _promotions;

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

        if (_conservative || _recordConservative || _touched.Count > MaxSpans)
        {
            GpuSyncProfile.CountChain(ChainDecision.Conservative);
            Reset();
            return true;
        }

        if ((_recordReadsImages && _wroteImages) || (_recordWritesImages && (_wroteImages || _readImages)) ||
            Overlaps(_recordTouched, _written) || Overlaps(_recordWritten, _touched))
        {
            GpuSyncProfile.CountChain(ChainDecision.Overlap);
            Reset();
            return true;
        }

        GpuSyncProfile.CountChain(ChainDecision.Skipped);
        return false;
    }

    public void Commit(bool deferred, long promotions)
    {
        if (!deferred)
        {
            Reset();
            return;
        }

        _touched.AddRange(_recordTouched);
        _written.AddRange(_recordWritten);
        _readImages |= _recordReadsImages;
        _wroteImages |= _recordWritesImages;
        _conservative |= _recordConservative;
        _open = true;
        _promotions = promotions;
    }

    private void Reset()
    {
        _touched.Clear();
        _written.Clear();
        _open = false;
        _conservative = false;
        _readImages = false;
        _wroteImages = false;
    }

    private void Collect(ResolvedDispatch record, in ShaderStageResources stage)
    {
        _recordTouched.Clear();
        _recordWritten.Clear();
        _recordConservative = false;
        _recordReadsImages = false;
        _recordWritesImages = false;
        if (stage.Program is not { } program)
        {
            _recordConservative = true;
            return;
        }

        var resources = stage.Resources;
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

            var span = new GuestSpan(descriptor.Address, footprint);
            _recordTouched.Add(span);
            if (program.Buffers[index].Written)
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

        foreach (var range in resources.DeviceAddressRanges)
        {
            if (!range.Planned || range.Size == 0)
            {
                _recordConservative = true;
                continue;
            }

            var span = new GuestSpan(range.Base, range.Size);
            _recordTouched.Add(span);
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
            _recordTouched.Add(new GuestSpan(record.IndirectArgumentsAddress, IndirectArgumentBytes));
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
    Count,
}
