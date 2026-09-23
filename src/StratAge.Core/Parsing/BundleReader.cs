using StratAge.Core.Domain;

namespace StratAge.Core.Parsing;

public sealed record IngestResult(
    IngestCategory Category,
    CreateStudy? Study,
    IReadOnlyList<object> Commands,
    IReadOnlyList<Diag> Diagnostics,
    IReadOnlyList<string> Sources)
{
    public bool Ok => Category == IngestCategory.Ok;
}

/// <summary>
/// 导入器：负责词法/结构/必填字段层。语义冲突（深度重复、倒置等）交给重放器，
/// 因此本层不会把语义问题伪装成“成功”，也不会用默认值填空。
/// </summary>
public sealed class BundleReader
{
    readonly List<Diag> _diags = [];

    public IngestResult Read(string json, int maxDepth = 64, long maxTokens = 200_000)
    {
        var (root, failure) = JsonParser.Parse(json, maxDepth, maxTokens);
        if (failure is not null)
            return new IngestResult(failure.Category, null, [], [failure.Diagnostic], []);

        if (root!.Kind != "object")
            return new IngestResult(IngestCategory.FormatDamaged, null, [],
                [new Diag("BUNDLE_ROOT", "根必须是对象", "$", root.Line, root.Column)], []);

        var schemaNode = root.Get("schema");
        var schema = schemaNode?.Text ?? "";
        if (schema is not ("strat-age/case-bundle/v1" or "strat-age/event-batch/v1"))
            _diags.Add(new Diag("BUNDLE_SCHEMA", "schema 必须是 strat-age/case-bundle/v1", "$.schema",
                schemaNode?.Line ?? root.Line, schemaNode?.Column ?? root.Column));

        CreateStudy? study = null;
        var studyNode = root.Get("study");
        if (studyNode is not null)
            study = ReadStudy(studyNode);
        else
            _diags.Add(new Diag("STUDY_MISSING", "缺少 study 块（深度基准与时间基准必须显式给出）", "$.study",
                root.Line, root.Column));

        var sources = new List<string>();
        if (root.Get("sources") is { Kind: "array" } srcArr)
            foreach (var s in srcArr.Items)
                if (s.Kind == "string") sources.Add(s.Text!);

        var commands = new List<object>();
        var cmdNode = root.Get("commands");
        if (cmdNode is null)
            _diags.Add(new Diag("COMMANDS_MISSING", "缺少 commands 数组", "$.commands", root.Line, root.Column));
        else if (cmdNode.Kind != "array")
            _diags.Add(new Diag("COMMANDS_TYPE", "commands 必须是数组", "$.commands", cmdNode.Line, cmdNode.Column));
        else
            for (var i = 0; i < cmdNode.Items.Count; i++)
            {
                var c = ReadCommand(cmdNode.Items[i], $"$.commands[{i}]", sources);
                if (c is not null) commands.Add(c);
            }

        var category = _diags.Count == 0
            ? IngestCategory.Ok
            : IngestCategory.IncompleteInput;
        return new IngestResult(category, study, commands, _diags, sources);
    }

    CreateStudy ReadStudy(JToken n)
    {
        var path = "$.study";
        var id = ReqStr(n, "id", path) ?? $"study@{n.Line}:{n.Column}";
        var name = ReqStr(n, "name", path) ?? "";
        var datumUnit = ReqStr(n, "datumUnit", path) ?? "m";
        var datumBasis = ReqStr(n, "datumBasis", path) ?? "";
        var timeBasis = ReqStr(n, "timeBasis", path) ?? "";
        return new CreateStudy(id, name, datumUnit, datumBasis, timeBasis);
    }

    object? ReadCommand(JToken n, string path, List<string> sources)
    {
        if (n.Kind != "object")
        {
            _diags.Add(new Diag("COMMAND_TYPE", "命令必须是对象", path, n.Line, n.Column));
            return null;
        }
        var typeNode = n.Get("type");
        if (typeNode?.Kind != "string")
        {
            _diags.Add(new Diag("COMMAND_TYPE_MISSING", "命令缺少字符串字段 type", path + ".type",
                typeNode?.Line ?? n.Line, typeNode?.Column ?? n.Column));
            return null;
        }
        var type = typeNode.Text!;
        var id = ReqStr(n, "id", path) ?? $"{type}@seq";
        var branch = OptStr(n, "branchId");
        var note = OptStr(n, "note");
        var cmdSources = ReadSources(n, sources);

