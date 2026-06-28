using Anaglyph.Lasertag;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// MovingTarget.cs — A simple shootable virtual target for the user study
//
// WHAT THIS FILE DOES:
//   A benign, software-controlled moving target the participant shoots. Unlike
//   the game's Zombie NPCs it doesn't chase or fight — it moves on a fixed,
//   repeatable path and gives a quick flash/vanish when hit. NO metrics are
//   logged — this is the qualitative user-study task; outcomes are captured by
//   surveys (Likert / NASA-TLX).
//
//   It can be hand-placed (fixed path) OR spawned by TargetSpawner, which uses
//   the Hit event + Relocate()/SetPhase() hooks below to drop N targets at
//   seeded-random positions and relocate them on hit. Either way the per-target
//   motion is deterministic, so it stays identical across participants/conditions.
//
//   The point of the task: as the experimenter moves a REAL obstacle into the
//   targets' path, the reconstruction must OCCLUDE the target (hide it) and
//   BLOCK shots. On the edge architecture the obstacle's reconstruction lags, so
//   targets visibly leak through / shots pass through — the perceptible
//   difference participants rate. (Occlusion of this virtual target is handled by
//   the engine's occlusion feature against the room mesh — see OcclusionMesh /
//   the edge fix in EdgeChunkStore — not by this script.)
//
// SETUP (Editor) — two ways to place targets:
//   • Spawned (recommended): make the target a PREFAB — a small mesh (Sphere
//     ~0.15 m), a non-trigger Collider, and an OCCLUDED material (e.g. Zombie.mat,
//     so the room mesh can hide it). Assign it to a TargetSpawner, which
//     instantiates N at seeded-random positions and relocates them on hit.
//   • Hand-placed: drop 1–3 into the scene under a root with EnableInUserStudy
//     (so they exist only in user-study builds).
//   In BOTH cases use the **Default** layer — so bullets hit it and the occlusion
//   feature can hide it. Do NOT use the "Chunk" layer.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    [RequireComponent(typeof(Collider))]
    public class MovingTarget : MonoBehaviour
    {
        [Header("Motion (world-space, repeatable)")]
        [Tooltip("Primary oscillation axis in world space (normalized).")]
        [SerializeField] private Vector3 axis = Vector3.right;
        [Tooltip("Half-travel each side, in metres.")]
        [SerializeField] private float amplitude = 0.6f;
        [Tooltip("Oscillation speed in cycles per second.")]
        [SerializeField] private float cyclesPerSecond = 0.4f;

        [Header("Optional second axis (for a figure-8 / oval)")]
        [SerializeField] private Vector3 axis2 = Vector3.zero;
        [SerializeField] private float amplitude2 = 0f;
        [SerializeField] private float cycles2PerSecond = 0.27f;

        [Tooltip("Phase offset (s) — set differently per target so they don't move in lockstep.")]
        [SerializeField] private float phaseOffset = 0f;

        [Header("Hit feedback (no logging)")]
        [SerializeField] private float flashSeconds = 0.12f;
        [Tooltip("Briefly vanish + become un-hittable when hit, then reappear.")]
        [SerializeField] private float hideSeconds = 0.4f;
        [SerializeField] private Color flashColor = Color.white;

        // Raised when this target is shot. TargetSpawner subscribes to relocate
        // the target to a new (seeded) position on hit. Null when the target is
        // placed by hand rather than spawned — the fixed-path behaviour is
        // unchanged in that case.
        public event System.Action<MovingTarget> Hit;

        private Vector3  origin;
        private Renderer rend;
        private Collider col;
        private Color    baseColor;
        private float    startTime;
        private float    flashUntil  = -1f;
        private float    hiddenUntil = -1f;

        private void Awake()
        {
            origin = transform.position;
            rend   = GetComponentInChildren<Renderer>();
            col    = GetComponent<Collider>();
            if (rend != null) baseColor = rend.material.color;
            startTime = Time.time + phaseOffset;
        }

        // Re-anchor if the target is repositioned/enabled at runtime.
        private void OnEnable() => origin = transform.position;

        private void Update()
        {
            // ── Repeatable motion ────────────────────────────────────────────
            float t = Time.time - startTime;
            Vector3 off = axis.normalized * (Mathf.Sin(t * cyclesPerSecond * Mathf.PI * 2f) * amplitude);
            if (amplitude2 != 0f && axis2 != Vector3.zero)
                off += axis2.normalized * (Mathf.Sin(t * cycles2PerSecond * Mathf.PI * 2f) * amplitude2);
            transform.position = origin + off;

            // ── Hit feedback: brief vanish + flash ───────────────────────────
            bool hidden = Time.time < hiddenUntil;
            if (rend != null && rend.enabled == hidden) rend.enabled = !hidden;
            if (col  != null && col.enabled  == hidden) col.enabled  = !hidden;
            if (rend != null && !hidden)
                rend.material.color = (Time.time < flashUntil) ? flashColor : baseColor;
        }

        // Called by Bullet via BroadcastMessage("OnShot", DamageData) on a hit.
        private void OnShot(Bullet.DamageData data)
        {
            flashUntil  = Time.time + flashSeconds;
            hiddenUntil = Time.time + hideSeconds;
            Hit?.Invoke(this);
        }

        // ── Spawner hooks (used by TargetSpawner; no-ops for hand-placed targets) ──

        /// <summary>Move the oscillation centre to a new world position. The
        /// target is briefly hidden right after a hit, so relocating here makes
        /// it reappear at the new spot with no visible pop.</summary>
        public void Relocate(Vector3 newOrigin)
        {
            transform.position = newOrigin;
            origin = newOrigin;
        }

        /// <summary>Set the motion phase (seconds) so spawned targets don't move
        /// in lockstep. Kept deterministic by the spawner's seeded RNG.</summary>
        public void SetPhase(float seconds)
        {
            phaseOffset = seconds;
            startTime   = Time.time + phaseOffset;
        }
    }
}
