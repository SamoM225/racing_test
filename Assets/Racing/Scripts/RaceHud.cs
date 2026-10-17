using System.Collections.Generic;
using UnityEngine;

namespace Racing
{
    /// <summary>Portrait mobile UI in IMGUI (touch works as mouse): menu, HUD with floating joystick, pause, results.</summary>
    [RequireComponent(typeof(RaceManager))]
    public class RaceHud : MonoBehaviour
    {
        RaceManager _race;
        GUIStyle _big, _mid, _small, _center, _smallCenter, _button;
        Texture2D _ring, _dot;
        float _scale;
        static readonly string[] Ordinals = { "1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th" };

        void Awake() => _race = GetComponent<RaceManager>();

        /// <summary>Pause button in screen pixels (bottom-left origin, as touches report them).</summary>
        Rect PauseButtonScreen => new Rect(Screen.width - 170f * _scale, 0f, 170f * _scale, 170f * _scale);
        /// <summary>Recover button (GUI units, top-left origin) and the same in screen pixels (bottom-left origin).</summary>
        Rect RecoverButton(float w, float h) => new Rect(w / 2f - 230f, h * 0.68f, 460f, 140f);
        Rect RecoverButtonScreen
        {
            get
            {
                var r = RecoverButton(Screen.width / _scale, Screen.height / _scale);
                return new Rect(r.x * _scale, Screen.height - (r.y + r.height) * _scale, r.width * _scale, r.height * _scale);
            }
        }
        bool RecoverShown => _race.Player != null && _race.CanRecover(_race.Player);

        void Update()
        {
            _scale = Mathf.Min(Screen.width, Screen.height) / 1080f;
            if (_race.Player?.Driver is PlayerDriver pd && pd.IgnoreTouchAt == null)
                pd.IgnoreTouchAt = p => PauseButtonScreen.Contains(p) || (RecoverShown && RecoverButtonScreen.Contains(p));
        }

