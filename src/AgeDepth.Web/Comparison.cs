using AgeDepth.Core;

namespace AgeDepth.Web;

/// <summary>Delta between a frozen/baseline run and a hypothetical run with a new anchor.</summary>
public static class Comparison
{
    public static object Summarize(ModelRunResult baseline, ModelRunResult compared, AgeAnchor newAnchor)
    {
        var segments = new List<object>();
        foreach (var candidate in compared.Candidates)
        {
            var baseCandidate = baseline.Candidates.FirstOrDefault(c => c.Kind == candidate.Kind);
            foreach (var segment in candidate.Segments)
            {
                var baseSegment = baseCandidate?.Segments.FirstOrDefault(s => s.Segment.Id == segment.Segment.Id);
                if (baseSegment is null)
                {
                    segments.Add(new
                    {
                        candidate = candidate.Kind,
                        segmentId = segment.Segment.Id,
                        change = "new-or-unsolved-baseline",
                        valueId = $"cmp:{candidate.Kind}:{segment.Segment.Id}"
                    });
                    continue;
                }
                double BaseWidth() => BandWidth(baseSegment);
                var deltaWidth = BandWidth(segment) - BaseWidth();
                var deltaMedian = segment.Median.Zip(baseSegment.Median, (a, b) => Math.Abs(a - b)).DefaultIfEmpty(0).Max();
                segments.Add(new
                {
                    candidate = candidate.Kind,
                    segmentId = segment.Segment.Id,
                    change = segment.Digest == baseSegment.Digest ? "unchanged" : "recomputed",
                    bandWidthDeltaYr = Math.Round(deltaWidth, 1),
                    maxMedianShiftYr = Math.Round(deltaMedian, 1),
                    valueId = $"cmp:{candidate.Kind}:{segment.Segment.Id}"
                });
            }
        }
        return new
        {
            newAnchorId = newAnchor.Id,
            newAnchorDepth = newAnchor.Depth,
            baselineRunId = baseline.RunId,
            comparedRunId = compared.RunId,
            diagnostics = compared.Diagnostics.Select(d => new { d.Code, d.Message, d.Location }).ToArray(),
            segments
        };
    }

    private static double BandWidth(SegmentResult segment)
    {
        var acc = 0.0;
        for (var i = 0; i < segment.Median.Length; i++)
            acc += segment.P975[i] - segment.P25[i];
        return segment.Median.Length == 0 ? 0 : acc / segment.Median.Length;
    }
}
