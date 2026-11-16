using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;
using UnityEngine;

namespace SeapowerMultiplayer.UI
{
    /// <summary>Whose formations and units the game's Formation Manager lists.</summary>
    public enum FormationView
    {
        /// <summary>What this player commands, plus anything nobody owns.</summary>
        Mine,
        /// <summary>What the other players on the team command.</summary>
        Teammates,
        /// <summary>The whole side - what the game shows on its own.</summary>
        Everyone,
    }

    /// <summary>
    /// Makes the Formation Manager answer "whose is this?".
    ///
    /// The manager fills itself from the player's whole taskforce, and in co-op that
    /// taskforce is shared: every formation and lone unit of every teammate lands in
    /// one list, with nothing on a row to say who commands it. Two people flying off
    /// the same carriers produce a column of near-identical flights, half of which
    /// refuse your orders.
    ///
    /// The rows are the game's own objects drawn by the game's own templates, so there
    /// is nowhere to hang a label without changing the formation's real name (which is
    /// saved and replicated). What can be done cleanly is choosing which rows exist:
    /// the list is narrowed to one owner group at a time, and Ctrl+F8 steps through
    /// Mine / Teammates' / Everyone.
    ///
    /// Only ever narrows, and only where ownership means something - an established
    /// session with the unit lock on and at least one teammate. Anywhere else the
    /// manager is left exactly as the game builds it.
    /// </summary>
    public static class FormationManagerFilter
    {
        private static WeakReference<FormationManagerViewModel>? _vm;

        private static readonly MethodInfo? _rebuild =
            AccessTools.Method(typeof(FormationManagerViewModel), "RebuildFromTaskforce");
        private static readonly MethodInfo? _removeItem =
            AccessTools.Method(typeof(FormationManagerViewModel), "RemoveItem");
        private static readonly FieldInfo? _itemCategories =
            AccessTools.Field(typeof(FormationManagerViewModel), "_itemCategories");

        private static bool _fullRefresh;
        private static readonly HashSet<int> _changedIds = new();
        private static bool _wasActive;
        private static bool _warned;

        // ── The notice shown when the view changes ────────────────────────────

        public static float  NoticeUntil { get; private set; }
        public static string NoticeText  { get; private set; } = "";
        private const float NoticeSec = 3.5f;

        // ── View ──────────────────────────────────────────────────────────────

        public static FormationView View
        {
            get => Enum.TryParse(Plugin.Instance.CfgFormationManagerView.Value, true, out FormationView v)
                ? v : FormationView.Mine;
            private set => Plugin.Instance.CfgFormationManagerView.Value = value.ToString();
        }

        /// <summary>Ownership only means something in an established session with the
        /// lock on and somebody to share the side with.</summary>
        private static bool Applies =>
            NetworkManager.Instance.IsEstablished
            && OwnershipRules.LockActive
            && Teams.HasTeammates;

        public static string Describe(FormationView view) => view switch
        {
            FormationView.Mine      => "my units",
            FormationView.Teammates => "teammates' units",
            _                       => "everyone's units",
        };

        /// <summary>Text for the settings button.</summary>
        public static string ButtonText => $"Formation Manager: {Describe(View)}";

        public static void Cycle()
        {
            View = View switch
            {
                FormationView.Mine      => FormationView.Teammates,
                FormationView.Teammates => FormationView.Everyone,
                _                       => FormationView.Mine,
            };
            _fullRefresh = true;

            NoticeText = Applies
                ? $"Formation Manager: showing {Describe(View)}  (Ctrl+F8 to change)"
                : $"Formation Manager set to {Describe(View)} - applies in a session with the unit lock on";
            NoticeUntil = Time.unscaledTime + NoticeSec;
        }

        // ── The filter ────────────────────────────────────────────────────────

        private static ObjectBase? UnitOf(object? item) => item switch
        {
            UnitFormation f => f.LeaderStation?.UnitObject,
            ObjectBase o    => o,
            _               => null,
        };

