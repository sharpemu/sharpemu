// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Loader;

public readonly record struct ImportedSymbolMetadata(
    string? LibraryName,
    string? ModuleName)
{
    public string DiagnosticSuffix
    {
        get
        {
            if (string.IsNullOrWhiteSpace(LibraryName) && string.IsNullOrWhiteSpace(ModuleName))
            {
                return string.Empty;
            }

            var library = string.IsNullOrWhiteSpace(LibraryName) ? "?" : LibraryName;
            var module = string.IsNullOrWhiteSpace(ModuleName) ? "?" : ModuleName;
            return $" library={library} module={module}";
        }
    }
}
