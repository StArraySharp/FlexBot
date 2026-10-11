/* ---------- 插件 ---------- */
let lastPlugJson = '';

async function pollPlugins() {
  try {
    const list = await (await fetch('/api/plugins')).json();
    const fp = JSON.stringify(list);
    if (fp === lastPlugJson) return; // 数据未变化：跳过重建，避免整页闪烁
    lastPlugJson = fp;
    renderPlugins(list);
  } catch {}
}

/* 卡片内联图标（避免额外请求） */
const PLUG_ICONS = {
  reload: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 5V2L7 6l5 5V8a6 6 0 1 1-6 6H4a8 8 0 1 0 8-9Z"/></svg>',
  gear: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M19.43 12.98c.04-.32.07-.65.07-.98s-.02-.66-.07-.98l2.11-1.65c.19-.15.24-.42.12-.64l-2-3.46c-.12-.22-.37-.31-.6-.22l-2.49 1c-.52-.4-1.08-.73-1.69-.98l-.38-2.65A.5.5 0 0 0 14 2h-4a.5.5 0 0 0-.49.42l-.38 2.65c-.61.25-1.17.59-1.69.98l-2.49-1c-.23-.08-.48 0-.6.22l-2 3.46c-.13.22-.07.49.12.64l2.11 1.65c-.04.32-.08.65-.08.98s.03.66.08.98l-2.11 1.65c-.19.15-.24.42-.12.64l2 3.46c.12.22.37.31.6.22l2.49-1c.52.4 1.08.73 1.69.98l.38 2.65c.04.24.25.42.49.42h4a.5.5 0 0 0 .49-.42l.38-2.65c.61-.25 1.17-.58 1.69-.98l2.49 1c.23.08.48 0 .6-.22l2-3.46c.12-.22.07-.49-.12-.64l-2.11-1.65ZM12 15.5A3.5 3.5 0 1 1 15.5 12 3.5 3.5 0 0 1 12 15.5Z"/></svg>',
};

/* 前端转义：插件清单（plugin.json / AI 生成）、设置定义、persona 文件里的字符串都可能含 < > " '，
   拼进 innerHTML / 属性前一律过它。注意不能叫 esc —— config.js 也在顶层声明了同名常量，
   而所有 js 会被拼成同一个 /app.js，重名会直接让整包语法错误。 */
const plugEsc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

function renderPlugins(list) {
  plugStat = { loaded: list.filter(p => p.loaded).length, total: list.length };
  $('plugCount').textContent = `${plugStat.loaded} / ${plugStat.total} 已加载`;
  $('dPlug').innerHTML = `${plugStat.loaded} <small>/ ${plugStat.total}</small>`;
  const grid = $('plugGrid');
  grid.innerHTML = '';
  list.forEach(p => {
    const card = document.createElement('div');
    card.className = 'pcard' + (p.loaded ? '' : ' off');
    const icon = `<div class="icon">${plugEsc((p.name || '?').slice(0, 1).toUpperCase())}</div>`;
    // 来源徽标：ai = AI 生成（带审计），manual = pb_build 手工生成；handmade（内置/手工放入/zip）不显示
    const originBadge = p.source === 'ai'
      ? `<span class="tag ai" title="AI 生成${p.model ? ' · ' + plugEsc(p.model) : ''}">AI 生成</span>`
      : p.source === 'manual'
        ? `<span class="tag manual" title="手工生成">手写</span>`
        : '';
    const tags = originBadge + (p.loaded
      ? `<span class="tag">已加载</span>${p.autoLoad ? '' : '<span class="tag noauto">自启关</span>'}`
      : `<span class="tag off">未加载</span>`);
    // 操作只留两个图标（重载 / 设置）+ 底部一个状态动作（加载 / 卸载）；
    // 未加载时不显示重载，卸载只从内存移除、插件文件保留
    card.innerHTML = `
      <div class="head">${icon}
        <div class="meta"><div class="nm">${plugEsc(p.name)} ${p.version ? `<span class="ver">v${plugEsc(p.version)}</span>` : ''}</div>
        <div class="ver">${tags}</div></div>
        <div class="acts">
          ${p.loaded ? `<button class="ico" data-op="reload" data-name="${plugEsc(p.name)}" title="重载" aria-label="重载">${PLUG_ICONS.reload}</button>` : ''}
          <button class="ico" data-set="${plugEsc(p.name)}" title="设置" aria-label="设置">${PLUG_ICONS.gear}</button>
        </div>
      </div>
      <div class="desc" title="${plugEsc(p.desc)}">${plugEsc(p.desc)}</div>
      <div class="foot">
        <label class="switch" title="启动时自动加载"><input type="checkbox" data-auto="${plugEsc(p.name)}" ${p.autoLoad ? 'checked' : ''}><span class="track"><span class="thumb"></span></span>自启</label>
        <span class="grow"></span>
        ${p.loaded
          ? `<button class="state stop" data-op="unload" data-name="${plugEsc(p.name)}" >卸载</button>`
          : `<button class="state" data-op="load" data-name="${plugEsc(p.name)}" >加载</button>`}
      </div>`;
    grid.appendChild(card);
  });
  grid.querySelectorAll('button[data-op]').forEach(btn => {
    btn.onclick = async () => {
      btn.disabled = true;
      const { op, name } = btn.dataset;
      const verb = op === 'reload' ? '重载' : op === 'load' ? '加载' : '卸载';
      toast(`${verb} ${name} 中…`);
      try {
        const d = await (await fetch(`/api/plugins/${name}/${op}`, { method: 'POST' })).json();
        d.ok ? toast(`${name} 已${verb}`) : toast(`${verb} ${name} 失败，详见日志`);
      } catch (e) { toast('请求失败: ' + e); }
      pollPlugins();
    };
  });
  grid.querySelectorAll('button[data-set]').forEach(btn => {
    btn.onclick = () => openPSettings(btn.dataset.set);
  });
  grid.querySelectorAll('input[data-auto]').forEach(sw => {
    sw.onchange = async () => {
      const name = sw.dataset.auto;
      const d = await (await fetch('/api/config')).json();
      const autoload = { ...(d.pluginAutoload || {}), [name]: sw.checked };
      const r = await fetch('/api/config', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pluginAutoload: autoload, reloadPlugins: false })
      }).then(r => r.json());
      r.ok ? toast(`${name} 自启已${sw.checked ? '开启' : '关闭'}（下次启动生效）`) : toast('保存失败');
      pollPlugins();
    };
  });
}

