/* ---------- 自定义背景 + 透明度（服务端配置） ---------- */
(async function initThemeCfg() {
  try {
    const r = await (await fetch('/api/theme-cfg')).json();
    document.documentElement.style.setProperty('--bg-opacity', (r.bgOpacity ?? 45) / 100);
    document.documentElement.style.setProperty('--ui-opacity', (r.uiOpacity ?? 100) / 100);
    document.documentElement.style.setProperty('--ui-blur', ((r.uiOpacity ?? 100) * 0.1).toFixed(2) + 'px');
    if (r.hasBackground)
      document.documentElement.style.setProperty('--custom-bg', 'url(/background?' + Date.now() + ')');
  } catch {}
})();


/* ---------- 主题切换（跟随系统 → 浅色 → 深色 循环；持久化 localStorage） ---------- */
const THEME_KEY = 'qqbot_theme';
function applyTheme(mode) {
  // mode: null/'' = 跟随系统；'light' / 'dark' 手动
  if (mode === 'light' || mode === 'dark') document.documentElement.dataset.theme = mode;
  else delete document.documentElement.dataset.theme;
  localStorage.setItem(THEME_KEY, mode || '');
  const txt = mode === 'light' ? '浅色' : mode === 'dark' ? '深色' : '主题·系统';
  const el = document.getElementById('themeTxt');
  if (el) el.textContent = txt;
}
(function initTheme() {
  applyTheme(localStorage.getItem(THEME_KEY) || '');
})();
const themeBtn = document.getElementById('navTheme');
if (themeBtn) themeBtn.onclick = () => {
  const cur = localStorage.getItem(THEME_KEY) || '';
  const next = cur === '' ? 'light' : cur === 'light' ? 'dark' : '';
  applyTheme(next);
  toast(next === '' ? '主题：跟随系统' : next === 'light' ? '主题：浅色' : '主题：深色');
};


/* ---------- 外观：背景上传 + 透明度 ---------- */
(function initAppearance() {
  const uiSlider = document.getElementById('uiOpacity');
  const ctlSlider = document.getElementById('ctlOpacity');
  const bgSlider = document.getElementById('bgOpacity');
  if (!uiSlider) return;
  const apply = (ui, ctl, bg) => {
    document.documentElement.style.setProperty('--ui-opacity', ui / 100);
    document.documentElement.style.setProperty('--ui-blur', ((ui) * 0.1).toFixed(2) + 'px');
    document.documentElement.style.setProperty('--ctl-opacity', ctl / 100);
    document.documentElement.style.setProperty('--bg-opacity', bg / 100);
    document.getElementById('uiOpVal').textContent = ui + '%';
    document.getElementById('ctlOpVal').textContent = ctl + '%';
    document.getElementById('bgOpVal').textContent = bg + '%';
  };
  // 初始值
  fetch('/api/theme-cfg').then(r => r.json()).then(c => {
    uiSlider.value = c.uiOpacity; ctlSlider.value = c.ctlOpacity ?? 100; bgSlider.value = c.bgOpacity;
    apply(c.uiOpacity, c.ctlOpacity ?? 100, c.bgOpacity);
  }).catch(() => {});
  {/* 滑动即时预览；松手才保存 */
  let saveTimer;
  const save = () => {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => {
      fetch('/api/theme-cfg', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ uiOpacity: +uiSlider.value, ctlOpacity: +ctlSlider.value, bgOpacity: +bgSlider.value })
      });
    }, 400);
  };
  const onSlide = () => { apply(+uiSlider.value, +ctlSlider.value, +bgSlider.value); save(); };
  uiSlider.oninput = onSlide; ctlSlider.oninput = onSlide; bgSlider.oninput = onSlide; }
  const msg = document.getElementById('bgMsg');
  document.getElementById('bgUpload').onclick = () => document.getElementById('bgFile').click();
  document.getElementById('bgFile').onchange = async e => {
    const f = e.target.files[0];
    if (!f) return;
    msg.textContent = '上传中…';
    const fd = new FormData(); fd.append('file', f);
    try {
      const r = await fetch('/background', { method: 'POST', body: fd }).then(r => r.json());
      msg.textContent = r.ok ? '✓ 已设置' : (r.error || '失败');
      if (r.ok) {
        document.documentElement.style.setProperty('--custom-bg', 'url(/background?' + Date.now() + ')');
        document.getElementById('bgRemove').style.display = '';
      }
    } catch (ex) { msg.textContent = '请求失败: ' + ex; }
    setTimeout(() => msg.textContent = '', 3000);
    e.target.value = '';
  };
  document.getElementById('bgRemove').onclick = async () => {
    const r = await fetch('/background', { method: 'DELETE' }).then(r => r.json());
    msg.textContent = r.ok ? '已移除' : (r.error || '');
    if (r.ok) document.documentElement.style.setProperty('--custom-bg', 'none');
    setTimeout(() => msg.textContent = '', 3000);
  };
})();

