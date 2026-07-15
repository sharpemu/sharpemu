// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// Converts the RDNA2 unified FORMAT field used by GFX10 buffer and image
/// descriptors into the data-format/number-format pair used by the rest of
/// SharpEmu's AGC pipeline.
/// </summary>
public static class Gfx10UnifiedFormat
{
    public static bool TryDecode(
        uint unifiedFormat,
        out uint dataFormat,
        out uint numberFormat)
    {
        // RDNA2 ISA table 47 is intentionally sparse. In particular, the
        // integer/normalized spellings that some assemblers expose for
        // 10_11_11 and 11_11_10 are reserved by the hardware data-format
        // table; so are 10_10_10_2 USCALED/SSCALED. Keep this an exact table
        // instead of deriving ranges that accidentally make reserved encodings
        // look valid.
        (dataFormat, numberFormat) = unifiedFormat switch
        {
            0 => (0u, 0u),
            1 => (1u, 0u),
            2 => (1u, 1u),
            3 => (1u, 2u),
            4 => (1u, 3u),
            5 => (1u, 4u),
            6 => (1u, 5u),
            7 => (2u, 0u),
            8 => (2u, 1u),
            9 => (2u, 2u),
            10 => (2u, 3u),
            11 => (2u, 4u),
            12 => (2u, 5u),
            13 => (2u, 7u),
            14 => (3u, 0u),
            15 => (3u, 1u),
            16 => (3u, 2u),
            17 => (3u, 3u),
            18 => (3u, 4u),
            19 => (3u, 5u),
            20 => (4u, 4u),
            21 => (4u, 5u),
            22 => (4u, 7u),
            23 => (5u, 0u),
            24 => (5u, 1u),
            25 => (5u, 2u),
            26 => (5u, 3u),
            27 => (5u, 4u),
            28 => (5u, 5u),
            29 => (5u, 7u),
            36 => (6u, 7u),
            43 => (7u, 7u),
            44 => (8u, 0u),
            45 => (8u, 1u),
            48 => (8u, 4u),
            49 => (8u, 5u),
            50 => (9u, 0u),
            51 => (9u, 1u),
            52 => (9u, 2u),
            53 => (9u, 3u),
            54 => (9u, 4u),
            55 => (9u, 5u),
            56 => (10u, 0u),
            57 => (10u, 1u),
            58 => (10u, 2u),
            59 => (10u, 3u),
            60 => (10u, 4u),
            61 => (10u, 5u),
            62 => (11u, 4u),
            63 => (11u, 5u),
            64 => (11u, 7u),
            65 => (12u, 0u),
            66 => (12u, 1u),
            67 => (12u, 2u),
            68 => (12u, 3u),
            69 => (12u, 4u),
            70 => (12u, 5u),
            71 => (12u, 7u),
            72 => (13u, 4u),
            73 => (13u, 5u),
            74 => (13u, 7u),
            75 => (14u, 4u),
            76 => (14u, 5u),
            77 => (14u, 7u),
            128 => (1u, 9u),
            129 => (3u, 9u),
            130 => (10u, 9u),
            // Image-only encodings without a legacy DATA_FORMAT equivalent
            // retain the unified identifier for exact downstream dispatch.
            131 => (131u, 7u),
            132 => (34u, 7u),
            133 => (16u, 0u),
            134 => (17u, 0u),
            135 => (18u, 0u),
            136 => (19u, 0u),
            137 => (137u, 0u),
            138 => (138u, 0u),
            139 => (139u, 0u),
            140 => (4u, 7u),
            141 => (20u, 0u),
            142 => (20u, 4u),
            143 => (21u, 0u),
            144 => (21u, 4u),
            145 => (22u, 4u),
            146 => (22u, 7u),
            147 => (32u, 0u),
            148 => (32u, 1u),
            149 => (32u, 4u),
            150 => (32u, 9u),
            151 => (33u, 0u),
            152 => (33u, 1u),
            153 => (33u, 4u),
            154 => (33u, 9u),
            169 => (169u, 0u),
            170 => (170u, 9u),
            171 => (171u, 0u),
            172 => (172u, 9u),
            173 => (173u, 0u),
            174 => (174u, 9u),
            175 => (175u, 0u),
            176 => (176u, 1u),
            177 => (177u, 0u),
            178 => (178u, 1u),
            179 => (179u, 7u),
            180 => (180u, 7u),
            181 => (181u, 0u),
            182 => (182u, 9u),
            _ => (0u, 0u),
        };

        return unifiedFormat == 0 || dataFormat != 0;
    }
}