$('reloadAll').onclick = async () => {
  toast('重载全部插件中…');
  const list = await (await fetch('/api/plugins')).json();
  for (const p of list.filter(x => x.loaded))
    await fetch(`/api/plugins/${p.name}/reload`, { method: 'POST' });
  toast('全部插件已重载');
  pollPlugins();
};

/* ---------- 刷新（重新搜索插件目录） ---------- */
$('plugRefresh').onclick = async () => {
  const btn = $('plugRefresh');
  btn.disabled = true;
  toast('重新搜索插件目录…');
  try {
    const d = await (await fetch('/api/plugins/rescan', { method: 'POST' })).json();
    lastPlugJson = '';            // 强制重建卡片（否则指纹未变会跳过渲染）
    await pollPlugins();
    toast(`发现 ${d.total} 个插件 · 已加载 ${d.loaded}`);
  } catch (e) {
    toast('刷新失败: ' + e);
  } finally {
    btn.disabled = false;
  }
};

/* ---------- 添加插件（上传 zip） ---------- */
$('plugAdd').onclick = () => $('plugZip').click();

$('plugZip').onchange = async () => {
  const input = $('plugZip');
  const file = input.files && input.files[0];
  input.value = '';               // 置空以便重复选择同一个文件
  if (!file) return;

  const btn = $('plugAdd');
  btn.disabled = true;
  toast(`上传 ${file.name}（${(file.size / 1024).toFixed(0)} KB）…`);
  try {
    const fd = new FormData();
    fd.append('file', file);
    const d = await (await fetch('/api/plugins/upload', { method: 'POST', body: fd })).json();
    if (d.ok) {
      lastPlugJson = '';
      await pollPlugins();
      // 一个 zip 可能含多个插件（如发布用的 FlexBot_BuiltInPlugins.zip）
      const names = (d.installed && d.installed.length) ? d.installed : (d.name ? [d.name] : []);
      const head = names.length > 1 ? `已安装 ${names.length} 个插件：${names.join('、')}` : `已添加 ${d.name}`;
      const warn = d.warnings && d.warnings.length ? `（⚠ ${d.warnings.length} 条提醒）` : '';
      toast(head + warn);
      if (d.warnings && d.warnings.length) console.warn('[plugAdd] 提醒:', d.warnings);
      console.log('[plugAdd]', d.message);
    } else {
      toast(d.error || '添加失败');
    }
  } catch (e) {
    toast('上传失败: ' + e);
  } finally {
    btn.disabled = false;
  }
};


