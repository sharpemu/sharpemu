// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native.Windows;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed unsafe class WindowsFaultHandlingTests
{
    [Fact]
    public void HandlerThunkFiltersExceptionsThatMustNotEnterManagedCode()
    {
        using var memory = new FakeHostMemory();
        var handling = new WindowsFaultHandling(memory);

        var thunk = handling.CreateHandlerThunk((nint)0x1234, 1, (nint)0x5678);

        Assert.NotEqual(0, thunk);
        var code = new ReadOnlySpan<byte>((void*)thunk, 256);
        Assert.True(ContainsComparison(code, WindowsFaultCodes.ClrManagedException));
        Assert.True(ContainsComparison(code, 0xE06D7363u));
        Assert.True(ContainsComparison(code, WindowsFaultCodes.FastFail));
        Assert.True(ContainsComparison(code, WindowsFaultCodes.StackOverflow));
        Assert.Equal(HostPageProtection.ReadExecute, memory.LastProtection);
        Assert.True(memory.Flushed);

        handling.FreeThunk(thunk);
    }

    private static bool ContainsComparison(ReadOnlySpan<byte> code, uint exceptionCode)
    {
        for (var i = 0; i <= code.Length - 7; i++)
        {
            if (code[i] == 0x3D &&
                BinaryPrimitives.ReadUInt32LittleEndian(code[(i + 1)..]) == exceptionCode &&
                code[i + 5] == 0x74)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FakeHostMemory : IHostMemory, IDisposable
    {
        private void* _allocation;

        public HostPageProtection LastProtection { get; private set; }

        public bool Flushed { get; private set; }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            _ = desiredAddress;
            LastProtection = protection;
            _allocation = NativeMemory.Alloc((nuint)size);
            return (ulong)_allocation;
        }

        public bool Free(ulong address)
        {
            NativeMemory.Free((void*)address);
            _allocation = null;
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            _ = address;
            _ = size;
            LastProtection = protection;
            rawOldProtection = 0;
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
            _ = address;
            _ = size;
            Flushed = true;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            throw new NotSupportedException();

        public bool Commit(ulong address, ulong size, HostPageProtection protection) =>
            throw new NotSupportedException();

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection) =>
            throw new NotSupportedException();

        public bool Query(ulong address, out HostRegionInfo info) =>
            throw new NotSupportedException();

        public void Dispose()
        {
            if (_allocation != null)
            {
                NativeMemory.Free(_allocation);
            }
        }
    }
}
