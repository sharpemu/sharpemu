// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // Exact Gen5 primary register defaults originally reverse-engineered by Kyty (MIT).
    // Hashes, offsets, and values match the tables consumed by the PS5 AGC SDK.
    private static RegisterDefaultGroup[] CreatePrimaryRegisterDefaults() =>
    [
        // context register groups
        new(0u, 0u, 0xE24F806Du, [new(0x202u, 0x00CC0010u)]), // CB_COLOR_CONTROL
        new(0u, 1u, 0xF6C28182u, [new(0x109u, 0x00000000u)]), // CB_DCC_CONTROL
        new(0u, 2u, 0x6F6E55A5u, [new(0x104u, 0x00000000u)]), // CB_RMI_GL2_CACHE_CONTROL
        new(0u, 3u, 0x0BC65DA4u, [new(0x08Fu, 0x00000000u)]), // CB_SHADER_MASK
        new(0u, 4u, 0x9E5AD592u, [new(0x08Eu, 0x0000000Fu)]), // CB_TARGET_MASK
        new(0u, 5u, 0xBB513B98u, [new(0x2DCu, 0x0000AA00u)]), // DB_ALPHA_TO_MASK
        new(0u, 6u, 0xAB64B23Bu, [new(0x001u, 0x00000000u)]), // DB_COUNT_CONTROL
        new(0u, 7u, 0x53C39964u, [new(0x200u, 0x00000000u)]), // DB_DEPTH_CONTROL
        new(0u, 8u, 0x01396B11u, [new(0x201u, 0x00000000u)]), // DB_EQAA
        new(0u, 9u, 0x7D42019Au, [new(0x000u, 0x00000000u)]), // DB_RENDER_CONTROL
        new(0u, 10u, 0x3548F523u, [new(0x006u, 0x00000000u)]), // PS_SHADER_SAMPLE_EXCLUSION_MASK
        new(0u, 11u, 0xF43AD28Au, [new(0x01Fu, 0x00000000u)]), // DB_RMI_L2_CACHE_CONTROL
        new(0u, 12u, 0x6DE4C312u, [new(0x203u, 0x00000000u)]), // DB_SHADER_CONTROL
        new(0u, 13u, 0x00A77AE0u, [new(0x2B0u, 0x00000000u)]), // DB_SRESULTS_COMPARE_STATE0
        new(0u, 14u, 0x00A779B7u, [new(0x2B1u, 0x00000000u)]), // DB_SRESULTS_COMPARE_STATE1
        new(0u, 15u, 0x5100100Cu, [new(0x10Cu, 0x00000000u)]), // DB_STENCILREFMASK
        new(0u, 16u, 0x59958BBAu, [new(0x10Du, 0x00000000u)]), // DB_STENCILREFMASK_BF
        new(0u, 17u, 0x0C06F17Cu, [new(0x10Bu, 0x00000000u)]), // DB_STENCIL_CONTROL
        new(0u, 18u, 0x6F104B72u, [new(0x1FFu, 0x00000000u)]), // GE_MAX_OUTPUT_PER_SUBGROUP
        new(0u, 19u, 0x25C70D9Cu, [new(0x204u, 0x00000000u)]), // PA_CL_CLIP_CNTL
        new(0u, 20u, 0x3881201Eu, [new(0x20Du, 0x00000000u)]), // PA_CL_OBJPRIM_ID_CNTL
        new(0u, 21u, 0x09AFDDAFu, [new(0x206u, 0x0000043Fu)]), // PA_CL_VTE_CNTL
        new(0u, 22u, 0x367D63CFu, [new(0x2F8u, 0x00000000u)]), // PA_SC_AA_CONFIG
        new(0u, 23u, 0x43707DB8u, [new(0x083u, 0x0000FFFFu)]), // PA_SC_CLIPRECT_RULE
        new(0u, 24u, 0xF6AE26BAu, [new(0x313u, 0x00000000u)]), // PA_SC_CONSERVATIVE_RASTERIZATION_CNTL
        new(0u, 25u, 0x1B917652u, [new(0x800003FEu, 0x00000000u)]), // PA_SC_FSR_ENABLE
        new(0u, 26u, 0x94B1E4F7u, [new(0x0EAu, 0x00000000u)]), // PA_SC_HORIZ_GRID
        new(0u, 27u, 0xE3661B6Cu, [new(0x0E9u, 0x00000000u)]), // PA_SC_LEFT_VERT_GRID
        new(0u, 28u, 0x1EB8D73Au, [new(0x292u, 0x00000002u)]), // PA_SC_MODE_CNTL_0
        new(0u, 29u, 0x15051FA3u, [new(0x293u, 0x00000000u)]), // PA_SC_MODE_CNTL_1
        new(0u, 30u, 0x9C51A7F1u, [new(0x0E8u, 0x00000000u)]), // PA_SC_RIGHT_VERT_GRID
        new(0u, 31u, 0xA20EFC70u, [new(0x080u, 0x00000000u)]), // PA_SC_WINDOW_OFFSET
        new(0u, 32u, 0x0EC09F6Eu, [new(0x211u, 0x00000000u)]), // PA_STATE_STEREO_X
        new(0u, 33u, 0x34A7D6D3u, [new(0x210u, 0x00000000u)]), // PA_STEREO_CNTL
        new(0u, 34u, 0xCE831B94u, [new(0x08Du, 0x00000000u)]), // PA_SU_HARDWARE_SCREEN_OFFSET
        new(0u, 35u, 0x5CC72A74u, [new(0x282u, 0x00000008u)]), // PA_SU_LINE_CNTL
        new(0u, 36u, 0x3B77713Cu, [new(0x281u, 0xFFFF0000u)]), // PA_SU_POINT_MINMAX
        new(0u, 37u, 0x40F64410u, [new(0x280u, 0x00080008u)]), // PA_SU_POINT_SIZE
        new(0u, 38u, 0x69441268u, [new(0x2DFu, 0x00000000u)]), // PA_SU_POLY_OFFSET_CLAMP
        new(0u, 39u, 0x2E418B83u, [new(0x2DEu, 0x000001E9u)]), // PA_SU_POLY_OFFSET_DB_FMT_CNTL
        new(0u, 40u, 0xA00D0C8Du, [new(0x205u, 0x00000240u)]), // PA_SU_SC_MODE_CNTL
        new(0u, 41u, 0xB1289FB3u, [new(0x20Cu, 0x00000001u)]), // PA_SU_SMALL_PRIM_FILTER_CNTL
        new(0u, 42u, 0x144832FBu, [new(0x2F9u, 0x0000002Du)]), // PA_SU_VTX_CNTL
        new(0u, 43u, 0x9890D9FAu, [new(0x1BAu, 0x00000000u)]), // SPI_TMPRING_SIZE
        new(0u, 44u, 0x9016FAF1u, [new(0x2A6u, 0x00000000u)]), // VGT_DRAW_PAYLOAD_CNTL
        new(0u, 45u, 0x4B73CE27u, [new(0x2CEu, 0x00000400u)]), // VGT_GS_MAX_VERT_OUT
        new(0u, 46u, 0x5F5A3E7Bu, [new(0x29Bu, 0x00000002u)]), // VGT_GS_OUT_PRIM_TYPE
        new(0u, 47u, 0xD4AF3A51u, [new(0x2D6u, 0x00000000u)]), // VGT_LS_HS_CONFIG
        new(0u, 48u, 0x6CF4F543u, [new(0x2A3u, 0xFFFFFFFFu)]), // VGT_PRIMITIVEID_RESET
        new(0u, 49u, 0x5FB86CCBu, [new(0x2A1u, 0x00000000u)]), // VGT_PRIMITIVEID_EN
        new(0u, 50u, 0xEDEFA188u, [new(0x2ADu, 0x00000000u)]), // VGT_REUSE_OFF
        new(0u, 51u, 0xD0DE9EE6u, [new(0x2D5u, 0x00000000u)]), // VGT_SHADER_STAGES_EN
        new(0u, 52u, 0xC5831803u, [new(0x2D4u, 0x88101000u)]), // VGT_TESS_DISTRIBUTION
        new(0u, 53u, 0x8E6DE84Bu, [new(0x2DBu, 0x00000000u)]), // VGT_TF_PARAM
        new(0u, 54u, 0xD0771662u, [new(0x2F5u, 0x00000000u), new(0x2F6u, 0x00000000u)]), // PA_SC_CENTROID_PRIORITY_0, PA_SC_CENTROID_PRIORITY_1
        new(0u, 55u, 0x569F7444u, [new(0x2FEu, 0x00000000u)]), // PA_SC_AA_SAMPLE_LOCS_PIXEL_X0Y0_0
        new(0u, 56u, 0x5C6637CDu, [new(0x30Eu, 0xFFFFFFFFu), new(0x30Fu, 0xFFFFFFFFu)]), // PA_SC_AA_MASK_X0Y0_X1Y0, PA_SC_AA_MASK_X0Y1_X1Y1
        new(0u, 57u, 0xCAE3E690u, [new(0x311u, 0x00000002u), new(0x312u, 0x03FF0080u)]), // PA_SC_BINNER_CNTL_0, PA_SC_BINNER_CNTL_1
        new(0u, 58u, 0x43FBD769u, [new(0x105u, 0x00000000u), new(0x107u, 0x00000000u), new(0x106u, 0x00000000u), new(0x108u, 0x00000000u)]), // CB_BLEND_RED, CB_BLEND_BLUE, CB_BLEND_GREEN, CB_BLEND_ALPHA
        new(0u, 59u, 0xEF550356u, [new(0x1E0u, 0x20010001u)]), // CB_BLEND0_CONTROL
        new(0u, 60u, 0x8F52E279u, [new(0x020u, 0x00000000u), new(0x021u, 0x00000000u)]), // TA_BC_BASE_ADDR, TA_BC_BASE_ADDR_HI
        new(0u, 61u, 0x1F2D8149u, [new(0x084u, 0x00000000u), new(0x085u, 0x20002000u)]), // PA_SC_CLIPRECT_0_TL, PA_SC_CLIPRECT_0_BR
        new(0u, 62u, 0x853D0614u, [new(0x800003FFu, 0x00000000u)]), // CX_NOP
        new(0u, 63u, 0x4413C6F9u, [new(0x008u, 0x00000000u), new(0x009u, 0x00000000u)]), // DB_DEPTH_BOUNDS_MIN, DB_DEPTH_BOUNDS_MAX
        new(0u, 64u, 0x67096014u, [new(0x010u, 0x80000000u), new(0x011u, 0x20000000u), new(0x012u, 0x00000000u), new(0x013u, 0x00000000u), new(0x014u, 0x00000000u), new(0x015u, 0x00000000u), new(0x01Au, 0x00000000u), new(0x01Bu, 0x00000000u), new(0x01Cu, 0x00000000u), new(0x01Du, 0x00000000u), new(0x01Eu, 0x00000000u), new(0x002u, 0x00000000u), new(0x005u, 0x00000000u), new(0x007u, 0x00000000u), new(0x00Bu, 0x00000000u), new(0x00Au, 0x00000000u)]), // DB_Z_INFO, DB_STENCIL_INFO, DB_Z_READ_BASE, DB_STENCIL_READ_BASE, DB_Z_WRITE_BASE, DB_STENCIL_WRITE_BASE, DB_Z_READ_BASE_HI, DB_STENCIL_READ_BASE_HI, DB_Z_WRITE_BASE_HI, DB_STENCIL_WRITE_BASE_HI, DB_HTILE_DATA_BASE_HI, DB_DEPTH_VIEW, DB_HTILE_DATA_BASE, DB_DEPTH_SIZE_XY, DB_DEPTH_CLEAR, DB_STENCIL_CLEAR
        new(0u, 65u, 0x88F5E915u, [new(0x0EBu, 0xFF00FF00u), new(0x0ECu, 0x00000000u)]), // PA_SC_FOV_WINDOW_LR, PA_SC_FOV_WINDOW_TB
        new(0u, 66u, 0x033F1EFFu, [new(0x800003FCu, 0x00000000u), new(0x800003FDu, 0x00000000u)]), // FSR_RECURSIONS0, FSR_RECURSIONS1
        new(0u, 67u, 0x918106BBu, [new(0x090u, 0x80000000u), new(0x091u, 0x40004000u)]), // PA_SC_GENERIC_SCISSOR_TL, PA_SC_GENERIC_SCISSOR_BR
        new(0u, 68u, 0x95F0E7ACu, [new(0x2FAu, 0x4E7E0000u), new(0x2FBu, 0x4E7E0000u), new(0x2FCu, 0x4E7E0000u), new(0x2FDu, 0x4E7E0000u)]), // PA_CL_GB_VERT_CLIP_ADJ, PA_CL_GB_VERT_DISC_ADJ, PA_CL_GB_HORZ_CLIP_ADJ, PA_CL_GB_HORZ_DISC_ADJ
        new(0u, 69u, 0xB48CBAB2u, [new(0x2E2u, 0x00000000u), new(0x2E3u, 0x00000000u)]), // PA_SU_POLY_OFFSET_BACK_SCALE, PA_SU_POLY_OFFSET_BACK_OFFSET
        new(0u, 70u, 0x05BB3BC6u, [new(0x2E0u, 0x00000000u), new(0x2E1u, 0x00000000u)]), // PA_SU_POLY_OFFSET_FRONT_SCALE, PA_SU_POLY_OFFSET_FRONT_OFFSET
        new(0u, 71u, 0x94FABA07u, [new(0x003u, 0x00000000u), new(0x004u, 0x00000000u)]), // DB_RENDER_OVERRIDE, DB_RENDER_OVERRIDE2
        new(0u, 72u, 0x38E92C91u, [new(0x318u, 0x00000000u), new(0x31Bu, 0x00000000u), new(0x31Cu, 0x00000000u), new(0x31Du, 0x00000000u), new(0x31Eu, 0x00000048u), new(0x31Fu, 0x00000000u), new(0x321u, 0x00000000u), new(0x323u, 0x00000000u), new(0x324u, 0x00000000u), new(0x325u, 0x00000000u), new(0x390u, 0x00000000u), new(0x398u, 0x00000000u), new(0x3A0u, 0x00000000u), new(0x3A8u, 0x00000000u), new(0x3B0u, 0x00000000u), new(0x3B8u, 0x0006C000u)]), // CB_COLOR0_BASE, CB_COLOR0_VIEW, CB_COLOR0_INFO, CB_COLOR0_ATTRIB, CB_COLOR0_DCC_CONTROL, CB_COLOR0_CMASK, CB_COLOR0_FMASK, CB_COLOR0_CLEAR_WORD0, CB_COLOR0_CLEAR_WORD1, CB_COLOR0_DCC_BASE, CB_COLOR0_BASE_EXT, CB_COLOR0_CMASK_BASE_EXT, CB_COLOR0_FMASK_BASE_EXT, CB_COLOR0_DCC_BASE_EXT, CB_COLOR0_ATTRIB2, CB_COLOR0_ATTRIB3
        new(0u, 73u, 0x0B177B43u, [new(0x00Cu, 0x00000000u), new(0x00Du, 0x40004000u)]), // PA_SC_SCREEN_SCISSOR_TL, PA_SC_SCREEN_SCISSOR_BR
        new(0u, 74u, 0x48531062u, [new(0x191u, 0x00000000u)]), // SPI_PS_INPUT_CNTL_0
        new(0u, 75u, 0xAAA964B9u, [new(0x16Fu, 0x00000000u), new(0x170u, 0x00000000u), new(0x171u, 0x00000000u), new(0x172u, 0x00000000u)]), // PA_CL_UCP_0_X, PA_CL_UCP_0_Y, PA_CL_UCP_0_Z, PA_CL_UCP_0_W
        new(0u, 76u, 0x7690AF6Fu, [new(0x10Fu, 0x4E7E0000u), new(0x111u, 0x4E7E0000u), new(0x113u, 0x4E7E0000u), new(0x110u, 0x00000000u), new(0x112u, 0x00000000u), new(0x114u, 0x00000000u), new(0x094u, 0x80000000u), new(0x095u, 0x40004000u), new(0x0B4u, 0x00000000u), new(0x0B5u, 0x00000000u)]), // PA_CL_VPORT_XSCALE, PA_CL_VPORT_YSCALE, PA_CL_VPORT_ZSCALE, PA_CL_VPORT_XOFFSET, PA_CL_VPORT_YOFFSET, PA_CL_VPORT_ZOFFSET, PA_SC_VPORT_SCISSOR_0_TL, PA_SC_VPORT_SCISSOR_0_BR, PA_SC_VPORT_ZMIN_0, PA_SC_VPORT_ZMAX_0
        new(0u, 77u, 0x078D7060u, [new(0x081u, 0x80000000u), new(0x082u, 0x40004000u)]), // PA_SC_WINDOW_SCISSOR_TL, PA_SC_WINDOW_SCISSOR_BR
        // shader register groups
        new(1u, 0u, 0x5D6E3EC7u, [new(0x212u, 0x00000000u)]), // COMPUTE_PGM_RSRC1
        new(1u, 1u, 0x57E7079Au, [new(0x213u, 0x00000000u)]), // COMPUTE_PGM_RSRC2
        new(1u, 2u, 0x7467FAFDu, [new(0x228u, 0x00000000u)]), // COMPUTE_PGM_RSRC3
        new(1u, 3u, 0x9E826B50u, [new(0x215u, 0x00000000u)]), // COMPUTE_RESOURCE_LIMITS
        new(1u, 4u, 0xDC484F18u, [new(0x218u, 0x00000000u)]), // COMPUTE_TMPRING_SIZE
        new(1u, 5u, 0x5DA8BCA3u, [new(0x08Au, 0x00000000u)]), // SPI_SHADER_PGM_RSRC1_GS
        new(1u, 6u, 0x5CA726D8u, [new(0x10Au, 0x00000000u)]), // SPI_SHADER_PGM_RSRC1_HS
        new(1u, 7u, 0x5DD28360u, [new(0x00Au, 0x00000000u)]), // SPI_SHADER_PGM_RSRC1_PS
        new(1u, 8u, 0x57EFA0BEu, [new(0x08Bu, 0x00000000u)]), // SPI_SHADER_PGM_RSRC2_GS
        new(1u, 9u, 0x502363D5u, [new(0x10Bu, 0x00000000u)]), // SPI_SHADER_PGM_RSRC2_HS
        new(1u, 10u, 0x506D14BDu, [new(0x00Bu, 0x00000000u)]), // SPI_SHADER_PGM_RSRC2_PS
        new(1u, 11u, 0xB2609506u, [new(0x224u, 0x00000000u)]), // COMPUTE_USER_ACCUM_0
        new(1u, 12u, 0x9E5CFB8Au, [new(0x107u, 0x00000000u), new(0x087u, 0x00000000u), new(0x007u, 0x00000000u)]), // SPI_SHADER_PGM_RSRC3_HS, SPI_SHADER_PGM_RSRC3_GS, SPI_SHADER_PGM_RSRC3_PS
        new(1u, 13u, 0xC918DF3Eu, [new(0x20Cu, 0x00000000u), new(0x20Du, 0x00000000u)]), // COMPUTE_PGM_LO, COMPUTE_PGM_HI
        new(1u, 14u, 0xC9751C9Cu, [new(0x0C8u, 0x00000000u), new(0x0C9u, 0x00000000u)]), // SPI_SHADER_PGM_LO_ES, SPI_SHADER_PGM_HI_ES
        new(1u, 15u, 0xC97EF77Au, [new(0x088u, 0x00000000u), new(0x089u, 0x00000000u)]), // SPI_SHADER_PGM_LO_GS, SPI_SHADER_PGM_HI_GS
        new(1u, 16u, 0xC927C6B9u, [new(0x108u, 0x00000000u), new(0x109u, 0x00000000u)]), // SPI_SHADER_PGM_LO_HS, SPI_SHADER_PGM_HI_HS
        new(1u, 17u, 0xC92A1EC5u, [new(0x148u, 0x00000000u), new(0x149u, 0x00000000u)]), // SPI_SHADER_PGM_LO_LS, SPI_SHADER_PGM_HI_LS
        new(1u, 18u, 0xC9E01B31u, [new(0x008u, 0x00000000u), new(0x009u, 0x00000000u)]), // SPI_SHADER_PGM_LO_PS, SPI_SHADER_PGM_HI_PS
        new(1u, 19u, 0x50685F29u, [new(0x800002FFu, 0x00000000u)]), // SH_NOP
        new(1u, 20u, 0xB26219CAu, [new(0x0B2u, 0x00000000u)]), // SPI_SHADER_USER_ACCUM_ESGS_0
        new(1u, 21u, 0xB25B6CF9u, [new(0x132u, 0x00000000u)]), // SPI_SHADER_USER_ACCUM_LSHS_0
        new(1u, 22u, 0xB2F86101u, [new(0x032u, 0x00000000u)]), // SPI_SHADER_USER_ACCUM_PS_0
        new(1u, 23u, 0x07E3B155u, [new(0x082u, 0x00000000u), new(0x083u, 0x00000000u)]), // SPI_SHADER_USER_DATA_ADDR_LO_GS, SPI_SHADER_USER_DATA_ADDR_HI_GS
        new(1u, 24u, 0x07E383C6u, [new(0x102u, 0x00000000u), new(0x103u, 0x00000000u)]), // SPI_SHADER_USER_DATA_ADDR_LO_HS, SPI_SHADER_USER_DATA_ADDR_HI_HS
        new(1u, 25u, 0xBDA98653u, [new(0x240u, 0x00000000u)]), // COMPUTE_USER_DATA_0
        new(1u, 26u, 0xBDBD1D0Fu, [new(0x08Cu, 0x00000000u)]), // SPI_SHADER_USER_DATA_GS_0
        new(1u, 27u, 0xBD946FD4u, [new(0x10Cu, 0x00000000u)]), // SPI_SHADER_USER_DATA_HS_0
        new(1u, 28u, 0xBDF02A4Cu, [new(0x00Cu, 0x00000000u)]), // SPI_SHADER_USER_DATA_PS_0
        // uconfig register groups
        new(2u, 0u, 0x19E93E85u, [new(0x41Fu, 0x00000000u)]), // GDS_OA_ADDRESS
        new(2u, 1u, 0x3B5C2AF3u, [new(0x41Du, 0x00000000u)]), // GDS_OA_CNTL
        new(2u, 2u, 0x47974A35u, [new(0x41Eu, 0x00000000u)]), // GDS_OA_COUNTER
        new(2u, 3u, 0x105971C2u, [new(0x25Bu, 0x00000000u)]), // GE_CNTL
        new(2u, 4u, 0x7D137765u, [new(0x24Au, 0x00000000u)]), // GE_INDX_OFFSET
        new(2u, 5u, 0xD187FEBCu, [new(0x24Bu, 0x00000000u)]), // GE_MULTI_PRIM_IB_RESET_EN
        new(2u, 6u, 0x12F854ACu, [new(0x25Fu, 0x00000000u)]), // GE_STEREO_CNTL
        new(2u, 7u, 0x40D49AD1u, [new(0x262u, 0x00000000u)]), // GE_USER_VGPR_EN
        new(2u, 8u, 0x8C0923DAu, [new(0x80003FF4u, 0x00000000u)]), // FSR_EXTEND_SUBPIXEL_ROUNDING
        new(2u, 9u, 0xBB8DF494u, [new(0x80003FFDu, 0x00000000u)]), // TEXTURE_GRADIENT_CONTROL
        new(2u, 10u, 0xF6D8A76Eu, [new(0x382u, 0x40000040u)]), // TEXTURE_GRADIENT_FACTORS
        new(2u, 11u, 0x7620F1E9u, [new(0x248u, 0x00000000u)]), // VGT_OBJECT_ID
        new(2u, 12u, 0x9EBFAB10u, [new(0x242u, 0x00000000u)]), // VGT_PRIMITIVE_TYPE
        new(2u, 13u, 0x98A09D0Eu, [new(0x380u, 0x00000000u), new(0x381u, 0x00000000u)]), // TA_CS_BC_BASE_ADDR, TA_CS_BC_BASE_ADDR_HI
        new(2u, 14u, 0x195D37D2u, [new(0x80003FF5u, 0x00000000u), new(0x80003FF6u, 0x00000000u)]), // FSR_ALPHA_VALUE0, FSR_ALPHA_VALUE1
        new(2u, 15u, 0xF9EC4F85u, [new(0x80003FF7u, 0x00000000u), new(0x80003FF8u, 0x00000000u), new(0x80003FF9u, 0x00000000u), new(0x80003FFAu, 0x00000000u)]), // FSR_CONTROL_POINT0, FSR_CONTROL_POINT1, FSR_CONTROL_POINT2, FSR_CONTROL_POINT3
        new(2u, 16u, 0x4626B750u, [new(0x80003FFBu, 0x00000000u), new(0x80003FFCu, 0x00000000u)]), // FSR_WINDOW0, FSR_WINDOW1
        new(2u, 17u, 0x4CC673A0u, [new(0x80003FFEu, 0x00000000u)]), // MEMORY_MAPPING_MASK
        new(2u, 18u, 0xDE5B3431u, [new(0x80003FFFu, 0x00000000u)]), // UC_NOP
        new(2u, 19u, 0x036AC8A6u, [new(0x25Cu, 0x00000000u)]), // GE_USER_VGPR1
    ];
}
