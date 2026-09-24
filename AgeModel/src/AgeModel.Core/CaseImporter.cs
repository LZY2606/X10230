using System.Text.Json;

namespace AgeModel.Core;

public static class CaseImporter
{
    public static ImportResult Import(string json)
    {
        CasePackage? package;
        try
        {
            package = JsonSerializer.Deserialize<CasePackage>(json, ChronologyJson.Options);
        }
        catch (JsonException ex)
        {
            return new ImportResult(ProjectStatus.FormatCorrupt, null, new[]
            {
                new Diagnostic("FORMAT_CORRUPT", "error", "$", null, "Case package is not valid JSON or does not match the case schema: " + ex.Message)
            });
        }

        if (package is null)
        {
            return new ImportResult(ProjectStatus.FormatCorrupt, null, new[]
            {
                new Diagnostic("FORMAT_CORRUPT", "error", "$", null, "Case package decoded to null.")
            });
        }

        var diagnostics = new List<Diagnostic>();
        if (package.Schema != "core-chronology-case/v1")
            diagnostics.Add(new Diagnostic("UNSUPPORTED_SCHEMA", "error", "schema", null, $"Unsupported schema '{package.Schema}'."));
        if (string.IsNullOrWhiteSpace(package.CaseId))
            diagnostics.Add(new Diagnostic("MISSING_CASE_ID", "error", "caseId", null, "caseId is required."));
        if (package.Segments.Count == 0)
            diagnostics.Add(new Diagnostic("NO_SEGMENTS", "error", "segments", null, "At least one core segment is required."));
        if (package.Anchors.Count == 0)
            diagnostics.Add(new Diagnostic("NO_ANCHORS", "error", "anchors", null, "At least one age anchor is required."));
        if (package.Parameters is null)
            diagnostics.Add(new Diagnostic("MISSING_PARAMETERS", "error", "parameters", null, "A parameter version is required."));
        if (diagnostics.Any(d => d.Severity == "error"))
            return new ImportResult(ProjectStatus.FormatCorrupt, null, diagnostics);

        var normalized = Normalize(package, diagnostics);
        var semantic = ValidateSemantics(normalized);
        if (semantic.Count > 0)
            return new ImportResult(ProjectStatus.SemanticallyUnsolvable, normalized, semantic);

        return new ImportResult(ProjectStatus.Ok, normalized, diagnostics);
    }

