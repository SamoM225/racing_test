using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Racing.EditorTools
{
    public static class RacingEditorTools
    {
        const string ScenePath = "Assets/Scenes/Race.unity";

        /// <summary>Race scene: camera with the race camera, light, one "Race" object. Everything else is built at runtime.</summary>
        [MenuItem("Racing/Rebuild Race Scene")]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Camera.main.gameObject.AddComponent<ChaseCamera>();
            Camera.main.farClipPlane = 1500f;
            var race = new GameObject("Race");
            race.AddComponent<RaceManager>();
            race.AddComponent<RaceHud>();
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait; // mobile, like racing_unity
            PlayerSettings.productName = "RC Racing";
            Debug.Log("Racing: scene created at " + ScenePath);
        }

        /// <summary>
        /// Headless check of every racing_unity map: geometry of all layouts (both directions), then one full race of
        /// bots per map (one of them counts as "the player", so the game's mode-3 rubber band is active). Reports per map
        /// what matters for natural bots: everybody finishes, no respawns, overtakes, time off the road, lap spread.
        /// Fails (exit code 1 in batch mode) when a track is broken or bots cannot finish.
        /// Optional env var RACING_MAPS = comma list of map names to limit the run.
        /// </summary>
        [MenuItem("Racing/Run Headless Bot Race (all maps)")]
        public static void RunBotRace()
        {
            var failures = new StringBuilder();
            var report = new StringBuilder();
            var oldMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            var go = new GameObject("BotRaceTest");
            try
            {
                CheckGestures(failures);
                var rm = go.AddComponent<RaceManager>();
                rm.opponents = 3;
                rm.difficulty = Difficulty.Normal;
                string only = System.Environment.GetEnvironmentVariable("RACING_MAPS");
                foreach (var map in MapLibrary.Ordered())
                {
                    if (!string.IsNullOrEmpty(only) && !only.Contains(map.name.Replace("_T2", ""))) continue;
                    var probe = new GameObject("probe").AddComponent<Track>();
                    probe.Build(map, SurfaceDef.Defaults(), rm.carTuning, rm.control.cruiseSpeed, geometry: false);
                    CheckTrack(map, probe, failures, report);
                    Object.DestroyImmediate(probe.gameObject);
                    if (map.name.EndsWith("_T2")) continue; // race the game's default layouts
                    rm.mapName = map.name;
                    rm.seed = map.name.GetHashCode();
                    rm.Initialize();
                    RunOneRace(rm, failures, report);
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

            Debug.Log("RACING BOT RACE REPORT\n" + report);
            if (failures.Length > 0) Debug.LogError("RACING BOT RACE FAILED\n" + failures);
            else Debug.Log("RACING BOT RACE PASSED");
            if (Application.isBatchMode) EditorApplication.Exit(failures.Length > 0 ? 1 : 0);
        }

        static void RunOneRace(RaceManager rm, StringBuilder failures, StringBuilder report)
        {
            rm.StartRace(playerIsBot: true, skipCountdown: true);
            const float dt = 0.02f;
            int steps = 0, boostSteps = 0, driftSteps = 0;
            var offroad = new float[rm.Cars.Count];
            float followTime = 0f, contactTime = 0f, lockedTime = 0f;
            int attempts = 0;
            var lastIntent = new string[rm.Cars.Count];
            for (; steps < 60 * 50 * 6 && !rm.RaceOver; steps++)
            {
                rm.Tick(dt);
                Physics.Simulate(dt);
                for (int i = 0; i < rm.Cars.Count; i++)
                {
                    var rc = rm.Cars[i];
                    if (rc.Car.IsBoosting) boostSteps++;
                    if (rc.Car.IsDrifting) driftSteps++;
                    if (Mathf.Abs(rc.Lateral) > rm.Track.HalfWidth) offroad[i] += dt;
                    if (rc.AI != null && !rc.Finished)
                    {
                        if (rc.AI.Intent == "Overtake" && lastIntent[i] != "Overtake") attempts++;
                        lastIntent[i] = rc.AI.Intent;
                    }
                    if (rc.Finished || rm.RaceTime < 5f) continue;
                    if (rc.Pilot.Locked) lockedTime += dt;
                    foreach (var o in rm.Cars)
                    {
                        if (o == rc || o.Finished) continue;
                        float gap = o.Progress - rc.Progress, dLat = Mathf.Abs(o.Lateral - rc.Lateral);
                        if (gap > 0f && gap < 10f && dLat < CarController.Size.x) followTime += dt;            // stuck in its lane
                        if (gap > 0f && gap < CarController.Size.z * 1.05f && dLat < CarController.Size.x * 0.95f) contactTime += dt; // overlapping = touching
                    }
                }
            }

            string map = rm.Track.Map.name;
            float minBest = float.MaxValue, maxBest = 0f, off = 0f;
            int overtakes = 0, respawns = 0, dashes = 0;
            var order = new List<RaceCar>(rm.Cars);
            order.Sort((a, b) => a.FinishPosition.CompareTo(b.FinishPosition));
            for (int i = 0; i < rm.Cars.Count; i++)
            {
                var rc = rm.Cars[i];
                overtakes += rc.Overtakes; respawns += rc.Respawns; dashes += rc.Dashes; off += offroad[i];
                // The game ends the race when the player finishes; a bot close behind may legitimately not be home yet.
                if (!rc.Finished && rc.Progress < 0.9f * rm.Laps * rm.Track.Length) failures.AppendLine($"{map}: {rc.Name} did not finish (progress {rc.Progress:0} / {rm.Laps * rm.Track.Length:0}) at {rc.Car.transform.position} idx {rc.TrackIndex} lat {rc.Lateral:0.0} v {rc.Car.Speed:0.0} up {rc.Car.transform.up.y:0.00} surf {rm.Track.Surface[rc.TrackIndex]} crossing {rm.Track.Crossing[rc.TrackIndex]} stuck {rc.StuckTime:0.0}");
                if (rc.Respawns - rc.Wrecks > 2) failures.AppendLine($"{map}: {rc.Name} needed {rc.Respawns - rc.Wrecks} respawns: {rc.RespawnLog}"); // wrecks are the damage game, not getting lost
                else if (rc.Respawns > 0) report.AppendLine($"    respawns {rc.Name}: {rc.RespawnLog}");
                if (rc.BestLap < float.MaxValue) { minBest = Mathf.Min(minBest, rc.BestLap); maxBest = Mathf.Max(maxBest, rc.BestLap); }
            }
            var finish = new List<string>();
            foreach (var rc in order) finish.Add($"{rc.Name}{(rc.IsPlayer ? "*" : "")} {rc.FinishTime:0.0}");
            report.AppendLine($"  RACE {map,-22} {rm.Laps} laps {steps * dt,4:0}s | {string.Join(", ", finish)} | lap {minBest:0.0}-{maxBest:0.0}s " +
                              $"overt {overtakes}/{attempts} tries, follow {followTime:0}s contact {contactTime:0.0}s locked {lockedTime:0}s dash {dashes} resp {respawns} offroad {off:0}s drift {driftSteps * dt:0}s");
            if (maxBest - minBest < 0.05f) failures.AppendLine($"{map}: all bots drive identical laps (no spread)");
        }

        /// <summary>The racing_unity drag split and swipe rule.</summary>
        static void CheckGestures(StringBuilder failures)
        {
            var (s, b) = PlayerDriver.Decompose(new Vector2(100f, 0f), 400f);
            if (!Mathf.Approximately(s, 0.25f) || b != 0f) failures.AppendLine($"pure side drag: {s}, {b}");
            (s, b) = PlayerDriver.Decompose(new Vector2(-300f, 300f), 400f);
            if (!Mathf.Approximately(s, -0.5f) || !Mathf.Approximately(b, 0.5f)) failures.AppendLine($"diagonal drag: {s}, {b}");
            (s, b) = PlayerDriver.Decompose(new Vector2(0f, -900f), 400f);
            if (s != 0f || !Mathf.Approximately(b, -1f)) failures.AppendLine($"full brake drag: {s}, {b}");
            if (!PlayerDriver.IsSwipe(0.3f, new Vector2(0f, 500f), 400f, 0.8f)) failures.AppendLine("fast long flick is not a dash");
            if (PlayerDriver.IsSwipe(1.0f, new Vector2(0f, 500f), 400f, 0.8f)) failures.AppendLine("slow hold became a dash");
            if (PlayerDriver.IsSwipe(0.3f, new Vector2(0f, 200f), 400f, 0.8f)) failures.AppendLine("short flick became a dash");
        }

        /// <summary>Rebuilt length must match the game's spline, racing line on the road, sane speed profile.</summary>
        static void CheckTrack(MapData map, Track track, StringBuilder failures, StringBuilder report)
        {
            float minRadius = float.MaxValue, minSpeed = float.MaxValue, maxSpeed = 0f;
            int crossing = 0;
            for (int j = 0; j < track.Count; j++)
            {
                if (Mathf.Abs(track.Curvature[j]) > 1e-4f) minRadius = Mathf.Min(minRadius, 1f / Mathf.Abs(track.Curvature[j]));
                if (Mathf.Abs(track.LineOffset[j]) > Mathf.Max(track.HalfWidth - CarController.Size.x * 0.5f - 1f, 0.01f)) { failures.AppendLine($"{map.name}: racing line off the road at {j}"); break; }
                float v = track.LineSpeed[j];
                if (float.IsNaN(v) || v <= 1f) { failures.AppendLine($"{map.name}: bad line speed {v} at {j}"); break; }
                minSpeed = Mathf.Min(minSpeed, v);
                maxSpeed = Mathf.Max(maxSpeed, v);
                if (track.Crossing[j]) crossing++;
            }
            float lengthError = Mathf.Abs(track.Length - map.length) / map.length;
            report.AppendLine($"  track {map.name,-26} {track.Length,6:0} m (game {map.length:0}) width {track.HalfWidth * 2f:0} min R {minRadius:0} m " +
                              $"speed {minSpeed:0}-{maxSpeed:0} m/s crossing {crossing * track.Step:0} m cps {track.CheckpointDistances.Length} grid {track.Grid.Count}");
            if (lengthError > 0.03f) failures.AppendLine($"{map.name}: length {track.Length:0} m vs game {map.length:0} m");
        }
    }
}
