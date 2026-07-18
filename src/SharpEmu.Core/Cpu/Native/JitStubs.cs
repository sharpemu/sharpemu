// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Core.Cpu.Native;

public static unsafe class JitStubs
{
    public const int XsaveBufferSize = 2688;

    public const ulong XsaveChkGuard = 0xDeadBeef5533CCAAu;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct JmpWithIndex
    {
        private fixed byte _code[16];

        public static ReadOnlySpan<byte> Template => new byte[]
        {
            0x68, 0x00, 0x00, 0x00, 0x00, // push <index>
            0xE9, 0x00, 0x00, 0x00, 0x00, // jmp <handler>
            0x90, 0x90, 0x90, 0x90, 0x90, 0x90 // nop padding
        };

        public static int Size => 16;

        public void Initialize()
        {
            fixed (byte* code = _code)
            {
                Template.CopyTo(new Span<byte>(code, 16));
            }
        }

        public void SetIndex(uint index)
        {
            fixed (byte* code = _code)
            {
                *(uint*)(code + 1) = index;
            }
        }

        public void SetHandler(void* handler)
        {
            fixed (byte* code = _code)
            {
                var funcAddr = (long)handler;
                var ripAddr = (long)(code + 10); // After push + jmp
                var offset64 = funcAddr - ripAddr;
                var offset32 = (uint)(ulong)offset64;

                *(uint*)(code + 6) = offset32;
            }
        }

        public byte* GetCodePointer()
        {
            fixed (byte* code = _code)
            {
                return code;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Call9
    {
        private fixed byte _code[9];

        public static ReadOnlySpan<byte> Template => new byte[]
        {
            0xE8, 0x00, 0x00, 0x00, 0x00, // call func
            0x48, 0x89, 0xC0,             // mov rax,rax
            0x90                          // nop
        };

        public static int Size => 9;

        public void Initialize()
        {
            fixed (byte* code = _code)
            {
                Template.CopyTo(new Span<byte>(code, 9));
            }
        }

        public void SetFunc(void* func)
        {
            fixed (byte* code = _code)
            {
                var funcAddr = (long)func;
                var ripAddr = (long)(code + 5); // After call instruction
                var offset64 = funcAddr - ripAddr;
                var offset32 = (uint)(ulong)offset64;

                *(uint*)(code + 1) = offset32;
            }
        }

        public byte* GetCodePointer()
        {
            fixed (byte* code = _code)
            {
                return code;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct JmpRax
    {
        private fixed byte _code[12];

        public static ReadOnlySpan<byte> Template => new byte[]
        {
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, // movabs rax,<addr>
            0xFF, 0xE0                                                   // jmp rax
        };

        public static int Size => 12;

        public void Initialize()
        {
            fixed (byte* code = _code)
            {
                Template.CopyTo(new Span<byte>(code, 12));
            }
        }

        public void SetTarget(void* target)
        {
            fixed (byte* code = _code)
            {
                *(ulong*)(code + 2) = (ulong)target;
            }
        }

        public byte* GetCodePointer()
        {
            fixed (byte* code = _code)
            {
                return code;
            }
        }
    }

    public static class SafeCall
    {
        public static ReadOnlySpan<byte> Template => new byte[]
        {
            0x51,
            0x52,
            0x41, 0x50,
            0x41, 0x51,
            0x41, 0x52,
            0x41, 0x53,
            0x57,
            0x56,
            0x48, 0xBF, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0xBE, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0xB9, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0xB0, 0x01,
            0x86, 0x07,
            0x84, 0xC0,
            0x75, 0xF8,
            0xB8, 0xFF, 0xFF, 0xFF, 0xFF,
            0xBA, 0xFF, 0xFF, 0xFF, 0xFF,
            0x0F, 0xAE, 0x26,
            0x48, 0x83, 0xEC, 0x08,
            0xFF, 0xD1,
            0x48, 0x83, 0xC4, 0x08,
            0x48, 0x89, 0xC1,
            0xB8, 0xFF, 0xFF, 0xFF, 0xFF,
            0xBA, 0xFF, 0xFF, 0xFF, 0xFF,
            0x0F, 0xAE, 0x2E,
            0x48, 0x89, 0xC8,
            0xC6, 0x07, 0x00,
            0x5E,
            0x5F,
            0x41, 0x5B,
            0x41, 0x5A,
            0x41, 0x59,
            0x41, 0x58,
            0x5A,
            0x59,
            0xC3
        };

        public static int Size => Template.Length; // 0x6c = 108 bytes
    }

    public static ReadOnlySpan<byte> TlsAccessPattern => new byte[]
    {
        0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00
    };

    public static void CreateJmpWithIndex(byte* location, uint index, void* handler)
    {
        
        var code = location;
        
        code[0] = 0x68;
        *(uint*)(code + 1) = index;
        
        code[5] = 0xE9;
        var handlerAddr = (long)handler;
        var ripAddr = (long)(code + 10); // After push + jmp
        var offset64 = handlerAddr - ripAddr;
        *(uint*)(code + 6) = (uint)(ulong)offset64;
        
        for (int i = 10; i < 16; i++)
        {
            code[i] = 0x90;
        }
    }

    public static void CreateCall9(byte* location, void* func)
    {
        
        var code = location;
        
        code[0] = 0xE8;
        var funcAddr = (long)func;
        var ripAddr = (long)(code + 5);
        var offset64 = funcAddr - ripAddr;
        *(uint*)(code + 1) = (uint)(ulong)offset64;
        
        code[5] = 0x48;
        code[6] = 0x89;
        code[7] = 0xC0;
        
        code[8] = 0x90;
    }

    public static void CreateJmpRax(byte* location, void* target)
    {
        
        var code = location;
        
        code[0] = 0x48;
        code[1] = 0xB8;
        *(ulong*)(code + 2) = (ulong)target;
        
        code[10] = 0xFF;
        code[11] = 0xE0;
    }

    public static List<nint> FindTlsAccessPatterns(byte* start, int length)
    {
        var results = new List<nint>();
        var pattern = TlsAccessPattern;
        var end = start + length - pattern.Length;

        for (var ptr = start; ptr <= end; ptr++)
        {
            if (MatchesPattern(ptr, pattern))
            {
                results.Add((nint)ptr);
            }
        }

        return results;
    }

    private static bool MatchesPattern(byte* ptr, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (ptr[i] != pattern[i])
                return false;
        }
        return true;
    }
}
