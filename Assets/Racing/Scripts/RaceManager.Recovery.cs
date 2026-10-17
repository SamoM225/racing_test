using UnityEngine;

namespace Racing
{
    /// <summary>
    /// Getting a car back into the race: respawn as the last resort (the pilot's three-point turn and the car's roll back
    /// onto its wheels come first), the ghost after a respawn, wrecks at 0 HP, sparks / camera shake on hits.
    /// </summary>
    public partial class RaceManager
    {
        const float FlippedUpY = 0.25f;                 // car up.y below it = flipped
        const float FallBelow = 6f;                     // m under the road = fell off
        const float OutBeyond = 3f;                     // m past the run-off = off the track
        const float LostRespawn = 1.5f;                 // s fallen / off the track before the respawn
        const float FlippedRespawn = 3f;                // s flipped (the car tries to roll back first)
        const float StuckRespawn = 6f;                  // s stuck (the three-point turn tries first)
        const float StartGrace = 5f;                    // s at the start nobody counts as stuck
        const float StandSpeed = 0.8f;                  // m/s = standing
        const float CrawlSpeed = 3f, CrawlWanted = 15f; // m/s crawling although the target speed is above CrawlWanted
        const float RecoverButtonStuck = 1.5f, RecoverButtonBad = 0.5f; // s before the player may press RECOVER
        const float GhostSeconds = 2f;                  // s passing through the others after a respawn
        const float GhostClearLengths = 1.2f;           // x car length to everybody before the ghost ends
        const float RespawnEdge = 2.5f;                 // m from the road edge
        const float RespawnClear = 8f, RespawnLane = 3f, RespawnShift = 4f; // m: don't drop onto someone
        const float RespawnSpeed = 10f;                 // m/s rejoining

        void CheckRespawn(RaceCar rc, float dt)
        {
            UpdateGhost(rc);
            if (State != RaceState.Racing) return;
            var t = rc.Car.transform;
            if (rc.Wrecked)
            {
                rc.Wrecked = false;
                rc.Wrecks++;
                rc.RespawnLog += $"wreck@{rc.Distance:0} ";
                OnImpact(rc, t.position, 15f);
                Respawn(rc);
                rc.Car.Repair();
                return;
            }
            bool flipped = t.up.y < FlippedUpY;
            bool lost = t.position.y < Track.Center[rc.TrackIndex].y - FallBelow
                        || (Mathf.Abs(rc.Lateral) > Track.HalfWidth + Track.Runoff + OutBeyond && !Track.Crossing[rc.TrackIndex]); // at a crossing it is on the other road
            rc.BadTime = flipped || lost ? rc.BadTime + dt : 0f;
            // Stuck: standing, crawling although it wants to go fast, or turning round. The pilot's three-point turn and the
            // car's roll back onto its wheels get their chance first; the respawn is the last resort.
            bool crawling = rc.Car.Speed < CrawlSpeed && rc.Pilot.TargetSpeed > CrawlWanted;
            rc.StuckTime = RaceTime > StartGrace && (rc.Car.Speed < StandSpeed || crawling || rc.Pilot.Recovering) ? rc.StuckTime + dt : 0f;
            if (rc.BadTime > (lost ? LostRespawn : FlippedRespawn) || rc.StuckTime > StuckRespawn)
            {
                rc.RespawnLog += (rc.StuckTime > StuckRespawn ? "stuck" : flipped ? "flip" : t.position.y < Track.Center[rc.TrackIndex].y - FallBelow ? "fell" : "out")
                                 + $"@{rc.Distance:0}{(rc.Car.SinceKnock < 4f ? $"(knock {rc.Car.SinceKnock:0.0}s)" : "")} ";
                Respawn(rc);
            }
        }

        /// <summary>The player may call the recover as soon as the car is clearly in trouble (button / R), not only after the timeout.</summary>
        public bool CanRecover(RaceCar rc) => State == RaceState.Racing && !rc.Finished && rc.Car.GhostTime <= 0f
                                               && (rc.StuckTime > RecoverButtonStuck || rc.BadTime > RecoverButtonBad || rc.Pilot.Recovering);