/* ---------- 插件设置弹窗 ---------- */
let psetName = '';
let psetPersonas = null;    // 当前编辑的人格列表（保存设置时随正文一并落盘）
let psetPersonaSync = null; // 人格元数据 → 隐藏 textarea 的回写回调

/* ---------- 设置弹窗：结构化列表控件辅助 ---------- */
function parseJsonArray(v) {
  if (Array.isArray(v)) return v;
  try { const a = JSON.parse(v || '[]'); return Array.isArray(a) ? a : []; } catch { return []; }
}
// 把数组写回隐藏 textarea（随表单一起提交）
function writeHidden(row, key, list) {
  let ta = row.querySelector(`textarea[data-k="${key}"]`);
  if (!ta) {
    ta = document.createElement('textarea');
    ta.dataset.k = key; ta.hidden = true;
    row.appendChild(ta);
  }
  ta.value = JSON.stringify(list);
}
function getFK(k) { return document.querySelector(`#psetForm [data-k="${k}"]`)?.value || ''; }

// 备用模型卡片列表
function renderFbList(container, list, onsync) {
  if (!list.length) { container.innerHTML = '<div class="visionhint">（空：主模型失败时直接返回错误）</div>'; return; }
  container.innerHTML = list.map((f, i) => `
    <div class="fbrow" data-fb-i="${i}" style="display:grid;grid-template-columns:1fr 1fr 1fr auto;gap:8px;align-items:end">
      <div class="tf"><input data-fb-base value="${plugEsc(f.baseUrl || '')}" placeholder=" "><label>Base URL</label></div>
      <div class="tf"><input data-fb-model value="${plugEsc(f.model || '')}" placeholder=" "><label>模型名</label></div>
      <div class="tf"><input data-fb-key type="password" value="${plugEsc(f.apiKey || '')}" placeholder=" " autocomplete="off"><label>API Key（空=主Key）</label></div>
      <button class="btn err-tonal small" type="button" data-fb-del style="height:48px">✕</button>
    </div>`).join('');
  const sync = () => {
    container.querySelectorAll('[data-fb-i]').forEach(el => {
      const i = +el.dataset.fbI;
      list[i].baseUrl = el.querySelector('[data-fb-base]').value;
      list[i].model = el.querySelector('[data-fb-model]').value;
      list[i].apiKey = el.querySelector('[data-fb-key]').value;
    });
    onsync();
  };
  container.querySelectorAll('input').forEach(inp => inp.oninput = sync);
  container.querySelectorAll('[data-fb-del]').forEach(btn => btn.onclick = () => {
    list.splice(+btn.closest('[data-fb-i]').dataset.fbI, 1);
    renderFbList(container, list, onsync); onsync();
  });
}

// 多人格卡片列表
function renderPersonaList(container, list, onsync) {
  if (!list.length) list.push({ name: '默认人格', enabled: true, file: '' });
  psetPersonas = list;
  psetPersonaSync = onsync;
  container.innerHTML = list.map((p, i) => `
    <div class="persona" data-ps-i="${i}" style="border:1px solid var(--outline-var);border-radius:12px;padding:14px">
      <div style="display:flex;align-items:center;gap:10px;margin-bottom:12px">
        <label class="switch"><input type="radio" name="ps-active" ${p.enabled ? 'checked' : ''} data-ps-en="${i}"><span class="track"><span class="thumb"></span></span>启用</label>
        <div class="tf" style="flex:1"><input data-ps-name value="${plugEsc(p.name || '')}" placeholder=" "><label>人格名称</label></div>
        <button class="btn ghost small" type="button" data-ps-del>删除</button>
      </div>
      <div class="tf"><textarea data-ps-md rows="10" placeholder="人格正文（Markdown），随「保存设置」一起写入 personas/<人格名>.md" spellcheck="false"></textarea></div>
      <div class="psetdesc" data-ps-hint>${p.file ? '正文文件：personas/' + esc(p.file) : '尚未保存正文文件'}</div>
    </div>`).join('');
  // 异步拉取各自正文（最少改动：逐个 fetch）
  container.querySelectorAll('[data-ps-i]').forEach(el => {
    const i = +el.dataset.psI;
    const p = list[i];
    const ta = el.querySelector('[data-ps-md]');
    if (p.file) {
      fetch('/api/personas/file?file=' + encodeURIComponent(p.file)).then(r => r.json()).then(d => { ta.value = d.text || ''; }).catch(() => {});
    }
  });
  const syncMeta = () => {
    container.querySelectorAll('[data-ps-i]').forEach(el => {
      const i = +el.dataset.psI;
      list[i].name = el.querySelector('[data-ps-name]').value;
    });
    onsync();
  };
  container.querySelectorAll('[data-ps-name]').forEach(inp => inp.oninput = syncMeta);
  container.querySelectorAll('[data-ps-en]').forEach(radio => radio.onchange = () => {
    list.forEach((p, i) => p.enabled = i === +radio.dataset.psEn);
    renderPersonaList(container, list, onsync); onsync();
  });
  container.querySelectorAll('[data-ps-del]').forEach(btn => btn.onclick = () => {
    if (list.length === 1) return toast('至少保留一个人格。');
    const i = +btn.closest('[data-ps-i]').dataset.psI;
    const p = list[i];
    if (p.file) fetch('/api/personas/file?file=' + encodeURIComponent(p.file), { method: 'DELETE' }).catch(() => {});
    list.splice(i, 1);
    if (!list.some(pp => pp.enabled)) list[0].enabled = true;
    renderPersonaList(container, list, onsync); onsync();
  });
}

