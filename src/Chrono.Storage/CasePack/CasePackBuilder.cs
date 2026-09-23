using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Units;
using Chrono.Storage.Workflow;

namespace Chrono.Storage.CasePack;

public static class CasePackBuilder
{
    public const string ProjectId = "core-kc21";
    public const string ProjectName = "KC-21 Kuroshio Margin core: depth anchors, sedimentation rates, multi-candidate lineage";
    public const string CounterBranchId = "counter-case";

    private static readonly VerticalDatum Datum = VerticalDatum.Csfa;
    private static readonly TimeDatum CalBp = new("calBP", "calibrated years before present",
        "present = 1950 CE; Marine20 calibration basis");

    public static MaterialProject Baseline()
    {
        var sections = new List<CoreSection>
        {
            new("sec-1H", "1H", 1, new DepthValue(0), new DepthValue(1.5), Datum, "0 m", "150 cm"),
            new("sec-2H", "2H", 2, new DepthValue(1.5), new DepthValue(3.0), Datum, "1.5 m", "3.0 m"),
            new("sec-3H", "3H", 3, new DepthValue(3.0), new DepthValue(4.0), Datum, "3.0 m", "4.0 m"),
            new("sec-4H", "4H", 4, new DepthValue(4.0), new DepthValue(5.2), Datum, "4.0 m", "5.2 m")
        };

        var boundaries = new List<SegmentBoundary>
        {
            new("bdy-unconf-250", new DepthValue(2.5), BoundaryKind.Unconformity,
                "hiatus H2 (erosional surface)", "2.50 m"),
            new("bdy-gap-400", new DepthValue(4.0), BoundaryKind.CoringGap,
                "coring gap between 3H and 4H", "4.00 m"),
            new("bdy-dup-185", new DepthValue(1.85), BoundaryKind.DuplicateDepthMarker,
                "duplicate aliquot depth 1.85 m", "185 cm"),
            new("bdy-inv-365", new DepthValue(3.65), BoundaryKind.InversionMarker,
                "overturned turbidite pair boundary", "3.65 m")
        };

        var segments = new List<Segment>
        {
            new("seg-A", "segment A (0-2.5 m)", 1, null, "bdy-unconf-250", true),
            new("seg-B", "segment B (2.5-4.0 m)", 2, "bdy-unconf-250", "bdy-gap-400", true),
            new("seg-C", "segment C (4.0-5.2 m)", 3, "bdy-gap-400", null, true)
        };

        var reservoir = new ReservoirCorrection(
            "res-nwp", "NWP-220", "Marine20", CalBp, 220, 60,
            "regional marine reservoir offset, NW Pacific margin", "220 +/- 60 yr");

        var eventBeds = new List<EventBed>
        {
            new("evt-kah", "K-Ah tephra", EventBedKind.Tephra, new DepthValue(2.20), 0.012,
                "tephra:K-Ah", "2.20 m"),
            new("evt-t3", "turbidite T-3", EventBedKind.Turbidite, new DepthValue(3.55), 0.08,
                "turbidite:T-3", "3.55 m"),
            new("evt-flood", "flood bed F-1", EventBedKind.FloodBed, new DepthValue(4.45), 0.03,
                "flood:F-1", "94.5 in")
        };

        var anchors = new List<Anchor>
        {
            Anchor("a-01", "A1 foram 0.30 m", AnchorRole.Radiocarbon, 0.30, "30 cm",
                new NormalDistribution(1250, 40), null, null, null, null, null,
                Raw("LSC-1101", "G. bulloides", "1120 +/- 35 BP", "35", 1120, 35), "labbook-2024-11", 1),
            Anchor("a-02", "A2 foram 1.10 m", AnchorRole.Radiocarbon, 1.10, "1.10 m",
                new NormalDistribution(2480, 60), null, null, null, null, null,
                Raw("LSC-1102", "G. bulloides", "2310 +/- 55 BP", "55", 2310, 55), "labbook-2024-11", 2),
            Anchor("a-03a", "A3a aliquot 1.85 m", AnchorRole.Radiocarbon, 1.85, "185 cm",
                new NormalDistribution(3200, 90), null, null, null, "dup-185", null,
                Raw("LSC-1103a", "mixed benthics", "2980 +/- 80 BP", "80", 2980, 80), "labbook-2024-11", 3),
            Anchor("a-03b", "A3b aliquot 1.85 m", AnchorRole.Radiocarbon, 1.85, "185 cm",
                new NormalDistribution(3550, 110), null, null, null, "dup-185", null,
                Raw("LSC-1103b", "mixed benthics", "3320 +/- 100 BP", "100", 3320, 100), "labbook-2024-11", 4),
            Anchor("a-04a", "K-Ah tie (glass shard)", AnchorRole.EventTie, 2.20, "2.20 m",
                new NormalDistribution(7128, 30), null, null, "evt-kah", null, "tie-kah",
                null, "tephra-cat-7.2", 5),
            Anchor("a-04b", "K-Ah tie (lake varve)", AnchorRole.EventTie, 2.20, "2.20 m",
                new NormalDistribution(7155, 35), null, null, "evt-kah", null, "tie-kah",
                null, "varve-chron-v3", 6),
            Anchor("a-05", "A5 shell hash 2.75 m", AnchorRole.Radiocarbon, 2.75, "2.75 m",
                new StudentTDistribution(8900, 120, 3), null, null, null, null, null,
                Raw("LSC-1105", "shell hash", "8120 +/- 130 BP", "130", 8120, 130), "labbook-2025-02", 7),
            Anchor("a-06", "A6 marine shell 3.40 m", AnchorRole.Radiocarbon, 3.40, "3.40 m",
                new NormalDistribution(10620, 150),
                new NormalDistribution(10620 - 220, Math.Sqrt(150 * 150 + 60 * 60)),
                "res-nwp", null, null, null,
                Raw("LSC-1106", "Turritella", "9420 +/- 140 BP", "140", 9420, 140), "labbook-2025-02", 8),
            Anchor("a-07", "A7 overturned top 3.60 m", AnchorRole.Radiocarbon, 3.60, "3.60 m",
                new NormalDistribution(12100, 200), null, null, "evt-t3", null, null,
                Raw("LSC-1107", "reworked foram", "10480 +/- 190 BP", "190", 10480, 190), "labbook-2025-02", 9),
            Anchor("a-08", "A8 overturned base 3.70 m", AnchorRole.Radiocarbon, 3.70, "3.70 m",
                new NormalDistribution(11800, 180), null, null, "evt-t3", null, null,
                Raw("LSC-1108", "reworked foram", "10220 +/- 170 BP", "170", 10220, 170), "labbook-2025-02", 10),
            Anchor("a-09", "A9 bulk organics 4.30 m", AnchorRole.Radiocarbon, 4.30, "4.30 m",
                new EmpiricalDistribution(LongTailSample(13100)), null, null, null, null, null,
                Raw("LSC-1109", "bulk organics", "11250 +/- 400 BP", "400", 11250, 400), "labbook-2025-06", 11),
            Anchor("a-10", "A10 wood fragment 4.90 m", AnchorRole.Radiocarbon, 4.90, "4.90 m",
                new NormalDistribution(15200, 300), null, null, null, null, null,
                Raw("LSC-1110", "wood fragment", "12600 +/- 280 BP", "280", 12600, 280), "labbook-2025-06", 12),
            Anchor("a-11", "A11 contaminated 1.50 m", AnchorRole.Radiocarbon, 1.50, "1.50 m",
                new NormalDistribution(900, 500), null, null, null, null, null,
                Raw("LSC-1111", "rootlet-contaminated", "810 +/- 60 BP", "60", 810, 60), "labbook-2024-11", 13),
            Anchor("a-12", "A12 superseded aliquot 3.10 m", AnchorRole.Radiocarbon, 3.10, "3.10 m",
                new NormalDistribution(9700, 400), null, null, null, null, null,
                Raw("LSC-1112", "superseded run", "8890 +/- 380 BP", "380", 8890, 380), "labbook-2025-02", 14)
        };

        var exclusions = new List<SourceExclusion>
        {
            new("exc-labflag-a11", "a-11", ExclusionKind.Contamination,
                "lab flag: rootlet contamination, age too young by >2 sigma",
                "labbook-2024-11#LSC-1111", false, "QA flag 2024-12-03"),
            new("exc-superseded-a12", "a-12", ExclusionKind.Superseded,
                "superseded by re-run LSC-1106 series",
                "labbook-2025-02#LSC-1112", false, "QA memo 2025-03-14")
        };

        var rawTokens = new List<RawToken>
        {
            new("evt-flood", "depth", "94.5 in", RawParseStatus.Unconverted, null,
                "unit 'in' is not a recognized length unit; value kept at source position"),
            new("a-01", "depth", "30 cm", RawParseStatus.Parsed, "cm", null),
            new("a-03a", "depth", "185 cm", RawParseStatus.Parsed, "cm", null)
        };

        return new MaterialProject(ProjectId, ProjectName, CalBp,
            sections, boundaries, segments, eventBeds,
            new List<ReservoirCorrection> { reservoir },
            anchors, exclusions, rawTokens);
    }