        /// <summary>Back on the racing line where the car is, facing the road, rolling, as a ghost for a moment.</summary>
        public void Respawn(RaceCar rc)
        {
            int idx = rc.TrackIndex;
            float lateral = Mathf.Clamp(Track.LineOffset[idx], -Track.HalfWidth + RespawnEdge, Track.HalfWidth - RespawnEdge);
            foreach (var other in Cars) // don't drop onto someone
                if (other != rc && Mathf.Abs(other.Progress - rc.Progress) < RespawnClear && Mathf.Abs(other.Lateral - lateral) < RespawnLane)
                    lateral = other.Lateral > 0f ? other.Lateral - RespawnShift : other.Lateral + RespawnShift;
            var t = rc.Car.transform;
            t.position = Track.Center[idx] + Track.Right[idx] * lateral + Vector3.up * 0.8f;
            t.rotation = Quaternion.LookRotation(Track.Tangent[idx]);
            rc.Car.Body.position = t.position;
            rc.Car.Body.rotation = t.rotation;
            rc.Car.ResetMotion();
            rc.Car.Body.linearVelocity = Track.Tangent[idx] * RespawnSpeed;
            rc.Pilot.Reset(lateral - Track.LineOffset[idx]);
            rc.Car.GhostTime = GhostSeconds;
            SetGhost(rc, true);
            rc.Respawns++;
            rc.BadTime = rc.StuckTime = 0f;
            rc.CurrentDrift = 0f;
            if (rc.IsPlayer) Say("RESPAWN", Color.gray);
        }

        /// <summary>After a recover the car passes through the others for a moment (and until it is clear of them).</summary>
        void SetGhost(RaceCar rc, bool on)
        {
            var col = rc.Car.GetComponent<Collider>();
            foreach (var other in Cars)
                if (other != rc && (on || !other.Ghosted))
                    Physics.IgnoreCollision(col, other.Car.GetComponent<Collider>(), on);
            rc.Ghosted = on;
        }

        void UpdateGhost(RaceCar rc)
        {
            if (!rc.Ghosted || rc.Car.GhostTime > 0f) return;
            foreach (var other in Cars)
                if (other != rc && (other.Car.transform.position - rc.Car.transform.position).magnitude < CarController.Size.z * GhostClearLengths)
                {
                    rc.Car.GhostTime = 0.3f; // still inside someone
                    return;
                }
            SetGhost(rc, false);
        }

        static readonly bool DamageTrace = System.Environment.GetEnvironmentVariable("RACING_TRACE") == "1";

        /// <summary>Damage bookkeeping: who dealt it, and a wreck at 0 HP (respawned on the next tick, repaired).</summary>
        void OnDamaged(RaceCar rc, float amount, CarController source)
        {
            if (State != RaceState.Racing || rc.Finished) return;
            var attacker = source != null ? Cars.Find(c => c.Car == source) : null;
            if (DamageTrace) Debug.Log($"DMG t={RaceTime:0.0} {rc.Name} -{amount:0} from {attacker?.Name ?? "wall"} hp {rc.Car.HP:0} @{rc.Distance:0} v{rc.Car.Speed:0} dashT {(attacker != null ? attacker.Car.DashTimer : 0f):0.0}");
            if (attacker != null) attacker.DamageDealt += amount;
            if (rc.Car.HP > 0f || rc.Wrecked) return;
            rc.Wrecked = true; // respawned on the next tick (not inside the physics contact callback)
            if (rc.IsPlayer) Say("WRECKED", new Color(1f, 0.3f, 0.2f));
            else if (attacker != null && attacker.IsPlayer) Say($"{rc.Name} WRECKED", new Color(1f, 0.6f, 0.2f));
        }

        ParticleSystem _sparks;

        /// <summary>Sparks at the contact, and the camera feels hits on the car it follows.</summary>
        void OnImpact(RaceCar rc, Vector3 point, float strength)
        {
            if (_sparks == null)
            {
                var go = new GameObject("Sparks");
                go.transform.SetParent(transform, false);
                _sparks = go.AddComponent<ParticleSystem>();
                _sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _sparks.main;
                main.playOnAwake = false;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 12f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.3f), new Color(1f, 0.45f, 0.1f));
                main.gravityModifier = 1.5f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = _sparks.emission;
                emission.enabled = false;
                var shape = _sparks.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.1f;
                go.GetComponent<ParticleSystemRenderer>().sharedMaterial = Mats.Unlit(Color.white);
            }
            _sparks.transform.position = point;
            _sparks.Emit(Mathf.Clamp(Mathf.RoundToInt(strength * 2f), 4, 30));
            var cam = Camera.main != null ? Camera.main.GetComponent<ChaseCamera>() : null;
            if (cam != null && cam.target == rc.Car) cam.Shake(strength * 0.08f);
        }
    }
}
