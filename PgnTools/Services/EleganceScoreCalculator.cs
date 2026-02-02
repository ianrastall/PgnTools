using System.Globalization;
using System.Text.Json;

namespace PgnTools.Services;

internal readonly record struct EleganceScore(
    int Score,
    double Soundness,
    double Coherence,
    double Tactical,
    double Quiet,
    double LengthPenalty);

internal readonly record struct EleganceEvaluationMetrics(
    int PlyCount,
    int EvaluatedPlyCount,
    double ForcingMovePercent,
    int QuietImprovementCount,
    int TrendBreakCount,
    int BlunderCount,
    int MistakeCount,
    int DubiousCount,
    double AverageAbsSwingCp,
    double EvalStdDevCp,
    int SoundSacrificeCount,
    int UnsoundSacrificeCount,
    int SoundSacrificeCp,
    int UnsoundSacrificeCp);

internal static class EleganceScoreCalculator
{
    private static readonly NormalizationDistributions Norms = NormalizationDistributions.Load();

    public static EleganceScore Calculate(in EleganceEvaluationMetrics metrics)
    {
        if (metrics.PlyCount <= 0)
        {
            return default;
        }

        var coverage = metrics.PlyCount > 0 ? metrics.EvaluatedPlyCount / (double)metrics.PlyCount : 0d;

        var soundnessRaw = metrics.AverageAbsSwingCp + (metrics.BlunderCount * 25d) + (metrics.MistakeCount * 10d) + (metrics.DubiousCount * 5d);
        var coherenceRaw = metrics.EvalStdDevCp + (metrics.TrendBreakCount * 18d);
        var tacticalRaw = metrics.ForcingMovePercent;
        var quietRaw = metrics.PlyCount > 0 ? metrics.QuietImprovementCount * 100d / metrics.PlyCount : 0d;

        // Sacrifice awareness: reward compensated material losses, penalize unsound ones.
        var soundSacrificeUnits = metrics.SoundSacrificeCp / 100d;
        var unsoundSacrificeUnits = metrics.UnsoundSacrificeCp / 100d;
        soundnessRaw += unsoundSacrificeUnits * 35d;
        quietRaw += (soundSacrificeUnits * 0.75d) + (metrics.SoundSacrificeCount * 0.35d);
        quietRaw -= (unsoundSacrificeUnits * 0.40d) + (metrics.UnsoundSacrificeCount * 0.25d);
        quietRaw = Math.Max(0d, quietRaw);

        var soundness = Norms.Normalize(DistributionType.Soundness, soundnessRaw);
        var coherence = Norms.Normalize(DistributionType.Coherence, coherenceRaw);
        var tactical = Norms.Normalize(DistributionType.TacticalDensity, tacticalRaw);
        var quiet = Norms.Normalize(DistributionType.QuietBrilliancy, quietRaw);
        var lengthPenalty = ComputeLengthPenalty(metrics.PlyCount);

        if (coverage < 0.8d)
        {
            var scale = Math.Clamp((coverage - 0.2d) / 0.6d, 0d, 1d);
            soundness *= scale;
            coherence *= scale;
            quiet *= scale;
        }

        var rawScore =
            (0.25d * soundness) +
            (0.20d * coherence) +
            (0.20d * tactical) +
            (0.30d * quiet) -
            (0.05d * lengthPenalty);

        var score = (int)Math.Round(Math.Clamp(rawScore, 0d, 100d), MidpointRounding.AwayFromZero);

        return new EleganceScore(score, soundness, coherence, tactical, quiet, lengthPenalty);
    }

    public static string FormatDetails(in EleganceScore elegance)
    {
        return FormattableString.Invariant(
            $"S={Math.Round(elegance.Soundness, MidpointRounding.AwayFromZero):0};C={Math.Round(elegance.Coherence, MidpointRounding.AwayFromZero):0};T={Math.Round(elegance.Tactical, MidpointRounding.AwayFromZero):0};Q={Math.Round(elegance.Quiet, MidpointRounding.AwayFromZero):0};L={Math.Round(elegance.LengthPenalty, MidpointRounding.AwayFromZero):0}");
    }

