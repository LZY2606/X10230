const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));

$$(".tabs button").forEach(b => b.onclick = () => {
  $$(".tabs button").forEach(x => x.classList.remove("active"));
  b.classList.add("active");
  $$("section.tab").forEach(x => x.classList.add("hidden"));
  $("#tab-" + b.dataset.tab).classList.remove("hidden");
});

async function getJson(url) {
  const r = await fetch(url);
  return r.json();
}

function candidateTable(r) {
  $("#candidate-table tbody").innerHTML = r.candidates.map(c => `
    <tr><td><code>${c.candidateId}</code></td><td>${c.rank}</td>
    <td>${c.tied ? '<span class="bad">并列等优（全部保留）</span>' : ""}</td>
    <td>${Number.isFinite(c.fitScore) ? c.fitScore.toFixed(0) : "∞ 不可行"}</td>
    <td>${c.coveragePenalty.toFixed(0)}</td><td>${c.rates.length}</td>
    <td>${esc(c.infeasibleReason || "")}</td></tr>`).join("");
}

$("#candidate").onchange = async () => {
  const ev = await currentFreezeEvent();
  $("#svg-main").innerHTML = await loadSvg(ev, $("#candidate").value);
};

async function currentFreezeEvent() {
  const p = await getJson("/api/project");
  const main = p.data.branches.find(b => b.id === "main");
  return main.frozenAtEventId || main.frozenAtEventID;
}

async function loadSvg(eventId, candidate) {
  const r = await fetch(`/api/svg/${eventId}?candidate=${encodeURIComponent(candidate)}`);
  return await r.text();
}

(async function init() {
  const p = await getJson("/api/project");
  const freezeEv = p.data.branches.find(b => b.id === "main").frozenAtEventId;
  const res = await getJson("/api/result/" + freezeEv);
  candidateTable(res.data);
  renderMaterials(p.data);
  renderEvents(p.data);
})();

function renderMaterials(p) {
  const rows = p.materials.map(m => {
    const raw = [m.depthRaw?.raw, m.ageRaw?.raw, m.sigmaRaw?.raw, m.eventAgeRaw?.raw, m.deltaR?.raw]
      .filter(Boolean).map(esc).join(" / ");
    const si = [m.depthSiMbsf != null ? m.depthSiMbsf.toFixed(3) + " mbsf" : "",
      m.ageSiYrBp != null ? Math.round(m.ageSiYrBp) + " cal yr BP" : ""].filter(Boolean).join(" / ");
    const basis = [`基准 ${m.depthDatum}`, m.ageScale, m.distribution,
      m.reservoirValidFrom ? `储层有效 ${m.reservoirValidFrom.raw}–${m.reservoirValidTo.raw}` : "",
      m.boundaryAtBase !== "Continuous" ? "边界:" + m.boundaryAtBase : "",
      m.overturnedSection ? "倒置层段" : ""].filter(Boolean).join(" · ");
    const status = m.diagnostic ? `<span class="bad">${esc(m.diagnostic)}</span>`
      : m.excluded ? `已排除（决定保留）：${esc(m.exclusionReason)}` : "有效";
    return `<tr><td><code>${m.id}</code></td><td>${m.kind}</td>
      <td>${esc(m.sourceRef)}:${m.sourceLine}<br><small>${esc(m.sourceFragment || "")} · ${esc(m.sampleCode || "")} ${esc(m.eventName || "")}</small></td>
      <td>${raw || "—"}</td><td>${si || "—"}</td><td>${esc(basis)}</td><td>${status}</td></tr>`;
  });
  $("#material-table tbody").innerHTML = rows.join("");
}

function renderEvents(p) {
  $("#event-table tbody").innerHTML = p.events.map(e => `
    <tr><td>${e.seq}</td><td><code>${e.id}</code></td><td>${e.branchId}</td>
    <td>${e.parentEventId ? "<code>" + esc(e.parentEventId) + "</code>" : "—"}</td>
    <td>${esc(e.type)}</td><td>${esc(e.parameterSetId)}</td>
    <td><code>${esc(e.inputDigest.slice(0, 16))}…</code></td>
    <td>${e.resultRef ? "<code>" + esc(e.resultRef) + "</code>" : "—"}</td>
    <td>${e.resultRef ? `<button data-ev="${e.id}">读回当时视图</button>` : ""}</td></tr>`).join("");
  $$("#event-table button").forEach(b => b.onclick = async () => {
    const id = b.dataset.ev;
    const [svgR, dvR] = await Promise.all([fetch(`/api/svg/${id}?candidate=all-retained`), getJson("/api/display-values/" + id)]);
    const svg = await svgR.text();
    const dvs = dvR.data || [];
    $("#event-view").innerHTML = `<h3>事件 <code>${id}</code> 当时的依赖版本与可见结论</h3>
      <div>${svg}</div>
      <details open><summary>稳定显示值（${dvs.length} 个，身份跨导出/导入保持）</summary>
      <div class="dvlist"><table><thead><tr><th>稳定身份</th><th>类别</th><th>值</th><th>单位/基准</th><th>失效原因</th></tr></thead><tbody>
      ${dvs.map(d => `<tr><td><code>${esc(d.id)}</code></td><td>${esc(d.kind)}</td><td>${esc(d.text)}</td>
        <td>${esc([d.unit, d.datum].filter(Boolean).join(" · "))}</td><td>${esc(d.invalidReason || "")}</td></tr>`).join("")}
      </tbody></table></div></details>`;
  });
}