        try
        {
            object? cmd = type switch
            {
                "section.defined" => new DefineSection(
                    id, ReqRaw(n, "top", path), ReqRaw(n, "bottom", path),
                    ReqStr(n, "kind", path) ?? "", branch, OptInt(n, "version") ?? 1, OptStr(n, "parentId")),
                "anchor.put" => new PutAnchor(id, ReqStr(n, "sectionId", path) ?? "",
                    ReqRaw(n, "depth", path), ReqDist(n.Get("ageDist"), path + ".ageDist"),
                    OptStr(n, "material") ?? "generic", OptStr(n, "pairKey"), OptStr(n, "eventId"),
                    branch, OptInt(n, "version") ?? 1, OptStr(n, "parentId")),
                "reservoir.defined" => new DefineReservoir(id, OptStr(n, "name") ?? "",
                    ReqDist(n.Get("deltaR"), path + ".deltaR")),
                "event.put" => new PutEvent(id, OptStr(n, "name") ?? "", OptStr(n, "kind") ?? "",
                    ReqRaw(n, "depth", path), OptStr(n, "correlatesTo")),
                "exclusion.recorded" => new RecordExclusion(id, ReqStr(n, "targetId", path) ?? "",
                    ReqStr(n, "targetKind", path) ?? "", ReqStr(n, "reason", path) ?? "",
                    ReqStr(n, "basis", path) ?? ""),
                "decision.human" => new HumanDecision(id, ReqStr(n, "decision", path) ?? "",
                    OptStr(n, "scope") ?? "study", OptStr(n, "value") ?? "",
                    OptStr(n, "conflictsWith") ?? "", branch),
                "parameter.revised" => new ReviseParameter(id, ReqStr(n, "parameter", path) ?? "",
                    ReqStr(n, "newVersion", path) ?? "", OptStr(n, "oldVersion") ?? "", note ?? ""),
                "model.frozen" => new FreezeModel(id, ReqStr(n, "modelRunId", path) ?? ""),
                "anchor.tried" => new TryAnchor(id, ReqStr(n, "modelRunId", path) ?? "",
                    ReqStr(n, "sectionId", path) ?? "", ReqRaw(n, "depth", path),
                    ReqDist(n.Get("ageDist"), path + ".ageDist"), OptStr(n, "material") ?? "generic",
                    OptStr(n, "eventId")),
                "trial.resolved" => new ResolveTrial(id, ReqStr(n, "trialId", path) ?? "",
                    ReqStr(n, "outcome", path) ?? "", OptStr(n, "acceptedRunId")),
                "counter.recorded" => ReadCounter(n, path, id),
                _ => UnknownCommand(n, path, type),
            };
            return cmd;
        }
        catch (RequiredFieldException) { return null; }
    }

    object UnknownCommand(JToken n, string path, string type)
    {
        var tn = n.Get("type")!;
        _diags.Add(new Diag("COMMAND_UNKNOWN", $"未知命令类型 '{type}'", path + ".type", tn.Line, tn.Column));
        throw new RequiredFieldException();
    }

    RecordCounterExperiment ReadCounter(JToken n, string path, string id)
    {
        var findings = new List<CounterFinding>();
        if (n.Get("findings") is { Kind: "array" } farr)
            foreach (var f in farr.Items)
            {
                var code = ReqStr(f, "ruleCode", path + ".findings[]") ?? "";
                var status = ReqStr(f, "status", path + ".findings[]") ?? "";
                var obj = ReqStr(f, "objectId", path + ".findings[]") ?? "";
                findings.Add(new CounterFinding
                {
                    RuleCode = code, Status = status, ObjectId = obj, Detail = OptStr(f, "detail") ?? ""
                });
            }
        return new RecordCounterExperiment(id,
            ReqStr(n, "baselineMarker", path) ?? "",
            ReqStr(n, "changedSourceId", path) ?? "",
            ReqStr(n, "changeKind", path) ?? "",
            OptStr(n, "change") ?? "{}",
            ReqStr(n, "ruleCode", path) ?? "",
            OptStr(n, "expectedContainment") ?? "",
            OptBool(n, "adopted") ?? false,
            OptStr(n, "outcome") ?? "",
            OptStr(n, "affectedIds") ?? "[]",
            findings);
    }

