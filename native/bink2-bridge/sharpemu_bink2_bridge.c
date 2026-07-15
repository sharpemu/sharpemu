/*
 * Copyright (C) 2026 SharpEmu Emulator Project
 * SPDX-License-Identifier: GPL-2.0-or-later
 *
 * Build this small adapter with a licensed RAD Bink 2 SDK. The SDK and its
 * headers are not distributed by SharpEmu. See docs/bink2-bridge.md.
 */

#include <stdint.h>
#include "bink.h"

typedef struct sharpemu_bink2_info {
  uint32_t width;
  uint32_t height;
  uint32_t frames_per_second_numerator;
  uint32_t frames_per_second_denominator;
} sharpemu_bink2_info;

int sharpemu_bink2_open_utf8(const char *path, HBINK *movie, sharpemu_bink2_info *info) {
  HBINK bink;
  if (!path || !movie || !info) return 0;

  bink = BinkOpen(path, 0);
  if (!bink) return 0;

  *movie = bink;
  info->width = bink->Width;
  info->height = bink->Height;
  info->frames_per_second_numerator = bink->FrameRate;
  info->frames_per_second_denominator = bink->FrameRateDiv;
  return 1;
}

int sharpemu_bink2_decode_next_bgra(HBINK movie, uint8_t *destination,
                                    uint32_t stride, uint32_t destination_bytes) {
  uint64_t needed;
  if (!movie || !destination || stride < movie->Width * 4) return 0;
  needed = (uint64_t)stride * movie->Height;
  if (needed > destination_bytes) return 0;

  /* Async Bink I/O has not filled the next frame yet; retry on the next host present. */
  if (BinkWait(movie)) return 0;
  BinkDoFrame(movie);
  BinkCopyToBuffer(movie, destination, stride, movie->Height, 0, 0, BINKSURFACE32RA);
  BinkNextFrame(movie);
  return 1;
}

void sharpemu_bink2_close(HBINK movie) {
  if (movie) BinkClose(movie);
}
