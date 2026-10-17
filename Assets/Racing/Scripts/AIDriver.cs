using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Bot = a player with a thumb on the same virtual joystick (design doc "RC minihra – AI stratégia botov"):
    /// <list type="bullet">
    /// <item><b>Same driving logic, same input.</b> The game drives every car along the track; the bot, like the player,
    /// only shifts the nav point, holds boost / brake and swipes dashes - under the same joystick rules
    /// (taps under 0.8 s are free, long holds drain energy, release locks the joystick for 3 s).</item>
    /// <item><b>Perception</b> of other cars from a snapshot delayed by its own reaction time (log-normal per look),
    /// extrapolated like a human would, incl. cars behind.</item>
    /// <item><b>Decisions</b> in intervals (0.18–0.35 s, jittered), utility scored, held 2–4 s, switched only when clearly
    /// better (hysteresis): overtake side (free space + inside of the next corner), slipstream, one-move defence,
    /// boost on straights released before the corner (sometimes too late - the car slides wide),
    /// dashes to ram / push / pass with a short wind-up, energy kept below the top.</item>
    /// <item><b>Thumb</b>: limited speed, short taps for line changes, personal preferred line + slow wander.</item>
    /// <item><b>Character</b>: archetype + ±12 % spread, so no two bots drive alike.</item>
    /// </list>
    /// Never does what humans don't: steering into walls, braking for no reason, same-frame reactions.
    /// Distances are metres along the track (gap) or across it (lateral); widths / lengths are car sizes.
    /// </summary>
    public class AIDriver : IDriver
    {
        // Perception.
        const float AheadRange = 40f, BehindRange = 20f;           // m a car ahead / behind is watched
        const float SameLaneWidths = 1.3f;                          // x car width = in my lane
        const float BesideLengths = 0.75f;                          // x car length = alongside
        const float BesideMinWidths = 0.85f, BesideMaxWidths = 2.2f;
        const float EdgeMargin = 1.25f;                             // m between my side and the road edge
        const float StraightBrakeDistance = 120f;                   // m to the next braking point = "on a straight"
        const float CornerLookFrom = 20f, CornerLookTo = 90f;       // m ahead the next corner's inside is read
        const float PassExtra = 0.8f;                               // m besides a car width to pass (racing_unity overtake line)

        // Position: overtake / slipstream / defend.
        const float PassDoneBehind = 2f;                            // m past a car length behind me = pass completed
        const float PassGiveUp = 35f;                               // m the target got away
        const float DraftMinGap = 9f, DraftMinLove = 0.4f, DraftHold = 1.5f, DraftScore = 0.5f;
        const float OvertakeMaxGap = 25f;                           // m
        const float OvertakeMinClosing = -0.5f;                     // m/s; not losing ground on it
        const int LineLookSteps = 8;                                // track samples ahead for the racing line
        const float LineWeight = 0.6f;                              // score lost per road half-width away from the line
        const float InsideBonus = 0.35f, InsideAggression = 0.15f;  // score for the inside of the next corner
        const float BlockedLook = 10f, BlockedPenalty = 1f;         // m past the target checked for traffic
        const float SwitchMargin = 1.25f;                           // a new side must score 25 % better (hysteresis)
        const float OvertakeHoldMin = 2f, OvertakeHoldMax = 4f;     // s
        const float PassingKeep = 1f;                               // s an overtake in progress is re-held each look
        const float DefendRange = 12f, DefendFaster = 1f;           // m behind, m/s faster than me
        const float DefendMinAggression = 0.45f, DefendLateral = 0.55f, DefendHold = 2.5f, DefendCooldown = 6f;

        // Boost.
        const float BrakeZoneLook = 150f;                           // m; one release roll per braking zone within it
        const int BrakeZoneSame = 10;                               // track samples = the same zone
        const float LateReleaseMin = 0.3f, LateReleaseMax = 0.8f;   // s too late when the roll says "mistake"
        const float ReleaseJitter = 0.1f;                           // ± share of the release lead otherwise
        const float ChaseGap = 15f;                                 // m to the next car / the player = chase
        const float BoostWantThreshold = 0.45f;
        const float BoostMinGrip = 0.5f;                            // no boost on ice
        const float HeldBackBelow = 0.95f, PulledUpAbove = 1.05f;   // handicap / handicap factor
        const float FinalSprintShare = 0.85f;                       // of the race distance
        const float CheckpointFightRange = 80f, CheckpointAhead = 15f, CheckpointBehind = 10f;

        // Brake (gap keeping).
        const float LaneWidths = 1.05f;                             // x car width = right in my lane
        const float KeepGap = 3f, KeepGapPerMs = 0.8f;              // m + s x closing speed
        const float KeepMinClosing = 0.3f;                          // m/s

        // Dash.
        const float DashSafeSeconds = 2.5f;                         // s of travel to the braking point at the dashed speed
        const float FullEnergyMargin = 0.7f;                        // pips below max = "full" (would overflow)
        const float SideDashEdge = 0.6f;                            // x car width kept from the edge after a side dash

        // Thumb.
        const float IntentBlendRate = 2f;                           // 1/s
        const float ThumbDeadZone = 0.4f;                           // m nav error the thumb ignores
        const float ThumbFullStick = 1.5f;                          // m of nav error for a full stick
        const float LaneChangeStick = 0.4f;                         // ~4 m/s (the old 10 m/s swerve looked robotic)
        const float YieldBrake = -0.6f;                             // stick when braking / yielding
        const float KeepFingerHold = 0.4f, KeepFingerEnergy = 1.2f; // s held, pips left
        const float TrafficAhead = 35f, TrafficBehind = 15f;        // m
        const float RoomLengths = 1.5f, RoomGap = 0.6f;             // m past a car length = alongside; m left beside it

        public readonly string Archetype;
        public readonly float Skill;
        public string Intent { get; private set; } = "Line";

        readonly float _reaction, _thinkInterval, _thumbSpeed, _wanderAmp, _wanderFreq, _lineBias;
        readonly float _aggression, _draftLove, _boostLove, _dashLove, _mistakeChance, _startReaction, _releaseLead;

        readonly RaceManager _race;
        readonly RaceCar _me;
        readonly System.Random _rnd;
        readonly float _seed;

        // Intent, decided in intervals.
        float _nextThink, _intentUntil, _intentScore, _intentLateral, _intentBlend, _defendReadyAt;
        int _intentSide, _passTarget = -1;
        bool _intentActive, _wantBoost, _wantBrake;
        int _brakeZone = -1;
        float _zoneLeadFactor = 1f;

        // Scheduled dash (wind-up, then swipe).
        Vector2 _dashDir;
        int _dashLap = -1, _dashesThisLap;
        readonly float _dashStartDelay;
        float _dashAt = -1f, _nextDashAllowed;

        CarState[] _view, _thinkView;

        // Thumb.
        float _side, _bb;

        public AIDriver(RaceManager race, RaceCar me, float skill, int index, int seed)
        {
            _race = race;
            _me = me;
            _rnd = new System.Random(seed);
            _seed = (float)_rnd.NextDouble() * 100f;
            Skill = Mathf.Clamp01(skill);
            float s = Skill;

            float aggression = 0.5f, draft = 0.5f, boost = 0.6f, dash = 0.5f, mistakes = 1f;
            switch (index % 5)
            {
                case 0: Archetype = "Racer"; break;
                case 1: Archetype = "Bully"; aggression = 0.9f; dash = 0.9f; break;
                case 2: Archetype = "Speedster"; boost = 0.95f; draft = 0.3f; mistakes = 1.3f; break;
                case 3: Archetype = "Tactician"; draft = 0.9f; dash = 0.35f; aggression = 0.6f; mistakes = 0.7f; break;
                default: Archetype = "Rookie"; aggression = 0.3f; dash = 0.3f; mistakes = 1.6f; break;
            }

            // Skill 0..1 maps each trait between a slow / sloppy and a fast / precise human, then ±12 % personal spread.
            _reaction = Spread(Mathf.Lerp(0.52f, 0.22f, s));            // s
            _thinkInterval = Spread(Mathf.Lerp(0.35f, 0.18f, s));       // s between decisions
            _thumbSpeed = Spread(Mathf.Lerp(3f, 8f, s));                // stick units per s
            _wanderAmp = Spread(Mathf.Lerp(1.2f, 0.5f, s));             // m
            _wanderFreq = Spread(1f / Mathf.Lerp(4f, 8f, (float)_rnd.NextDouble())); // 1/s
            _lineBias = ((float)_rnd.NextDouble() * 2f - 1f) * 0.3f * CarController.Size.x; // m, own preferred line
            _aggression = Mathf.Clamp01(Spread(aggression));
            _draftLove = Mathf.Clamp01(Spread(draft));
            _boostLove = Mathf.Clamp01(Spread(boost));
            _dashLove = Mathf.Clamp01(Spread(dash));
            _mistakeChance = Mathf.Clamp01(Spread(Mathf.Lerp(0.25f, 0.04f, s) * mistakes)); // per braking zone
            _startReaction = Spread(Mathf.Lerp(0.5f, 0.12f, s));        // s
            _releaseLead = Spread(Mathf.Lerp(0.8f, 0.25f, s));          // s of safety margin when releasing boost before a corner
            _dashStartDelay = Range(3f, 6f);                            // s, no dashes at the start
        }

        /// <summary>Lateral distance to pass a car: its width + 0.8 m (racing_unity overtake line).</summary>
        static float PassOffset => CarController.Size.x + PassExtra;

        float Spread(float v, float amount = 0.12f) => v * (1f + ((float)_rnd.NextDouble() * 2f - 1f) * amount);
        float Range(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

        float Gaussian()
        {
            double u1 = 1.0 - _rnd.NextDouble(), u2 = _rnd.NextDouble();
            return (float)(System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2));
        }

        /// <summary>Reaction time of one look: log-normal around the bot's own (σ 0.3), 0.1..1.2 s.</summary>
        float SampleReaction() => Mathf.Clamp(_reaction * Mathf.Exp(Gaussian() * 0.3f), 0.1f, 1.2f);

        public string Describe() =>
            $"{Archetype} skill {Skill:0.00} react {_reaction:0.00}s aggr {_aggression:0.00} boost {_boostLove:0.00} dash {_dashLove:0.00}";

        // -------------------------------------------------------------------------------------------------------------
        // Decisions (intervals)
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>What one look at the race shows (delayed by the reaction time).</summary>
        struct Traffic
        {
            public CarState[] others;
            public int ahead, behind, beside;       // indices into others, -1 = none
            public float aheadGap, behindGap;       // m to the car ahead in my lane / any car behind
            public float nextGap;                   // m to the nearest car ahead in any lane
            public float v, limit, brakeDist;       // my speed, lateral limit, m to the next braking point
            public int idx, inside;                 // my track index, inside of the next corner (-1 left, 1 right, 0 none)
            public bool onStraight;
        }

        /// <summary>Race situation flags used by boost and dash.</summary>
        struct Phase
        {
            public bool chasing, passing, lastLap, finalSprint, checkpointFight, chase, heldBack;
            public float passGap;
        }

        void Think(float now)
        {
            var traffic = Look();
            ChoosePosition(traffic, now);
            RollBrakeZone(traffic);
            var phase = ReadPhase(traffic);
            DecideBoost(traffic, ref phase);
            DecideBrake(traffic, phase);
            DecideDash(traffic, phase, now);
        }

        /// <summary>Perception: the delayed snapshot, the cars ahead / behind / beside, the road ahead.</summary>
        Traffic Look()
        {
            var track = _race.Track;
            var cars = _race.Cars;
            var t = new Traffic
            {
                limit = track.HalfWidth - (CarController.Size.x * 0.5f + EdgeMargin),
                v = _me.Car.ForwardSpeed,
                idx = _me.TrackIndex,
                others = _race.DelayedStates(SampleReaction(), _thinkView ??= new CarState[_race.Cars.Count]),
                ahead = -1, behind = -1, beside = -1,
                nextGap = float.MaxValue, aheadGap = float.MaxValue, behindGap = float.MaxValue,
            };
            for (int k = 0; k < cars.Count; k++)
            {
                if (cars[k] == _me) continue;
                float gap = t.others[k].progress - _me.Progress;
                float dLat = t.others[k].lateral - _me.Lateral;
                if (gap > 0f && gap < t.nextGap) t.nextGap = gap;
                if (gap > 0f && gap < AheadRange && Mathf.Abs(dLat) < CarController.Size.x * SameLaneWidths && gap < t.aheadGap) { t.ahead = k; t.aheadGap = gap; }
                if (gap < 0f && gap > -BehindRange && -gap < t.behindGap) { t.behind = k; t.behindGap = -gap; }
                if (Mathf.Abs(gap) < CarController.Size.z * BesideLengths && Mathf.Abs(dLat) > CarController.Size.x * BesideMinWidths && Mathf.Abs(dLat) < CarController.Size.x * BesideMaxWidths) t.beside = k;
            }
            t.brakeDist = track.DistanceToBraking(t.idx, t.v);
            t.onStraight = t.brakeDist > StraightBrakeDistance;
            t.inside = track.TurnAhead(t.idx, CornerLookFrom, CornerLookTo);
            return t;
        }

        /// <summary>
        /// 1) Position: keep an overtake in progress, or pick overtake side / slipstream / one-move defence / free line.
        /// Sides are scored: close to the racing line, inside of the next corner, lane free past the target.
        /// </summary>
        void ChoosePosition(in Traffic t, float now)
        {
            var track = _race.Track;
            var cars = _race.Cars;
            var others = t.others;
            string intent = "Line";
            // An overtake in progress keeps its target until I am clearly past it (or it pulls away) - otherwise
            // the bot would lose it the moment it leaves its lane and fall back in behind (the "never completes" bug).
            if (_passTarget >= 0)
            {
                var target = others[_passTarget];
                float gapT = target.progress - _me.Progress;
                float lat = target.lateral + _intentSide * PassOffset;
                if (gapT < -(CarController.Size.z + PassDoneBehind) || gapT > PassGiveUp || cars[_passTarget].Finished || Mathf.Abs(lat) > t.limit) _passTarget = -1;
                else { intent = "Overtake"; Intent = intent; _intentActive = true; _intentLateral = lat; _intentUntil = now + PassingKeep; }
            }
            if (_passTarget < 0 && t.ahead >= 0)
            {
                var s = others[t.ahead];
                float closing = t.v - s.speed;
                if (t.aheadGap > DraftMinGap && t.onStraight && _draftLove > DraftMinLove)
                {
                    intent = "Draft";
                    Commit(intent, 0, s.lateral, DraftScore, now, DraftHold);
                }
                else if (t.aheadGap < OvertakeMaxGap && closing > OvertakeMinClosing)
                {
                    int bestSide = 0;
                    float bestScore = float.MinValue, bestLat = 0f;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float cand = s.lateral + side * PassOffset;
                        if (Mathf.Abs(cand) > t.limit) continue;
                        float score = 1f - LineWeight * Mathf.Abs(cand - track.LineOffset[track.Wrap(t.idx + LineLookSteps)]) / track.HalfWidth;
                        if (side == t.inside) score += InsideBonus + InsideAggression * _aggression;
                        if (LaneBlocked(others, cars, cand, s.progress + BlockedLook)) score -= BlockedPenalty;
                        if (score > bestScore) { bestScore = score; bestSide = side; bestLat = cand; }
                    }
                    if (bestSide != 0)
                    {
                        intent = "Overtake";
                        bool keep = Intent == "Overtake" && _intentActive && now < _intentUntil && bestSide != _intentSide && bestScore < _intentScore * SwitchMargin;
                        if (keep) _intentLateral = s.lateral + _intentSide * PassOffset;
                        else Commit(intent, bestSide, bestLat, bestScore, now, Range(OvertakeHoldMin, OvertakeHoldMax));
                        _passTarget = t.ahead;
                    }
                    else intent = "Follow";
                }
            }
            else if (_passTarget < 0 && t.behind >= 0 && t.behindGap < DefendRange && others[t.behind].speed > t.v + DefendFaster
                     && _aggression > DefendMinAggression && now > _defendReadyAt && t.inside != 0)
            {
                intent = "Defend";
                Commit(intent, t.inside, t.inside * t.limit * DefendLateral, 1f, now, DefendHold);
                _defendReadyAt = now + DefendCooldown; // one move, then race - no weaving
            }
            if (intent == "Line" || intent == "Follow")
            {
                if (_intentActive && now >= _intentUntil) _intentActive = false;
                if (!_intentActive) Intent = intent;
            }
        }

        /// <summary>
        /// One roll per braking zone decides whether this time the boost release comes too late (the car arrives hot and
        /// slides wide) - the human mistake, at the bot's own rate.
        /// </summary>
        void RollBrakeZone(in Traffic t)
        {
            if (t.brakeDist >= BrakeZoneLook) return;
            var track = _race.Track;
            int zone = track.Wrap(t.idx + Mathf.RoundToInt(t.brakeDist / track.Step));
            if (Mathf.Abs(zone - _brakeZone) <= BrakeZoneSame) return;
            _brakeZone = zone;
            _zoneLeadFactor = _rnd.NextDouble() < _mistakeChance ? -Range(LateReleaseMin, LateReleaseMax) / _releaseLead : Range(1f - ReleaseJitter, 1f + ReleaseJitter);
        }

        /// <summary>Race phases (doc: start, checkpoint pressure, last lap / final sprint from 85 %), and the chase.</summary>
        Phase ReadPhase(in Traffic t)
        {
            var track = _race.Track;
            var p = new Phase
            {
                chasing = t.ahead >= 0 && t.aheadGap < 20f || Intent == "Overtake",
                passing = _passTarget >= 0 && Mathf.Abs(t.others[_passTarget].lateral - _me.Lateral) > CarController.Size.x, // beside it, lane clear
                passGap = _passTarget >= 0 ? t.others[_passTarget].progress - _me.Progress : 0f,
                lastLap = _me.LapsDone >= _race.Laps - 1,
                finalSprint = _me.Progress >= FinalSprintShare * _race.Laps * track.Length,
            };
            float toCheckpoint = track.DistanceToNextCheckpoint(_me.Distance);
            p.checkpointFight = toCheckpoint < CheckpointFightRange && ((t.ahead >= 0 && t.aheadGap < CheckpointAhead) || (t.behind >= 0 && t.behindGap < CheckpointBehind));
            // Catch up whenever the field gets away (the race is about staying in the fight): someone far ahead, or the player.
            float playerLag = _race.Player != null && _race.Player != _me ? _race.Player.Progress - _me.Progress : 0f;
            p.chase = (t.nextGap > ChaseGap && t.nextGap < float.MaxValue) || playerLag > ChaseGap;
            if (p.chase && _passTarget < 0 && !_intentActive) Intent = "Chase";
            return p;
        }

        /// <summary>
        /// 2) Boost: a utility (character + race phase) held on straights and released ahead of the braking point.
        /// A bot the band holds back does not boost away from it (only to fight: pass, checkpoint, finish), and one the
        /// band already pulls up gains nothing from boost unless it is chasing (boost and band never stack - AutoPilot).
        /// </summary>
        void DecideBoost(in Traffic t, ref Phase p)
        {
            var car = _me.Car;
            var ap = _me.Pilot;
            float want = _boostLove + (p.chase ? 0.8f : 0f) + (p.chasing ? 0.3f : 0f) + (p.passing ? 0.6f : 0f) + (p.lastLap ? 0.3f : 0f)
                         + (p.checkpointFight ? 0.4f : 0f) + (p.finalSprint ? 1f : 0f);
            p.heldBack = car.Handicap < HeldBackBelow && !(p.passing || p.checkpointFight || p.finalSprint);
            bool pulledUp = ap.HandicapFactor > PulledUpAbove && !p.chase;
            _wantBoost = car.Surface.grip > BoostMinGrip && want > BoostWantThreshold && !ap.Locked && !p.heldBack && !pulledUp
                         && BoostIsSafe(_race.Track, t.v, _releaseLead * _zoneLeadFactor);
        }

        /// <summary>3) Brake: anything right ahead in my lane - keep ~3 m (+ 0.8 s of closing speed) instead of touching it.</summary>
        void DecideBrake(in Traffic t, in Phase p)
        {
            if (t.ahead >= 0 && !p.passing && Mathf.Abs(t.others[t.ahead].lateral - _me.Lateral) < CarController.Size.x * LaneWidths)
            {
                float closing = t.v - t.others[t.ahead].speed;
                _wantBrake = t.aheadGap < CarController.Size.z + KeepGap + Mathf.Max(0f, closing) * KeepGapPerMs && closing > KeepMinClosing;
            }
            else _wantBrake = false;
        }

        /// <summary>
        /// 4) Dash: purposeful, budgeted per lap, never in the first seconds of the start (same cost / cooldown as the
        /// player). In order: finish a pass, shove the car beside, ram the car ahead, brake-check a tailgater, or use
        /// energy that would overflow. A forward dash only where the faster car still makes the next corner.
        /// </summary>
        void DecideDash(in Traffic t, in Phase p, float now)
        {
            var track = _race.Track;
            var car = _me.Car;
            var ap = _me.Pilot;
            var c = _race.control;
            if (_me.LapsDone != _dashLap) { _dashLap = _me.LapsDone; _dashesThisLap = 0; }
            int budget = Mathf.RoundToInt(Mathf.Lerp(2f, 5f, _dashLove)) + (p.finalSprint ? 3 : 0) + (p.chase ? 3 : 0);
            if (_dashAt >= 0f || now < _nextDashAllowed || ap.DashReadyIn > 0f || _race.State != RaceState.Racing
                || _race.RaceTime <= _dashStartDelay || _dashesThisLap >= budget) return;

            float reserve = p.finalSprint || p.chase ? 0.5f : Mathf.Lerp(3f, 1f, _aggression); // pips kept for later
            bool energyFull = car.Energy >= car.EnergyMax - FullEnergyMargin;
            bool dashSafe = track.DistanceToBraking(t.idx, t.v + c.dashForwardSpeed) > (t.v + c.dashForwardSpeed) * DashSafeSeconds;
            Vector2 dir = Vector2.zero;
            if (p.passing && p.passGap > -1f && p.passGap < 8f && dashSafe && _rnd.NextDouble() < 0.4f + _dashLove * 0.5f)
                dir = Vector2.up; // finish the pass with a burst
            else if (t.beside >= 0 && _rnd.NextDouble() < _dashLove * 0.6f)
            {
                float dLat = t.others[t.beside].lateral - _me.Lateral;
                if (Mathf.Abs(_me.Lateral + Mathf.Sign(dLat) * CarController.Size.x * c.dashSideDistance) < track.HalfWidth - CarController.Size.x * SideDashEdge)
                    dir = new Vector2(Mathf.Sign(dLat), 0f); // shove it sideways
            }
            else if (t.ahead >= 0 && t.aheadGap > 3f && t.aheadGap < 9f && dashSafe && _rnd.NextDouble() < _dashLove * (p.checkpointFight || p.finalSprint ? 0.9f : 0.5f))
                dir = Vector2.up; // ram from behind / burst past
            else if (t.behind >= 0 && t.behindGap < 5f && _aggression > 0.7f && _rnd.NextDouble() < _dashLove * 0.25f)
                dir = Vector2.down; // brake-check the car glued to my bumper
            else if ((energyFull || p.finalSprint || p.chase) && !p.heldBack && t.onStraight && dashSafe && _rnd.NextDouble() < (p.chase ? 0.8f : 0.5f))
                dir = Vector2.up; // speed: energy would overflow, or the finish is near

            if (dir == Vector2.zero || car.Energy < c.dashCost + (energyFull ? 0f : reserve)) return;
            if (_rnd.NextDouble() < Mathf.Lerp(0.3f, 0.05f, Skill)) dir = Quaternion.Euler(0f, 0f, Range(-40f, 40f)) * dir; // sloppy swipe
            _dashDir = dir;
            _dashAt = now + Range(0.1f, 0.3f); // s of wind-up, like a thumb getting ready
            _nextDashAllowed = now + Range(2.5f, 6f);
            _dashesThisLap++;
        }

        /// <summary>
        /// Can I keep holding boost? Release when the distance to any slower point ahead gets close to what it takes
        /// to come back down to the speed that point allows (s = (v² - v_allowed²) / 2a + reaction distance) plus my safety lead.
        /// </summary>
        public bool BoostIsSafe(Track track, float v, float leadSeconds)
        {
            var c = _race.control;
            const float g = 9.81f;
            float boosted = Mathf.Max(v, Mathf.Min(v + 5f, c.cruiseSpeed * (1f + c.boostShare)));
            int n = Mathf.CeilToInt(250f / track.Step); // m looked ahead
            for (int k = 1; k <= n; k++)
            {
                int j = track.Wrap(_me.TrackIndex + k);
                float allowed = Mathf.Min(track.LineSpeed[j] * c.cornerMargin, c.cruiseSpeed);
                if (allowed >= boosted) continue;
                var surface = _race.surfaces[(int)track.Surface[j]];
                float decel = Mathf.Min(_me.Car.tuning.brakeDeceleration, _me.Car.tuning.grip * surface.grip * g) * 0.6f; // 60 % of what the tyres can
                float needed = (boosted * boosted - allowed * allowed) / (2f * decel) + boosted * (c.speedBlendTime + leadSeconds);
                if (k * track.Step < needed) return false;
            }
            return true;
        }

        void Commit(string intent, int side, float lateral, float score, float now, float hold)
        {
            Intent = intent;
            _intentActive = true;
            _intentSide = side;
            _intentLateral = lateral;
            _intentScore = score;
            _intentUntil = now + hold;
        }

        bool LaneBlocked(CarState[] others, System.Collections.Generic.List<RaceCar> cars, float lateral, float untilProgress)
        {
            for (int k = 0; k < cars.Count; k++)
            {
                if (cars[k] == _me) continue;
                float gap = others[k].progress - _me.Progress;
                if (gap > -3f && others[k].progress < untilProgress && Mathf.Abs(others[k].lateral - lateral) < CarController.Size.x * 1.15f) return true;
            }
            return false;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Thumb (every physics step)
        // -------------------------------------------------------------------------------------------------------------

        public DriverInput GetInput(float dt)
        {
            var input = new DriverInput();
            var track = _race.Track;
            var ap = _me.Pilot;
            float now = _race.SimTime;
            if (_race.RaceTime < _startReaction || _race.State != RaceState.Racing) return input;
            _view = _race.DelayedStates(_reaction, _view ??= new CarState[_race.Cars.Count]);

            if (now >= _nextThink)
            {
                Think(now);
                _nextThink = now + _thinkInterval * Range(0.8f, 1.2f);
            }

            // Where I want to be across the road: own line + slow wander, blended toward the current intent.
            float limit = track.HalfWidth - (CarController.Size.x * 0.5f + EdgeMargin);
            float line = track.LineOffset[_me.TrackIndex];
            float wander = (Mathf.PerlinNoise(_seed, now * _wanderFreq) - 0.5f) * 2f * _wanderAmp;
            _intentBlend = Mathf.MoveTowards(_intentBlend, _intentActive ? 1f : 0f, dt * IntentBlendRate);
            float desired = Mathf.Lerp(line + _lineBias + wander, _intentLateral, _intentBlend);
            desired = Mathf.Clamp(LeaveRoom(Mathf.Clamp(desired, -limit, limit)), -limit, limit);

            // Short taps toward it (a dead zone, so the thumb is mostly off the screen); lane changes slow, faster only to dodge.
            float diff = ap.NavFor(desired) - ap.NavOffset;
            float maxSide = _wantBrake ? 1f : LaneChangeStick;
            float side = Mathf.Abs(diff) < ThumbDeadZone ? 0f : Mathf.Clamp(diff / ThumbFullStick, -maxSide, maxSide);
            float bb = _wantBoost ? 1f : _wantBrake || Squeezed() ? YieldBrake : 0f;
            _side = Mathf.MoveTowards(_side, side, _thumbSpeed * dt);
            _bb = Mathf.MoveTowards(_bb, bb, _thumbSpeed * dt);
            if (Mathf.Abs(_side) < 0.02f) _side = 0f;
            if (Mathf.Abs(_bb) < 0.02f) _bb = 0f;

            input.side = _side;
            input.boostBrake = _bb;
            // Joystick rule: releasing a hold longer than 0.8 s locks it for 3 s. In traffic a good player keeps the finger
            // down with the stick centred (costs a little energy) and lifts only when nobody is around.
            bool keepFinger = ap.HoldTime > KeepFingerHold && _me.Car.Energy > KeepFingerEnergy && (InTraffic() || _wantBoost || _intentActive);
            input.holding = _side != 0f || _bb != 0f || keepFinger;

            if (_dashAt >= 0f && now >= _dashAt)
            {
                input.dash = _dashDir;
                _dashAt = -1f;
            }
            return input;
        }

        /// <summary>Side by side and touching, with the other car's nose ahead of mine: I am the one who yields and tucks in.</summary>
        bool Squeezed()
        {
            var cars = _race.Cars;
            for (int k = 0; k < cars.Count; k++)
            {
                if (cars[k] == _me) continue;
                float gap = _view[k].progress - _me.Progress;
                if (gap > 0f && gap < CarController.Size.z * 1.1f && Mathf.Abs(_view[k].lateral - _me.Lateral) < CarController.Size.x * 1.25f) return true;
            }
            return false;
        }

        /// <summary>Another car within 35 m ahead or 15 m behind (as I saw it).</summary>
        bool InTraffic()
        {
            var cars = _race.Cars;
            for (int k = 0; k < cars.Count; k++)
            {
                if (cars[k] == _me) continue;
                float gap = _view[k].progress - _me.Progress;
                if (gap > -TrafficBehind && gap < TrafficAhead) return true;
            }
            return false;
        }

        /// <summary>A car alongside keeps its lane: never squeeze it, leave a car's width (racing etiquette).</summary>
        float LeaveRoom(float desired)
        {
            var others = _view;
            var cars = _race.Cars;
            for (int k = 0; k < cars.Count; k++)
            {
                if (cars[k] == _me) continue;
                float gap = others[k].progress - _me.Progress;
                float dLat = others[k].lateral - _me.Lateral;
                if (Mathf.Abs(gap) > CarController.Size.z + RoomLengths || Mathf.Abs(dLat) > CarController.Size.x * BesideMaxWidths) continue;
                desired = dLat > 0f ? Mathf.Min(desired, others[k].lateral - (CarController.Size.x + RoomGap)) : Mathf.Max(desired, others[k].lateral + (CarController.Size.x + RoomGap));
            }
            return desired;
        }
    }
}