        void Styles()
        {
            if (_big != null) return;
            _big = new GUIStyle(GUI.skin.label) { fontSize = 80, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true };
            _mid = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, richText = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 28, richText = true };
            _center = new GUIStyle(_mid) { alignment = TextAnchor.MiddleCenter };
            _smallCenter = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 40, fontStyle = FontStyle.Bold };
            _ring = Circle(128, 0.86f);
            _dot = Circle(64, 0f);
        }

        static Texture2D Circle(int size, float hole)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                    float a = Mathf.Clamp01((1f - d) * r) * Mathf.Clamp01((d - hole) * r);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            return tex;
        }

        void OnGUI()
        {
            Styles();
            _scale = Mathf.Min(Screen.width, Screen.height) / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
            float w = Screen.width / _scale, h = Screen.height / _scale;

            switch (_race.State)
            {
                case RaceState.Menu: if (_settings) Settings(w, h); else Menu(w, h); break;
                case RaceState.Countdown:
                case RaceState.Racing: Hud(w, h); break;
                case RaceState.Results: Results(w, h); break;
            }
            if (_race.Paused) Pause(w, h);
        }

        // -------------------------------------------------------------------------------------------------------------

        Vector2 _menuScroll;
        static readonly float[] CarSizes = { 0.5f, 0.6f, 0.7f, 0.85f, 1f };
        static readonly float[] RoadWidths = { 1f, 1.25f, 1.5f, 1.75f, 2f };
        static readonly SurfaceType[] RoadSurfaces = { SurfaceType.Mud, SurfaceType.Dirtway, SurfaceType.Gravel, SurfaceType.Sand, SurfaceType.Snow, SurfaceType.Ice, SurfaceType.Asphalt };

        bool _settings;

        /// <summary>Main screen: just start, settings, quit. Everything configurable lives on the settings screen.</summary>
        void Menu(float w, float h)
        {
            float bw = Mathf.Min(w - 80f, 820f), x = (w - bw) / 2f, y = h * 0.2f;
            GUI.Label(new Rect(x, y, bw, 110f), "RC RACING", _big);
            var map = MapLibrary.Find(_race.mapName)?.Title ?? "";
            GUI.Label(new Rect(x, y + 120f, bw, 60f), map, _smallCenter);
            y += 220f;
            if (GUI.Button(new Rect(x, y, bw, 140f), "START RACE", _button)) _race.StartRace();
            if (GUI.Button(new Rect(x, y + 170f, bw, 110f), "SETTINGS", _button)) _settings = true;
            if (GUI.Button(new Rect(x, y + 310f, bw, 110f), "QUIT", _button)) RaceManager.Quit();
            GUI.Label(new Rect(x, y + 460f, bw, 280f),
                "<b>Hold 0.8 s & drag</b> anywhere: joystick\n  ← → move across the track\n  ↑ boost   ↓ brake\n<b>Quick swipe</b>: dash (costs 1 energy)\n\nKeyboard: arrows/WASD, Space/X dash ↑↓, Q/E dash ←→",
                _small);
        }

        void Settings(float w, float h)
        {
            const float contentH = 1900f;
            float bw = Mathf.Min(w - 80f, 820f), x = (w - bw) / 2f;
            _menuScroll = GUI.BeginScrollView(new Rect(0f, 0f, w, h), _menuScroll, new Rect(0f, 0f, w - 30f, Mathf.Max(h, contentH)));
            float y = 60f;
            GUI.Box(new Rect(x - 20f, y - 30f, bw + 40f, contentH - 60f), GUIContent.none);
            GUI.Label(new Rect(x, y, bw, 110f), "SETTINGS", _big);
            y += 140f;
            MapPicker(x, y, bw); y += 230f;
            int laps = Stepper(x, y, bw, "Laps", _race.laps, 1, 10); y += 105f;
            if (laps != _race.laps) { _race.laps = laps; _race.mapLaps = false; }
            _race.opponents = Stepper(x, y, bw, "Opponents", _race.opponents, 1, 7); y += 105f;
            if (Choice(x, y, bw, "Difficulty", _race.difficulty.ToString())) _race.difficulty = (Difficulty)(((int)_race.difficulty + 1) % 3);
            y += 105f;

            // Size and surface: the track is rebuilt when they change.
            if (Choice(x, y, bw, "Car size", $"{_race.carScale:0.##}x {(_race.carScale < 0.95f ? "(RC)" : "(game)")}"))
            {
                _race.carScale = Next(CarSizes, _race.carScale);
                _race.Initialize();
            }
            y += 105f;
            if (Choice(x, y, bw, "Road width", $"{_race.roadWidthScale:0.##}x"))
            {
                _race.roadWidthScale = Next(RoadWidths, _race.roadWidthScale);
                _race.SelectMap(_race.mapName);
            }
            y += 105f;
            if (Choice(x, y, bw, "Surface", _race.overrideSurface ? _race.roadSurface.ToString() : "Map default"))
            {
                // Map default -> Mud -> Dirt -> Gravel -> Sand -> Snow -> Ice -> Asphalt -> Map default
                int i = _race.overrideSurface ? System.Array.IndexOf(RoadSurfaces, _race.roadSurface) + 1 : 0;
                _race.overrideSurface = i < RoadSurfaces.Length;
                if (_race.overrideSurface) _race.roadSurface = RoadSurfaces[i];
                _race.SelectMap(_race.mapName);
            }
            y += 135f;
            if (GUI.Button(new Rect(x, y, bw, 110f), "BACK", _button)) _settings = false;
            GUI.EndScrollView();
        }

        bool Choice(float x, float y, float bw, string label, string value)
        {
            GUI.Label(new Rect(x, y, bw * 0.45f, 90f), label, _mid);
            return GUI.Button(new Rect(x + bw * 0.45f, y, bw * 0.55f, 90f), value, _button);
        }

        static float Next(float[] options, float current)
        {
            int i = System.Array.FindIndex(options, o => Mathf.Abs(o - current) < 0.01f);
            return options[(i + 1) % options.Length];
        }


        /// <summary>The 24 racing_unity maps, plus their reverse layout.</summary>
        void MapPicker(float x, float y, float bw)
        {
            var maps = MapLibrary.Ordered();
            if (maps.Count == 0) { GUI.Label(new Rect(x, y, bw, 90f), "maps.json missing", _mid); return; }
            int idx = Mathf.Max(0, maps.FindIndex(m => m.name == _race.mapName));
            bool reverse = maps[idx].name.EndsWith("_T2");
            string baseName = maps[idx].name.Replace("_T2", "");
            var forward = maps.FindAll(m => !m.name.EndsWith("_T2"));
            int f = Mathf.Max(0, forward.FindIndex(m => m.name == baseName));
            int nf = f;
            if (GUI.Button(new Rect(x, y, 110f, 100f), "<", _button)) nf = (f - 1 + forward.Count) % forward.Count;
            GUI.Label(new Rect(x + 110f, y, bw - 220f, 100f), forward[f].Title + $"\n<size=26>{forward[f].env} · {forward[f].length:0} m · {forward[f].laps} laps</size>", _center);
            if (GUI.Button(new Rect(x + bw - 110f, y, 110f, 100f), ">", _button)) nf = (f + 1) % forward.Count;
            bool nr = reverse;
            if (GUI.Button(new Rect(x, y + 115f, bw, 90f), reverse ? "Direction: REVERSE" : "Direction: normal", _button)) nr = !reverse;
            if (nf != f || nr != reverse)
            {
                string name = forward[nf].name + (nr ? "_T2" : "");
                if (MapLibrary.Find(name).name != name) name = forward[nf].name;
                _race.mapLaps = true;
                _race.SelectMap(name);
            }
        }
        int Stepper(float x, float y, float bw, string label, int value, int min, int max)
        {
            GUI.Label(new Rect(x, y, bw * 0.45f, 90f), label, _mid);
            float bx = x + bw * 0.45f, sw = bw * 0.55f;
            if (GUI.Button(new Rect(bx, y, 110f, 90f), "-", _button)) value--;
            GUI.Label(new Rect(bx + 110f, y, sw - 220f, 90f), value.ToString(), _center);
            if (GUI.Button(new Rect(bx + sw - 110f, y, 110f, 90f), "+", _button)) value++;
            return Mathf.Clamp(value, min, max);
        }

        // -------------------------------------------------------------------------------------------------------------

        void Hud(float w, float h)
        {
            var p = _race.Player;
            if (p == null) return;
            var car = p.Car;
            var ap = p.Pilot;

            // Top: position, lap, time, points.
            GUI.Label(new Rect(30f, 30f, 500f, 110f), $"<size=100>{Ordinal(p.Position)}</size><size=44> / {_race.Cars.Count}</size>", _mid);
            int lap = Mathf.Clamp(p.LapsDone + 1, 1, _race.Laps);
            GUI.Label(new Rect(w - 430f, 30f, 400f, 60f), $"LAP {lap}/{_race.Laps}", new GUIStyle(_mid) { alignment = TextAnchor.UpperRight });
            GUI.Label(new Rect(w - 430f, 85f, 400f, 40f), Fmt(_race.RaceTime), new GUIStyle(_small) { alignment = TextAnchor.UpperRight });
            GUI.Label(new Rect(w - 430f, 125f, 400f, 40f), $"{_race.LivePoints(p)} pts", new GUIStyle(_small) { alignment = TextAnchor.UpperRight });

            // Standings.
            float y = 160f;
            var order = new List<RaceCar>(_race.Cars);
            order.Sort((a, b) => a.Position.CompareTo(b.Position));
            foreach (var rc in order)
            {
                float gap = p.Progress - rc.Progress;
                string g = rc == p ? "" : (gap > 0 ? $"-{gap:0}m" : $"+{-gap:0}m");
                GUI.color = rc.Color;
                GUI.Label(new Rect(30f, y, 520f, 36f), $"{rc.Position}. {rc.Name}  {g}  <size=22>{rc.Car.HP:0} HP</size>", _small);
                y += 34f;
            }
            GUI.color = Color.white;

            // Feed and countdown.
            if (_race.State == RaceState.Countdown)
                GUI.Label(new Rect(0f, h * 0.3f, w, 160f), $"<size=180>{Mathf.CeilToInt(_race.Countdown)}</size>", _big);
            float fy = h * 0.36f;
            foreach (var (text, color, time) in _race.Feed)
            {
                float age = Time.unscaledTime - time;
                if (age > 2.5f) continue;
                GUI.color = new Color(color.r, color.g, color.b, Mathf.Clamp01(2.5f - age));
                GUI.Label(new Rect(0f, fy, w, 56f), text, _center);
                fy += 56f;
            }
            GUI.color = Color.white;

            // Bottom: health, speed, surface, drift, energy, dash, joystick lock.
            float by = h - 330f;
            HealthBar(new Rect(30f, by - 60f, w - 230f, 44f), car);
            GUI.Label(new Rect(30f, by, 500f, 90f), $"<size=84>{car.Speed * 3.6f:0}</size> km/h", _mid);
            string state = car.Surface.type.ToString().ToUpper() + (car.Draft > 0.15f ? "   SLIPSTREAM" : "") + (car.IsBoosting ? "   BOOST" : "")
                           + (car.Oversteer ? "   <color=#ff66e6>OVERSTEER</color>" : car.Understeer ? "   <color=#66ccff>UNDERSTEER</color>" : "");
            GUI.Label(new Rect(30f, by + 95f, w - 230f, 40f), state, _small);
            if (p.CurrentDrift > 0f)
                GUI.Label(new Rect(w - 560f, by + 10f, 380f, 60f), $"<color=#ff66e6>DRIFT {p.CurrentDrift:N0} x{p.DriftCombo:0.0}</color>", new GUIStyle(_mid) { alignment = TextAnchor.UpperRight });

            Pips(new Rect(30f, by + 150f, w - 230f, 54f), car.Energy, car.EnergyMax);
            string dash = ap.DashReadyIn > 0f ? $"DASH {ap.DashReadyIn:0.0}s" : car.Energy >= _race.control.dashCost ? "DASH READY" : "NO ENERGY";
            // Gesture feedback: what the drag is doing right now (swipe up = boost, down = brake).
            float speedPct = (ap.SpeedOrder - 1f) * 100f;
            if (Mathf.Abs(speedPct) > 2f)
                GUI.Label(new Rect(0f, h * 0.62f, w, 80f), speedPct > 0f ? $"<color=#ffa020>BOOST +{speedPct:0}%</color>" : $"<color=#ff5050>BRAKE {speedPct:0}%</color>", new GUIStyle(_center) { fontSize = 56 });
            string stick = ap.Locked ? $"   JOYSTICK LOCKED {ap.LockRemaining:0.0}s" : ap.JoystickVisible ? "   JOYSTICK (costs energy after 1s)" : "";
            GUI.Label(new Rect(30f, by + 215f, w - 60f, 40f), dash + stick, _small);

            // Pause button (bottom-right, excluded from the drag area).
            if (GUI.Button(new Rect(w - 170f, h - 170f, 140f, 140f), "II", _button)) _race.SetPaused(true);
            // Recover: only while the car is in trouble (stuck, flipped, turning round); the race does it anyway after a while.
            if (RecoverShown && GUI.Button(RecoverButton(w, h), "RECOVER", _button)) _race.Respawn(p);

            Joystick(p.Driver as PlayerDriver, ap);
            NameTags();
        }

        static readonly string[] DamageNames = { "", "DAMAGED", "HEAVY DAMAGE", "CRITICAL" };
        static readonly Color[] DamageColors = { new Color(0.3f, 0.9f, 0.3f), new Color(0.95f, 0.85f, 0.2f), new Color(1f, 0.55f, 0.15f), new Color(1f, 0.2f, 0.15f) };

        void HealthBar(Rect r, CarController car)
        {
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = DamageColors[car.DamageLevel];
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * car.HP / CarController.MaxHP, r.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 12f, r.y + 2f, r.width, r.height), $"HP {car.HP:0}   {DamageNames[car.DamageLevel]}", _small);
        }

        void Pips(Rect r, float energy, float max)
        {
            int n = Mathf.RoundToInt(max);
            float gap = 8f, pw = (r.width - gap * (n - 1)) / n;
            for (int i = 0; i < n; i++)
            {
                var pip = new Rect(r.x + i * (pw + gap), r.y, pw, r.height);
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(pip, Texture2D.whiteTexture);
                float fill = Mathf.Clamp01(energy - i);
                GUI.color = fill >= 1f ? new Color(0.25f, 0.8f, 1f) : new Color(0.25f, 0.8f, 1f, 0.5f);
                GUI.DrawTexture(new Rect(pip.x, pip.y, pip.width * fill, pip.height), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
        }

        /// <summary>Floating joystick: appears where the finger went down once the hold leaves the swipe window.</summary>
        void Joystick(PlayerDriver pd, AutoPilot ap)
        {
            if (pd == null || !pd.Dragging || !ap.JoystickVisible) return;
            float radius = pd.Threshold / _scale;
            Vector2 start = new Vector2(pd.DragStart.x, Screen.height - pd.DragStart.y) / _scale;
            Vector2 cur = new Vector2(pd.DragCurrent.x, Screen.height - pd.DragCurrent.y) / _scale;
            Vector2 knob = start + Vector2.ClampMagnitude(cur - start, radius);
            GUI.color = new Color(1f, 1f, 1f, 0.5f);
            GUI.DrawTexture(new Rect(start.x - radius, start.y - radius, radius * 2f, radius * 2f), _ring);
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.DrawTexture(new Rect(knob.x - 60f, knob.y - 60f, 120f, 120f), _dot);
            GUI.color = Color.white;
        }

        void NameTags()
        {
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var rc in _race.Cars)
            {
                if (rc.IsPlayer) continue;
                Vector3 sp = cam.WorldToScreenPoint(rc.Car.transform.position + Vector3.up * 2f);
                if (sp.z <= 0f || sp.z > 140f) continue;
                GUI.color = rc.Color;
                GUI.Label(new Rect(sp.x / _scale - 100f, (Screen.height - sp.y) / _scale - 24f, 200f, 36f), $"{rc.Position}. {rc.Name}", _smallCenter);
            }
            GUI.color = Color.white;
        }

        // -------------------------------------------------------------------------------------------------------------

        void Results(float w, float h)
        {
            var order = new List<RaceCar>(_race.Cars);
            order.Sort((a, b) => b.Result.Total.CompareTo(a.Result.Total));
            float bw = Mathf.Min(w - 60f, 1000f), x = (w - bw) / 2f, y = h * 0.06f;
            GUI.Box(new Rect(x - 15f, y - 15f, bw + 30f, 200f + order.Count * 120f + 300f), GUIContent.none);
            GUI.Label(new Rect(x, y, bw, 110f), "RESULTS", _big);
            y += 130f;
            int rank = 1;
            foreach (var rc in order)
            {
                var r = rc.Result;
                GUI.color = rc.Color;
                GUI.Label(new Rect(x, y, bw, 56f), $"{rank++}.  {rc.Name}   <size=30>finish {Ordinal(rc.FinishPosition)}  {(rc.Finished ? Fmt(rc.FinishTime) : "DNF")}</size>", _mid);
                GUI.Label(new Rect(x, y, bw, 56f), $"{r.Total} pts", new GUIStyle(_mid) { alignment = TextAnchor.UpperRight });
                GUI.color = new Color(1f, 1f, 1f, 0.8f);
                GUI.Label(new Rect(x + 20f, y + 56f, bw - 20f, 50f),
                    $"place {r.placement} · checkp. {r.checkpoints} · drift {r.drift} · overt. {r.overtakes} · dash {r.dashHits} · f.lap {r.fastestLap} · clean {r.clean} · finish {r.finish} · damage dealt {rc.DamageDealt:0} HP · wrecks {rc.Wrecks}", _small);
                y += 120f;
            }
            GUI.color = Color.white;
            y += 30f;
            if (GUI.Button(new Rect(x, y, bw, 110f), "RESTART", _button)) _race.StartRace();
            if (GUI.Button(new Rect(x, y + 130f, bw / 2f - 10f, 110f), "MENU", _button)) _race.ToMenu();
            if (GUI.Button(new Rect(x + bw / 2f + 10f, y + 130f, bw / 2f - 10f, 110f), "QUIT", _button)) RaceManager.Quit();
        }

        void Pause(float w, float h)
        {
            float bw = Mathf.Min(w - 120f, 700f), x = (w - bw) / 2f, y = h / 2f - 330f;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, w, h), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, bw, 110f), "PAUSED", _big);
            if (GUI.Button(new Rect(x, y + 140f, bw, 110f), "RESUME", _button)) _race.SetPaused(false);
            if (GUI.Button(new Rect(x, y + 270f, bw, 110f), "RESTART", _button)) _race.StartRace();
            if (GUI.Button(new Rect(x, y + 400f, bw, 110f), "MENU", _button)) _race.ToMenu();
            if (GUI.Button(new Rect(x, y + 530f, bw, 110f), "QUIT", _button)) RaceManager.Quit();
        }

        static string Ordinal(int p) => p >= 1 && p <= Ordinals.Length ? Ordinals[p - 1] : p + "th";
        static string Fmt(float s) => $"{(int)(s / 60f)}:{s % 60f:00.00}";
    }
}
