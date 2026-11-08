// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The bitset a program linked with run-time call records marks when a call reaches a callee the program does not hold
// (DescriptorBindingKind.CallMarks): the call still returns at once, and the host links the callees that calls reach into
// the caller's next program, so callees the tables name but no call reaches cost no compile time. A callee's bit hashes its
// address with its caller's hash; callees that share a bit only make one of them look called.
public static class CallMarks
{
    public const uint Bits = 65536;
    public const uint Words = Bits / 32;
    public const uint Bytes = Words * sizeof(uint);

    public static uint Seed(ulong callerHash) => unchecked((uint)callerHash ^ (uint)(callerHash >> 32) * 0x85EBCA77u);

    public static uint Bit(ulong address, ulong callerHash) =>
        Mix(unchecked((uint)address), unchecked((uint)(address >> 32)), Seed(callerHash));

    // The translator emits the same steps (EmitCallMark).
    public static uint Mix(uint low, uint high, uint seed)
    {
        unchecked
        {
            var value = low ^ (high * 0x9E3779B1u) ^ seed;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value & (Bits - 1);
        }
    }
}
