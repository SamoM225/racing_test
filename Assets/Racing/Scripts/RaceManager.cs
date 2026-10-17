using System.Collections.Generic;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Owns the race: track, cars and a single deterministic tick with one fixed order every step for every car, player
    /// or bot (see Tick). This file: race flow, track positions, laps, checkpoints, positions / overtakes, the perception
    /// history for bots, slipstream. The other parts: RaceManager.RubberBand.cs, RaceManager.Recovery.cs (respawn,
    /// ghost, wrecks, sparks), RaceManager.Scoring.cs (drift, points, race end).
    /// </summary>
    public partial class RaceManager : MonoBehaviour
    {
        // Slipstream: inside a cone behind a faster car.
        const float DraftMinSpeed = 15f;               // m/s of the car in front
        const float DraftMinDist = 4f, DraftMaxDist = 25f; // m; strength falls linearly to 0 at DraftMaxDist
        const float DraftCone = 0.95f;                 // cos of the cone's half angle (~18°)
        const float DraftWidths = 0.85f;               // x car width off the car's axis
        const float DraftRate = 2f;                    // 1/s the draft builds / fades
        const float DraftEnergy = 0.3f;                // pips per s at full draft

        [Header("Race")]
        public int laps = 3;
        [Range(1, 7)] public int opponents = 3; // 4 cars, like the game
        public Difficulty difficulty = Difficulty.Normal;
        [Tooltip("racing_unity map (see Resources/maps.json); _T2 = the same loop in reverse")] public string mapName = "NewYork_Downtown";
        [Tooltip("Use the map's own lap count (game value)")] public bool mapLaps = true;

        [Header("Size & surface (menu)")]
        [Tooltip("Car size factor (1 = the game's 4.1 x 2.1 m Formula; 0.7 = RC look)")] [Range(0.4f, 1f)] public float carScale = 0.7f;
        [Tooltip("Road width factor over the map's own width")] [Range(1f, 2.5f)] public float roadWidthScale = 1.5f;
        [Tooltip("Force one surface on the whole road instead of the map's bands")] public bool overrideSurface;
        public SurfaceType roadSurface = SurfaceType.Mud;
        string _builtKey;
        string TrackKey => $"{mapName}|{roadWidthScale:0.00}|{(overrideSurface ? roadSurface.ToString() : "map")}";
        public int seed = 0; // 0 = random every race

        [Header("Rubber band (racing_unity mode 3 values, improved: the only mode)")]
        [Tooltip("OrderSpeed of the 1st, 2nd, 3rd... car in front of the player")] public float[] frontCarSpeeds = { 0.9f, 0.75f, 0.5f, 0.4f };
        [Tooltip("OrderSpeed of the 1st, 2nd, 3rd... car behind the player")] public float[] rearCarSpeeds = { 1.1f, 1.2f, 1.3f, 1.4f };
        public float frontGapSpeed = 0.4f;
        public float rearGapSpeed = 2f;
        [Tooltip("s, SmoothDamp of OrderSpeed (smoothTimeForSpeed)")] public float smoothTimeForSpeed = 1f;

        [Header("Tuning (live in Play mode)")]
        public ControlTuning control = new ControlTuning();
        public CarTuning carTuning = new CarTuning();
        public SurfaceDef[] surfaces = SurfaceDef.Defaults();

        public Track Track { get; private set; }
        public List<RaceCar> Cars { get; } = new List<RaceCar>();
        public RaceCar Player { get; private set; }
        public RaceState State { get; private set; } = RaceState.Menu;
        public float SimTime { get; private set; }
        public float RaceTime { get; private set; }
        public float Countdown { get; private set; }
        public int Laps => laps;
        public bool RaceOver { get; private set; }
        public bool Paused { get; private set; }
        public readonly List<(string text, Color color, float time)> Feed = new List<(string, Color, float)>();

        static readonly string[] BotNames = { "Viper", "Nova", "Blaze", "Ghost", "Rex", "Luna", "Zed" };
        static readonly Color[] BotColors =
        {
            new Color(0.2f, 0.45f, 1f), new Color(0.2f, 0.85f, 0.3f), new Color(1f, 0.55f, 0.1f), new Color(0.92f, 0.92f, 0.92f),
            new Color(0.6f, 0.3f, 0.9f), new Color(1f, 0.85f, 0.15f), new Color(0.1f, 0.85f, 0.9f)
        };

        const int HistoryFrames = 64;
        CarState[][] _history;
        int _historyHead, _historyCount;
        float _dt = 0.02f;
        int[] _checkpointArrivals;
        float _playerFinishedAt = -1f;
        Transform _carsRoot;
        System.Random _rnd;

        public void Start()
        {
            Time.fixedDeltaTime = 0.02f;
            if (Application.isMobilePlatform) Screen.orientation = ScreenOrientation.Portrait; // like racing_unity
            Initialize();
            var cam = Camera.main;
            if (cam != null && cam.GetComponent<ChaseCamera>() == null) cam.gameObject.AddComponent<ChaseCamera>();
            if (GetComponent<RaceHud>() == null) gameObject.AddComponent<RaceHud>();
        }

        public void Initialize()
        {
            CarController.Scale = carScale;
            if (Track != null && _builtKey == TrackKey) return;
            if (surfaces == null || surfaces.Length != System.Enum.GetValues(typeof(SurfaceType)).Length) surfaces = SurfaceDef.Defaults();
            if (Track != null) Mats.Kill(Track.gameObject);
            var map = MapLibrary.Find(mapName);
            if (map == null) { Debug.LogError("Racing: Resources/maps.json missing"); return; }
            mapName = map.name;
            var go = new GameObject("Track " + map.name);
            go.transform.SetParent(transform, false);
            Track = go.AddComponent<Track>();
            Track.Build(map, surfaces, carTuning, control.cruiseSpeed, roadWidthScale, overrideSurface ? roadSurface : (SurfaceType?)null);
            _builtKey = TrackKey;
            if (mapLaps) laps = map.laps;
            ApplyEnvironment(map);
        }

        /// <summary>Switch map (menu). The track is rebuilt on the next Initialize / StartRace.</summary>
        public void SelectMap(string name)
        {
            mapName = name;
            ClearCars();
            Initialize();
        }

        /// <summary>The map's fog, a warm sun like the game's baked light, sky = fog colour.</summary>
        void ApplyEnvironment(MapData map)
        {
            var fog = map.fog != null && map.fog.Length == 3 ? new Color(map.fog[0], map.fog[1], map.fog[2]) : new Color(0.7f, 0.8f, 0.9f);
            RenderSettings.fog = map.fogOn;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = fog;
            RenderSettings.fogStartDistance = 90f;   // game: 50..150 for a lower camera; ours sits ~50 m from the car
            RenderSettings.fogEndDistance = 260f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.52f, 0.56f);
            var cam = Camera.main;
            if (cam != null) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = fog; }
            var sun = FindFirstObjectByType<Light>();
            if (sun != null && sun.type == LightType.Directional)
            {
                sun.color = new Color(1f, 0.92f, 0.8f);
                sun.intensity = 1.1f;
                sun.transform.rotation = Quaternion.LookRotation(new Vector3(-0.32f, -0.77f, 0.56f));
                sun.shadows = LightShadows.Soft;
            }
        }

        int CheckpointsPerLap => Track.CheckpointDistances.Length;

        /// <summary>Race progress (m) of the k-th checkpoint of the race, counting laps.</summary>
        float CheckpointProgress(int k) => (k / CheckpointsPerLap) * Track.Length + Track.CheckpointDistances[k % CheckpointsPerLap];

        // -------------------------------------------------------------------------------------------------------------
        // Race flow
        // -------------------------------------------------------------------------------------------------------------

        public void StartRace(bool playerIsBot = false, bool skipCountdown = false)
        {
            Initialize();
            ClearCars();
            SetPaused(false);
            _rnd = new System.Random(seed != 0 ? seed : System.Environment.TickCount);
            _carsRoot = new GameObject("Cars").transform;
            _carsRoot.SetParent(transform, false);

            int total = opponents + 1;
            int playerSlot = total - 1; // start from the back, work through the field
            float baseSkill = difficulty == Difficulty.Easy ? 0.25f : difficulty == Difficulty.Normal ? 0.55f : 0.85f;
            for (int slot = 0; slot < total; slot++)
            {
                bool isPlayer = slot == playerSlot;
                int botIndex = slot < playerSlot ? slot : slot - 1;
                var rc = new RaceCar
                {
                    Name = isPlayer ? "YOU" : BotNames[botIndex % BotNames.Length],
                    Color = isPlayer ? new Color(0.95f, 0.15f, 0.15f) : BotColors[botIndex % BotColors.Length],
                    IsPlayer = isPlayer,
                    GridSlot = slot,
                };
                var go = new GameObject(rc.Name);
                go.transform.SetParent(_carsRoot, false);
                go.AddComponent<Rigidbody>();
                rc.Car = go.AddComponent<CarController>();
                rc.Car.Setup(carTuning, surfaces, rc.Color);
                rc.Car.Energy = control.energyStart;
                rc.Car.Body.interpolation = RigidbodyInterpolation.Interpolate; // every car: physics runs at 50 Hz, the screen faster
                rc.Pilot = new AutoPilot(this, rc);
                PlaceOnGrid(rc, slot);

                float skill = Mathf.Clamp01(baseSkill + (float)(_rnd.NextDouble() * 2 - 1) * 0.15f);
                if (!isPlayer || playerIsBot)
                {
                    rc.AI = new AIDriver(this, rc, skill, botIndex, _rnd.Next());
                    rc.Driver = rc.AI;
                    if (isPlayer) Player = rc; // a bot in the player's seat (tests): the handicap still centres on it
                }
                else
                {
                    rc.Driver = new PlayerDriver(this);
                    Player = rc;
                }
                var car = rc;
                rc.Car.WallHit += impact => OnWallHit(car, impact);
                rc.Car.Impact += (point, strength) => OnImpact(car, point, strength);
                rc.Car.Damaged += (amount, source) => OnDamaged(car, amount, source);
                rc.Car.MiniTurbo += tier => { if (car.IsPlayer) Say($"MINI TURBO {new string('>', tier)}", new Color(0.3f, 0.8f, 1f)); };
                rc.Car.DashHit += victim =>
                {
                    if (State != RaceState.Racing || car.Finished) return;
                    car.DashHits++;
                    if (car.IsPlayer) Say($"DASH HIT  +{Points.DashHitEach}", new Color(1f, 0.5f, 0.2f));
                    else if (Player != null && victim == Player.Car) Say($"{car.Name} RAMMED YOU", new Color(1f, 0.3f, 0.3f));
                };
                Cars.Add(rc);
            }

            _history = new CarState[HistoryFrames][];
            for (int i = 0; i < HistoryFrames; i++) _history[i] = new CarState[Cars.Count];
            _historyCount = 0;
            _checkpointArrivals = new int[CheckpointsPerLap * laps + 1];
            _playerFinishedAt = -1f;
            RaceOver = false;
            RaceTime = 0f;
            Feed.Clear();
            Countdown = skipCountdown ? 0f : 3.5f;
            State = skipCountdown ? RaceState.Racing : RaceState.Countdown;
            var cam = Camera.main != null ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (cam != null) cam.target = Player != null ? Player.Car : Cars[0].Car;
        }

        /// <summary>The map's StartingPointsHolder slots; more cars continue the same pattern (2.6 m sides, 3 m stagger).</summary>
        void PlaceOnGrid(RaceCar rc, int slot)
        {
            Vector3 pos;
            if (slot < Track.Grid.Count) pos = Track.Grid[slot].pos;
            else
            {
                float back = 12f + slot * 3f;
                Track.Sample(Track.WrapDistance(-back), out var c, out var r, out _, out _);
                pos = c + r * (slot % 2 == 0 ? -2.6f : 2.6f);
            }
            int idx = 0;
            Track.Locate(pos, ref idx, out float d, out float lateral);
            lateral = Mathf.Clamp(lateral * roadWidthScale, -Track.HalfWidth + CarController.Size.x, Track.HalfWidth - CarController.Size.x); // the grid widens with the road
            pos = Track.Center[idx] + Track.Right[idx] * lateral + Track.Tangent[idx] * (d - idx * Track.Step);
            var t = rc.Car.transform;
            t.position = pos + Vector3.up * 0.8f;
            t.rotation = Quaternion.LookRotation(Track.Tangent[idx]);
            rc.Car.Body.position = t.position;
            rc.Car.Body.rotation = t.rotation;
            rc.TrackIndex = idx;
            rc.Distance = d;
            rc.Lateral = lateral;
            rc.Progress = d > Track.Length / 2f ? d - Track.Length : d; // behind the line = negative
            rc.Pilot.Reset(lateral - Track.LineOffset[idx]);
        }

        public void ToMenu()
        {
            ClearCars();
            SetPaused(false);
            State = RaceState.Menu;
        }

        void ClearCars()
        {
            foreach (var rc in Cars) if (rc.Car != null) Mats.Kill(rc.Car.gameObject);
            if (_carsRoot != null) Mats.Kill(_carsRoot.gameObject);
            Cars.Clear();
            Player = null;
        }

        public void SetPaused(bool paused)
        {
            Paused = paused;
            if (Application.isPlaying) Time.timeScale = paused ? 0f : 1f;
        }

        public static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape) && (State == RaceState.Countdown || State == RaceState.Racing)) SetPaused(!Paused);
            if (Input.GetKeyDown(KeyCode.R) && State == RaceState.Racing && Player != null && !Paused) Respawn(Player);
            if (Player?.Driver is PlayerDriver pd && !Paused) pd.UpdatePointer();
        }

        void FixedUpdate()
        {
            if (State == RaceState.Menu || Cars.Count == 0) return;
            Tick(Time.fixedDeltaTime);
        }

        // -------------------------------------------------------------------------------------------------------------
        // The tick
        // -------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// One race step (dt = 0.02 s, FixedUpdate or a test loop): track positions -> places -> perception history ->
        /// slipstream -> rubber band -> each car: driver input -> AutoPilot -> CarController -> drift score and recovery
        /// -> race end. The same order every step keeps the race deterministic (the tests rely on it).
        /// </summary>
        public void Tick(float dt)
        {
            _dt = dt;
            SimTime += dt;
            if (State == RaceState.Countdown)
            {
                Countdown -= dt;
                if (Countdown <= 0f) { State = RaceState.Racing; RaceTime = 0f; Say("GO!", Color.green); }
            }
            else RaceTime += dt;

            foreach (var rc in Cars) UpdateTrackPosition(rc);
            UpdatePositions();
            RecordHistory();
            UpdateSlipstream();
            UpdateRubberBanding(dt);

            bool hold = State == RaceState.Countdown;
            foreach (var rc in Cars)
            {
                rc.Car.Hold = hold;
                if (hold) rc.Car.Simulate(0f, 0f, false, dt);
                else rc.Pilot.Step(rc.Driver.GetInput(dt), dt);
            }
            foreach (var rc in Cars)
            {
                UpdateDrift(rc, dt);
                CheckRespawn(rc, dt);
            }
            CheckRaceEnd();
        }

        void UpdateTrackPosition(RaceCar rc)
        {
            float old = rc.Distance;
            Track.Locate(rc.Car.transform.position, ref rc.TrackIndex, out rc.Distance, out rc.Lateral);
            float delta = rc.Distance - old;
            if (delta < -Track.Length / 2f) delta += Track.Length;
            if (delta > Track.Length / 2f) delta -= Track.Length;
            rc.Progress += delta;
            if (State != RaceState.Racing && State != RaceState.Results) return;

            // Laps.
            int lapsDone = rc.Progress < 0f ? 0 : Mathf.FloorToInt(rc.Progress / Track.Length);
            if (lapsDone > rc.LapsDone)
            {
                rc.LastLap = RaceTime - rc.LapStart;
                rc.LapStart = RaceTime;
                bool best = rc.LastLap < rc.BestLap;
                if (best) rc.BestLap = rc.LastLap;
                rc.LapsDone = lapsDone;
                if (rc.IsPlayer && !rc.Finished) Say($"LAP {rc.LastLap:0.00}s{(best ? "  BEST" : "")}", Color.white);
            }

            // Checkpoints: order points (100 % first .. 25 % last) and a bit of boost, like energy pips in the original.
            int cpPerLap = CheckpointsPerLap;
            while (!rc.Finished && rc.NextCheckpoint < _checkpointArrivals.Length - 1 && rc.Progress >= CheckpointProgress(rc.NextCheckpoint))
            {
                int order = _checkpointArrivals[rc.NextCheckpoint]++;
                float share = CheckpointShare(order, Cars.Count);
                float pts = share * Points.CheckpointPool / (cpPerLap * laps);
                rc.CheckpointPoints += pts;
                rc.Car.Energy = Mathf.Min(rc.Car.EnergyMax, rc.Car.Energy + 2f * share); // 2 pips x order share, as in the original
                rc.NextCheckpoint++;
                if (rc.IsPlayer) Say($"CHECKPOINT {order + 1}.  +{pts:0}", new Color(1f, 0.85f, 0.2f));
            }

            // Finish.
            if (!rc.Finished && rc.Progress >= laps * Track.Length)
            {
                rc.Finished = true;
                rc.FinishTime = RaceTime;
                rc.FinishPosition = 1 + Cars.FindAll(c => c.Finished && c != rc).Count;
                if (rc.IsPlayer)
                {
                    Say($"FINISH  P{rc.FinishPosition}", Color.green);
                    _playerFinishedAt = RaceTime;
                    rc.AI ??= new AIDriver(this, rc, 0.7f, 0, 1234); // autopilot for the cool-down lap
                    rc.Driver = rc.AI;
                    BankDrift(rc);
                }
            }
        }

        /// <summary>racing_unity checkpoint order points 10 / 8 / 5 / 3 (4 cars) as a share; spread linearly for more cars.</summary>
        static float CheckpointShare(int order, int cars)
        {
            float[] table = { 1f, 0.8f, 0.5f, 0.3f };
            if (cars <= table.Length) return table[Mathf.Min(order, table.Length - 1)];
            return Mathf.Lerp(1f, 0.3f, order / (float)(cars - 1));
        }

        void UpdatePositions()
        {
            var order = new List<RaceCar>(Cars);
            order.Sort((a, b) =>
            {
                if (a.Finished && b.Finished) return a.FinishTime.CompareTo(b.FinishTime);
                if (a.Finished != b.Finished) return a.Finished ? -1 : 1;
                return b.Progress.CompareTo(a.Progress);
            });
            for (int i = 0; i < order.Count; i++)
            {
                var rc = order[i];
                int pos = i + 1;
                if (State == RaceState.Racing && RaceTime > 3f && !rc.Finished)
                {
                    // Overtakes count only when the gained position is held for 1.5 s.
                    if (rc.Position != 0 && pos < rc.Position && rc.PendingSince < 0f) { rc.PendingFromPosition = rc.Position; rc.PendingSince = RaceTime; }
                    if (rc.PendingSince >= 0f)
                    {
                        if (pos >= rc.PendingFromPosition) rc.PendingSince = -1f;
                        else if (RaceTime - rc.PendingSince >= 1.5f)
                        {
                            int gained = rc.PendingFromPosition - pos;
                            rc.Overtakes += gained;
                            rc.PendingSince = -1f;
                            if (rc.IsPlayer) Say(gained > 1 ? $"OVERTAKE x{gained}" : "OVERTAKE", new Color(0.4f, 1f, 0.4f));
                        }
                    }
                }
                rc.Position = pos;
            }
        }

        void RecordHistory()
        {
            var frame = _history[_historyHead];
            for (int i = 0; i < Cars.Count; i++)
            {
                var rc = Cars[i];
                frame[i] = new CarState { position = rc.Car.transform.position, progress = rc.Progress, lateral = rc.Lateral, speed = rc.Car.ForwardSpeed };
            }
            _historyHead = (_historyHead + 1) % HistoryFrames;
            _historyCount = Mathf.Min(_historyCount + 1, HistoryFrames);
        }

        /// <summary>
        /// The race as seen <paramref name="delay"/> seconds ago, extrapolated forward with the speed seen then -
        /// like a human who reacts late but still judges where a car is going. Written into <paramref name="into"/>.
        /// </summary>
        public CarState[] DelayedStates(float delay, CarState[] into)
        {
            int back = Mathf.Clamp(Mathf.RoundToInt(delay / _dt), 1, Mathf.Max(_historyCount, 1));
            var frame = _history[((_historyHead - back) % HistoryFrames + HistoryFrames) % HistoryFrames];
            float elapsed = back * _dt;
            for (int i = 0; i < frame.Length; i++)
            {
                into[i] = frame[i];
                into[i].progress += frame[i].speed * elapsed;
            }
            return into;
        }

        /// <summary>Driving in another car's wake: less drag, a bit of top speed, slow boost charge.</summary>
        void UpdateSlipstream()
        {
            foreach (var a in Cars)
            {
                float draft = 0f;
                Vector3 pa = a.Car.transform.position;
                foreach (var b in Cars)
                {
                    if (a == b || b.Car.Speed < DraftMinSpeed) continue;
                    Vector3 d = b.Car.transform.position - pa;
                    float dist = d.magnitude;
                    if (dist < DraftMinDist || dist > DraftMaxDist) continue;
                    Vector3 bf = b.Car.transform.forward;
                    if (Vector3.Dot(bf, d / dist) < DraftCone) continue;
                    if (Vector3.ProjectOnPlane(d, bf).magnitude > CarController.Size.x * DraftWidths) continue;
                    draft = Mathf.Max(draft, 1f - dist / DraftMaxDist);
                }
                a.Car.Draft = Mathf.MoveTowards(a.Car.Draft, draft, _dt * DraftRate);
                if (a.Car.Draft > 0.1f) a.Car.Energy = Mathf.Min(a.Car.EnergyMax, a.Car.Energy + DraftEnergy * a.Car.Draft * _dt);
            }
        }

        public void Say(string text, Color color)
        {
            Feed.Add((text, color, Time.unscaledTime));
            if (Feed.Count > 6) Feed.RemoveAt(0);
        }
    }
}
