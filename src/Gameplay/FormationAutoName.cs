using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace SeapowerMultiplayer
{
    /// <summary>
    /// Names air formations after what they are carrying.
    ///
    /// A flight that leaves a deck is called "&lt;leader&gt; Flight" by the game, which
    /// says nothing about why it was launched. With two players putting aircraft up
    /// from the same carriers the Formation Manager turns into a column of near
    /// identical rows, and telling the CAP from the strike means opening each one.
    /// This puts the loadout in front: "Air to Air - Red-1 Flight".
    ///
    /// HOST ONLY while a session is up. A formation's name already replicates - the
    /// Name setter is patched (Patch_UnitFormation_Name) and travels as a
    /// FormationOp.Rename - so the host names the flight and everyone else is told.
    /// Guests naming their own copies as well would send every name twice.
    ///
    /// KEEPS THE NAME IT FOUND. The game always gives a new formation a name - a
    /// launch gets "&lt;callsign&gt; Flight", a mission-authored one whatever its author
    /// wrote - so there is no "unnamed" state to look for. The name a formation has
    /// the first time it is seen is kept as its base, and the loadout goes in front
    /// of that.
    ///
    /// NEVER OVERRIDES A PLAYER. After that first look a formation is only touched
    /// while its name is exactly what this class last gave it. The moment somebody
    /// types a name of their own it stops matching, and the flight is left alone for
    /// good.
    /// </summary>
    public static class FormationAutoName
    {
        private const float SweepIntervalSec = 2f;
        private static float _nextSweep;

        private const string Separator = " - ";

        private sealed class Entry
        {
            /// <summary>The name the formation had when first seen.</summary>
            public string Base = "";
            /// <summary>The name this class last gave it.</summary>
            public string Given = "";
            /// <summary>A player renamed it - never touch it again.</summary>
            public bool HandsOff;
            /// <summary>Repeats of the current name still owed to the other players.</summary>
            public int Repeats;
            public float RepeatAt;
        }

        /// <summary>A rename is addressed to "the formation led by this unit", and a
        /// guest builds its copy of a new flight from a separate message - so a name
        /// sent the moment the flight forms can arrive before there is anything to
        /// put it on. It is said again a few times, spaced out, rather than once.</summary>
        private static readonly float[] RepeatAfterSec = { 6f, 20f, 60f };

        /// <summary>What is known about each formation. Weak, so a formation that is
        /// disbanded takes its entry with it.</summary>
        private static readonly ConditionalWeakTable<UnitFormation, Entry> _entries = new();

        private static readonly AccessTools.FieldRef<UnitFormation, string>? _nameRef =
            AccessTools.FieldRefAccess<UnitFormation, string>("_name");

        /// <summary>The game's own automatic name ("&lt;leader&gt; Flight"), kept up to
        /// date by the LeaderStation setter. Private, with a private accessor.</summary>
        private static readonly AccessTools.FieldRef<UnitFormation, string>? _internalNameRef =
            AccessTools.FieldRefAccess<UnitFormation, string>("_internalName");

        private static readonly List<string> _loadouts = new();
        private static bool _warned;

        /// <summary>Called every frame from Plugin.Update; does its work every couple
        /// of seconds. A sweep rather than a hook on formation creation because the
        /// name depends on things that keep changing after it - members joining as
        /// they launch, the leader being swapped, callsigns being assigned.</summary>
        public static void Tick()
        {
            if (Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + SweepIntervalSec;

            if (!Plugin.Instance.CfgAutoNameFlights.Value) return;
            if (_nameRef == null) return;
            // In a session the host names and the name replicates. Out of one, this
            // machine is the only one there is.
            if (NetworkManager.Instance.IsConnected && !Plugin.Instance.CfgIsHost.Value) return;
            if (!SessionManager.MissionIsLive || SessionManager.SceneLoading) return;
            if (!Singleton<TaskforceManager>.InstanceExists(false)) return;

            try
            {
                var sides = Singleton<TaskforceManager>.Instance._taskForces;
                for (int i = 0; i < sides.Count; i++)
                {
                    var tf = sides[i];
                    if (tf?.Formations == null) continue;
                    // Only sides a person commands - an AI side has nobody to read it.
                    if (tf != Globals._playerTaskforce && !Teams.IsHumanControlled(tf)) continue;

                    for (int f = 0; f < tf.Formations.Count; f++)
                        Consider(tf.Formations[f]);
                }
            }
            catch (System.Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.Log.LogWarning($"[AutoName] Formation naming sweep failed: {ex.Message}");
            }
        }

        private static void Consider(UnitFormation formation)
        {
            var leader = formation?.LeaderStation?.UnitObject;
            if (leader == null || leader.IsDestroyed) return;
            if (!(leader is Aircraft) && !(leader is Helicopter)) return;

            string current = _nameRef!(formation!) ?? "";

            string? loadout = LoadoutsOf(formation!, leader);
            if (loadout == null) return;

            if (!_entries.TryGetValue(formation!, out var entry))
            {
                entry = new Entry { Base = current, Given = current };
                // A name this class gave it in an earlier life - a formation rebuilt
                // from a save comes back already carrying its prefix, and taking that
                // as the base would stack a second one on top.
                int cut = current.IndexOf(Separator, System.StringComparison.Ordinal);
                if (cut > 0 && current.StartsWith(loadout + Separator, System.StringComparison.Ordinal))
                    entry.Base = current.Substring(cut + Separator.Length);
                if (entry.Base.Length == 0 && _internalNameRef != null)
                    entry.Base = _internalNameRef(formation!) ?? "";
                _entries.Add(formation!, entry);
            }

            if (entry.HandsOff) return;
            if (current != entry.Given)
            {
                // Not what was left here last time: a player renamed it.
                entry.HandsOff = true;
                return;
            }

            string wanted = entry.Base.Length == 0 ? loadout : loadout + Separator + entry.Base;
            if (wanted == current)
            {
                if (entry.Repeats <= 0 || Time.unscaledTime < entry.RepeatAt) return;
                if (!NetworkManager.Instance.IsConnected) { entry.Repeats = 0; return; }
                entry.Repeats--;
                int next = RepeatAfterSec.Length - entry.Repeats;
                if (entry.Repeats > 0) entry.RepeatAt = Time.unscaledTime + RepeatAfterSec[next];
                // Same name again: nothing changes here, the setter's patch re-sends it.
            }
            else
            {
                entry.Repeats  = RepeatAfterSec.Length;
                entry.RepeatAt = Time.unscaledTime + RepeatAfterSec[0];
            }

            entry.Given = wanted;

            // Under Authority so the ownership gate reads this as the simulation's
            // doing rather than the host player's: the setter's patch broadcasts the
            // rename, and a refusal there would leave the name changed here and not
            // on anyone else's screen.
            using (Authority.Allowed())
                formation!.Name = wanted;
        }

        /// <summary>The loadout(s) the flight is carrying - the leader's first, then
        /// any a wingman has that differs. Null when nothing useful is known.</summary>
        private static string? LoadoutsOf(UnitFormation formation, ObjectBase leader)
        {
            _loadouts.Clear();
            Add(leader);
            var stations = formation.Stations;
            if (stations != null)
                for (int i = 0; i < stations.Count; i++)
                    Add(stations[i]?.UnitObject);

            if (_loadouts.Count == 0) return null;
            if (_loadouts.Count > 2) return "Mixed";
            return string.Join(" / ", _loadouts);
        }

        private static void Add(ObjectBase? unit)
        {
            string? variant = unit?._obp?._loadoutVariant;
            if (string.IsNullOrEmpty(variant) || variant == "None") return;
            string display = DisplayName(variant!);
            if (!_loadouts.Contains(display)) _loadouts.Add(display);
        }

        /// <summary>The loadout's display name, looked up the way the game's own
        /// Loadout.DisplayName does it, falling back to the raw key.</summary>
        private static string DisplayName(string variant)
        {
            try
            {
                if (Singleton<LanguageResourceHandler>.InstanceExists(false)
                    && Singleton<LanguageResourceHandler>.Instance.LanguageDictionary
                        .TryGetValue("loadoutnames" + variant.ToLower(), out string text)
                    && !string.IsNullOrEmpty(text))
                    return text.Trim();
            }
            catch { /* fall through to the key */ }
            return variant;
        }
    }
}
