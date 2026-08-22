// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // The open-source version 7 dataset differs from version 8 only in
    // VGT_TESS_DISTRIBUTION. Reuse the common data and keep the changed table private.
    private static CompactRegisterDefaults PublicRegisterDefaultsVersion7 =>
        CreatePublicRegisterDefaultsVersion7();

    private static CompactRegisterDefaults InternalRegisterDefaultsVersion7 =>
        InternalRegisterDefaultsVersion8;

    private static CompactRegisterDefaults CreatePublicRegisterDefaultsVersion7()
    {
        var table0 = (uint[])PublicRegisterDefaultsVersion8.Table0Registers.Clone();
        for (var index = 0; index < table0.Length; index += 2)
        {
            if (table0[index] != 0x02D4u)
            {
                continue;
            }

            table0[index + 1] = 0x88101010u;
            return PublicRegisterDefaultsVersion8 with { Table0Registers = table0 };
        }

        throw new InvalidOperationException("Version 8 defaults do not contain VGT_TESS_DISTRIBUTION.");
    }
}
