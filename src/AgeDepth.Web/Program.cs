using System.Text.Json;
using AgeDepth;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbPath = Environment.GetEnvironmentVariable("AGEDEPTH_DB") ?? "agedepth.db";
var store = new Store(dbPath);
var gate = new object();

string FindPack()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var p = Path.Combine(dir.FullName, "casepack", "casepack.json");
        if (File.Exists(p)) return p;
        dir = dir.Parent;
    }
    return Path.Combine(AppContext.BaseDirectory, "casepack", "casepack.json");
}

lock (gate)
{
    if (store.HeadSeq() == 0)
    {
        CasePack.Seed(store, FindPack());
        ArchiveBuilder.Build(store);
    }
}

CaseState StateAt(long? at) { lock (gate) return CasePack.Replay(store, at); }

app.MapGet("/", (long? at) =>
{
    var state = StateAt(at);
    var events = store.Events();
    var head = store.HeadSeq();
    var diags = state.Computation.Segments.SelectMany(s => s.Diagnostics).ToList();
    var evRows = string.Join("", events.Select(e =>
        $"<tr data-id='{e.Id}'><td>{e.Seq}</td><td><a href='/?at={e.Seq}'>{e.Id}</a></td>" +
        $"<td>{e.Kind}</td><td>{e.Status}</td><td>{e.ParamVersion}</td><td>{e.InputHash}</td></tr>"));
    var diagRows = string.Join("", diags.Select(d =>
        $"<li data-id='diag-{d.Code}-{d.MaterialId ?? d.SegmentId}'><b>{d.Code}</b> @{d.SegmentId}" +
        $"{(d.Depth is null ? "" : $" depth={d.Depth}")} — {d.Message}</li>"));
    var decRows = string.Join("", state.Decisions.Select(d => $"<li data-id='{d.Id}'>{d.Id}: {d.Payload}</li>"));
    var frozen = state.Frozen is null ? "未冻结" : $"已冻结 @{state.Frozen.Seq}（{state.Frozen.Id}）";
    var replayNote = at is null ? "" : $"<p class='warn'>正在查看事件 #{at} 时的历史状态（依赖版本与可见结论按当时恢复）。<a href='/'>回到最新</a></p>";

    var html = $$"""
    <!doctype html><html lang="zh"><head><meta charset="utf-8"><title>岩芯年代尺审阅</title>
    <style>
      body{font-family:system-ui;margin:24px;max-width:1100px} h1{font-size:20px}
      section{border:1px solid #ccc;border-radius:8px;padding:12px;margin:12px 0}
      table{border-collapse:collapse;font-size:12px} td,th{border:1px solid #ddd;padding:3px 8px}
      .warn{background:#fdf3e0;padding:6px;border-radius:6px} button{margin:2px}
      svg{width:100%;height:auto;background:#fafafa}
    </style></head><body>
    <h1>岩芯年代尺：深度锚点、沉积速率与多候选模型谱系 <small>(KR-1201)</small></h1>
    <p>工作流：导入 → 推导 → 复核 → 冻结 → 比较新锚点影响 → 导出/再导入。状态：{{frozen}}｜参数版本 {{state.Params.Version}}｜事件头 #{{head}}</p>
    {{replayNote}}
    <section><h2>岩芯纵剖 · 锚点分布 · 不确定度带</h2>{{Svg.Render(state)}}</section>
    <section><h2>速率区间</h2>{{Svg.RateTable(state)}}</section>
    <section><h2>诊断（带位置，不用空值伪装成功）</h2><ul>{{diagRows}}</ul></section>
    <section><h2>人工决定（冲突决定均保留）</h2><ul>{{decRows}}</ul></section>
    <section><h2>操作</h2>
      <button onclick="freeze()">冻结当前模型</button>
      <button onclick="addAnchor()">加入新锚点并比较影响</button>
      <button onclick="decide('accept')">接受主候选</button>
      <button onclick="decide('reject')">否定主候选</button>
      <a href="/api/export" download>导出</a>
      <button onclick="reimport()">删除本地库并再导入</button>
      <a href="/archive">反例档案</a>
      <pre id="out"></pre>
    </section>
    <section><h2>事件日志（点击可回到任一历史事件）</h2>
      <table><tr><th>#</th><th>身份</th><th>类型</th><th>状态</th><th>参数版本</th><th>输入摘要</th></tr>{{evRows}}</table>
    </section>
    <script>
    const out = m => document.getElementById('out').textContent = typeof m === 'string' ? m : JSON.stringify(m, null, 2);
    async function freeze(){
      const head = await (await fetch('/api/head')).json();
      const r = await fetch('/api/freeze?expected=' + head, {method:'POST'});
      out(await r.json()); if(r.ok) location.reload();
    }
    async function addAnchor(){
      const r = await fetch('/api/anchor', {method:'POST'});
      out(await r.json()); location.reload();
    }
    async function decide(action){
      const r = await fetch('/api/decision', {method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({action, target:'candidate:seg-B', rationale:'审阅页操作 ' + new Date().toISOString()})});
      out(await r.json()); location.reload();
    }
    async function reimport(){
      const data = await (await fetch('/api/export')).text();
      const r = await fetch('/api/reimport', {method:'POST', body:data});
      out(await r.json()); location.reload();
    }
    </script></body></html>
    """;
    return Results.Content(html, "text/html");
});

