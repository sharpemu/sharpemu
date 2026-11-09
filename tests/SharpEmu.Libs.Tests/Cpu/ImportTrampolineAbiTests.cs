// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportTrampolineAbiTests
{
    [Fact]
    public unsafe void GeneratedTransferExit_BypassesGuestRestoreAndBalancesHostStack()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
        var code = CreateTrampolineBytes();
        byte[] exit = [0x4C, 0x8B, 0x5C, 0x24, 0x20, 0x4D, 0x85, 0xDB,
            0x74, 0x07, 0x48, 0x83, 0xC4, 0x38, 0x41, 0xFF, 0xE3];
        var position = code.AsSpan().IndexOf(exit);
        Assert.True(position >= 0);
        var guestRestore = code.AsSpan(position).IndexOf(new byte[] { 0x4C, 0x89, 0xE4 });
        Assert.True(guestRestore > exit.Length);
        var page = (byte*)HostMemory.Alloc(null, 4096,
            HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE, HostMemory.PAGE_EXECUTE_READWRITE);
        Assert.True(page != null);
        try
        {
            byte[] prefix = [0x48, 0x83, 0xEC, 0x38, 0x49, 0xBB];
            prefix.CopyTo(new Span<byte>(page, prefix.Length));
            var offset = prefix.Length;
            *(ulong*)(page + offset) = (ulong)(page + 128);
            offset += 8;
            byte[] store = [0x4C, 0x89, 0x5C, 0x24, 0x20, 0x48, 0xB8];
            store.CopyTo(new Span<byte>(page + offset, store.Length));
            offset += store.Length;
            *(ulong*)(page + offset) = 0x123456789ABCDEF0;
            offset += 8;
            code.AsSpan(position, exit.Length).CopyTo(new Span<byte>(page + offset, exit.Length));
            offset += exit.Length;
            page[offset] = 0x0F;
            page[offset + 1] = 0x0B;
            page[128] = 0xC3;
            Assert.Equal(0x123456789ABCDEF0UL, ((delegate* unmanaged<ulong>)page)());
        }
        finally
        {
            Assert.True(HostMemory.Free(page, 0, HostMemory.MEM_RELEASE));
        }
    }

    [Fact]
    public unsafe void GeneratedTrampoline_PreservesVolatileGuestState()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            // The production backend emits and initializes x86-64 callback
            // thunks, so inspect it only in an x64 test process.
            return;
        }

        var code = CreateTrampolineBytes();

        AssertContains(code, [0x48, 0x81, 0xEC, 0xB0, 0x00, 0x00, 0x00]); // sub rsp,0xB0
        AssertContains(code, [0x48, 0x89, 0x04, 0x24]);                   // mov [rsp],rax
        AssertContains(code, [0x4C, 0x89, 0x54, 0x24, 0x08]);             // mov [rsp+8],r10
        AssertContains(code, [0x4C, 0x89, 0x5C, 0x24, 0x10]);             // mov [rsp+16],r11
        AssertContains(code, [0x0F, 0xAE, 0x5C, 0x24, 0x18]);             // stmxcsr [rsp+24]
        AssertContains(code, [0xD9, 0x7C, 0x24, 0x1C]);                   // fnstcw [rsp+28]
        AssertContains(code, [0x4C, 0x8D, 0xA4, 0x24, 0xB0, 0, 0, 0]);   // lea r12,[rsp+0xB0]

        for (var xmm = 0; xmm < 8; xmm++)
        {
            Span<byte> save = new byte[9];
            save[0] = 0xF3;
            save[1] = 0x0F;
            save[2] = 0x7F;
            save[3] = (byte)(0x84 | (xmm << 3));
            save[4] = 0x24;
            BinaryPrimitives.WriteInt32LittleEndian(save[5..], 0x30 + (xmm * 0x10));
            AssertContains(code, save);
        }

        for (var xmm = 0; xmm < 2; xmm++)
        {
            Span<byte> restore = new byte[10];
            restore[0] = 0xF3;
            restore[1] = 0x41;
            restore[2] = 0x0F;
            restore[3] = 0x6F;
            restore[4] = (byte)(0x84 | (xmm << 3));
            restore[5] = 0x24;
            BinaryPrimitives.WriteInt32LittleEndian(restore[6..], -0x80 + (xmm * 0x10));
            AssertContains(code, restore);
        }
    }

    [Fact]
    public void GeneratedTrampoline_SavesGrownHostBoundsBeforeGuestReturn()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;

        var code = CreateTrampolineBytes();
        var stateSave = code.AsSpan().IndexOf(new byte[] { 0x4C, 0x89, 0x54, 0x24, 0x30 });
        var stateRestore = code.AsSpan().IndexOf(new byte[] { 0x4C, 0x8B, 0x54, 0x24, 0x30 });
        Assert.True(stateSave >= 0 && stateRestore > stateSave);

        ReadOnlySpan<byte> saveBounds = [
            0x65, 0x4C, 0x8B, 0x1C, 0x25, 0x08, 0, 0, 0, 0x4D, 0x89, 0x5A, 0x08,
            0x65, 0x4C, 0x8B, 0x1C, 0x25, 0x10, 0, 0, 0, 0x4D, 0x89, 0x5A, 0x10];
        Assert.True(code.AsSpan(stateRestore + 5).StartsWith(saveBounds));
        var guestRestore = code.AsSpan(stateRestore).IndexOf(
            new byte[] { 0x65, 0x4C, 0x89, 0x34, 0x25, 0x08, 0, 0, 0 });
        Assert.True(guestRestore > saveBounds.Length);
    }

    private static unsafe byte[] CreateTrampolineBytes()
    {
        // GetUninitializedObject skips the field initializers, so every reference
        // field the emitter touches has to be supplied here, including the
        // per-index trampoline cache.
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));
        SetPrivateField(backend, "_importHandlerTrampolines", new List<nint>());
        SetPrivateField(backend, "_importHandlerTrampolineByIndex", new Dictionary<int, nint>());

        var createTrampoline = typeof(DirectExecutionBackend).GetMethod(
            "CreateImportHandlerTrampoline",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(createTrampoline);
        var trampoline = (nint)createTrampoline.Invoke(backend, [0])!;
        Assert.NotEqual(0, trampoline);

        try
        {
            return new ReadOnlySpan<byte>((void*)trampoline, 1024).ToArray();
        }
        finally
        {
            Assert.True(HostMemory.Free((void*)trampoline, 0, HostMemory.MEM_RELEASE));
        }
    }

    private static void SetPrivateField(DirectExecutionBackend backend, string name, object value)
    {
        var field = typeof(DirectExecutionBackend).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null, $"DirectExecutionBackend has no field '{name}'.");
        field!.SetValue(backend, value);
    }

    private static void AssertContains(ReadOnlySpan<byte> code, ReadOnlySpan<byte> expected)
    {
        Assert.True(
            code.IndexOf(expected) >= 0,
            $"Generated trampoline did not contain {Convert.ToHexString(expected)}.");
    }
}
