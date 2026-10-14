/* ---------- 历史消息（数据层 P1，只读） ---------- */
/* 只展示：库体积与条数、会话清单、某会话最近 100 条。检索（FTS5）属于 P2。 */

function fmtBytes(n) {
  if (!n) return '0 B';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
  return (n / 1024 / 1024).toFixed(1) + ' MB';
}
function scopeLabel(scope) {
  return scope.startsWith('group:') ? '群 ' + scope.slice(6)
       : scope.startsWith('private:') ? '私聊 ' + scope.slice(8)
       : scope;
}
async function loadHistory() {
  await loadHistoryStats();
  await loadHistoryScopes();
  await loadHistoryRecent();
}
async function loadHistoryStats() {
  try {
    const d = await (await fetch('/api/history/stats')).json();
    if (!d.enabled) { $('histTotal').textContent = '未启用'; return; }
    $('histTotal').textContent = d.total + ' 条' + (d.queued ? '（待入库 ' + d.queued + '）' : '');
    $('histGroup').textContent = '群 ' + d.group + ' · 保留 ' + d.groupKeepDays + ' 天';
    $('histPrivate').textContent = '私聊 ' + d.privateMsgs + ' · 保留 ' + d.privateKeepDays + ' 天';
    $('histBytes').textContent = fmtBytes(d.bytes);
    $('histRange').textContent = d.oldest + ' ~ ' + d.newest;
  } catch { }
}
async function loadHistoryScopes() {
  const sel = $('histScope');
  const cur = sel.value;
  try {
    const list = await (await fetch('/api/history/scopes')).json();
    sel.innerHTML = '';
    for (const s of list) {
      const o = document.createElement('option');
      o.value = s.scope;
      o.textContent = scopeLabel(s.scope) + ' · ' + s.count + ' 条 · 最后 ' + s.last;
      sel.appendChild(o);
    }
    if (cur && list.some(s => s.scope === cur)) sel.value = cur;
  } catch { }
}
async function loadHistoryRecent() {
  const scope = $('histScope').value;
  const box = $('histList');
  box.innerHTML = '';
  if (!scope) { box.textContent = '暂无可显示的消息（只存关注群与私聊）'; return; }
  try {
    const list = await (await fetch('/api/history/recent?scope=' + encodeURIComponent(scope) + '&limit=100')).json();
    if (!list.length) { box.textContent = '该会话暂无消息'; return; }
    for (const m of list) {
      const row = document.createElement('div');
      row.style.borderBottom = '1px solid rgba(128,128,128,.18)';
      row.style.padding = '2px 0';
      row.textContent = `${m.ts}  ${m.nick}(${m.userId})  ${m.recalled ? '[已撤回] ' : ''}${m.text}`;
      box.appendChild(row);
    }
    box.scrollTop = box.scrollHeight;
  } catch { }
}
$('histRefresh').onclick = () => loadHistory();
$('histScope').onchange = () => loadHistoryRecent();
