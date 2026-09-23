using System.Globalization;
using ChronoCore.Quantities;

namespace ChronoCore.Import;

/// <summary>
/// 从导入文本解析年龄分布与时间基准。
/// 支持：radiocarbon(N±S)、calNormal(mu,sigma)、calUniform(low,high)、calLogNormalTail(median,sigmaLog,floor)。
/// 无法解析的文本必须原样保留并附诊断。
/// </summary>
public static class AgeTextParser
{
    public static bool TryParse(string text, out ParsedAge? parsed, out string? diagnostic)
    {
        parsed = null; diagnostic = null;
        var t = text.Trim();
        try
        {
            int open = t.IndexOf('(');
            if (open <= 0 || !t.EndsWith(")"))
                return Fail(text, ref diagnostic);
            var name = t[..open].Trim();
            var args = t[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
            double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

            switch (name)
            {
                case "radiocarbon" when args.Length == 2:
                {
                    var (mu, sig) = ParsePlusMinus(args[0]);
                    parsed = new ParsedAge(TimeBasis.RadiocarbonBP, mu, sig, null);
                    return true;
                }
                case "calNormal" when args.Length == 2:
                    parsed = new ParsedAge(TimeBasis.CalBP, Num(args[0]), Num(args[1]),
                        new NormalAge(Num(args[0]), Num(args[1]), TimeBasis.CalBP));
                    return true;
                case "calUniform" when args.Length == 2:
                {
                    double lo = Num(args[0]), hi = Num(args[1]);
                    if (hi <= lo) return Fail(text, ref diagnostic, "calUniform 上界必须大于下界");
                    parsed = new ParsedAge(TimeBasis.CalBP, (lo + hi) / 2, (hi - lo) / Math.Sqrt(12),
                        new UniformAge(lo, hi, TimeBasis.CalBP));
                    return true;
                }
                case "calLogNormalTail" when args.Length == 3:
                {
                    double med = Num(args[0]), sl = Num(args[1]), fl = Num(args[2]);
                    if (med <= 0 || sl <= 0 || fl < 0) return Fail(text, ref diagnostic, "calLogNormalTail 参数非法");
                    var dist = new LogNormalLongTailAge(med, sl, fl, TimeBasis.CalBP);
                    parsed = new ParsedAge(TimeBasis.CalBP, dist.Mean, dist.Quantile(0.84) - dist.Quantile(0.5), dist);
                    return true;
                }
                default:
                    return Fail(text, ref diagnostic);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return Fail(text, ref diagnostic);
        }
    }

    private static (double Mu, double Sigma) ParsePlusMinus(string s)
    {
        var parts = s.Split('±', StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new FormatException();
        return (double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static bool Fail(string text, ref string? diagnostic, string? extra = null)
    {
        diagnostic = $"无法解析/转换年龄「{text}」；原值保留在原位" + (extra is null ? "" : "：" + extra);
        return false;
    }
}

public sealed record ParsedAge(TimeBasis Basis, double Center, double Sigma, AgeDistribution? Distribution);
