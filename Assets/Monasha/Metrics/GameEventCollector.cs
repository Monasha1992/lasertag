using Anaglyph.Lasertag;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// GameEventCollector.cs — Hooks gameplay events into the metrics timeline
//
// WHAT THIS FILE DOES:
//   Subscribes to existing Anaglyph game events and emits matching `event`
//   rows in MetricsLogger so analysis can correlate gameplay actions with
//   mesh / system / OVR samples on the same timeline.
//
//   Events captured:
//     - shot_fired     — Local player pulled the trigger (MainPlayer.Died,
//                         player damage etc. fire on this scope too).
//     - player_damaged — Local player took damage
//     - player_died    — Local player died
//     - player_respawned — Local player respawned
//
//   The bullet-level OnFire / OnCollide events are NOT subscribed here
//   because they would emit one row per bullet (potentially many per second)
//   and the data is mostly redundant with shot_fired + the damage events.
//   If you need bullet-by-bullet rows for the interaction-fidelity analysis,
//   add subscriptions per-bullet via a Bullet.OnSpawned hook — Bullet.cs:32
//   exposes the events.
//
// WHY THIS MATTERS FOR THE THESIS:
//   Three concrete uses:
//
//     1) Time-to-first-kill, hit-rate, deaths-per-minute — interaction
//        fidelity proxies derived purely from event timestamps.
//
//     2) Correlate frame-time spikes with combat moments (do FPS dips
//        coincide with the user shooting? With taking damage?).
//
//     3) Slice mesh-discard rate by "combat" vs "exploration" intervals
//        (between shots vs continuous shooting).
//
//   None of these require per-bullet detail at the moment; the high-level
//   player-state events are enough.
//
// ATTACHMENT:
//   Add this component to MetricsRoot. It auto-subscribes/unsubscribes
//   on OnEnable / OnDisable so scene reloads are clean.
// ─────────────────────────────────────────────────────────────────────────────

namespace Monasha.Metrics
{
    public class GameEventCollector : MonoBehaviour
    {
        // Cached Blaster reference, populated lazily — Blasters are usually
        // child GameObjects of the rig prefab, so they don't exist at
        // OnEnable time. We look them up on each shot from a parent hierarchy
        // search instead, but cache the most-recent finder result so steady
        // state has zero lookups.
        // (Implementation note: easier to subscribe at the local player
        // level — see below.)

        private void OnEnable()
        {
            // MainPlayer static events fire from the locally-controlled
            // player only — exactly what we want for participant-level
            // metrics. No multiplayer-other-player noise.
            MainPlayer.Damaged   += OnDamaged;
            MainPlayer.Died      += OnDied;
            MainPlayer.Respawned += OnRespawned;
        }

        private void OnDisable()
        {
            MainPlayer.Damaged   -= OnDamaged;
            MainPlayer.Died      -= OnDied;
            MainPlayer.Respawned -= OnRespawned;
        }

        // ─────────────────────────────────────────────────────────────────────
        // MainPlayer event handlers — direct passthroughs to MetricsLogger
        // ─────────────────────────────────────────────────────────────────────
        private void OnDamaged()
        {
            MetricsLogger.Instance?.LogEvent("player_damaged");
        }

        private void OnDied()
        {
            MetricsLogger.Instance?.LogEvent("player_died");
        }

        private void OnRespawned()
        {
            MetricsLogger.Instance?.LogEvent("player_respawned");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public hook used by Blaster.onFire to emit a shot_fired event.
        //
        // Why this design: Blasters are inside the runtime-spawned XR rig
        // prefab. The cleanest way to wire one of their public UnityEvent
        // fields to MetricsLogger from outside the prefab is via an explicit
        // hook the prefab references. Wire each Blaster's `onFire` UnityEvent
        // in the Inspector to call GameEventCollector.OnShotFired (drag the
        // MetricsRoot GameObject from the scene into the UnityEvent slot, pick
        // GameEventCollector → OnShotFired from the dropdown).
        //
        // Alternative (no Inspector wiring needed): if you'd rather subscribe
        // by reflection, FindObjectsOfType<Blaster>() in Start() and subscribe
        // each Blaster.onFire to OnShotFired. That works but is fragile if
        // Blasters are instantiated dynamically. Inspector wiring is cleaner.
        // ─────────────────────────────────────────────────────────────────────
        public void OnShotFired()
        {
            MetricsLogger.Instance?.LogEvent("shot_fired");
        }

        // Optional, also for Inspector wiring on Bullet prefabs if you want
        // per-bullet collide events. Most studies don't need this.
        public void OnBulletHit()
        {
            MetricsLogger.Instance?.LogEvent("bullet_hit");
        }
    }
}
