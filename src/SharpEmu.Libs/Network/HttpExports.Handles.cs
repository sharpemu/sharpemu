// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

// Handle-table-backed libSceHttp setters and getters. They apply to a template, a connection or
// a request (all three share one id space); a value set on a node is read back from that node
// and, failing that, from its parents, mirroring how requests inherit template settings.
// Boolean arguments accept only 0 (disable) and 1 (enable). Callbacks are retained but never
// invoked: no request makes progress offline.
public static partial class HttpExports
{

    // (id, int enable)
    [SysAbiExport(
        Nid = "qFg2SuyTJJY",
        ExportName = "sceHttpSetAuthEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetAuthEnabled(CpuContext ctx) => SetBoolSetting(ctx, "auth");

    // (id, int enable)
    [SysAbiExport(
        Nid = "T-mGo9f3Pu4",
        ExportName = "sceHttpSetAutoRedirect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetAutoRedirect(CpuContext ctx) => SetBoolSetting(ctx, "autoredirect");

    // (id, int enable)
    [SysAbiExport(
        Nid = "XNUoD2B9a6A",
        ExportName = "sceHttpSetCookieEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieEnabled(CpuContext ctx) => SetBoolSetting(ctx, "cookie");

    // (id, int enable)
    [SysAbiExport(
        Nid = "i9mhafzkEi8",
        ExportName = "sceHttpSetInflateGZIPEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetInflateGZIPEnabled(CpuContext ctx) => SetBoolSetting(ctx, "inflategzip");

    // (id, int enable)
    [SysAbiExport(
        Nid = "PDxS48xGQLs",
        ExportName = "sceHttpSetChunkedTransferEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetChunkedTransferEnabled(CpuContext ctx) => SetBoolSetting(ctx, "chunked");

    // (id, int enable)
    [SysAbiExport(
        Nid = "HRX1iyDoKR8",
        ExportName = "sceHttpSetAcceptEncodingGZIPEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetAcceptEncodingGZIPEnabled(CpuContext ctx) => SetBoolSetting(ctx, "acceptgzip");

    // (id, int enable)
    [SysAbiExport(
        Nid = "s2-NPIvz+iA",
        ExportName = "sceHttpSetNonblock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetNonblock(CpuContext ctx) => SetBoolSetting(ctx, "nonblock");

    // (id, int enable)
    [SysAbiExport(
        Nid = "22buO-UufJY",
        ExportName = "sceHttpSetDelayBuildRequestEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetDelayBuildRequestEnabled(CpuContext ctx) => SetBoolSetting(ctx, "delaybuild");

    // (id, int* enable)
    [SysAbiExport(
        Nid = "9m8EcOGzcIQ",
        ExportName = "sceHttpGetAuthEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetAuthEnabled(CpuContext ctx) => GetBoolSetting(ctx, "auth", 0);

    // (id, int* enable)
    [SysAbiExport(
        Nid = "mmLexUbtnfY",
        ExportName = "sceHttpGetAutoRedirect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetAutoRedirect(CpuContext ctx) => GetBoolSetting(ctx, "autoredirect", 1);

    // (id, int* enable)
    [SysAbiExport(
        Nid = "iSZjWw1TGiA",
        ExportName = "sceHttpGetCookieEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetCookieEnabled(CpuContext ctx) => GetBoolSetting(ctx, "cookie", 1);

    // (id, int* enable)
    [SysAbiExport(
        Nid = "1rpZqxdMRwQ",
        ExportName = "sceHttpGetAcceptEncodingGZIPEnabled",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetAcceptEncodingGZIPEnabled(CpuContext ctx) => GetBoolSetting(ctx, "acceptgzip", 1);

    // (id, int* enable)
    [SysAbiExport(
        Nid = "Wq4RNB3snSQ",
        ExportName = "sceHttpGetNonblock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpGetNonblock(CpuContext ctx) => GetBoolSetting(ctx, "nonblock", 0);

    // (id)
    [SysAbiExport(
        Nid = "O6kcg-Y+EIU",
        ExportName = "sceHttpEnableAuth",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpEnableAuth(CpuContext ctx) => SetSetting(ctx, null, "auth", 1);

    // (id)
    [SysAbiExport(
        Nid = "MGd186UORN8",
        ExportName = "sceHttpDisableAuth",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDisableAuth(CpuContext ctx) => SetSetting(ctx, null, "auth", 0);

    // (id)
    [SysAbiExport(
        Nid = "l4NOLPo4pAQ",
        ExportName = "sceHttpEnableCookie",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpEnableCookie(CpuContext ctx) => SetSetting(ctx, null, "cookie", 1);

    // (id)
    [SysAbiExport(
        Nid = "IRobR8oVaFE",
        ExportName = "sceHttpDisableCookie",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDisableCookie(CpuContext ctx) => SetSetting(ctx, null, "cookie", 0);

    // (id)
    [SysAbiExport(
        Nid = "PYc72xLph5g",
        ExportName = "sceHttpEnableRedirect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpEnableRedirect(CpuContext ctx) => SetSetting(ctx, null, "autoredirect", 1);

    // (id)
    [SysAbiExport(
        Nid = "aZKLDHgZJ4E",
        ExportName = "sceHttpDisableRedirect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpDisableRedirect(CpuContext ctx) => SetSetting(ctx, null, "autoredirect", 0);

    // (id, value)
    [SysAbiExport(
        Nid = "Tc-hAYDKtQc",
        ExportName = "sceHttpSetResolveTimeOut",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetResolveTimeOut(CpuContext ctx) => SetUIntSetting(ctx, "resolvetimeout", false);

    // (id, value)
    [SysAbiExport(
        Nid = "K1d1LqZRQHQ",
        ExportName = "sceHttpSetResolveRetry",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetResolveRetry(CpuContext ctx) => SetUIntSetting(ctx, "resolveretry", false);

    // (id, value)
    [SysAbiExport(
        Nid = "mMcB2XIDoV4",
        ExportName = "sceHttpSetRecvBlockSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetRecvBlockSize(CpuContext ctx) => SetUIntSetting(ctx, "recvblocksize", false);

    // (id, value)
    [SysAbiExport(
        Nid = "2NeZnMEP3-0",
        ExportName = "sceHttpSetPriorityOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetPriorityOption(CpuContext ctx) => SetUIntSetting(ctx, "priority", false);

    // (id, value)
    [SysAbiExport(
        Nid = "gZ9TpeFQ7Gk",
        ExportName = "sceHttpSetPolicyOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetPolicyOption(CpuContext ctx) => SetUIntSetting(ctx, "policy", false);

    // (id, value)
    [SysAbiExport(
        Nid = "a4VsZ4oqn68",
        ExportName = "sceHttpSetResponseHeaderMaxSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetResponseHeaderMaxSize(CpuContext ctx) => SetUIntSetting(ctx, "responseheadermax", false);

    // (id, value)
    [SysAbiExport(
        Nid = "kjUUKrp1UqQ",
        ExportName = "sceHttpSetResHeaderMaxSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetResHeaderMaxSize(CpuContext ctx) => SetUIntSetting(ctx, "responseheadermax", false);

    // (id, value)
    [SysAbiExport(
        Nid = "pM--+kIeW-8",
        ExportName = "sceHttpSetCookieMaxNum",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieMaxNum(CpuContext ctx) => SetUIntSetting(ctx, "cookiemaxnum", false);

    // (id, value)
    [SysAbiExport(
        Nid = "Kp6juCJUJGQ",
        ExportName = "sceHttpSetCookieMaxNumPerDomain",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieMaxNumPerDomain(CpuContext ctx) => SetUIntSetting(ctx, "cookiemaxnumperdomain", false);

    // (id, value)
    [SysAbiExport(
        Nid = "7Y4364GBras",
        ExportName = "sceHttpSetCookieMaxSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieMaxSize(CpuContext ctx) => SetUIntSetting(ctx, "cookiemaxsize", false);

    // (id, value)
    [SysAbiExport(
        Nid = "pHc3bxUzivU",
        ExportName = "sceHttpSetCookieTotalMaxSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieTotalMaxSize(CpuContext ctx) => SetUIntSetting(ctx, "cookietotalmaxsize", false);

    // (id, value)
    [SysAbiExport(
        Nid = "jUjp+yqMNdQ",
        ExportName = "sceHttpsSetMinSslVersion",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpsSetMinSslVersion(CpuContext ctx) => SetUIntSetting(ctx, "minsslversion", false);

    // (id, value)
    [SysAbiExport(
        Nid = "U5ExQGyyx9s",
        ExportName = "sceHttpsSetSslVersion",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpsSetSslVersion(CpuContext ctx) => SetUIntSetting(ctx, "sslversion", false);

    // (id, uint32 sslFlags)
    [SysAbiExport(
        Nid = "f42K37mm5RM",
        ExportName = "sceHttpsEnableOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpsEnableOption(CpuContext ctx) => SetSslFlags(ctx, clear: false);

    // (id, uint32 sslFlags)
    [SysAbiExport(
        Nid = "mSQCxzWTwVI",
        ExportName = "sceHttpsDisableOption",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpsDisableOption(CpuContext ctx) => SetSslFlags(ctx, clear: true);

    // (id, callback, userArg)
    [SysAbiExport(
        Nid = "jf4TB2nUO40",
        ExportName = "sceHttpSetAuthInfoCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetAuthInfoCallback(CpuContext ctx) => SetCallbackSetting(ctx, null, "authinfocb");

    // (id, callback, userArg)
    [SysAbiExport(
        Nid = "h9wmFZX4i-4",
        ExportName = "sceHttpSetRedirectCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetRedirectCallback(CpuContext ctx) => SetCallbackSetting(ctx, null, "redirectcb");

    // (id, callback, userArg)
    [SysAbiExport(
        Nid = "Kh6bS2HQKbo",
        ExportName = "sceHttpSetCookieRecvCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieRecvCallback(CpuContext ctx) => SetCallbackSetting(ctx, null, "cookierecvcb");

    // (id, callback, userArg)
    [SysAbiExport(
        Nid = "GnVDzYfy-KI",
        ExportName = "sceHttpSetCookieSendCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetCookieSendCallback(CpuContext ctx) => SetCallbackSetting(ctx, null, "cookiesendcb");

    // (id, callback, userArg)
    [SysAbiExport(
        Nid = "vO4B-42ef-k",
        ExportName = "sceHttpSetRequestStatusCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpSetRequestStatusCallback(CpuContext ctx) => SetCallbackSetting(ctx, NodeKind.Request, "requeststatuscb");

    // (id, callback, userArg)
    [SysAbiExport(
        Nid = "htyBOoWeS58",
        ExportName = "sceHttpsSetSslCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceHttp")]
    public static int HttpsSetSslCallback(CpuContext ctx) => SetCallbackSetting(ctx, null, "sslcb");
}