$("#btn-freeze").onclick = async () => {
  const r = await fetch("/api/freeze?branchId=main", { method: "POST" }).then(x => x.json());
  const el = $("#profile-msg");
  el.textContent = r.data.outcome === "FreezeRaced"
    ? "✓ 并发冻结被拒绝：" + r.data.message
    : "结果：" + r.data.outcome + " " + r.data.message;
  el.className = "msg " + (r.data.outcome === "FreezeRaced" ? "ok" : "bad");
};

$("#btn-trial").onclick = async () => {
  const body = {
    trialBranchId: "trial-review-" + Date.now(),
    depthRaw: $("#t-depth").value, ageRaw: $("#t-age").value, sigmaRaw: $("#t-sigma").value
  };
  const r = await fetch("/api/trial-anchor", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
  }).then(x => x.json());
  const el = $("#trial-result");
  if (!r.ok) { el.textContent = "输入不完整：" + JSON.stringify(r.error?.diagnostics ?? r.error); el.className = "msg bad"; return; }
  el.innerHTML = `✓ 试验分支 <code>${r.data.trialBranchId}</code>；局部重算段：${(r.data.affectedScope || []).join(", ") || "（未落在任何可插值段）"}；结果 ${r.data.result?.outcome}`;
  el.className = "msg ok";
  const svg = await loadSvg(r.data.event.id, r.data.result.candidates[0].candidateId);
  $("#svg-trial").innerHTML = svg;
};

$("#btn-roundtrip").onclick = async () => {
  const el = $("#profile-msg");
  el.textContent = "正在导出并导入到全新数据库…";
  const r = await fetch("/api/import-roundtrip", { method: "POST" }).then(x => x.json());
  el.innerHTML = r.ok && r.data.verified
    ? `✓ ${r.data.report.outcome}：${r.data.report.message}`
    : `✗ 往返异常：${r.data.report?.outcome} ${r.data.report?.message || r.error}`;
  el.className = "msg " + (r.ok && r.data.verified ? "ok" : "bad");
};

$("#btn-load-archive").onclick = loadArchive;
$("#btn-archive").onclick = () => { $$(".tabs button").find(x => x.dataset.tab === "archive").click(); loadArchive(); };

async function loadArchive() {
  const r = await getJson("/api/archive");
  $("#archive-view").innerHTML = r.data.map(run => `
    <div class="archive-run">
      <h3>${esc(run.label)} <span class="pill ${run.adoption === "Adopted" ? "adopted" : "notadopted"}">${run.adoption === "Adopted" ? "采纳" : "未采纳"}</span></h3>
      <div class="tags">${run.tags.map(t => `<span class=tag>${esc(t)}</span>`).join("")}</div>
      <p>${esc(run.changeDescription)} · 直接来源 <code>${esc(run.changedSourceMaterialId)}</code>
      · 受影响显示值 ${run.affectedCount} 个，越界 <b class="${run.outOfScopeCount ? "bad" : "ok"}">${run.outOfScopeCount}</b></p>
      ${run.verdicts.map(v => `<div class="verdict ${v.holds ? "" : "fail"}">
        <b>${v.holds ? "✓" : "✗"} <code>${esc(v.ruleId)}</code></b> — ${esc(v.statement)}
        <br><small>证据：${esc(v.evidence)}</small></div>`).join("")}
      <details><summary>受影响的显示值（前 60 个）</small></summary><table><thead><tr><th>稳定身份</th><th>基线</th><th>改动后</th><th>范围</th></tr></thead><tbody>
      ${run.affected.map(a => `<tr><td><code>${esc(a.displayId)}</code></td><td>${esc(a.baselineText)}</td><td>${esc(a.variantText)}</td>
        <td class="${a.withinExpectedScope ? "ok" : "bad"}">${a.withinExpectedScope ? "局部内" : "越界！"}</td></tr>`).join("")}
      </tbody></table></details>
    </div>`).join("");
}
