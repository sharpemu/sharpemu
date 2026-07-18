// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu.Metal;

// Core Graphics / Metal ABI structs passed by value through objc_msgSend. Struct
// *returns* are deliberately never used: on x86-64 (this process runs under Rosetta
// on Apple silicon) large struct returns switch to objc_msgSend_stret, and avoiding
// them entirely keeps one calling convention everywhere.
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X;
    public double Y;
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtlClearColor
{
    public double Red;
    public double Green;
    public double Blue;
    public double Alpha;
}

/// <summary>MTLTextureSwizzleChannels: one MTLTextureSwizzle byte per output
/// channel (Zero=0, One=1, Red=2, Green=3, Blue=4, Alpha=5).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MtlTextureSwizzleChannels
{
    public byte Red;
    public byte Green;
    public byte Blue;
    public byte Alpha;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtlRegion
{
    public nuint X;
    public nuint Y;
    public nuint Z;
    public nuint Width;
    public nuint Height;
    public nuint Depth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtlSize
{
    public nuint Width;
    public nuint Height;
    public nuint Depth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtlOrigin
{
    public nuint X;
    public nuint Y;
    public nuint Z;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtlScissorRect
{
    public nuint X;
    public nuint Y;
    public nuint Width;
    public nuint Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MtlViewport
{
    public double OriginX;
    public double OriginY;
    public double Width;
    public double Height;
    public double ZNear;
    public double ZFar;
}

/// <summary>
/// Objective-C runtime access for the Metal presenter: AppKit, QuartzCore, and Metal
/// through objc_msgSend, with one LibraryImport overload per distinct native
/// signature. Dependency-free by design — this plus the OS frameworks is the entire
/// Metal path, which is what keeps it NativeAOT-clean.
/// </summary>
internal static partial class MetalNative
{
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRunLoopGetMain();

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopStop(nint runLoop);

    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
    private const string AppKitFramework = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string QuartzCoreFramework = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";

    private static bool _frameworksLoaded;

    /// <summary>
    /// Makes the AppKit and QuartzCore classes visible to objc_getClass; Metal is
    /// pulled in by its own LibraryImport. Call once before any Class() lookup.
    /// </summary>
    public static void EnsureFrameworksLoaded()
    {
        if (_frameworksLoaded)
        {
            return;
        }

        NativeLibrary.Load(AppKitFramework);
        NativeLibrary.Load(QuartzCoreFramework);
        _frameworksLoaded = true;
    }

    [LibraryImport(MetalFramework)]
    public static partial nint MTLCreateSystemDefaultDevice();

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(ObjCLibrary)]
    public static partial void objc_registerClassPair(nint cls);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool class_addMethod(nint cls, nint name, nint imp, string types);

    [LibraryImport(ObjCLibrary)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjCLibrary)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    /// <summary>objc_msgSend for -gpuResourceID. MTLResourceID is a one-field
    /// 8-byte struct, returned in a register on the x86-64 ABI, so it maps to a
    /// ulong return — the value written into a Tier 2 argument buffer slot.</summary>
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial ulong SendGpuResourceId(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument);


    /// <summary>objc_msgSend for a CGRect-returning selector (e.g. -bounds).
    /// A 32-byte struct is returned via the x86-64 stret ABI — a hidden
    /// pointer to caller storage passed ahead of self/_cmd — so this must not
    /// be folded into the plain objc_msgSend overloads.</summary>
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend_stret")]
    public static partial void SendStretRect(out CGRect result, nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint argument0, nint argument1, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendAtIndex(nint receiver, nint selector, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBool(nint receiver, nint selector);

    /// <summary>One-argument BOOL sends, e.g. respondsToSelector:.</summary>
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool SendBool(nint receiver, nint selector, nint argument);


    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint argument);

    /// <summary>Two-object-argument void sends, e.g. setObject:forKey:.</summary>
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint argument0, nint argument1);

    /// <summary>performSelectorOnMainThread:withObject:waitUntilDone: — the SEL
    /// to perform is itself an argument, followed by the object and the wait
    /// flag.</summary>
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidPerformSelector(
        nint receiver,
        nint selector,
        nint performedSelector,
        nint argument,
        [MarshalAs(UnmanagedType.I1)] bool waitUntilDone);

    /// <summary>setSwizzle: on MTLTextureDescriptor. Four one-byte
    /// MTLTextureSwizzle values, passed packed like the framework expects.</summary>
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidSwizzle(
        nint receiver,
        nint selector,
        MtlTextureSwizzleChannels channels);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidDouble(nint receiver, nint selector, double argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidSize(nint receiver, nint selector, CGSize size);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidRect(nint receiver, nint selector, CGRect rect);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidClearColor(nint receiver, nint selector, MtlClearColor color);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidBlendColor(
        nint receiver,
        nint selector,
        float red,
        float green,
        float blue,
        float alpha);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidViewport(nint receiver, nint selector, MtlViewport viewport);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendSetAtIndex(nint receiver, nint selector, nint value, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidCopyTexture(nint receiver, nint selector, nint source, nint destination);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendBuffer(nint receiver, nint selector, nint bytes, nuint length, nuint options);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendNewBuffer(nint receiver, nint selector, nuint length, nuint options);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendCopyTextureToBuffer(
        nint receiver,
        nint selector,
        nint sourceTexture,
        nuint sourceSlice,
        nuint sourceLevel,
        MtlOrigin sourceOrigin,
        MtlSize sourceSize,
        nint destinationBuffer,
        nuint destinationOffset,
        nuint destinationBytesPerRow,
        nuint destinationBytesPerImage);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendCopyBufferToTexture(
        nint receiver,
        nint selector,
        nint sourceBuffer,
        nuint sourceOffset,
        nuint sourceBytesPerRow,
        nuint sourceBytesPerImage,
        MtlSize sourceSize,
        nint destinationTexture,
        nuint destinationSlice,
        nuint destinationLevel,
        MtlOrigin destinationOrigin);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendDispatch(
        nint receiver,
        nint selector,
        MtlSize threadgroups,
        MtlSize threadsPerThreadgroup);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendSetBuffer(nint receiver, nint selector, nint buffer, nuint offset, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidScissor(nint receiver, nint selector, MtlScissorRect rect);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendDrawPrimitivesInstanced(
        nint receiver,
        nint selector,
        nuint primitiveType,
        nuint vertexStart,
        nuint vertexCount,
        nuint instanceCount);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendDrawIndexedPrimitives(
        nint receiver,
        nint selector,
        nuint primitiveType,
        nuint indexCount,
        nuint indexType,
        nint indexBuffer,
        nuint indexBufferOffset,
        nuint instanceCount);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendTimer(
        nint receiver,
        nint selector,
        double interval,
        nint target,
        nint timerSelector,
        nint userInfo,
        [MarshalAs(UnmanagedType.I1)] bool repeats);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitFrame(nint receiver, nint selector, CGRect frame);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWindow(
        nint receiver,
        nint selector,
        CGRect contentRect,
        nuint styleMask,
        nuint backing,
        [MarshalAs(UnmanagedType.I1)] bool defer);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendNextEvent(
        nint receiver,
        nint selector,
        ulong eventMask,
        nint untilDate,
        nint inMode,
        [MarshalAs(UnmanagedType.I1)] bool dequeue);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial nint SendTextureDescriptor(
        nint receiver,
        nint selector,
        nuint pixelFormat,
        nuint width,
        nuint height,
        [MarshalAs(UnmanagedType.I1)] bool mipmapped);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendReplaceRegion(
        nint receiver,
        nint selector,
        MtlRegion region,
        nuint mipmapLevel,
        nint bytes,
        nuint bytesPerRow);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    public static partial void SendDrawPrimitives(
        nint receiver,
        nint selector,
        nuint primitiveType,
        nuint vertexStart,
        nuint vertexCount);

    public static nint Class(string name) => objc_getClass(name);

    public static nint Selector(string name) => sel_registerName(name);

    /// <summary>Autoreleased NSString — only valid inside an autorelease pool
    /// unless the caller retains it.</summary>
    public static nint NsString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), Selector("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>Reads an NSString's UTF-8 contents, or null if the handle is nil.</summary>
    public static string? ReadNsString(nint nsString)
    {
        if (nsString == 0)
        {
            return null;
        }

        var utf8 = Send(nsString, Selector("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static string DescribeError(nint error)
    {
        if (error == 0)
        {
            return "unknown error";
        }

        var description = Send(error, Selector("localizedDescription"));
        var utf8 = Send(description, Selector("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8) ?? "unknown error";
    }
}
