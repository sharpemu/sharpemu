// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe class StubManager : IDisposable
{
    private readonly List<nint> _allocatedStubs = new();
    private readonly Dictionary<string, nint> _importHandlers = new();
    private readonly Dictionary<ulong, nint> _stubAddresses = new();
    private byte* _pltMemory;
    private int _pltOffset;
    private const int PltMemorySize = 1024 * 1024; // 1MB for stubs

    public StubManager()
    {
        _pltMemory = (byte*)VirtualAlloc(
            null,
            (nuint)PltMemorySize,
            AllocationType.Reserve | AllocationType.Commit,
            MemoryProtection.ExecuteReadWrite);

        if (_pltMemory == null)
        {
            throw new OutOfMemoryException("Failed to allocate executable memory for stubs");
        }

        _pltOffset = 0;
    }

    public nint CreateImportStub(ulong guestAddress, string nid, ImportHandler handler)
    {
        if (!_importHandlers.TryGetValue(nid, out var handlerPtr))
        {
            handlerPtr = CreateHandlerTrampoline(nid, handler);
            _importHandlers[nid] = handlerPtr;
        }

        var stubPtr = _pltMemory + _pltOffset;
        var stubIndex = (uint)(_stubAddresses.Count);

        JitStubs.CreateJmpWithIndex(stubPtr, stubIndex, (void*)handlerPtr);

        _pltOffset += JitStubs.JmpWithIndex.Size;
        _stubAddresses[guestAddress] = (nint)stubPtr;

        return (nint)stubPtr;
    }

    public nint CreatePltEntry(uint index, nint gotAddress)
    {
        var pltPtr = _pltMemory + _pltOffset;

        var pltCode = new byte[]
        {
            0x49, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // movabs r11,<addr>
            0x41, 0xFF, 0x73, 0x08, // push QWORD PTR [r11+8]
            0x41, 0xFF, 0x63, 0x10, // jmp QWORD PTR [r11+16]
            0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 // nop padding
        };

        *(ulong*)(pltCode.AsSpan().Slice(2).GetPinnableReference()) = (ulong)gotAddress;

        fixed (byte* src = pltCode)
        {
            Buffer.MemoryCopy(src, pltPtr, pltCode.Length, pltCode.Length);
        }

        _pltOffset += pltCode.Length;

        return (nint)pltPtr;
    }

    public nint CreateTlsHandler(delegate*<void> tlsGetAddrFunc)
    {
        var handlerPtr = _pltMemory + _pltOffset;

        JitStubs.CreateCall9(handlerPtr, (void*)tlsGetAddrFunc);

        _pltOffset += JitStubs.Call9.Size;

        return (nint)handlerPtr;
    }

    public nint CreateSafeCallTrampoline(delegate*<void> func, byte* regSaveArea, byte* lockVar)
    {
        var trampolinePtr = _pltMemory + _pltOffset;

        var template = JitStubs.SafeCall.Template;
        fixed (byte* src = template)
        {
            Buffer.MemoryCopy(src, trampolinePtr, template.Length, template.Length);
        }

        *(ulong*)(trampolinePtr + 0x0c + 2) = (ulong)lockVar;

        *(ulong*)(trampolinePtr + 0x16 + 2) = (ulong)regSaveArea;

        *(ulong*)(trampolinePtr + 0x20 + 2) = (ulong)func;

        _pltOffset += template.Length;

        return (nint)trampolinePtr;
    }

    public bool TryGetStubAddress(ulong guestAddress, out nint hostAddress)
    {
        return _stubAddresses.TryGetValue(guestAddress, out hostAddress);
    }

    public bool PatchTlsAccess(byte* guestCode, int codeSize, nint tlsHandler)
    {
        var locations = JitStubs.FindTlsAccessPatterns(guestCode, codeSize);

        foreach (var location in locations)
        {
            JitStubs.CreateCall9((byte*)location, (void*)tlsHandler);
        }

        return locations.Count > 0;
    }

    private nint CreateHandlerTrampoline(string nid, ImportHandler handler)
    {
        var trampolinePtr = _pltMemory + _pltOffset;



        var code = new List<byte>();

        code.Add(0x57);
        code.Add(0x56);
        code.Add(0x55);
        code.Add(0x53);
        code.AddRange(new byte[] { 0x41, 0x54 });
        code.AddRange(new byte[] { 0x41, 0x55 });
        code.AddRange(new byte[] { 0x41, 0x56 });
        code.AddRange(new byte[] { 0x41, 0x57 });

        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });

        int contextOffset = code.Count;
        code.AddRange(new byte[] { 0x48, 0xB9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        int handlerOffset = code.Count;
        code.AddRange(new byte[] { 0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        code.AddRange(new byte[] { 0xFF, 0xD0 });

        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });

        code.AddRange(new byte[] { 0x41, 0x5F });
        code.AddRange(new byte[] { 0x41, 0x5E });
        code.AddRange(new byte[] { 0x41, 0x5D });
        code.AddRange(new byte[] { 0x41, 0x5C });
        code.Add(0x5B);
        code.Add(0x5D);
        code.Add(0x5E);
        code.Add(0x5F);

        code.Add(0xC3);

        var codeArray = code.ToArray();
        fixed (byte* src = codeArray)
        {
            Buffer.MemoryCopy(src, trampolinePtr, codeArray.Length, codeArray.Length);
        }

        *(ulong*)(trampolinePtr + contextOffset + 2) = 0;

        var handlerPtr = Marshal.GetFunctionPointerForDelegate(handler);
        *(ulong*)(trampolinePtr + handlerOffset + 2) = (ulong)handlerPtr;

        _pltOffset += codeArray.Length;

        _allocatedStubs.Add((nint)trampolinePtr);

        return (nint)trampolinePtr;
    }

    public void Dispose()
    {
        if (_pltMemory != null)
        {
            VirtualFree(_pltMemory, 0, FreeType.Release);
            _pltMemory = null;
        }

        _allocatedStubs.Clear();
        _importHandlers.Clear();
        _stubAddresses.Clear();
    }

    private static void* VirtualAlloc(void* lpAddress, nuint dwSize, AllocationType flAllocationType, MemoryProtection flProtect) =>
        HostMemory.Alloc(lpAddress, dwSize, (uint)flAllocationType, (uint)flProtect);

    private static bool VirtualFree(void* lpAddress, nuint dwSize, FreeType dwFreeType) =>
        HostMemory.Free(lpAddress, dwSize, (uint)dwFreeType);

    [Flags]
    private enum AllocationType : uint
    {
        Commit = 0x1000,
        Reserve = 0x2000,
    }

    [Flags]
    private enum MemoryProtection : uint
    {
        ExecuteReadWrite = 0x40,
    }

    private enum FreeType : uint
    {
        Release = 0x8000,
    }

    public delegate void ImportHandler(CpuContext context);
}
