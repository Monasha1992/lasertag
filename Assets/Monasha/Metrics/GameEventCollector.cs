using Anaglyph.Lasertag;
using Anaglyph.Lasertag.Weapons;
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
//     - shot_fired     — Local player fired (Blaster.LocalFired / Automatic.LocalFired
//                         static events; full-auto raises one per bolt).
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
        private void OnEnable()
        {
            // MainPlayer static events fire from the locally-controlled
            // player only — exactly what we want for participant-level
            // metrics. No multiplayer-other-player noise.
            MainPlayer.Damaged   += OnDamaged;
            MainPlayer.Died      += OnDied;
            MainPlayer.Respawned += OnRespawned;

            // Local-fire static events from both weapon types (runtime-spawned
            // prefabs can't be Inspector-wired to this scene component).
            Blaster.LocalFired   += OnShotFired;
            Automatic.LocalFired += OnShotFired;
        }

        private void OnDisable()
        {
            MainPlayer.Damaged   -= OnDamaged;
            MainPlayer.Died      -= OnDied;
            MainPlayer.Respawned -= OnRespawned;

            Blaster.LocalFired   -= OnShotFired;
            Automatic.LocalFired -= OnShotFired;
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
        // shot_fired handler — subscribed to Blaster.LocalFired /
        // Automatic.LocalFired in OnEnable. Also kept public so a Blaster/
        // Automatic `onFire` UnityEvent could call it directly if ever wired in
        // the Inspector (not required — the static-event subscription covers it).
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
