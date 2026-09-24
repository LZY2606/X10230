let WS = 'A-LINE';
let VIEW_SEQ = null;
let CACHE = null;

function toast(msg, kind) {
  const t = document.getElementById('toast');
  const d = document.createElement('div');
  d.textContent = msg;
  if (kind === 'bad') d.style.background = '#7a2020';
  t.appendChild(d);
  setTimeout(() => d.remove(), 4200);
}

async function api(path, opts) {
  const r = await fetch(path, opts);
  const text = await r.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!r.ok) {
    const msg = typeof body === 'object' && body
      ? `${body.outcome || r.status}: ${body.error || JSON.stringify(body)}`
      : `${r.status} ${text}`;
    toast(msg, 'bad');
    throw new Error(msg);
  }
  return body;
}

async function loadWorkspaces() {
  const list = await api('/api/workspaces');
  const box = document.getElementById('wsTabs');
  box.innerHTML = '';
  for (const w of list) {
    const b = document.createElement('button');
    b.textContent = `${w.name} (${w.Kind === 'counterexample' ? '反例' : '叙事'})`;
    b.className = w.Id === WS ? 'active' : '';
    b.onclick = () => { WS = w.Id; VIEW_SEQ = null; load(); };
    box.appendChild(b);
  }
}

async function load() {
  const url = `/api/workspaces/${encodeURIComponent(WS)}${VIEW_SEQ ? `?seq=${VIEW_SEQ}` : ''}`;
  CACHE = await api(url);
  const s = CACHE.state;
  document.getElementById('chainBadge').textContent =
    (CACHE.chainValid ? '哈希链有效' : '哈希链失效') + ` @${CACHE.viewingSeq}/${CACHE.currentHeadSeq}`;
  document.getElementById('chainBadge').className = 'pill ' + (CACHE.chainValid ? 'ok' : 'bad');
  const fz = CACHE.frozen;
  document.getElementById('freezeBadge').textContent = fz
    ? `已冻结 ${fz.ModelId} by ${fz.Actor}` : '未冻结（草稿）';
  document.getElementById('freezeBadge').className = 'pill ' + (fz ? 'info' : 'ok');
  document.getElementById('freezeToken').value = s.activeParamsId || '';

  document.getElementById('figureWrap').innerHTML =
    `<img alt="age depth figure" style="width:100%" src="${CACHE.figureUrl}&t=${Date.now()}"/>`;
  const p = s.project || {};
  document.getElementById('metaLine').innerHTML =
    `项目 <b data-id="proj:${p.projectId}">${p.name || ''}</b> · 深度基准 ${p.datum || ''} · ` +
    `深度方向 ${p.depthDirection || ''} · 年龄单位 ${p.ageUnit || ''} · 时间基准 ${p.calendarReference || ''}`;

  renderMaterials(s);
  renderDecisions(s);
  renderTimeline();
  renderCandidates(s);
  renderTrials(s);
}

