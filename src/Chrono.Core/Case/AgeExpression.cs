using Chrono.Core.Domain;
using Chrono.Core.Models;

namespace Chrono.Core.Case;

/// <summary>
/// Age expressions keep the distribution, never a mean:
///   N(mu±sigma BP)
///   MIX(w1*N(mu±sigma)+w2*N(mu±sigma) BP)   long-tailed calibration modes
///   EMP(v1,v2,v3,... BP)                     empirical posterior samples
///   FREE(...)                                unparseable -> retained verbatim
/// </summary>
public static class AgeExpression
{
    public static (AgeDistribution Dist, IReadOnlyList<Diagnostic> Diagnostics) Parse(string raw)
    {
        var text = raw.Trim();
        var unit = ExtractUnit(text);
        try
        {
            if (text.StartsWith("N(", StringComparison.OrdinalIgnoreCase))
                return (ParseNormal(Between(text, "N(", unitEnd(text)), unit), Array.Empty<Diagnostic>());
            if (text.StartsWith("MIX(", StringComparison.OrdinalIgnoreCase))
                return (ParseMixture(Between(text, "MIX(", unitEnd(text)), unit), Array.Empty<Diagnostic>());
            if (text.StartsWith("EMP(", StringComparison.OrdinalIgnoreCase))
                return (ParseEmpirical(Between(text, "EMP(", unitEnd(text)), unit), Array.Empty<Diagnostic>());
        }
        catch
        {
            return (new EmpiricalDist(new double[] { 0 }, "yr BP",
                new[] { new Diagnostic("AGE-SYNTAX", $"年龄表达式损坏：'{raw}'，原始表示保留", "", Severity.Warning) }),
                new[] { new Diagnostic("AGE-SYNTAX", $"年龄表达式损坏：'{raw}'，原始表示保留，不参与拟合", "", Severity.Warning) });
        }
        // Free text / unconvertible: retained at the source position.
        var d = new Diagnostic("AGE-UNCONV", $"年龄值无法转换为分布：'{raw}'，原值保留，不为绘图丢弃", "", Severity.Warning);
        return (new EmpiricalDist(new double[] { 0 }, unit.Length > 0 ? unit : "?", new[] { d }), new[] { d });
    }

    private static int unitEnd(string s)
    {
        var p = s.LastIndexOf(')');
        return p < 0 ? s.Length : p;
    }

    private static string Between(string s, string open, int closeParen) =>
        s.Substring(open.Length, closeParen - open.Length);

    private static string ExtractUnit(string s)
    {
        var tail = s[(s.LastIndexOf(')') + 1)..].Trim();
        return tail.Length > 0 ? tail : "yr BP";
    }

    private static NormalDist ParseNormal(string body, string unit)
    {
        // 12500±180 or 12500
        var parts = body.Split('±', StringSplitOptions.TrimEntries);
        var mu = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var sigma = parts.Length > 1 ? double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new NormalDist(mu, sigma, unit, Array.Empty<Diagnostic>());
    }

    private static MixtureDist ParseMixture(string body, string unit)
    {
        // 0.7*N(12500±180)+0.3*N(14200±900)
        var comps = new List<(double, NormalDist)>();
        foreach (var token in body.Split('+', StringSplitOptions.TrimEntries))
        {
            var star = token.IndexOf('*');
            var w = double.Parse(token[..star], System.Globalization.CultureInfo.InvariantCulture);
            var inner = token[(star + 2)..].TrimEnd(')');
            comps.Add((w, ParseNormal(inner, unit)));
        }
        var sum = comps.Sum(c => c.Item1);
        return new MixtureDist(comps.Select(c => (c.Item1 / sum, c.Item2)).ToList(), unit, Array.Empty<Diagnostic>());
    }

    private static EmpiricalDist ParseEmpirical(string body, string unit)
    {
        var vals = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (vals.Count < 2) throw new FormatException("EMP 至少需要两个样本");
        return new EmpiricalDist(vals, unit, Array.Empty<Diagnostic>());
    }
}
