using System.Text;
using System.Text.Json;
using ChronoCore;
using ChronoStorage;

namespace ChronoWeb;

public static class ReviewPage
{
    private static readonly JsonSerializerOptions Jo =
        new() { PropertyNameCaseInsensitive = true };

    public static ReviewModel Build(ChronoRepository repo, string branch, long? atSeq)
    {
        var all = repo.ReadAllEvents();
        var state = EventFolder.FoldTo(all, branch, atSeq);
        var branchEvents = all.Where(e => e.BranchId == branch)
            .OrderBy(e => e.Seq).ToList();
        if (atSeq is not null) branchEvents = branchEvents.Where(e => e.Seq <= atSeq.Value).ToList();
        var models = repo.ReadDerivedModels(branch);

        // 当前显示的模型：该分支最新一次采纳派生中的最优候选（或 ?at 指定事件时的当时结果）
        DerivedModel? current = null;
        var deriveEv = branchEvents.LastOrDefault(e =>
            (e.Type == EventType.ModelsDerived || e.Type == EventType.LocalRecompute));
        if (deriveEv is not null)
        {
            var payload = deriveEv.Payload.Deserialize<ModelsDerivedPayload>(Jo)!;
            var validIds = payload.AdoptedModelIds;
            current = models.Where(m => validIds.Contains(m.Id))
                .OrderByDescending(m => m.SemanticallyValid).ThenBy(m => m.FitScore)
                .FirstOrDefault() ?? models.FirstOrDefault(m => payload.ModelIds.Contains(m.Id));
        }

        var judgments = branchEvents.Where(e => e.Type == EventType.ReplayJudgment)
            .Select(e => e.Payload.Deserialize<JudgmentPayload>(Jo)!).ToList();
        return new ReviewModel(branch, state.HeadSeq, state, branchEvents, repo.ListBranches(),
            models.OrderBy(m => m.Rank).ToList(), current,
            Array.Empty<ReplayStepResult>(), judgments, null, null);
    }

