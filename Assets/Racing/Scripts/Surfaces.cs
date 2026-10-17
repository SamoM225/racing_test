using System;
using UnityEngine;

namespace Racing
{
    /// <summary>racing_unity TrackSurfaceMaterial, same ids.</summary>
    public enum SurfaceType { Gravel, Asphalt, Grass, Tarmac, Dirtway, Slickroad, Mud, Sand, Snow, Ice, Cobblestone, WoodenPlanks }

    /// <summary>What the tyres feel. Multiplies the car's own values (car value x surface value), like the original game.</summary>
    [Serializable]
    public class SurfaceDef
    {
        public SurfaceType type;
        [Tooltip("Tyre friction multiplier (lateral + longitudinal). Lower = slides earlier.")]
        public float grip = 1f;
        [Tooltip("Rolling resistance, 1/s (deceleration = value x speed).")]
        public float rollingDrag = 0.02f;
        [Tooltip("Top speed multiplier.")]
        public float maxSpeed = 1f;
        [Tooltip("How easily a slide keeps going once the tyre breaks loose (kinetic / static friction).")]
        [Range(0.3f, 1f)] public float slideFriction = 0.85f;
        public Color color = Color.gray;

        // Grip follows the order of racing_unity LateralGrip (VehiclePhysicsTuning) compressed to 0.45..1: in the game a
        // surface mostly changes the side slide, not the pace. Max speed = its maxSpeedCoefficient.
        // Grass is the run-off.
        public static SurfaceDef[] Defaults() => new[]
        {
            //                          grip   drag   max    slide  (grip x car μ, rolling drag 1/s, top speed share, kinetic/static)
            S(SurfaceType.Gravel,      0.90f, 0.03f, 1.00f, 0.85f, new Color(0.62f, 0.58f, 0.52f)),
            S(SurfaceType.Asphalt,      1.00f, 0.015f, 1.00f, 0.85f, new Color(0.24f, 0.24f, 0.27f)),
            S(SurfaceType.Grass,        0.60f, 0.15f, 0.75f, 0.75f, new Color(0.33f, 0.55f, 0.24f)), // run-off: slows, never stops a car
            S(SurfaceType.Tarmac,       0.97f, 0.015f, 1.00f, 0.85f, new Color(0.30f, 0.30f, 0.33f)),
            S(SurfaceType.Dirtway,      0.80f, 0.04f, 0.85f, 0.80f, new Color(0.55f, 0.40f, 0.26f)),
            S(SurfaceType.Slickroad,    0.62f, 0.02f, 1.00f, 0.90f, new Color(0.18f, 0.20f, 0.24f)),
            S(SurfaceType.Mud,          0.70f, 0.06f, 1.00f, 0.80f, new Color(0.38f, 0.27f, 0.17f)),
            S(SurfaceType.Sand,         0.75f, 0.05f, 1.00f, 0.80f, new Color(0.86f, 0.72f, 0.48f)),
            S(SurfaceType.Snow,         0.62f, 0.04f, 1.00f, 0.88f, new Color(0.84f, 0.87f, 0.92f)),
            S(SurfaceType.Ice,          0.45f, 0.005f, 1.00f, 0.92f, new Color(0.70f, 0.87f, 0.97f)),
            S(SurfaceType.Cobblestone,  0.88f, 0.04f, 1.00f, 0.85f, new Color(0.48f, 0.46f, 0.44f)),
            S(SurfaceType.WoodenPlanks, 0.82f, 0.03f, 1.00f, 0.85f, new Color(0.55f, 0.38f, 0.22f)),
        };

        static SurfaceDef S(SurfaceType t, float grip, float drag, float max, float slide, Color c) =>
            new SurfaceDef { type = t, grip = grip, rollingDrag = drag, maxSpeed = max, slideFriction = slide, color = c };
    }

    /// <summary>
    /// The virtual joystick of racing_unity. The game drives the car along the track; the driver only shifts it
    /// sideways, speeds it up / slows it down and dashes. Player (finger) and bots (virtual thumb) produce the same thing.
    /// </summary>
    public struct DriverInput
    {
        public bool holding;     // finger on the screen (joystick rules: free window, energy drain, cooldown)
        public float side;       // -1 .. +1 maneuver: moves the nav point sideways at ManeuverSideSpeed
        public float boostBrake; // -1 brake .. +1 boost (±50 % speed)
        public Vector2 dash;     // swipe direction (x side, y forward); zero = no dash
    }

    public interface IDriver
    {
        DriverInput GetInput(float dt);
    }

    /// <summary>Snapshot of one car, recorded every physics step so bots can see the race with a reaction delay.</summary>
    public struct CarState
    {
        public Vector3 position;
        public float progress, lateral, speed;
    }

    static class Mats
    {
        static Material _lit, _unlit;

        // Resources/RacingLit.mat (Standard) is always in the build and so is its shader; the primitives' default material
        // is not (nothing in the scene uses Standard, a player build strips it: everything rendered magenta).
        public static Material Lit(Color c)
        {
            if (_lit == null) _lit = Resources.Load<Material>("RacingLit");
            if (_lit == null)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _lit = g.GetComponent<Renderer>().sharedMaterial;
                Kill(g);
            }
            return new Material(_lit) { color = c };
        }

        public static Material Unlit(Color c)
        {
            if (_unlit == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null) return Lit(c);
                _unlit = new Material(shader);
            }
            return new Material(_unlit) { color = c };
        }

        public static void Kill(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        /// <summary>Visual-only primitive (collider removed).</summary>
        public static Transform Prim(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 localScale, Color color, string name = null)
        {
            var g = GameObject.CreatePrimitive(type);
            if (name != null) g.name = name;
            Kill(g.GetComponent<Collider>());
            g.GetComponent<Renderer>().sharedMaterial = Lit(color);
            g.transform.SetParent(parent, false);
            g.transform.localPosition = localPos;
            g.transform.localScale = localScale;
            return g.transform;
        }
    }
}
