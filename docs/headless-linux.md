<!--
SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Headless Linux Validation

This document records the validation environment, methodology, and results for
running SharpEmu on a headless Linux server without a physical GPU. The setup
uses Xvfb for virtual X11 and Lavapipe (Mesa's software Vulkan implementation)
for rendering, allowing SharpEmu to run on CI runners, Docker containers, and
servers that lack discrete graphics hardware.

## Motivation

The existing `PreferX11OnLinuxWayland()` helper only sets the
`GLFW_PLATFORM_X11` init hint when `WAYLAND_DISPLAY` is present. On minimal
Linux environments (CI runners, Xvfb sessions, Docker containers, or servers
without Wayland libraries installed), GLFW's automatic platform detection
may not select a backend with a usable rendering surface.

In our test environment, we observed that without an explicit X11 hint,
the presenter thread started and reached `Vulkan VideoOut ready`, but no
swapchain images were produced. Requesting X11 explicitly avoided relying
on GLFW's auto-selection in these environments.

The fix in this branch drops the `WAYLAND_DISPLAY` gate: as long as `DISPLAY`
is set on Linux, the X11 platform is requested explicitly.

## Test Environment

| Property | Value |
|---|---|
| CPU | Intel(R) Xeon(R) Processor, 2 cores, 2800 MHz |
| RAM | 3.9 GiB |
| Physical GPU | None |
| Kernel | Linux 5.10.134 x86_64 |
| Display server | Xvfb `:1` 1920x1080x24 |
| Vulkan ICD | Lavapipe (`libvulkan_lvp.so`, LLVM 19.1.7, 256 bits) |
| GLFW | 3.4 (system `libglfw.so.3`) |
| .NET SDK | 10.0.302 |
| Game used | Dreaming Sarah (PPSA02929) |

## Reproduction Steps

```bash
# 1. Install dependencies (Debian/Ubuntu)
apt-get install -y xvfb mesa-vulkan-drivers libglfw3 libffi8 \
    libegl1 libglx0 libopengl0 libdecor-0-0 \
    libwayland-client0 libwayland-cursor0 libwayland-egl1 \
    libxkbcommon0 libxrandr2 libxinerama1 libxi6 libxcursor1 libxrender1

# 2. Start Xvfb
Xvfb :1 -screen 0 1920x1080x24 -nolisten tcp -ac -noreset &

# 3. Set environment
export DISPLAY=:1
export XDG_RUNTIME_DIR=/tmp/xdg
mkdir -p /tmp/xdg
export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json

# 4. Optional: capture framebuffers for verification
export SHARPEMU_TRACE_GUEST_IMAGES=present
export SHARPEMU_GUEST_IMAGE_DUMP_DIR=/tmp/framebuffers
export SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS=1

# 5. Run
./SharpEmu /path/to/eboot.bin
```

## Comparison Matrix

The fix was validated with a four-way comparison: upstream and this branch,
each tested with and without `libwayland-client` installed. Each run was
90 seconds on the same machine.

| Test | Build | libwayland installed? | `GLFW windowing platform in use` | `vk.swapchain_image` events | Framebuffer dumps |
|---|---|---|---|---|---|
| A | upstream `90c72eb` | Yes | `0x0` → X11 fallback | > 0 | > 0 |
| B | this branch | Yes | `X11` | > 0 | > 0 |
| C | upstream `90c72eb` | No | `0x0` (NULL platform) | 0 | 0 |
| D | this branch | No | `X11` | > 0 | > 0 |

### Key finding

When `libwayland-client` **is** installed, upstream and this branch behave
identically: GLFW tries Wayland, fails (because `WAYLAND_DISPLAY` is unset),
and falls back to X11 successfully.

When `libwayland-client` is **not** installed, we observed that upstream
selects a non-rendering platform (`0x0`), while this branch's explicit
X11 hint selects `X11`, which produces swapchain images.

## Sample Log Output (this branch, libwayland absent)

```
[LOADER][INFO] Linux X11 session detected; requested GLFW X11 backend explicitly.
[LOADER][INFO] GLFW windowing platform in use: X11
[LOADER][INFO] Vulkan candidate: llvmpipe (LLVM 19.1.7, 256 bits) (Cpu) score=20
[LOADER][INFO] Vulkan device: llvmpipe (LLVM 19.1.7, 256 bits) (Cpu)
[LOADER][INFO] Vulkan VideoOut ready: 1920x1080, format=B8G8R8A8Srgb
[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160
[LOADER][INFO] Vulkan VideoOut presented guest frame: image=0x0000000001260000 3840x2160
[LOADER][TRACE] vk.swapchain_image size=1920x1080 format=B8G8R8A8Srgb nonzero_bytes=8294400/8294400 nonblack_pixels=2073600/2073600 hash=0xFD0983529E75AA1F
```

## Change Isolation

The fix is a single-file, ~50-line change to
`src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs`. It does not touch
game logic, HLE exports, or shader compilation.

## Validation Notes

- Reproduced on 2026-07-19 (UTC) on the environment described above.
- Upstream baseline: `90c72eb`.
- Dreaming Sarah (PPSA02929) was used as the test game.
- The test is reproducible: install Debian, install the packages above,
  remove `libwayland-client0`, run the commands above, and compare the
  `GLFW windowing platform in use` line between upstream and this branch.
