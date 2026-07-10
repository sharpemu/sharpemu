// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ngs2;

public static class Ngs2Exports
{
    private const int OrbisNgs2ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int OrbisNgs2ErrorInvalidSystemHandle = unchecked((int)0x804A0230);
    private const int OrbisNgs2ErrorInvalidRackHandle = unchecked((int)0x804A0261);
    private const int OrbisNgs2ErrorInvalidVoiceHandle = unchecked((int)0x804A0300);
    private const ulong HandleStorageSize = 0x20;
    private const int RenderBufferInfoSize = 0x18;
    private const ulong MaximumRenderBufferSize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
    private static long _nextUid;
    private static long _renderCount;

    private sealed record SystemState(uint Uid);
    private sealed record RackState(ulong SystemHandle, uint RackId);
    private sealed record VoiceState(ulong RackHandle, uint VoiceIndex);

    [SysAbiExport(
        Nid = "mPYgU4oYpuY",
        ExportName = "sceNgs2SystemCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreateWithAllocator(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 1, ownerHandle: 0, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Systems[handle] = new SystemState(unchecked((uint)Interlocked.Increment(ref _nextUid)));
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "u-WrYDaJA3k",
        ExportName = "sceNgs2SystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Systems.Remove(handle))
            {
                return ctx.SetReturn(OrbisNgs2ErrorInvalidSystemHandle);
            }

            var rackHandles = Racks
                .Where(pair => pair.Value.SystemHandle == handle)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var rackHandle in rackHandles)
            {
                RemoveRackLocked(rackHandle);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "U546k6orxQo",
        ExportName = "sceNgs2RackCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreateWithAllocator(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.R8];
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 2, systemHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Racks[handle] = new RackState(systemHandle, rackId);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "lCqD7oycmIM",
        ExportName = "sceNgs2RackDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(handle))
            {
                return ctx.SetReturn(OrbisNgs2ErrorInvalidRackHandle);
            }

            RemoveRackLocked(handle);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "MwmHz8pAdAo",
        ExportName = "sceNgs2RackGetVoiceHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetVoiceHandle(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var voiceIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(rackHandle))
            {
                return ctx.SetReturn(OrbisNgs2ErrorInvalidRackHandle);
            }

            var existing = Voices.FirstOrDefault(
                pair => pair.Value.RackHandle == rackHandle && pair.Value.VoiceIndex == voiceIndex);
            if (existing.Key != 0)
            {
                return ctx.TryWriteUInt64(outHandleAddress, existing.Key)
                    ? ctx.SetReturn(0)
                    : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (outHandleAddress == 0)
        {
            return ctx.SetReturn(OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 4, rackHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Voices[handle] = new VoiceState(rackHandle, voiceIndex);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "uu94irFOGpA",
        ExportName = "sceNgs2VoiceControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceControl(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(
                Voices.ContainsKey(ctx[CpuRegister.Rdi]) 
                    ? 0 
                    : OrbisNgs2ErrorInvalidVoiceHandle);
        }
    }

    [SysAbiExport(
        Nid = "AbYvTOZ8Pts",
        ExportName = "sceNgs2VoiceRunCommands",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceRunCommands(CpuContext ctx) => Ngs2VoiceControl(ctx);

    [SysAbiExport(
        Nid = "i0VnXM-C9fc",
        ExportName = "sceNgs2SystemRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRender(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var bufferInfoAddress = ctx[CpuRegister.Rsi];
        var bufferInfoCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return ctx.SetReturn(OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (bufferInfoCount != 0 && bufferInfoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        for (uint i = 0; i < bufferInfoCount; i++)
        {
            var entryAddress = bufferInfoAddress + (i * RenderBufferInfoSize);
            if (!ctx.TryReadUInt64(entryAddress, out var bufferAddress) ||
                !ctx.TryReadUInt64(entryAddress + 8, out var bufferSize))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (bufferAddress != 0 && bufferSize != 0)
            {
                if (bufferSize > MaximumRenderBufferSize || !TryClearGuestBuffer(ctx, bufferAddress, bufferSize))
                {
                    return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        var count = Interlocked.Increment(ref _renderCount);
        if (ShouldTrace() && (count <= 4 || count % 10_000 == 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.render#{count} system=0x{systemHandle:X16} buffers={bufferInfoCount}");
        }

        return ctx.SetReturn(0);
    }

    private static bool TryCreateHandle(CpuContext ctx, uint type, ulong ownerHandle, out ulong handle)
    {
        handle = 0;
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleStorageSize, 16, out handle))
        {
            return false;
        }

        Span<byte> data = stackalloc byte[(int)HandleStorageSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data[0..8], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..16], ownerHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..28], type);
        return ctx.Memory.TryWrite(handle, data);
    }

    private static bool TryClearGuestBuffer(CpuContext ctx, ulong address, ulong length)
    {
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        for (ulong offset = 0; offset < length;)
        {
            var chunkSize = (int)Math.Min((ulong)zeroes.Length, length - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..chunkSize]))
            {
                return false;
            }

            offset += unchecked((uint)chunkSize);
        }

        return true;
    }

    private static void RemoveRackLocked(ulong rackHandle)
    {
        Racks.Remove(rackHandle);
        foreach (var voiceHandle in Voices
                     .Where(pair => pair.Value.RackHandle == rackHandle)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Voices.Remove(voiceHandle);
        }
    }

    private static bool ShouldTrace() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_NGS2"),
            "1",
            StringComparison.Ordinal);
}