function esc(v) {
  return String(v ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}

function renderMaterials(s) {
  let segRows = s.segments.map(x => `<tr><td data-id="seg:${x.segmentId}">${x.segmentId}</td><td>${esc(x.topDepthRaw)}</td><td>${esc(x.baseDepthRaw)}</td><td>${esc(x.lithology)}</td></tr>`).join('');
  const gapRows = s.gaps.map(x => `<tr><td data-id="gap:${x.gapId}">${x.gapId}</td><td>${x.kind}</td><td>${x.aboveSegmentId}→${x.belowSegmentId}</td><td>${esc(x.note || '')}</td></tr>`).join('');
  const anchorRows = s.anchors.map(a => {
    const q = quantiles(a.samples);
    return `<tr><td data-id="anchor:${a.anchorId}">${a.anchorId}</td><td>${a.segmentId}</td><td>${esc(a.depthRaw)}</td><td>${a.labCode}</td><td>${q.q50.toFixed(2)}</td><td>[${q.q05.toFixed(2)},${q.q95.toFixed(2)}]</td><td>${a.samples.length}样本</td></tr>`;
  }).join('');
  const offRows = s.offsets.map(o => `<tr><td data-id="offset:${o.offsetId}">${o.offsetId}</td><td>${o.anchorId}</td><td>${o.offsetKa}±${o.sigmaKa} ka</td><td>${esc(o.basis)}</td></tr>`).join('');
  const layRows = s.layers.map(l => `<tr><td data-id="layer:${l.layerId}">${l.layerId}</td><td>${esc(l.depthRaw)}</td><td>${l.layerType}</td><td>${esc(l.correlatesTo || '—')}</td></tr>`).join('');
  document.getElementById('materialsBox').innerHTML =
    `<table><tr><th>分段</th><th>顶深(原文)</th><th>底深(原文)</th><th>岩性</th></tr>${segRows}</table>` +
    `<table style="margin-top:6px"><tr><th>显式边界</th><th>类型</th><th>段</th><th>说明</th></tr>${gapRows}</table>` +
    `<table style="margin-top:6px"><tr><th>锚点</th><th>段</th><th>深度原文</th><th>实验室</th><th>q50</th><th>q05–q95</th><th>分布</th></tr>${anchorRows}</table>` +
    `<table style="margin-top:6px"><tr><th>储层偏移</th><th>锚点</th><th>量值</th><th>依据</th></tr>${offRows}</table>` +
    `<table style="margin-top:6px"><tr><th>事件层</th><th>深度原文</th><th>类型</th><th>相关</th></tr>${layRows}</table>`;
}

function quantiles(xs) {
  const z = [...xs].sort((a, b) => a - b);
  const Q = p => { const h = (z.length - 1) * p, lo = Math.floor(h), f = h - lo; return z[lo] * (1 - f) + z[Math.min(lo + 1, z.length - 1)] * f; };
  return { q05: Q(.05), q50: Q(.5), q95: Q(.95) };
}

function renderDecisions(s) {
  const rows = s.decisions.map(d =>
    `<tr><td data-id="decision:${d.decisionId}">${d.decisionId}</td><td>${d.targetId}</td><td>${d.action}</td><td>${esc(d.rationale)}</td><td>${d.author}</td></tr>`).join('');
  const params = s.params.map(p =>
    `<tr><td data-id="params:${p.paramsId}">${p.paramsId} v${p.version}</td><td>预算 ${p.enumerationBudget}</td><td>膨胀 ${p.ageInflation}</td><td>长尾比 ${p.longTailRatio}</td><td>${(s.activeParamsId === p.paramsId) ? '★生效' : ''}</td></tr>`).join('');
  document.getElementById('decisionsBox').innerHTML =
    `<table><tr><th>决定</th><th>对象</th><th>动作</th><th>理由</th><th>作者</th></tr>${rows}</table>` +
    `<table style="margin-top:6px"><tr><th>参数</th><th>枚举预算</th><th>年龄膨胀</th><th>长尾阈值</th><th>状态</th></tr>${params}</table>`;
}

function renderTimeline() {
  const box = document.getElementById('timeline');
  box.innerHTML = '';
  for (const e of CACHE.events) {
    const d = document.createElement('div');
    d.className = 'ev ' + (e.type.startsWith('judgment') || e.type === 'decision.conflict' || e.type === 'freeze.conflict' ? 'judgment'
      : e.type.includes('freeze') ? 'freeze' : e.type.startsWith('trial') ? 'trial' : '');
    d.textContent = `#${e.seq} ${e.type} · ${e.actor} · ${e.id}`;
    d.title = JSON.stringify(e.payload);
    d.onclick = () => { VIEW_SEQ = e.seq; load(); };
    if (e.seq === CACHE.viewingSeq) d.style.background = '#dce9f4';
    box.appendChild(d);
  }
}

function renderCandidates(s) {
  const m = s.models[s.models.length - 1];
  const box = document.getElementById('candBox');
  if (!m) { box.textContent = '尚无模型'; return; }
  let html = `<div class="muted" data-id="model:${m.modelId}">${m.modelId} · 参数 ${m.paramsId} · 输入指纹 ${m.inputFingerprint} · 总结果 ${m.overallOutcome}</div>`;
  for (const r of m.segments) {
    html += `<div style="margin-top:4px"><span class="pill ${r.outcome === 'Ok' ? 'ok' : 'bad'}">${r.segmentId} ${r.outcome}</span>`;
    for (const c of r.candidates) {
      html += `<div data-id="candidate:${c.candidateId}" class="muted">　${c.candidateId} · score ${c.score} · ${c.anchorIds.length} 锚点 · 速率 ${c.rates.length} 段</div>`;
    }
    for (const d of r.diagnostics)
      html += `<div class="diag" data-id="diag:${r.segmentId}:${d.code}">　⚠ ${d.code}: ${esc(d.message)}${d.jsonPointer ? ' @' + d.jsonPointer : ''}</div>`;
    html += '</div>';
  }
  box.innerHTML = html;
}

function renderTrials(s) {
  const box = document.getElementById('trialBox');
  box.innerHTML = s.trials.map(t =>
    `<div class="ev trial"><b data-id="trial:${t.trialId}">${t.trialId}</b> · ${t.outcome} · 影响 ${t.impactScope} [${(t.affectedSegmentIds || []).join(',')}] · ${t.adopted ? '已采纳' : '未采纳'}<br><span class="muted">来源 ${esc(t.directSourceId)}：${esc(t.mutation)}</span></div>`
  ).join('') || '<span class="muted">暂无试验</span>';
}

async function doFreeze() {
  const token = document.getElementById('freezeToken').value.trim();
  const r = await api(`/api/workspaces/${WS}/freeze`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ expectedParamsId: token || null, actor: 'ui-reviewer' })
  });
  toast(r.ok ? '冻结成功（模型已不可变）' : `并发冻结冲突，已留审计事件 ${r.conflict?.Id}`, r.ok ? 'ok' : 'bad');
  VIEW_SEQ = null; await load();
}

