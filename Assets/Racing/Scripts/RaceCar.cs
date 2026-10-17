using UnityEngine;

namespace Racing
{
    public enum RaceState { Menu, Countdown, Racing, Results }
    public enum Difficulty { Easy, Normal, Hard }

    /// <summary>One car in the race: who drives it, where it is on the track, and its score. Plain data, owned by RaceManager.</summary>
    public class RaceCar
    {
        public string Name;
        public Color Color;
        public bool IsPlayer;
        public CarController Car;
        public IDriver Driver;   // AIDriver or PlayerDriver: produces the joystick input
        public AIDriver AI;
        public AutoPilot Pilot;  // turns the input into steering / throttle
        public int GridSlot;

        // Track position (Progress is continuous metres since the start line; negative on the grid).
        public int TrackIndex;
        public float Distance, Lateral, Progress; // m along the lap, m from the centre line (+ = right), m since the start
        public int LapsDone, Position, NextCheckpoint;
        public float LapStart, LastLap, BestLap = float.MaxValue;
        public bool Finished;
        public float FinishTime;
        public int FinishPosition;

        // Scoring.
        public float CheckpointPoints, DriftScore, CurrentDrift, DriftCombo = 1f, DriftComboTimer, DriftGrace;
        public int Overtakes, WallHits, Respawns, Dashes, DashHits;
        public string RespawnLog = "";
        public int PendingFromPosition;
        public float PendingSince = -1f;
        public float DamageDealt;   // HP taken off other cars
        public int Wrecks;
        public Points Result;

        // Recovery.
        public float BadTime, StuckTime; // s flipped / off the track, s stuck
        public bool Wrecked, Ghosted;
    }

    /// <summary>Final race points (the goal is points, not only the finish position - like the original minigame).</summary>
    public struct Points
    {
        public int placement, checkpoints, drift, overtakes, dashHits, fastestLap, clean, finish;
        public int Total => placement + checkpoints + drift + overtakes + dashHits + fastestLap + clean + finish;

        public static readonly int[] PlacementTable = { 200, 150, 120, 95, 75, 55, 40, 30 };
        public const int CheckpointPool = 200, DriftDivisor = 20, DriftMax = 250, OvertakeEach = 15, OvertakeMax = 150, DashHitEach = 10, DashHitMax = 100;
        public const int FastestLap = 50, CleanMax = 100, CleanPerHit = 10, FinishBonus = 100;
    }
}
