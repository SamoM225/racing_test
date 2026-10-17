using System;
using UnityEngine;

namespace Racing
{
    /// <summary>Shared by every car (player and bots drive identical physics). Editable live on the RaceManager.</summary>
    [Serializable]
    public class CarTuning
    {
        [Header("Engine")]
        [Tooltip("m/s, physical limit (the driving logic sets the actual target speed)")] public float maxSpeed = 52f;
        [Tooltip("m/s² at standstill, fades to 0 at max speed")] public float acceleration = 13f;
        [Tooltip("m/s²")] public float brakeDeceleration = 24f;
        [Tooltip("m/s")] public float reverseMaxSpeed = 8f;
        [Tooltip("deceleration = value x speed²")] public float airDrag = 0.0007f;

        [Header("Steering")]
        [Tooltip("deg")] public float maxSteerAngle = 34f;
        [Tooltip("Share of steering left at max speed")] [Range(0.1f, 1f)] public float highSpeedSteer = 0.42f;
        [Tooltip("Steering lock per second (x max angle)")] public float steerResponse = 7f;

        [Header("Grip & slide")]
        [Tooltip("Tyre friction coefficient on asphalt (x surface grip)")] public float grip = 1.45f;
        [Tooltip("Rear grip relative to front; < 1 = the rear steps out first (drifts)")] [Range(0.5f, 1.2f)] public float rearGrip = 0.92f;
        [Tooltip("0 = front wheel drive, 1 = rear wheel drive (power oversteer)")] [Range(0f, 1f)] public float driveBias = 0.65f;
        [Tooltip("How hard the tyre fights lateral slip (1 = kills it in one step)")] [Range(0.1f, 1f)] public float lateralStiffness = 0.6f;
        [Tooltip("Arcade help: turns the velocity toward the nose while sliding (keeps speed in drifts)")] [Range(0f, 4f)] public float driftAssist = 1.4f;
        [Tooltip("Arcade help: steering controls the yaw rate while drifting")] [Range(0f, 6f)] public float driftControl = 2.5f;
        [Tooltip("deg; slip angle from which a slide counts as a drift")] public float driftAngle = 10f;
        [Tooltip("N per (m/s)²")] public float downforce = 2.5f;

        [Header("Suspension")]
        public float suspensionLength = 0.45f;
        public float wheelRadius = 0.36f;
        public float spring = 22000f;
        public float damper = 2600f;

        [Header("Boost")]
        [Tooltip("m/s² extra while boosting")] public float boostAcceleration = 11f;
        [Tooltip("Energy pips per second while drifting at 30°")] public float driftEnergy = 0.5f;
        [Tooltip("Seconds of drift for mini-turbo tiers 1/2/3")] public Vector3 miniTurboTiers = new Vector3(0.8f, 1.6f, 2.6f);
        [Tooltip("Free boost seconds per mini-turbo tier")] public float miniTurboPerTier = 0.35f;
    }

