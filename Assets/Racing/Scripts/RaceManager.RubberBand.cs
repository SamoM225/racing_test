using System.Collections.Generic;
using UnityEngine;

namespace Racing
{
    /// <summary>
    /// The rubber band. racing_unity's handicap (RaceManager.AssignPositionAndSpeed, mode 3 "From Player"), written as
    /// OrderSpeed (car.Handicap) every tick. The driving logic smooths it (SmoothDamp, smoothTimeForSpeed), freezes it
    /// while the car maneuvers and applies it through (x + 1) / 2 - see AutoPilot.UpdateHandicap / TargetSpeedFor.
    /// </summary>
    public partial class RaceManager
    {
        const float RankFullGap = 25f;                          // m to the player where the place value is fully applied
        const float GapValuesFrom = 10f, GapValuesTo = 40f;     // m over which it blends into frontGapSpeed / rearGapSpeed
        const float LeadEaseFrom = 8f, LeadEaseTo = 50f;        // m of player lead over which the player eases off...
        const float LeadEaseMin = 0.5f;                         // ...down to this OrderSpeed (effective -25 %)
        const float LagHelpFrom = 15f, LagHelpTo = 50f;         // m the player is behind (last) over which it gets help...
        const float LagHelpMax = 1.4f;                          // ...up to this OrderSpeed (effective +20 %)
        const float PredictSeconds = 1.5f;                      // s the gap is looked ahead
        const float PredictMaxRate = 8f;                        // m/s cap of the gap's change rate
        const float BandFinalSprint = 0.85f;                    // share of the race from which bots are never held back

        void UpdateRubberBanding(float dt)
        {
            if (State != RaceState.Racing) return;
            var sorted = new List<RaceCar>(Cars);
            sorted.Sort((a, b) => a.Position.CompareTo(b.Position));
            foreach (var rc in Cars) rc.Car.Handicap = 1f;
            ApplyHandicap(sorted);

            // Final sprint: from 85 % of the race a bot is never held back (racing_unity RaceManager.cs:1252).
            foreach (var rc in Cars)
                if (rc.AI != null && !rc.IsPlayer && rc.Progress >= BandFinalSprint * laps * Track.Length)
                    rc.Car.Handicap = Mathf.Max(rc.Car.Handicap, 1f);
        }

        /// <summary>
        /// The game's mode 3 values by place (frontCarSpeeds / rearCarSpeeds), but blended in by the real distance to the
        /// player: soft right next to it, full at 25 m, the frontGapSpeed / rearGapSpeed values reached smoothly over
        /// 10..40 m instead of a step at 100 m. The player is not untouchable: a runaway leader eases off, a player stuck
        /// last gets a little help. The game is about damage points, so the field has to stay together.
        /// </summary>
        void ApplyHandicap(List<RaceCar> sorted)
        {
            int p = Player != null ? sorted.IndexOf(Player) : -1;
            if (p < 0 || Player.Finished) return;
            float vp = Player.Car.ForwardSpeed;
            for (int i = 0; i < sorted.Count; i++)
            {
                var car = sorted[i];
                if (car.Finished || i == p) continue;
                int k = Mathf.Abs(i - p) - 1;
                float rank = i < p ? frontCarSpeeds[Mathf.Min(k, frontCarSpeeds.Length - 1)] : rearCarSpeeds[Mathf.Min(k, rearCarSpeeds.Length - 1)];
                float opening = i < p ? car.Car.ForwardSpeed - vp : vp - car.Car.ForwardSpeed; // m/s the gap grows
                float gap = Ahead(Mathf.Abs(car.Progress - Player.Progress), opening);
                float s = Mathf.Lerp(1f, rank, Mathf.InverseLerp(0f, RankFullGap, gap));
                s = Mathf.Lerp(s, i < p ? frontGapSpeed : rearGapSpeed, Mathf.InverseLerp(GapValuesFrom, GapValuesTo, gap));
                car.Car.Handicap = s;
            }
            // The player: ease off when running away in front, a hand when left behind (effective -25 % / +20 % at most,
            // as strong as the game holds back the bots in front of the player).
            float lead = p == 0 && sorted.Count > 1 ? Ahead(Player.Progress - sorted[1].Progress, vp - sorted[1].Car.ForwardSpeed) : 0f;
            float lag = p == sorted.Count - 1 && sorted.Count > 1 ? Ahead(sorted[p - 1].Progress - Player.Progress, sorted[p - 1].Car.ForwardSpeed - vp) : 0f;
            Player.Car.Handicap = Mathf.Lerp(1f, LeadEaseMin, Mathf.InverseLerp(LeadEaseFrom, LeadEaseTo, lead)) * Mathf.Lerp(1f, LagHelpMax, Mathf.InverseLerp(LagHelpFrom, LagHelpTo, lag));

            // The band acts on the gap as it will be in 1.5 s, not as it is: a car pulling away is answered right away,
            // not once the gap is already open (plus the smoothing).
            static float Ahead(float gap, float opening) => Mathf.Max(0f, gap + Mathf.Clamp(opening, -PredictMaxRate, PredictMaxRate) * PredictSeconds);
        }
    }
}