    static IReadOnlyList<string> ReadSources(JToken n, List<string> known)
    {
        if (n.Get("sources") is not { Kind: "array" } arr) return [];
        var list = new List<string>();
        foreach (var it in arr.Items) if (it.Kind == "string") list.Add(it.Text!);
        return list;
    }

    string ReqRaw(JToken parent, string key, string path)
    {
        var m = parent.Get(key);
        if (m is null)
        {
            _diags.Add(new Diag("FIELD_MISSING", $"缺少字段 '{key}'", $"{path}.{key}"));
            throw new RequiredFieldException();
        }
        return m.Kind switch
        {
            "string" => m.Text!,
            "number" => m.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "object" when m.Get("raw")?.Kind == "string" => m.Get("raw")!.Text!,
            _ => throw throwBad(m, $"{path}.{key}", "深度/坐标必须是字符串或 {raw,...}")
        };
    }

    string? ReqStr(JToken parent, string key, string path)
    {
        var m = parent.Get(key);
        if (m is null)
        {
            _diags.Add(new Diag("FIELD_MISSING", $"缺少字段 '{key}'", $"{path}.{key}"));
            throw new RequiredFieldException();
        }
        if (m.Kind != "string")
            throw throwBad(m, $"{path}.{key}", $"'{key}' 必须是字符串");
        return m.Text;
    }

    static string? OptStr(JToken parent, string key) =>
        parent.Get(key) is { Kind: "string" } s ? s.Text : null;
    static int? OptInt(JToken parent, string key) =>
        parent.Get(key) is { Kind: "number" } num ? (int)num.Number : null;
    static bool? OptBool(JToken parent, string key) =>
        parent.Get(key) is { Kind: "bool" } b ? b.Number == 1 : null;

    RequiredFieldException throwBad(JToken m, string path, string msg)
    {
        _diags.Add(new Diag("FIELD_TYPE", msg, path, m.Line, m.Column));
        return new RequiredFieldException();
    }

    DistributionSpec ReqDist(JToken? m, string path)
    {
        if (m is null)
        {
            _diags.Add(new Diag("FIELD_MISSING", "缺少年龄分布；一个锚点的分布不能被均值替代", path));
            throw new RequiredFieldException();
        }
        var kind = ReqStr(m, "kind", path) ?? "normal";
        var unit = ReqStr(m, "unit", path) ?? "";
        if (!new[] { "normal", "uniform", "empirical", "radiocarbon" }.Contains(kind))
        {
            _diags.Add(new Diag("DIST_KIND", $"未知分布类型 '{kind}'", path + ".kind", m.Line, m.Column));
            throw new RequiredFieldException();
        }
        if (kind == "normal" && (m.Get("mean") is null || m.Get("sigma") is null))
        {
            _diags.Add(new Diag("DIST_PARAMS", "normal 分布需要 mean 与 sigma；仅给均值会丢失分布", path));
            throw new RequiredFieldException();
        }
        if (kind == "uniform" && (m.Get("min") is null || m.Get("max") is null))
        {
            _diags.Add(new Diag("DIST_PARAMS", "uniform 分布需要 min 与 max", path));
            throw new RequiredFieldException();
        }
        if (kind == "empirical" && (m.Get("samples") is not { Kind: "array" } a || a.Items.Count < 2))
        {
            _diags.Add(new Diag("DIST_PARAMS", "empirical 分布需要至少 2 个样本（长尾必须保留）", path + ".samples"));
            throw new RequiredFieldException();
        }
        var samples = new List<double>();
        if (m.Get("samples") is { Kind: "array" } sa)
            foreach (var it in sa.Items)
                if (it.Kind == "number") samples.Add(it.Number);
                else _diags.Add(new Diag("DIST_SAMPLE", "样本必须是数字", path + ".samples", it.Line, it.Column));

        return new DistributionSpec
        {
            Kind = kind,
            Unit = unit,
            Mean = Num(m, "mean"), Sigma = Num(m, "sigma"),
            Min = Num(m, "min"), Max = Num(m, "max"),
            Samples = samples.Count > 0 ? samples : null,
            CalibrationVersion = OptStr(m, "calibrationVersion"),
            ReservoirDeltaR = Num(m, "reservoirDeltaR"),
            ReservoirId = OptStr(m, "reservoirId"),
        };
    }

    static double? Num(JToken parent, string key) =>
        parent.Get(key) is { Kind: "number" } n ? n.Number : null;

    sealed class RequiredFieldException : Exception { }
}
