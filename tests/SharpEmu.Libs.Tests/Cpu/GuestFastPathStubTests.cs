// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Executes the emitted pthread_self / pthread_getspecific fast-path stubs for
/// real. The stubs are the whole point of the optimization, so the tests run
/// their machine code rather than pattern-matching bytes: a stub that answers
/// the wrong value, clobbers a caller-saved-only contract, or unbalances the
/// stack fails here instead of in a game.
/// </summary>
[Collection("GuestFastPath")]
public sealed unsafe class GuestFastPathStubTests
{
    private const ulong RaxSentinel = 0x1122334455667788UL;

    // pthread_key_create hands out keys from 1 upwards; stay above anything a
    // test that drives the real exports can reach.
    private const int KeyBase = 700;

    [Fact]
    public void SelfStub_ReturnsTheBoundGuestThreadHandle()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadSelf);
        const ulong handle = 0x0000700011223340UL;
        harness.Bind(handle);

        Assert.Equal(handle, harness.Call(0));
    }

    [Fact]
    public void SelfStub_FallsBackWhenNoGuestThreadIsBound()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadSelf);
        harness.Bind(0);

        // The fallback trampoline returns the incoming RAX untouched, so this
        // asserts both that the slow path was taken and that the guest's RAX
        // (the SysV variadic vector count) survived the attempt.
        Assert.Equal(RaxSentinel, harness.Call(0));
    }

    [Fact]
    public void GetspecificStub_ReturnsValuesWrittenThroughTheManagedApi()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadGetspecific);
        var handle = NewHandle();
        harness.Bind(handle);

        Assert.Equal(0UL, harness.Call(KeyBase + 7));

        GuestFastPath.SetSpecific(handle, KeyBase + 7, 0xDEADBEEFCAFEF00DUL);
        GuestFastPath.SetSpecific(handle, GuestFastPath.TlsSlotCount - 1, 0x55UL);

        Assert.Equal(0xDEADBEEFCAFEF00DUL, harness.Call(KeyBase + 7));
        Assert.Equal(0x55UL, harness.Call((ulong)(GuestFastPath.TlsSlotCount - 1)));
        Assert.Equal(0UL, harness.Call(KeyBase + 8));

        // pthread_key_delete must make the key read back unset everywhere.
        GuestFastPath.ClearKeyEverywhere(KeyBase + 7);
        Assert.Equal(0UL, harness.Call(KeyBase + 7));
    }

    [Fact]
    public void GetspecificStub_FallsBackForKeysTheTableCannotHold()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadGetspecific);
        harness.Bind(NewHandle());

        Assert.Equal(RaxSentinel, harness.Call(GuestFastPath.TlsSlotCount));
        Assert.Equal(RaxSentinel, harness.Call(unchecked((ulong)-1L)));
    }

    [Fact]
    public void GetspecificStub_FallsBackWhenNoThreadIsBound()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadGetspecific);
        harness.Bind(0);

        Assert.Equal(RaxSentinel, harness.Call(KeyBase + 3));
    }

    [Fact]
    public void GetspecificStub_AnswersOnAHostThreadWithNoGuestThreadBound()
    {
        if (!CanRunStubs())
        {
            return;
        }

        // The primary execution thread has no guest thread handle, and
        // KernelPthreadState answers it from its own synthetic handle; the stub
        // has to read that thread's values rather than bail.
        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadGetspecific);
        var hostHandle = NewHandle();
        harness.Bind(0);
        harness.BindHost(hostHandle);
        GuestFastPath.SetSpecific(hostHandle, KeyBase + 6, 0x777UL);

        Assert.Equal(0x777UL, harness.Call(KeyBase + 6));

        // A guest thread bound on top owns the table while it runs, and the host
        // handle comes back when it unbinds.
        var guestHandle = NewHandle();
        GuestFastPath.SetSpecific(guestHandle, KeyBase + 6, 0x888UL);
        harness.Bind(guestHandle);
        Assert.Equal(0x888UL, harness.Call(KeyBase + 6));
        harness.Bind(0);
        Assert.Equal(0x777UL, harness.Call(KeyBase + 6));
    }

    [Fact]
    public void SelfStub_StaysOnTheManagedPathForHostThreads()
    {
        if (!CanRunStubs())
        {
            return;
        }

        // pthread_self registers an unbound thread's CPU context with the guest
        // scheduler, so a host thread must never be answered by the stub.
        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadSelf);
        harness.Bind(0);
        harness.BindHost(NewHandle());

        Assert.Equal(RaxSentinel, harness.Call(0));
    }

    [Fact]
    public void GetspecificStub_ReadsTheThreadCurrentlyBound()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var harness = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadGetspecific);
        var first = NewHandle();
        var second = NewHandle();
        GuestFastPath.SetSpecific(first, KeyBase + 2, 0x1111UL);
        GuestFastPath.SetSpecific(second, KeyBase + 2, 0x2222UL);

        harness.Bind(first);
        Assert.Equal(0x1111UL, harness.Call(KeyBase + 2));

        harness.Bind(second);
        Assert.Equal(0x2222UL, harness.Call(KeyBase + 2));

        harness.Bind(first);
        Assert.Equal(0x1111UL, harness.Call(KeyBase + 2));
    }

    /// <summary>
    /// Measures the per-call cost the stubs replace. The managed gateway costs
    /// ~2.9 us/call as measured in-game with SHARPEMU_PERF_HLE=1; the stub must
    /// be orders of magnitude below that, so the bound is deliberately loose
    /// enough to survive a loaded CI machine while still failing if a stub ever
    /// regains a managed transition.
    /// </summary>
    [Fact]
    public void Stubs_CostFarLessThanTheManagedGateway()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var self = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadSelf);
        using var getspecific = new StubHarness(DirectExecutionBackend.GuestFastPathStub.PthreadGetspecific);
        var handle = NewHandle();
        self.Bind(handle);
        GuestFastPath.SetSpecific(handle, KeyBase + 4, 0x99UL);

        var selfNanos = MeasureNanosPerCall(self, 0);
        getspecific.Bind(handle);
        var getspecificNanos = MeasureNanosPerCall(getspecific, KeyBase + 4);

        Assert.True(
            selfNanos < 500,
            $"pthread_self stub cost {selfNanos:F1} ns/call; the managed gateway it replaces costs ~2900 ns/call.");
        Assert.True(
            getspecificNanos < 500,
            $"pthread_getspecific stub cost {getspecificNanos:F1} ns/call; the managed gateway costs ~2900 ns/call.");
    }

    private static double MeasureNanosPerCall(StubHarness harness, ulong argument)
    {
        const int iterations = 2_000_000;
        for (var i = 0; i < 100_000; i++)
        {
            _ = harness.Call(argument);
        }

        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            _ = harness.Call(argument);
        }

        var elapsed = Stopwatch.GetTimestamp() - start;
        // The measured loop includes the managed calli that a guest call does not
        // pay, so this is an upper bound on the stub's own cost.
        return elapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;
    }

    private const ulong MemoryCopySlowPathMarker = 0x5105_1A7B_AC4E_0001UL;

    [Fact]
    public void MemoryCopyStub_CopiesForwardAndReturnsTheDestination()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var stub = new MemoryCopyStub();
        var source = Enumerable.Range(1, 300).Select(value => (byte)value).ToArray();
        var destination = new byte[300];
        fixed (byte* sourcePointer = source)
        fixed (byte* destinationPointer = destination)
        {
            Assert.Equal((ulong)destinationPointer, stub.Call(destinationPointer, sourcePointer, 300));
            Assert.Equal((ulong)destinationPointer, stub.Call(destinationPointer + 7, sourcePointer, 0) - 7);
        }

        Assert.Equal(source, destination);
    }

    [Fact]
    public void MemoryCopyStub_HandlesADestinationBelowAnOverlappingSource()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var stub = new MemoryCopyStub();
        var buffer = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        fixed (byte* pointer = buffer)
        {
            Assert.Equal((ulong)pointer, stub.Call(pointer, pointer + 8, 40));
        }

        Assert.Equal(Enumerable.Range(8, 40).Select(value => (byte)value), buffer.Take(40));
        Assert.Equal(Enumerable.Range(40, 24).Select(value => (byte)value), buffer.Skip(40));
    }

    [Fact]
    public void MemoryCopyStub_LeavesADestinationInsideTheSourceToTheManagedExport()
    {
        if (!CanRunStubs())
        {
            return;
        }

        using var stub = new MemoryCopyStub();
        var buffer = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var original = buffer.ToArray();
        fixed (byte* pointer = buffer)
        {
            Assert.Equal(MemoryCopySlowPathMarker, stub.Call(pointer + 8, pointer, 40));
            Assert.Equal(MemoryCopySlowPathMarker, stub.Call(pointer, pointer, 1));
        }

        Assert.Equal(original, buffer);
    }

    /// <summary>The memcpy stub called with the platform (SysV) convention the guest uses.</summary>
    private sealed class MemoryCopyStub : IDisposable
    {
        private readonly byte* _page;

        public MemoryCopyStub()
        {
            _page = (byte*)HostMemory.Alloc(
                null,
                4096,
                HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
                HostMemory.PAGE_EXECUTE_READWRITE);
            Assert.True(_page != null);

            // Fallback at +0x100: mov rax, marker / ret.
            _page[0x100] = 0x48;
            _page[0x101] = 0xB8;
            *(ulong*)(_page + 0x102) = MemoryCopySlowPathMarker;
            _page[0x10A] = 0xC3;

            var stub = DirectExecutionBackend.EmitGuestMemoryCopyStub((nint)(_page + 0x100));
            for (var i = 0; i < stub.Count; i++)
            {
                _page[i] = stub[i];
            }

            Assert.True(HostMemory.Protect(_page, 4096, HostMemory.PAGE_EXECUTE_READ, out _));
        }

        public ulong Call(byte* destination, byte* source, nuint count) =>
            ((delegate* unmanaged<byte*, byte*, nuint, ulong>)_page)(destination, source, count);

        public void Dispose() => _ = HostMemory.Free(_page, 0, HostMemory.MEM_RELEASE);
    }

    private static bool CanRunStubs() =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static ulong NewHandle() =>
        0x0000700020000000UL + ((ulong)Environment.CurrentManagedThreadId << 20) +
        ((ulong)Interlocked.Increment(ref _handleCounter) << 8);

    private static int _handleCounter;

    /// <summary>
    /// Owns one emitted stub, a fallback trampoline that reports "slow path
    /// taken" by returning the incoming RAX, and a caller thunk that sets up
    /// RAX/RDI the way guest code would.
    /// </summary>
    private sealed class StubHarness : IDisposable
    {
        // One native TLS key for the whole fixture: GuestFastPath publishes a
        // host thread's block exactly once, so a second key would never receive
        // it and every stub would silently take the slow path.
        private static readonly uint BlockTlsKey = EnableFastPath();

        private readonly byte* _page;
        private readonly delegate* unmanaged<ulong> _entry;
        private readonly ulong* _argumentSlot;

        private static uint EnableFastPath()
        {
            var key = PosixHostStubs.TlsAlloc();
            Assert.NotEqual(uint.MaxValue, key);
            GuestFastPath.Enable(block => PosixHostStubs.TlsSetValue(key, block));
            return key;
        }

        public StubHarness(DirectExecutionBackend.GuestFastPathStub kind)
        {
            var key = BlockTlsKey;
            // xunit reuses host threads, and the bindings are thread-local.
            GuestFastPath.UnbindCurrentThread();

            _page = (byte*)HostMemory.Alloc(
                null,
                4096,
                HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
                HostMemory.PAGE_EXECUTE_READWRITE);
            Assert.True(_page != null);

            // The argument the thunk loads into RDI lives outside the code page,
            // which becomes read-execute below.
            _argumentSlot = (ulong*)NativeMemory.AllocZeroed(sizeof(ulong));

            // Fallback trampoline at +0x100: `ret`, which hands back RAX exactly
            // as the stub left it.
            _page[0x100] = 0xC3;

            var stub = DirectExecutionBackend.EmitGuestFastPathStub(kind, key, (nint)(_page + 0x100));
            for (var i = 0; i < stub.Count; i++)
            {
                _page[0x200 + i] = stub[i];
            }

            // Caller thunk at +0: mov rax, sentinel / mov rdi, [argumentSlot] /
            // mov r11, stub / call r11 / ret. Only caller-saved registers are
            // touched, so the managed caller's state is safe.
            var offset = 0;
            Emit(ref offset, 0x48, 0xB8);
            EmitU64(ref offset, RaxSentinel);
            Emit(ref offset, 0x48, 0xBF);
            EmitU64(ref offset, (ulong)(nint)_argumentSlot);
            Emit(ref offset, 0x48, 0x8B, 0x3F); // mov rdi, [rdi]
            Emit(ref offset, 0x49, 0xBB);
            EmitU64(ref offset, (ulong)(nint)(_page + 0x200));
            Emit(ref offset, 0x41, 0xFF, 0xD3); // call r11
            Emit(ref offset, 0xC3);
            Assert.True(offset < 0x100);

            Assert.True(HostMemory.Protect(_page, 4096, HostMemory.PAGE_EXECUTE_READ, out _));
            HostMemory.FlushInstructionCache(_page, 4096);
            _entry = (delegate* unmanaged<ulong>)_page;
        }

        public void Bind(ulong guestThreadHandle) => GuestFastPath.BindGuestThread(guestThreadHandle);

        public void BindHost(ulong hostThreadHandle) => GuestFastPath.BindHostThread(hostThreadHandle);

        public ulong Call(ulong argument)
        {
            *_argumentSlot = argument;
            return _entry();
        }

        public void Dispose()
        {
            GuestFastPath.UnbindCurrentThread();
            NativeMemory.Free(_argumentSlot);
            _ = HostMemory.Free(_page, 0, HostMemory.MEM_RELEASE);
        }

        private void Emit(ref int offset, params byte[] bytes)
        {
            foreach (var value in bytes)
            {
                _page[offset++] = value;
            }
        }

        private void EmitU64(ref int offset, ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                _page[offset++] = (byte)(value >> (i * 8));
            }
        }
    }
}
