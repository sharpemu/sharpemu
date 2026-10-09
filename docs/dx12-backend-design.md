<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Design notes: a Direct3D 12 guest-GPU backend

Status: **discussion draft**. Nothing here is implemented. The goal is to agree on a direction
with the maintainers before any code is written, as `CONTRIBUTING.md` asks for large
architectural changes. Proposal #199 already describes `IGuestGpuBackend` as the seam for a
future DX12 backend; this document records what that would involve against the current tree.

Line counts and file counts below were taken from the source tree on 2026-10-09 and are
approximate.

## 1. What exists today

### 1.1 Two seams

1. `IGuestGpuBackend` (`src/SharpEmu.Libs/Gpu/IGuestGpuBackend.cs`) is the coarse seam used by
   the AGC, VideoOut and SystemService export layers. It is expressed in guest terms and is
   implemented by `VulkanGuestGpuBackend` and `MetalGuestGpuBackend`. `GuestGpu.Current`
   chooses between them (`SHARPEMU_GPU_BACKEND`).
2. Below it, a shared renderer (`RenderExecutor`, `ShaderPipelineCache`, `GuestImageCache`)
   talks to the host through `IRenderHost` and `IShaderPipelineHost`.
   `MetalCommandStreamHost` implements both, so a second backend already runs on this shared
   layer.

### 1.2 Shader pipeline

`SharpEmu.ShaderCompiler` decodes Gen5 shaders into a backend-neutral IR. Each emitter is a
separate project that consumes it: `SharpEmu.ShaderCompiler.Vulkan` (SPIR-V) and
`SharpEmu.ShaderCompiler.Metal` (MSL). Metal has golden-file tests
(`tests/SharpEmu.ShaderCompiler.Metal.Tests/Goldens`) that run without a GPU.

### 1.3 Rough sizes

| Area | Lines |
| --- | --- |
| Neutral `SharpEmu.ShaderCompiler` | ~19k |
| Vulkan emitter (SPIR-V) | ~19k |
| Vulkan host (`Gpu/Vulkan` plus the `Vulkan*` files in `VideoOut`) | ~15k |
| Metal emitter and host | ~16k |

## 2. Where Vulkan leaks through the seam

The interface name suggests the shared layer is neutral. It is not yet.

- `IRenderHost`, `IShaderPipelineHost` and `IShaderPipelineProvider` use Vulkan enums
  directly: `ImageLayout`, `ImageAspectFlags`, `CompareOp`, `IndexType`, `SampleCountFlags`,
  `PrimitiveTopology`. The Metal host imports `Silk.NET.Vulkan` only to receive and translate
  these.
- Files under `Gpu/` that import `Silk.NET.Vulkan`: Images 19, Rendering 11, Buffers 7,
  Pipelines 7, Scheduling 2, plus 26 in `VideoOut`. `GpuBuffer`, `CachedImage` and
  `GpuMemorySlabs` are concrete Vulkan implementations, not shared logic.

Consequence: a D3D12 host written against the current interfaces would have to translate
Vulkan enums to D3D12, which works (Metal does the same) but bakes Vulkan vocabulary into the
shared layer.

## 3. What a D3D12 backend needs

### 3.1 Shader emitter

Options, in the order I would consider them:

1. **HLSL emitter compiled with DXC.** Text output like the MSL emitter, so
   `Gen5MslTranslator` is the closest model. D3D12 offers a native `WaveSize` attribute
   (Shader Model 6.6), relevant to the wave64 handling discussed in #870. Cost: a runtime
   dependency on the DXC compiler library.
2. **SPIR-V to HLSL with SPIRV-Cross.** Cheap to start, but it inherits the structure of the
   SPIR-V emitter, including the control-flow problems tracked in #1064.
3. **Direct DXIL.** DXIL is signed LLVM bitcode. Not recommended.

### 3.2 Host

- Device, queues, swapchain and fences.
- Resource-state tracking and barriers (a different model from `VulkanSynchronization`).
- Root signature and descriptor heaps. This is where the shared interfaces are most likely to
  need changes, since the Vulkan path relies on push descriptors
  (`PipelineHandle.UsesPushDescriptors`).
