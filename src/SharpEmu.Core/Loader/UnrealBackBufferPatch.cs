// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using Iced.Intel;
using SharpEmu.HLE;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Unreal Engine titles size the back buffer they register with sceVideoOut from the console
/// variables <c>PS5.DefaultBackBufferWidth</c> and <c>PS5.DefaultBackBufferHeight</c>. Their
/// defaults (3840x2160 on every shipped title seen so far) are immediates compiled into the
/// registration call, and nothing the system advertises, not the output status, not a system
/// parameter, not a file, feeds them: a shipping build also ignores command-line and
/// user-settings overrides of these two variables. So a title that is told it drives a 1080p
/// display still renders every pass at 4K. When the host's display patch resolution is below
/// the compiled default, this rewrites the two immediates to that size.
/// </summary>
internal static class UnrealBackBufferPatch
{
    private const string WidthVariable = "PS5.DefaultBackBufferWidth";
    private const string HeightVariable = "PS5.DefaultBackBufferHeight";

    /// <summary>How many instructions after the name load the default immediate may sit.</summary>
    private const int ImmediateSearchDepth = 8;

    internal readonly record struct Site(ulong NameAddress, ulong ImmediateAddress, uint CurrentValue);

    internal static bool IsEnabled =>
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_UE_BACKBUFFER_PATCH"), "0", StringComparison.Ordinal);

    /// <summary>Rewrites the compiled back-buffer defaults to <paramref name="width"/> x
    /// <paramref name="height"/>. Returns true when both were rewritten.</summary>
    public static bool Apply(
        ICpuMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(programHeaders);
        if (!IsEnabled || width == 0 || height == 0)
        {
            return false;
        }

        var segments = new List<(ulong Start, byte[] Bytes, bool Executable)>();
        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load || header.FileSize == 0 || header.FileSize > int.MaxValue)
            {
                continue;
            }

            var bytes = GC.AllocateUninitializedArray<byte>((int)header.FileSize);
            var start = imageBase + header.VirtualAddress;
            if (!memory.TryRead(start, bytes))
            {
                continue;
            }

            segments.Add((start, bytes, (header.Flags & ProgramHeaderFlags.Execute) != 0));
        }

        if (!TryFindName(segments, WidthVariable, out var widthName) ||
            !TryFindName(segments, HeightVariable, out var heightName))
        {
            return false;
        }

        Site? widthSite = null;
        Site? heightSite = null;
        foreach (var (start, bytes, executable) in segments)
        {
            if (!executable)
            {
                continue;
            }

            widthSite ??= TryLocateDefault(bytes, start, widthName);
            heightSite ??= TryLocateDefault(bytes, start, heightName);
        }

        if (widthSite is not { } w || heightSite is not { } h)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Unreal back-buffer defaults not patched: found the variable names but not their " +
                $"registration (width site={(widthSite is null ? "missing" : "found")}, height site={(heightSite is null ? "missing" : "found")}).");
            return false;
        }

        if (w.CurrentValue == width && h.CurrentValue == height)
        {
            Console.WriteLine($"[LOADER] Unreal back-buffer default already {width}x{height}; nothing to patch.");
            return true;
        }

        Span<byte> immediate = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(immediate, width);
        var wrote = memory.TryWrite(w.ImmediateAddress, immediate);
        BinaryPrimitives.WriteUInt32LittleEndian(immediate, height);
        wrote &= memory.TryWrite(h.ImmediateAddress, immediate);
        if (!wrote)
        {
            Console.Error.WriteLine("[LOADER][WARN] Unreal back-buffer defaults not patched: the code segment refused the write.");
            return false;
        }

        Console.WriteLine(
            $"[LOADER] Unreal back-buffer default {w.CurrentValue}x{h.CurrentValue} -> {width}x{height} " +
            $"(PS5.DefaultBackBufferWidth at 0x{w.ImmediateAddress:X}, PS5.DefaultBackBufferHeight at 0x{h.ImmediateAddress:X}); " +
            "SHARPEMU_UE_BACKBUFFER_PATCH=0 disables.");
        return true;
    }

    /// <summary>Finds the UTF-16 console-variable name as a whole, NUL-terminated string.</summary>
    private static bool TryFindName(List<(ulong Start, byte[] Bytes, bool Executable)> segments, string name, out ulong address)
    {
        var needle = Encoding.Unicode.GetBytes(name + "\0");
        foreach (var (start, bytes, _) in segments)
        {
            var span = bytes.AsSpan();
            var searchFrom = 0;
            while (searchFrom < span.Length)
            {
                var found = span[searchFrom..].IndexOf(needle);
                if (found < 0)
                {
                    break;
                }

                var offset = searchFrom + found;
                // The name must start a string: the preceding UTF-16 unit is a NUL or the segment start.
                if (offset < 2 || (bytes[offset - 1] == 0 && bytes[offset - 2] == 0))
                {
                    address = start + (ulong)offset;
                    return true;
                }

                searchFrom = offset + 2;
            }
        }

        address = 0;
        return false;
    }

    /// <summary>
    /// Locates the registration of the variable whose name lives at <paramref name="nameAddress"/>:
    /// a <c>lea rsi, [rip+name]</c> followed within a few instructions by <c>mov edx, imm32</c>,
    /// the default passed to <c>IConsoleManager::RegisterConsoleVariable</c>.
    /// </summary>
    internal static Site? TryLocateDefault(ReadOnlySpan<byte> code, ulong codeBase, ulong nameAddress)
    {
        // lea rsi, [rip+disp32] = 48 8D 35 disp32; the displacement is relative to the next instruction.
        for (var index = 0; index + 7 <= code.Length; index++)
        {
            if (code[index] != 0x48 || code[index + 1] != 0x8D || code[index + 2] != 0x35)
            {
                continue;
            }

            var displacement = BinaryPrimitives.ReadInt32LittleEndian(code.Slice(index + 3, 4));
            var instructionEnd = codeBase + (ulong)index + 7;
            if (unchecked(instructionEnd + (ulong)(long)displacement) != nameAddress)
            {
                continue;
            }

            var windowLength = Math.Min(code.Length - index, 7 + ImmediateSearchDepth * 15);
            var reader = new ByteArrayCodeReader(code.Slice(index, windowLength).ToArray());
            var decoder = Iced.Intel.Decoder.Create(64, reader, codeBase + (ulong)index);
            for (var step = 0; step <= ImmediateSearchDepth && decoder.IP < instructionEnd + (ulong)windowLength - 7; step++)
            {
                decoder.Decode(out var instruction);
                if (instruction.IsInvalid)
                {
                    break;
                }

                if (instruction.Code == Code.Mov_r32_imm32 && instruction.Op0Register == Register.EDX && instruction.Length == 5)
                {
                    return new Site(nameAddress, instruction.IP + 1, instruction.Immediate32);
                }

                if (instruction.FlowControl != FlowControl.Next)
                {
                    break;
                }
            }
        }

        return null;
    }
}
