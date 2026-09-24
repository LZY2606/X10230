using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace ChronoScale.Core;

/// <summary>
/// Parses the domain JSON distribution representation into an <see cref="AgeDistribution"/>.
/// Empirical arrays are preserved verbatim (long tail kept); parametric forms are materialized with a
/// deterministic seed derived from the anchor identity so replay is stable.
/// </summary>
public static class DistributionFactory
{
    public sealed record Result(AgeDistribution Distribution, IReadOnlyList<Diagnostic> Diagnostics);

    public static Result Parse(JsonNode? node, string timeUnit, string entityId, string field, int? line)
    {
        var diags = new List<Diagnostic>();
        if (node is not JsonObject o)
        {
            diags.Add(new Diagnostic(Severity.Error, "DIST_MISSING", "distribution node is missing",
                entityId, field, line));
            return new(AgeDistribution.Unconvertible(timeUnit, null), diags);
        }

        string kindText = o["kind"]?.GetValue<string>()?.ToLowerInvariant() ?? "empirical";
        string rawText = o.ToJsonString();

        if (o["samples"] is JsonArray emp && emp.Count > 0)
        {
            var samples = new List<double>();
            int idx = 0;
            foreach (var item in emp)
            {
                if (item is JsonValue v && v.TryGetValue(out double d))
                    samples.Add(d);
                else if (item is JsonValue vs && vs.TryGetValue(out string? ss)
                    && double.TryParse(ss, NumberStyles.Float, CultureInfo.InvariantCulture, out var dp))
                    samples.Add(dp);
                else
                    diags.Add(new Diagnostic(Severity.Warning, "DIST_SAMPLE_UNCONVERTIBLE",
                        "sample could not be converted and was retained in place",
                        entityId, $"{field}.samples[{idx}]", line));
                idx++;
            }
            if (samples.Count < 2)
            {
                diags.Add(new Diagnostic(Severity.Error, "DIST_TOO_FEW",
                    "empirical distribution needs >= 2 convertible samples", entityId, field, line));
                return new(new AgeDistribution(DistributionKind.Unconvertible, timeUnit, samples, Raw: rawText), diags);
            }
            return new(new AgeDistribution(DistributionKind.Empirical, timeUnit, samples, Raw: rawText), diags);
        }

        if (o["components"] is JsonArray mix)
        {
            var comps = new List<AgeDistribution>();
            var weights = new List<double>();
            foreach (var c in mix)
            {
                var w = c?["weight"]?.GetValue<double>() ?? 1.0;
                var sub = Parse(c?["distribution"] ?? c, timeUnit, entityId, field, line);
                diags.AddRange(sub.Diagnostics);
                comps.Add(sub.Distribution);
                weights.Add(w);
            }
            return new(new AgeDistribution(DistributionKind.Mixture, timeUnit,
                comps.SelectMany(c => c.Samples).ToList(), comps, weights, rawText), diags);
        }

        // Parametric: materialize deterministically from seed (seed derived from identity if absent).
        if (o.TryGetPropertyValue("mean", out var meanNode) && meanNode is not null
            && o.TryGetPropertyValue("sigma", out var sigmaNode) && sigmaNode is not null)
        {
            if (!meanNode.AsValue().TryGetValue(out double mean) ||
                !sigmaNode.AsValue().TryGetValue(out double sigma))
            {
                diags.Add(new Diagnostic(Severity.Error, "DIST_PARAM_UNCONVERTIBLE",
                    "mean/sigma not convertible; value kept in place, not dropped for plotting",
                    entityId, field, line, rawText));
                return new(AgeDistribution.Unconvertible(timeUnit, rawText), diags);
            }
            ulong seed = o["seed"]?.GetValue<ulong?>()
                ?? (ulong)StableSeed(entityId + "|" + field);
            int n = o["draws"]?.GetValue<int?>() ?? 2000;
            var rng = new DeterministicRng(seed);
            bool log = kindText == "lognormal";
            var samples = new double[n];
            for (int i = 0; i < n; i++)
            {
                double z = rng.StandardNormal();
                samples[i] = log ? mean * Math.Exp(sigma * z) : mean + sigma * z;
            }
            if (log) Array.Sort(samples); else Array.Sort(samples);
            return new(new AgeDistribution(log ? DistributionKind.LogNormal : DistributionKind.Normal,
                timeUnit, samples, Raw: rawText), diags);
        }

        diags.Add(new Diagnostic(Severity.Error, "DIST_UNRECOGNIZED",
            "distribution has no samples/components/mean+sigma", entityId, field, line, rawText));
        return new(AgeDistribution.Unconvertible(timeUnit, rawText), diags);
    }

    public static ulong StableSeed(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return BitConverter.ToUInt64(hash, 0);
    }
}
