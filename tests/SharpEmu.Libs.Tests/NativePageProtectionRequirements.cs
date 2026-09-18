// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class NativePageProtectionFactAttribute : FactAttribute
{
    public NativePageProtectionFactAttribute()
    {
        if (Environment.SystemPageSize != 4096)
            Skip = "This test requires native 4 KiB page protection.";
    }
}

public sealed class NativePageProtectionTheoryAttribute : TheoryAttribute
{
    public NativePageProtectionTheoryAttribute()
    {
        if (Environment.SystemPageSize != 4096)
            Skip = "This test requires native 4 KiB page protection.";
    }
}
