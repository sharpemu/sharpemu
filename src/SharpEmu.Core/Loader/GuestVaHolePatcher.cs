// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

// GTA V-specific workaround for macOS 27. macOS 27 refuses host mappings in
// [0x10_0000_0000, 0x70_0000_0000), even with MAP_FIXED (EPERM). GTA V Enhanced hardcodes the
// start of the PS5 user area (0x10_0000_0000) as the base of a virtual arena it maps into with
// sceKernelBatchMap, and direct execution cannot relocate those fixed requests, so the game
// crashes at boot. Until guest addresses in that window can be translated, the hardcoded base is
// moved above the blocked window when the loader finds RAGE's arena-base store.
internal static class GuestVaHolePatcher
{
    private const ulong HardcodedUserAreaBase = 0x10_0000_0000UL;
    private const ulong DefaultRelocatedBase = 0x80_0000_0000UL;

    // movabs rax, 0x1000000000
    // lea    rdi, [rip+disp32]
    // mov    esi, imm32
    // mov    r64, r64
    // mov    [rip+disp32], rax
    // RAGE (GTA V Enhanced) stores its streaming arena base this way.
    private static readonly short[] RageArenaBaseSignature =
    [
        0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00,
        0x48, 0x8D, 0x3D, -1, -1, -1, -1,
        0xBE, -1, -1, -1, -1,
        0x48, 0x89, -1,
        0x48, 0x89, 0x05,
    ];

    public static int Patch(IVirtualMemory memory, IReadOnlyList<ProgramHeader> programHeaders, ulong imageBase)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(27) ||
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_VA_HOLE_PATCH") == "1")
        {
            return 0;
        }

        var relocatedBase = DefaultRelocatedBase;
        var overrideText = Environment.GetEnvironmentVariable("SHARPEMU_VA_HOLE_BASE");
        if (!string.IsNullOrWhiteSpace(overrideText))
        {
            relocatedBase = Convert.ToUInt64(overrideText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? overrideText[2..] : overrideText, 16);
        }

        var patched = 0;
        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load ||
                (header.Flags & ProgramHeaderFlags.Execute) == 0 ||
                header.FileSize == 0 ||
                header.FileSize > int.MaxValue)
            {
                continue;
            }

            var segmentStart = imageBase + header.VirtualAddress;
            var bytes = GC.AllocateUninitializedArray<byte>((int)header.FileSize);
            if (!memory.TryRead(segmentStart, bytes))
            {
                continue;
            }

            for (var offset = 0; offset <= bytes.Length - RageArenaBaseSignature.Length; offset++)
            {
                if (bytes[offset] != 0x48 || bytes[offset + 1] != 0xB8 || !Matches(bytes, offset))
                {
                    continue;
                }

                var site = segmentStart + (ulong)offset;
                Span<byte> immediate = stackalloc byte[8];
                BitConverter.TryWriteBytes(immediate, relocatedBase);
                var ok = memory.TryWrite(site + 2, immediate);
                Console.Error.WriteLine(
                    $"[LOADER][INFO] VA hole patch: arena base 0x{HardcodedUserAreaBase:X} -> 0x{relocatedBase:X} at 0x{site:X16} ok={ok}");
                if (ok)
                {
                    patched++;
                }
            }
        }

        return patched;
    }

    private static bool Matches(byte[] bytes, int offset)
    {
        for (var index = 0; index < RageArenaBaseSignature.Length; index++)
        {
            var expected = RageArenaBaseSignature[index];
            if (expected >= 0 && bytes[offset + index] != expected)
            {
                return false;
            }
        }

        return true;
    }
}
