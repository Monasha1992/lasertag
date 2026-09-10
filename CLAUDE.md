# AR Laser Tag — Thesis Project Context

Master's thesis prototype: an AR laser-tag game on Meta Quest 3 that needs a continuously
updated 3D mesh of the real room (bullets collide with real walls). The thesis compares two
ways of building that mesh:

- **Standalone** — everything on-device (Quest GPU integrates depth into a TSDF; Burst CPU jobs mesh it per chunk).
- **Edge** — Quest streams raw depth over TCP to a Mac (M3 Max); a Metal pipeline integrates + meshes; chunks come back over Wi-Fi.

Research questions: RQ1 on-device limits (battery/thermal/frame-time/UX) · RQ2 offload viability (bandwidth, latency impact) · RQ3 the crossover point under varied room/network conditions.

## Repo map

| Path | What | Branch |
|---|---|---|
| `lasertag/` (this repo) | Unity 6 project, Quest client, both architectures | `thesis/unified` |
| `../EdgeMetalServer/` | Swift package, Mac server (TCP :9876 + Metal shaders) | `v1` |
| `../docs/` | Own git repo. **Docs 01–12 are the deep reference — read them instead of re-exploring code.** Index in `../docs/README.md` | — |

## Architecture switch & dual builds

- `Assets/Monasha/Metrics/ArchitectureManager.cs` selects the mode: `EDGE_BUILD` / `STANDALONE_BUILD` scripting defines override the Inspector value; sets `EnvironmentMapper.UseEdgeServer`; logs a boot banner (grep `ArchitectureManager` in `adb logcat`).
- The user ALSO flips ProjectSettings per build (productName + Android package id) so the APKs coexist on one headset. Four ids exist, **dot-separated**: `com.monasha.lasertag.edge`, `com.monasha.lasertag.stand`, plus `.edge.us` / `.stand.us` for the `USER_STUDY_BUILD` variants.
- `EnableInStandalone` / `EnableInEdge` components (`Assets/Monasha/Metrics/EnableInMode.cs`) self-disable GameObjects per mode.

## Key files

| File | Role |
|---|---|
| `Assets/Monasha/EdgeServer/EdgeServerClient.cs` | Edge client: depth send (10 Hz, one-in-flight), reader thread, 0x03/0x04 handling |
| `Assets/Monasha/EdgeServer/EdgeChunkStore.cs` | Client-side chunk cache (persistence) — dictionary of 3.2 m grid-cell meshes + colliders. **Edge cells are 3.2 m (`chunkSizeVox=32` × 0.1 m); standalone `ChunkManager` chunks are 5 m — two separate systems, don't conflate.** |
| `Assets/Monasha/Metrics/MetricsLogger.cs` (+ collectors in same folder) | Multi-row-type CSV telemetry (frame/mesh/system/ovr/event) for the study |
| `Assets/Anaglyph/XRTemplate/Depth/EnvironmentMapper.cs` | TSDF volume owner (both modes); local integration (standalone) |
| `Assets/Anaglyph/XRTemplate/Depth/Meshing/ChunkManager.cs` | Standalone chunk meshing (CPU/Burst) |
| `../EdgeMetalServer/Sources/EdgeMetalServer/EdgeMetalServer.swift` | TCP listener; `useChunkedMeshing` flag (0x04 chunked vs legacy 0x03) |
| `../EdgeMetalServer/Sources/EdgeMetalServer/MetalPipeline.swift` | GPU pipeline driver; chunk selection/meshing |
| `…/Shaders/VolumeIntegration.metal` | TSDF integrate; 0.5 blend (anti-shake; tuning history in comments) |
| `…/Shaders/SurfaceNets.metal` | Two-pass meshing; region-relative coordVertMap |
| `…/Shaders/DepthDilation.metal` | Hole filling (2 passes, one-voxel radius — do not crank up, erodes thin objects) |
| `…/Sources/EdgeMetalServer/MetricsRecorder.swift` | Mac-side per-frame CSV (join to Quest CSV on timestamp echo) |

## Current state (verified 2026-06-12)

- **The chunked-path perf fix is applied** (verified 2026-08-29): empty-chunk backoff is in `MetalPipeline.swift`, `updateDistance` is 3 in `MainScene.unity`, and the Quest bake rate-limit is `colliderUpdateInterval = 3`. Server compute now runs at a **4.78 ms median**. `../docs/13-pending-perf-fix.md` is historical — do not treat its items as outstanding.
- **Live defect: the server emits out-of-range mesh indices** — 16.8 % of chunk responses in the 2026-08-29 rig run. Sample: `cell(15,4,15) index[13173]=13150, vertCount=13101`; the same cell also repeats within one batch. Suspect the region-relative `coordVertMap` origin in `SurfaceNets.metal`. `EdgeServerClient.ValidateMeshBlock` now rejects these (they previously segfaulted PhysX on a bake thread); the **server-side cause is still unfixed**.
- **Verified spec table** — every architecture number with its source file — is in `../../Submissions/Draft_Chapters/08_VERIFIED_SPECS.md`. Prefer it over `docs/01–14`, which are stale in places (e.g. doc 14 says 7 dilation steps; the serialized value is 8).
- Pending Editor wiring: attach `EnableInStandalone` to ChunkManager + `EnableInEdge` to EdgeServerClient/EdgeMesh; add the metric collectors to MetricsRoot; wire `Blaster.onFire` → `GameEventCollector.OnShotFired`.
- Studies designed, not run: Study 1 dual-headset rig (`../docs/10-rig-setup.md`), Study 2 N=12 user study (`../docs/11-user-study.md`, N needs supervisor sign-off).
- Uncommitted in working trees: ProjectSettings (standalone naming), `XR Rig.prefab` tweaks, `MetalPipeline.swift` state fields.

## Working agreements — IMPORTANT

- **Never `git commit` or `git push` in any of the three repos.** Propose changes as exact file paths + before/after snippets; the user applies them by hand.
- The wire protocol (0x01/0x03/0x04, spec in `../docs/04-protocol.md`) has no version field — **both ends must be redeployed together** after protocol changes.
- **Restart the Mac server after any change** (`swift run` in `../EdgeMetalServer/`) — it caches the frustum list and TSDF volume per process.
- Metrics CSVs land in `Application.persistentDataPath` (pull via `adb`); Mac CSVs in `~/EdgeMetalServer/logs/`.
- The room mesh must be on layer 6 ("Chunk") or bullets pass through it.