app.MapGet("/api/head", () => Results.Json(store.HeadSeq()));

app.MapGet("/api/state", (long? at) =>
{
    var s = StateAt(at);
    return Results.Json(new
    {
        kind = s.Computation.Kind.ToString(),
        candidates = s.Computation.AllCandidates.Select(c => new { c.Id, c.SegmentId, c.Status, c.Reason }),
        diagnostics = s.Computation.Segments.SelectMany(x => x.Diagnostics),
        frozen = s.Frozen?.Id
    });
});

app.MapPost("/api/freeze", (long expected) =>
{
    lock (gate)
    {
        var state = StateAt(null);
        var payload = JsonSerializer.Serialize(new
        {
            candidates = state.Computation.AllCandidates.Where(c => c.Status == "accepted").Select(c => c.Id)
        }, Hashing.Json);
        var (kind, ev) = store.Freeze(expected, payload);
        return kind == ResultKind.Ok
            ? Results.Json(new { status = "frozen", eventId = ev!.Id })
            : Results.Json(new { status = "conflict", eventId = ev!.Id }, statusCode: 409);
    }
});

app.MapPost("/api/anchor", () =>
{
    lock (gate)
    {
        var before = StateAt(null).Computation.AllCandidates
            .Where(c => c.SegmentId == "seg-B" && c.Status == "accepted").Select(c => c.Id).ToArray();
        var anchor = new AgeAnchor("anc-new-" + store.HeadSeq(), "seg-B",
            Quantity.Of(5.40, "mbsf", "mbsf"),
            new AgeDistribution("samples", new[] { 10.9, 11.05, 11.2, 11.3, 11.45 }, "ka BP"),
            Quantity.Of(0.38, "ka", "Marine20 ΔR"), "review-page");
        var json = JsonSerializer.Serialize(anchor, Hashing.Json);
        store.PutMaterial(new Material(anchor.Id, "anchor", Hashing.Sha256(json), json));
        var ev = store.AppendEvent("source-change", Array.Empty<string>(), StateAt(null).Params.Version,
            Hashing.Sha256(json),
            JsonSerializer.Serialize(new { replaces = "", material = JsonSerializer.Deserialize<JsonElement>(json), note = "新锚点影响比较" }, Hashing.Json), "ok");
        CasePack.RecordComputation(store, ev.Id);
        var after = StateAt(null).Computation.AllCandidates
            .Where(c => c.SegmentId == "seg-B" && c.Status == "accepted").Select(c => c.Id).ToArray();
        return Results.Json(new { before, after, eventId = ev.Id });
    }
});

app.MapPost("/api/decision", (JsonElement body) =>
{
    lock (gate)
    {
        var ev = store.AppendEvent("decision", Array.Empty<string>(), StateAt(null).Params.Version,
            Hashing.Sha256(body.GetRawText()), body.GetRawText(), "recorded");
        return Results.Json(new { eventId = ev.Id });
    }
});

app.MapGet("/api/export", () =>
{
    lock (gate) return Results.Text(store.Export(), "application/json");
});

app.MapPost("/api/import", async (HttpRequest req) =>
{
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    lock (gate)
    {
        var (kind, msg) = store.Import(body);
        return kind == ResultKind.Ok ? Results.Json(new { status = msg }) : Results.Json(new { status = kind.ToString(), msg }, statusCode: 422);
    }
});

app.MapPost("/api/reimport", async (HttpRequest req) =>
{
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    lock (gate)
    {
        var fresh = new Store(dbPath + ".reimport");
        var (kind, msg) = fresh.Import(body);
        if (kind != ResultKind.Ok) return Results.Json(new { status = kind.ToString(), msg }, statusCode: 422);
        var roundtrip = fresh.Export() == store.Export();
        return Results.Json(new { status = "reimported", identityPreserved = roundtrip });
    }
});

app.MapGet("/archive", () =>
{
    var judgments = store.Events().Where(e => e.Kind == "judgment").ToList();
    var rows = string.Join("", judgments.Select(j =>
        $"<tr data-id='{j.Id}'><td>{j.Id}</td><td>{j.Status}</td><td><code>{System.Net.WebUtility.HtmlEncode(j.Payload)}</code></td></tr>"));
    var html = $"""<!doctype html><html lang="zh"><head><meta charset="utf-8"><title>反例档案</title>
    <style>body{{font-family:system-ui;margin:24px}}td,th{{border:1px solid #ddd;padding:4px 8px;font-size:12px}}table{{border-collapse:collapse}}</style>
    </head><body><h1>反例档案：基线 + 单源扰动</h1>
    <p>每条判定都是机器可读事件（规则 R1 单调仅限不断层段 / R2 分布不被均值替代 / R3 等优候选保留 / R4 局部重算 / R5 并发冻结）。被采纳与未采纳结果均保留。</p>
    <table><tr><th>事件身份</th><th>判定</th><th>载荷</th></tr>{rows}</table>
    <p><a href="/">返回审阅页</a></p></body></html>""";
    return Results.Content(html, "text/html");
});

app.Run();

public partial class Program { }
