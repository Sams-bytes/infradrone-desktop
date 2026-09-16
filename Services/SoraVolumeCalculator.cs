// Services/SoraVolumeCalculator.cs
//
// Implements the minimum-dimension calculations for the Contingency Volume (CV)
// and Ground Risk Buffer (GRB) exactly as published in:
//   JARUS guidelines on SORA, Annex A, Edition 2.5 (13.05.2024), section A.5.2
//   "Guidelines on collecting and presenting system and operation information"
// Every formula below is cited to its Annex A paragraph. Default assumption
// values are the Annex's own standard values (A.5.2.3). Nothing is invented.
//
// Self-check: Verify() reproduces the six worked examples printed in the Annex.

using System;

namespace InfraDroneDesktop.Services
{
    public enum SoraAirframe { Multirotor, FixedWing }

    public enum GrbMethod
    {
        OneToOneRule,      // A.5.2.4 "Simplified approach: 1:1 rule"
        Ballistic,         // A.5.2.4 — rotorcraft/multirotor ONLY
        Parachute,         // A.5.2.4 "Termination with parachute"
        FixedWingGlide,    // A.5.2.4 "Power is switched off", glide ratio E
        FixedWingNoGlide   // A.5.2.4 controls set so no gliding -> 1:1 rule
    }

    public enum ContingencyManoeuvre
    {
        StopOrTurn,   // multirotor: stop to hover / fixed-wing: 180° turn
        Parachute     // parachute triggered on leaving the FG
    }

    /// <summary>Inputs. Defaults = Annex A standard assumptions (A.5.2.3).</summary>
    public sealed class SoraVolumeParameters
    {
        public SoraAirframe Airframe { get; set; } = SoraAirframe.Multirotor;

        // A.5.2.1 — required UA / operation data (A.2 form fields 0.6, 0.8)
        public double V0 { get; set; }               // max operational speed, m/s
        public double CD { get; set; }               // max characteristic dimension, m
        public double HFG { get; set; }              // height of flight geography, m
        public double VWind { get; set; } = 3.0;     // max permissible wind, m/s

        // A.5.2.3 CV horizontal — standard assumptions
        public double SGNSS { get; set; } = 3.0;     // GNSS accuracy, m
        public double SPos { get; set; } = 3.0;      // position holding error, m
        public double SK { get; set; } = 1.0;        // map error, m
        public double TR { get; set; } = 1.0;        // reaction time, s (may be < 1 s for automatic geofence)
        public double MaxPitchDeg { get; set; } = 45.0;   // multirotor Θmax ≤ 45°
        public double MaxRollDeg { get; set; } = 30.0;    // fixed-wing Φmax ≤ 30°
        public ContingencyManoeuvre Manoeuvre { get; set; } = ContingencyManoeuvre.StopOrTurn;
        public double TParachute { get; set; } = 0.0;     // time to open parachute, s
        public double SAdd { get; set; } = 0.0;           // additional horizontal distance, m

        // A.5.2.3 CV vertical
        public bool BarometricAltitude { get; set; } = true; // HAM = 1 m baro, 4 m GNSS
        public double HAdd { get; set; } = 0.0;              // additional vertical distance, m

        // A.5.2.4 GRB
        public GrbMethod GrbMethod { get; set; } = GrbMethod.OneToOneRule;
        public double GlideRatio { get; set; } = 0.0;   // E = CL/CD, fixed-wing glide only
        public double VzParachute { get; set; } = 0.0;  // descent rate under parachute, m/s

        public const double G = 9.81; // m/s², value used in the Annex examples
    }

    public sealed class SoraVolumeResult
    {
        // Horizontal widths (metres, measured outward from the previous boundary)
        public double SFGmin { get; init; }  // A.5.2.2: S_FG ≥ 3·CD
        public double SCV { get; init; }     // A.5.2.3
        public double SGRB { get; init; }    // A.5.2.4
        // Vertical
        public double HFG { get; init; }
        public double HCV { get; init; }     // A.5.2.3
        // Components, for the operations-manual "Calculation of CV / GRB" section
        public double SR { get; init; }
        public double SCM { get; init; }
        public double HAM { get; init; }
        public double HR { get; init; }
        public double HCM { get; init; }
        public string Method { get; init; } = "";
    }