function normalSamples(mu, sig) {
  const out = [];
  for (let i = 0; i < 120; i++) {
    let z = 0;
    for (let j = 0; j < 12; j++) z += Math.random() * 2 - 1;
    out.push(+(mu + sig * z / 12 * 3.4641).toFixed(3));
  }
  return out;
}

async function doTrial(adopt) {
  const seg = document.getElementById('tSeg').value.trim() || (CACHE.state.segments[0]?.segmentId ?? '');
  const depth = document.getElementById('tDepth').value.trim() || '1.00m';
  const mu = parseFloat(document.getElementById('tMu').value || '10');
  const sig = parseFloat(document.getElementById('tSig').value || '0.5');
  const id = 'A-UI-' + Math.random().toString(36).slice(2, 7);
  const r = await api(`/api/workspaces/${WS}/trial`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      directSourceId: `${id}(new)`, mutation: `审阅台新锚点试验 ${depth}`, adopt,
      override: { kind: 'anchor-add', id, segmentId: seg, depthRaw: depth, samples: normalSamples(mu, sig) }
    })
  });
  toast(`${adopt ? '采纳' : '未采纳'}：结果 ${r.outcome}，影响范围 ${r.impactScope}，受影响段 ${r.affectedSegmentIds.join(',')}`);
  VIEW_SEQ = null; await load();
}

async function recompute() {
  await api(`/api/workspaces/${WS}/recompute`, { method: 'POST' });
  toast('已重算（新模型事件，历史不覆盖）');
  VIEW_SEQ = null; await load();
}

function jump(seq) { VIEW_SEQ = seq === -1 ? null : seq; load(); }

async function doExport() {
  const r = await fetch(`/api/workspaces/${WS}/export`);
  const blob = await r.blob();
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `${WS}-archive.json`;
  a.click();
}

async function doImport() {
  const f = document.getElementById('importFile').files[0];
  if (!f) return toast('请先选择归档文件', 'bad');
  const r = await fetch('/api/import', { method: 'POST', body: await f.text() });
  if (!r.ok) { toast('导入失败：' + (await r.text()), 'bad'); return; }
  toast('导入成功：身份、顺序、来源关系按哈希链恢复');
  VIEW_SEQ = null; await loadWorkspaces(); await load();
}

async function doDeleteDb() {
  if (!confirm('删除服务器 data/agedepth.db？删除后将重建并重新播种案例包，之后可导入你的归档验证往返')) return;
  await api('/api/admin/reset-db', { method: 'POST' });
  toast('本地数据库已删除并重建（样例重新播种）');
  VIEW_SEQ = null; await loadWorkspaces(); WS = 'A-LINE'; await load();
}

loadWorkspaces().then(load);
