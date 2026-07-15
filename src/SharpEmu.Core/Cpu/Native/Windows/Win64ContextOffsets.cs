// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native.Windows;

/// <summary>
/// Byte offsets into the Win64 CONTEXT record delivered to vectored exception
/// handlers. The handlers read/write guest registers directly at these offsets
/// (no managed CONTEXT struct exists); a future POSIX backend gets a sibling
/// class for its mcontext layout.
/// </summary>
internal static class Win64ContextOffsets
{
    public const int Size = 0x4D0;
    public const int Mxcsr = 52;
    public const int Rax = 120;
    public const int Rcx = 128;
    public const int Rdx = 136;
    public const int Rbx = 144;
    public const int Rsp = 152;
    public const int Rbp = 160;
    public const int Rsi = 168;
    public const int Rdi = 176;
    public const int R8 = 184;
    public const int R9 = 192;
    public const int R10 = 200;
    public const int R11 = 208;
    public const int R12 = 216;
    public const int R13 = 224;
    public const int R14 = 232;
    public const int R15 = 240;
    public const int Rip = 248;
}