    public static class SoraVolumeCalculator
    {
        public static SoraVolumeResult Compute(SoraVolumeParameters p)
        {
            if (p.V0 <= 0) throw new ArgumentException("V0 must be > 0 (A.2 field 0.8).");
            if (p.CD <= 0) throw new ArgumentException("CD must be > 0 (A.2 field 0.6).");
            if (p.HFG <= 0) throw new ArgumentException("HFG must be > 0.");
            if (p.GrbMethod == GrbMethod.Ballistic && p.Airframe != SoraAirframe.Multirotor)
                throw new ArgumentException("Ballistic GRB is only permitted for rotorcraft/multirotors (A.5.2.4).");

            double g = SoraVolumeParameters.G;

            // ---- A.5.2.3 CV horizontal ----
            double sR = p.V0 * p.TR;                         // S_R = V0 · tR
            double sCM;
            if (p.Manoeuvre == ContingencyManoeuvre.Parachute)
                sCM = p.V0 * p.TParachute;                   // S_CM = V0 · tP
            else if (p.Airframe == SoraAirframe.Multirotor)
                sCM = 0.5 * p.V0 * p.V0 / (g * Math.Tan(Deg(p.MaxPitchDeg)));   // ½·V0²/(g·tanΘ)
            else
                sCM = p.V0 * p.V0 / (g * Math.Tan(Deg(p.MaxRollDeg)));          // V0²/(g·tanΦ)

            double sCV = p.SGNSS + p.SPos + p.SK + sR + sCM + p.SAdd;           // S_CV

            // ---- A.5.2.3 CV vertical ----
            double hAM = p.BarometricAltitude ? 1.0 : 4.0;   // H_Baro = 1 m, H_GNSS = 4 m
            double hR = p.V0 * 0.7 * p.TR;                   // H_R = V0 · 0.7 · tR
            double hCM;
            if (p.Manoeuvre == ContingencyManoeuvre.Parachute)
                hCM = p.V0 * p.TParachute * 0.7;             // H_CM = V0 · tP · 0.7
            else if (p.Airframe == SoraAirframe.Multirotor)
                hCM = 0.5 * p.V0 * p.V0 / g;                 // ½·V0²/g
            else
                hCM = p.V0 * p.V0 / g * 0.3;                 // V0²/g · 0.3

            double hCV = p.HFG + hAM + hR + hCM + p.HAdd;    // H_CV

            // ---- A.5.2.4 GRB horizontal ----
            double halfCD = 0.5 * p.CD;
            double sGRB; string method;
            switch (p.GrbMethod)
            {
                case GrbMethod.Ballistic:
                    sGRB = p.V0 * Math.Sqrt(2.0 * hCV / g) + halfCD;
                    method = "Ballistic (A.5.2.4)"; break;
                case GrbMethod.Parachute:
                    if (p.VzParachute <= 0) throw new ArgumentException("VzParachute must be > 0.");
                    double vWind = Math.Max(p.VWind, 3.0);   // values below 3 m/s not considered realistic
                    sGRB = p.V0 * p.TParachute + vWind * hCV / p.VzParachute;
                    method = "Parachute termination (A.5.2.4)"; break;
                case GrbMethod.FixedWingGlide:
                    if (p.GlideRatio <= 0) throw new ArgumentException("GlideRatio (E) must be > 0.");
                    sGRB = p.GlideRatio * hCV;
                    method = "Fixed-wing power-off glide, E=" + p.GlideRatio + " (A.5.2.4)"; break;
                case GrbMethod.FixedWingNoGlide:
                case GrbMethod.OneToOneRule:
                default:
                    sGRB = hCV + halfCD;
                    method = "1:1 rule (A.5.2.4)"; break;
            }

            return new SoraVolumeResult
            {
                SFGmin = 3.0 * p.CD, SCV = sCV, SGRB = sGRB,
                HFG = p.HFG, HCV = hCV,
                SR = sR, SCM = sCM, HAM = hAM, HR = hR, HCM = hCM, Method = method
            };
        }

        private static double Deg(double d) => d * Math.PI / 180.0;

        /// <summary>
        /// Reproduces the worked examples printed in Annex A §A.5.2.3/§A.5.2.4.
        /// Returns null if all match, otherwise a description of the first mismatch.
        /// </summary>
        public static string? Verify()
        {
            var mr = Compute(new SoraVolumeParameters { Airframe = SoraAirframe.Multirotor, V0 = 10, CD = 1.5, HFG = 100 });
            if (!Near(mr.SCV, 22.1, 0.05)) return $"Multirotor SCV {mr.SCV:F2} != 22.1";
            if (!Near(mr.HCV, 113.1, 0.05)) return $"Multirotor HCV {mr.HCV:F2} != 113.1";
            if (!Near(mr.SGRB, 113.85, 0.01)) return $"Multirotor 1:1 SGRB {mr.SGRB:F2} != 113.85";

            var mrB = Compute(new SoraVolumeParameters { Airframe = SoraAirframe.Multirotor, V0 = 10, CD = 1.5, HFG = 100, GrbMethod = GrbMethod.Ballistic });
            if (!Near(mrB.SGRB, 48.77, 0.01)) return $"Multirotor ballistic SGRB {mrB.SGRB:F2} != 48.77";

            var fw = Compute(new SoraVolumeParameters { Airframe = SoraAirframe.FixedWing, V0 = 30, CD = 3, HFG = 100, GrbMethod = GrbMethod.FixedWingGlide, GlideRatio = 20 });
            if (!Near(fw.SCV, 195.9, 0.05)) return $"Fixed-wing SCV {fw.SCV:F2} != 195.9";
            if (!Near(fw.HCV, 149.52, 0.01)) return $"Fixed-wing HCV {fw.HCV:F2} != 149.52";
            if (!Near(fw.SGRB, 2990.4, 0.2)) return $"Fixed-wing glide SGRB {fw.SGRB:F1} != 2990.4";

            return null;
        }

        private static bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;
    }
}