        /// <summary>Should this row be left out of the manager?</summary>
        internal static bool Hides(object? item)
        {
            if (!Applies) return false;
            var view = View;
            if (view == FormationView.Everyone) return false;

            var unit = UnitOf(item);
            if (unit == null) return false;   // nothing to judge it by - leave it in

            byte owner = FormationOwnership.OwnerOf(unit);
            bool mine  = owner == PlayerRegistry.LocalSlot;
            bool free  = owner == FormationOwnership.Unowned;

            // An unowned unit answers to anybody, so it is never hidden.
            return view == FormationView.Mine ? !(mine || free) : mine;
        }

        internal static void Remember(FormationManagerViewModel vm)
        {
            if (_vm != null && _vm.TryGetTarget(out var known) && ReferenceEquals(known, vm)) return;
            _vm = new WeakReference<FormationManagerViewModel>(vm);
        }

        // ── Keeping the list in step ──────────────────────────────────────────

        /// <summary>One unit changed hands.</summary>
        public static void OnOwnershipChanged(int unitId) => _changedIds.Add(unitId);

        /// <summary>The whole table changed.</summary>
        public static void OnOwnershipReset() => _fullRefresh = true;

        /// <summary>Every frame, from Plugin.Update.</summary>
        public static void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F8)
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                Cycle();

            bool active = Applies;
            if (active != _wasActive) { _wasActive = active; _fullRefresh = true; }

            if (!_fullRefresh && _changedIds.Count == 0) return;
            if (_vm == null || !_vm.TryGetTarget(out var vm))
            {
                // No manager built yet; it will filter itself as it is filled.
                _fullRefresh = false;
                _changedIds.Clear();
                return;
            }

            try
            {
                if (!_fullRefresh) _fullRefresh = !Reconcile(vm);
                if (_fullRefresh) _rebuild?.Invoke(vm, null);
            }
            catch (Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning($"[FormationView] Could not refresh the Formation Manager: " +
                                          $"{(ex.InnerException ?? ex).Message}");
                }
            }
            finally
            {
                _fullRefresh = false;
                _changedIds.Clear();
            }
        }

        /// <summary>
        /// Bring the list into line after individual units changed hands, without
        /// rebuilding it - a rebuild collapses whatever the player had open, and
        /// aircraft change hands every time one is launched.
        ///
        /// Rows that should no longer be there are taken out with the manager's own
        /// RemoveItem. A row that should now be there and is not cannot be put back
        /// that way (the manager applies conditions of its own when it adds units), so
        /// that case returns false and the caller rebuilds.
        /// </summary>
        private static bool Reconcile(FormationManagerViewModel vm)
        {
            if (_itemCategories?.GetValue(vm) is not IDictionary present || _removeItem == null)
                return false;

            List<object>? toRemove = null;
            foreach (var key in present.Keys)
                if (Hides(key)) (toRemove ??= new List<object>()).Add(key);

            if (toRemove != null)
                for (int i = 0; i < toRemove.Count; i++)
                    _removeItem.Invoke(vm, new[] { toRemove[i], (object)true });

            foreach (int id in _changedIds)
            {
                var unit = StateSerializer.FindById(id);
                if (unit == null || unit.IsDestroyed) continue;
                if (unit._taskforce != Globals._playerTaskforce) continue;

                var formation = unit.Formation;
                bool listed = present.Contains(unit) || (formation != null && present.Contains(formation));
                bool wanted = !Hides((object?)formation ?? unit);
                if (wanted && !listed) return false;
            }
            return true;
        }
    }

    /// <summary>Every row the manager lists goes through AddItem - when it is first
    /// built, when it is rebuilt, and when the taskforce gains a formation or a unit.</summary>
    [HarmonyPatch(typeof(FormationManagerViewModel), "AddItem")]
    public static class Patch_FormationManager_AddItem_OwnerFilter
    {
        static bool Prefix(FormationManagerViewModel __instance, INotifyPropertyChanged item)
        {
            FormationManagerFilter.Remember(__instance);
            return !FormationManagerFilter.Hides(item);
        }
    }
}