    private static double ComputeLengthPenalty(int plyCount)
    {
        const int threshold = 160;
        if (plyCount <= threshold)
        {
            return 0;
        }

        var distance = plyCount - threshold;
        return 100d * (1d - 1d / (1d + Math.Exp(-distance / 30d)));
    }

    private enum DistributionType
    {
        Soundness,
        Coherence,
        TacticalDensity,
        QuietBrilliancy
    }

    private sealed class NormalizationDistributions
    {
        private readonly Dictionary<DistributionType, DistributionBand> _bands;

        private NormalizationDistributions(Dictionary<DistributionType, DistributionBand> bands)
        {
            _bands = bands;
        }

        public static NormalizationDistributions Load()
        {
            var bands = CreateDefaultBands();
            var configPath = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(configPath))
            {
                return new NormalizationDistributions(bands);
            }

            try
            {
                using var stream = File.OpenRead(configPath);
                var config = JsonSerializer.Deserialize<NormalizationConfig>(stream);
                if (config?.Distributions == null)
                {
                    return new NormalizationDistributions(bands);
                }

                foreach (var entry in config.Distributions)
                {
                    if (!TryMapDistribution(entry.Key, out var type))
                    {
                        continue;
                    }

                    var value = entry.Value;
                    if (value == null || !value.P10.HasValue || !value.P90.HasValue || !value.HigherIsBetter.HasValue)
                    {
                        continue;
                    }

                    bands[type] = new DistributionBand(value.P10.Value, value.P90.Value, value.HigherIsBetter.Value);
                }
            }
            catch
            {
            }

            return new NormalizationDistributions(bands);
        }

        public double Normalize(DistributionType type, double rawValue)
        {
            if (!_bands.TryGetValue(type, out var band))
            {
                return 50;
            }

            return band.Normalize(rawValue);
        }

        private static Dictionary<DistributionType, DistributionBand> CreateDefaultBands()
        {
            return new Dictionary<DistributionType, DistributionBand>
            {
                { DistributionType.Soundness, new DistributionBand(45d, 280d, HigherIsBetter: false) },
                { DistributionType.Coherence, new DistributionBand(35d, 240d, HigherIsBetter: false) },
                { DistributionType.TacticalDensity, new DistributionBand(22d, 56d, HigherIsBetter: true) },
                { DistributionType.QuietBrilliancy, new DistributionBand(0.4d, 6.5d, HigherIsBetter: true) }
            };
        }

        private static string? ResolveConfigPath()
        {
            var primary = Path.Combine(AppContext.BaseDirectory, "Assets", "elegance-distributions.json");
            if (File.Exists(primary))
            {
                return primary;
            }

            var secondary = Path.Combine(AppContext.BaseDirectory, "elegance-distributions.json");
            return File.Exists(secondary) ? secondary : null;
        }

        private static bool TryMapDistribution(string key, out DistributionType type)
        {
            type = default;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.Trim().ToLowerInvariant() switch
            {
                "soundness" => Assign(DistributionType.Soundness, out type),
                "coherence" => Assign(DistributionType.Coherence, out type),
                "tacticaldensity" => Assign(DistributionType.TacticalDensity, out type),
                "quietbrilliancy" => Assign(DistributionType.QuietBrilliancy, out type),
                _ => false
            };
        }

        private static bool Assign(DistributionType mapped, out DistributionType type)
        {
            type = mapped;
            return true;
        }
    }

    private readonly record struct DistributionBand(double P10, double P90, bool HigherIsBetter)
    {
        public double Normalize(double rawValue)
        {
            if (P90 <= P10)
            {
                return 50;
            }

            var t = (rawValue - P10) / (P90 - P10);
            if (!HigherIsBetter)
            {
                t = 1d - t;
            }

            return Math.Clamp(t, 0d, 1d) * 100d;
        }
    }

    private sealed class NormalizationConfig
    {
        public Dictionary<string, DistributionConfigEntry>? Distributions { get; set; }
    }

    private sealed class DistributionConfigEntry
    {
        public double? P10 { get; set; }
        public double? P90 { get; set; }
        public bool? HigherIsBetter { get; set; }
    }
}