    /// <summary>
    /// Arcade raycast car: spring-damper suspension per wheel, friction-circle tyres scaled by the surface,
    /// kinetic friction once a tyre breaks loose (this is what makes ice/dirt slide and drifts hold),
    /// mini-turbo from drifting. It only knows steer + throttle; who decides them is the driving logic.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : MonoBehaviour
    {
        static readonly Vector3 BaseSize = new Vector3(2.1f, 0.9f, 4.1f); // Formula1 from the original game
        /// <summary>Car size factor for every car (RC look; menu). The whole car scales: body, collider, suspension.</summary>
        public static float Scale = 1f;
        public static Vector3 Size => BaseSize * Scale;
        public static float WheelBase => 2.8f * Scale;

        public CarTuning tuning;
        public SurfaceDef[] surfaces;

        public Rigidbody Body { get; private set; }
        public float ForwardSpeed { get; private set; }
        public float Speed { get; private set; }
        public float SlipAngle { get; private set; }
        public float YawRate { get; private set; }
        public bool Grounded { get; private set; }
        public bool IsDrifting { get; private set; }
        public bool IsBoosting { get; private set; }
        public SurfaceDef Surface { get; private set; }
        public float CurrentMaxSteer { get; private set; }
        public float DriftTime { get; private set; }

        // Written by the race / driving logic.
        [NonSerialized] public float Energy;
        [NonSerialized] public float EnergyMax = 10f;
        [NonSerialized] public float BurstTimer;
        [NonSerialized] public float Draft;
        [NonSerialized] public float Handicap = 1f;
        [NonSerialized] public float DashTimer;
        [NonSerialized] public bool Hold;
        /// <summary>s left of a knock: the tyres are broken loose (the car slides and turns from the hit), the steering help is down.</summary>
        [NonSerialized] public float Stun;
        [NonSerialized] public float SinceKnock = 99f; // s since the last knock (test diagnostics)
        public bool SideDashing => _sideDashLeft > 0f;

        public event Action<float> WallHit;              // impact speed
        public event Action<int> MiniTurbo;              // tier 1..3
        public event Action<CarController> DashHit;      // rammed this car while dashing
        public event Action<Vector3, float> Impact;      // contact point, strength (m/s): sparks, camera shake

        readonly Vector3[] _wheelLocal = new Vector3[4];
        readonly float[] _lastCompression = new float[4];
        readonly SurfaceDef[] _wheelSurface = new SurfaceDef[4];
        readonly Transform[] _wheelVis = new Transform[4];
        readonly TrailRenderer[] _skid = new TrailRenderer[2];
        float _steerAngle, _wheelSpin;
        float _sideDashLeft, _sideDashTime, _sideDashPeak, _sideDashSign, _hitGuard;
        Transform _bodyVis;
        Vector3 _lastVel;
        float _roll, _pitch;

        public void Setup(CarTuning carTuning, SurfaceDef[] surfaceDefs, Color color)
        {
            tuning = carTuning;
            surfaces = surfaceDefs;
            Surface = surfaces[(int)SurfaceType.Asphalt];
            gameObject.layer = 2; // Ignore Raycast: wheels never hit cars

            Body = GetComponent<Rigidbody>();
            Body.mass = 1200f;
            Body.linearDamping = 0f;
            Body.angularDamping = 0.6f;
            Body.centerOfMass = new Vector3(0f, -0.3f * Scale, 0f);
            transform.localScale = Vector3.one * Scale;
            Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(BaseSize.x, 0.6f, BaseSize.z);
            col.center = new Vector3(0f, 0.25f, 0f);
            col.material = new PhysicsMaterial("Car")
            {
                dynamicFriction = 0.1f, staticFriction = 0.1f, bounciness = 0.15f,
                frictionCombine = PhysicsMaterialCombine.Minimum, bounceCombine = PhysicsMaterialCombine.Average
            };
            col.providesContacts = true;
            if (!_listening) { Physics.ContactEvent += OnContacts; _listening = true; }

            float x = 0.85f, z = 1.4f; // local (unscaled) units
            _wheelLocal[0] = new Vector3(-x, 0f, z);
            _wheelLocal[1] = new Vector3(x, 0f, z);
            _wheelLocal[2] = new Vector3(-x, 0f, -z);
            _wheelLocal[3] = new Vector3(x, 0f, -z);
            BuildVisuals(color);
        }

        void BuildVisuals(Color color)
        {
            var t = transform;
            _bodyVis = new GameObject("BodyVis").transform; // leans on the springs (roll / pitch); wheels stay on the ground
            _bodyVis.SetParent(t, false);
            var b = _bodyVis;
            Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.3f, 0f), new Vector3(1.5f, 0.45f, BaseSize.z), color, "Body");
            Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.18f, 1.75f), new Vector3(BaseSize.x, 0.12f, 0.5f), color * 0.8f, "FrontWing");
            Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.85f, -1.8f), new Vector3(BaseSize.x, 0.1f, 0.45f), color * 0.8f, "RearWing");
            Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.65f, -0.2f), new Vector3(0.7f, 0.35f, 1.1f), new Color(0.1f, 0.1f, 0.12f), "Cockpit");
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject("Wheel" + i).transform;
                pivot.SetParent(t, false);
                pivot.localPosition = _wheelLocal[i];
                var tyre = Mats.Prim(PrimitiveType.Cylinder, pivot, Vector3.zero, new Vector3(0.72f, 0.18f, 0.72f), new Color(0.08f, 0.08f, 0.08f));
                tyre.localRotation = Quaternion.Euler(0f, 0f, 90f);
                _wheelVis[i] = pivot;
            }
            for (int i = 0; i < 2; i++)
            {
                var g = new GameObject("Skid" + i);
                g.transform.SetParent(t, false);
                var tr = g.AddComponent<TrailRenderer>();
                tr.time = 2.5f;
                tr.minVertexDistance = 0.4f;
                tr.widthMultiplier = 0.32f;
                tr.alignment = LineAlignment.TransformZ;
                tr.sharedMaterial = Mats.Unlit(new Color(0.05f, 0.05f, 0.05f, 0.75f));
                tr.emitting = false;
                tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                g.transform.rotation = Quaternion.LookRotation(Vector3.down);
                _skid[i] = tr;
            }
        }

        public void ResetMotion()
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            DriftTime = 0f;
            DashTimer = 0f;
            Stun = 0f;
            _sideDashLeft = 0f;
            _lastVel = Vector3.zero;
            for (int i = 0; i < 2; i++) _skid[i].Clear();
        }

        /// <summary>
        /// Side dash: a sideways lunge that covers <paramref name="distance"/> m in <paramref name="time"/> s. The thrust
        /// follows a linearly fading sideways speed (racing_unity: Lerp(slideSpeed, 0, t)); the tyres slide while it pushes
        /// (they would eat the sideways motion in one step otherwise) and the nose turns into it a little.
        /// </summary>
        public void SideDash(float side, float distance, float time)
        {
            _sideDashSign = Mathf.Sign(side);
            _sideDashTime = _sideDashLeft = Mathf.Max(time, 0.05f);
            _sideDashPeak = 2f * distance / _sideDashTime;
            Body.AddForce(transform.right * _sideDashSign * _sideDashPeak * 0.5f, ForceMode.VelocityChange); // the kick; the thrust carries it
            Body.AddTorque(transform.up * _sideDashSign * 0.5f, ForceMode.VelocityChange);
        }

        public void CancelSideDash() => _sideDashLeft = 0f;

        /// <summary>One physics step: steer -1..1, throttle -1 (brake/reverse) .. 1.</summary>
        public void Simulate(float steerInput, float throttleInput, bool boosting, float dt)
        {
            var c = tuning;
            var t = transform;
            Vector3 up = t.up;
            Vector3 vel = Body.linearVelocity;
            Vector3 velFlat = Vector3.ProjectOnPlane(vel, up);
            ForwardSpeed = Vector3.Dot(vel, t.forward);
            Speed = velFlat.magnitude;
            YawRate = Vector3.Dot(Body.angularVelocity, up);
            DashTimer = Mathf.Max(0f, DashTimer - dt);
            Stun = Mathf.Max(0f, Stun - dt);
            SinceKnock += dt;
            _hitGuard = Mathf.Max(0f, _hitGuard - dt);
            // Tyres let go while a side dash pushes (fading with it), and partly after a knock (the car skids with the hit;
            // more would let it spin round - the yaw damping below stays on for that).
            float stun01 = Mathf.Clamp01(Stun / 0.5f);
            float latGrip = 1f - Mathf.Max(_sideDashLeft > 0f ? 0.85f * _sideDashLeft / _sideDashTime : 0f, 0.3f * stun01);
            float allGrip = 1f - 0.4f * stun01;

            float speed01 = Mathf.Clamp01(Speed / c.maxSpeed);
            CurrentMaxSteer = c.maxSteerAngle * Mathf.Lerp(1f, c.highSpeedSteer, speed01);
            _steerAngle = Mathf.MoveTowards(_steerAngle, Mathf.Clamp(steerInput, -1f, 1f) * CurrentMaxSteer, c.steerResponse * c.maxSteerAngle * dt);

            BurstTimer = Mathf.Max(0f, BurstTimer - dt);
            IsBoosting = (boosting || BurstTimer > 0f) && !Hold;

            float topSpeed = c.maxSpeed * AverageSurfaceMaxSpeed() * (1f + 0.06f * Draft);
            float throttle = Mathf.Clamp(throttleInput, -1f, 1f);
            float driveAccel = 0f, brakeAccel = 0f;
            if (throttle > 0f)
            {
                float x = Mathf.Clamp01(ForwardSpeed / topSpeed);
                driveAccel = throttle * c.acceleration * (1f - x * x);
                if (IsBoosting && ForwardSpeed < topSpeed) driveAccel += c.boostAcceleration * throttle;
            }
            else if (throttle < 0f)
            {
                if (ForwardSpeed > 0.5f) brakeAccel = -throttle * c.brakeDeceleration;
                else driveAccel = throttle * c.acceleration * 0.6f * (1f - Mathf.Clamp01(-ForwardSpeed / c.reverseMaxSpeed));
            }

            float mass = Body.mass, wheelMass = mass / 4f;
            float downforce = c.downforce * Speed * Speed;
            Vector3 com = Body.worldCenterOfMass;
            int grounded = 0;

            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                Vector3 origin = t.TransformPoint(_wheelLocal[i]);
                float rayLength = (c.suspensionLength + c.wheelRadius) * Scale;
                if (!Physics.Raycast(origin, -up, out var hit, rayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    _lastCompression[i] = 0f;
                    _wheelVis[i].localPosition = _wheelLocal[i] - Vector3.up * c.suspensionLength;
                    if (!front) _skid[i - 2].emitting = false;
                    continue;
                }
                grounded++;

                // Suspension.
                float springLength = hit.distance - c.wheelRadius * Scale;
                float compression = c.suspensionLength * Scale - springLength;
                float compressionVel = (compression - _lastCompression[i]) / dt;
                _lastCompression[i] = compression;
                float load = Mathf.Max(0f, compression * c.spring + compressionVel * c.damper);
                Body.AddForceAtPosition(up * load, origin);

                var surface = SurfaceOf(hit.collider);
                _wheelSurface[i] = surface;

                // Tyre frame on the ground plane.
                Vector3 fwd = t.forward;
                if (front) fwd = Quaternion.AngleAxis(_steerAngle, up) * fwd;
                fwd = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
                Vector3 right = Vector3.Cross(hit.normal, fwd);
                Vector3 pv = Body.GetPointVelocity(hit.point);
                float vLat = Vector3.Dot(pv, right);
                float vLong = Vector3.Dot(pv, fwd);

                // Lateral: fight the slip. Longitudinal: drive, brakes, rolling resistance.
                float latF = -vLat * wheelMass / dt * c.lateralStiffness * latGrip;
                float driveShare = front ? 1f - c.driveBias : c.driveBias;
                float longF = mass * driveAccel * driveShare * 0.5f;
                if (brakeAccel > 0f)
                    longF -= Mathf.Sign(vLong) * Mathf.Min(mass * brakeAccel * (front ? 0.6f : 0.4f) * 0.5f, Mathf.Abs(vLong) * wheelMass / dt);
                if (Hold) longF -= vLong * wheelMass / dt;
                longF -= vLong * surface.rollingDrag * wheelMass;

                // Friction circle; once the tyre slides it only has kinetic friction left (breakaway).
                float mu = c.grip * surface.grip * (front ? 1f : c.rearGrip);
                float slide = Mathf.InverseLerp(1f, 3.5f, Mathf.Abs(vLat));
                float maxF = mu * (load + downforce * 0.25f) * Mathf.Lerp(1f, surface.slideFriction, slide) * allGrip;
                var f = new Vector2(latF, longF);
                if (f.sqrMagnitude > maxF * maxF) f = f.normalized * maxF;

                // Applied at the height of the centre of mass: yaw from tyres, no roll that flips the car.
                Vector3 forcePoint = hit.point + up * Vector3.Dot(com - hit.point, up);
                Body.AddForceAtPosition(right * f.x + fwd * f.y, forcePoint);

                _wheelVis[i].localPosition = _wheelLocal[i] - Vector3.up * springLength / Scale;
                if (!front)
                {
                    var skid = _skid[i - 2];
                    skid.transform.position = hit.point + hit.normal * 0.03f;
                    skid.emitting = Mathf.Abs(vLat) > 2.5f || (brakeAccel > 0.8f * c.brakeDeceleration && Speed > 12f);
                }
            }
            Grounded = grounded >= 2;
            Surface = MajoritySurface(grounded);

            if (grounded > 0) Body.AddForce(-up * downforce);
            // Arcade self-righting while airborne or on two wheels (jumps, bumps), so small RC cars don't land on the roof.
            if (grounded < 3)
            {
                Vector3 tilt = Vector3.Cross(up, Vector3.up);
                Vector3 roll = Body.angularVelocity - Vector3.Project(Body.angularVelocity, up);
                Body.AddTorque(tilt * 16f - roll * 3f, ForceMode.Acceleration);
            }
            Body.AddForce(-velFlat * velFlat.magnitude * c.airDrag * (1f - 0.45f * Draft), ForceMode.Acceleration);

            // Side dash thrust: the sideways speed follows the fading profile (the sliding tyres still take some, this tops it up).
            if (_sideDashLeft > 0f)
            {
                Vector3 side = Vector3.ProjectOnPlane(t.right, up).normalized * _sideDashSign;
                float want = _sideDashPeak * _sideDashLeft / _sideDashTime;
                float have = Vector3.Dot(Body.linearVelocity, side);
                Body.AddForce(side * Mathf.Clamp((want - have) * 15f, 0f, 60f), ForceMode.Acceleration);
                _sideDashLeft -= dt;
            }

            SlipAngle = Speed > 3f && ForwardSpeed > 0f ? Vector3.SignedAngle(t.forward, velFlat, up) : 0f;
            // A side dash or a knock slides the car sideways on purpose: no drift (points, assist) out of that.
            bool forcedSlide = _sideDashLeft > 0f || Stun > 0f;
            IsDrifting = !forcedSlide && Grounded && Speed > 9f && Mathf.Abs(SlipAngle) > c.driftAngle && Mathf.Abs(SlipAngle) < 100f;

            if (!forcedSlide && Grounded && ForwardSpeed > 5f && Mathf.Abs(SlipAngle) > 4f)
            {
                // Arcade drift: keep momentum by bending the velocity toward the nose...
                Vector3 aligned = Vector3.Slerp(velFlat, t.forward * velFlat.magnitude, Mathf.Clamp01(c.driftAssist * 0.5f * dt));
                Body.linearVelocity = aligned + Vector3.Project(vel, up);
                // ...and let the steering set the yaw rate, so a slide stays controllable.
                if (IsDrifting)
                    Body.AddTorque(up * (steerInput * 2.2f - YawRate) * c.driftControl, ForceMode.Acceleration);
            }
            else if (Stun > 0f && Grounded) // knocked: it skids with the hit, but the yaw is held near what the steering asks (no spin round)
                Body.AddTorque(up * (Mathf.Clamp(steerInput, -0.4f, 0.4f) * 2.2f - YawRate) * c.driftControl * 1.5f, ForceMode.Acceleration);

            UpdateDriftCharge(dt);
            SpinWheels(dt);
            LeanBody(dt);
        }

        /// <summary>Body roll / pitch from the felt acceleration (corners, brakes, dashes, hits). Visual only.</summary>
        void LeanBody(float dt)
        {
            Vector3 vel = Body.linearVelocity;
            Vector3 acc = (vel - _lastVel) / dt;
            _lastVel = vel;
            float lat = Vector3.Dot(acc, transform.right), lon = Vector3.Dot(acc, transform.forward);
            float k = Mathf.Clamp01(12f * dt);
            _roll = Mathf.Lerp(_roll, Mathf.Clamp(lat * 0.45f, -9f, 9f), k);   // leans out of the turn
            _pitch = Mathf.Lerp(_pitch, Mathf.Clamp(-lon * 0.2f, -4f, 4f), k); // nose dives on the brakes
            _bodyVis.localRotation = Quaternion.Euler(_pitch, 0f, _roll);
        }

        void UpdateDriftCharge(float dt)
        {
            var c = tuning;
            if (IsDrifting)
            {
                DriftTime += dt;
                Energy = Mathf.Min(EnergyMax, Energy + c.driftEnergy * Mathf.Clamp01(Mathf.Abs(SlipAngle) / 30f) * dt);
                return;
            }
            if (DriftTime <= 0f) return;
            int tier = DriftTime >= c.miniTurboTiers.z ? 3 : DriftTime >= c.miniTurboTiers.y ? 2 : DriftTime >= c.miniTurboTiers.x ? 1 : 0;
            DriftTime = 0f;
            if (tier == 0) return;
            BurstTimer = Mathf.Max(BurstTimer, tier * c.miniTurboPerTier);
            MiniTurbo?.Invoke(tier);
        }

        void SpinWheels(float dt)
        {
            _wheelSpin += ForwardSpeed / (tuning.wheelRadius * Scale) * dt * Mathf.Rad2Deg;
            for (int i = 0; i < 4; i++)
                _wheelVis[i].localRotation = Quaternion.Euler(_wheelSpin, i < 2 ? _steerAngle : 0f, 0f);
        }

        SurfaceDef SurfaceOf(Collider col)
        {
            return col.TryGetComponent<SurfaceTag>(out var tag) ? surfaces[(int)tag.type] : surfaces[(int)SurfaceType.Asphalt];
        }

        float AverageSurfaceMaxSpeed()
        {
            float sum = 0f;
            for (int i = 0; i < 4; i++) sum += (_wheelSurface[i] ?? surfaces[(int)SurfaceType.Asphalt]).maxSpeed;
            return sum / 4f;
        }

        SurfaceDef MajoritySurface(int grounded)
        {
            if (grounded == 0) return Surface;
            SurfaceDef best = _wheelSurface[0] ?? surfaces[(int)SurfaceType.Asphalt];
            int bestCount = 0;
            for (int i = 0; i < 4; i++)
            {
                int n = 0;
                for (int j = 0; j < 4; j++) if (_wheelSurface[j] == _wheelSurface[i]) n++;
                if (n > bestCount && _wheelSurface[i] != null) { bestCount = n; best = _wheelSurface[i]; }
            }
            return best;
        }

        static bool _listening;

        /// <summary>
        /// Contacts of every physics step (Physics.ContactEvent fires in the editor tests too, unlike OnCollisionEnter).
        /// A hit is a spike of the contact impulse, not the "enter" (with speculative contacts that comes a step early, at
        /// zero impulse); closing speed from J = reduced mass x (1 + bounce) x v. Leaning on each other stays far below it.
        /// </summary>
        static void OnContacts(PhysicsScene scene, Unity.Collections.NativeArray<ContactPairHeader>.ReadOnly headers)
        {
            for (int h = 0; h < headers.Length; h++)
            {
                var header = headers[h];
                var a = header.body != null ? header.body.GetComponent<CarController>() : null;
                var b = header.otherBody != null ? header.otherBody.GetComponent<CarController>() : null;
                if (a == null && b == null) continue;
                for (int i = 0; i < header.pairCount; i++)
                {
                    ref readonly var pair = ref header.GetContactPair(i);
                    float j = pair.impulseSum.magnitude;
                    if (pair.isCollisionExit || pair.contactCount == 0 || j < 1f) continue; // not touching yet
                    Vector3 point = pair.GetContactPoint(0).position, n = pair.GetContactPoint(0).normal;
                    if (a != null && b != null)
                    {
                        if (Vector3.Dot(n, a.transform.position - b.transform.position) < 0f) n = -n; // n: into a
                        float vn = j / (a.Body.mass * b.Body.mass / (a.Body.mass + b.Body.mass) * 1.15f);
                        if (a.DashTimer > 0f) a.Ram(b, point);
                        if (b.DashTimer > 0f) b.Ram(a, point);
                        if (a.DashTimer <= 0f && b.DashTimer <= 0f) { a.Knock(point, n, vn); b.Knock(point, -n, vn); }
                    }
                    else
                    {
                        var car = a ?? b;
                        if (Mathf.Abs(n.y) > 0.6f) continue; // ground
                        if (Vector3.Dot(n, car.Body.worldCenterOfMass - point) < 0f) n = -n;
                        float vn = j / (car.Body.mass * 1.15f);
                        // Walls: PhysX bounces the car off, plus sparks / camera; no knock (a spin at a wall only chains into the next hit).
                        if (vn > 3f && car._hitGuard <= 0f) { car._hitGuard = 0.3f; car.WallHit?.Invoke(vn); car.Impact?.Invoke(point, vn * 0.5f); }
                    }
                }
            }
        }

        /// <summary>
        /// A knock: the tyres break loose for a moment (longer for a harder hit), so the car really slides and turns with the
        /// impulse PhysX gave it instead of the tyres eating it in the next step; a hit off the centre spins it.
        /// </summary>
        void Knock(Vector3 point, Vector3 n, float vn)
        {
            if (vn < 1.5f || _hitGuard > 0f) return;
            _hitGuard = 0.3f; // one hit = one knock (the impulse is spread over a few steps)
            SinceKnock = 0f;
            float k = Mathf.Clamp01((vn - 1.5f) / 8f);
            Stun = Mathf.Max(Stun, Mathf.Lerp(0.15f, 0.5f, k)); // felt, but the car is driving again within half a second
            Vector3 up = transform.up;
            Vector3 lever = Vector3.ProjectOnPlane(point - Body.worldCenterOfMass, up);
            float spin = Vector3.Dot(Vector3.Cross(lever, Vector3.ProjectOnPlane(n, up)), up); // lever arm x push direction
            Body.AddTorque(up * Mathf.Clamp(spin * vn * 0.06f, -1f, 1f), ForceMode.VelocityChange);
            Impact?.Invoke(point, vn);
        }

        /// <summary>
        /// Dash ram (racing_unity DashCollidedWith): whatever part touches, the hit car is shoved away and spins from where it
        /// was hit, its own dash is cancelled; the rammer bounces back a little. Dodging a dash is nearly impossible, as in the original.
        /// </summary>
        void Ram(CarController other, Vector3 point)
        {
            Vector3 push = Vector3.ProjectOnPlane(other.transform.position - transform.position, Vector3.up).normalized;
            Vector3 rel = Body.linearVelocity - other.Body.linearVelocity;
            float strength = Mathf.Clamp(3f + Mathf.Max(0f, Vector3.Dot(rel, push)) * 0.4f, 3f, 6f);
            other.Body.AddForce(push * strength, ForceMode.VelocityChange);
            other._hitGuard = 0f;
            other.Knock(point, push, strength + 3f);
            other.DashTimer = 0f;
            other.CancelSideDash();
            Body.AddForce(-push * strength * 0.3f, ForceMode.VelocityChange);
            Stun = Mathf.Max(Stun, 0.2f);
            _hitGuard = 0.3f;
            DashTimer = 0f;
            CancelSideDash();
            Impact?.Invoke(point, strength * 0.6f); // the rammer feels it too (camera)
            DashHit?.Invoke(other);
        }
    }
}
