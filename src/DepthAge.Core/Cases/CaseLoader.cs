using DepthAge.Core.Models;

namespace DepthAge.Core.Cases;

/// <summary>把离线领域案例包加载成不可变材料（原始表示保留，规范化只发生在派生层）。</summary>
public sealed class CaseLoader
{
    private readonly UnitRegistry _units;

    public CaseLoader(UnitRegistry? units = null) => _units = units ?? new UnitRegistry();

    public ImportOutcome LoadBaseline(CasePackage package)
    {
        var outcome = new ImportOutcome();

        foreach (var d in package.Datums)
            outcome.Datums.Add(new DepthDatum
            {
                Id = d.Id, Name = d.Name, CoordinateSystem = d.CoordinateSystem,
                CanonicalUnit = d.CanonicalUnit, ZeroOffsetToCanonicalM = d.ZeroOffsetM, SourceRef = d.SourceRef
            });

        foreach (var s in package.Sections)
            outcome.Sections.Add(new CoreSection
            {
                Id = s.Id, CoreId = s.CoreId, TopDepthM = s.TopM, BottomDepthM = s.BottomM,
                BottomBoundary = s.BottomBoundary, BoundaryNote = s.BoundaryNote, SourceRef = s.SourceRef
            });

        foreach (var c in package.CalibrationCurves)
            outcome.CalibrationCurves.Add(new CalibrationCurve
            {
                Id = c.Id, Name = c.Name,
                Points = c.Points.Select(p => new CalibrationPoint(p.RadiocarbonYrBP, p.CalYrBP, p.SigmaCalYr)).ToList(),
                SourceRef = c.SourceRef
            });

        foreach (var r in package.ReservoirOffsets)
            outcome.ReservoirOffsets.Add(new ReservoirOffset
            {
                Id = r.Id, Name = r.Name, DeltaRYr = r.DeltaRYr, SigmaYr = r.SigmaYr,
                AppliesTopDepthM = r.TopM, AppliesBottomDepthM = r.BottomM, SourceRef = r.SourceRef
            });

        foreach (var e in package.EventBeds)
        {
            var ageQty = _units.Parse($"{e.MeanCalYrBP} cal yr BP", e.MeanCalYrBP, "cal yr bp", UnitKind.Age);
            var rng = new DeterministicRng(MaterialPreparation.SeedFor(e.Id, e.MeanCalYrBP));
            var samples = Enumerable.Range(0, 400)
                .Select(_ => e.MeanCalYrBP + e.SigmaYr * rng.NextNormal())
                .Where(v => v >= 0)
                .OrderBy(v => v).ToList();
            outcome.EventBeds.Add(new EventBed
            {
                Id = e.Id, Name = e.Name, SourceRef = e.SourceRef,
                Age = new AgeDistribution
                {
                    Kind = AgeKind.Calendar,
                    ReportedAge = ageQty,
                    ReportedSigma = e.SigmaYr,
                    CalendarUnit = "cal yr BP",
                    SamplesCalYrBP = samples,
                    Quantiles = new[] { 0.025, 0.05, 0.16, 0.5, 0.84, 0.95, 0.975 }
                        .Select(p => new QuantilePoint(p, CalibrationCurve.Quantile(samples, p))).ToList(),
                    CalendarReady = true
                }
            });
        }

        foreach (var link in package.EventLinks)
        {
            var depthQty = _units.Parse($"{link.DepthM} m", link.DepthM, "m", UnitKind.Depth, link.DatumId);
            outcome.EventCorrelations.Add(new EventCorrelation
            {
                Id = link.Id, EventBedId = link.EventBedId, CoreId = link.CoreId,
                Depth = new DepthPoint { DatumId = link.DatumId, Quantity = depthQty },
                DepthToleranceM = link.ToleranceM, SourceRef = link.SourceRef
            });
        }

        foreach (var a in package.Anchors)
        {
            var rawDepth = a.DepthRawOverride ?? $"{a.Depth} {a.DepthUnit}";
            var depthQty = a.DepthRawOverride is null
                ? _units.Parse(rawDepth, a.Depth, a.DepthUnit, UnitKind.Depth, a.DatumId)
                : ParseUnconvertibleDepth(rawDepth, a, package);

            var kind = a.AgeKind.ToLowerInvariant() switch
            {
                "calendar" => AgeKind.Calendar,
                "radiocarbon" => AgeKind.Radiocarbon,
                "event" or "eventderived" => AgeKind.EventDerived,
                _ => AgeKind.Calendar
            };
            var ageQty = _units.Parse($"{a.ReportedAge} {a.AgeUnit}", a.ReportedAge, a.AgeUnit, UnitKind.Age);

            // 长尾：通过预设偏态样本（混合分布）显式表达，不能退化成均值±sigma
            List<double> preset = a.PresetSamples ?? new();
            var distribution = new AgeDistribution
            {
                Kind = kind,
                ReportedAge = ageQty,
                ReportedSigma = a.Sigma,
                CalendarUnit = kind == AgeKind.Calendar || kind == AgeKind.EventDerived ? "cal yr BP" : null,
                CalibrationCurveId = a.CalibrationCurveId,
                ReservoirOffsetId = null,
                SamplesCalYrBP = preset,
                Quantiles = preset.Count >= 8
                    ? new[] { 0.025, 0.05, 0.16, 0.25, 0.5, 0.75, 0.84, 0.95, 0.975 }
                        .Select(p => new QuantilePoint(p, CalibrationCurve.Quantile(preset.OrderBy(x => x).ToList(), p))).ToList()
                    : new(),
                CalendarReady = preset.Count >= 8,
                Diagnostics = a.LongTail ? new List<string> { "长尾分布：以经验样本/分位数保留偏态尾部，不用均值替代" } : new()
            };

            var anchor = new AnchorMaterial
            {
                Id = a.Id, Label = a.Label, CoreId = a.CoreId, SourceRef = a.SourceRef,
                Depth = new DepthPoint { DatumId = a.DatumId, Quantity = depthQty },
                Age = distribution, EventBedId = a.EventBedId, Method = a.Method
            };
            outcome.Anchors.Add(anchor);

            if (!depthQty.IsUsable)
                foreach (var diag in depthQty.Diagnostics)
                    outcome.Diagnostics.Add(new Diagnostic
                    {
                        Code = "D_DEPTH_UNCONVERTIBLE", Severity = "Warning",
                        Message = $"锚点 {a.Id}：{diag}；原始表示 '{rawDepth}' 保留在原位置，未为绘图丢弃",
                        MaterialId = a.Id, FieldPath = $"anchors[{a.Id}].depth"
                    });
            if (!ageQty.IsUsable && kind == AgeKind.Radiocarbon)
                outcome.Diagnostics.Add(new Diagnostic
                {
                    Code = "D_AGE_NEEDS_CALIBRATION", Severity = "Info",
                    Message = $"锚点 {a.Id}：放射性年龄保留原表示，将经储层偏移与校准曲线派生",
                    MaterialId = a.Id, FieldPath = $"anchors[{a.Id}].age.reportedAge"
                });
        }

        return outcome;
    }

    private Quantity ParseUnconvertibleDepth(string raw, CaseAnchor a, CasePackage package)
    {
        // 形如 "2.40 英寻?" 的损坏/未知单位：数值可能可解析但单位不可转换
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double? value = double.TryParse(tokens.FirstOrDefault(), out var v) ? v : null;
        var unit = tokens.Length > 1 ? string.Join(' ', tokens.Skip(1)) : a.DepthUnit;
        return _units.Parse(raw, value, unit, UnitKind.Depth, a.DatumId);
    }
}
