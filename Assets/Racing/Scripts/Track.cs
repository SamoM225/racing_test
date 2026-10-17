using System.Collections.Generic;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// A racing_unity map rebuilt from its extracted spline: exact Bezier centre line with elevation, road width and
    /// run-off, surface bands, checkpoints, start grid, fog. Resampled every ~2 m. On top of the geometry it precomputes
    /// what a good driver learns about a track: a minimum-curvature racing line and a grip-based speed profile.
    /// </summary>
    public class Track : MonoBehaviour
    {
        public const float WallHeight = 5f; // invisible collider height, like the game's SM_RoadBarrier_Invisible (visible barrier is 0.9 m)

        const float SampleSpacing = 2f;                 // m between track samples (Step ~ this)
        // Racing line: an elastic band relaxed at shrinking scales (coarse shape first, then detail).
        static readonly int[] LineScales = { 24, 12, 6, 3, 1 }; // samples to the neighbours the point is pulled between
        const int LineIterations = 150;                 // relaxation passes per scale
        const float LineStiffness = 0.6f;               // share of the way to the neighbours' midpoint per pass
        const float LineEdgeMargin = 1.15f;             // m between the car's side and the road edge
        // Speed profile (what a good driver can do on the racing line).
        const float Gravity = 9.81f;                    // m/s²
        const float CornerGripShare = 0.85f;            // of μ used in corners: no downforce, some slip
        const float ProfileBrakeShare = 0.8f;           // of the possible deceleration used before corners
        const float ProfileAccelShare = 0.85f;          // of the possible acceleration used out of corners
        const float ProfileMinSpeed = 6f;               // m/s, never slower
        const int CurvatureSpan = 3;                    // samples each side for the circle through three line points
        const int CrestSpan = 4;                        // samples each side for the vertical curvature

        public MapData Map { get; private set; }
        public float HalfWidth { get; private set; } = 5f;
        public float Runoff { get; private set; } = 2f;
        public int Count { get; private set; }
        public float Step { get; private set; }
        public float Length { get; private set; }
        public Vector3[] Center, Tangent, Right;   // Tangent / Right are horizontal
        public SurfaceType[] Surface;
        public float[] Curvature;                  // signed, centre line; + = turning right
        public float[] LineOffset;                 // racing line, lateral metres from centre
        public float[] LineSpeed;                  // grip-limited speed on the racing line, capped at cruise, m/s
        public float[] LimitSpeed;                 // same, capped only by the car's physical max (corners decide)
        public bool[] Crossing;                    // another part of the loop passes here (no walls, no run-off)
        public float[] CheckpointDistances { get; private set; }
        public List<(Vector3 pos, float yaw)> Grid { get; } = new List<(Vector3, float)>();

        public int Wrap(int i) => ((i % Count) + Count) % Count;
        public float WrapDistance(float d) => ((d % Length) + Length) % Length;
        public Vector3 LinePoint(int i) { i = Wrap(i); return Center[i] + Right[i] * LineOffset[i]; }

        public void Build(MapData map, SurfaceDef[] surfaces, CarTuning car, float topSpeed, float widthScale = 1f, SurfaceType? forcedSurface = null, bool geometry = true)
        {
            Map = map;
            HalfWidth = Mathf.Max(3.5f, (map.halfLeft + map.halfRight) * 0.5f) * widthScale;
            Runoff = Mathf.Max(2f, Mathf.Max(map.runoffLeft, map.runoffRight));
            BuildCenterLine();
            AssignSurfaces();
            if (forcedSurface.HasValue) for (int j = 0; j < Count; j++) Surface[j] = forcedSurface.Value;
            FindCrossings();
            BuildRacingLine();
            LineSpeed = SpeedProfile(surfaces, car, topSpeed);
            LimitSpeed = SpeedProfile(surfaces, car, car.maxSpeed); // grip limit only: what catch-up may never exceed
            CheckpointDistances = map.checkpoints != null && map.checkpoints.Length > 0 ? (float[])map.checkpoints.Clone() : new[] { Length / 3f, Length * 2f / 3f };
            System.Array.Sort(CheckpointDistances);
            Grid.Clear();
            if (map.gridPos != null)
                for (int i = 0; i < map.gridYaw.Length; i++) Grid.Add((map.Knot(map.gridPos, i), map.gridYaw[i]));
            if (geometry) BuildGeometry(surfaces);
        }

        // ---------------------------------------------------------------------------------------------------------
        // Queries
        // ---------------------------------------------------------------------------------------------------------

        /// <summary>Nearest track point, searched around the last known index (3D, so a bridge never snaps to the road below).</summary>
        public void Locate(Vector3 pos, ref int hint, out float distance, out float lateral)
        {
            int best = Wrap(hint);
            float bestD = float.MaxValue;
            for (int k = -15; k <= 30; k++)
            {
                int i = Wrap(hint + k);
                float d = (pos - Center[i]).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (bestD > 40f * 40f) // lost (respawn, teleport): global search
                for (int i = 0; i < Count; i++)
                {
                    float d = (pos - Center[i]).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
            hint = best;
            Vector3 rel = pos - Center[best];
            distance = WrapDistance(best * Step + Vector3.Dot(rel, Tangent[best]));
            lateral = Vector3.Dot(rel, Right[best]);
        }

        /// <summary>Interpolated centre, right vector and racing line at a lap distance.</summary>
        public void Sample(float distance, out Vector3 center, out Vector3 right, out float line, out int index)
        {
            float f = WrapDistance(distance) / Step;
            int i = Mathf.FloorToInt(f);
            float t = f - i;
            i = Wrap(i);
            int j = Wrap(i + 1);
            center = Vector3.Lerp(Center[i], Center[j], t);
            right = Vector3.Lerp(Right[i], Right[j], t).normalized;
            line = Mathf.Lerp(LineOffset[i], LineOffset[j], t);
            index = i;
        }

        public float SpeedAt(float distance) => At(LineSpeed, distance);
        public float LimitAt(float distance) => At(LimitSpeed, distance);

        float At(float[] profile, float distance)
        {
            float f = WrapDistance(distance) / Step;
            int i = Mathf.FloorToInt(f);
            return Mathf.Lerp(profile[Wrap(i)], profile[Wrap(i + 1)], f - i);
        }

        /// <summary>Metres to the first point ahead where the racing line wants to be clearly slower than v.</summary>
        public float DistanceToBraking(int index, float v, float maxLook = 300f, float margin = 3f)
        {
            int n = Mathf.CeilToInt(maxLook / Step);
            for (int k = 1; k <= n; k++)
                if (LineSpeed[Wrap(index + k)] < v - margin) return k * Step;
            return float.MaxValue;
        }

        /// <summary>+1 if the next corner in the window turns right, -1 left, 0 straight.</summary>
        public int TurnAhead(int index, float from, float to)
        {
            float sum = 0f;
            for (int k = Mathf.RoundToInt(from / Step); k <= Mathf.RoundToInt(to / Step); k++) sum += Curvature[Wrap(index + k)];
            return Mathf.Abs(sum) < 0.02f ? 0 : (sum > 0f ? 1 : -1);
        }

        /// <summary>Metres to the next checkpoint (or the finish line) ahead of a lap distance.</summary>
        public float DistanceToNextCheckpoint(float lapDistance)
        {
            foreach (float c in CheckpointDistances) if (c > lapDistance) return c - lapDistance;
            return Length - lapDistance;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Centre line, surfaces, crossings, racing line, speed profile
        // ---------------------------------------------------------------------------------------------------------

        void BuildCenterLine()
        {
            var map = Map;
            int n = map.KnotCount;
            const int samplesPerSegment = 32;
            var dense = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                Vector3 p0 = map.Knot(map.kp, i), p1 = map.Knot(map.ko, i), p2 = map.Knot(map.ki, j), p3 = map.Knot(map.kp, j);
                for (int s = 0; s < samplesPerSegment; s++)
                {
                    float t = s / (float)samplesPerSegment, u = 1f - t;
                    dense.Add(u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
                }
            }

            // Start the loop at the finish line, like the game measures everything from it.
            Vector3 finish = map.finish != null && map.finish.Length == 3 ? new Vector3(map.finish[0], map.finish[1], map.finish[2]) : dense[0];
            int startIdx = 0;
            float bestD = float.MaxValue;
            for (int k = 0; k < dense.Count; k++)
            {
                float d = (dense[k] - finish).sqrMagnitude;
                if (d < bestD) { bestD = d; startIdx = k; }
            }
            var loop = new List<Vector3>(dense.Count);
            for (int k = 0; k < dense.Count; k++) loop.Add(dense[(startIdx + k) % dense.Count]);

            int m = loop.Count;
            var cum = new float[m + 1];
            for (int k = 0; k < m; k++) cum[k + 1] = cum[k] + Vector3.Distance(loop[k], loop[(k + 1) % m]);
            Length = cum[m];
            Count = Mathf.RoundToInt(Length / SampleSpacing);
            Step = Length / Count;

            Center = new Vector3[Count];
            int seg = 0;
            for (int j = 0; j < Count; j++)
            {
                float d = j * Step;
                while (cum[seg + 1] < d) seg++;
                float t = (d - cum[seg]) / Mathf.Max(cum[seg + 1] - cum[seg], 1e-5f);
                Center[j] = Vector3.Lerp(loop[seg], loop[(seg + 1) % m], t);
            }

            Tangent = new Vector3[Count];
            Right = new Vector3[Count];
            for (int j = 0; j < Count; j++)
            {
                Vector3 tg = Center[Wrap(j + 1)] - Center[Wrap(j - 1)];
                tg.y = 0f;
                Tangent[j] = tg.normalized;
                Right[j] = Vector3.Cross(Vector3.up, Tangent[j]);
            }
            Curvature = new float[Count];
            for (int j = 0; j < Count; j++)
                Curvature[j] = Vector3.SignedAngle(Tangent[Wrap(j - 2)], Tangent[Wrap(j + 2)], Vector3.up) * Mathf.Deg2Rad / (4f * Step);
        }

        /// <summary>Surface bands are in metres from the finish line; a gap takes the nearest band (the game blends them).</summary>
        void AssignSurfaces()
        {
            Surface = new SurfaceType[Count];
            var map = Map;
            int bands = map.bandMat != null ? map.bandMat.Length : 0;
            for (int j = 0; j < Count; j++)
            {
                float d = j * Step;
                var best = SurfaceType.Asphalt;
                float bestGap = float.MaxValue;
                for (int b = 0; b < bands; b++)
                {
                    float from = map.bandFrom[b], to = map.bandTo[b];
                    bool inside = from <= to ? d >= from && d <= to : d >= from || d <= to;
                    float gap = inside ? 0f : Mathf.Min(CircDist(d, from), CircDist(d, to));
                    if (gap < bestGap) { bestGap = gap; best = (SurfaceType)map.bandMat[b]; }
                }
                Surface[j] = best;
            }
        }

        float CircDist(float a, float b)
        {
            float d = Mathf.Abs(a - b) % Length;
            return Mathf.Min(d, Length - d);
        }

        void FindCrossings()
        {
            Crossing = new bool[Count];
            float reach = HalfWidth + Runoff + 3f;
            int skip = Mathf.CeilToInt(reach * 4f / Step);
            for (int a = 0; a < Count; a++)
                for (int b = 0; b < Count; b++)
                {
                    int di = Mathf.Abs(a - b);
                    if (Mathf.Min(di, Count - di) < skip) continue;
                    Vector3 d = Center[a] - Center[b];
                    if (Mathf.Abs(d.y) < 4f && d.x * d.x + d.z * d.z < reach * reach) { Crossing[a] = true; break; }
                }
        }

        /// <summary>
        /// Minimum-curvature racing line as an elastic band: every point is pulled toward the midpoint of its neighbours,
        /// at shrinking scales, clamped to the road. Straightening the line = the largest radius = the fastest corner.
        /// </summary>
        void BuildRacingLine()
        {
            LineOffset = new float[Count];
            float limit = Mathf.Max(0f, HalfWidth - (CarController.Size.x * 0.5f + LineEdgeMargin)); // car half width + safety from the edge
            foreach (int k in LineScales)
                for (int iter = 0; iter < LineIterations; iter++)
                    for (int j = 0; j < Count; j++)
                    {
                        Vector3 mid = (LinePoint(j - k) + LinePoint(j + k)) * 0.5f;
                        float target = Vector3.Dot(mid - Center[j], Right[j]);
                        LineOffset[j] = Mathf.Clamp(Mathf.Lerp(LineOffset[j], target, LineStiffness), -limit, limit);
                    }
        }

        /// <summary>
        /// Speed profile on the racing line, m/s per sample:
        /// <list type="number">
        /// <item>corner: v = √(μg / κ), κ = curvature of the circle through three line points (lateral grip = centripetal force);</item>
        /// <item>crest: v = √(g R) - over a convex hump the car would leave the ground above it (no launch, no flip);</item>
        /// <item>capped at topSpeed x the surface's max speed, at least ProfileMinSpeed;</item>
        /// <item>braking pass backwards and acceleration pass forwards: v² ≤ v_next² + 2 a s, so every corner is reached slow
        /// enough and left no faster than the car can accelerate.</item>
        /// </list>
        /// </summary>
        float[] SpeedProfile(SurfaceDef[] surfaces, CarTuning car, float topSpeed)
        {
            const float g = Gravity;
            var speed = new float[Count];
            var mu = new float[Count];
            for (int j = 0; j < Count; j++)
            {
                var s = surfaces[(int)Surface[j]];
                mu[j] = car.grip * s.grip * CornerGripShare;
                Vector3 a = LinePoint(j - CurvatureSpan), b = LinePoint(j), c = LinePoint(j + CurvatureSpan);
                a.y = b.y = c.y = 0f;
                float area2 = Mathf.Abs(Vector3.Cross(b - a, c - b).y);
                float kappa = 2f * area2 / Mathf.Max(Vector3.Distance(a, b) * Vector3.Distance(b, c) * Vector3.Distance(a, c), 1e-4f); // 1/R
                float vCorner = kappa > 1e-5f ? Mathf.Sqrt(mu[j] * g / kappa) : float.MaxValue;
                const int kv = CrestSpan;
                float h = kv * Step;
                float yCurv = (Center[Wrap(j + kv)].y - 2f * Center[j].y + Center[Wrap(j - kv)].y) / (h * h); // y'' = -1/R on a crest
                float vCrest = yCurv < -1e-4f ? Mathf.Sqrt(g / -yCurv) : float.MaxValue;
                speed[j] = Mathf.Max(ProfileMinSpeed, Mathf.Min(Mathf.Min(vCorner, vCrest), topSpeed * s.maxSpeed));
            }
            for (int rep = 0; rep < 2; rep++)
                for (int j = Count - 1; j >= 0; j--)
                {
                    float dec = Mathf.Min(car.brakeDeceleration, mu[j] * g) * ProfileBrakeShare;
                    speed[j] = Mathf.Min(speed[j], Mathf.Sqrt(speed[Wrap(j + 1)] * speed[Wrap(j + 1)] + 2f * dec * Step));
                }
            for (int rep = 0; rep < 2; rep++)
                for (int j = 0; j < Count; j++)
                {
                    float acc = Mathf.Min(car.acceleration, mu[j] * g) * ProfileAccelShare;
                    speed[j] = Mathf.Min(speed[j], Mathf.Sqrt(speed[Wrap(j - 1)] * speed[Wrap(j - 1)] + 2f * acc * Step));
                }
            return speed;
        }

        // ---------------------------------------------------------------------------------------------------------
        // Geometry
        // ---------------------------------------------------------------------------------------------------------

        void BuildGeometry(SurfaceDef[] surfaces)
        {
            var theme = Theme.For(Map.env);
            BuildTerrain(theme);

            // Road: one mesh + collider per run of equal surface.
            int start = 0;
            while (start < Count && Surface[start] == Surface[0]) start++;
            if (start == Count) start = 0;
            for (int k = 0, runStart = start; k < Count; k++)
            {
                int j = Wrap(start + k + 1);
                if (Surface[j] != Surface[Wrap(runStart)] || k == Count - 1)
                {
                    int runEnd = start + k + 1;
                    var type = Surface[Wrap(runStart)];
                    var go = Strip("Road_" + type, runStart, runEnd, -HalfWidth, HalfWidth, 0.03f, surfaces[(int)type].color, null);
                    go.AddComponent<MeshCollider>();
                    go.AddComponent<SurfaceTag>().type = type;
                    runStart = runEnd;
                }
            }

            // Mud and dirt get darker puddles / ruts so the surface reads at a glance (visual only).
            var puddleRnd = new System.Random(Map.name.GetHashCode() ^ 77);
            for (int j = 0; j < Count; j += 7)
            {
                var type = Surface[j];
                if (Crossing[j] || (type != SurfaceType.Mud && type != SurfaceType.Dirtway)) continue;
                if (puddleRnd.NextDouble() < 0.4) continue;
                float lat = ((float)puddleRnd.NextDouble() * 2f - 1f) * (HalfWidth - 1.5f);
                var puddle = Mats.Prim(PrimitiveType.Cylinder, transform, Center[j] + Right[j] * lat + Vector3.up * 0.045f,
                    new Vector3(1.5f + (float)puddleRnd.NextDouble() * 2.5f, 0.005f, 2.5f + (float)puddleRnd.NextDouble() * 4f),
                    surfaces[(int)type].color * (type == SurfaceType.Mud ? 0.6f : 0.75f), "Puddle");
                puddle.rotation = Quaternion.LookRotation(Tangent[j]);
            }

            // Run-off (grass, drivable, at road height so elevated sections don't drop off), skipped where the loop crosses itself.
            foreach (float side in new[] { -1f, 1f })
            {
                var go = Strip("Runoff", 0, Count, side < 0 ? -HalfWidth - Runoff : HalfWidth, side < 0 ? -HalfWidth : HalfWidth + Runoff, 0.02f, theme.runoff, Crossing);
                go.AddComponent<MeshCollider>();
                go.AddComponent<SurfaceTag>().type = SurfaceType.Grass;
            }
            // Where the loop crosses itself the run-off lies on the other road: fill it with road, no hole, no grass trap.
            int cross = System.Array.IndexOf(Crossing, true);
            if (cross >= 0)
            {
                var notCrossing = new bool[Count];
                for (int j = 0; j < Count; j++) notCrossing[j] = !Crossing[j];
                var type = Surface[cross];
                foreach (float side in new[] { -1f, 1f })
                {
                    var go = Strip("RunoffCrossing", 0, Count, side < 0 ? -HalfWidth - Runoff : HalfWidth, side < 0 ? -HalfWidth : HalfWidth + Runoff, 0.02f, surfaces[(int)type].color, notCrossing);
                    go.AddComponent<MeshCollider>();
                    go.AddComponent<SurfaceTag>().type = type;
                }
            }

            // Markings.
            Strip("EdgeL", 0, Count, -HalfWidth + 0.2f, -HalfWidth + 0.45f, 0.04f, theme.edgeLine, Crossing);
            Strip("EdgeR", 0, Count, HalfWidth - 0.45f, HalfWidth - 0.2f, 0.04f, theme.edgeLine, Crossing);
            if (theme.centerDashes)
                for (int j = 0; j < Count; j += 6)
                    if (!Crossing[j]) Strip("Dash", j, j + 2, -0.12f, 0.12f, 0.045f, new Color(1f, 0.82f, 0.15f), null);
            for (int stripe = 0; stripe < 8; stripe++)
                Strip("Start" + stripe, 0, 1, -HalfWidth + stripe * HalfWidth / 4f, -HalfWidth + (stripe + 1) * HalfWidth / 4f, 0.05f, stripe % 2 == 0 ? Color.white : Color.black, null);

            BuildCheckpointGates();
            BuildWalls(theme);
            BuildProps(theme);
        }

        GameObject Strip(string name, int from, int to, float left, float right, float y, Color color, bool[] skip)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            bool open = false;
            for (int k = from; k <= to; k++)
            {
                int j = Wrap(k);
                bool skipped = skip != null && (skip[j] || skip[Wrap(k - 1)]);
                verts.Add(Center[j] + Right[j] * left + Vector3.up * y);
                verts.Add(Center[j] + Right[j] * right + Vector3.up * y);
                if (k > from && open && !skipped)
                {
                    int v = verts.Count - 4;
                    tris.AddRange(new[] { v, v + 2, v + 1, v + 1, v + 2, v + 3 });
                }
                open = true;
            }
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = Mats.Lit(color);
            return go;
        }

        /// <summary>Checkpoint gates (the game's checkpoint triggers give order points).</summary>
        void BuildCheckpointGates()
        {
            foreach (float d in CheckpointDistances)
            {
                Sample(d, out var c, out var r, out _, out int idx);
                var gate = new GameObject("Checkpoint").transform;
                gate.SetParent(transform, false);
                gate.position = c;
                gate.rotation = Quaternion.LookRotation(Tangent[idx]);
                float w = HalfWidth + 0.6f;
                var color = new Color(1f, 0.8f, 0.1f);
                Mats.Prim(PrimitiveType.Cube, gate, new Vector3(-w, 2f, 0f), new Vector3(0.4f, 4f, 0.4f), color);
                Mats.Prim(PrimitiveType.Cube, gate, new Vector3(w, 2f, 0f), new Vector3(0.4f, 4f, 0.4f), color);
                Mats.Prim(PrimitiveType.Cube, gate, new Vector3(0f, 4f, 0f), new Vector3(2f * w + 0.4f, 0.6f, 0.4f), color);
                Strip("CheckpointLine", idx, idx + 1, -HalfWidth, HalfWidth, 0.045f, new Color(1f, 0.85f, 0.2f), null);
            }
        }

        /// <summary>Invisible wall boxes at the outer run-off edge (like the game's SM_RoadBarrier_Invisible) + a visible red/white barrier.</summary>
        void BuildWalls(Theme theme)
        {
            var walls = new GameObject("Walls").transform;
            walls.SetParent(transform, false);
            float offset = HalfWidth + Runoff;
            var physics = new PhysicsMaterial("Wall") { dynamicFriction = 0.05f, staticFriction = 0.05f, bounciness = 0.2f, frictionCombine = PhysicsMaterialCombine.Minimum };
            var vertsA = new List<Vector3>(); var trisA = new List<int>();
            var vertsB = new List<Vector3>(); var trisB = new List<int>();
            const int span = 3;
            foreach (float side in new[] { -1f, 1f })
                for (int k = 0; k < Count; k += span)
                {
                    int i = Wrap(k), j = Wrap(k + span);
                    Vector3 a = Center[i] + Right[i] * offset * side;
                    Vector3 b = Center[j] + Right[j] * offset * side;
                    if (WallBlocked(a) || WallBlocked(b)) continue;
                    var box = new GameObject("WallCollider").transform;
                    box.SetParent(walls, false);
                    box.position = (a + b) * 0.5f + Vector3.up * WallHeight * 0.5f + Right[i] * side * 0.5f;
                    box.rotation = Quaternion.LookRotation(b - a);
                    var col = box.gameObject.AddComponent<BoxCollider>();
                    col.size = new Vector3(1f, WallHeight + 0.6f, Vector3.Distance(a, b) + 0.6f);
                    col.sharedMaterial = physics;

                    bool red = (k / span) % 2 == 0;
                    var verts = red ? vertsA : vertsB;
                    var tris = red ? trisA : trisB;
                    Vector3 h = Vector3.up * 0.9f, o = Right[i] * side * 0.4f;
                    Quad(verts, tris, a, a + h, b, b + h, -Right[i] * side); // face toward the road
                    Quad(verts, tris, a + h, a + o + h, b + h, b + o + h, Vector3.up);
                }
            Barrier("BarrierA", vertsA, trisA, theme.barrierA, walls);
            Barrier("BarrierB", vertsB, trisB, theme.barrierB, walls);
        }


        /// <summary>Quad with its own vertices, wound so its normal faces <paramref name="facing"/> (no averaged black normals).</summary>
        static void Quad(List<Vector3> verts, List<int> tris, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 facing)
        {
            int v = verts.Count;
            verts.AddRange(new[] { p0, p1, p2, p3 });
            bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), facing) < 0f;
            tris.AddRange(flip ? new[] { v, v + 2, v + 1, v + 1, v + 2, v + 3 } : new[] { v, v + 1, v + 2, v + 2, v + 1, v + 3 });
        }
        void Barrier(string name, List<Vector3> verts, List<int> tris, Color color, Transform parent)
        {
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = Mats.Lit(color);
        }

        /// <summary>A wall point on top of another part of the loop (a crossing) would close the road.</summary>
        bool WallBlocked(Vector3 p)
        {
            float reach = HalfWidth + Runoff - 0.5f;
            for (int i = 0; i < Count; i += 2)
            {
                Vector3 d = p - Center[i];
                if (Mathf.Abs(d.y) < 4f && d.x * d.x + d.z * d.z < reach * reach) return true;
            }
            return false;
        }

        /// <summary>Terrain that follows the track: just under the road, lowest road at crossings (so bridges stay bridges), easing out.</summary>
        void BuildTerrain(Theme theme)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var p in Center) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            const float margin = 140f;
            min -= new Vector3(margin, 0f, margin);
            max += new Vector3(margin, 0f, margin);
            const int res = 257;
            float sizeX = max.x - min.x, sizeZ = max.z - min.z;
            float baseH = min.y - 3f, range = Mathf.Max(max.y - baseH + 3f, 10f);
            float corridor = HalfWidth + Runoff + 2f;
            var heights = new float[res, res];
            for (int zi = 0; zi < res; zi++)
                for (int xi = 0; xi < res; xi++)
                {
                    float x = min.x + sizeX * xi / (res - 1), z = min.z + sizeZ * zi / (res - 1);
                    float nearest = float.MaxValue, nearH = baseH, corridorH = float.MaxValue;
                    for (int i = 0; i < Count; i += 2)
                    {
                        float dx = Center[i].x - x, dz = Center[i].z - z, d2 = dx * dx + dz * dz;
                        if (d2 < nearest) { nearest = d2; nearH = Center[i].y; }
                        if (d2 < corridor * corridor && Center[i].y < corridorH) corridorH = Center[i].y;
                    }
                    float dist = Mathf.Sqrt(nearest);
                    float h = corridorH < float.MaxValue ? corridorH - 0.3f
                        : Mathf.Lerp(nearH - 0.3f, Mathf.Lerp(nearH, baseH + 3f, 0.6f), Mathf.SmoothStep(0f, 1f, (dist - corridor) / 70f));
                    heights[zi, xi] = Mathf.Clamp01((h - baseH) / range);
                }
            var data = new TerrainData { heightmapResolution = res };
            data.size = new Vector3(sizeX, range, sizeZ);
            data.SetHeights(0, 0, heights);
            var tex = new Texture2D(4, 4);
            var px = new Color[16];
            for (int i = 0; i < 16; i++) px[i] = theme.ground * (0.92f + 0.08f * (i % 3));
            tex.SetPixels(px);
            tex.Apply();
            data.terrainLayers = new[] { new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(6f, 6f) } };
            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(min.x, baseH, min.z);
            go.AddComponent<SurfaceTag>().type = SurfaceType.Grass;
            _terrain = go.GetComponent<Terrain>();
            _terrain.drawInstanced = false;
            var terrainMat = Resources.Load<Material>("RacingTerrain"); // keeps the terrain shader in a player build
            if (terrainMat != null) _terrain.materialTemplate = terrainMat;
        }

        Terrain _terrain;

        /// <summary>Low-poly scenery by environment, outside the walls, never on the road.</summary>
        void BuildProps(Theme theme)
        {
            var root = new GameObject("Props").transform;
            root.SetParent(transform, false);
            var rnd = new System.Random(Map.name.GetHashCode());
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var p in Center) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float clear = HalfWidth + Runoff + 4f;
            int placed = 0;
            for (int attempt = 0; attempt < 2000 && placed < 220; attempt++)
            {
                float x = Mathf.Lerp(min.x - 60f, max.x + 60f, (float)rnd.NextDouble());
                float z = Mathf.Lerp(min.z - 60f, max.z + 60f, (float)rnd.NextDouble());
                float nearest = float.MaxValue;
                for (int i = 0; i < Count; i += 3)
                {
                    float dx = Center[i].x - x, dz = Center[i].z - z;
                    nearest = Mathf.Min(nearest, dx * dx + dz * dz);
                }
                float dist = Mathf.Sqrt(nearest);
                if (dist < clear || dist > 70f) continue;
                var pos = new Vector3(x, 0f, z);
                pos.y = _terrain != null ? _terrain.SampleHeight(pos) + _terrain.transform.position.y : 0f;
                theme.Spawn(root, pos, rnd, dist < clear + 10f);
                placed++;
            }
        }
    }

    /// <summary>Look of a racing_unity environment (ground, barriers, props), approximated with primitives.</summary>
    public class Theme
    {
        public Color ground, runoff, edgeLine = Color.white, barrierA = new Color(0.85f, 0.15f, 0.15f), barrierB = Color.white;
        public bool centerDashes;
        public System.Action<Transform, Vector3, System.Random, bool> Spawn;

        static Transform P(PrimitiveType t, Transform root, Vector3 pos, Vector3 scale, Color c) => Mats.Prim(t, root, pos + Vector3.up * scale.y * 0.5f, scale, c);
        static float R(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);

        static void Tree(Transform root, Vector3 p, System.Random r, Color leaf)
        {
            float h = R(r, 3f, 6f);
            P(PrimitiveType.Cylinder, root, p, new Vector3(0.5f, h * 0.5f, 0.5f), new Color(0.4f, 0.27f, 0.15f));
            float s = R(r, 3f, 5f);
            Mats.Prim(PrimitiveType.Sphere, root, p + Vector3.up * (h + s * 0.3f), new Vector3(s, s * 1.2f, s), leaf);
        }

        static void Palm(Transform root, Vector3 p, System.Random r)
        {
            float h = R(r, 6f, 9f);
            P(PrimitiveType.Cylinder, root, p, new Vector3(0.4f, h * 0.5f, 0.4f), new Color(0.55f, 0.42f, 0.25f));
            Mats.Prim(PrimitiveType.Sphere, root, p + Vector3.up * h, new Vector3(4.5f, 1.2f, 4.5f), new Color(0.25f, 0.55f, 0.2f));
        }

        static void Building(Transform root, Vector3 p, System.Random r, Color[] palette)
        {
            float w = R(r, 8f, 16f), d = R(r, 8f, 16f), h = R(r, 8f, 30f);
            var t = P(PrimitiveType.Cube, root, p, new Vector3(w, h, d), palette[r.Next(palette.Length)]);
            t.rotation = Quaternion.Euler(0f, R(r, 0f, 90f), 0f);
        }

        public static Theme For(string env)
        {
            var leaf = new Color(0.22f, 0.5f, 0.2f);
            switch (env)
            {
                case "Downtown":
                    return new Theme
                    {
                        ground = new Color(0.55f, 0.56f, 0.55f), runoff = new Color(0.62f, 0.62f, 0.6f), centerDashes = true,
                        Spawn = (root, p, r, near) =>
                        {
                            if (near && r.NextDouble() < 0.6) Tree(root, p, r, leaf);
                            else Building(root, p, r, new[] { new Color(0.7f, 0.35f, 0.28f), new Color(0.6f, 0.6f, 0.62f), new Color(0.82f, 0.76f, 0.62f), new Color(0.45f, 0.5f, 0.58f) });
                        }
                    };
                case "Formula":
                    return new Theme
                    {
                        ground = new Color(0.35f, 0.6f, 0.28f), runoff = new Color(0.4f, 0.65f, 0.32f), barrierA = new Color(0.9f, 0.1f, 0.1f),
                        Spawn = (root, p, r, near) =>
                        {
                            if (near && r.NextDouble() < 0.5)
                                for (int i = 0; i < 3; i++) P(PrimitiveType.Cylinder, root, p + Vector3.up * i * 0.5f, new Vector3(1.2f, 0.25f, 1.2f), new Color(0.08f, 0.08f, 0.08f));
                            else if (r.NextDouble() < 0.35)
                            {
                                var g = P(PrimitiveType.Cube, root, p, new Vector3(24f, 6f, 8f), new Color(0.75f, 0.75f, 0.8f));
                                g.rotation = Quaternion.Euler(0f, R(r, 0f, 180f), 0f);
                                Mats.Prim(PrimitiveType.Cube, g, new Vector3(0f, 0.6f, 0f), new Vector3(1.02f, 0.15f, 1.1f), new Color(0.15f, 0.3f, 0.7f));
                            }
                            else Tree(root, p, r, leaf);
                        }
                    };
                case "Forest":
                    return new Theme { ground = new Color(0.25f, 0.45f, 0.2f), runoff = new Color(0.3f, 0.5f, 0.22f), barrierA = new Color(0.55f, 0.38f, 0.2f), barrierB = new Color(0.45f, 0.3f, 0.16f),
                        Spawn = (root, p, r, near) => { if (r.NextDouble() < 0.85) Tree(root, p, r, new Color(0.15f, R(r, 0.35f, 0.5f), 0.15f)); else Mats.Prim(PrimitiveType.Sphere, root, p, Vector3.one * R(r, 2f, 5f), new Color(0.5f, 0.5f, 0.5f)); } };
                case "Factory":
                    return new Theme { ground = new Color(0.45f, 0.42f, 0.38f), runoff = new Color(0.5f, 0.47f, 0.42f), barrierA = new Color(0.95f, 0.75f, 0.1f), barrierB = new Color(0.15f, 0.15f, 0.15f),
                        Spawn = (root, p, r, near) =>
                        {
                            if (r.NextDouble() < 0.5) P(PrimitiveType.Cylinder, root, p, new Vector3(R(r, 4f, 8f), R(r, 4f, 9f), R(r, 4f, 8f)), new Color(0.6f, 0.62f, 0.65f));
                            else Building(root, p, r, new[] { new Color(0.5f, 0.45f, 0.4f), new Color(0.4f, 0.42f, 0.45f), new Color(0.6f, 0.35f, 0.25f) });
                        } };
                case "Mud":
                    return new Theme { ground = new Color(0.42f, 0.33f, 0.22f), runoff = new Color(0.45f, 0.36f, 0.24f), barrierA = new Color(0.5f, 0.35f, 0.2f), barrierB = new Color(0.6f, 0.45f, 0.28f),
                        Spawn = (root, p, r, near) => { if (r.NextDouble() < 0.6) Palm(root, p, r); else P(PrimitiveType.Cube, root, p, new Vector3(5f, 3f, 5f), new Color(0.5f, 0.38f, 0.25f)); } };
                case "Desert":
                    return new Theme { ground = new Color(0.86f, 0.72f, 0.48f), runoff = new Color(0.82f, 0.68f, 0.45f), barrierA = new Color(0.75f, 0.55f, 0.35f), barrierB = new Color(0.9f, 0.85f, 0.75f),
                        Spawn = (root, p, r, near) =>
                        {
                            double k = r.NextDouble();
                            if (k < 0.4) Palm(root, p, r);
                            else if (k < 0.75) Mats.Prim(PrimitiveType.Sphere, root, p, new Vector3(R(r, 10f, 20f), R(r, 3f, 6f), R(r, 10f, 20f)), new Color(0.9f, 0.75f, 0.5f));
                            else P(PrimitiveType.Cube, root, p, new Vector3(R(r, 4f, 8f), R(r, 3f, 5f), R(r, 4f, 8f)), new Color(0.8f, 0.62f, 0.42f));
                        } };
                case "Nordic":
                    return new Theme { ground = new Color(0.95f, 0.97f, 1f), runoff = new Color(0.72f, 0.78f, 0.86f), edgeLine = new Color(0.15f, 0.3f, 0.75f), barrierA = new Color(0.2f, 0.35f, 0.7f), barrierB = new Color(0.9f, 0.2f, 0.2f),
                        Spawn = (root, p, r, near) => { if (r.NextDouble() < 0.7) Tree(root, p, r, new Color(0.15f, 0.32f, 0.22f)); else Mats.Prim(PrimitiveType.Sphere, root, p, Vector3.one * R(r, 3f, 7f), new Color(0.5f, 0.52f, 0.56f)); } };
                default: // Country
                    return new Theme { ground = new Color(0.45f, 0.65f, 0.3f), runoff = new Color(0.5f, 0.68f, 0.33f), barrierA = new Color(0.6f, 0.42f, 0.25f), barrierB = new Color(0.85f, 0.8f, 0.7f),
                        Spawn = (root, p, r, near) =>
                        {
                            double k = r.NextDouble();
                            if (k < 0.55) Tree(root, p, r, leaf);
                            else if (k < 0.8) P(PrimitiveType.Cylinder, root, p, new Vector3(1.6f, 0.8f, 1.6f), new Color(0.9f, 0.78f, 0.35f));
                            else { var h = P(PrimitiveType.Cube, root, p, new Vector3(7f, 4f, 9f), new Color(0.9f, 0.88f, 0.82f)); Mats.Prim(PrimitiveType.Cube, h, new Vector3(0f, 0.6f, 0f), new Vector3(1.05f, 0.3f, 1.05f), new Color(0.65f, 0.2f, 0.15f)); }
                        } };
            }
        }
    }
}
