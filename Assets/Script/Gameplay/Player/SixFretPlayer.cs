using System.Linq;
using UnityEngine;
using YARG.Core;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine.Guitar;
using YARG.Core.Engine.Guitar.Engines;
using YARG.Core.Input;
using YARG.Core.Game;
using YARG.Gameplay.Visuals;
using YARG.Player;
using YARG.Gameplay.HUD;
using YARG.Core.Replays;

namespace YARG.Gameplay.Player
{
    public sealed class SixFretPlayer : TrackPlayer<GuitarEngine, GuitarNote>
    {
        public override bool ShouldUpdateInputsOnResume => true;

        private static float[] GuitarStarMultiplierThresholds => new[] { 0.21f, 0.46f, 0.77f, 1.85f, 3.08f, 4.52f };

        public GuitarEngineParameters EngineParams { get; private set; }

        [SerializeField]
        private FretArray _fretArray;

        public override float[] StarMultiplierThresholds { get; protected set; } = GuitarStarMultiplierThresholds;
        public override int[] StarScoreThresholds { get; protected set; }

        private SongStem _stem;
        private int _sustainCount;
        public float WhammyFactor { get; private set; }

        public override void Initialize(int index, YargPlayer player, SongChart chart, TrackView trackView, StemMixer mixer, int? currentHighScore)
        {
            _stem = player.Profile.CurrentInstrument.ToSongStem();
            if (_stem == SongStem.Bass && mixer[SongStem.Bass] == null)
            {
                _stem = SongStem.Rhythm;
            }
            base.Initialize(index, player, chart, trackView, mixer, currentHighScore);
        }

        protected override InstrumentDifficulty<GuitarNote> GetNotes(SongChart chart)
        {
            var track = chart.GetSixFretTrack(Player.Profile.CurrentInstrument).Clone();
            return track.GetDifficulty(Player.Profile.CurrentDifficulty);
        }

        protected override GuitarEngine CreateEngine()
        {
            bool isBass = Player.Profile.CurrentInstrument == Instrument.SixFretBass;
            if (!Player.IsReplay)
            {
                EngineParams = Player.EnginePreset.FiveFretGuitar.Create(StarMultiplierThresholds, isBass);
            }
            else
            {
                EngineParams = (GuitarEngineParameters) Player.EngineParameterOverride;
            }

            var engine = new YargFiveFretEngine(NoteTrack, SyncTrack, EngineParams, Player.Profile.IsBot);
            HitWindow = EngineParams.HitWindow;

            engine.OnNoteHit += OnNoteHit;
            engine.OnNoteMissed += OnNoteMissed;
            engine.OnOverstrum += OnOverhit;
            engine.OnSustainStart += OnSustainStart;
            engine.OnSustainEnd += OnSustainEnd;
            engine.OnSoloStart += OnSoloStart;
            engine.OnSoloEnd += OnSoloEnd;
            engine.OnStarPowerPhraseHit += OnStarPowerPhraseHit;
            engine.OnStarPowerStatus += OnStarPowerStatus;
            engine.OnCountdownChange += OnCountdownChange;

            return engine;
        }

        protected override void FinishInitialization()
        {
            base.FinishInitialization();

            StarScoreThresholds = PopulateStarScoreThresholds(StarMultiplierThresholds, Engine.BaseScore);

            IndicatorStripes.Initialize(Player.EnginePreset.FiveFretGuitar);
            _fretArray.FretCount = 3; // Display only 3 frets
            _fretArray.Initialize(
                Player.ThemePreset,
                Player.Profile.GameMode,
                Player.ColorProfile.FiveFretGuitar, // Temporarily use FiveFretGuitar until SixFretGuitar is recognized
                Player.Profile.LeftyFlip);

            GameManager.BeatEventHandler.Subscribe(_fretArray.PulseFretColors);
        }

        protected override void InitializeSpawnedNote(IPoolable poolable, GuitarNote note)
        {
            ((SixFretNoteElement) poolable).NoteRef = note;
        }

        protected override void OnNoteHit(int index, GuitarNote chordParent)
        {
            base.OnNoteHit(index, chordParent);
            if (GameManager.Paused) return;
            foreach (var note in chordParent.AllNotes)
            {
                (NotePool.GetByKey(note) as SixFretNoteElement)?.HitNote();

                if (note.Fret != (int) SixFretGuitarFret.Open)
                {
                    // Map 6-fret positions to display positions (1-3 for both lower and upper)
                    int displayFret = GetDisplayFretForFret(note.Fret);
                    _fretArray.PlayHitAnimation(displayFret);
                }
                else
                {
                    _fretArray.PlayOpenHitAnimation();
                }
            }
        }

