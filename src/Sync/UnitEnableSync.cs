using System.Collections.Generic;
using HarmonyLib;
using SeaPower;
using SeapowerMultiplayer.Messages;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Units a mission holds back and switches on later.
    ///
    /// A mission can author a unit with <c>Disabled=True</c> and enable it from a
    /// trigger (<c>Action_SetEnabledStatus</c>) - the stock South Atlantic missions
    /// bring their air strikes in this way, ten minutes after the start. The unit is
    /// there from the moment the mission loads; it is only inactive.
    /// <c>ObjectBase.Enable()</c> is what wakes it: clears Disabled, hands it control,
    /// puts it back in its taskforce and activates the GameObject.
    ///
    /// Nothing replicated that, and it fell through two gaps at once:
    ///
    ///   HOST - the unit registry is rebuilt from a scan of ACTIVE objects at every
    ///   session start, which drops a disabled unit, and an enable does not run Awake
    ///   again, so nothing put it back. A unit missing from the registry is not in the
    ///   state stream or the census: the host flew the strike and never told anyone.
    ///
    ///   GUEST - triggers are replayed for their presentation only, with the unit block
    ///   skipped (see <see cref="TriggerSync"/>), on the stated assumption that the
    ///   simulation result arrives through the streams. For an enable there was no such
    ///   stream, so the guest's copy stayed disabled where the mission had parked it.
    ///
    /// What the guest saw was the host's contacts for those aircraft sitting motionless
    /// at their start positions - the contact share could find the inactive object and
    /// plot it, and it never moved - until a full resync shipped a save in which they
    /// were already enabled.
    ///
    /// So: an enable or disable on the host is announced, re-registers the unit, and
    /// the guest runs the game's own Enable()/Disable() on its copy.
    /// </summary>
    public static class UnitEnableSync
    {
        /// <summary>Guest: units the host disabled a moment ago, so a stream packet
        /// that was already in flight cannot switch one straight back on.</summary>
        private static readonly Dictionary<int, float> _disabledAt = new();
        private const float DisableSettleSec = 3f;

        public static void Reset() => _disabledAt.Clear();

        /// <summary>Both machines, after the game's Enable()/Disable() has run.</summary>
        internal static void OnChanged(ObjectBase unit, bool wasDisabled)
        {
            if (unit == null || unit is WeaponBase) return;
            bool disabled = unit.Disabled;

            // The registry mirrors what is live. Awake does not run again on an
            // enable, so this is the only thing that puts the unit back in the lists
            // the stream, the census and every by-id lookup read.
            if (disabled) UnitRegistry.Unregister(unit);
            else          UnitRegistry.Register(unit);

            if (disabled == wasDisabled) return;
            if (!CaptureState.HostCaptureActive) return;
            if (unit.UniqueID == 0) return;

            NetworkManager.Instance.BroadcastToClients(new GameEventMessage
            {
                EventType      = GameEventType.UnitEnabledChanged,
                SourceEntityId = unit.UniqueID,
                Param          = disabled ? 0f : 1f,
            });
            Telemetry.Count(disabled ? "v2.capturedUnitDisabled" : "v2.capturedUnitEnabled");
            Plugin.Log.LogInfo($"[Capture] {unit.name} (id={unit.UniqueID}) " +
                               $"{(disabled ? "disabled" : "enabled")} by the mission - replicated.");
        }

        /// <summary>Guest: the host enabled or disabled a unit.</summary>
        public static void ClientApply(int unitId, bool enabled)
        {
            if (Plugin.Instance.CfgIsHost.Value) return;

            // FindById's last resort scans inactive objects too, which is exactly
            // where a disabled unit is.
            var unit = StateSerializer.FindById(unitId);
            if (unit == null)
            {
                Telemetry.Count("v2.unitEnableUnknownId");
                Plugin.Log.LogWarning($"[UnitEnable] Host {(enabled ? "enabled" : "disabled")} id={unitId}, " +
                                      "which is not in this mission here.");
                return;
            }

            if (!enabled) _disabledAt[unitId] = Time.unscaledTime;
            else          _disabledAt.Remove(unitId);

            Set(unit, enabled);
        }

        /// <summary>
        /// Guest: a state sample arrived for a unit that is still disabled here. The
        /// host only streams enabled units, so its copy is live and ours should be -
        /// the announcement was lost, or landed while this machine was still loading.
        /// </summary>
        internal static void ClientEnsureEnabled(ObjectBase unit)
        {
            if (!unit.Disabled) return;
            if (_disabledAt.TryGetValue(unit.UniqueID, out float at)
                && Time.unscaledTime - at < DisableSettleSec) return;

            Telemetry.Count("v2.unitEnableHealed");
            Set(unit, true);
        }

        private static void Set(ObjectBase unit, bool enabled)
        {
            if (unit.Disabled != enabled) return; // already in that state

            try
            {
                using (Authority.Allowed())
                {
                    if (enabled) unit.Enable();
                    else         unit.Disable();
                }
                Plugin.Log.LogInfo($"[UnitEnable] {unit.name} (id={unit.UniqueID}) " +
                                   $"{(enabled ? "enabled" : "disabled")} to match the host.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[UnitEnable] Could not {(enabled ? "enable" : "disable")} " +
                                    $"{unit.name} (id={unit.UniqueID}): {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.Enable))]
    public static class Patch_V2_UnitEnable_Capture
    {
        static void Prefix(ObjectBase __instance, out bool __state) => __state = __instance.Disabled;
        static void Postfix(ObjectBase __instance, bool __state) => UnitEnableSync.OnChanged(__instance, __state);
    }

    [HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.Disable))]
    public static class Patch_V2_UnitDisable_Capture
    {
        static void Prefix(ObjectBase __instance, out bool __state) => __state = __instance.Disabled;
        static void Postfix(ObjectBase __instance, bool __state) => UnitEnableSync.OnChanged(__instance, __state);
    }
}
