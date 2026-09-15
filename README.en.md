# Clipboard

[简体中文](README.md) | English

**End-to-end encrypted clipboard history and cross-device sync for Windows 11.**

[![Release](https://github.com/KINNNNNNG/clipboard/actions/workflows/release.yml/badge.svg)](https://github.com/KINNNNNNG/clipboard/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4)
![Rust](https://img.shields.io/badge/rust-1.88-000000)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

Clipboard shows a native WinUI 3 panel next to the caret, takes over `Win+V`, and keeps text, image, and file history in a local SQLCipher database. Cross-device sync runs on a purpose-built end-to-end encrypted log: remotes only ever store ciphertext, while file bundles and file paths stay on the machine.

- Download: [Releases](https://github.com/KINNNNNNG/clipboard/releases/latest)
- Current version: `0.1.0`
- Status and acceptance boundaries: [docs/STATUS.md](docs/STATUS.md)

## Features

**Native panel and shortcuts**

- A single WinUI 3 panel appears near the caret. It tries to take over `Win+V` and falls back to a configurable alternate shortcut.
- Up/Down highlights and auto-scrolls, `Esc` closes the panel, and restoring an item pastes it into the original app immediately.

**History**

- Local capture of text and images, asynchronous thumbnails, favorites, delete, single-item clear, and clear-all.
- Capture of single and multiple files and folders, with Fluent file and folder cards, friendly source application names, and system file type icons.
- Favorited file bundles are cached in encrypted chunks up to a size limit; when the original path is gone the app restores only into a restricted local staging directory, and un-favoriting or deleting releases the cache.

**Search and filtering**

- Instant substring and regular-expression search over text; text, images, and files can be filtered by copy time, source application, and type.
- Images are never OCRed. Search covers text content and file paths only.

**Retention**

- Maximum regular item count, retention days, and image space limit are configurable and enforced after every capture. Favorites are never cleaned up automatically.

**Sync**

- WebDAV and Alibaba Cloud OSS adapters sync end-to-end encrypted text and image events, encrypted image objects, favorite state, and delete tombstones.
- Image objects are uploaded first and referenced by the log afterwards; the receiving side commits history only after AEAD verification passes.
- Background realtime sync drains a persistent outbox on startup and uses exponential backoff with 0%-25% jitter. Invalid credentials pause automatic retries until sync is re-enabled.

## Privacy and security model

- **Encrypted at rest:** history lives in a SQLCipher database. Keys are purpose-separated with HKDF-SHA-256, and image and sync objects use XChaCha20-Poly1305.
- **Files never leave the machine:** file bundles, file paths, and file contents are strictly local-only. Only encrypted text and image events enter the sync queue.
- **Device pairing:** recovery codes and Argon2id + XChaCha20-Poly1305 pairing files. A pairing file carries only the master key and non-sensitive remote configuration; account and AccessKey Secret must be entered separately.
- **Credential handling:** sync credentials are protected only by the current Windows user DPAPI and are never written to the settings JSON, logs, or status text. New devices require entering them again.
- **Observability without content leakage:** diagnostics and sync logs are redacted to categories and counts.

## Download and install

Download `Clipboard-Setup-v0.1.0.exe` and `SHA256SUMS.txt` from [Releases](https://github.com/KINNNNNNG/clipboard/releases/latest), verify the hash, then run the installer:

```powershell
Get-FileHash .\Clipboard-Setup-v0.1.0.exe -Algorithm SHA256
```

Once the output matches `SHA256SUMS.txt`, double-click the installer:

- Installs to `%LOCALAPPDATA%\Programs\Clipboard` per user. Administrator rights are not required.
- Creates a Start menu entry and offers an optional desktop shortcut.
- Uninstalling removes program files only. History, keys, settings, and logs under `%LOCALAPPDATA%\Clipboard` are preserved.
- The app ships with the Windows App SDK in self-contained mode, so the target machine needs no separate Windows App Runtime.
- The installer is currently unsigned, so Windows SmartScreen may warn about an unknown publisher.

### Updates

The Updates section of the settings page shows the running version. You can check manually, or turn on the startup check. Checks read this repository's GitHub Releases only, and accept nothing but an installer asset named like `Clipboard-Setup-vX.Y.Z.exe`.

After downloading, the installer is verified against the published `SHA256SUMS.txt`; only a matching SHA-256 enables the install action. Once you confirm, the client exits by itself and the installer closes leftover processes, replaces files, and restarts the app. History, keys, and settings are untouched. A newer release can also be skipped.

## Build from source

### Prerequisites

| Dependency | Version | Purpose |
| --- | --- | --- |
| Rust | 1.88 (pinned by `rust-toolchain.toml`) | Rust workspace and FFI |
| .NET SDK | 8.0 | WinUI 3 client and tests |
| Visual Studio 2022 Build Tools | With MSVC and Windows SDK 26100 | Native build and resource compilation |
| Perl | Complete installation, path in `OPENSSL_SRC_PERL` | Building vendored OpenSSL (SQLCipher) |
| Inno Setup | 6 | Producing the EXE installer |

You can verify the toolchain with:

```powershell
pwsh -NoProfile -File scripts/verify-toolchain.ps1
```

### Build and run

```powershell
cargo build -p clipboard-ffi
dotnet build src\Clipboard.Windows\Clipboard.Windows.csproj -c Debug -p:Platform=x64
```

The client copies `clipboard_ffi.dll` automatically. Build output lands in `src\Clipboard.Windows\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\`; run `Clipboard.Windows.exe --show` to open the panel.

### Produce an installer

```powershell
pwsh -NoProfile -File scripts/package-windows.ps1 -Version 0.1.0
```

Output is `artifacts\windows\installer\Clipboard-Setup-v0.1.0.exe` plus `artifacts\windows\SHA256SUMS.txt`. See the [Windows installer release guide](docs/release/windows-installer.md).

## Testing and verification

```powershell
# Rust core: toolchain, formatting, Clippy, workspace tests, FFI smoke
pwsh -NoProfile -File scripts/test-core.ps1

# Full Windows client verification; add -SkipGuiSmoke on machines without a desktop
pwsh -NoProfile -File scripts/verify-windows-client.ps1

# Rust workspace tests only
cargo test --workspace --all-targets
```

Before a release, run the full verification. It closes stale clients on the same path, then checks the core toolchain, formatting, Clippy, WebDAV and OSS remote sync tests, the FFI smoke test, the Windows toolchain, targeted sync settings tests, the full WinUI test suite, and the x64 build. It also confirms Debug x64 output contains no certificates or private keys, and launches two clients in a row to prove the second instance closes the first.

Manual acceptance should additionally cover text and image capture and paste in Notepad, Edge, Office, and VS Code; Fluent icons and the item count label after copying single and multiple files and folders in Explorer; source application names; Up/Down highlighting with auto-scroll; the panel staying visible without sending `Ctrl+V` when the original path is unavailable; the alternate shortcut when `Win+V` takeover fails; no duplicate history from the app's own write-back; and 100%/150%/200% DPI on multi-monitor edges.

On a controlled Windows 11 x64 performance machine you can also run the benchmarks explicitly:

```powershell
pwsh -NoProfile -File scripts/measure-history-search.ps1
pwsh -NoProfile -File scripts/measure-windows-history-diagnostics.ps1
```

The first runs a Release Rust search gate over 10,000 synthetic text items: 30 samples each for substring and combined filters, with P95 at or below 200 ms. The second reports real FFI, ViewModel, fixed-capacity image LRU, and process memory trends. Neither participates in day-to-day tests.

## Repository layout

```text
crates/                Rust workspace
  clipboard-domain/    Shared domain types
  clipboard-crypto/    Local key derivation and authenticated object encryption
  clipboard-storage/   SQLCipher persistence and the sync outbox
  clipboard-search/    Deterministic text, path, and regex search
  clipboard-core/      Use-case orchestration and versioned command protocol
  clipboard-ffi/       Stable C ABI
  clipboard-sync/      Sync protocol primitives and WebDAV/OSS adapters
src/
  Clipboard.Windows/         WinUI 3 client
  Clipboard.FfiSmoke/        P/Invoke smoke test
tests/
  Clipboard.Windows.Tests/        Client unit and integration tests
  Clipboard.Windows.Performance/  Performance and diagnostics entry points
installer/             Inno Setup installer script
scripts/               Build, verification, packaging, and benchmark scripts
docs/                  Design, plan, and status documents
```

## Release process

Pushing a tag that matches `vMAJOR.MINOR.PATCH` triggers the `Windows Release` workflow. It verifies the Rust core on Ubuntu and builds the client installer on Windows in parallel, then creates a GitHub Release with the installer and checksum once both succeed:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

You can also run the workflow manually from the Actions page and supply a version.

## Roadmap and known limitations

- Cross-device sync of file history is not available; file bundles remain strictly local-only.
- A macOS client and a cross-platform sync UI are not available.
- No signed MSIX package yet. The project ships a self-contained EXE installer, and full install, upgrade, and uninstall acceptance is still pending.
- Remaining stage-five work covers real WebDAV and OSS writes, two-device sync, and the visible WinUI focus and DPI matrix. Priorities are tracked in the [stage five closure roadmap](docs/superpowers/plans/2026-08-25-stage-five-closure-roadmap.md).

## Contributing

Issues and pull requests are welcome.

- Run `scripts/test-core.ps1` before submitting, and `scripts/verify-windows-client.ps1` when the change touches the client.
- Follow `.editorconfig`: 4-space indentation for Rust, 2 spaces for YAML, JSON, and TOML.
- Commit messages and documentation are written in Chinese. Commit messages carry a type prefix such as `修复：`, `测试：`, `文档：`, or `构建：`.
- New behavior needs tests. Fix root causes rather than symptoms.

## Documentation

- [Development status, evidence, and priorities](docs/STATUS.md)
- [Windows installer release guide](docs/release/windows-installer.md)
- [Changelog](CHANGELOG.md)
- [Product and architecture design](docs/superpowers/specs/2026-07-31-cross-device-clipboard-design.md)
- [V1 roadmap](docs/superpowers/plans/2026-07-31-clipboard-v1-roadmap.md)
- [Stage five closure roadmap](docs/superpowers/plans/2026-08-25-stage-five-closure-roadmap.md)

## License

Released under the [MIT License](LICENSE).
