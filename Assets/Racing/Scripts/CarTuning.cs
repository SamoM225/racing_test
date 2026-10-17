using System;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Every physical norm of a car in one place (SI units: m, s, kg, N; angles in degrees unless noted).
    /// Shared by every car - player and bots drive identical physics. Editable live on the RaceManager.
    /// The model behind the numbers is described in ARCHITECTURE.md ("Fyzika auta").
    /// </summary>
    [Serializable]
    public class CarTuning
    {
        [Header("Chassis")]
        [Tooltip("kg")] public float mass = 1200f;
        [Tooltip("Centre of gravity height / wheelbase. Sets how much load the brakes and the throttle move between the axles")]
        public float cogHeightRatio = 0.12f;

        [Header("Engine & brakes")]
        [Tooltip("m/s, physical limit (the driving logic sets the actual target speed)")] public float maxSpeed = 52f;
        [Tooltip("m/s² at standstill; fades with (speed / top speed)² to 0 at top speed")] public float acceleration = 13f;
        [Tooltip("m/s² at full brake")] public float brakeDeceleration = 24f;
        [Tooltip("Share of the braking force on the front axle (front + rear = 1)")] public float brakeFront = 0.6f;
        [Tooltip("Share of the braking force on the rear axle (front + rear = 1)")] public float brakeRear = 0.4f;
        [Tooltip("m/s")] public float reverseMaxSpeed = 8f;
        [Tooltip("Reverse acceleration as a share of the forward one")] public float reverseAccelShare = 0.6f;
        [Tooltip("Aerodynamic drag: deceleration (m/s²) = value x speed²")] public float airDrag = 0.0007f;

        [Header("Slipstream")]
        [Tooltip("Top speed gained at full slipstream (share)")] public float draftTopSpeed = 0.06f;
        [Tooltip("Air drag removed at full slipstream (share)")] public float draftDragCut = 0.45f;

        [Header("Steering")]
        [Tooltip("deg")] public float maxSteerAngle = 34f;
        [Tooltip("Share of steering left at max speed")] [Range(0.1f, 1f)] public float highSpeedSteer = 0.42f;
        [Tooltip("Steering lock per second (x max angle)")] public float steerResponse = 7f;

        [Header("Tyres: grip & slide")]
        [Tooltip("Tyre friction coefficient μ on asphalt (x surface grip)")] public float grip = 1.45f;
        [Tooltip("Rear grip relative to front; < 1 = the rear steps out first (drifts)")] [Range(0.5f, 1.2f)] public float rearGrip = 0.96f;
        [Tooltip("Load moved between the axles by brakes / throttle (x physical): braking lightens the rear = oversteer, " +
                 "throttle lightens the front = understeer")] [Range(0f, 1.5f)] public float weightTransfer = 0.8f;
        [Tooltip("0 = front wheel drive, 1 = rear wheel drive (power oversteer)")] [Range(0f, 1f)] public float driveBias = 0.65f;
        [Tooltip("How hard the tyre fights lateral slip (1 = kills it in one step)")] [Range(0.1f, 1f)] public float lateralStiffness = 0.6f;
        [Tooltip("m/s of sideways tyre slip where breakaway starts (static -> kinetic friction)")] public float slideStart = 1f;
        [Tooltip("m/s of sideways tyre slip where only kinetic friction is left")] public float slideFull = 3.5f;
        [Tooltip("deg; body slip angle from which a slide counts as a drift")] public float driftAngle = 10f;
        [Tooltip("N per (m/s)²")] public float downforce = 2.5f;

        [Header("Arcade help")]
        [Tooltip("Turns the velocity toward the nose while sliding (keeps speed in drifts)")] [Range(0f, 4f)] public float driftAssist = 1.4f;
        [Tooltip("How hard the steering controls the yaw rate while drifting")] [Range(0f, 6f)] public float driftControl = 2.5f;
        [Tooltip("rad/s; the yaw rate full steering asks for in a drift")] public float driftYawRate = 2.2f;
        [Tooltip("rad/s² per unit of tilt: rights the car in the air and on two wheels (small RC cars would land on the roof)")] public float airRighting = 16f;
        [Tooltip("1/s; damps the roll / pitch rate while righting")] public float airRightingDamping = 3f;

        [Header("Suspension")]
        [Tooltip("m")] public float suspensionLength = 0.45f;
        [Tooltip("m")] public float wheelRadius = 0.36f;
        [Tooltip("N/m per wheel")] public float spring = 22000f;
        [Tooltip("N·s/m per wheel")] public float damper = 2600f;

        [Header("Boost")]
        [Tooltip("m/s² extra while boosting")] public float boostAcceleration = 11f;
        [Tooltip("Energy pips per second while drifting at 30°")] public float driftEnergy = 0.5f;
        [Tooltip("Seconds of drift for mini-turbo tiers 1/2/3")] public Vector3 miniTurboTiers = new Vector3(0.8f, 1.6f, 2.6f);
        [Tooltip("Free boost seconds per mini-turbo tier")] public float miniTurboPerTier = 0.35f;

        [Header("Hits & damage (closing speed = m/s along the contact normal)")]
        [Tooltip("m/s; car-to-car hits below this are leaning, not a knock")] public float knockMinSpeed = 1.5f;
        [Tooltip("s of loose tyres after the lightest knock")] public float knockStunMin = 0.15f;
        [Tooltip("s of loose tyres after the hardest knock")] public float knockStunMax = 0.5f;
        [Tooltip("rad/s of spin per (lever arm m x closing speed m/s) of an off-centre hit")] public float knockSpin = 0.06f;
        [Tooltip("rad/s, spin cap of a knock")] public float knockSpinMax = 1f;
        [Tooltip("m/s; car-to-car hits cost HP above this")] public float knockDamageFrom = 3f;
        [Tooltip("HP per m/s above knockDamageFrom")] public float knockDamagePerMs = 2f;
        [Tooltip("m/s; wall hits cost HP above this")] public float wallDamageFrom = 5f;
        [Tooltip("HP per m/s above wallDamageFrom")] public float wallDamagePerMs = 2f;
        [Tooltip("HP a dash ram takes off the hit car, plus the ram strength (3-6)")] public float ramDamageBase = 3f;
        [Tooltip("HP the rammer loses")] public float ramSelfDamage = 1f;
        [Tooltip("Top speed and acceleration lost per damage level (0..3)")] public float damageSpeedLoss = 0.05f;
    }
}
