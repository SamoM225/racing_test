using UnityEngine;

namespace Racing
{
    /// <summary>Drift score with combo, wall hits (clean driving), the race end and the final points.</summary>
    public partial class RaceManager
    {
        const float DriftPointsRate = 0.5f;     // points per (deg of slip x m/s) per s
        const float DriftGraceTime = 0.6f;      // s a drift may pause before it is banked
        const float DriftComboStep = 0.5f, DriftComboMax = 5f, DriftComboWindow = 2.5f; // +x per banked drift, cap, s to keep it
        const float PlayerFinishTimeout = 25f;  // s after the player's finish the race ends anyway
        const float NoPlayerTimeout = 60f;      // s after the winner (bot-only test races)

        void UpdateDrift(RaceCar rc, float dt)
        {
            if (State != RaceState.Racing || rc.Finished) return;
            var car = rc.Car;
            if (car.IsDrifting)
            {
                rc.CurrentDrift += Mathf.Abs(car.SlipAngle) * car.Speed * dt * DriftPointsRate;
                rc.DriftGrace = 0f;
                return;
            }
            if (rc.CurrentDrift > 0f)
            {
                rc.DriftGrace += dt;
                if (rc.DriftGrace > DriftGraceTime) BankDrift(rc);
                return;
            }
            rc.DriftComboTimer -= dt;
            if (rc.DriftComboTimer <= 0f) rc.DriftCombo = 1f;
        }

        void BankDrift(RaceCar rc)
        {
            if (rc.CurrentDrift < 1f) { rc.CurrentDrift = 0f; return; }
            float pts = rc.CurrentDrift * rc.DriftCombo;
            rc.DriftScore += pts;
            if (rc.IsPlayer) Say($"DRIFT {pts:N0}" + (rc.DriftCombo > 1f ? $"  x{rc.DriftCombo:0.0}" : ""), new Color(1f, 0.4f, 0.9f));
            rc.DriftCombo = Mathf.Min(rc.DriftCombo + DriftComboStep, DriftComboMax);
            rc.DriftComboTimer = DriftComboWindow;
            rc.CurrentDrift = 0f;
        }

        /// <summary>A wall hit loses the running drift and its combo, and costs clean-driving points.</summary>
        void OnWallHit(RaceCar rc, float impact)
        {
            if (State != RaceState.Racing || rc.Finished) return;
            rc.WallHits++;
            if (rc.CurrentDrift > 0f && rc.IsPlayer) Say("DRIFT LOST", new Color(1f, 0.3f, 0.3f));
            rc.CurrentDrift = 0f;
            rc.DriftCombo = 1f;
        }

        void CheckRaceEnd()
        {
            if (RaceOver || State != RaceState.Racing) return;
            bool allDone = Cars.TrueForAll(c => c.Finished);
            bool timeout = _playerFinishedAt >= 0f && RaceTime - _playerFinishedAt > PlayerFinishTimeout;
            bool noPlayer = Player == null && Cars.Exists(c => c.Finished) && RaceTime - Cars.Find(c => c.FinishPosition == 1).FinishTime > NoPlayerTimeout;
            if (!allDone && !timeout && !noPlayer) return;
            if (Player != null && !Player.Finished) return;
            FinishRace();
        }

        void FinishRace()
        {
            RaceOver = true;
            State = RaceState.Results;
            // Unfinished cars are classified by progress, but get no finish bonus.
            var unfinished = Cars.FindAll(c => !c.Finished);
            unfinished.Sort((a, b) => b.Progress.CompareTo(a.Progress));
            int pos = Cars.Count - unfinished.Count;
            foreach (var rc in unfinished) rc.FinishPosition = ++pos;

            float fastest = float.MaxValue;
            foreach (var rc in Cars) fastest = Mathf.Min(fastest, rc.BestLap);
            foreach (var rc in Cars)
            {
                BankDrift(rc);
                rc.Result = new Points
                {
                    placement = Points.PlacementTable[Mathf.Min(rc.FinishPosition - 1, Points.PlacementTable.Length - 1)],
                    checkpoints = Mathf.RoundToInt(rc.CheckpointPoints),
                    drift = Mathf.Min(Points.DriftMax, Mathf.RoundToInt(rc.DriftScore / Points.DriftDivisor)),
                    overtakes = Mathf.Min(Points.OvertakeMax, rc.Overtakes * Points.OvertakeEach),
                    dashHits = Mathf.Min(Points.DashHitMax, rc.DashHits * Points.DashHitEach),
                    fastestLap = rc.BestLap <= fastest && fastest < float.MaxValue ? Points.FastestLap : 0,
                    clean = Mathf.Max(0, Points.CleanMax - rc.WallHits * Points.CleanPerHit),
                    finish = rc.Finished ? Points.FinishBonus : 0,
                };
            }
        }

        /// <summary>Points so far (live HUD).</summary>
        public int LivePoints(RaceCar rc) =>
            Mathf.RoundToInt(rc.CheckpointPoints) + Mathf.Min(Points.DriftMax, Mathf.RoundToInt(rc.DriftScore / Points.DriftDivisor)) +
            Mathf.Min(Points.OvertakeMax, rc.Overtakes * Points.OvertakeEach) + Mathf.Min(Points.DashHitMax, rc.DashHits * Points.DashHitEach);
    }
}
