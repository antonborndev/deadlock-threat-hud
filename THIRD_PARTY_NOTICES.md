# Third-party notices

This repository and its release executable use third-party software and game
materials. The project's own license does not replace the licenses or terms
that apply to those components.

## Project license scope

The repository is mixed-license. GPL-3.0-only applies only to the original
project-authored code, build scripts, configuration files, and manually drawn
reaction images described in the README. The presence of the GPL license file
does not assert ownership of or relicense any material identified below.

## Direct .NET dependencies

- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET), MIT License.
  The Steamworks SDK and its redistributable native library remain subject to
  Valve's applicable Steamworks terms. This repository does not currently add
  a GPLv3 section 7 exception for linking with or distributing Steamworks SDK
  components; compatibility should be resolved before treating the combined
  executable as freely redistributable under GPL-3.0-only.
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), MIT License.
- [Grpc.Tools](https://github.com/grpc/grpc), Apache License 2.0. This is a
  build-only dependency used to provide `protoc` and standard protobuf files.

Exact package versions are pinned in
`ThreatHudBridge/ThreatHudBridge.csproj` and their transitive dependency graph
is resolved by NuGet during the build.

## Rust dependencies

- [haste](https://github.com/deadlock-api/haste), pinned to commit
  `77e8c5ac10b6ac64dd64c0d19455243de8bc1a92`, BSD 3-Clause License.
- haste's dependency graph includes
  [dungers](https://github.com/deadlock-api/dungers), pinned to commit
  `5e1e2aac76a027987911de3ef3d23ecfd992a7fb`, and
  [valveprotos-rs](https://github.com/deadlock-api/valveprotos-rs), pinned to
  commit `a23b534ea8ef5f64e4c4aff8657547cdb3bee26d`.
- Other Rust crates and exact versions are recorded in
  `ThreatHudBroadcastParser/Cargo.lock`.

No license file or license metadata was found in the pinned `dungers` source
tree during this review. This notice is not permission to redistribute that
code. The source repository only references it as a Git dependency, but a
compiled parser and the final Bridge executable contain its code. Redistribution
of those binaries therefore carries unresolved licensing risk until the
maintainer supplies an applicable license or the dependency is replaced.
`valveprotos-rs` identifies BSD 3-Clause and Unlicense-covered material, with
separate notices for protobuf definitions.

## Original reaction artwork

- `threathud/panorama/images/threathud/reaction_like.png`;
- `threathud/panorama/images/threathud/reaction_dislike.png`;
- the corresponding compiled textures inside
  `ThreatHudBridge/pak57_dir.vpk`.

These images were manually redrawn from scratch by the project owner. The final
PNG files contain no AI-generated pixels. They are original project artwork and
are licensed under GPL-3.0-only together with the project-authored source code.

## AI-assisted application icon

- `ThreatHudBridge/ThreatHudBridge.ico` uses an AI-generated base that was
  manually edited in Adobe Photoshop.
- The corresponding icon embedded in release executables has the same status.
- The icon is used by the Windows desktop executable and is not displayed by
  the in-game Panorama mod.

This file is not licensed under GPL-3.0-only. No representation is made
about the copyrightability of AI-generated elements. To the extent the project
owner holds copyright or related rights in the manual contributions, no
separate permission to copy, modify, or redistribute the icon is granted.

## Deadlock and Valve-derived material

The following files contain or are derived from Deadlock Panorama layouts:

- `threathud/panorama/layout/base_hud.xml`;
- `threathud/panorama/layout/citadel_hud_and_db_overlay.xml`;
- `threathud/panorama/layout/citadel_hud_hero_shop.xml`;
- the compiled versions of those layouts inside
  `ThreatHudBridge/pak57_dir.vpk`.

These files, and their compiled forms inside
`ThreatHudBridge/pak57_dir.vpk`, are not licensed under GPL-3.0-only. Valve
retains all applicable rights. This repository grants no additional permission
to use or redistribute Valve material; any such permission must come from
Valve's applicable terms or from law. This project is not affiliated with or
endorsed by Valve.

Before publishing a release archive, generate and include a complete license
report for the exact restored NuGet and Cargo dependency graphs.