    private static CasePackage Normalize(CasePackage package, List<Diagnostic> diagnostics)
    {
        var datum = package.DepthDatum;
        var segments = package.Segments.Select(s =>
        {
            var start = QuantityNormalizer.NormalizeDepth(s.StartDepth, datum.DepthUnit, datum.CoordinateDatum);
            var end = QuantityNormalizer.NormalizeDepth(s.EndDepth, datum.DepthUnit, datum.CoordinateDatum);
            WarnIfNeeded(start, $"segments[{s.Id}].startDepth", s.Id, diagnostics);
            WarnIfNeeded(end, $"segments[{s.Id}].endDepth", s.Id, diagnostics);
            return s with { StartDepth = start, EndDepth = end };
        }).ToArray();

        var boundaries = package.Boundaries.Select(b =>
        {
            var top = QuantityNormalizer.NormalizeDepth(b.TopDepth, datum.DepthUnit, datum.CoordinateDatum);
            var bottom = QuantityNormalizer.NormalizeDepth(b.BaseDepth, datum.DepthUnit, datum.CoordinateDatum);
            WarnIfNeeded(top, $"boundaries[{b.Id}].topDepth", b.Id, diagnostics);
            WarnIfNeeded(bottom, $"boundaries[{b.Id}].baseDepth", b.Id, diagnostics);
            var minAge = b.MinimumMissingAge is null ? null : QuantityNormalizer.NormalizeAge(b.MinimumMissingAge);
            var maxAge = b.MaximumMissingAge is null ? null : QuantityNormalizer.NormalizeAge(b.MaximumMissingAge);
            if (minAge is not null) WarnIfNeeded(minAge, $"boundaries[{b.Id}].minimumMissingAge", b.Id, diagnostics);
            if (maxAge is not null) WarnIfNeeded(maxAge, $"boundaries[{b.Id}].maximumMissingAge", b.Id, diagnostics);
            return b with { TopDepth = top, BaseDepth = bottom, MinimumMissingAge = minAge, MaximumMissingAge = maxAge };
        }).ToArray();

        var offsets = package.ReservoirOffsets.Select(o =>
        {
            var offset = QuantityNormalizer.NormalizeAge(o.Offset);
            WarnIfNeeded(offset, $"reservoirOffsets[{o.Id}].offset", o.Id, diagnostics);
            return o with { Offset = offset };
        }).ToArray();

        var anchors = package.Anchors.Select(a =>
        {
            var depth = QuantityNormalizer.NormalizeDepth(a.Depth, datum.DepthUnit, datum.CoordinateDatum);
            WarnIfNeeded(depth, $"anchors[{a.Id}].depth", a.Id, diagnostics);
            if (a.AgeDistribution.Samples.Count == 0)
                diagnostics.Add(new Diagnostic("DISTRIBUTION_MISSING", "error", $"anchors[{a.Id}].ageDistribution.samples", a.Id,
                    "Anchor distributions must retain samples; a mean-only anchor is invalid."));
            if (a.AgeDistribution.Samples.Count > package.Parameters.MaxDistributionSamples)
                diagnostics.Add(new Diagnostic("DISTRIBUTION_BUDGET", "error", $"anchors[{a.Id}].ageDistribution.samples", a.Id,
                    $"Distribution has {a.AgeDistribution.Samples.Count} samples but the budget is {package.Parameters.MaxDistributionSamples}."));
            return a with { Depth = depth };
        }).ToArray();

        var events = package.EventLayers.Select(e =>
        {
            var depth = QuantityNormalizer.NormalizeDepth(e.Depth, datum.DepthUnit, datum.CoordinateDatum);
            WarnIfNeeded(depth, $"eventLayers[{e.Id}].depth", e.Id, diagnostics);
            return e with { Depth = depth };
        }).ToArray();

        return package with
        {
            Segments = segments,
            Boundaries = boundaries,
            ReservoirOffsets = offsets,
            Anchors = anchors,
            EventLayers = events
        };
    }

    private static void WarnIfNeeded(DomainQuantity quantity, string path, string subjectId, List<Diagnostic> diagnostics)
    {
        if (!string.IsNullOrEmpty(quantity.Diagnostic))
        {
            diagnostics.Add(new Diagnostic("UNCONVERTIBLE_VALUE", "error", path, subjectId, quantity.Diagnostic,
                quantity.Raw + " " + quantity.Unit));
        }
    }

    private static List<Diagnostic> ValidateSemantics(CasePackage package)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var segment in package.Segments)
        {
            if (segment.StartDepth.NormalizedValue is double start && segment.EndDepth.NormalizedValue is double end && start >= end)
                diagnostics.Add(new Diagnostic("INVALID_SEGMENT_RANGE", "error", $"segments[{segment.Id}]", segment.Id,
                    "Segment end depth must be greater than start depth."));
        }

        var anchorIds = package.Anchors.Select(a => a.Id).ToHashSet();
        foreach (var offset in package.ReservoirOffsets)
        {
            foreach (var target in offset.TargetAnchorIds.Except(anchorIds))
                diagnostics.Add(new Diagnostic("DANGLING_REFERENCE", "error", $"reservoirOffsets[{offset.Id}].targetAnchorIds", offset.Id,
                    $"Target anchor {target} does not exist."));
        }
        foreach (var layer in package.EventLayers)
        {
            foreach (var linked in layer.LinkedAnchorIds.Except(anchorIds))
                diagnostics.Add(new Diagnostic("DANGLING_REFERENCE", "error", $"eventLayers[{layer.Id}].linkedAnchorIds", layer.Id,
                    $"Linked anchor {linked} does not exist."));
        }
        foreach (var exclusion in package.Exclusions)
        {
            if (exclusion.SubjectType == "anchor" && !anchorIds.Contains(exclusion.SubjectId))
                diagnostics.Add(new Diagnostic("DANGLING_EXCLUSION", "error", $"exclusions[{exclusion.Id}].subjectId", exclusion.Id,
                    $"Excluded anchor {exclusion.SubjectId} does not exist."));
        }
        return diagnostics;
    }
}