- Buffers, images, tiling and detile, and the global data share.

### 3.3 Estimated effort

Comparable to the Metal backend: on the order of 15 to 20k lines. This is an estimate, not a
measurement.

## 4. Is it worth it?

Honest trade-offs for discussion:

- Vulkan already works on Windows. D3D12 would give a different driver path, which may avoid
  some Vulkan-driver-specific faults (#1064, #870), but that is a hypothesis and has not been
  tested.
- Maintenance cost: a third backend means every change to the shared renderer must keep three
  hosts working.
- Benefit not tied to bugs: mature Windows debugging tools (PIX) and a second reference point
  that keeps the seam honest.

## 5. Related work

Checked on GitHub on 2026-10-09 with a text search; a change that does not use these words
could have been missed.

- No pull request mentions DX12, D3D12, DirectX, HLSL or DXIL. The only mention is proposal
  #199, closed on 2026-07-15 without comments.
- #236 ("Rewrite Vulkan backend with native renderer") proposed a native C++ Vulkan backend
  and was closed without being merged. This document does not overlap with it: it proposes
  no code, and it is about a new API backend rather than replacing the Vulkan host.
- Open pull requests that touch GPU files related to step M0 below. File lists were read
  with `gh pr view --json files`; for #1066 and #1061 GitHub returned only the first 100 of
  141 and 413 files, so those two are partial.
  - #1066, consolidated rendering, resolution and performance fixes. It modifies
    `IRenderHost.cs` and `IShaderPipelineHost.cs` directly, the interfaces M0 would change,
    so M0 and this pull request would conflict.
  - #1060, shared guest buffers with GPU-ordered completion signals. Touches `GpuBuffer`,
    `GuestBufferCache` and three `VulkanVideoPresenter` files; not the interfaces.
  - #1042, guest occlusion queries counted with Vulkan occlusion queries. Touches
    `VulkanVideoPresenter` files and `OcclusionResultBlock`; not the interfaces.
  - #1061, mesh shader compilation. Among the first 100 files: `GuestBufferCache`,
    `CachedImage`, `GpuTiler` and `GuestImageCache` files; the interfaces are not among them.
  - #773, host-aware memory budgets and detile pool caps. Touches only `VulkanDetilePass` and
    `VulkanVideoPresenter`.

  Only #1066 conflicts with M0 directly. The others touch Vulkan classes that M0 leaves
  alone, but they may need small adjustments if signatures that use the neutral types change.

## 6. Proposed sequence

Each step is a separate, small pull request.

| Step | Change | Verification |
| --- | --- | --- |
| M0 | Replace Vulkan enums in `IRenderHost`, `IShaderPipelineHost` and `IShaderPipelineProvider` with neutral types. Vulkan and Metal map them. | Existing build and tests; no behaviour change |
| M1 | HLSL emitter in a new `SharpEmu.ShaderCompiler.Dx12` project, with golden tests. | Golden tests, no GPU needed |
| M2 | D3D12 device and swapchain, presenting a cleared frame. | Run on any D3D12 GPU |
| M3 | Buffers, images and draws behind `IGuestGpuBackend`. | Needs a command stream or a title |

M0 is useful even if no D3D12 backend is ever written, because it removes Vulkan vocabulary
from the shared layer. It is also the step most likely to conflict with in-flight pull
requests (74 were open at the time of writing, several of them listed in section 5), so it
needs maintainer agreement.

## 7. Questions for the maintainers

1. Is a D3D12 backend wanted, or should effort stay on Vulkan and Metal?
2. Is M0 (neutral enums) acceptable on its own, and when would conflicts with open pull
   requests be least disruptive?
3. HLSL via DXC: is the extra runtime dependency acceptable on Windows?
4. Should `GpuBuffer`, `CachedImage` and the memory slabs move behind interfaces, or stay
   Vulkan-only with each backend keeping its own?
