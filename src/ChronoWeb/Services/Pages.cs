using System.Text;
using ChronoCore.Engine;
using ChronoCore.Storage;
using ChronoCore.Web;

namespace ChronoWeb.Services;

public static class Pages
{
    public static string ReviewPage(ProjectRecord project, DerivationResult frozen, DerivationResult? trial, DerivationResult latest)
    {
        var mainBranch = project.Branches.First(b => b.Id == "main");
        var freezeEv = project.Events.First(e => e.Id == mainBranch.FrozenAtEventId);
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>BX-2026-C 深度年龄模型审阅台</title>
<link rel="stylesheet" href="/app.css"/>
</head><body>
<header>
  <h1>岩芯年代尺审阅台 · BX-2026-C</h1>
  <p class="sub">沉积学小组：岩芯分段 + 年龄锚点 + 事件层 → 深度年龄模型，保留多套可辩护候选。规则在服务端，SVG 由服务端按引擎结果渲染。</p>
</header>
<nav class="tabs">
  <button data-tab="narrative" class="active">完整叙事</button>
  <button data-tab="profile">纵剖与模型</button>
  <button data-tab="materials">不可变材料</button>
  <button data-tab="events">事件/历史读回</button>
  <button data-tab="archive">反例档案</button>
</nav>
""");
        sb.Append("<section class=\"tab\" id=\"tab-narrative\">");
        sb.Append("<h2>一次会话：导入 → 推导 → 边界复核 → 修订/局部改动/冲突 → 冻结 → 新锚点影响</h2>");
        sb.Append("<ol class=\"narrative\">");
        foreach (var step in CasePackScriptSteps())
        {
            sb.Append($"<li data-tags=\"{string.Join(" ", step.Tags)}\"><strong>{System.Net.WebUtility.HtmlEncode(step.Title)}</strong>");
            sb.Append($"<div class=\"tags\">{string.Join(" ", step.Tags.Select(t => $"<span class=tag>{t}</span>"))}</div>");
            sb.Append($"<p>{System.Net.WebUtility.HtmlEncode(step.Expectation)}</p></li>");
        }
        sb.Append("</ol>");
        sb.Append("<div class=\"freezebanner\">冻结事件 <code>").Append(freezeEv.Id).Append("</code> · 摘要 <code>")
          .Append(freezeEv.InputDigest[..24]).Append("…</code> · 参数 <code>").Append(freezeEv.ParameterSetId)
          .Append("</code>。冻结后主干只读；下面的新锚点在试验分支上比较，读回历史时仍是冻结时的结论。</div>");
        sb.Append("</section>");

        sb.Append("<section class=\"tab hidden\" id=\"tab-profile\">");
        sb.Append("<div class=\"controls\">");
        sb.Append("<label>候选模型 <select id=\"candidate\">");
        foreach (var c in frozen.Candidates)
            sb.Append($"<option value=\"{c.CandidateId}\">{c.CandidateId}（rank #{c.Rank}{(c.Tied ? "，并列等优" : "")}）</option>");
        sb.Append("</select></label>");
        sb.Append("<button id=\"btn-freeze\">并发再冻结主干（应被拒绝）</button>");
        sb.Append("<button id=\"btn-archive\">重放反例档案</button>");
        sb.Append("<a class=\"btn\" href=\"/api/export\">导出项目 JSON</a>");
        sb.Append("<button id=\"btn-roundtrip\">导出→新库导入往返校验</button>");
        sb.Append("</div>");
        sb.Append("<div class=\"grid2\">");
        sb.Append("<div><h3>冻结模型（服务端 SVG，元素 data-dv 为稳定显示身份）</h3>")
          .Append("<div id=\"svg-main\">").Append(SvgViewRenderer.Render(frozen, frozen.Candidates[0].CandidateId)).Append("</div></div>");
        sb.Append("<div><h3>新锚点试验分支 <code>trial-new-anchor</code></h3>");
        if (trial is not null)
            sb.Append("<div id=\"svg-trial\">").Append(SvgViewRenderer.Render(trial, trial.Candidates[0].CandidateId)).Append("</div>");
        sb.Append("</div></div>");

        sb.Append("""
<div class="trialform">
  <h3>冻结后比较新锚点影响（在新试验分支上，不污染冻结视图）</h3>
  <input id="t-depth" placeholder="深度，如 7.10 mbsf" value="7.10 mbsf"/>
  <input id="t-age" placeholder="年龄，如 7280 yr BP" value="7280 yr BP"/>
  <input id="t-sigma" placeholder="1σ，如 60 yr" value="60 yr"/>
  <button id="btn-trial">提交试验锚点</button>
  <span id="trial-result" class="msg"></span>
</div>
<div id="profile-msg" class="msg"></div>
<table id="candidate-table"><thead><tr><th>候选</th><th>排名</th><th>并列</th><th>拟合分（越窄越优）</th><th>未覆盖罚分</th><th>速率区间数</th><th>失效原因</th></tr></thead><tbody></tbody></table>
</section>
""");

        sb.Append("<section class=\"tab hidden\" id=\"tab-materials\"><h2>不可变材料（原始表示保留；派生层才规范化）</h2>");
        sb.Append("<p class=hint>六类：深度基准 / 分段缺口 / 放射性年龄分布 / 储层偏移 / 事件层相关 / 排除决定。无法转换的值留在原位置并附诊断。</p>");
        sb.Append("<table id=\"material-table\"><thead><tr><th>稳定身份</th><th>类别</th><th>来源（片段:行）</th><th>原始表示</th><th>规范化(SI)</th><th>单位/基准/有效区间</th><th>状态/失效原因</th></tr></thead><tbody></tbody></table>");
        sb.Append("</section>");

        sb.Append("<section class=\"tab hidden\" id=\"tab-events\"><h2>事件台账（回到任意旧事件 = 恢复当时依赖版本与可见结论）</h2>");
        sb.Append("<table id=\"event-table\"><thead><tr><th>#</th><th>事件</th><th>分支</th><th>父事件</th><th>类型</th><th>参数版本</th><th>输入摘要</th><th>结果</th><th></th></tr></thead><tbody></tbody></table>");
        sb.Append("<div id=\"event-view\"><p class=hint>选择一条事件查看当时的 SVG 与显示值。</p></div>");
        sb.Append("</section>");

        sb.Append("<section class=\"tab hidden\" id=\"tab-archive\"><h2>反例档案：基线 → 每次只改一个直接来源 → 影响范围检验</h2>");
        sb.Append("<p class=hint>采纳与未采纳结果都保留；四条核心判断以机器可读 verdict 形式给出；越界影响必须为 0。</p>");
        sb.Append("<button id=\"btn-load-archive\">加载/重放反例档案</button>");
        sb.Append("<div id=\"archive-view\"></div></section>");

        sb.Append("""
<script src="/app.js"></script>
</body></html>
""");
        return sb.ToString();
    }

    private static IEnumerable<(string Title, string Expectation, string[] Tags)> CasePackScriptSteps()
    {
        foreach (var s in ChronoCore.CasePack.CaseScript.Build())
            yield return (s.Title, s.Expectation, s.BoundaryTags);
    }
}
