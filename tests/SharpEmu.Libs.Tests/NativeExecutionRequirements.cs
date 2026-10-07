// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class NativeX64FactAttribute : FactAttribute
{
    public NativeX64FactAttribute()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The native execution backend requires an x64 process.";
    }
}

public sealed class NativeX64TheoryAttribute : TheoryAttribute
{
    public NativeX64TheoryAttribute()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            Skip = "The native execution backend requires an x64 process.";
    }
}
