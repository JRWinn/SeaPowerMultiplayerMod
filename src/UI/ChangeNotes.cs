using UnityEngine;

namespace SeapowerMultiplayer.UI
{
    /// <summary>
    /// The short "what changed" card shown at launch.
    ///
    /// Shown once per set of notes, not once per launch: it comes up in the main menu
    /// the first time a build carrying new notes starts, and dismissing it records
    /// <see cref="Id"/> so it stays away until the notes change. A card that returns
    /// on every start is one players learn to click through without reading.
    ///
    /// TO ANNOUNCE A NEW SET OF CHANGES: rewrite <see cref="Text"/> and change
    /// <see cref="Id"/> to anything it has not been before. Nothing else needs
    /// touching - the overlay binds to these two strings.
    ///
    /// Keep it to a few lines a player can take in at a glance. This is not the
    /// changelog; it is the three things they would otherwise find out by surprise.
    /// </summary>
    internal static class ChangeNotes
    {
        /// <summary>Identifies this set of notes. Compared for equality only.</summary>
        // The build stamp is part of the id on purpose: every new build shows the
        // card once, which is how a player sees at a glance that the file they were
        // sent is the one that loaded.
        internal const string Id = "notes-3 " + BuildStamp.Value;

        internal const string Title = "What's new in this build";

        // One item per line, split with \n - the card is a single wrapping TextBlock,
        // so there is no list markup to keep in step. Plain hyphens rather than a
        // bullet glyph: the overlay font is the game's, and a hyphen cannot come out
        // as a missing-character box.
        internal const string Text =
            "- Teammates see enemy missiles, torpedoes and rockets the host has detected.\n" +
            "- Units a mission brings in later, like timed air strikes, now appear and move for teammates.\n" +
            "- A refresh (Ctrl+F10) no longer lets the host run on while a teammate loads.\n" +
            "- A teammate's aircraft no longer get stuck returning to base, and fly with gear up.\n" +
            "- A second mission in the same lobby starts clean.\n" +
            "- Flights are named after their loadout in the Formation Manager.\n" +
            "- The Formation Manager lists only your own units. Ctrl+F8 switches to teammates' or everyone's.\n" +
            "- You are warned if a player joins on a different build.";

        internal const string Footnote =
            "Build " + BuildStamp.Value + ". Everyone in the session should be on this build.";

        /// <summary>Held back for the same boot hitch the consent prompt waits out, so
        /// the card does not flash up over a half-drawn main menu.</summary>
        private const float ShowDelaySec = 3f;

        /// <summary>
        /// True while the card should be on screen.
        ///
        /// Yields to the two popups that share its spot: a failed init matters more
        /// than release notes, and the one-time consent prompt is a question that has
        /// to be answered. Every popup is the same centred card, so two at once would
        /// simply sit on top of each other; this one waits its turn instead.
        /// </summary>
        internal static bool ShouldShow =>
            Plugin.Instance != null
            && Plugin.Instance.CfgSeenChangeNotes.Value != Id
            && Plugin.FatalInitError == null
            && !Analytics.ShouldPromptConsent
            && Time.realtimeSinceStartup > ShowDelaySec;

        internal static void Dismiss() => Plugin.Instance.CfgSeenChangeNotes.Value = Id;
    }
}
