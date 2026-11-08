// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using SharpEmu.GnmMode.Shaders;

namespace SharpEmu.GnmMode.Libraries.VideoOut;

public static class GnmVideoOutExports
{
    private const ulong Gen4FlipStatusSize = 0x40;
    private const ulong SharedFlipStatusSize = 0x80;
    private const ulong SyntheticAttributeAddress = 0x00007FFFFFF00000;
    private const ulong SyntheticBuffersAddress = SyntheticAttributeAddress + 0x1000;
    private const ulong SyntheticPointerAddress = SyntheticAttributeAddress + 0x2000;
    private readonly record struct DisplayTarget(ulong Source, ulong Address, uint Width, uint Height);
    private static readonly ConcurrentDictionary<ulong, DisplayTarget> _displayTargets = new();
    private static readonly Dictionary<(int Handle, int Index), ulong> _registeredSources = new();
    private static readonly object _registrationGate = new();

    public static int SetFlipRate(CpuContext context)
    {
        var handle = (int)context[CpuRegister.Rdi];
        var rate = (int)context[CpuRegister.Rsi];
        var result = VideoOutExports.VideoOutSetFlipRate(context);
        if (GnmDiagnostics.Log || result != 0)
            Console.Error.WriteLine($"[GNMMODE][{(result == 0 ? "TIMING" : "WARN")}] sceVideoOutSetFlipRate handle={handle} " +
                $"rate={rate} expected_hz={(rate is >= 0 and <= 2 ? 60 / (rate + 1) : 0)} result=0x{result:X8}.");
        return result;
    }

    internal static bool TryGetDisplayTarget(ulong address, uint pitch, uint allocatedHeight,
        out ulong guestAddress, out ulong destination, out uint displayHeight)
    {
        if (_displayTargets.TryGetValue(address, out var target) && pitch == target.Width &&
            allocatedHeight >= target.Height && allocatedHeight - target.Height < 64)
        {
            guestAddress = target.Source;
            destination = target.Address;
            displayHeight = target.Height;
            return true;
        }
        guestAddress = 0;
        destination = 0;
        displayHeight = 0;
        return false;
    }

    internal static void ResetDisplayTargets()
    {
        lock (_registrationGate)
        {
            _registeredSources.Clear();
            _displayTargets.Clear();
        }
        GcnImageDescriptor.InvalidateNormalizations(NormalizationInvalidation.Display);
    }

    private static ICpuMemory UnwrapMemory(ICpuMemory memory)
    {
        for (var depth = 0; depth < 4 && memory is ICpuMemoryWrapper wrapper && !ReferenceEquals(memory, wrapper.Inner); depth++)
            memory = wrapper.Inner;
        return memory;
    }

