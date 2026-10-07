// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Minimal Metal.framework access via objc_msgSend — just enough to compile MSL source
/// with the OS runtime compiler and dispatch a single-thread compute kernel. Kept
/// dependency-free on purpose; object lifetimes lean on process teardown, which is fine
/// for a test host.
/// </summary>
internal static partial class MetalNative
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";

    [StructLayout(LayoutKind.Sequential)]
    private struct MtlSize
    {
        public nuint Width;
        public nuint Height;
        public nuint Depth;
    }

    [LibraryImport(MetalFramework)]
    private static partial nint MTLCreateSystemDefaultDevice();

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument0, nint argument1, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendBuffer(nint receiver, nint selector, nint bytes, nuint length, nuint options);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendSetBuffer(nint receiver, nint selector, nint buffer, nuint offset, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendDispatch(nint receiver, nint selector, MtlSize threadgroups, MtlSize threadsPerThreadgroup);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendUseResource(nint receiver, nint selector, nint resource, nuint usage);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial ulong SendULong(nint receiver, nint selector);

    // MTLResourceUsageRead | MTLResourceUsageWrite for buffers reached through an argument buffer.
    private const nuint ReadWriteUsage = 3;

    private static readonly Lazy<nint> Device = new(() =>
        OperatingSystem.IsMacOS() ? MTLCreateSystemDefaultDevice() : 0);

    public static bool IsAvailable => Device.Value != 0;

    private static nint Selector(string name) => sel_registerName(name);

    private static nint NsString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(objc_getClass("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static string DescribeError(nint error)
    {
        if (error == 0)
        {
            return "unknown error";
        }

        var description = Send(error, Selector("localizedDescription"));
        var utf8 = Send(description, Selector("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8) ?? "unknown error";
    }

    /// <summary>Compiles MSL source with the OS runtime compiler.</summary>
    public static bool TryCompileLibrary(string source, out nint library, out string error)
    {
        library = 0;
        error = string.Empty;

        // Metal defaults to fast-math; GCN float semantics do not survive it, so the
        // harness compiles the way a real Metal backend must: fast-math off.
        var options = Send(Send(objc_getClass("MTLCompileOptions"), Selector("alloc")), Selector("init"));
        SendVoidBool(options, Selector("setFastMathEnabled:"), false);

        nint nsError = 0;
        library = Send(
            Device.Value,
            Selector("newLibraryWithSource:options:error:"),
            NsString(source),
            options,
            ref nsError);
        if (library == 0)
        {
            error = DescribeError(nsError);
            return false;
        }

        return true;
    }

    /// <summary>Runs a kernel compiled through a compile request: the argument buffer
    /// is filled directly from the layout with the data buffer's address and length
    /// (every buffer field points at the one data buffer), push data at slot 1.</summary>
    public static bool TryExecuteWithArgumentBuffer(
        nint library,
        string entryPoint,
        byte[] bufferContents,
        byte[] pushData,
        Gen5MslArgumentLayout layout,
        uint threadsPerThreadgroup,
        out byte[] result,
        out string error)
    {
        result = [];
        var buffer = SharedBuffer(bufferContents);
        if (buffer == 0)
        {
            error = "failed to create the data buffer";
            return false;
        }

        var address = SendULong(buffer, Selector("gpuAddress"));
        var arguments = new byte[Math.Max(layout.ByteSize, 8)];
        foreach (var field in layout.Fields)
        {
            for (var element = 0; element < field.Count; element++)
            {
                switch (field.FieldKind)
                {
                    case MslArgumentFieldKind.BufferPointers:
                        BitConverter.TryWriteBytes(arguments.AsSpan((int)field.ByteOffset + (element * 8)), address);
                        break;
                    case MslArgumentFieldKind.BufferByteCounts:
                        BitConverter.TryWriteBytes(arguments.AsSpan((int)field.ByteOffset + (element * 4)), (uint)bufferContents.Length);
                        break;
                }
            }
        }

        var argumentBuffer = SharedBuffer(arguments);
        var pushBuffer = SharedBuffer(pushData.Length == 0 ? new byte[4] : pushData);
        if (argumentBuffer == 0 || pushBuffer == 0)
        {
            error = "failed to create the argument or push buffer";
            return false;
        }

        return RunKernel(
            library,
            entryPoint,
            [(argumentBuffer, (nuint)Gen5MslArgumentLayout.ResourcesBufferIndex), (pushBuffer, (nuint)Gen5MslArgumentLayout.PushDataBufferIndex)],
            [buffer],
            threadsPerThreadgroup,
            buffer,
            bufferContents.Length,
            out result,
            out error);
    }

    // options 0 = MTLResourceStorageModeShared: CPU-visible for readback.
    private static nint SharedBuffer(byte[] contents)
    {
        unsafe
        {
            fixed (byte* bytes = contents)
            {
                return SendBuffer(
                    Device.Value,
                    Selector("newBufferWithBytes:length:options:"),
                    (nint)bytes,
                    (nuint)contents.Length,
                    0);
            }
        }
    }

    private static bool RunKernel(
        nint library,
        string entryPoint,
        IReadOnlyList<(nint Buffer, nuint Index)> bindings,
        IReadOnlyList<nint> residentBuffers,
        uint threadsPerThreadgroup,
        nint resultBuffer,
        int resultLength,
        out byte[] result,
        out string error)
    {
        result = [];
        error = string.Empty;

        var function = Send(library, Selector("newFunctionWithName:"), NsString(entryPoint));
        if (function == 0)
        {
            error = $"entry point '{entryPoint}' not found in the compiled library";
            return false;
        }

        nint nsError = 0;
        var pipeline = Send(
            Device.Value,
            Selector("newComputePipelineStateWithFunction:error:"),
            function,
            ref nsError);
        if (pipeline == 0)
        {
            error = $"pipeline creation failed: {DescribeError(nsError)}";
            return false;
        }

        var queue = Send(Device.Value, Selector("newCommandQueue"));
        if (queue == 0)
        {
            error = "failed to create the command queue";
            return false;
        }

        var commandBuffer = Send(queue, Selector("commandBuffer"));
        var encoder = Send(commandBuffer, Selector("computeCommandEncoder"));
        SendVoid(encoder, Selector("setComputePipelineState:"), pipeline);
        foreach (var (buffer, index) in bindings)
        {
            SendSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), buffer, 0, index);
        }

        foreach (var resident in residentBuffers)
        {
            SendUseResource(encoder, Selector("useResource:usage:"), resident, ReadWriteUsage);
        }

        var oneGroup = new MtlSize { Width = 1, Height = 1, Depth = 1 };
        var threads = new MtlSize { Width = threadsPerThreadgroup, Height = 1, Depth = 1 };
        SendDispatch(encoder, Selector("dispatchThreadgroups:threadsPerThreadgroup:"), oneGroup, threads);
        SendVoid(encoder, Selector("endEncoding"));
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));

        var contentsPointer = Send(resultBuffer, Selector("contents"));
        if (contentsPointer == 0)
        {
            error = "buffer contents unavailable after execution";
            return false;
        }

        result = new byte[resultLength];
        Marshal.Copy(contentsPointer, result, 0, result.Length);
        return true;
    }
}
