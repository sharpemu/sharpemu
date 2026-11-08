// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeWideStringCompareTests
{
    [Theory]
    [InlineData("pNtJdE3x49E")]
    [InlineData("fV2xHER+bKE")]
    [InlineData("E8wCoUEbfzk")]
    public void IntrinsicReturnsCanonicalOrdering(string nid)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        // Bloodborne's behavior-file map tests for exactly 1, not merely > 0.
        Assert.Equal(1, Compare(nid, "c9999", "c1180", 6));
        Assert.Equal(-1, Compare(nid, "c1180", "c9999", 6));
        Assert.Equal(0, Compare(nid, "c1180", "c1180", 6));
        Assert.Equal(0, Compare(nid, "", "", 6));
        Assert.Equal(-1, Compare(nid, "", "c1180", 6));
        Assert.Equal(1, Compare(nid, "c1180", "", 6));
        Assert.Equal(-1, Compare(nid, "abc", "abcd", 6));
        Assert.Equal(1, Compare(nid, "abcd", "abc", 6));
        Assert.Equal(1, Compare(nid, "\uFFFF", "\u0001", 6));
        Assert.Equal(-1, Compare(nid, "\u0001", "\uFFFF", 6));
    }

    [Fact]
    public void WcsncmpHonorsTheCodeUnitLimit()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        Assert.Equal(0, Compare("E8wCoUEbfzk", null, null, 0));
        Assert.Equal(0, Compare("E8wCoUEbfzk", "c9999", "c1180", 1));
        Assert.Equal(1, Compare("E8wCoUEbfzk", "c9999", "c1180", 2));
        Assert.Equal(-1, Compare("E8wCoUEbfzk", "c1180", "c9999", 2));
        Assert.Equal(0, Compare("E8wCoUEbfzk", "abc", "abcd", 3));
    }

    private static unsafe int Compare(string nid, string? left, string? right, nuint count)
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(typeof(DirectExecutionBackend));
        var trampolines = typeof(DirectExecutionBackend).GetField(
            "_importHandlerTrampolines", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trampolines);
        trampolines.SetValue(backend, new List<nint>());

        var create = typeof(DirectExecutionBackend).GetMethod(
            "TryCreateNativeImportIntrinsic", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(create);
        object?[] args = [nid, null];
        Assert.True((bool)create.Invoke(backend, args)!);
        var intrinsic = (nint)args[1]!;
        byte* adapter = null;
        try
        {
            var entry = intrinsic;
            if (OperatingSystem.IsWindows())
            {
                // Translate Win64 arguments to SysV and preserve Win64's nonvolatile RDI/RSI.
                adapter = (byte*)HostMemory.Alloc(null, 4096,
                    HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
                Assert.True(adapter != null);
                ReadOnlySpan<byte> prefix = [
                    0x57, 0x56,                   // push rdi / push rsi
                    0x48, 0x83, 0xEC, 0x08,       // sub rsp,8
                    0x48, 0x89, 0xCF,             // mov rdi,rcx
                    0x48, 0x89, 0xD6,             // mov rsi,rdx
                    0x4C, 0x89, 0xC2,             // mov rdx,r8
                    0x48, 0xB8];                  // mov rax,intrinsic
                prefix.CopyTo(new Span<byte>(adapter, prefix.Length));
                *(nint*)(adapter + prefix.Length) = intrinsic;
                ReadOnlySpan<byte> suffix = [
                    0xFF, 0xD0,                   // call rax
                    0x48, 0x83, 0xC4, 0x08,       // add rsp,8
                    0x5E, 0x5F, 0xC3];            // pop rsi / pop rdi / ret
                suffix.CopyTo(new Span<byte>(adapter + prefix.Length + sizeof(nint), suffix.Length));
                Assert.True(HostMemory.Protect(adapter, 4096, HostMemory.PAGE_EXECUTE_READ, out _));
                HostMemory.FlushInstructionCache(adapter, 4096);
                entry = (nint)adapter;
            }

            fixed (char* leftPointer = left)
            fixed (char* rightPointer = right)
            {
                return ((delegate* unmanaged<char*, char*, nuint, int>)entry)(leftPointer, rightPointer, count);
            }
        }
        finally
        {
            if (adapter != null) Assert.True(HostMemory.Free(adapter, 0, HostMemory.MEM_RELEASE));
            Assert.True(HostMemory.Free((void*)intrinsic, 0, HostMemory.MEM_RELEASE));
        }
    }
}
