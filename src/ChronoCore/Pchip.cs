namespace ChronoCore;

/// <summary>
/// 单调三次 Hermite（Fritsch–Carlson PCHIP）。在连续段内保证不越过数据点的单调性；
/// 线性模式作为另一套候选谱系并存。
/// </summary>
public static class Pchip
{
    public static double[] Slopes(double[] xs, double[] ys)
    {
        var n = xs.Length;
        var h = new double[n - 1];
        var delta = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            h[i] = xs[i + 1] - xs[i];
            delta[i] = h[i] == 0 ? 0 : (ys[i + 1] - ys[i]) / h[i];
        }
        var d = new double[n];
        if (n == 2) { d[0] = d[1] = delta[0]; return d; }
        d[0] = ((2 * h[0] + h[1]) * delta[0] - h[0] * delta[1]) / (h[0] + h[1]);
        if (Sign(d[0]) != Sign(delta[0])) d[0] = 0;
        else if (Sign(delta[0]) != Sign(delta[1]) && Math.Abs(d[0]) > Math.Abs(3 * delta[0]))
            d[0] = 3 * delta[0];
        d[n - 1] = ((2 * h[n - 2] + h[n - 3]) * delta[n - 2] - h[n - 2] * delta[n - 3]) / (h[n - 2] + h[n - 3]);
        if (Sign(d[n - 1]) != Sign(delta[n - 2])) d[n - 1] = 0;
        else if (Sign(delta[n - 2]) != Sign(delta[n - 3]) && Math.Abs(d[n - 1]) > Math.Abs(3 * delta[n - 2]))
            d[n - 1] = 3 * delta[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            if (delta[i - 1] == 0 || delta[i] == 0 || Sign(delta[i - 1]) != Sign(delta[i])) { d[i] = 0; continue; }
            var w1 = 2 * h[i] + h[i - 1];
            var w2 = h[i] + 2 * h[i - 1];
            d[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
        }
        return d;
    }

    private static int Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : 0;

    public static double Interpolate(double[] xs, double[] ys, double[] d, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var i = Array.BinarySearch(xs, x);
        if (i >= 0) return ys[i];
        i = ~i - 1;
        var h = xs[i + 1] - xs[i];
        var t = (x - xs[i]) / h;
        var t2 = t * t; var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + t;
        var h01 = -2 * t3 + 3 * t2;
        var h11 = t3 - t2;
        return h00 * ys[i] + h10 * h * d[i] + h01 * ys[i + 1] + h11 * h * d[i + 1];
    }

    public static double Linear(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var i = Array.BinarySearch(xs, x);
        if (i >= 0) return ys[i];
        i = ~i - 1;
        var t = (x - xs[i]) / (xs[i + 1] - xs[i]);
        return ys[i] + t * (ys[i + 1] - ys[i]);
    }
}
