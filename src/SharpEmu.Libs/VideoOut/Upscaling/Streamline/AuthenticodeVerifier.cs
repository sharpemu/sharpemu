// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace SharpEmu.Libs.VideoOut.Upscaling.Streamline;

[SupportedOSPlatform("windows")]
internal static unsafe partial class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new(0x00aac56b, 0xcd44, 0x11d0, 0x8c, 0xc2, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint RevocationCheckNone = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint StructSize;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint StructSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, Guid* action, TrustData* data);

    public static bool IsSignedBy(string path, string publisher, out string detail)
    {
        if (!File.Exists(path))
        {
            detail = "missing";
            return false;
        }

        var pathPointer = Marshal.StringToHGlobalUni(path);
        try
        {
            var file = new FileInfo { StructSize = (uint)sizeof(FileInfo), FilePath = pathPointer };
            var data = new TrustData
            {
                StructSize = (uint)sizeof(TrustData),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = (nint)(&file),
                StateAction = StateActionVerify,
                ProviderFlags = RevocationCheckNone,
            };
            var action = GenericVerifyV2;
            var status = WinVerifyTrust(-1, &action, &data);
            data.StateAction = StateActionClose;
            _ = WinVerifyTrust(-1, &action, &data);
            if (status != 0)
            {
                detail = $"signature not trusted (0x{status:X8})";
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
        }

        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            if (certificate.Subject.Contains(publisher, StringComparison.OrdinalIgnoreCase))
            {
                detail = certificate.Subject;
                return true;
            }

            detail = $"signed by another publisher: {certificate.Subject}";
            return false;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or IOException)
        {
            detail = $"signer unreadable: {exception.Message}";
            return false;
        }
    }
}
