using System;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Control values of racing_unity (TrackSettings / Dash / design doc). Same for the player and every bot.
    /// How they are used: AutoPilot (driving logic) and ARCHITECTURE.md ("Ovládanie").
    /// </summary>
    [Serializable]
    public class ControlTuning
    {
        [Header("Maneuver (joystick sideways)")]
        [Tooltip("m/s the nav point moves sideways at full stick")] public float maneuverSideSpeed = 10f;
        [Tooltip("m, max nav point offset from the racing line. racing_unity has ±7 m from its spline on a 14 m road; " +
                 "here the base is the racing line, so the road edge is the real limit")] public float maxDirection = 12f;

        [Header("Speed (joystick up/down)")]
        [Tooltip("m/s cruise on a straight at OrderSpeed 1 (racing_unity Formula: MaxSpeed 180 x 0.183)")] public float cruiseSpeed = 33f;
        [Tooltip("+50 % at full boost (doc: symmetric ±50 %)")] public float boostShare = 0.5f;
        [Tooltip("-50 % at full brake")] public float brakeShare = 0.5f;
        [Tooltip("s, how fast the speed order follows the stick")] public float speedBlendTime = 0.35f;
        [Tooltip("Share of the grip-limited corner speed used without boost (boost above this can slide)")] [Range(0.6f, 1f)] public float cornerMargin = 0.9f;

        [Header("Joystick rules")]
        [Tooltip("s, a hold shorter than this is a swipe window (no joystick, no cost)")] public float dashWindow = 0.8f;
        [Tooltip("s free after the joystick appears")] public float joystickFreeSeconds = 1f;
        [Tooltip("energy per second past the free time")] public float joystickDrainPerSecond = 0.5f;
        [Tooltip("s locked after releasing a visible joystick or running out of energy")] public float joystickCooldown = 3f;
        [Tooltip("Swipe / full stick distance as a share of the screen width")] public float swipeScreenFraction = 0.4f;

        [Header("Energy")]
        [Tooltip("pips")] public float energyMax = 10f;
        [Tooltip("pips at the start")] public float energyStart = 5f;
        [Tooltip("pips per second")] public float energyRegen = 0.65f;

        [Header("Dash (swipe)")]
        [Tooltip("pips")] public float dashCost = 1f;
        [Tooltip("s between dashes")] public float dashCooldown = 1f;
        [Tooltip("s the car counts as dashing (rams on contact)")] public float dashDuration = 0.8f;
        [Tooltip("m/s added to the target speed, fading out")] public float dashForwardSpeed = 12f;
        [Tooltip("m/s taken from the target speed, fading out")] public float dashBackSpeed = 10f;
        [Tooltip("s of the dash speed change")] public float dashBoostDuration = 1.2f;
        [Tooltip("x car width the car travels sideways on a side dash")] public float dashSideDistance = 2f;
        [Tooltip("s of the side dash thrust (sideways speed fades linearly to 0)")] public float dashSideTime = 0.5f;
    }
}
