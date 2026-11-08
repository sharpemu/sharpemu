// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;

namespace SharpEmu.GnmMode.Libraries.SaveData;

public static class GnmSaveDataExports
{
    private const ulong SyntheticMountAddress = 0x00007FFFFFF10000;
    private const int ParameterError = unchecked((int)0x809F0000);
    private static int _mountReported;

    public static int Mount(CpuContext context) => MountCore(context, mount2: false);

    public static int Mount2(CpuContext context) => MountCore(context, mount2: true);

    private static int MountCore(CpuContext context, bool mount2)
    {
        var source = context[CpuRegister.Rdi];
        var resultAddress = context[CpuRegister.Rsi];
        var directoryOffset = mount2 ? 0x08UL : 0x10UL;
        var blocksOffset = mount2 ? 0x10UL : 0x20UL;
        var modeOffset = mount2 ? 0x18UL : 0x28UL;

        if (source == 0 || resultAddress == 0 ||
            !context.TryReadUInt32(source, out var userId) ||
            !context.TryReadUInt64(source + directoryOffset, out var directory) ||
            !context.TryReadUInt64(source + blocksOffset, out var blocks) ||
            !context.TryReadUInt32(source + modeOffset, out var mode) ||
            directory == 0)
            return context.SetReturn(ParameterError);

        // PS4 Mount has titleId before the directory and fingerprint after it.
        // The shared mount service expects PS5 Mount3 offsets.
        // Translate only the input ABI; keep its mount bookkeeping and output.
        var translated = new byte[0x30];
        BinaryPrimitives.WriteUInt32LittleEndian(translated, userId);
        BinaryPrimitives.WriteUInt64LittleEndian(translated.AsSpan(0x08), directory);
        BinaryPrimitives.WriteUInt64LittleEndian(translated.AsSpan(0x10), blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(translated.AsSpan(0x20), mode);

        var proxy = new CpuContext(new SyntheticMountMemory(context.Memory, translated), context.TargetGeneration);
        proxy[CpuRegister.Rdi] = SyntheticMountAddress;
        proxy[CpuRegister.Rsi] = resultAddress;
        var result = SaveDataExports.SaveDataMount3(proxy);
        if (result != 0 || Interlocked.CompareExchange(ref _mountReported, 1, 0) == 0)
            Console.Error.WriteLine($"[GNMMODE][{(result == 0 ? "SAVEDATA" : "WARN")}] {(mount2 ? "Mount2" : "Mount")} PS4->shared " +
                $"user={userId} dir=0x{directory:X} blocks={blocks} mode=0x{mode:X} result=0x{result:X8}.");
        return context.SetReturn(result);
    }

    private sealed class SyntheticMountMemory(ICpuMemory inner, byte[] mount) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            var offset = address - SyntheticMountAddress;
            if (offset < (ulong)mount.Length && (ulong)destination.Length <= (ulong)mount.Length - offset)
            {
                mount.AsSpan((int)offset, destination.Length).CopyTo(destination);
                return true;
            }

            return inner.TryRead(address, destination);
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
    }
}
