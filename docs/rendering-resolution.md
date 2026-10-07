<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Rendering resolution

Options → Rendering exposes three independent sizes, globally and per game:

- **Resolution** sets the host window size or exclusive fullscreen mode.
- **Game display mode** reports a 1080p display to the game by default. Select
  3840×2160 to request a 4K display. A registered presentation buffer cannot
  change this setting.
- **Internal resolution scale** multiplies the host size of the game's
  screen-sized render targets. 100% is native and changes nothing.

The CLI equivalents are `--resolution=1280x720`,
`--guest-resolution=1920x1080` (or `3840x2160`) and
`--render-scale=0.5`. `SHARPEMU_RENDER_SCALE=0.5` does the same for scripted
runs and overrides the stored setting. The accepted range is 0.25 to 2,
rounded to a hundredth.

The game chooses its internal render targets. Some games adapt those targets to
the reported display mode; others render at a fixed resolution and may still
register 4K buffers with a 1080p display. Silent Hill: The Short Message does so
in current testing. The size of that presentation buffer alone also does not
establish the sizes of its intermediate scene targets. Changing the window alone
does not reduce the work the game renders; the internal resolution scale does.

## How the scale is applied

Guest memory layouts never change. A scaled image keeps its guest geometry in
its description - extent, pitch, tile mode, mip layout - and only its host
backing is created at `ceil(guest × scale)`. Everything that would notice:

- **Rasterization.** The render area, the viewport and the scissor are scaled
  with the attachments. Scissor edges round outward so no guest pixel loses its
  host coverage. With clipping disabled the vertex program bakes the viewport
  transform against a reference extent that is divided by an upscale, so the
  same program maps guest pixels onto host positions at either resolution.
- **Shaders.** `gl_FragCoord` is divided by the attachments' factor as it is
  loaded, so the pixel position inputs and everything derived from them - screen
  UVs above all - stay in guest pixels. Integer image coordinates are mapped
  onto host texels through the bound image's factor, and resource-info queries
  report guest sizes. Dynamic sample offsets are converted from guest texels
  to host texels before normalization. The factors and a mask of scaled image resources travel in
  shader data, so no shader is specialized per scale.
- **Guest transfers.** An upload fills a guest-resolution twin of the image from
  the copy regions the tiler built and blits into the backing; a download blits
  the backing into the twin and copies out of it. A copy between two images that
  disagree on host resolution runs at guest resolution the same way.
- **Presentation.** The flip path already works from the host extent of the
  captured image, so a scaled flip buffer still fills the window.

## What scales and what does not

Only screen-class, single-level, single-sample, uncompressed 2D render and depth
targets scale: width ≥ 1280, height ≥ 720, not square, render or depth tiling,
guest-placed, in a format the device can blit. The decision is a pure function
of the guest image description, so every view, alias and copy of one guest range
agrees on it.

Everything else keeps its guest resolution:

- block-compressed images, multisample footprints, mip chains, cube and array
  faces, 3D images;
- depth targets with a stencil plane, because Vulkan cannot blit stencil and the
  guest-resolution twin could not be filled;
- HTile, which encodes guest depth blocks the renderer reads back as clear
  state. DCC only describes guest bytes the host never stores, so it does scale;
- asset tilings (linear, standard), shadow maps, lookup tables, probe atlases
  and eye-adaptation readbacks, which are below the size threshold or square;
- storage images the moment a compute program writes one. The guest dispatch
  grid does not shrink with the host image, so a scaled image would be written
  with holes above 100% and redundantly below. The guest range is remembered and
  every later image over that memory stays native;
- every attachment of a pass whose attachments disagree, which happens when one
  of them falls into a case above. The draw drops the scaled attachments back to
  guest resolution and resolves its targets again.

Only the Vulkan backend scales; the Metal backend ignores the setting.

## Known limitations

- **Compute-written surfaces never scale.** In a deferred renderer that is a
  large part of the frame, so the saving is smaller than the scale suggests.
- **Depth with stencil never scales**, and it takes its pass with it.
- **Tone and screen-space effects shift.** Measured on Silent Hill: The Short
  Message, the title scene is correct in composition, coverage and UI placement
  at 50% and 200%, but slightly brighter than native at both, and at 50% the
  character's coat picks up colour speckle. Effects that reason about texels
  rather than about the screen - screen-space shadows, subsurface scattering,
  dithering - change their footprint with the host resolution.
- **Guest stencil uploads and downloads** of a scaled image would lose the
  stencil plane; this cannot happen today only because stencil images never
  scale.

## Measured effect

Silent Hill: The Short Message at the title menu, 1080p display mode, GPU time
per draw scope (averaged over a whole run):

| scope | 50% | 100% | 200% |
|---|---|---|---|
| `vs=CBE40391 ps=E0D7BA42` | 1.68 ms | 7.60 ms | 41.81 ms |
| `vs=9A9C9C47 ps=B2FD2C67` | 0.71 ms | 2.44 ms | 10.52 ms |
| `vs=9A9C9C47 ps=E5A1AB7F` | 0.78 ms | 2.35 ms | 8.27 ms |

Pixel work follows the area, as it should. Frame rate does not: this title is
bound by guest command translation and compute, not by pixels, and stays at
roughly 2.5 fps at every scale. Dreaming Sarah holds 60 fps at 50% with one
scaled target.
