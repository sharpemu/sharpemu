// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Core.Diagnostics;

public static partial class WindowsCrashCapture
{
    internal sealed class DebuggerBreakTracker
    {
        private readonly Dictionary<uint, nint> _threadStarts = new();
        private nint _libraryBase;
        private nint _breakThreadEntry;
        private nint _breakpointAddress;

        internal void Observe(DebugEvent debugEvent)
        {
            switch (debugEvent.Kind)
            {
                case CreateProcessEvent:
                    _threadStarts[debugEvent.ThreadId] = debugEvent.ProcessThreadStartAddress;
                    break;
                case CreateThreadEvent:
                    _threadStarts[debugEvent.ThreadId] = debugEvent.ThreadStartAddress;
                    break;
                case ExitThreadEvent:
                    _threadStarts.Remove(debugEvent.ThreadId);
                    break;
                case LoadLibraryEvent:
                    if (TryReadDebuggerExports(debugEvent.FileHandle, out var threadOffset, out var breakpointOffset))
                    {
                        _libraryBase = debugEvent.LoadedLibraryBase;
                        _breakThreadEntry = checked(_libraryBase + threadOffset);
                        _breakpointAddress = checked(_libraryBase + breakpointOffset);
                    }
                    break;
                case UnloadLibraryEvent when debugEvent.UnloadedLibraryBase == _libraryBase:
                    _libraryBase = _breakThreadEntry = _breakpointAddress = 0;
                    break;
            }
        }

        internal bool IsDebuggerBreak(DebugEvent debugEvent) =>
            debugEvent.Kind == ExceptionEvent && debugEvent.FirstChance != 0 &&
            debugEvent.Exception.Code == BreakpointException && _libraryBase != 0 &&
            debugEvent.Exception.Address == _breakpointAddress &&
            _threadStarts.TryGetValue(debugEvent.ThreadId, out var startAddress) && startAddress == _breakThreadEntry;
    }

    private static unsafe bool TryReadDebuggerExports(nint fileHandle, out int threadOffset, out int breakpointOffset)
    {
        threadOffset = breakpointOffset = 0;
        if (fileHandle == 0 || fileHandle == -1)
            return false;
        const int pathCapacity = 512;
        var pathBuffer = stackalloc char[pathCapacity];
        var pathLength = GetFinalPathNameByHandle(fileHandle, pathBuffer, pathCapacity, 0);
        if (pathLength == 0 || pathLength >= pathCapacity)
            return false;
        var path = new string(pathBuffer, 0, (int)pathLength);
        var systemLibrary = @"\\?\" + Path.Combine(Environment.SystemDirectory, "ntdll.dll");
        if (!string.Equals(path, systemLibrary, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            // Read the file supplied by the debug event, then use the target's load address.
            using var handle = new SafeFileHandle(fileHandle, ownsHandle: false);
            using var stream = new FileStream(handle, FileAccess.Read);
            using var image = new PEReader(stream);
            if (image.PEHeaders.PEHeader is not { Magic: PEMagic.PE32Plus } header)
                return false;
            var directory = header.ExportTableDirectory;
            if (directory.Size < 40)
                return false;
            var exports = image.GetSectionData(directory.RelativeVirtualAddress).GetReader(0, 40);
            exports.Offset = 20;
            var functionCount = exports.ReadInt32();
            var nameCount = exports.ReadInt32();
            var functionsAddress = exports.ReadInt32();
            var namesAddress = exports.ReadInt32();
            var ordinalsAddress = exports.ReadInt32();
            var functions = image.GetSectionData(functionsAddress).GetReader(0, checked(functionCount * 4));
            var names = image.GetSectionData(namesAddress).GetReader(0, checked(nameCount * 4));
            var ordinals = image.GetSectionData(ordinalsAddress).GetReader(0, checked(nameCount * 2));
            for (var nameIndex = 0; nameIndex < nameCount; nameIndex++)
            {
                var nameBlock = image.GetSectionData(names.ReadInt32());
                var nameBytes = nameBlock.GetContent(0, Math.Min(nameBlock.Length, "DbgUiRemoteBreakin".Length + 1)).AsSpan();
                var terminator = nameBytes.IndexOf((byte)0);
                var ordinal = ordinals.ReadUInt16();
                if (ordinal >= functionCount)
                    return false;
                if (terminator < 0)
                    continue;
                var name = Encoding.ASCII.GetString(nameBytes[..terminator]);
                if (name is not ("DbgUiRemoteBreakin" or "DbgBreakPoint"))
                    continue;
                functions.Offset = ordinal * 4;
                var functionOffset = functions.ReadInt32();
                // A forwarded export is not executable code in this image.
                if (functionOffset <= 0 || functionOffset >= header.SizeOfImage ||
                    (functionOffset >= directory.RelativeVirtualAddress &&
                     functionOffset - directory.RelativeVirtualAddress < directory.Size))
                    return false;
                if (name == "DbgUiRemoteBreakin")
                    threadOffset = functionOffset;
                else
                    breakpointOffset = functionOffset;
            }
            return threadOffset != 0 && breakpointOffset != 0;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or ArgumentException or OverflowException)
        {
            threadOffset = breakpointOffset = 0;
            return false;
        }
    }
}
