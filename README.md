# Deadlock Threat HUD

Source code for the Deadlock Threat HUD Panorama mod and its Windows desktop
companion, Threat HUD Bridge.

The project is not affiliated with, endorsed by, or sponsored by Valve. Deadlock,
Steam, and related names and assets are trademarks or property of their
respective owners.

## Features

- Displays current-match player win-rate and rank information when public data
  is available.
- Provides a lane adviser based on hero composition and historical lane data.
- Displays current-match hero-damage information from the delayed Valve
  broadcast stream.
- Stores optional local like/dislike notes for encountered players.
- Installs, removes, activates, and deactivates the bundled Panorama VPK.
- Runs the desktop supervisor and low-traffic local worker as separate
  processes.

Live broadcast information is inherently delayed and can be unavailable while
a match or relay is still being initialized.

## Security and architecture

Threat HUD does **not**:

- inject a DLL into Deadlock;
- read or write Deadlock process memory;
- modify the Deadlock executable;
- capture or intercept network packets;
- bypass VAC or another anti-cheat component;
- automate input, purchases, movement, or abilities.

The Panorama mod reads information exposed through the normal game UI and
communicates with Bridge through an HTTP service bound to
`127.0.0.1:28741`. The server is available only on the local computer.

Bridge uses Steamworks for local Steam context, requests public statistics and
match metadata from `https://api.deadlock-api.com`, and starts the bundled Rust
parser for the delayed Valve broadcast stream. The parser is compiled from the
source in `ThreatHudBroadcastParser` and embedded into the final executable.

The mod manager copies the bundled `pak57_dir.vpk` into Deadlock's `addons`
directory. Activation adds managed `citadel/addons` search-path entries to
`gameinfo.gi` while Deadlock is closed. Before the first change it creates
`gameinfo.gi.threathud.bak`. It refuses to overwrite an existing `pak57_dir.vpk`
that it does not own.

More information about network requests and local files is in
[PRIVACY.md](PRIVACY.md).

## Repository layout

- `ThreatHudBridge/` — .NET 8 WinForms supervisor, local HTTP worker, API
  services, mod manager, and release script.
- `ThreatHudBroadcastParser/` — Rust parser for the delayed Valve broadcast
  stream.
- `threathud/` — Panorama JavaScript, XML layouts, and image sources.
- `tools/` — optional local VPK build tooling for Reduced CSDK.

The checked-in `ThreatHudBridge/pak57_dir.vpk` is the exact binary payload
embedded by the Bridge build. Its corresponding readable Panorama sources are
in `threathud/`. The VPK contains 42 compiled resources: 37 scripts, three
layouts, and two textures.

## Build Threat HUD Bridge

Requirements:

- Windows 10 or Windows 11, x64;
- .NET 8 SDK;
- Git;
- Rustup/Cargo. The parser toolchain is pinned by
  `ThreatHudBroadcastParser/rust-toolchain.toml`;
- Visual Studio 2022 Build Tools with the C++ desktop workload, required by
  native Rust dependencies and the Windows MSVC linker.

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\ThreatHudBridge\build-release.ps1
```

The script restores the pinned NuGet packages, builds the Rust parser with the
locked Cargo dependency graph, embeds the parser and VPK, and publishes one
self-contained executable:

```text
ThreatHudBridge\dist\ThreatHudBridge.exe
```

## Rebuild the Panorama VPK

This optional step requires a compatible Reduced CSDK installation containing
`resourcecompiler.exe` and `CSDKCfgVPK.exe`. Reduced CSDK and Valve's tools are
not included in this repository.

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build_mod.ps1 -ModFolderName threathud -Force
```

Choose the local build destination. Then copy the result into the Bridge
project before rebuilding the executable:

```powershell
Copy-Item .\tools\builds\threathud.vpk .\ThreatHudBridge\pak57_dir.vpk -Force
powershell -NoProfile -ExecutionPolicy Bypass -File .\ThreatHudBridge\build-release.ps1
```

## Verification performed for this source snapshot

- All 37 Panorama JavaScript files pass `node --check`.
- All three Panorama XML layouts parse successfully.
- The embedded VPK has a valid Valve VPK v2 header and contains the expected
  42 compiled resources.
- A static scan found no bundled API keys, passwords, private keys, databases,
  user logs, or personal filesystem paths.

The final Windows publish could not be executed in the Linux review
environment. A release should be built and tested on Windows from a clean clone
before its executable and source tag are published.

## Windows publisher warning

Current release executables are not Authenticode-signed. Windows SmartScreen
can therefore display **Unknown publisher** or an additional warning. Verify
the published SHA-256 hash against the matching immutable source tag before
running a release. A SmartScreen warning is not, by itself, a malware verdict.

## Release integrity

Build every distributed executable from a clean clone of the public repository.
Create an immutable Git tag for that version and publish SHA-256 hashes for the
tag's executable and embedded VPK. The GameBanana Alternate File Source should
link to the matching tag rather than to a moving `main` branch.

Do not claim that an older executable corresponds to this public snapshot. Once
the repository is ready, produce a new executable from it and distribute that
new build.

## AI assistance disclosure

ChatGPT was used as a coding assistant for portions of the C#, Rust, Panorama
JavaScript, build tooling, debugging, and documentation. The project owner
directed development, tested release builds in Deadlock, and remains responsible
for the distributed software.

The Windows application icon uses an AI-generated base that was manually edited
in Adobe Photoshop. It is used only as the desktop executable icon and is not
displayed by the in-game Panorama mod. The like/dislike images were manually
redrawn from scratch by the project owner; the final PNG files contain no
AI-generated pixels. Asset licensing is described in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

Except as identified below and in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), the original source code,
build scripts, configuration files, and manually drawn reaction images authored
for Deadlock Threat HUD are licensed under the GNU General Public License,
version 3 only
(`GPL-3.0-only`). See [LICENSE](LICENSE).

This license does not relicense third-party dependencies, Valve-derived
materials, or the AI-assisted application icon identified in
`THIRD_PARTY_NOTICES.md`. `ThreatHudBridge/pak57_dir.vpk` is a mixed-content
bundle: GPL-3.0-only continues to apply to the project-authored code compiled
into it, but no rights in its excluded components are granted by this project.

## Links

- Website: https://deadlock.ltd
- Community and support: https://discord.gg/MJcXrGXGt

Third-party components and Valve-derived materials are identified in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