    public static ChronoParameters RevisedParameters() => new(
        "params-v2", 4000, 20260923L, 0.0, 120.0, 1.0,
        "revision: raise MC samples and cap sedimentation rate at 120 cm/ka");

    public static MaterialProject RevisedMaterialsLocalReservoir()
    {
        var baseline = Baseline();
        var reservoir = baseline.ReservoirCorrections[0] with
        {
            DeltaRCalYr = 260,
            DeltaRSigmaCalYr = 70,
            BasisNote = "revision: updated regional compilation (2026 cruise)"
        };
        var anchors = baseline.Anchors.Select(a =>
            a.ReservoirId == "res-nwp"
                ? a with
                {
                    CorrectedAge = new NormalDistribution(
                        a.MeasuredAge.Mean() - 260,
                        Math.Sqrt(150 * 150 + 70 * 70))
                }
                : a).ToList();
        return baseline with
        {
            ReservoirCorrections = new List<ReservoirCorrection> { reservoir },
            Anchors = anchors
        };
    }

    private static Anchor Anchor(string id, string label, AnchorRole role, double depthMeters,
        string rawDepth, AgeDistribution measured, AgeDistribution? corrected,
        string? reservoirId, string? eventBedId, string? dupGroup, string? tieGroup,
        RawRadiometric? radio, string provenance, int order) =>
        new(id, label, role, new DepthValue(depthMeters), rawDepth, measured, corrected,
            reservoirId, eventBedId, dupGroup, tieGroup, radio, provenance, order);

    private static RawRadiometric Raw(string lab, string material, string convText,
        string sigmaText, double? conv, double? sigma) =>
        new(lab, material, convText, sigmaText, conv, sigma);

    private static List<double> LongTailSample(double center)
    {
        var values = new List<double>();
        for (int i = 0; i < 64; i++)
        {
            double u = (i + 0.5) / 64.0;
            double tail = u > 0.9 ? Math.Pow((u - 0.9) * 10, 3) * 900 : 0;
            values.Add(center + (u - 0.5) * 600 + tail);
        }
        values.Sort();
        return values;
    }
}