    public static int RegisterBuffers(CpuContext context)
    {
        var bufferCount = (int)context[CpuRegister.Rcx];
        var startIndex = (int)context[CpuRegister.Rsi];
        var handle = (int)context[CpuRegister.Rdi];
        var attributeAddress = context[CpuRegister.R8];
        var result = -1;
        uint ps4Width = 0, ps4Height = 0;
        List<(int Index, ulong Source, ulong Destination)>? registeredTargets = null;

        if (bufferCount is > 0 and <= 16 && startIndex >= 0 && startIndex + bufferCount <= 16 &&
            UnwrapMemory(context.Memory) is PhysicalVirtualMemory vm &&
            context.TryReadUInt32(attributeAddress, out var ps4Format) &&
            context.TryReadUInt32(attributeAddress + 4, out var ps4Tiling) && ps4Tiling == 0 &&
            context.TryReadUInt32(attributeAddress + 12, out ps4Width) &&
            context.TryReadUInt32(attributeAddress + 16, out ps4Height) &&
            TryNormalizeFormat(ps4Format, out var format) &&
            DisplayFormatRule.TryDecode(format, out var displayFormat) &&
            ps4Width is > 0 and <= 16384 && ps4Height is > 0 and <= 16384)
        {
            var footprint = TileGeometry.TextureTotalSize(displayFormat.GuestFormat, ps4Width, ps4Height,
                1, 1, GuestTileMode.RenderTarget, false);
            if (footprint.Size != 0)
            {
                var addresses = new byte[bufferCount * sizeof(ulong)];
                registeredTargets = new List<(int, ulong, ulong)>();
                for (var i = 0; i < bufferCount; i++)
                {
                    if (!context.TryReadUInt64(context[CpuRegister.Rdx] + (ulong)i * 8, out var source))
                        break;
                    var registered = source;
                    if ((source & 0xFFFF) != 0)
                    {
                        // PS4's 32 KiB placement is valid for GNM but the common Vulkan
                        // display decoder requires a 64 KiB aligned render-target base.
                        if (!TryAllocateTrackedShadow(context, (ulong)footprint.Size, out registered))
                        {
                            Console.Error.WriteLine($"[GNMMODE][ERROR] Could not allocate a tracked VideoOut shadow for buffer 0x{source:X16}.");
                            return context.SetReturn(-1);
                        }
                        if (GnmDiagnostics.Log)
                            Console.Error.WriteLine($"[GNMMODE][INFO] VideoOut shadow buffer index={startIndex + i} source=0x{source:X16} aligned=0x{registered:X16}.");
                    }
                    registeredTargets.Add((startIndex + i, source, registered));
                    BinaryPrimitives.WriteUInt64LittleEndian(addresses.AsSpan(i * 8), registered);
                }

                if (registeredTargets.Count == bufferCount && registeredTargets.Any(target => target.Source != target.Destination))
                {
                    var proxy = new CpuContext(new SyntheticBuffersMemory(context.Memory, addresses), context.TargetGeneration);
                    proxy[CpuRegister.Rdi] = context[CpuRegister.Rdi];
                    proxy[CpuRegister.Rsi] = context[CpuRegister.Rsi];
                    proxy[CpuRegister.Rdx] = SyntheticBuffersAddress;
                    proxy[CpuRegister.Rcx] = context[CpuRegister.Rcx];
                    proxy[CpuRegister.R8] = context[CpuRegister.R8];
                    result = VideoOutExports.VideoOutRegisterBuffers(proxy);
                }
            }
        }

        if (registeredTargets is null || registeredTargets.Count == 0 ||
            registeredTargets.All(target => target.Source == target.Destination))
            result = VideoOutExports.VideoOutRegisterBuffers(context);
        if (result < 0)
        {
            return result;
        }

        if (registeredTargets is not null && registeredTargets.Count == bufferCount)
        {
            lock (_registrationGate)
            {
                for (var i = 0; i < bufferCount; i++)
                    if (_registeredSources.Remove((handle, startIndex + i), out var previous))
                    {
                        if (_displayTargets.TryRemove(previous, out var old) && old.Address != previous)
                            _displayTargets.TryRemove(old.Address, out _);
                    }
                foreach (var (index, source, destination) in registeredTargets)
                {
                    _registeredSources[(handle, index)] = source;
                    var target = new DisplayTarget(source, destination, ps4Width, ps4Height);
                    _displayTargets[source] = target;
                    if (destination != source)
                        _displayTargets[destination] = target;
                }
            }
            GcnImageDescriptor.InvalidateNormalizations(NormalizationInvalidation.Display);
        }
        var originalAttribute = context[CpuRegister.R8];
        if (!context.TryReadUInt32(originalAttribute, out var originalFormat))
            return result;

        // The common Vulkan display decoder uses the PS5 64-bit encoding. PS4
        // VideoOut stores the same pixel ordering as a 32-bit attribute value.
        if (!TryNormalizeFormat(originalFormat, out var normalized))
            return result;

        if (!context.TryReadUInt32(originalAttribute + 4, out var tiling) ||
            !context.TryReadUInt32(originalAttribute + 12, out var width) ||
            !context.TryReadUInt32(originalAttribute + 16, out var height))
            return result;

        var attribute = new byte[0x50];
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), tiling);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(12), width);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(16), height);
        BinaryPrimitives.WriteUInt64LittleEndian(attribute.AsSpan(32), normalized);
        var attributeProxy = new CpuContext(new SyntheticAttributeMemory(context.Memory, attribute), context.TargetGeneration);
        attributeProxy[CpuRegister.Rdi] = context[CpuRegister.Rdi];
        attributeProxy[CpuRegister.Rsi] = (ulong)result;
        attributeProxy[CpuRegister.Rdx] = SyntheticAttributeAddress;
        attributeProxy[CpuRegister.Rcx] = 0;
        var changed = VideoOutExports.VideoOutSubmitChangeBufferAttribute2(attributeProxy);
        if (changed != 0)
            Console.Error.WriteLine($"[GNMMODE][WARN] Could not normalize VideoOut format 0x{originalFormat:X8}: result=0x{changed:X8}.");
        return result;
    }

    private static bool TryNormalizeFormat(uint format, out ulong normalized)
    {
        normalized = format switch
        {
            0x80000000 => 0x8000000000000000UL, // A8R8G8B8 -> BGRA8
            0x80002200 => 0x8000000022000000UL, // A8B8G8R8 -> RGBA8
            0x88060000 => 0x8100000600000000UL, // A2R10G10B10
            0x88000000 => 0x8100000000000000UL,
            0x88740000 => 0x8100070400000000UL,
            _ => 0UL,
        };
        return normalized != 0;
    }

    internal static bool TryAllocateTrackedShadow(CpuContext context, ulong size, out ulong address)
    {
        // The renderer binds the shadow as a GPU target. It needs tracked,
        // GPU-accessible direct backing rather than private guest memory.
        var length = (size + 0x3FFFUL) & ~0x3FFFUL;
        var pointer = new SyntheticPointerMemory(context.Memory);
        var proxy = new CpuContext(pointer, context.TargetGeneration);
        proxy[CpuRegister.Rdi] = length;
        proxy[CpuRegister.Rsi] = 0x10000;
        proxy[CpuRegister.Rdx] = 0;
        proxy[CpuRegister.Rcx] = SyntheticPointerAddress;
        address = 0;
        var allocated = KernelMemoryCompatExports.KernelAllocateMainDirectMemory(proxy);
        if (allocated != 0)
        {
            Console.Error.WriteLine($"[GNMMODE][ERROR] VideoOut direct allocation failed: result=0x{allocated:X8} size=0x{length:X}.");
            return false;
        }

        var offset = pointer.Value;
        pointer.Value = 0;
        proxy[CpuRegister.Rdi] = SyntheticPointerAddress;
        proxy[CpuRegister.Rsi] = length;
        proxy[CpuRegister.Rdx] = 0x33; // CPU and GPU read/write.
        proxy[CpuRegister.Rcx] = 0;
        proxy[CpuRegister.R8] = offset;
        proxy[CpuRegister.R9] = 0x10000;
        var mapped = KernelMemoryCompatExports.KernelMapDirectMemory(proxy);
        if (mapped == 0 && pointer.Value != 0)
        {
            address = pointer.Value;
            return true;
        }

        Console.Error.WriteLine($"[GNMMODE][ERROR] VideoOut direct mapping failed: result=0x{mapped:X8} offset=0x{offset:X} size=0x{length:X}.");
        proxy[CpuRegister.Rdi] = offset;
        proxy[CpuRegister.Rsi] = length;
        _ = KernelMemoryCompatExports.KernelReleaseDirectMemory(proxy);
        return false;
    }

    private sealed class SyntheticPointerMemory(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner => inner;

        public ulong Value { get; set; }

        public bool TryRead(ulong address, Span<byte> data)
        {
            if (address == SyntheticPointerAddress && data.Length == sizeof(ulong))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(data, Value);
                return true;
            }
            return inner.TryRead(address, data);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            if (address == SyntheticPointerAddress && data.Length == sizeof(ulong))
            {
                Value = BinaryPrimitives.ReadUInt64LittleEndian(data);
                return true;
            }
            return inner.TryWrite(address, data);
        }
    }

    private sealed class SyntheticBuffersMemory(ICpuMemory inner, byte[] addresses) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> data)
        {
            var offset = address - SyntheticBuffersAddress;
            if (offset < (ulong)addresses.Length && (ulong)data.Length <= (ulong)addresses.Length - offset)
            {
                addresses.AsSpan((int)offset, data.Length).CopyTo(data);
                return true;
            }
            return inner.TryRead(address, data);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => inner.TryWrite(address, data);
    }

    private sealed class SyntheticAttributeMemory(ICpuMemory inner, byte[] attribute) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> data)
        {
            var offset = address - SyntheticAttributeAddress;
            if (offset < (ulong)attribute.Length && (ulong)data.Length <= (ulong)attribute.Length - offset)
            {
                attribute.AsSpan((int)offset, data.Length).CopyTo(data);
                return true;
            }
            return inner.TryRead(address, data);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => inner.TryWrite(address, data);
    }

    public static int GetFlipStatus(CpuContext context)
    {
        var proxy = new CpuContext(
            new FlipStatusMemory(context.Memory, context[CpuRegister.Rsi]), context.TargetGeneration);
        proxy[CpuRegister.Rdi] = context[CpuRegister.Rdi];
        proxy[CpuRegister.Rsi] = context[CpuRegister.Rsi];
        return VideoOutExports.VideoOutGetFlipStatus(proxy);
    }

    public static int GetResolutionStatus(CpuContext context)
    {
        var proxy = new CpuContext(
            new ResolutionStatusMemory(context.Memory, context[CpuRegister.Rsi]), context.TargetGeneration);
        proxy[CpuRegister.Rdi] = context[CpuRegister.Rdi];
        proxy[CpuRegister.Rsi] = context[CpuRegister.Rsi];
        var result = VideoOutExports.VideoOutGetOutputStatus(proxy);
        return context.SetReturn(result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
            ? unchecked((int)0x80290002) : result);
    }

    private sealed class ResolutionStatusMemory(ICpuMemory inner, ulong destination) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> data) => inner.TryRead(address, data);

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            if (address != destination || data.Length != 0x30)
                return inner.TryWrite(address, data);
            var uhd = BinaryPrimitives.ReadUInt32LittleEndian(data) == 2;
            var width = uhd ? 3840u : 1920u;
            var height = uhd ? 2160u : 1080u;
            Span<byte> status = stackalloc byte[0x30];
            status.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(status, width);
            BinaryPrimitives.WriteUInt32LittleEndian(status[4..], height);
            BinaryPrimitives.WriteUInt32LittleEndian(status[8..], width);
            BinaryPrimitives.WriteUInt32LittleEndian(status[12..], height);
            data[8..16].CopyTo(status[16..24]);
            return inner.TryWrite(address, status);
        }
    }

    private sealed class FlipStatusMemory(ICpuMemory inner, ulong destination) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> data) => inner.TryRead(address, data);

        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            // Gen4's 0x40-byte status ends before Bloodborne's stack canary.
            var offset = address - destination;
            if (offset < SharedFlipStatusSize && (ulong)data.Length <= SharedFlipStatusSize - offset)
            {
                if (offset >= Gen4FlipStatusSize)
                {
                    return true;
                }

                return inner.TryWrite(address, data[..(int)Math.Min((ulong)data.Length, Gen4FlipStatusSize - offset)]);
            }

            return inner.TryWrite(address, data);
        }
    }
}
