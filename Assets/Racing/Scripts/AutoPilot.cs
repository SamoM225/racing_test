using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Driving logic shared by the player and the bots (doc: strategy -> virtual joystick -> driving logic).
    /// The game steers along the racing line; the input only offsets the nav point, scales the speed and dashes.
    /// The joystick rules (free swipe window, energy drain, cooldown) and the handicap freeze while maneuvering
    /// apply to everybody - the only difference between player and bot is where the input comes from.
    /// One step: joystick rules -> nav point and speed order -> dash -> steering -> target speed -> throttle -> recover
    /// or CarController.Simulate.
    /// </summary>
    public class AutoPilot
    {
        // Steering: pure pursuit with a lookahead that grows with speed.
        const float LookBase = 6f, LookPerMs = 0.5f;   // m, + s x speed
        const float LookMin = 7f, LookMax = 26f;        // m
        const float EdgeMargin = 1f;                    // m between the car's side and the road edge for the nav point
        const float SlideCatchDeg = 8f;                 // deg body slip from which the game countersteers
        const float SlideCatchMinSpeed = 5f;            // m/s
        const float SlideCatchGain = 0.45f;             // share of the slip angle steered against
        const float WrongWayDeg = 90f;                  // deg off the road direction = facing the wrong way
        const float UTurnSpeed = 6f;                    // m/s while turning round

        // Target speed.
        const float ProfileLookahead = 0.35f;           // s ahead the corner speed is read
        const float RadiusFactorMin = 0.5f;             // off-line radius factor floor (inside of a corner)
        const float BurstFactor = 1.2f;                 // mini-turbo speed share
        const float CatchUpGripLow = 0.75f, CatchUpGripHigh = 0.95f; // surface grip range of the catch-up allowance
        const float CatchUpMaxAllowance = 1.1f;         // x physical corner limit on a grippy road (~94 % of real grip)
        const float SpeedFloor = 4f;                    // m/s; the band never parks a car
        const float StickDeadZone = 0.05f;              // stick off centre = maneuvering
        const float FreezeMaxTime = 2f;                 // s the handicap may stay frozen (game: bot attack maneuver)

        // Throttle: a speed controller with a coast band. err = target - speed (m/s).
        const float ThrottleBase = 0.25f, ThrottleGain = 0.3f; // throttle = base + gain x err when too slow
        const float CoastBand = 1.5f;                   // m/s too fast that is only coasted off
        const float BrakeGain = 0.35f, BrakeBase = 0.1f; // brake = gain x (err + band) - base beyond it
        const float BrakeGripShare = 0.7f;              // max braking as a share of μg, keeps grip to turn (no straight slide on ice)
        const float Gravity = 9.81f;                    // m/s²
        const float UndersteerLift = 0.1f;              // throttle cap while the front washes out
        const float BoostingOrder = 1.15f;              // speed order from which the car shows / uses boost
        const float BoostingDashBonus = 2f;             // m/s of dash bonus that counts as boosting

        // Front safety: time to collision with a car in my lane.
        const float SafetyRange = 20f;                  // m ahead
        const float SafetyLane = 1.05f;                 // x car width counts as my lane
        const float SafetyGap = 0.5f;                   // m kept beyond a car length
        const float TtcBrake = 0.4f, TtcLift = 1f;      // s: brake below, lift below
        const float SafetyBrake = -0.5f;                // throttle when braking for it

        // Recover (three-point turn).
        const float StuckSpeed = 1.5f;                  // m/s although the throttle is open (> StuckThrottle)
        const float StuckThrottle = 0.3f;
        const float RecoverWrongWayDeg = 100f;          // deg off the road direction
        const float RecoverWrongWaySpeed = 6f;          // m/s
        const float StuckTimeout = 0.8f;                // s before the turn starts
        const float ReverseTime = 1.6f, ForwardTime = 1.5f; // s per leg at most
        const float RearBlockedAfter = 0.6f;            // s reversing without moving = the rear is blocked
        const float ReverseDoneDeg = 35f, ForwardDoneDeg = 30f, ReverseSteerDeg = 15f;
        const float RecoverForwardThrottle = 0.6f;

        // Dash.
        const float DashKickForward = 5f, DashKickBack = 4f; // m/s instant speed change of a forward / back dash
        const float SideDashMinMove = 0.5f;             // m; at the road edge still a short shove (rams the car beside)

        readonly RaceManager _race;
        readonly RaceCar _me;

        /// <summary>m, nav point offset from the racing line (+ = right).</summary>
        public float NavOffset { get; private set; }
        /// <summary>Speed multiplier from the stick: 0.5 (full brake) .. 1.5 (full boost).</summary>
        public float SpeedOrder { get; private set; } = 1f;
        public float HoldTime { get; private set; }
        public bool JoystickVisible => HoldTime > Tuning.dashWindow;
        public bool Locked => _lockTimer > 0f || _awaitRelease;
        public float LockRemaining => _lockTimer;
        public float DashReadyIn => Mathf.Max(0f, _dashReadyAt - _race.SimTime);
        /// <summary>m/s the throttle controller aims for this step.</summary>
        public float TargetSpeed { get; private set; }

        float _lockTimer, _dashReadyAt, _dashBonus, _dashBonusStart, _dashBonusTime, _frozenOrder = 1f, _orderVel, _frozenAt;
        bool _frozen;
        /// <summary>Smoothed OrderSpeed (handicap) as the game uses it; starts at 0 = the start ramp.</summary>
        public float SmoothedOrderSpeed { get; private set; }
        /// <summary>Speed share coming from the handicap: (s + 1) / 2.</summary>
        public float HandicapFactor => (SmoothedOrderSpeed + 1f) * 0.5f;
        bool _awaitRelease, _wasHolding;
        float _stuckTime, _recoverUntil;
        int _recover; // 0 driving, 1 reversing, 2 forward on full lock
        /// <summary>Three-point turn in progress (wall or wrong way).</summary>
        public bool Recovering => _recover != 0;
        public int Recoveries { get; private set; }

        ControlTuning Tuning => _race.control;

        public AutoPilot(RaceManager race, RaceCar me)
        {
            _race = race;
            _me = me;
        }

        public void Reset(float navOffset)
        {
            NavOffset = navOffset;
            SpeedOrder = 1f;
            SmoothedOrderSpeed = 0f;
            _orderVel = 0f;
            _frozen = false;
            HoldTime = _lockTimer = _dashBonus = 0f;
            _awaitRelease = _wasHolding = false;
            _recover = 0;
            _stuckTime = 0f;
        }

        public void Step(DriverInput input, float dt)
        {
            var c = Tuning;
            var car = _me.Car;
            float now = _race.SimTime;
            car.EnergyMax = c.energyMax;
            car.Energy = Mathf.Min(c.energyMax, car.Energy + c.energyRegen * dt);

            ApplyJoystickRules(ref input, dt);

            // Maneuver: nav point offset from the racing line.
            NavOffset = ClampNav(NavOffset + Mathf.Clamp(input.side, -1f, 1f) * c.maneuverSideSpeed * dt);

            // Speed order ±50 % (own smoothing, not the handicap's).
            float bb = Mathf.Clamp(input.boostBrake, -1f, 1f);
            float order = 1f + (bb > 0f ? bb * c.boostShare : bb * c.brakeShare);
            SpeedOrder = Mathf.MoveTowards(SpeedOrder, order, dt / Mathf.Max(c.speedBlendTime, 0.01f));

            TryDash(input.dash, now);

            // Knocked: the game does not drag the car back to its old line - it carries on from where the hit put it.
            if (car.Stun > 0f) NavOffset = NavFor(_me.Lateral);

            float v = car.ForwardSpeed;
            float steer = SteerToNavPoint(v, out float lateral, out float heading);
            bool facingBack = Mathf.Abs(heading) > WrongWayDeg;
            if (facingBack) steer = -Mathf.Sign(heading);

            UpdateHandicap(input, now, dt);
            float dashBonus = _dashBonus * Mathf.Clamp01(1f - (now - _dashBonusStart) / Mathf.Max(_dashBonusTime, 0.01f));
            TargetSpeed = TargetSpeedFor(v, lateral, dashBonus);
            if (facingBack) TargetSpeed = Mathf.Min(TargetSpeed, UTurnSpeed); // tight U-turn

            float throttle = ThrottleFor(TargetSpeed - v, v, lateral);
            if (Recover(heading, throttle, v, now, dt)) return;

            bool boosting = SpeedOrder > BoostingOrder || dashBonus > BoostingDashBonus;
            car.Simulate(steer, throttle, boosting, dt);
        }

        /// <summary>
        /// Pure pursuit to the nav point (racing line + offset), a lookahead ahead by speed: steer angle = atan(2 L sin α / d).
        /// The game also countersteers slides. Facing the wrong way it would have no answer (sin 180° = 0) - the caller
        /// turns round instead. Out: the nav point's lateral position and the heading (deg, + = nose right of the road).
        /// </summary>
        float SteerToNavPoint(float v, out float lateral, out float heading)
        {
            var track = _race.Track;
            var car = _me.Car;
            float roadLimit = RoadLimit();
            float look = Mathf.Clamp(v * LookPerMs + LookBase, LookMin, LookMax);
            track.Sample(_me.Distance + look, out var center, out var right, out float line, out _);
            lateral = Mathf.Clamp(line + NavOffset, -roadLimit, roadLimit);
            // During a side dash the thrust moves the car across; the steering only keeps it parallel to the road.
            float aim = car.SideDashing ? Mathf.Clamp(_me.Lateral, -roadLimit, roadLimit) : lateral;
            Vector3 local = car.transform.InverseTransformPoint(center + right * aim);
            float alpha = Mathf.Atan2(local.x, local.z);
            float steerDeg = Mathf.Atan(2f * CarController.WheelBase * Mathf.Sin(alpha) / look) * Mathf.Rad2Deg;
            float steer = Mathf.Clamp(steerDeg / Mathf.Max(car.CurrentMaxSteer, 1f), -1f, 1f);
            if (Mathf.Abs(car.SlipAngle) > SlideCatchDeg && v > SlideCatchMinSpeed && !car.SideDashing) // the game catches slides for everybody
                steer = Mathf.Clamp(steer + car.SlipAngle / car.CurrentMaxSteer * SlideCatchGain, -1f, 1f);
            heading = Vector3.SignedAngle(track.Tangent[_me.TrackIndex], Vector3.ProjectOnPlane(car.transform.forward, Vector3.up), Vector3.up);
            return steer;
        }

        /// <summary>
        /// The rubber band as the game applies it: OrderSpeed (car.Handicap, written by the race) smoothed with SmoothDamp,
        /// frozen during a real maneuver (stick off centre) for at most 2 s - a finger resting on the screen must not
        /// switch the rubber band off.
        /// </summary>
        void UpdateHandicap(DriverInput input, float now, float dt)
        {
            var car = _me.Car;
            bool maneuvering = JoystickVisible && (Mathf.Abs(input.side) > StickDeadZone || Mathf.Abs(input.boostBrake) > StickDeadZone);
            if (maneuvering && !_frozen) { _frozen = true; _frozenOrder = car.Handicap; _frozenAt = now; }
            if (!maneuvering || now - _frozenAt > FreezeMaxTime) _frozen = false;
            SmoothedOrderSpeed = Mathf.SmoothDamp(SmoothedOrderSpeed, _frozen ? _frozenOrder : car.Handicap, ref _orderVel, _race.smoothTimeForSpeed, float.MaxValue, dt);
        }

        /// <summary>
        /// Target speed = corner profile x handicap x stick (+ mini-turbo, + dash). The profile is the track's grip-limited
        /// speed a moment ahead, capped at cruise, slower on the inside of a corner. Catch-up never exceeds the physical
        /// corner limit and never stacks with boost.
        /// </summary>
        float TargetSpeedFor(float v, float lateral, float dashBonus)
        {
            var c = Tuning;
            var track = _race.Track;
            var car = _me.Car;
            float ahead = _me.Distance + v * ProfileLookahead;
            float profile = Mathf.Min(track.SpeedAt(ahead) * c.cornerMargin, c.cruiseSpeed);
            // Off the racing line the corner radius changes: v ~ √R, R = 1/κ - lateral; inside = tighter = slower (never faster outside, to stay safe).
            track.Sample(ahead, out _, out _, out float lineAhead, out int idxAhead);
            float k = track.Curvature[idxAhead];
            profile *= Mathf.Sqrt(Mathf.Clamp((1f - k * lateral) / Mathf.Max(1f - k * lineAhead, 0.2f), RadiusFactorMin, 1f));
            float burst = car.BurstTimer > 0f ? BurstFactor : 1f;
            float handicapped = profile * HandicapFactor;
            // Catch-up never pushes a car past the grip limit of a corner (the boost stick still can). Up to ~94 % of real
            // grip on a grippy road; on sand / mud / snow a slide does not come back, so stay at the safe limit.
            float allowance = Mathf.Lerp(1f, CatchUpMaxAllowance, Mathf.InverseLerp(CatchUpGripLow, CatchUpGripHigh, car.Surface.grip));
            if (HandicapFactor > 1f) handicapped = Mathf.Min(handicapped, Mathf.Max(profile, track.LimitAt(ahead) * allowance));
            // Catch-up and boost never stack (both together end in the wall): a car being pulled up by the band gets the
            // band speed, the stick only adds on top of a plain or held-back car (like the game multiplier inside OrderSpeed).
            float target = SpeedOrder > 1f && HandicapFactor > 1f ? Mathf.Max(handicapped, profile * SpeedOrder) : handicapped * SpeedOrder;
            return Mathf.Max(SpeedFloor, target * burst + dashBonus);
        }

        /// <summary>
        /// Speed controller: open the throttle when too slow, coast inside a small band, brake beyond it - but never
        /// harder than the surface allows, never into a car ahead, and lift while the front washes out.
        /// </summary>
        float ThrottleFor(float err, float v, float lateral)
        {
            var car = _me.Car;
            float throttle = err >= 0f ? Mathf.Clamp01(ThrottleBase + err * ThrottleGain)
                           : err > -CoastBand ? 0f
                           : Mathf.Clamp((err + CoastBand) * BrakeGain - BrakeBase, -1f, 0f);
            throttle = Mathf.Min(throttle, FrontSafety(v, lateral));
            // Brake only as hard as the surface allows (a = share x μ x g), so the tyres keep some grip for turning.
            float maxBrake = Mathf.Clamp01(BrakeGripShare * car.tuning.grip * car.Surface.grip * Gravity / car.tuning.brakeDeceleration);
            throttle = Mathf.Max(throttle, -maxBrake);
            // Front washing out (understeer): lift, so the load comes onto the front tyres and they bite again.
            if (car.Understeer) throttle = Mathf.Min(throttle, UndersteerLift);
            return throttle;
        }

        /// <summary>
        /// Stuck with the nose in a wall, or facing the wrong way: a three-point turn instead of blind reversing. Back up with
        /// the wheels turned so the nose swings toward the road direction, then forward on full lock; again if still blocked.
        /// Drives the car itself while it runs (returns true).
        /// </summary>
        bool Recover(float heading, float throttle, float v, float now, float dt)
        {
            var car = _me.Car;
            if (_recover == 0)
            {
                bool blocked = _race.RaceTime > 3f && Mathf.Abs(v) < StuckSpeed && throttle > StuckThrottle;
                bool wrongWay = Mathf.Abs(heading) > RecoverWrongWayDeg && car.Speed < RecoverWrongWaySpeed;
                _stuckTime = car.Grounded && (blocked || wrongWay) ? _stuckTime + dt : 0f;
                if (_stuckTime < StuckTimeout) return false;
                _stuckTime = 0f;
                _recover = 1;
                _recoverUntil = now + ReverseTime;
                Recoveries++;
            }
            if (_recover == 1)
            {
                // Backing up with the wheels turned right swings the nose left, so steer toward the heading error.
                bool rearBlocked = now > _recoverUntil - (ReverseTime - RearBlockedAfter) && car.ForwardSpeed > -0.5f;
                if (Mathf.Abs(heading) < ReverseDoneDeg || now > _recoverUntil || rearBlocked)
                {
                    _recover = Mathf.Abs(heading) < ReverseDoneDeg ? 0 : 2;
                    _recoverUntil = now + ForwardTime;
                }
                else { car.Simulate(Mathf.Abs(heading) > ReverseSteerDeg ? Mathf.Sign(heading) : 0f, -1f, false, dt); return true; }
            }
            if (_recover == 2)
            {
                if (Mathf.Abs(heading) < ForwardDoneDeg || now > _recoverUntil) { _recover = 0; NavOffset = NavFor(_me.Lateral); }
                else { car.Simulate(-Mathf.Sign(heading), RecoverForwardThrottle, false, dt); return true; }
            }
            return false;
        }

        /// <summary>m from the centre line the nav point may go: the car's side stays EdgeMargin inside the road.</summary>
        float RoadLimit() => _race.Track.HalfWidth - (CarController.Size.x * 0.5f + EdgeMargin);

        /// <summary>Offset from the racing line, at most maxDirection, and the nav point stops at the road edge.</summary>
        float ClampNav(float nav)
        {
            float roadLimit = RoadLimit();
            float line = _race.Track.LineOffset[_me.TrackIndex];
            float max = Tuning.maxDirection;
            return Mathf.Clamp(Mathf.Clamp(nav, -max, max), -roadLimit - line, roadLimit - line);
        }

        /// <summary>The nav offset that puts the car at an absolute lateral position here.</summary>
        public float NavFor(float lateral) => ClampNav(lateral - _race.Track.LineOffset[_me.TrackIndex]);

        /// <summary>racing_unity joystick: free while shorter than the swipe window + free seconds, then drains energy; locked after.</summary>
        void ApplyJoystickRules(ref DriverInput input, float dt)
        {
            var c = Tuning;
            var car = _me.Car;
            if (_awaitRelease && !input.holding) _awaitRelease = false;
            if (_lockTimer > 0f) _lockTimer -= dt;
            if (Locked)
            {
                input.side = input.boostBrake = 0f;
                input.holding = false;
                HoldTime = 0f;
                return;
            }

            if (input.holding)
            {
                HoldTime += dt;
                if (HoldTime > c.dashWindow + c.joystickFreeSeconds)
                {
                    car.Energy -= c.joystickDrainPerSecond * dt;
                    if (car.Energy <= 0f)
                    {
                        car.Energy = 0f;
                        Lock();
                        _awaitRelease = true; // need a fresh press after the cooldown
                        input.side = input.boostBrake = 0f;
                    }
                }
            }
            else
            {
                if (_wasHolding && HoldTime > c.dashWindow) Lock();
                HoldTime = 0f;
            }
            _wasHolding = input.holding;

            void Lock()
            {
                _lockTimer = c.joystickCooldown;
                HoldTime = 0f;
            }
        }

        /// <summary>A swipe: sideways = side dash (CarController.SideDash), up / down = speed bonus fading over dashBoostDuration.</summary>
        void TryDash(Vector2 dir, float now)
        {
            if (dir.sqrMagnitude < 0.01f) return;
            var c = Tuning;
            var car = _me.Car;
            if (now < _dashReadyAt || car.Energy < c.dashCost || _race.State != RaceState.Racing) return;
            car.Energy -= c.dashCost;
            _dashReadyAt = now + c.dashCooldown;
            car.DashTimer = c.dashDuration;
            _me.Dashes++;

            var t = car.transform;
            if (Mathf.Abs(dir.x) > Mathf.Abs(dir.y))
            {
                // The nav point jumps across (racing_unity: width x 1.1) and the car physically travels exactly that far,
                // starting from where it is - the steering never has to drag it the rest of the way.
                float side = Mathf.Sign(dir.x);
                float from = NavFor(_me.Lateral);
                NavOffset = ClampNav(from + side * CarController.Size.x * c.dashSideDistance);
                float moved = Mathf.Max(Mathf.Abs(NavOffset - from), SideDashMinMove);
                car.SideDash(side, moved, c.dashSideTime);
            }
            else
            {
                bool forward = dir.y > 0f;
                _dashBonus = forward ? c.dashForwardSpeed : -c.dashBackSpeed;
                _dashBonusStart = now;
                _dashBonusTime = c.dashBoostDuration;
                car.Body.AddForce(t.forward * (forward ? DashKickForward : -DashKickBack), ForceMode.VelocityChange);
            }
        }

        /// <summary>Driving-logic safety only (doc: TTC braking stays, milder): lift / brake behind a car in my lane, by time to collision.</summary>
        float FrontSafety(float v, float lateral)
        {
            float limit = 1f;
            foreach (var other in _race.Cars)
            {
                if (other == _me) continue;
                float gap = other.Progress - _me.Progress;
                if (gap <= 0f || gap > SafetyRange) continue;
                if (Mathf.Abs(other.Lateral - lateral) > CarController.Size.x * SafetyLane || Mathf.Abs(other.Lateral - _me.Lateral) > CarController.Size.x * SafetyLane) continue;
                float closing = v - other.Car.ForwardSpeed;
                if (closing <= 0f) continue;
                float ttc = (gap - CarController.Size.z - SafetyGap) / closing;
                if (ttc < TtcBrake) limit = Mathf.Min(limit, SafetyBrake);
                else if (ttc < TtcLift) limit = Mathf.Min(limit, 0f);
            }
            return limit;
        }
    }
}
