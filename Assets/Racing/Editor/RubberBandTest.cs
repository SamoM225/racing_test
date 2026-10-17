using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Racing.EditorTools
{
    /// <summary>
    /// Does the field stay together? The real game is about damage points, so the cars must stay in contact.
    /// Three players in the player's seat: normal, fast (boost + dash wherever safe - the "boost all race + dash" exploit from
    /// racing_unity's bot tests) and a laggard (always braking). Metrics as in racing_unity Docs/BotTesting: field spread,
    /// player has a rival within 30 m, player alone (> 100 m). Judged on means over maps and seeds: median spread <= 50 m,
    /// rival within 30 m >= 90 %, alone <= 3 %, player > 30 m ahead of everybody <= 3 %; the laggard (cannot be kept within 30 m) spread <= 80 m, alone <= 10 %.
    /// </summary>
    public static class RubberBandTest
    {
        /// <summary>A human pattern on top of the player's driving: holds boost or brake all the time, optionally dashes whenever it can.</summary>
        class ScriptedPlayer : IDriver
        {
            readonly IDriver _inner;
            readonly RaceCar _me;
            readonly RaceManager _race;
            readonly float _boostBrake;
            readonly bool _dash;

            public ScriptedPlayer(RaceManager race, IDriver inner, RaceCar me, float boostBrake, bool dash)
            {
                _race = race; _inner = inner; _me = me; _boostBrake = boostBrake; _dash = dash;
            }

            public DriverInput GetInput(float dt)
            {
                var input = _inner.GetInput(dt); // keeps the lane steering sane
                if (_me.Pilot.Locked) { input.holding = false; input.boostBrake = 0f; return input; } // lift the finger, wait out the lock
                input.holding = true;
                // A fast human boosts wherever it is safe (into a corner it would crash and stop running away).
                var track = _race.Track;
                float v = _me.Car.ForwardSpeed;
                bool safe = _me.AI.BoostIsSafe(track, v, 0.3f);
                input.boostBrake = _boostBrake > 0f && !safe ? 0f : _boostBrake;
                float vd = v + _race.control.dashForwardSpeed; // dash only where the faster car still makes the next corner
                if (_dash && _me.Car.Energy >= 1.5f && _me.Pilot.DashReadyIn <= 0f && track.DistanceToBraking(_me.TrackIndex, vd) > vd * 2.5f) input.dash = Vector2.up;
                return input;
            }
        }

        /// <summary>No thumb at all (the game drives the line), one swipe when told to.</summary>
        class Swipe : IDriver
        {
            public Vector2 Next;
            public DriverInput GetInput(float dt)
            {
                var input = new DriverInput { dash = Next };
                Next = Vector2.zero;
                return input;
            }
        }

        static void Teleport(RaceManager rm, RaceCar rc, int idx, float lateral, Vector3 velocity, float yaw = 0f, float roll = 0f)
        {
            var track = rm.Track;
            var t = rc.Car.transform;
            t.position = track.Center[idx] + track.Right[idx] * lateral + Vector3.up * (roll != 0f ? 1.2f : 0.45f);
            t.rotation = Quaternion.LookRotation(track.Tangent[idx]) * Quaternion.Euler(0f, yaw, roll);
            rc.Car.Body.position = t.position;
            rc.Car.Body.rotation = t.rotation;
            rc.Car.Body.linearVelocity = velocity;
            rc.Car.Body.angularVelocity = Vector3.zero;
            rc.Pilot.Reset(lateral - track.LineOffset[idx]);
        }

        /// <summary>
        /// Side dash is a lunge that covers its distance (not a teleport, not a steer, no spin), and a dash ram knocks the hit
        /// car: it breaks loose, gets shoved away and turns.
        /// </summary>
        static void CheckDashPhysics(RaceManager rm, StringBuilder failures, StringBuilder report)
        {
            const float dt = 0.02f;
            rm.mapName = "NewYork_Downtown";
            rm.Initialize();
            var track = rm.Track;
            rm.opponents = 1;
            rm.StartRace(playerIsBot: true, skipCountdown: true);
            var me = rm.Player;
            var bot = rm.Cars.Find(c => c != me);
            var swipe = new Swipe();
            me.Driver = swipe;
            bot.Driver = new Swipe();
            for (int s = 0; s < 300; s++) { rm.Tick(dt); Physics.Simulate(dt); } // up to speed
            // A straight 100 m ahead, both cars side by side there at speed.
            int idx = me.TrackIndex;
            for (int n = 0; n < track.Center.Length; n++, idx = track.Wrap(idx + 1))
            {
                bool straight = true;
                for (int j = 0; j < Mathf.RoundToInt(80f / track.Step) && straight; j++) straight = Mathf.Abs(track.Curvature[track.Wrap(idx + j)]) < 0.004f;
                if (straight) break;
            }
            int straightIdx = idx;
            Vector3 vel = track.Tangent[idx] * 25f;
            float lane = -CarController.Size.x * 1.3f;
            Teleport(rm, me, idx, lane, vel);
            Teleport(rm, bot, idx, lane + CarController.Size.x * 1.3f * 2f + 0.5f, vel); // a car's width + 1.4 m free beside me
            for (int s = 0; s < 25; s++) { rm.Tick(dt); Physics.Simulate(dt); }

            // 1) Side dash away from the bot: distance, heading, speed.
            float lat0 = me.Lateral, v0 = me.Car.Speed, maxYaw = 0f;
            swipe.Next = Vector2.left;
            for (int s = 0; s < 35; s++)
            {
                rm.Tick(dt); Physics.Simulate(dt);
                maxYaw = Mathf.Max(maxYaw, Vector3.Angle(Vector3.ProjectOnPlane(me.Car.transform.forward, Vector3.up), track.Tangent[me.TrackIndex]));
            }
            float moved = lat0 - me.Lateral, want = CarController.Size.x * rm.control.dashSideDistance;
            report.AppendLine($"  DASH side: moved {moved:0.00} m (want {want:0.00}) max heading {maxYaw:0} deg speed {v0:0} -> {me.Car.Speed:0}");
            if (moved < want * 0.7f || moved > want * 1.4f || maxYaw > 20f || me.Car.Speed < v0 * 0.75f)
                failures.AppendLine($"side dash: moved {moved:0.00} m of {want:0.00}, heading {maxYaw:0} deg, speed {v0:0} -> {me.Car.Speed:0}");

            // 2) Side dash into the bot beside me: a ram (after the dash cooldown).
            for (int s = 0; s < 60; s++) { rm.Tick(dt); Physics.Simulate(dt); }
            idx = me.TrackIndex;
            vel = track.Tangent[idx] * 25f;
            Teleport(rm, me, idx, lane, vel);
            Teleport(rm, bot, idx, lane + CarController.Size.x + 0.4f, vel);
            for (int s = 0; s < 5; s++) { rm.Tick(dt); Physics.Simulate(dt); }
            float botLat0 = bot.Lateral, maxStun = 0f, botYaw = 0f;
            int hits0 = me.DashHits;
            swipe.Next = Vector2.right;
            for (int s = 0; s < 50; s++)
            {
                rm.Tick(dt); Physics.Simulate(dt);
                maxStun = Mathf.Max(maxStun, bot.Car.Stun);
                botYaw = Mathf.Max(botYaw, Vector3.Angle(Vector3.ProjectOnPlane(bot.Car.transform.forward, Vector3.up), track.Tangent[bot.TrackIndex]));
            }
            float shoved = bot.Lateral - botLat0;
            report.AppendLine($"  DASH ram: hits {me.DashHits - hits0} victim shoved {shoved:0.00} m, stun {maxStun:0.00} s, turned {botYaw:0} deg, upright {bot.Car.transform.up.y:0.00}");
            if (me.DashHits - hits0 != 1 || shoved < 1f || maxStun < 0.3f || botYaw < 5f || bot.Car.transform.up.y < 0.8f)
                failures.AppendLine($"dash ram: hits {me.DashHits - hits0}, shoved {shoved:0.00} m, stun {maxStun:0.00}, turned {botYaw:0} deg, upY {bot.Car.transform.up.y:0.00}");
            report.AppendLine($"  DAMAGE ram: victim HP {bot.Car.HP:0} (level {bot.Car.DamageLevel}), rammer HP {me.Car.HP:0}, dealt {me.DamageDealt:0}");
            if (bot.Car.HP > 95f || me.Car.HP < 95f || me.DamageDealt < 5f)
                failures.AppendLine($"damage: victim HP {bot.Car.HP:0}, rammer HP {me.Car.HP:0}, dealt {me.DamageDealt:0}");

            // 3) Recover: standing facing backwards, and nose against the wall - a three-point turn, no respawn.
            bot.Driver = new Swipe();
            Teleport(rm, bot, track.Wrap(me.TrackIndex + Mathf.RoundToInt(300f / track.Step)), 0f, Vector3.zero); // out of the way
            foreach (var (name, lat, yaw) in new[] { ("backwards", 0f, 180f), ("nose in wall", track.HalfWidth + track.Runoff - 1.6f, 85f) })
            {
                for (int s = 0; s < 10; s++) { rm.Tick(dt); Physics.Simulate(dt); }
                Teleport(rm, me, me.TrackIndex, lat, Vector3.zero, yaw);
                int respawns = me.Respawns;
                float p0 = me.Progress, facing = -1f, heading = 0f;
                for (int s = 0; s < 450; s++)
                {
                    rm.Tick(dt); Physics.Simulate(dt);
                    heading = Vector3.Angle(Vector3.ProjectOnPlane(me.Car.transform.forward, Vector3.up), track.Tangent[me.TrackIndex]);
                    if (facing < 0f && heading < 30f) facing = s * dt;
                }
                report.AppendLine($"  RECOVER {name}: facing the road after {facing:0.0} s, heading {heading:0} deg after 9 s, progress +{me.Progress - p0:0} m, respawns {me.Respawns - respawns}, turns {me.Pilot.Recoveries}");
                if (heading > 30f || me.Progress - p0 < 20f || me.Respawns != respawns)
                    failures.AppendLine($"recover {name}: heading {heading:0} deg, progress +{me.Progress - p0:0} m, respawns {me.Respawns - respawns}");
            }

            // 4) On the roof, standing: it rolls back onto its wheels by itself.
            {
                for (int s = 0; s < 10; s++) { rm.Tick(dt); Physics.Simulate(dt); }
                Teleport(rm, me, me.TrackIndex, 0f, Vector3.zero, 0f, 180f);
                int respawns = me.Respawns;
                float upright = -1f;
                for (int s = 0; s < 150 && upright < 0f; s++) { rm.Tick(dt); Physics.Simulate(dt); if (me.Car.transform.up.y > 0.9f && me.Car.Grounded) upright = s * dt; }
                report.AppendLine($"  RECOVER roof: upright after {upright:0.0} s, respawns {me.Respawns - respawns}");
                if (upright < 0f || me.Respawns != respawns) failures.AppendLine($"recover roof: upright {upright:0.0} s, respawns {me.Respawns - respawns}");
            }

            // 5) Weight transfer: hard lock at speed with the throttle pins the load on the rear - the front washes out
            //    (understeer); with the brakes the rear goes light and steps out (oversteer). Raw car, no pilot.
            foreach (var (name, throttle) in new[] { ("throttle", 1f), ("brakes", -0.5f) })
            {
                Teleport(rm, me, straightIdx, 0f, track.Tangent[straightIdx] * 24f);
                for (int s = 0; s < 10; s++) { me.Car.Simulate(0f, 0.3f, false, dt); Physics.Simulate(dt); }
                int over = 0, under = 0;
                for (int s = 0; s < 40; s++)
                {
                    me.Car.Simulate(0.35f, throttle, false, dt);
                    Physics.Simulate(dt);
                    if (me.Car.Oversteer) over++;
                    if (me.Car.Understeer) under++;
                    if (Trace && s % 4 == 0) report.AppendLine($"    {name} s{s} v{me.Car.Speed:0.0} front {me.Car.FrontSlip:0.0} rear {me.Car.RearSlip:0.0} body {me.Car.SlipAngle:0} yawR {me.Car.YawRate:0.00} drift {me.Car.IsDrifting}");
                }
                report.AppendLine($"  STEER {name}: oversteer {over * dt:0.00} s, understeer {under * dt:0.00} s");
                if (throttle > 0f ? under <= over : over <= under) failures.AppendLine($"steer balance with {name}: oversteer {over * dt:0.00} s, understeer {under * dt:0.00} s");
            }
            rm.opponents = 3;
        }

        [MenuItem("Racing/Run Rubber Band Test")]
        public static void Run()
        {
            var failures = new StringBuilder();
            var report = new StringBuilder();
            var oldMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            var go = new GameObject("RubberBandTest");
            try
            {
                var rm = go.AddComponent<RaceManager>();
                CheckDashPhysics(rm, failures, report);
                rm.opponents = 3;
                rm.difficulty = Difficulty.Normal;
                string only = System.Environment.GetEnvironmentVariable("RACING_MAPS");
                string[] maps = string.IsNullOrEmpty(only) ? new[] { "NewYork_Downtown", "Asia_Formula", "Dakar_Desert" } : only.Split(',');
                var scenarios = new (string name, float bb, bool dash)[] { ("normal", 0f, false), ("fast", 1f, true), ("laggard", -0.7f, false) };
                const int seeds = 5; // one race is noise (a single crash moves the spread by tens of metres): judge on means
                var sum = new Dictionary<string, (float spread, float rival, float alone, float lead, float escape, int incidents, int n)>();
                var spreads = new Dictionary<string, List<float>>();
                foreach (var map in maps)
                {
                    rm.mapName = map;
                    rm.Initialize();
                    foreach (var sc in scenarios)
                            for (int s = 0; s < seeds; s++)
                            {
                                rm.seed = map.GetHashCode() + 7919 * s;
                                if (Trace) TraceLog.AppendLine($"--- {map} {sc.name} seed {s}");
                                var r = RunOne(rm, sc.name == "normal" ? null : (rc => new ScriptedPlayer(rm, rc.AI, rc, sc.bb, sc.dash)));
                                report.AppendLine($"  {map,-18} {sc.name,-8} s{s} | spread mean {r.spreadMean,5:0} m p90 {r.spreadP90,5:0} m | " +
                                                  $"rival<30m {r.rival30 * 100f,3:0}% alone>100m {r.alone * 100f,3:0}% escaped {r.escape * 100f,3:0}% | player lead {r.leadMean,5:0} m P{r.playerPos} | " +
                                                  $"bot boost {r.botBoost * 100f:0}% dashes {r.botDashes} incidents {r.stalls}");
                                string key = sc.name;
                                sum.TryGetValue(key, out var a);
                                sum[key] = (a.spread + r.spreadMean, a.rival + r.rival30, a.alone + r.alone, a.lead + r.leadMean, a.escape + r.escape, a.incidents + r.stalls, a.n + 1);
                                if (!spreads.ContainsKey(key)) spreads[key] = new List<float>();
                                spreads[key].Add(r.spreadMean);
                            }
                }
                report.AppendLine("  MEANS over maps and seeds:");
                foreach (var kv in sum)
                {
                    var a = kv.Value;
                    float spread = a.spread / a.n, rival = a.rival / a.n, alone = a.alone / a.n, escape = a.escape / a.n;
                    var list = spreads[kv.Key];
                    list.Sort();
                    float median = list[list.Count / 2]; // robust to the odd race wrecked by a crash
                    report.AppendLine($"  {kv.Key} | spread median {median,5:0} m mean {spread,5:0} m | rival<30m {rival * 100f,3:0}% | alone>100m {alone * 100f,3:0}% | player lead {a.lead / a.n,5:0} m | escaped>30m {escape * 100f,3:0}% | incidents/race {a.incidents / (float)a.n:0.0}");
                    // A player who brakes the whole race cannot be kept within 30 m (bots never stop) - it must not be left alone.
                    bool lag = kv.Key.StartsWith("laggard");
                    if ((lag ? spread > 80f : median > 50f) || (!lag && rival < 0.9f) || alone > (lag ? 0.10f : 0.03f) || escape > 0.03f)
                        failures.AppendLine($"{kv.Key.Trim()}: pack not kept (spread median {median:0} m mean {spread:0} m, rival<30m {rival * 100f:0}%, alone {alone * 100f:0}%, escaped {escape * 100f:0}%)");
                }
            }
            catch (System.Exception e)
            {
                failures.AppendLine(e.ToString());
            }
            finally
            {
                Object.DestroyImmediate(go);
                Physics.simulationMode = oldMode;
            }

            Debug.Log("RACING RUBBER BAND REPORT\n" + report + TraceLog);
            if (failures.Length > 0) Debug.LogError("RACING RUBBER BAND FAILED\n" + failures);
            else Debug.Log("RACING RUBBER BAND PASSED");
            if (Application.isBatchMode) EditorApplication.Exit(failures.Length > 0 ? 1 : 0);
        }

        static readonly bool Trace = System.Environment.GetEnvironmentVariable("RACING_TRACE") == "1";
        static readonly StringBuilder TraceLog = new StringBuilder();


        static string IncidentCause(RaceManager rm, RaceCar rc, float speedBefore)
        {
            var track = rm.Track;
            string contact = "";
            foreach (var o in rm.Cars)
            {
                if (o == rc) continue;
                float gap = o.Progress - rc.Progress, lat = Mathf.Abs(o.Lateral - rc.Lateral);
                if (Mathf.Abs(gap) < CarController.Size.z * 1.3f && lat < CarController.Size.x * 1.3f)
                    contact += $"{o.Name}(gap {gap:0.0} lat {lat:0.0}{(o.Car.DashTimer > 0f ? " DASHING" : "")}) ";
            }
            int i = rc.TrackIndex;
            return $"    INCIDENT t={rm.RaceTime:0} {rc.Name} @{rc.Distance:0}m surf {track.Surface[i]} lat {rc.Lateral:0.0}/{track.HalfWidth:0} " +
                   $"v1s {speedBefore:0} line {track.LineSpeed[i]:0} curvR {(Mathf.Abs(track.Curvature[i]) > 1e-4f ? 1f / Mathf.Abs(track.Curvature[i]) : 999f):0} " +
                   $"upY {rc.Car.transform.up.y:0.00} knock {rc.Car.SinceKnock:0.0}s ago yaw {Vector3.SignedAngle(track.Tangent[i], Vector3.ProjectOnPlane(rc.Car.transform.forward, Vector3.up), Vector3.up):0} own-dash {rc.Car.DashTimer > 0f} intent {rc.AI?.Intent} contact: {contact}";
        }
        struct Result { public float spreadMean, spreadP90, rival30, alone, leadMean, escape, botBoost; public int playerPos, botDashes, stalls; }

        static Result RunOne(RaceManager rm, System.Func<RaceCar, IDriver> playerDriver)
        {
            rm.StartRace(playerIsBot: true, skipCountdown: true);
            var player = rm.Player;
            if (playerDriver != null) player.Driver = playerDriver(player);
            const float dt = 0.02f;
            var spreads = new List<float>();
            int samples = 0, rival = 0, alone = 0, escape = 0, boostSteps = 0, botSteps = 0, stalls = 0;
            var stalled = new bool[rm.Cars.Count];
            var speedAgo = new float[rm.Cars.Count];
            var hist = new Queue<string>[rm.Cars.Count];
            var rolled = new bool[rm.Cars.Count];
            float leadSum = 0f;
            for (int step = 0; step < 60 * 50 * 6 && !rm.RaceOver; step++)
            {
                rm.Tick(dt);
                Physics.Simulate(dt);
                foreach (var rc in rm.Cars)
                    if (rc != player && !rc.Finished) { botSteps++; if (rc.Car.IsBoosting) boostSteps++; }
                for (int i = 0; i < rm.Cars.Count; i++) // incident: a car suddenly crawling although it wants to go fast (crash, spin)
                {
                    var rc = rm.Cars[i];
                    bool s = rm.RaceTime > 5f && !rc.Finished && rc.Car.Speed < 8f && rc.Pilot.TargetSpeed > 20f;
                    if (s && !stalled[i])
                    {
                        stalls++;
                        if (Trace) TraceLog.AppendLine(IncidentCause(rm, rc, speedAgo[i]));
                    }
                    stalled[i] = s;
                }
                if (step % 50 == 0) for (int i = 0; i < rm.Cars.Count; i++) speedAgo[i] = rm.Cars[i].Car.Speed; // ~1 s ago
                if (Trace) // the last 2 s of a car that is rolling over
                    for (int i = 0; i < rm.Cars.Count; i++)
                    {
                        var rc = rm.Cars[i];
                        var c = rc.Car;
                        if (step % 5 == 0)
                        {
                            hist[i] ??= new Queue<string>();
                            hist[i].Enqueue($"      {rc.Name} d{rc.Distance:0} lat {rc.Lateral:0.0} y{c.transform.position.y - rm.Track.Center[rc.TrackIndex].y:0.0} v{c.Speed:0}/{rc.Pilot.TargetSpeed:0} up {c.transform.up.y:0.00} slip {c.SlipAngle:0} yawR {c.YawRate:0.0} gnd {c.Grounded} boost {c.IsBoosting} dashT {c.DashTimer:0.0} sd {c.SideDashing} stun {c.Stun:0.0} bb {rc.Pilot.SpeedOrder:0.00}");
                            if (hist[i].Count > 20) hist[i].Dequeue();
                        }
                        bool rolling = c.transform.up.y < 0.5f;
                        if (rolling && !rolled[i] && hist[i] != null)
                        {
                            string near = "";
                            foreach (var col in Physics.OverlapSphere(c.transform.position, 3f)) if (col.attachedRigidbody != c.Body) near += col.name + " ";
                            TraceLog.AppendLine($"    ROLLOVER {rc.Name} t={rm.RaceTime:0} crossing {rm.Track.Crossing[rc.TrackIndex]} surf {rm.Track.Surface[rc.TrackIndex]} near: {near}");
                            foreach (var l in hist[i]) TraceLog.AppendLine(l);
                        }
                        rolled[i] = rolling;
                    }
                if (Trace && step % 250 == 0)
                {
                    var line = new StringBuilder($"    t={rm.RaceTime,5:0} ");
                    foreach (var rc in rm.Cars)
                        line.Append($"| {rc.Name}{(rc == player ? "*" : "")} p{rc.Progress,6:0} h{rc.Car.Handicap:0.00} f{rc.Pilot.HandicapFactor:0.00} v{rc.Car.Speed,4:0}/{rc.Pilot.TargetSpeed,4:0} {rc.AI?.Intent} ");
                    Debug.Log(line.ToString());
                    TraceLog.AppendLine(line.ToString());
                }
                if (step % 25 != 0 || rm.RaceTime < 15f || player.Finished) continue;

                float min = float.MaxValue, max = float.MinValue, nearest = float.MaxValue, best = float.MinValue;
                foreach (var rc in rm.Cars)
                {
                    if (rc.Finished) continue;
                    min = Mathf.Min(min, rc.Progress);
                    max = Mathf.Max(max, rc.Progress);
                    if (rc == player) continue;
                    nearest = Mathf.Min(nearest, Mathf.Abs(rc.Progress - player.Progress));
                    best = Mathf.Max(best, rc.Progress);
                }
                spreads.Add(max - min);
                samples++;
                if (nearest < 30f) rival++;
                if (nearest > 100f) alone++;
                if (best > float.MinValue) leadSum += player.Progress - best;
                if (best > float.MinValue && player.Progress - best > 30f) escape++; // ran away from everybody
            }
            spreads.Sort();
            float mean = 0f;
            foreach (float s in spreads) mean += s;
            int dashes = 0;
            foreach (var rc in rm.Cars) if (rc != player) dashes += rc.Dashes;
            return new Result
            {
                spreadMean = spreads.Count > 0 ? mean / spreads.Count : 0f,
                spreadP90 = spreads.Count > 0 ? spreads[Mathf.Min(spreads.Count - 1, (int)(spreads.Count * 0.9f))] : 0f,
                rival30 = samples > 0 ? rival / (float)samples : 0f,
                alone = samples > 0 ? alone / (float)samples : 0f,
                leadMean = samples > 0 ? leadSum / samples : 0f,
                escape = samples > 0 ? escape / (float)samples : 0f,
                playerPos = player.FinishPosition,
                botBoost = botSteps > 0 ? boostSteps / (float)botSteps : 0f,
                botDashes = dashes,
                stalls = stalls,
            };
        }
    }
}