// 保存设置前：把每个人格的正文 md 一并落盘（改名时迁移文件并清理旧文件）
async function savePersonaBodies() {
  if (!psetPersonas) return;
  for (const el of document.querySelectorAll('#psetForm [data-ps-i]')) {
    const p = psetPersonas[+el.dataset.psI];
    if (!p) continue;
    const name = el.querySelector('[data-ps-name]').value.trim() || ('人格' + (+el.dataset.psI + 1));
    p.name = name;
    const fname = name.replace(/[\\/:*?"<>|]/g, '_') + '.md';
    const text = el.querySelector('[data-ps-md]').value;
    const r = await fetch('/api/personas/file', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ file: fname, text })
    }).then(r => r.json());
    if (!r.ok) throw new Error(r.error || fname);
    const old = p.file;
    p.file = r.file || fname;
    const hint = el.querySelector('[data-ps-hint]');
    if (hint) hint.textContent = '正文文件：personas/' + p.file;
    if (old && old !== p.file)
      fetch('/api/personas/file?file=' + encodeURIComponent(old), { method: 'DELETE' }).catch(() => {});
  }
  psetPersonaSync?.();
}

function settingVal(def, values) {
  const v = values?.[def.key];
  if (v === undefined || v === null) return def.type === 'bool' ? def.default === 'true' : (def.default ?? '');
  if (typeof v === 'boolean') return v;
  return String(v);
}
async function openPSettings(name) {
  psetName = name;
  psetPersonas = null;
  psetPersonaSync = null;
  $('psetName').textContent = `${name} · 设置`;
  $('psetSub').textContent = '保存后立即热应用';
  let d;
  try {
    d = await (await fetch(`/api/plugins/${name}/settings`)).json();
  } catch (e) { toast('读取设置失败: ' + e); return; }

  const form = $('psetForm');
  form.innerHTML = '';
  const empty = !d.defs || d.defs.length === 0;
  $('psetEmpty').style.display = empty ? 'block' : 'none';
  $('psetSave').style.display = empty ? 'none' : '';
  (d.defs || []).forEach(def => {
    const val = settingVal(def, d.values);
    const row = document.createElement('div');
    row.className = 'psetrow';
    if (def.type === 'bool') {
      row.innerHTML = `
        <div class="psetlbl">${plugEsc(def.label)}</div>
        <div style="display:flex;align-items:center;gap:12px">
          <label class="switch"><input type="checkbox" data-k="${plugEsc(def.key)}" ${val === true || val === 'true' ? 'checked' : ''}><span class="track"><span class="thumb"></span></span></label>
          <span class="psetdesc">${plugEsc(def.description || '')}</span>
        </div>`;
    } else if (def.type === 'select') {
      const opts = (def.options || []).map(o =>
        `<option value="${plugEsc(o)}" ${val === o ? 'selected' : ''}>${plugEsc(o)}</option>`).join('');
      row.innerHTML = `
        <div class="psetlbl">${plugEsc(def.label)}</div>
        <select class="mselect" data-k="${plugEsc(def.key)}">${opts}</select>
        ${def.description ? `<div class="psetdesc">${plugEsc(def.description)}</div>` : ''}`;
    } else if (def.type === 'password') {
      row.innerHTML = `
        <div class="tf"><input data-k="${plugEsc(def.key)}" type="password" value="${plugEsc(val)}" placeholder=" " autocomplete="off"><label>${plugEsc(def.label)}</label></div>
        ${def.description ? `<div class="psetdesc">${plugEsc(def.description)}</div>` : ''}`;
    } else if (def.type === 'models') {
      // 结构化备用模型列表：[{baseUrl,model,apiKey}]，卡片行（三字段+测试+删除）+ 添加
      const list = parseJsonArray(val);
      row.innerHTML = `
        <div class="psetlbl">${plugEsc(def.label)}
          <span class="grow"></span>
          <button class="btn ghost small" type="button" data-fb-add>+ 添加</button>
        </div>
        <div data-fb-list style="display:flex;flex-direction:column;gap:14px"></div>
        ${def.description ? `<div class="psetdesc">${plugEsc(def.description)}</div>` : ''}`;
      renderFbList(row.querySelector('[data-fb-list]'), list, () => writeHidden(row, def.key, list));
      row.querySelector('[data-fb-add]').onclick = () => {
        const main = getFK('ApiKey'), mainBase = getFK('BaseUrl');
        list.push({ baseUrl: mainBase || '', model: '', apiKey: '' });
        renderFbList(row.querySelector('[data-fb-list]'), list, () => writeHidden(row, def.key, list));
        writeHidden(row, def.key, list);
      };
    } else if (def.type === 'personas') {
      // 多人格卡片：[{name,enabled,instructions}]，启用单选、增删改
      const list = parseJsonArray(val);
      row.innerHTML = `
        <div class="psetlbl">${plugEsc(def.label)}
          <span class="grow"></span>
          <button class="btn ghost small" type="button" data-ps-add>+ 添加人格</button>
        </div>
        <div data-ps-list style="display:flex;flex-direction:column;gap:16px"></div>
        ${def.description ? `<div class="psetdesc">${plugEsc(def.description)}</div>` : ''}`;
      renderPersonaList(row.querySelector('[data-ps-list]'), list, () => writeHidden(row, def.key, list));
      row.querySelector('[data-ps-add]').onclick = () => {
        list.push({ name: `新人格 ${list.length + 1}`, enabled: false, file: '' });
        renderPersonaList(row.querySelector('[data-ps-list]'), list, () => writeHidden(row, def.key, list));
        writeHidden(row, def.key, list);
      };
    } else {
      row.innerHTML = `
        <div style="display:flex;gap:10px;align-items:flex-end">
          <div class="tf" style="flex:1"><input data-k="${plugEsc(def.key)}" type="${def.type === 'number' ? 'number' : 'text'}" value="${plugEsc(val)}" placeholder=" "><label>${plugEsc(def.label)}</label></div>
        </div>
        ${def.description ? `<div class="psetdesc">${plugEsc(def.description)}</div>` : ''}`;
    }
    form.appendChild(row);
  });
  $('psetMsg').textContent = '';
  $('psetOverlay').classList.remove('hidden');
}
$('psetClose').onclick = $('psetCancel').onclick = () => $('psetOverlay').classList.add('hidden');
$('psetOverlay').addEventListener('click', e => { if (e.target === e.currentTarget) e.currentTarget.classList.add('hidden'); });
$('psetSave').onclick = async () => {
  const btn = $('psetSave'); btn.disabled = true;
  $('psetMsg').textContent = '保存中…';
  try {
    await savePersonaBodies();
  } catch (e) {
    $('psetMsg').textContent = '';
    btn.disabled = false;
    toast('人格正文保存失败: ' + (e.message || e));
    return;
  }
  const payload = {};
  document.querySelectorAll('#psetForm [data-k]').forEach(el => {
    payload[el.dataset.k] = el.type === 'checkbox' ? el.checked
      : el.type === 'number' ? (parseFloat(el.value) || 0)
      : el.value;
  });
  try {
    const r = await fetch(`/api/plugins/${psetName}/settings`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).then(r => r.json());
    if (r.ok) {
      toast(`${psetName} 设置已保存${r.reloaded ? '并热应用' : ''}`);
      $('psetOverlay').classList.add('hidden');
    } else {
      $('psetMsg').textContent = '';
      toast('保存失败: ' + (r.error || ''));
    }
  } catch (e) { $('psetMsg').textContent = ''; toast('请求失败: ' + e); }
  finally { btn.disabled = false; }
};

