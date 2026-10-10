# Third-party notices

OpenStreamMS source code is licensed under the MIT License (see `LICENSE`).

This repository and its installer redistribute the following unmodified third-party
binaries. They are separate programs launched as independent processes (mere aggregation,
not linked into OpenStreamMS), so the MIT license does not apply to them. Each component
remains under its own license; the full license text ships next to the binaries.

| Component | Version | License | License file | Source code |
|---|---|---|---|---|
| [Sunshine](https://github.com/LizardByte/Sunshine) (LizardByte), [sunshine-webrtc](https://github.com/mlopezsegura/Sunshine-Web-RTC) build: upstream Sunshine plus Moonlight WebRTC for Samsung TVs | 2026.1006.152353-16-gbcde822b (commit `bcde822bd8f5d750be1eb71c8ccbcb87c98f0b7a`, upstream base `0594f62d4cc6179aa055f0363043adbc8849b62b`) | GPL-3.0 | `Sunshine/LICENSE.txt` | https://github.com/mlopezsegura/Sunshine-Web-RTC/tree/bcde822bd8f5d750be1eb71c8ccbcb87c98f0b7a |
| [FreeRDP](https://github.com/FreeRDP/FreeRDP) | 3.32.0 | Apache-2.0 | `FreeRDP/LICENSE.txt` | https://github.com/FreeRDP/FreeRDP |
| [zlib](https://zlib.net) (bundled with Sunshine) | — | zlib | `Sunshine/zlib-LICENSE.txt` | https://github.com/madler/zlib |

Sunshine's web UI (`Sunshine/assets/web`) contains compiled front-end libraries that are
distributed as part of Sunshine under the terms of its release.

## Source code offer (GPL-3.0)

The corresponding source code for the Sunshine binaries is available at the commit
linked above. If that link ever becomes unavailable, open an issue in this repository
and the maintainer will provide the corresponding source for the distributed version.

## Trademarks

Sunshine, Moonlight, FreeRDP, Windows and Remote Desktop are trademarks of their
respective owners. OpenStreamMS is not affiliated with or endorsed by them.
