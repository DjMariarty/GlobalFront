using System;
using GlobalFront.Client.Catalog;
using GlobalFront.Client.Presentation;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Commands;
using GlobalFront.Core.Model;
using GlobalFront.Server;
using EntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client.UI
{
    /// <summary>
    /// Every number the tactical HUD displays, pre-formatted once at type init
    /// (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// The contract this exists to keep is the one OD-24 states for the dynamic
    /// canvas: a steady-state frame must neither format a string nor dirty a
    /// <see cref="UnityEngine.UI.Graphic"/>. Avoiding the graphic dirty is the
    /// presenter's job; avoiding the format is this. <c>count.ToString()</c> is a
    /// heap allocation for every value that is not a cached small int, and a
    /// selection that is being dragged changes on most frames — so the strings are
    /// built once, and a frame that changes nothing hands back the same reference it
    /// handed back last time, which is exactly what lets the presenter compare by
    /// reference.
    /// </summary>
    public static class HudStringTable
    {
        /// <summary>Highest selection count with its own cached string.</summary>
        public const int MaxCachedCount = 512;

        /// <summary>Highest percentage with its own cached string.</summary>
        public const int MaxCachedPercent = 100;

        /// <summary>Shown instead of a number above <see cref="MaxCachedCount"/>.</summary>
        public const string SaturatedCount = "512+";

        /// <summary>Shown where there is no value to show — no selection, no stats.</summary>
        public const string NotAvailable = "--";

        private static readonly string[] Counts = BuildCounts();
        private static readonly string[] Percentages = BuildPercentages();

        /// <summary>The cached string for a count, saturating above the table.</summary>
        public static string Count(int value)
        {
            if (value <= 0)
            {
                return Counts[0];
            }

            return value < Counts.Length ? Counts[value] : SaturatedCount;
        }

        /// <summary>The cached string for a whole percentage, clamped into [0..100].</summary>
        public static string Percent(int value)
        {
            if (value <= 0)
            {
                return Percentages[0];
            }

            return value < Percentages.Length ? Percentages[value] : Percentages[Percentages.Length - 1];
        }

        /// <summary>
        /// The cached string for a quantised health readout: <see cref="NotAvailable"/>
        /// when there is nothing resolvable to report, the percentage otherwise. A
        /// negative quantised value is the "nothing to report" signal the state model
        /// uses, and it is not the same case as 0% — 0% means the squad is alive at
        /// zero hit points on the client's readout, which cannot happen, whereas
        /// <c>--</c> means no archetype resolved.
        /// </summary>
        public static string Health(int quantisedPercent) =>
            quantisedPercent < 0 ? NotAvailable : Percent(quantisedPercent);

        /// <summary>A kind's display name, or <see cref="NotAvailable"/> for a kind with no catalog row.</summary>
        public static string KindName(byte kind, IUnitCatalog catalog)
        {
            if (catalog != null && catalog.TryGet(kind, out var definition))
            {
                return definition.DisplayName;
            }

            return NotAvailable;
        }

        private static string[] BuildCounts()
        {
            var table = new string[MaxCachedCount + 1];
            for (var value = 0; value < table.Length; value++)
            {
                table[value] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return table;
        }

        private static string[] BuildPercentages()
        {
            var table = new string[MaxCachedPercent + 1];
            for (var value = 0; value < table.Length; value++)
            {
                table[value] = value + "%";
            }

            return table;
        }
    }

    /// <summary>
    /// What the dynamic selection HUD has to say about the current frame: how many
    /// units, of which archetypes, how healthy, and what the last order was
    /// (Phase 3, step 3.6, ADR-012/OD-24).
    ///
    /// Deliberately not a <see cref="UnityEngine.MonoBehaviour"/>. OD-24's zero-GC
    /// contract for the dynamic canvas is a statement about <em>when</em> the HUD is
    /// allowed to change, and "when" is decidable from data: the interesting question
    /// is whether this frame's selection summary differs from last frame's, and that
    /// is a comparison a test can make without a canvas, a frame loop or a font. The
    /// presenter in front of this type is the part that needs the canvas, and it is
    /// thin for the same reason <see cref="MinimapRadarGraphic"/> is.
    ///
    /// <b>Where the numbers come from.</b> Ownership, archetype and hit points are
    /// read from <see cref="ClientReplicationWorld"/> through the selection's slot
    /// indices — never from <see cref="GlobalFront.Client.Presentation.UnitView"/>,
    /// which caches none of them (step 3.3). The archetype rows come from
    /// <see cref="UnitCatalog"/> (OD-29), and a unit whose archetype the client has no
    /// row for is reported as unresolved rather than guessed at, which is the same
    /// rule the health bars keep: an empty bar on an unknown unit is a lie the player
    /// cannot act on, so the readout says <see cref="HudStringTable.NotAvailable"/>
    /// instead.
    /// </summary>
    public sealed class SelectionHudState
    {
        private readonly UnitSelectionController _selection;
        private readonly ClientReplicationWorld _world;
        private readonly int[] _kindCounts = new int[UnitKinds.Count];
        private readonly float[] _invMaxHealthByKind = new float[UnitKinds.Count];
        private readonly string[] _kindNames = new string[UnitKinds.Count];

        private int _selectedCount;
        private int _healthPercent = -1;
        private GameCommandType _lastCommandKind = GameCommandType.None;
        private MatchCommandRejection _lastRejection = MatchCommandRejection.None;
        private int _submittedCommandCount;
        private int _rejectedCommandCount;
        private int _lastObservedAttemptCount;
        private UnitCommandChannelSink _lastObservedSink;
        private bool _hasCommandFeedback;

        public SelectionHudState(
            UnitSelectionController selection,
            ClientReplicationWorld world,
            IUnitCatalog catalog = null)
        {
            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _selection = selection;
            _world = world;

            // The catalog is what makes a kind nameable and a health fraction
            // computable. Resolved once into flat arrays rather than queried per unit
            // per frame: the walk below runs for every selected unit, and a virtual
            // interface call per unit is the same cost the binder avoided the same way
            // (see UnitViewBinder's own row table).
            var source = catalog ?? UnitCatalog.Default;

            // Every row gets a name before any is resolved, because kind 0 is
            // Unknown and is never visited by the loop below — an archetype the client
            // has no row for has to read as "no name", not as a null reference handed
            // straight to a Text.
            for (var kind = 0; kind < _kindNames.Length; kind++)
            {
                _kindNames[kind] = HudStringTable.NotAvailable;
            }

            for (var kind = 1; kind < UnitKinds.Count; kind++)
            {
                if (UnitKinds.IsDefined((byte)kind) && source.TryGet((byte)kind, out var definition))
                {
                    _invMaxHealthByKind[kind] = definition.InvMaxHealth;
                    _kindNames[kind] = definition.DisplayName;
                }
            }
        }

        /// <summary>
        /// The production command sink, whose last submission this panel reports.
        /// Null in a build that has no channel yet, in which case the command half of
        /// the panel reads <see cref="HudStringTable.NotAvailable"/>.
        /// </summary>
        public UnitCommandChannelSink CommandFeedback { get; set; }

        /// <summary>Units in the selection right now.</summary>
        public int SelectedCount => _selectedCount;

        /// <summary>Live, friendly units of one archetype inside the selection.</summary>
        public int GetKindCount(byte kind) =>
            (uint)kind < (uint)_kindCounts.Length ? _kindCounts[kind] : 0;

        /// <summary>
        /// Mean health of the selection as a whole percentage, or -1 when no selected
        /// unit has a resolvable archetype. -1 rather than 0 because the two mean
        /// different things to the player.
        /// </summary>
        public int HealthPercent => _healthPercent;

        /// <summary>Kind of the last order the panel observed.</summary>
        public GameCommandType LastCommandKind => _lastCommandKind;

        /// <summary>Rejection the authoritative side returned for it, if the sink reported one.</summary>
        public MatchCommandRejection LastRejection => _lastRejection;

        /// <summary>Orders accepted since the channel was opened.</summary>
        public int SubmittedCommandCount => _submittedCommandCount;

        /// <summary>Orders the sink reported back as rejected.</summary>
        public int RejectedCommandCount => _rejectedCommandCount;

        // --------------------------------------------------------------- display text

        /// <summary>Selection size, from the cached number table.</summary>
        public string CountText => HudStringTable.Count(_selectedCount);

        /// <summary>An archetype's name, decided once from the catalog.</summary>
        public string KindName(byte kind) =>
            (uint)kind < (uint)_kindNames.Length ? _kindNames[kind] : HudStringTable.NotAvailable;

        /// <summary>How many selected units belong to one archetype.</summary>
        public string KindCountText(byte kind) => HudStringTable.Count(GetKindCount(kind));

        /// <summary>The mean-health readout.</summary>
        public string HealthText => HudStringTable.Health(_healthPercent);

        /// <summary>The player whose units these are.</summary>
        public string PlayerText => _selection.LocalPlayerId.IsValid
            ? HudStringTable.Count(_selection.LocalPlayerId.Value)
            : HudStringTable.NotAvailable;

        // -------------------------------------------------------------------- update

        /// <summary>
        /// Re-reads the selection and the command sink. Call once per frame from the
        /// presenter, before comparing anything.
        ///
        /// Allocation-free: one walk of the selection's slot indices, reading the
        /// replication table through them. The selection is capped at
        /// <see cref="GlobalFront.Core.Simulation.SimulationConstants.MaxSelectedEntities"/>
        /// per command and at the binder's slot count in the panel, so the walk is
        /// bounded by a number, not by the size of the army.
        ///
        /// <b>Only live, local units are counted.</b> The presenter and the controller's
        /// own prune do not have a fixed order within a frame, and
        /// <see cref="UnitSelectionController.LocalPlayerId"/> changes on a reconnect
        /// without the selection being rebuilt. So the slot index alone names nothing:
        /// the row has to still carry the entity that was selected, be alive, and belong
        /// to the player whose squad this panel claims to describe. A count of five with
        /// two corpses in it sends a move order to units the server will refuse, and
        /// shows the player a squad they no longer have.
        /// </summary>
        public void Update()
        {
            var counts = _kindCounts;
            for (var index = 0; index < counts.Length; index++)
            {
                counts[index] = 0;
            }

            var selected = _selection.SelectedCount;
            var localPlayer = _selection.LocalPlayerId;
            var liveSelected = 0;
            var healthSum = 0f;
            var healthSamples = 0;

            for (var index = 0; index < selected; index++)
            {
                var slot = _selection.GetSelectedSlot(index);
                var expectedEntity = _selection.GetSelectedEntity(index);
                if (!_world.TryGetSlotState(slot, out var state) ||
                    state.Entity != expectedEntity ||
                    state.Health <= 0 ||
                    state.Owner != localPlayer)
                {
                    // A slot the controller still names but the table no longer reports,
                    // or one it has reused for a different unit: the presenter runs after
                    // the controller's prune in some frames and before it in others, so
                    // this is a normal transient, not a corruption. The panel simply
                    // leaves the unit out of this frame's numbers.
                    continue;
                }

                liveSelected++;

                var kind = state.UnitKind;
                if ((uint)kind < (uint)counts.Length)
                {
                    counts[kind]++;
                }

                var invMax = (uint)kind < (uint)_invMaxHealthByKind.Length
                    ? _invMaxHealthByKind[kind]
                    : 0f;
                if (invMax <= 0f)
                {
                    continue;
                }

                // Clamped from above as well as below: the wire's hit points are an int
                // the server does not cap for the client's benefit, and a unit carrying
                // more than its archetype's maximum — an overheal, or a catalog row that
                // disagrees with the authority's — would otherwise push the squad mean
                // past 100%.
                healthSum += Math.Min(1f, Math.Max(0f, state.Health * invMax));
                healthSamples++;
            }

            _selectedCount = liveSelected;

            // Quantised to a whole percentage before it is compared, which is what
            // keeps the readout still. A mean over a hundred units changes on almost
            // every replication packet in a firefight; shown to full precision it would
            // dirty the panel several times a second for a difference nobody can see,
            // and OD-24's steady-state rule is about the visible frame rate, not about
            // the number of digits.
            _healthPercent = healthSamples > 0
                ? (int)System.Math.Round(
                    healthSum / healthSamples * HudStringTable.MaxCachedPercent,
                    System.MidpointRounding.AwayFromZero)
                : -1;

            UpdateCommandFeedback();
        }

        /// <summary>
        /// Pulls the last order out of the production sink, if there is one, and counts
        /// the ones that came back refused.
        ///
        /// <see cref="UnitCommandChannelSink.AttemptedCount"/> is the signal rather than
        /// <see cref="UnitCommandChannelSink.SubmittedCount"/>, because an order the
        /// server refused leaves the accepted counter exactly where it was — and an
        /// order the player needs to see refused is precisely the one that must not be
        /// invisible. Rejections are counted by watching attempts move rather than by
        /// reading a history the sink does not keep: the authoritative answer for a
        /// command arrives asynchronously over C0 (ADR-009), so "an order was handed
        /// over and came back refused" is the whole of what this layer can honestly say.
        /// </summary>
        private void UpdateCommandFeedback()
        {
            var feedback = CommandFeedback;
            if (feedback == null)
            {
                _hasCommandFeedback = false;
                return;
            }

            // A channel swap — a reconnect, a host migration, a test handing the panel a
            // fresh session — starts the order history again from nothing. Attempted
            // counts alone decide "a new order happened", and two sinks can agree on that
            // number while disagreeing on what the order was, which would leave the panel
            // reporting the previous session's result.
            if (!ReferenceEquals(feedback, _lastObservedSink))
            {
                _lastObservedSink = feedback;
                _lastObservedAttemptCount = 0;
                _lastCommandKind = GameCommandType.None;
                _lastRejection = MatchCommandRejection.None;
                _hasCommandFeedback = false;
            }

            var attempted = feedback.AttemptedCount;
            if (attempted != _lastObservedAttemptCount)
            {
                _lastObservedAttemptCount = attempted;
                _lastCommandKind = feedback.LastKind;
                _lastRejection = feedback.LastRejection;
                _hasCommandFeedback = true;
                if (_lastRejection != MatchCommandRejection.None)
                {
                    _rejectedCommandCount++;
                }
            }

            _submittedCommandCount = feedback.SubmittedCount;
        }

        /// <summary>Text for the last order: its kind, or "none" when nothing has been issued.</summary>
        public string CommandText => TacticalHudText.ForCommand(_lastCommandKind);

        /// <summary>Text for the last order's outcome.</summary>
        public string RejectionText => TacticalHudText.ForRejection(_lastRejection, _hasCommandFeedback);

        /// <summary>Accepted orders, from the cached number table.</summary>
        public string SubmittedCommandText =>
            _hasCommandFeedback ? HudStringTable.Count(_submittedCommandCount) : HudStringTable.NotAvailable;

        /// <summary>Refused orders, from the cached number table.</summary>
        public string RejectedCommandText =>
            _hasCommandFeedback ? HudStringTable.Count(_rejectedCommandCount) : HudStringTable.NotAvailable;
    }

    /// <summary>
    /// The command and rejection wording the selection panel shows, as a fixed table
    /// rather than as formatted text (step 3.6, OD-24).
    ///
    /// Indexed by the enum's own numeric value, so a new
    /// <see cref="GameCommandType"/> or <see cref="MatchCommandRejection"/> member
    /// added later falls through to a named "unrecognised" entry instead of throwing
    /// at the one point in the frame where the HUD is not allowed to fail. The
    /// alternative — <c>kind.ToString()</c> — allocates on every order, which is once
    /// per click and therefore affordable, but it would also mean the panel's wording
    /// is the enum's spelling, which is not a string a player should be shown.
    /// </summary>
    public static class TacticalHudText
    {
        /// <summary>Nothing has been ordered yet.</summary>
        public const string NoCommand = "--";

        private static readonly string[] CommandNames =
        {
            NoCommand,      // GameCommandType.None
            "Move",         // Move
            "Attack",       // Attack
            "Attack move",  // AttackMove
            "Stop",         // Stop
            "Guard",        // Guard
            "Build",        // Build
            "Produce",      // Produce
            "Ability",      // UseAbility
        };

        private static readonly string[] RejectionNames =
        {
            "Accepted",          // MatchCommandRejection.None
            "Match over",        // MatchOver
            "Header rejected",   // HeaderRejected
            "Duplicate order",   // DuplicateSequence
            "Unsupported order", // UnsupportedCommandType
            "Invalid payload",   // InvalidPayload
            "Unknown unit",      // UnknownEntity
            "Not your unit",     // NotEntityOwner
            "Unit is gone",      // EntityNotAlive
            "Formation off map", // FormationOutOfBounds
            "Session rejected",  // SessionRejected
        };

        private const string Unrecognised = "Unrecognised";

        /// <summary>Display wording for an order kind.</summary>
        public static string ForCommand(GameCommandType kind) =>
            (int)kind >= 0 && (int)kind < CommandNames.Length ? CommandNames[(int)kind] : Unrecognised;

        /// <summary>
        /// Display wording for an order's outcome. Says
        /// <see cref="NoCommand"/> rather than "Accepted" when no order has been
        /// observed at all, because a panel that opens reading "Accepted" is reporting
        /// a result the player never asked for.
        /// </summary>
        public static string ForRejection(MatchCommandRejection rejection, bool hasCommand)
        {
            if (!hasCommand)
            {
                return NoCommand;
            }

            return (int)rejection >= 0 && (int)rejection < RejectionNames.Length
                ? RejectionNames[(int)rejection]
                : Unrecognised;
        }
    }
}
