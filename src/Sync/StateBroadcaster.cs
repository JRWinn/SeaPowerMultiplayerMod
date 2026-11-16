using System.Collections;
using System.Collections.Generic;
using LiteNetLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// MonoBehaviour attached to the Plugin GameObject.
    /// Periodic host-side sync loops that ride Unity coroutines:
    /// damage-state corrections and waypoint drag flushing.
    /// </summary>
    public class StateBroadcaster : MonoBehaviour
    {
        private void Start()
        {
            StartCoroutine(DamageCorrectionLoop());
            StartCoroutine(WaypointFlushLoop());
            StartCoroutine(SensorStateLoop());
            StartCoroutine(UnitStatusLoop());
            StartCoroutine(AttackDesignationLoop());
            StartCoroutine(WeaponStatusLoop());
        }

        /// <summary>The client's status line is imposed here, and only here.
        ///
        /// Not a coroutine like everything else in this class, because this one is
        /// about ORDERING rather than cadence: Unity runs LateUpdate after every
        /// MonoBehaviour Update and before the frame draws, which is what makes the
        /// host's line the last word of the frame over the guest's own state machines.
        /// A timed loop, at any rate, could only change which writer happened to win -
        /// see UnitStatusManager.ClientLateAssertText.</summary>
        private void LateUpdate()
        {
            if (Plugin.Instance == null || Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsEstablished) return;
            if (SimSyncManager.CurrentState != SimState.Synchronized) return;
            if (SessionManager.SceneLoading) return;

            try
            {
                UnitStatusManager.ClientLateAssertText();
                // Also per-frame, and for the same reason - a mount slews at so many
                // degrees per second, so it wants driving every frame rather than on a
                // timer that would make it step.
                UnitStatusManager.ClientLateAimMounts();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[UnitStatus] Late status assert failed: {ex.Message}");
            }
        }

        // ── PausedToo ────────────────────────────────────────────────────────
        //
        // WaitForSeconds runs on scaled time, and the game pauses by setting the time
        // scale to zero - so a loop built on it does not tick at all while paused.
        // That is right for the loops that mirror the simulation (nothing moves), and
        // wrong for the ones that carry what a PLAYER does, because a pause is exactly
        // when players plan: an attack designation, a dragged waypoint or a doctrine
        // change made while paused sat on the machine that made it until someone
        // unpaused. Those loops wait on real time instead.

        // ── Weapon status / doctrine (both machines, own fleet) ─────────────
        //
        // The group-level doctrine paths write _weaponStatus directly instead of
        // calling SetWeaponStatus, so the order patch never sees them - see
        // WeaponStatusSync. Deliberately NOT host-gated: each side relays its own
        // fleet's doctrine to the other. A second is plenty for a setting a player
        // changes by hand, and keeps the sweep off the hot path.
        private IEnumerator WeaponStatusLoop()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(1f); // real time - see PausedToo
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try { WeaponStatusSync.Sweep(); }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Doctrine] Weapon status sweep failed: {ex.Message}");
                }
            }
        }

        // ── Attack designation (client → host) ──────────────────────────────
        //
        // The one order with no method to patch - see AttackDesignationSync. Faster
        // than the other loops because this is a direct player action waiting on a
        // reply, not background state: a quarter second is the most a click should
        // sit before the authoritative sim has it.
        private IEnumerator AttackDesignationLoop()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(0.25f); // real time - see PausedToo
                if (Plugin.Instance.CfgIsHost.Value) continue;
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try { AttackDesignationSync.ClientSweep(); }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Attack] Designation sweep failed: {ex.Message}");
                }
            }
        }

        // ── Bottom-row unit status (host → client, both modes) ──────────────
        //
        // Same shape and reasoning as the sensor loop: the status line and the
        // per-mount engagement state are host-decided, so without this a client
        // looking at a ship it had ordered to engage saw a blank line and "Ready"
        // on every mount. Not configurable for the same reason - it is the UI
        // being right, not a choice about what to share. PvP scoping is handled
        // inside the manager (remote player's own taskforce only).
        private IEnumerator UnitStatusLoop()
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try
                {
                    if (Plugin.Instance.CfgIsHost.Value) UnitStatusManager.HostBroadcast();
                    else                                 UnitStatusManager.ClientReassert();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[UnitStatus] Sync failed: {ex.Message}");
                }
            }
        }

        // ── Sensor emitter state (host → client, both modes) ────────────────
        //
        // Lives here rather than with the contact/drawing loops because those are
        // co-op only. Emission is world state: the client needs it in PvP too, or
        // its ESM cannot hear a radar that is genuinely radiating.
        //
        // Not configurable, unlike ContactSync/DrawingSync. Those are preferences
        // about what to share with the other player; this is the simulation being
        // right, and switching it off only restores the bug. It would also be a
        // per-machine setting capable of desyncing the two players silently.
        private IEnumerator SensorStateLoop()
        {
            var wait = new WaitForSeconds(0.5f);
            while (true)
            {
                yield return wait;
                if (!NetworkManager.Instance.IsEstablished) continue;
                if (SimSyncManager.CurrentState != SimState.Synchronized) continue;
                if (SessionManager.SceneLoading) continue;

                try
                {
                    if (Plugin.Instance.CfgIsHost.Value) SensorStateManager.HostBroadcast();
                    else                                 SensorStateManager.ClientReassert();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Sensors] Sync failed: {ex.Message}");
                }

                // Jam assignments ride the same loop for the same reason - they are
                // the other half of the emitter picture (see JamStateMessage), and
                // needed in both modes. Separate try so one failing does not take
                // the other down, or mislabel it.
                try
                {
                    if (Plugin.Instance.CfgIsHost.Value) JamStateManager.HostBroadcast();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Jam] Sync failed: {ex.Message}");
                }
            }
        }

        // ── Waypoint drag flush (catches throttled final positions) ────────
        private IEnumerator WaypointFlushLoop()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(0.15f); // real time - see PausedToo
                if (!NetworkManager.Instance.IsConnected) continue;

                foreach (var kvp in Patch_UserRootNode_UpdateSimulation._pending)
                {
                    var (unit, index) = kvp.Value;
                    var root = unit._userRoot;
                    if (root == null || index >= root.TaskViewModels.Count) continue;
                    // Drop/attack waypoints are excluded here for the same reason as at
                    // the send site, and more sharply: this runs up to 150 ms late, by
                    // which time a queue of drops has had even longer to complete tasks
                    // and shift every index under the one recorded above.
                    if (root.TaskViewModels[index].Task is GoToWaypointTask wp
                        && !(wp is AttackAtWaypoint))
                        Patch_UserRootNode_UpdateSimulation.SendEditWaypoint(unit, index, wp);
                }
                Patch_UserRootNode_UpdateSimulation._pending.Clear();
            }
        }

        // ── Periodic damage correction (catches drift / packet loss) ────────
        private IEnumerator DamageCorrectionLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Plugin.Instance.CfgDamageSyncInterval.Value);
                if (!NetworkManager.Instance.IsConnected) continue;

                // v2: damage is host-authoritative in both modes
                if (!Plugin.Instance.CfgIsHost.Value) continue;

                BroadcastDamageCorrections();
            }
        }

        /// <summary>Units that have qualified for damage sync at least once this battle.</summary>
        private static readonly HashSet<int> _everDamaged = new();

        /// <summary>New battle: unit ids are reused, so last battle's set means nothing.</summary>
        public static void ResetDamageTracking() => _everDamaged.Clear();

        /// <summary>
        /// Does this unit have damage the other machines need to hear about?
        ///
        /// The test used to be hull integrity alone, and IntegrityPercentage only sums
        /// the flooding compartments. Fire and per-system damage travel in the same
        /// message and nowhere else, so a hit that started a fire or knocked out a
        /// radar while the hull stayed above 99% - a small warhead on a big ship, a
        /// superstructure hit - was never sent: the ship burned on the host and looked
        /// untouched to everyone else. And once damage control brought the hull back
        /// over the line the updates stopped, leaving the last pre-repair picture in
        /// place for the rest of the battle.
        ///
        /// So: fire and dead systems qualify too, and a unit that has qualified once
        /// keeps being sent, which is what lets a repair or an extinguished fire reach
        /// the other side.
        /// </summary>
        private static bool NeedsDamageSync(int unitId, Compartments comps)
        {
            if (_everDamaged.Contains(unitId)) return true;

            bool damaged = comps._isSinking || comps.IntegrityPercentage <= 99f;
            var systems = comps._systemCompartments;
            for (int i = 0; !damaged && systems != null && i < systems.Length; i++)
            {
                var sc = systems[i];
                if (sc == null) continue;
                if (sc.FireSeverity > 0f) { damaged = true; break; }

                var list = sc._listOfSystems;
                for (int j = 0; list != null && j < list.Count; j++)
                    if (list[j] != null && list[j].Inoperable.Value) { damaged = true; break; }
            }

            if (damaged) _everDamaged.Add(unitId);
            return damaged;
        }

        private static void BroadcastDamageCorrections()
        {
            SendCorrections(UnitRegistry.Vessels);
            SendCorrections(UnitRegistry.Submarines);
        }

        /// <summary>Send one unit's damage state right now, outside the periodic loop.
        /// Used when the host starts a sink: the state stream's FlagSinking gets the
        /// client descending within ~100 ms, but the sink CLOCK (_sinkTime) only rides
        /// DamageState, so waiting for the next correction means descending off the
        /// wrong clock and then snapping. ReliableOrdered because it is a one-shot that
        /// sets that clock - the periodic loop stays unreliable and re-asserts it.</summary>
        public static void SendDamageStateNow(ObjectBase? unit)
        {
            if (unit == null) return;
            if (!Plugin.Instance.CfgIsHost.Value) return;
            if (!NetworkManager.Instance.IsConnected) return;

            var msg = DamageStateSerializer.Capture(unit);
            if (msg != null)
                NetworkManager.Instance.SendToOther(msg, DeliveryMethod.ReliableOrdered);
        }

        private static void SendCorrections<T>(System.Collections.Generic.IReadOnlyList<T> units) where T : ObjectBase
        {
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                // NOT skipped when IsDestroyed: DestroyByExplosion only sets the
                // destroyed flag - it does not sink anything. A destroyed hulk keeps
                // flooding and burning and only crosses the sink threshold later, so
                // dropping it here froze the client's fire and flooding at whatever the
                // last pre-death snapshot said, and starved the sink clock entirely.
                if (unit == null) continue;
                var comps = unit.Compartments;
                if (comps == null) continue;

                // Only send for units that have taken damage or are sinking
                if (!NeedsDamageSync(unit.UniqueID, comps)) continue;

                var msg = DamageStateSerializer.Capture(unit);
                if (msg != null)
                    NetworkManager.Instance.SendToOther(msg, DeliveryMethod.Unreliable);
            }
        }
    }
}
