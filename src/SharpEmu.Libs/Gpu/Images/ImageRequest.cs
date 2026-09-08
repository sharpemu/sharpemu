// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

public enum ImageRole : byte
{
    Texture,
    StorageImage,
    ColorTarget,
    DepthTarget,
    DisplaySurface,
}

// What a consumer asks the cache for: the guest image, the view it needs and the role it binds.
public struct ImageRequest
{
    public ImageDescription Description;
    public ImageViewDescription View;
    public ImageRole Role;

    public ImageRequest(in ImageDescription description, in ImageViewDescription view, ImageRole role)
    {
        Description = description;
        View = view;
        Role = role;
    }

    // A refresh of an existing image needs no view.
    public static ImageRequest Refresh(in ImageDescription description, ImageRole role) => new(description, ImageViewDescription.Default, role);
}