    public static string Render(ChronoRepository repo, string branch, long? atSeq, string? flash, string? kind)
    {
        var m = Build(repo, branch, atSeq);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang='zh'><head><meta charset='utf-8'>");
        sb.Append("<title>岩芯年代尺审阅 · CORESITE-7B</title><style>");
        sb.Append(Css);
        sb.Append("</style></head><body>");
        sb.Append("<header><b>地层年代 / 深度-年龄模型 / 不确定性应用</b>");
        sb.Append("<span class='muted'>离线案例 core-chronicle-7b · 服务端规则 · 无需账号/云端</span></header>");

        if (!string.IsNullOrEmpty(flash))
            sb.Append($"<div class='flash {kind}'>{H(flash)}</div>");

        // 分支与事件导航
        sb.Append("<nav class='branches'><b>分支/档案：</b>");
        foreach (var b in m.Branches)
        {
            var active = b.BranchId == branch;
            sb.Append(active ? $"<span class='chip active'>{b.BranchId}" :
                $"<a class='chip' href='/?branch={b.BranchId}'>{b.BranchId}");
            sb.Append($" <i>{H(b.Purpose)}</i>");
            sb.Append(active ? "</span>" : "</a>");
        }
        sb.Append("</nav>");

        sb.Append("<div class='grid2'>");
        sb.Append("<section><h2>岩芯纵剖 / 锚点分布 / 速率区间 / 不确定度带</h2>");
        sb.Append("<div class='svgbox'>").Append(SvgRender.RenderProfile(m.State, m.Current, branch)).Append("</div>");
        sb.Append("<div class='muted small'>每个图元带 data-stable-id（如 anchor-an-04-q95、rate-iv-1-...、");
        sb.Append("env-band-iv-2-...）；导出→删库→再导入后身份不变。</div>");

        RenderControls(sb, m);
        sb.Append("</section>");

        sb.Append("<section>");
        RenderCandidateTable(sb, m);
        RenderJudgments(sb, m);
        sb.Append("</section>");
        sb.Append("</div>");

        RenderMaterials(sb, m);
        RenderEvents(sb, m);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void RenderControls(StringBuilder sb, ReviewModel m)
    {
        sb.Append("<div class='controls'>");
        sb.Append("<form method='post' action='/actions/derive'>");
        sb.Append($"<input type='hidden' name='branch' value='{m.BranchId}'>");
        sb.Append("<label>参数版本 <select name='parameterVersion'>");
        foreach (var p in m.State.ParameterOrder)
            sb.Append($"<option value='{p.Version}' {(p.Version == m.State.CurrentParameterVersion ? "selected" : "")}>{p.Version} · {p.Interpolation} · 储层校正={p.UseReservoirCorrection} · 预算={p.MaxCandidates}</option>");
        sb.Append("</select></label>");
        sb.Append("<input name='actor' placeholder='审阅者' value='reviewer-A' size='9'>");
        sb.Append("<button>重新派生候选谱系</button></form>");

        if (m.Current is not null)
        {
            sb.Append($"<form method='post' action='/actions/freeze' class='inline'>");
            sb.Append($"<input type='hidden' name='branch' value='{m.BranchId}'>");
            sb.Append($"<input type='hidden' name='modelId' value='{m.Current.Id}'>");
            sb.Append("<input name='actor' placeholder='审阅者' value='reviewer-B' size='9'>");
            sb.Append($"<button class='warn'>冻结模型 {m.Current.Id[..8]}</button></form>");

            sb.Append("<form method='post' action='/actions/impact' class='box'>");
            sb.Append($"<input type='hidden' name='branch' value='{m.BranchId}'>");
            sb.Append($"<input type='hidden' name='modelId' value='{m.Current.Id}'>");
            sb.Append("<b>冻结后比较新锚点：</b><br>");
            sb.Append("深度 <input name='depth' value='26.00 mbsf' size='11'> ");
            sb.Append("年龄均值 cal a BP <input name='meanCalABP' value='160000' size='8'> ");
            sb.Append("σ <input name='sdCalABP' value='4000' size='6'> ");
            sb.Append("<label><input type='checkbox' name='adopt'> 采纳为正式修订</label> ");
            sb.Append("<button>比较影响（不改冻结模型）</button></form>");
        }
        sb.Append("</div>");
    }

    private static void RenderCandidateTable(StringBuilder sb, ReviewModel m)
    {
        sb.Append("<h2>多候选模型谱系（等优全部保留，稳定排序）</h2><table>");
        sb.Append("<tr><th>稳定身份</th><th>rank</th><th>谱系</th><th>插值/锚点</th><th>合法</th><th>等优</th><th>参数</th><th>输入摘要</th></tr>");
        foreach (var mdl in m.Models)
        {
            sb.Append($"<tr data-stable-id='row-{mdl.Id}' class='{(mdl.Id == m.Current?.Id ? "cur" : "")}'>");
            sb.Append($"<td><code>{mdl.Id}</code></td><td>{mdl.Rank}</td><td>{mdl.Family}</td>");
            sb.Append($"<td>{mdl.ChosenAnchorIds.Count} 锚点</td>");
            sb.Append($"<td>{(mdl.SemanticallyValid ? "✅" : "⛔")}</td>");
            sb.Append($"<td>{(mdl.Tie ? "并列" : "")}</td><td>{mdl.ParameterVersion}</td>");
            sb.Append($"<td><code title='{mdl.InputDigest}'>{mdl.InputDigest[..10]}…</code></td></tr>");
        }
        sb.Append("</table>");
    }

    private static void RenderJudgments(StringBuilder sb, ReviewModel m)
    {
        sb.Append("<h2>机器可读取判定（命题 → 裁决 → 受影响范围）</h2>");
        if (!m.Judgments.Any()) sb.Append("<p class='muted'>主分支无反例判定；请打开 counter-* 分支查看七情景。</p>");
        foreach (var j in m.Judgments)
        {
            sb.Append($"<details open class='judgment {(j.Holds ? "holds" : "fails")}' data-stable-id='judgment-{j.Scenario}'>");
            sb.Append($"<summary><code>{j.PropositionCode}</code> · {j.Scenario} · ");
            sb.Append($"裁决 <b>{j.Determination}</b> {(j.Holds ? "✔ 影响被限制" : "✘ 触发结构化拒绝")}</summary>");
            sb.Append($"<pre>{H(JsonSerializer.Serialize(j, new JsonSerializerOptions { WriteIndented = true }))}</pre>");
            sb.Append("</details>");
        }
    }

    private static void RenderMaterials(StringBuilder sb, ReviewModel m)
    {
        sb.Append("<h2>不可变材料层（原始表示 / 单位 / 基准 / 有效区间 / 失效原因）</h2>");
        sb.Append("<div class='grid4'>");
        Table(sb, "深度基准", m.State.DatumOrder, d =>
            new[] { d.Id, d.Name, d.Unit, d.Description });
        Table(sb, "连续分段（段内单调）", m.State.IntervalOrder, i =>
        {
            var t = i.Top; var b = i.Bottom;
            return new[] { i.Id, i.Name, $"{i.TopDepthRaw} → {i.BottomDepthRaw}",
                t.IsConvertible && b.IsConvertible ? $"有效 {t.Value:0.##}–{b.Value:0.##} m（{i.DatumId}）"
                : "失效:" + (t.Diagnostic ?? b.Diagnostic) };
        });
        Table(sb, "显式边界（缺口/不整合）", m.State.GapOrder, g =>
        {
            var a = Quantity.Depth(g.AboveDepthRaw, g.DatumId); var bb = Quantity.Depth(g.BelowDepthRaw, g.DatumId);
            return new[] { g.Id, g.Type, $"{g.AboveDepthRaw} / {g.BelowDepthRaw}",
                a.IsConvertible && bb.IsConvertible ? $"边界 {a.Value:0.##}–{bb.Value:0.##} m，不插值" : "失效:" + (a.Diagnostic ?? bb.Diagnostic) };
        });
        Table(sb, "储层偏移", m.State.ReservoirOrder, r =>
            new[] { r.Id, r.Name, $"{r.DeltaRRaw} ± {r.UncertaintyRaw}", (r.Apply ? "应用" : "未应用") + "｜" + r.Basis });
        Table(sb, "事件层相关", m.State.EventOrder, e =>
            new[] { e.Id, e.Name, $"{e.DepthRaw}｜外部年龄={e.ExternalAgeCalABPRaw ?? "无"}", e.CorrelationKey + "｜" + e.SourceNote });
        Table(sb, "排除决定（材料保留）", m.State.ExclusionOrder, x =>
            new[] { x.Id, $"{x.MaterialKind}:{x.MaterialId}", x.Reason, $"{x.DecidedBy} @{x.AtEventId}" });
        sb.Append("</div>");

        sb.Append("<h2>年龄锚点：完整分布（不用均值替代）</h2><table>");
        sb.Append("<tr><th>稳定身份</th><th>样品</th><th>深度（原始→规范）</th><th>方法/储层</th>" +
                  "<th>n</th><th>中位数</th><th>95%</th><th>99%（长尾）</th><th>并列组</th><th>状态</th></tr>");
        foreach (var a in m.State.AnchorOrder)
        {
            var dq = Quantity.Depth(a.DepthRaw, a.DatumId);
            var excluded = m.State.Exclusions.Values.Any(e => e.MaterialId == a.Id);
            sb.Append($"<tr data-stable-id='mat-{a.Id}' class='{(excluded ? "excluded" : "")}'>");
            sb.Append($"<td><code>{a.Id}</code></td><td>{H(a.SampleCode)}</td>");
            sb.Append(dq.IsConvertible
                ? $"<td>{H(a.DepthRaw)} <span class='muted'>→ {dq.Value:0.###} {dq.CanonicalUnit}（{dq.Basis}）</span></td>"
                : $"<td class='bad'>{H(a.DepthRaw)}<br>⛔ {H(dq.Diagnostic ?? "")}</td>");
            sb.Append($"<td class='small'>{H(a.Method)}{(a.ReservoirOffsetId is null ? "" : "｜" + a.ReservoirOffsetId)}</td>");
            sb.Append($"<td>{a.Samples.N}</td>");
            sb.Append(dq.IsConvertible && a.Samples.N > 0
                ? $"<td>{a.Samples.Median:0}</td><td>{a.Samples.Q025:0}–{a.Samples.Q975:0}</td>" +
                  $"<td class='{(a.Samples.LongTail ? "tail" : "")}'>{a.Samples.Q005:0}–{a.Samples.Q995:0}</td>"
                : "<td colspan='3' class='bad'>分布不可用于派生</td>");
            sb.Append($"<td>{a.AlternativeGroupId ?? "—"}</td>");
            sb.Append($"<td>{(excluded ? "已排除（决定保留）" : "参与建模")}</td></tr>");
        }
        sb.Append("</table>");
    }

    private static void RenderEvents(StringBuilder sb, ReviewModel m)
    {
        sb.Append("<h2>事件流（回到任意事件＝恢复当时依赖版本与可见结论）</h2>");
        sb.Append("<div class='toolbar'>");
        sb.Append($"<a class='btn' href='/export'>⬇ 导出项目 zip</a>");
        sb.Append("<form method='post' action='/import' enctype='multipart/form-data' class='inline'>");
        sb.Append("<input type='file' name='bundle' accept='.zip' required><button>导入（校验身份/顺序后整体替换）</button></form>");
        sb.Append("<form method='post' action='/actions/reset' class='inline' " +
                  "onsubmit=\"return confirm('删除本地数据库全部数据并重新播种？')\">" +
                  "<button class='danger'>删除本地数据库并重播种</button></form>");
        sb.Append("</div>");
        sb.Append("<table class='events'><tr><th>seq</th><th>稳定身份</th><th>类型</th><th>时间</th>" +
                  "<th>输入摘要/参数</th><th>采纳</th><th>复核视图</th></tr>");
        foreach (var e in m.Events)
        {
            sb.Append($"<tr class='{(e.Adopted ? "" : "notadopted")}' data-stable-id='eventrow-{e.BranchId}-{e.Seq}'>");
            sb.Append($"<td>{e.Seq}</td><td><code>{e.StableDisplayId}</code></td>");
            sb.Append($"<td>{e.Type}</td><td class='small'>{e.At[..19]}</td>");
            sb.Append($"<td class='small'><code>{(e.InputDigest.Length > 10 ? e.InputDigest[..10] + "…" : e.InputDigest)}</code>" +
                      $"{(e.ParameterVersion is null ? "" : "｜" + e.ParameterVersion)}</td>");
            sb.Append($"<td>{(e.Adopted ? "采纳" : "<span class='bad'>未采纳</span>")}</td>");
            sb.Append($"<td><a href='/?branch={e.BranchId}&at={e.Seq}'>回到此事件</a></td></tr>");
        }
        sb.Append("</table>");
    }

    private static void Table<T>(StringBuilder sb, string title, IEnumerable<T> rows,
        Func<T, string[]> cols)
    {
        sb.Append($"<div class='card'><h3>{title}</h3><table>");
        foreach (var r in rows) sb.Append("<tr>" +
            string.Join("", cols(r).Select((c, i) => $"<td{(i == 0 ? " class='id'" : "")}>{H(c)}</td>")) + "</tr>");
        sb.Append("</table></div>");
    }

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
    *{box-sizing:border-box} body{margin:0;font:13px/1.5 -apple-system,'PingFang SC',sans-serif;color:#222;background:#f6f7f5}
    header{background:#173f2e;color:#fff;padding:10px 18px;display:flex;gap:16px;align-items:baseline}
    header b{font-size:16px}.muted{color:#778}.small{font-size:11.5px}
    nav.branches{padding:8px 18px;background:#fff;border-bottom:1px solid #ddd}
    .chip{display:inline-block;padding:3px 10px;margin:2px;border:1px solid #9bb8a3;border-radius:12px;text-decoration:none;color:#234}
    .chip.active{background:#173f2e;color:#fff}.chip i{font-style:normal;opacity:.75;margin-left:4px}
    .grid2{display:grid;grid-template-columns:1.4fr 1fr;gap:14px;padding:12px 18px}
    .grid4{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;padding:0 18px}
    section{background:#fff;border:1px solid #e0e3df;border-radius:8px;padding:10px}
    h2{font-size:14px;margin:6px 0} h3{font-size:12.5px;margin:0 0 6px;color:#173f2e}
    .svgbox{overflow:auto;border:1px solid #e5e8e4;border-radius:6px;background:#fcfdfb}
    table{border-collapse:collapse;width:100%;font-size:12px} td,th{border-bottom:1px solid #eceeed;padding:3px 6px;text-align:left;vertical-align:top}
    th{background:#f0f4f0} td.id code{font-size:11px;color:#173f2e}
    .card{background:#fff;border:1px solid #e0e3df;border-radius:8px;padding:8px;margin-bottom:10px}
    .controls{margin-top:10px}.controls form{margin:6px 0;padding:6px;background:#fafbfa;border:1px solid #eee;border-radius:6px}
    .inline{display:inline-block}.box{border-left:3px solid #173f2e !important;padding-left:10px !important}
    button{background:#173f2e;color:#fff;border:0;border-radius:5px;padding:5px 12px;cursor:pointer;margin-left:6px}
    button.warn{background:#9a6a17} button.danger,.danger{background:#9a2a1e}
    input,select{padding:4px 6px;border:1px solid #c8ccc8;border-radius:4px;font-size:12px;margin:2px}
    .flash{margin:10px 18px;padding:8px 12px;border-radius:6px}.flash.info{background:#e3f1e6;border:1px solid #9bbfa4}
    .flash.warn{background:#fdf2d8;border:1px solid #d8b25e}.flash.error{background:#fbe2dc;border:1px solid #d58a76}
    tr.cur{background:#eef6ef} tr.excluded{opacity:.55} tr.notadopted{background:#fbeee9}
    .bad{color:#b3261e}.tail{font-weight:700;color:#7a2e91}
    .judgment{border:1px solid #ccc;border-radius:6px;padding:6px 8px;margin:6px 0}.judgment.holds{border-color:#7aa984;background:#f1f8f3}
    .judgment.fails{border-color:#d58a76;background:#fdf1ee} pre{white-space:pre-wrap;font-size:11px;margin:6px 0}
    .toolbar{padding:0 18px 8px}.btn{display:inline-block;background:#173f2e;color:#fff;padding:6px 12px;border-radius:6px;text-decoration:none;margin:4px 0}
    .events{margin:0 18px 24px;width:calc(100% - 36px)}
    code{background:#f0f2ef;padding:0 3px;border-radius:3px}
    """;
}
