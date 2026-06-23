using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// TargetSpawner.cs — Spawns N moving targets at SEEDED-random positions
//
// WHY SEEDED (not freshly random):
//   The user study compares conditions A/B/C within-subject with paired
//   NASA-TLX, VRSQ and Likert measures (docs/11-user-study.md). Those paired
//   comparisons are only valid if every condition — and every participant —
//   faces the SAME task. A fixed seed gives an identical "random-looking"
//   layout + motion for everyone, so difficulty and visual provocation are
//   held constant. Set `seed = 0` ONLY for casual demos (time-based, not
//   reproducible) — never for real study runs.
//
//   Each target gets its OWN seeded stream (seed + i·prime), so reproducibility
//   survives "respawn on hit": two participants who hit a given target the same
//   number of times see it in the same sequence of places, regardless of when
//   they shoot.
//
// SETUP (Editor):
//   - Make your occluded target sphere (MovingTarget + Collider + MeshRenderer
//     with Zombie.mat, Default layer) into a PREFAB.
//   - Put this component on an empty GameObject under the EnableInUserStudy
//     root, assign the prefab, set Count = 10, position/size the spawn box.
//   - The green gizmo (visible when selected) shows the spawn region.
//
// RESPAWN:
//   On hit, the target is relocated to its next seeded position. MovingTarget
//   hides briefly when shot (hideSeconds), so the relocate is seamless — no
//   separate delay needed here.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class TargetSpawner : MonoBehaviour
    {
        [Header("Prefab & count")]
        [Tooltip("Target prefab — must have MovingTarget + Collider + the occluded material.")]
        [SerializeField] private MovingTarget targetPrefab;
        [SerializeField] private int count = 10;

        [Header("Spawn region (box, relative to this GameObject)")]
        [Tooltip("Centre of the spawn box, in this transform's local space.")]
        [SerializeField] private Vector3 regionCenter = new Vector3(0f, 1.2f, 2f);
        [Tooltip("Full size of the spawn box in metres (targets spawn anywhere inside).")]
        [SerializeField] private Vector3 regionSize = new Vector3(3f, 1.5f, 2f);

        [Header("Reproducibility")]
        [Tooltip("Fixed seed = identical layout for every participant/condition " +
                 "(REQUIRED for the paired TLX/VRSQ/Likert analysis). " +
                 "Set 0 for a time-based seed — casual demo only, NOT for study runs.")]
        [SerializeField] private int seed = 12345;

        [Header("Motion variety (reproducible)")]
        [Tooltip("Give each target a different motion phase so they don't swing in lockstep.")]
        [SerializeField] private bool randomizePhase = true;

        private void Start()
        {
            if (targetPrefab == null)
            {
                Debug.LogError("[TargetSpawner] No targetPrefab assigned — nothing to spawn.");
                return;
            }

            int baseSeed = (seed == 0) ? System.Environment.TickCount : seed;

            for (int i = 0; i < count; i++)
            {
                // Independent stream per target → respawn order doesn't depend
                // on which other targets the participant hit first.
                var rng = new System.Random(baseSeed + i * 9973);

                Vector3 spawnPos = RandomPoint(rng);
                MovingTarget t = Instantiate(targetPrefab, spawnPos, Quaternion.identity, transform);

                if (randomizePhase)
                    t.SetPhase((float)(rng.NextDouble() * 4.0));

                // Capture this target's own stream for its respawns.
                System.Random captured = rng;
                t.Hit += mt => mt.Relocate(RandomPoint(captured));
            }
        }

        // Uniform point inside the box, in world space.
        private Vector3 RandomPoint(System.Random rng)
        {
            Vector3 half = regionSize * 0.5f;
            Vector3 local = new Vector3(
                Mathf.Lerp(-half.x, half.x, (float)rng.NextDouble()),
                Mathf.Lerp(-half.y, half.y, (float)rng.NextDouble()),
                Mathf.Lerp(-half.z, half.z, (float)rng.NextDouble()));
            return transform.TransformPoint(regionCenter + local);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color  = new Color(0f, 1f, 0.6f, 0.25f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(regionCenter, regionSize);
            Gizmos.color  = new Color(0f, 1f, 0.6f, 0.9f);
            Gizmos.DrawWireCube(regionCenter, regionSize);
        }
    }
}
