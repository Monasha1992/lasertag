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
- The user ALSO flips ProjectSettings per build (productName + Android package id `com.monasha.lasertag_edge` vs `com.monasha.lasertag_standalone`) so both APKs coexist on one headset.
- `EnableInStandalone` / `EnableInEdge` components (`Assets/Monasha/Metrics/EnableInMode.cs`) self-disable GameObjects per mode.

## Key files

| File | Role |
|---|---|
| `Assets/Monasha/EdgeServer/EdgeServerClient.cs` | Edge client: depth send (10 Hz, one-in-flight), reader thread, 0x03/0x04 handling |
| `Assets/Monasha/EdgeServer/EdgeChunkStore.cs` | Client-side chunk cache (persistence) — dictionary of 3.2 m grid-cell meshes + colliders |
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

- **A performance regression is live in the chunked edge path** (slow meshing, freezes). The complete fix spec — paste-ready code the user will apply himself — is in **`../docs/13-pending-perf-fix.md`**. Partial: state fields already sit uncommitted in `MetalPipeline.swift`; everything else (batched single-command-buffer dispatch, empty-chunk backoff, Quest bake rate-limit, capacity-headroom buffers, `updateDistance` 6→3 revert) is pending.
- Pending Editor wiring: attach `EnableInStandalone` to ChunkManager + `EnableInEdge` to EdgeServerClient/EdgeMesh; add the metric collectors to MetricsRoot; wire `Blaster.onFire` → `GameEventCollector.OnShotFired`.
- Studies designed, not run: Study 1 dual-headset rig (`../docs/10-rig-setup.md`), Study 2 N=12 user study (`../docs/11-user-study.md`, N needs supervisor sign-off).
- Uncommitted in working trees: ProjectSettings (standalone naming), `XR Rig.prefab` tweaks, `MetalPipeline.swift` state fields.

## Working agreements — IMPORTANT

- **Never `git commit` or `git push` in any of the three repos.** Propose changes as exact file paths + before/after snippets; the user applies them by hand.
- The wire protocol (0x01/0x03/0x04, spec in `../docs/04-protocol.md`) has no version field — **both ends must be redeployed together** after protocol changes.
- **Restart the Mac server after any change** (`swift run` in `../EdgeMetalServer/`) — it caches the frustum list and TSDF volume per process.
- Metrics CSVs land in `Application.persistentDataPath` (pull via `adb`); Mac CSVs in `~/EdgeMetalServer/logs/`.
- The room mesh must be on layer 6 ("Chunk") or bullets pass through it.
