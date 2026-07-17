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

    /// <summary>
    /// Runs one thread of a compute kernel with the guest data buffer and the
    /// SharpEmuUniforms constant buffer bound at the caller-supplied indices
    /// (per the Gen5MslTranslator contract the uniforms index equals the
    /// global-buffer count), then returns the data buffer contents.
    /// </summary>
    public static bool TryExecuteSingleThread(
        nint library,
        string entryPoint,
        byte[] bufferContents,
        byte[] uniformsContents,
        nuint dataIndex,
        nuint uniformsIndex,
        out byte[] result,
        out string error) =>
        TryExecuteThreadgroup(
            library, entryPoint, bufferContents, uniformsContents,
            dataIndex, uniformsIndex, threadsPerThreadgroup: 1, out result, out error);

    /// <summary>Runs the kernel as a single threadgroup of
    /// <paramref name="threadsPerThreadgroup"/> threads, so wave64 fixtures can
    /// exercise both 32-wide simdgroups of one guest wave under a threadgroup
    /// barrier.</summary>
    public static bool TryExecuteThreadgroup(
        nint library,
        string entryPoint,
        byte[] bufferContents,
        byte[] uniformsContents,
        nuint dataIndex,
        nuint uniformsIndex,
        uint threadsPerThreadgroup,
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
        nint buffer;
        unsafe
        {
            fixed (byte* contents = bufferContents)
            {
                // options 0 = MTLResourceStorageModeShared: CPU-visible for readback.
                buffer = SendBuffer(
                    Device.Value,
                    Selector("newBufferWithBytes:length:options:"),
                    (nint)contents,
                    (nuint)bufferContents.Length,
                    0);
            }
        }

        nint uniforms;
        unsafe
        {
            fixed (byte* contents = uniformsContents)
            {
                uniforms = SendBuffer(
                    Device.Value,
                    Selector("newBufferWithBytes:length:options:"),
                    (nint)contents,
                    (nuint)uniformsContents.Length,
                    0);
            }
        }

        if (queue == 0 || buffer == 0 || uniforms == 0)
        {
            error = "failed to create command queue or buffer";
            return false;
        }

        var commandBuffer = Send(queue, Selector("commandBuffer"));
        var encoder = Send(commandBuffer, Selector("computeCommandEncoder"));
        SendVoid(encoder, Selector("setComputePipelineState:"), pipeline);
        SendSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), buffer, 0, dataIndex);
        SendSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), uniforms, 0, uniformsIndex);
        var oneGroup = new MtlSize { Width = 1, Height = 1, Depth = 1 };
        var threads = new MtlSize { Width = threadsPerThreadgroup, Height = 1, Depth = 1 };
        SendDispatch(encoder, Selector("dispatchThreadgroups:threadsPerThreadgroup:"), oneGroup, threads);
        SendVoid(encoder, Selector("endEncoding"));
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));

        var contentsPointer = Send(buffer, Selector("contents"));
        if (contentsPointer == 0)
        {
            error = "buffer contents unavailable after execution";
            return false;
        }

        result = new byte[bufferContents.Length];
        Marshal.Copy(contentsPointer, result, 0, result.Length);
        return true;
    }
}
