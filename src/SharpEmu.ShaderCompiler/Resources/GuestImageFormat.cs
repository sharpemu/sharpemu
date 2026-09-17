// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// Facts about the 9-bit image format field: which formats a sampled image accepts,
// their numeric class, and the format remap the emitter applies.
public static class GuestImageFormat
{
    public const uint Invalid = 0;
    public const uint Format32Uint = 20;
    public const uint Format32Sint = 21;
    public const uint Format11x2x10Uint = 34;
    public const uint MaxFormat = 182;

    public const uint ImageType1D = 8;
    public const uint ImageType2D = 9;
    public const uint ImageType3D = 10;
    public const uint ImageTypeCube = 11;
    public const uint ImageType1DArray = 12;
    public const uint ImageType2DArray = 13;
    public const uint ImageType2DMsaa = 14;
    public const uint ImageType2DMsaaArray = 15;

    // Formats a sampled image supports and their integer classes; every other format
    // is unsupported for sampling.
    private static readonly (uint Format, ImageNumericClass Class)[] SampledFormats =
    [
        (1, ImageNumericClass.Float), (5, ImageNumericClass.Uint), (7, ImageNumericClass.Float), (8, ImageNumericClass.Float),
        (11, ImageNumericClass.Uint), (12, ImageNumericClass.Sint), (13, ImageNumericClass.Float), (14, ImageNumericClass.Float),
        (15, ImageNumericClass.Float), (18, ImageNumericClass.Uint), (19, ImageNumericClass.Sint), (20, ImageNumericClass.Uint),
        (21, ImageNumericClass.Sint), (22, ImageNumericClass.Float), (23, ImageNumericClass.Float), (24, ImageNumericClass.Float),
        (27, ImageNumericClass.Uint), (28, ImageNumericClass.Sint), (29, ImageNumericClass.Float), (34, ImageNumericClass.Uint),
        (36, ImageNumericClass.Float), (50, ImageNumericClass.Float), (54, ImageNumericClass.Uint), (56, ImageNumericClass.Float),
        (57, ImageNumericClass.Float), (60, ImageNumericClass.Uint), (61, ImageNumericClass.Sint), (62, ImageNumericClass.Uint),
        (63, ImageNumericClass.Sint), (64, ImageNumericClass.Float), (65, ImageNumericClass.Float), (66, ImageNumericClass.Float),
        (69, ImageNumericClass.Uint), (70, ImageNumericClass.Sint), (71, ImageNumericClass.Float), (72, ImageNumericClass.Uint),
        (73, ImageNumericClass.Sint), (74, ImageNumericClass.Float), (75, ImageNumericClass.Uint), (76, ImageNumericClass.Sint),
        (77, ImageNumericClass.Float), (128, ImageNumericClass.Float), (129, ImageNumericClass.Float), (130, ImageNumericClass.Float),
        (132, ImageNumericClass.Float), (133, ImageNumericClass.Float), (134, ImageNumericClass.Float), (136, ImageNumericClass.Float),
        (156, ImageNumericClass.Float), (157, ImageNumericClass.Float), (158, ImageNumericClass.Float), (159, ImageNumericClass.Float),
        (160, ImageNumericClass.Float), (161, ImageNumericClass.Float), (162, ImageNumericClass.Float), (163, ImageNumericClass.Float),
        (164, ImageNumericClass.Float), (165, ImageNumericClass.Float), (166, ImageNumericClass.Float), (167, ImageNumericClass.Float),
        (168, ImageNumericClass.Float), (169, ImageNumericClass.Float), (170, ImageNumericClass.Float), (171, ImageNumericClass.Float),
        (172, ImageNumericClass.Float), (173, ImageNumericClass.Float), (174, ImageNumericClass.Float), (175, ImageNumericClass.Float),
        (176, ImageNumericClass.Float), (177, ImageNumericClass.Float), (178, ImageNumericClass.Float), (179, ImageNumericClass.Float),
        (180, ImageNumericClass.Float), (181, ImageNumericClass.Float), (182, ImageNumericClass.Float),
    ];

    public static ImageNumericClass SampledNumericClass(uint format)
    {
        foreach (var (candidate, numericClass) in SampledFormats)
        {
            if (candidate == format)
            {
                return numericClass;
            }
        }

        return ImageNumericClass.Unsupported;
    }

    // The 11_11_10 integer format is read as one 32-bit integer.
    public static uint Remap(uint format) => format == Format11x2x10Uint ? Format32Uint : format;

    public static uint ImageTypeOf(ReadOnlySpan<uint> descriptor) => (descriptor[3] >> 28) & 0xF;

    public static uint FormatOf(ReadOnlySpan<uint> descriptor) => (descriptor[1] >> 20) & 0x1FF;
}
