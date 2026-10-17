using System;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Arcade raycast car. It only knows steer + throttle (+ boost); who decides them is the driving logic (AutoPilot).
    /// <list type="bullet">
    /// <item>Suspension: a spring-damper per wheel on a raycast (no wheel colliders).</item>
    /// <item>Tyres: lateral force fights the sideways slip, longitudinal force = drive / brakes / rolling drag; both are
    /// capped by the friction circle μ x load. Past breakaway only kinetic friction is left - this is what makes ice and
    /// dirt slide and a drift hold.</item>
    /// <item>Weight transfer: the longitudinal acceleration moves load between the axles (grip only), which gives
    /// oversteer on the brakes and understeer on the throttle.</item>
    /// <item>Arcade help: drift assist and yaw control keep slides playable, the car rights itself in the air.</item>
    /// <item>Hits: contact impulses become knocks (loose tyres, spin), dash rams and HP damage.</item>
    /// </list>
    /// All numbers that define the physics live in <see cref="CarTuning"/>; the constants below are thresholds of the
    /// implementation. Details: ARCHITECTURE.md.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarController : MonoBehaviour
    {
        static readonly Vector3 BaseSize = new Vector3(2.1f, 0.9f, 4.1f); // Formula1 from the original game, m
        /// <summary>Car size factor for every car (RC look; menu). The whole car scales: body, collider, suspension.</summary>
        public static float Scale = 1f;
        public static Vector3 Size => BaseSize * Scale;
        public static float WheelBase => 2.8f * Scale;

        // Car-body collisions.
        const float Bounciness = 0.15f;          // restitution of the car material
        const float HitMinImpulse = 1f;          // N·s; below it two bodies are not touching yet (speculative contact)
        const float GroundNormalY = 0.6f;        // a contact this flat is the ground, not a wall
        const float WallHitMinSpeed = 3f;        // m/s closing speed that counts as a wall hit
        const float HitGuard = 0.3f;             // s; one hit = one knock (its impulse is spread over a few steps)
        const float KnockFullAbove = 8f;         // m/s above knockMinSpeed for the strongest knock

        // Loose tyres.
        const float StunFull = 0.5f;             // s of stun that means "fully loose"
        const float KnockLatLoss = 0.3f;         // sideways grip lost when fully knocked (more lets the car spin round)
        const float KnockAllLoss = 0.4f;         // friction circle lost when fully knocked
        const float SideDashLatLoss = 0.85f;     // sideways grip lost at the start of a side dash (fades with it)
        const float KnockSteerCap = 0.4f;        // steering share the yaw hold follows after a knock
        const float KnockYawHold = 1.5f;         // x driftControl: how hard the yaw is held after a knock

        // Side dash thrust: a P controller on the sideways speed.
        const float DashThrustGain = 15f;        // 1/s
        const float DashThrustMax = 60f;         // m/s²

        // Dash ram.
        const float RamBase = 3f, RamPerMs = 0.4f, RamMax = 6f; // m/s shove: base + closing speed share, capped
        const float RamKnockExtra = 3f;          // the hit car's knock is the shove + this
        const float RamRebound = 0.3f;           // share of the shove the rammer bounces back
        const float RamSelfStun = 0.2f;          // s

        // Drift and balance thresholds.
        const float DriftMinSpeed = 9f;          // m/s
        const float SpinOutAngle = 100f;         // deg of body slip: past it the car is spinning, not drifting
        const float AssistMinSpeed = 5f;         // m/s forward
        const float AssistMinSlip = 4f;          // deg body slip
        const float BalanceMinSpeed = 8f;        // m/s; over/understeer are judged above it
        const float AxleSlideDeg = 6f;           // deg; an axle slides above this slip angle
        const float UndersteerMargin = 3f;       // deg the front slides more than the rear
        const float TailInDeg = 4f;              // deg body slip still counted as "tail in line"

        // Rolling back onto the wheels.
        const float OnSideUpY = 0.4f;            // up.y below it = on the side / roof
        const float FlipMaxSpeed = 4f;           // m/s
        const float FlipDelay = 0.6f;            // s on the side before the hop (and again after 2x this)
        const float FlipHop = 4f;                // m/s up
        const float FlipRoll = 5f;               // rad/s

        public CarTuning tuning;
        public SurfaceDef[] surfaces;

        public Rigidbody Body { get; private set; }
        public float ForwardSpeed { get; private set; }
        public float Speed { get; private set; }
        /// <summary>deg between the nose and the direction of travel (body slip angle).</summary>
        public float SlipAngle { get; private set; }
        /// <summary>rad/s around the car's up axis, + = turning right.</summary>
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
        /// <summary>s left passing through other cars after a recover (the race toggles the collisions); the body blinks.</summary>
        [NonSerialized] public float GhostTime;
        public bool SideDashing => _sideDashLeft > 0f;

        public const float MaxHP = 100f;
        [NonSerialized] public float HP = MaxHP;
        /// <summary>0 intact, 1 light (smoke, -5 %), 2 heavy (front wing gone, -10 %), 3 critical (rear wing gone, fire, -15 %).</summary>
        public int DamageLevel => HP > 75f ? 0 : HP > 50f ? 1 : HP > 25f ? 2 : 3;

        /// <summary>Average slip angle of the front / rear tyres (deg). Which axle slides more = over- or understeer.</summary>
        public float FrontSlip { get; private set; }
        public float RearSlip { get; private set; }
        /// <summary>The rear has let go and the tail is out (body at an angle to its path): a drift.</summary>
        public bool Oversteer => Grounded && Speed > BalanceMinSpeed && RearSlip > AxleSlideDeg && Mathf.Abs(SlipAngle) > AxleSlideDeg;
        /// <summary>The front slides while the tail stays in line: the car pushes wide of where it is steered.</summary>
        public bool Understeer => Grounded && Speed > BalanceMinSpeed && FrontSlip > AxleSlideDeg && FrontSlip > RearSlip + UndersteerMargin && Mathf.Abs(SlipAngle) < TailInDeg;

        public event Action<float> WallHit;              // impact speed
        public event Action<int> MiniTurbo;              // tier 1..3
        public event Action<CarController> DashHit;      // rammed this car while dashing
        public event Action<Vector3, float> Impact;      // contact point, strength (m/s): sparks, camera shake
        public event Action<float, CarController> Damaged; // HP lost, who did it (null = wall)

        readonly Vector3[] _wheelLocal = new Vector3[4]; // 0 front left, 1 front right, 2 rear left, 3 rear right
        readonly float[] _lastCompression = new float[4];
        readonly SurfaceDef[] _wheelSurface = new SurfaceDef[4];
        readonly Transform[] _wheelVis = new Transform[4];
        readonly TrailRenderer[] _skid = new TrailRenderer[2];
        float _steerAngle, _wheelSpin;
        float _sideDashLeft, _sideDashTime, _sideDashPeak, _sideDashSign, _hitGuard;
        Transform _bodyVis, _frontWing, _rearWing;
        Renderer _bodyRenderer;
        ParticleSystem _smoke;
        Color _color;
        Vector3 _lastVel;
        float _roll, _pitch, _flipTime;

        public void Setup(CarTuning carTuning, SurfaceDef[] surfaceDefs, Color color)
        {
            tuning = carTuning;
            surfaces = surfaceDefs;
            Surface = surfaces[(int)SurfaceType.Asphalt];
            gameObject.layer = 2; // Ignore Raycast: wheels never hit cars

            Body = GetComponent<Rigidbody>();
            Body.mass = tuning.mass;
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
                dynamicFriction = 0.1f, staticFriction = 0.1f, bounciness = Bounciness,
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

        public void ResetMotion()
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            DriftTime = 0f;
            DashTimer = 0f;
            Stun = 0f;
            _sideDashLeft = 0f;
            _flipTime = 0f;
            _lastVel = Vector3.zero;
            for (int i = 0; i < 2; i++) _skid[i].Clear();
        }

        /// <summary>
        /// Side dash: a sideways lunge that covers <paramref name="distance"/> m in <paramref name="time"/> s. The thrust
        /// follows a linearly fading sideways speed (racing_unity: Lerp(slideSpeed, 0, t)), so the peak is 2 x distance / time;
        /// the tyres slide while it pushes (they would eat the sideways motion in one step otherwise) and the nose turns into it a little.
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

        // =============================================================================================================
        // The physics step
        // =============================================================================================================

        /// <summary>
        /// One physics step (dt = 0.02 s): steer -1..1, throttle -1 (brake / reverse) .. 1. The order is fixed:
        /// motion -> timers and tyre looseness -> steering -> engine and brakes -> the four wheels (suspension + tyres)
        /// -> body forces (downforce, righting, drag) -> side dash thrust -> drift state and arcade help -> visuals.
        /// </summary>
        public void Simulate(float steerInput, float throttleInput, bool boosting, float dt)
        {
            var t = transform;
            Vector3 up = t.up;
            Vector3 vel = Body.linearVelocity;
            Vector3 velFlat = Vector3.ProjectOnPlane(vel, up);
            ForwardSpeed = Vector3.Dot(vel, t.forward);
            Speed = velFlat.magnitude;
            YawRate = Vector3.Dot(Body.angularVelocity, up);

            TickTimers(dt);
            TyreLooseness(out float latGrip, out float allGrip);
            Steer(steerInput, dt);
            BurstTimer = Mathf.Max(0f, BurstTimer - dt);
            IsBoosting = (boosting || BurstTimer > 0f) && !Hold;
            EngineAndBrakes(throttleInput, out float driveAccel, out float brakeAccel);

            float downforce = tuning.downforce * Speed * Speed;
            int grounded = Wheels(new WheelStep
            {
                up = up, com = Body.worldCenterOfMass, mass = Body.mass, downforce = downforce,
                driveAccel = driveAccel, brakeAccel = brakeAccel, latGrip = latGrip, allGrip = allGrip, dt = dt,
            });
            FlipBack(dt);
            BodyForces(grounded, up, velFlat, downforce);
            SideDashThrust(up, dt);
            DriftAndArcadeHelp(steerInput, vel, velFlat, up, dt);

            UpdateDriftCharge(dt);
            SpinWheels(dt);
            LeanBody(dt);
        }

        void TickTimers(float dt)
        {
            DashTimer = Mathf.Max(0f, DashTimer - dt);
            Stun = Mathf.Max(0f, Stun - dt);
            SinceKnock += dt;
            _hitGuard = Mathf.Max(0f, _hitGuard - dt);
        }

        /// <summary>
        /// Grip multipliers (1 = normal). Tyres let go while a side dash pushes (fading with it), and partly after a knock
        /// (the car skids with the hit; more would let it spin round - the yaw hold in DriftAndArcadeHelp stays on for that).
        /// </summary>
        void TyreLooseness(out float latGrip, out float allGrip)
        {
            float stun01 = Mathf.Clamp01(Stun / StunFull);
            latGrip = 1f - Mathf.Max(_sideDashLeft > 0f ? SideDashLatLoss * _sideDashLeft / _sideDashTime : 0f, KnockLatLoss * stun01);
            allGrip = 1f - KnockAllLoss * stun01;
        }

        /// <summary>Less lock at speed; the wheels turn at a limited rate.</summary>
        void Steer(float steerInput, float dt)
        {
            var c = tuning;
            float speed01 = Mathf.Clamp01(Speed / c.maxSpeed);
            CurrentMaxSteer = c.maxSteerAngle * Mathf.Lerp(1f, c.highSpeedSteer, speed01);
            _steerAngle = Mathf.MoveTowards(_steerAngle, Mathf.Clamp(steerInput, -1f, 1f) * CurrentMaxSteer, c.steerResponse * c.maxSteerAngle * dt);
        }

        /// <summary>
        /// Drive acceleration a = throttle x acceleration x (1 - (v / vTop)²) (+ boost), brakes, reverse; all in m/s².
        /// vTop = maxSpeed x surface x slipstream x damage.
        /// </summary>
        void EngineAndBrakes(float throttleInput, out float driveAccel, out float brakeAccel)
        {
            var c = tuning;
            float health = 1f - c.damageSpeedLoss * DamageLevel; // a battered car is slower
            float topSpeed = c.maxSpeed * AverageSurfaceMaxSpeed() * (1f + c.draftTopSpeed * Draft) * health;
            float throttle = Mathf.Clamp(throttleInput, -1f, 1f);
            driveAccel = 0f;
            brakeAccel = 0f;
            if (throttle > 0f)
            {
                float x = Mathf.Clamp01(ForwardSpeed / topSpeed);
                driveAccel = throttle * c.acceleration * (1f - x * x) * health;
                if (IsBoosting && ForwardSpeed < topSpeed) driveAccel += c.boostAcceleration * throttle;
            }
            else if (throttle < 0f)
            {
                if (ForwardSpeed > 0.5f) brakeAccel = -throttle * c.brakeDeceleration; // still rolling forward: brake
                else driveAccel = throttle * c.acceleration * c.reverseAccelShare * (1f - Mathf.Clamp01(-ForwardSpeed / c.reverseMaxSpeed));
            }
        }

        /// <summary>What every wheel of one step shares.</summary>
        struct WheelStep
        {
            public Vector3 up, com;
            public float mass, downforce, driveAccel, brakeAccel, latGrip, allGrip, dt;
        }

        /// <summary>The four wheels; returns how many touch the ground. Also sets Grounded, Surface and the axle slip angles.</summary>
        int Wheels(WheelStep s)
        {
            // Load moved between the axles by the longitudinal acceleration (ΔN = m x a x h/L, per wheel of the axle /2),
            // for the grip only: brakes put it on the front (the rear lets go = oversteer), throttle on the rear (understeer).
            float transfer = s.mass * (s.driveAccel - s.brakeAccel) * tuning.cogHeightRatio * tuning.weightTransfer * 0.5f;
            float frontSlip = 0f, rearSlip = 0f;
            int frontN = 0, rearN = 0, grounded = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!Wheel(i, s, transfer, out float slipDeg)) continue;
                grounded++;
                if (i < 2) { frontSlip += slipDeg; frontN++; } else { rearSlip += slipDeg; rearN++; }
            }
            Grounded = grounded >= 2;
            Surface = MajoritySurface(grounded);
            FrontSlip = frontN > 0 ? frontSlip / frontN : 0f;
            RearSlip = rearN > 0 ? rearSlip / rearN : 0f;
            return grounded;
        }

        /// <summary>
        /// One wheel: raycast suspension (F = k x compression + c x compression speed), then the tyre forces in the tyre's
        /// ground frame, capped by the friction circle. Returns false when the wheel is in the air.
        /// </summary>
        bool Wheel(int i, in WheelStep s, float transfer, out float slipDeg)
        {
            var c = tuning;
            var t = transform;
            bool front = i < 2;
            slipDeg = 0f;
            Vector3 origin = t.TransformPoint(_wheelLocal[i]);
            float rayLength = (c.suspensionLength + c.wheelRadius) * Scale;
            if (!Physics.Raycast(origin, -s.up, out var hit, rayLength, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                _lastCompression[i] = 0f;
                _wheelVis[i].localPosition = _wheelLocal[i] - Vector3.up * c.suspensionLength;
                if (!front) _skid[i - 2].emitting = false;
                return false;
            }

            // Suspension.
            float springLength = hit.distance - c.wheelRadius * Scale;
            float compression = c.suspensionLength * Scale - springLength;
            float compressionVel = (compression - _lastCompression[i]) / s.dt;
            _lastCompression[i] = compression;
            float load = Mathf.Max(0f, compression * c.spring + compressionVel * c.damper); // N
            Body.AddForceAtPosition(s.up * load, origin);

            var surface = SurfaceOf(hit.collider);
            _wheelSurface[i] = surface;

            // Tyre frame on the ground plane (the front wheels are steered).
            Vector3 fwd = t.forward;
            if (front) fwd = Quaternion.AngleAxis(_steerAngle, s.up) * fwd;
            fwd = Vector3.ProjectOnPlane(fwd, hit.normal).normalized;
            Vector3 right = Vector3.Cross(hit.normal, fwd);
            Vector3 pv = Body.GetPointVelocity(hit.point);
            float vLat = Vector3.Dot(pv, right);
            float vLong = Vector3.Dot(pv, fwd);

            // Lateral: the force that would cancel the slip this step (x stiffness). Longitudinal: drive, brakes, rolling drag.
            float wheelMass = s.mass / 4f;
            float latF = -vLat * wheelMass / s.dt * c.lateralStiffness * s.latGrip;
            float driveShare = front ? 1f - c.driveBias : c.driveBias;
            float longF = s.mass * s.driveAccel * driveShare * 0.5f;
            if (s.brakeAccel > 0f)
                longF -= Mathf.Sign(vLong) * Mathf.Min(s.mass * s.brakeAccel * (front ? c.brakeFront : c.brakeRear) * 0.5f, Mathf.Abs(vLong) * wheelMass / s.dt);
            if (Hold) longF -= vLong * wheelMass / s.dt;
            longF -= vLong * surface.rollingDrag * wheelMass;

            // Friction circle |F| <= μ x N; once the tyre slides it only has kinetic friction left (breakaway).
            float mu = c.grip * surface.grip * (front ? 1f : c.rearGrip);
            float slide = Mathf.InverseLerp(c.slideStart, c.slideFull, Mathf.Abs(vLat));
            float gripLoad = Mathf.Max(0f, load + (front ? -transfer : transfer));
            float maxF = mu * (gripLoad + s.downforce * 0.25f) * Mathf.Lerp(1f, surface.slideFriction, slide) * s.allGrip;
            slipDeg = Mathf.Atan2(Mathf.Abs(vLat), Mathf.Max(Mathf.Abs(vLong), 1f)) * Mathf.Rad2Deg;
            var f = new Vector2(latF, longF);
            if (f.sqrMagnitude > maxF * maxF) f = f.normalized * maxF;

            // Applied at the height of the centre of mass: yaw from tyres, no roll that flips the car.
            Vector3 forcePoint = hit.point + s.up * Vector3.Dot(s.com - hit.point, s.up);
            Body.AddForceAtPosition(right * f.x + fwd * f.y, forcePoint);

            _wheelVis[i].localPosition = _wheelLocal[i] - Vector3.up * springLength / Scale;
            if (!front)
            {
                var skid = _skid[i - 2];
                skid.transform.position = hit.point + hit.normal * 0.03f;
                skid.emitting = Mathf.Abs(vLat) > 2.5f || (s.brakeAccel > 0.8f * c.brakeDeceleration && Speed > 12f);
            }
            return true;
        }

        /// <summary>Downforce, the arcade self-righting while airborne or on two wheels, aerodynamic drag (less in a slipstream).</summary>
        void BodyForces(int grounded, Vector3 up, Vector3 velFlat, float downforce)
        {
            var c = tuning;
            if (grounded > 0) Body.AddForce(-up * downforce);
            if (grounded < 3)
            {
                Vector3 tilt = Vector3.Cross(up, Vector3.up);
                Vector3 roll = Body.angularVelocity - Vector3.Project(Body.angularVelocity, up);
                Body.AddTorque(tilt * c.airRighting - roll * c.airRightingDamping, ForceMode.Acceleration);
            }
            Body.AddForce(-velFlat * velFlat.magnitude * c.airDrag * (1f - c.draftDragCut * Draft), ForceMode.Acceleration);
        }

        /// <summary>Side dash thrust: the sideways speed follows the fading profile (the sliding tyres still take some, this tops it up).</summary>
        void SideDashThrust(Vector3 up, float dt)
        {
            if (_sideDashLeft <= 0f) return;
            Vector3 side = Vector3.ProjectOnPlane(transform.right, up).normalized * _sideDashSign;
            float want = _sideDashPeak * _sideDashLeft / _sideDashTime;
            float have = Vector3.Dot(Body.linearVelocity, side);
            Body.AddForce(side * Mathf.Clamp((want - have) * DashThrustGain, 0f, DashThrustMax), ForceMode.Acceleration);
            _sideDashLeft -= dt;
        }

        /// <summary>
        /// Body slip angle and drift state, then the arcade help: while sliding the velocity bends toward the nose (keeps
        /// speed in a drift) and the steering sets the yaw rate (keeps it controllable); after a knock the yaw is held.
        /// </summary>
        void DriftAndArcadeHelp(float steerInput, Vector3 vel, Vector3 velFlat, Vector3 up, float dt)
        {
            var c = tuning;
            var t = transform;
            SlipAngle = Speed > 3f && ForwardSpeed > 0f ? Vector3.SignedAngle(t.forward, velFlat, up) : 0f;
            // A side dash or a knock slides the car sideways on purpose: no drift (points, assist) out of that.
            // Neither is a car plowing on with the front washed out (understeer): a drift is the tail out.
            bool forcedSlide = _sideDashLeft > 0f || Stun > 0f;
            IsDrifting = !forcedSlide && !Understeer && Grounded && Speed > DriftMinSpeed && Mathf.Abs(SlipAngle) > c.driftAngle && Mathf.Abs(SlipAngle) < SpinOutAngle;

            if (!forcedSlide && Grounded && ForwardSpeed > AssistMinSpeed && Mathf.Abs(SlipAngle) > AssistMinSlip)
            {
                Vector3 aligned = Vector3.Slerp(velFlat, t.forward * velFlat.magnitude, Mathf.Clamp01(c.driftAssist * 0.5f * dt));
                Body.linearVelocity = aligned + Vector3.Project(vel, up);
                if (IsDrifting)
                    Body.AddTorque(up * (steerInput * c.driftYawRate - YawRate) * c.driftControl, ForceMode.Acceleration);
            }
            else if (Stun > 0f && Grounded) // knocked: it skids with the hit, but the yaw is held near what the steering asks (no spin round)
                Body.AddTorque(up * (Mathf.Clamp(steerInput, -KnockSteerCap, KnockSteerCap) * c.driftYawRate - YawRate) * c.driftControl * KnockYawHold, ForceMode.Acceleration);
        }

        /// <summary>On its side or roof and nearly stopped: after a moment it hops and rolls back onto its wheels (arcade recover).</summary>
        void FlipBack(float dt)
        {
            bool onSide = transform.up.y < OnSideUpY && Speed < FlipMaxSpeed;
            _flipTime = onSide ? _flipTime + dt : 0f;
            if (_flipTime < FlipDelay) return;
            _flipTime = -FlipDelay; // next try no sooner than 2 x FlipDelay
            Vector3 axis = Vector3.Cross(transform.up, Vector3.up); // the roll that stands it up
            if (axis.sqrMagnitude < 0.01f) axis = transform.forward; // exactly on the roof: over the side
            Body.AddForce(Vector3.up * FlipHop, ForceMode.VelocityChange);
            Body.AddTorque(axis.normalized * FlipRoll, ForceMode.VelocityChange);
        }

        /// <summary>Drifting charges energy; ending a long enough drift fires a mini-turbo (free boost) by tier.</summary>
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

        // =============================================================================================================
        // Hits and damage
        // =============================================================================================================

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
                    if (pair.isCollisionExit || pair.contactCount == 0 || j < HitMinImpulse) continue; // not touching yet
                    Vector3 point = pair.GetContactPoint(0).position, n = pair.GetContactPoint(0).normal;
                    if (a != null && b != null) CarHit(a, b, point, n, j);
                    else WallContact(a ?? b, point, n, j);
                }
            }
        }

        /// <summary>Car against car: a dash rams, otherwise both get knocked.</summary>
        static void CarHit(CarController a, CarController b, Vector3 point, Vector3 n, float impulse)
        {
            if (Vector3.Dot(n, a.transform.position - b.transform.position) < 0f) n = -n; // n: into a
            float vn = impulse / (a.Body.mass * b.Body.mass / (a.Body.mass + b.Body.mass) * (1f + Bounciness));
            if (a.DashTimer > 0f) a.Ram(b, point);
            if (b.DashTimer > 0f) b.Ram(a, point);
            if (a.DashTimer <= 0f && b.DashTimer <= 0f) { a.Knock(point, n, vn, b); b.Knock(point, -n, vn, a); }
        }

        /// <summary>
        /// Car against a static collider. Walls: PhysX bounces the car off, plus sparks / camera / damage; no knock (a spin
        /// at a wall only chains into the next hit).
        /// </summary>
        static void WallContact(CarController car, Vector3 point, Vector3 n, float impulse)
        {
            if (Mathf.Abs(n.y) > GroundNormalY) return; // ground
            if (Vector3.Dot(n, car.Body.worldCenterOfMass - point) < 0f) n = -n;
            float vn = impulse / (car.Body.mass * (1f + Bounciness));
            if (vn <= WallHitMinSpeed || car._hitGuard > 0f) return;
            car._hitGuard = HitGuard;
            car.WallHit?.Invoke(vn);
            car.Impact?.Invoke(point, vn * 0.5f);
            car.TakeDamage((vn - car.tuning.wallDamageFrom) * car.tuning.wallDamagePerMs, null); // a scrape costs nothing, a head-on hit ~15 HP
        }

        /// <summary>
        /// A knock: the tyres break loose for a moment (longer for a harder hit), so the car really slides and turns with the
        /// impulse PhysX gave it instead of the tyres eating it in the next step; a hit off the centre spins it
        /// (spin = lever arm x push direction).
        /// </summary>
        void Knock(Vector3 point, Vector3 n, float vn, CarController source, bool damage = true)
        {
            var c = tuning;
            if (vn < c.knockMinSpeed || _hitGuard > 0f) return;
            if (damage) TakeDamage((vn - c.knockDamageFrom) * c.knockDamagePerMs, source); // leaning on each other is free, a hard hit ~10 HP
            _hitGuard = HitGuard;
            SinceKnock = 0f;
            float k = Mathf.Clamp01((vn - c.knockMinSpeed) / KnockFullAbove);
            Stun = Mathf.Max(Stun, Mathf.Lerp(c.knockStunMin, c.knockStunMax, k)); // felt, but the car is driving again within half a second
            Vector3 up = transform.up;
            Vector3 lever = Vector3.ProjectOnPlane(point - Body.worldCenterOfMass, up);
            float spin = Vector3.Dot(Vector3.Cross(lever, Vector3.ProjectOnPlane(n, up)), up);
            Body.AddTorque(up * Mathf.Clamp(spin * vn * c.knockSpin, -c.knockSpinMax, c.knockSpinMax), ForceMode.VelocityChange);
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
            float strength = Mathf.Clamp(RamBase + Mathf.Max(0f, Vector3.Dot(rel, push)) * RamPerMs, RamBase, RamMax);
            other.Body.AddForce(push * strength, ForceMode.VelocityChange);
            other._hitGuard = 0f;
            other.Knock(point, push, strength + RamKnockExtra, this, false);
            other.TakeDamage(tuning.ramDamageBase + strength, this); // racing_unity: dash damage by hit strength, 6-9 HP (~12 rams to wreck)
            TakeDamage(tuning.ramSelfDamage, other);
            other.DashTimer = 0f;
            other.CancelSideDash();
            Body.AddForce(-push * strength * RamRebound, ForceMode.VelocityChange);
            Stun = Mathf.Max(Stun, RamSelfStun);
            _hitGuard = HitGuard;
            DashTimer = 0f;
            CancelSideDash();
            Impact?.Invoke(point, strength * 0.6f); // the rammer feels it too (camera)
            DashHit?.Invoke(other);
        }

        /// <summary>Lose HP (hits). Levels show on the car (darker body, wings fall off, smoke, fire) and cost speed.</summary>
        public void TakeDamage(float amount, CarController source)
        {
            if (amount < 0.5f || HP <= 0f) return;
            int before = DamageLevel;
            HP = Mathf.Max(0f, HP - amount);
            if (DamageLevel != before) ShowDamage(true);
            else _bodyRenderer.sharedMaterial.color = DamagedColor();
            Damaged?.Invoke(amount, source);
        }

        /// <summary>Back to full health (after a wreck).</summary>
        public void Repair()
        {
            HP = MaxHP;
            ShowDamage(false);
        }

        // =============================================================================================================
        // Visuals (no influence on the physics)
        // =============================================================================================================

        void BuildVisuals(Color color)
        {
            var t = transform;
            _bodyVis = new GameObject("BodyVis").transform; // leans on the springs (roll / pitch); wheels stay on the ground
            _bodyVis.SetParent(t, false);
            var b = _bodyVis;
            _color = color;
            _bodyRenderer = Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.3f, 0f), new Vector3(1.5f, 0.45f, BaseSize.z), color, "Body").GetComponent<Renderer>();
            _frontWing = Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.18f, 1.75f), new Vector3(BaseSize.x, 0.12f, 0.5f), color * 0.8f, "FrontWing");
            _rearWing = Mats.Prim(PrimitiveType.Cube, b, new Vector3(0f, 0.85f, -1.8f), new Vector3(BaseSize.x, 0.1f, 0.45f), color * 0.8f, "RearWing");
            _smoke = BuildSmoke(t);
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

        static ParticleSystem BuildSmoke(Transform car)
        {
            var go = new GameObject("Smoke");
            go.transform.SetParent(car, false);
            go.transform.localPosition = new Vector3(0f, 0.7f, -1.2f); // engine
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.gravityModifier = -0.15f; // rises
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 1.6f));
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.15f;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = Mats.Unlit(Color.white);
            ps.Play();
            return ps;
        }

        Color DamagedColor() => Color.Lerp(_color, new Color(0.12f, 0.12f, 0.12f), (1f - HP / MaxHP) * 0.6f);

        void ShowDamage(bool debris)
        {
            int level = DamageLevel;
            _bodyRenderer.sharedMaterial.color = DamagedColor();
            SetWing(_frontWing, level < 2, debris);
            SetWing(_rearWing, level < 3, debris);
            var main = _smoke.main;
            main.startColor = level >= 3 ? new ParticleSystem.MinMaxGradient(new Color(1f, 0.45f, 0.1f, 0.9f), new Color(0.1f, 0.1f, 0.1f, 0.8f))
                            : new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.55f, 0.55f, 0.6f), new Color(0.25f, 0.25f, 0.25f, 0.7f));
            var emission = _smoke.emission;
            emission.rateOverTime = level == 0 ? 0f : level == 1 ? 4f : level == 2 ? 12f : 25f;
        }

        void SetWing(Transform wing, bool on, bool debris)
        {
            if (wing.gameObject.activeSelf == on) return;
            wing.gameObject.SetActive(on);
            if (on || !debris || !Application.isPlaying) return;
            // The part flies off (visual only: no collider, gone after 3 s).
            var piece = Instantiate(wing.gameObject, wing.position, wing.rotation);
            piece.SetActive(true);
            piece.transform.localScale = Vector3.Scale(wing.localScale, transform.lossyScale);
            var rb = piece.AddComponent<Rigidbody>();
            rb.mass = 5f;
            rb.linearVelocity = Body.linearVelocity * 0.6f + Vector3.up * 5f + transform.right * UnityEngine.Random.Range(-3f, 3f);
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 10f;
            Destroy(piece, 3f);
        }

        void SpinWheels(float dt)
        {
            _wheelSpin += ForwardSpeed / (tuning.wheelRadius * Scale) * dt * Mathf.Rad2Deg;
            for (int i = 0; i < 4; i++)
                _wheelVis[i].localRotation = Quaternion.Euler(_wheelSpin, i < 2 ? _steerAngle : 0f, 0f);
        }

        /// <summary>Body roll / pitch from the felt acceleration (corners, brakes, dashes, hits); blinking while a ghost.</summary>
        void LeanBody(float dt)
        {
            Vector3 vel = Body.linearVelocity;
            Vector3 acc = (vel - _lastVel) / dt;
            _lastVel = vel;
            float lat = Vector3.Dot(acc, transform.right), lon = Vector3.Dot(acc, transform.forward);
            float k = Mathf.Clamp01(12f * dt);
            _roll = Mathf.Lerp(_roll, Mathf.Clamp(lat * 0.45f, -9f, 9f), k);   // deg; leans out of the turn
            _pitch = Mathf.Lerp(_pitch, Mathf.Clamp(-lon * 0.2f, -4f, 4f), k); // deg; nose dives on the brakes
            _bodyVis.localRotation = Quaternion.Euler(_pitch, 0f, _roll);
            if (GhostTime > 0f) GhostTime -= dt;
            _bodyVis.gameObject.SetActive(GhostTime <= 0f || Mathf.Repeat(GhostTime, 0.25f) > 0.1f);
        }
    }
}