        /// <summary>
        /// Maps 6-fret controller positions to 3 display positions
        /// Frets 1-3 → Display positions 0-2 (fret 1-3)
        /// Frets 4-6 → Display positions 0-2 (fret 1-3)
        /// Yellow combinations → Display positions 0-2 (fret 1-3)
        /// </summary>
        private int GetDisplayFretForFret(int fret)
        {
            switch (fret)
            {
                case 1: return 0; // Display as fret 1
                case 2: return 1; // Display as fret 2
                case 3: return 2; // Display as fret 3
                case 4: return 0; // Display as fret 1
                case 5: return 1; // Display as fret 2
                case 6: return 2; // Display as fret 3
                default: return 0; // Default fallback
            }
        }

        protected override void OnNoteMissed(int index, GuitarNote chordParent)
        {
            base.OnNoteMissed(index, chordParent);
            foreach (var note in chordParent.AllNotes)
            {
                (NotePool.GetByKey(note) as SixFretNoteElement)?.MissNote();
            }
        }

        protected override void UpdateVisuals(double songTime)
        {
            UpdateBaseVisuals(Engine.EngineStats, EngineParams, songTime);

            // Update 3 display frets, mapping all 6 controller frets + yellow combinations
            for (int displayFret = 0; displayFret < 3; displayFret++)
            {
                bool isPressed = false;
                
                switch (displayFret)
                {
                    case 0: // Display fret 1
                        isPressed = Engine.IsFretHeld(GuitarAction.Fret1) || // Regular fret 1
                                   Engine.IsFretHeld(GuitarAction.Fret4) || // Upper fret 4 (maps to fret 1)
                                   (Engine.IsFretHeld(GuitarAction.Fret1) && Engine.IsFretHeld(GuitarAction.Fret4)); // Yellow 1
                        break;
                    case 1: // Display fret 2
                        isPressed = Engine.IsFretHeld(GuitarAction.Fret2) || // Regular fret 2
                                   Engine.IsFretHeld(GuitarAction.Fret5) || // Upper fret 5 (maps to fret 2)
                                   (Engine.IsFretHeld(GuitarAction.Fret2) && Engine.IsFretHeld(GuitarAction.Fret5)); // Yellow 2
                        break;
                    case 2: // Display fret 3
                        isPressed = Engine.IsFretHeld(GuitarAction.Fret3) || // Regular fret 3
                                   Engine.IsFretHeld(GuitarAction.Fret6) || // Upper fret 6 (maps to fret 3)
                                   (Engine.IsFretHeld(GuitarAction.Fret3) && Engine.IsFretHeld(GuitarAction.Fret6)); // Yellow 3
                        break;
                }
                
                _fretArray.SetPressed(displayFret, isPressed);
            }
        }

        public override void SetStemMuteState(bool muted)
        {
            if (IsStemMuted != muted)
            {
                GameManager.ChangeStemMuteState(_stem, muted);
                IsStemMuted = muted;
            }
        }

        public override void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(_stem, active);
        }

        protected override void ResetVisuals()
        {
            base.ResetVisuals();
            _fretArray.ResetAll();
        }

        private void OnSustainStart(GuitarNote parent)
        {
            foreach (var note in parent.AllNotes)
            {
                if (parent.IsDisjoint && parent != note)
                {
                    continue;
                }

                if (note.Fret != (int) SixFretGuitarFret.Open)
                {
                    // Map 6-fret positions to display positions
                    int displayFret = GetDisplayFretForFret(note.Fret);
                    _fretArray.SetSustained(displayFret, true);
                }

                _sustainCount++;
            }
        }

        private void OnSustainEnd(GuitarNote parent, double timeEnded, bool finished)
        {
            foreach (var note in parent.AllNotes)
            {
                if (parent.IsDisjoint && parent != note)
                {
                    continue;
                }

                (NotePool.GetByKey(note) as SixFretNoteElement)?.SustainEnd(finished);

                if (note.Fret != (int) SixFretGuitarFret.Open)
                {
                    // Map 6-fret positions to display positions
                    int displayFret = GetDisplayFretForFret(note.Fret);
                    _fretArray.SetSustained(displayFret, false);
                }

                _sustainCount--;
            }

            if (!finished)
            {
                if (!parent.IsDisjoint || _sustainCount == 0)
                {
                    SetStemMuteState(true);
                }
            }

            if (_sustainCount == 0)
            {
                WhammyFactor = 0;
                GameManager.ChangeStemWhammyPitch(_stem, 0);
            }
        }

        protected override bool InterceptInput(ref GameInput input)
        {
            if (input.GetAction<GuitarAction>() == GuitarAction.StarPower && GameManager.IsPractice) return true;
            return false;
        }

        protected override void OnInputQueued(GameInput input)
        {
            base.OnInputQueued(input);

            if (_sustainCount > 0 && input.GetAction<GuitarAction>() == GuitarAction.Whammy)
            {
                WhammyFactor = Mathf.Clamp01(input.Axis);
                GameManager.ChangeStemWhammyPitch(_stem, WhammyFactor);
            }
        }

        public override (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData()
        {
            var frame = new ReplayFrame(Player.Profile, EngineParams, Engine.EngineStats, ReplayInputs.ToArray());
            return (frame, Engine.EngineStats.ConstructReplayStats(Player.Profile.Name));
        }
    }
}