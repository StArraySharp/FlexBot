/* ---------- 历史消息（数据层 P1，只读） ---------- */
/* 只展示：库体积与条数、会话清单、某会话最近 100 条。检索（FTS5）属于 P2。 */
/*
   设计原则（踩过坑）：绝不静默留白。
   - 每次请求都写一行诊断：URL → HTTP 状态 → 条数（或错误原因）
   - 字段缺失也照实显示（兜底 JSON），这样"空"只可能发生在真的没数据时
*/

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
function histStatus(text) {
  const el = $('histStatus');
  if (el) el.textContent = text;
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
  } catch (e) { histStatus('统计读取异常：' + e); }
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
  } catch (e) { histStatus('会话清单读取异常：' + e); }
}
async function loadHistoryRecent() {
  const scope = $('histScope').value;
  const box = $('histList');
  box.innerHTML = '';
  if (!scope) {
    box.textContent = '没有可选的会话（说明库里还没有关注群或私聊的消息）';
    histStatus('无 scope');
    return;
  }
  const url = '/api/history/recent?scope=' + encodeURIComponent(scope) + '&limit=100';
  let res, list;
  try {
    res = await fetch(url);
  } catch (e) {
    box.textContent = '请求失败：' + e;
    histStatus('网络异常 ' + url);
    return;
  }
  if (!res.ok) {
    box.textContent = '读取失败：HTTP ' + res.status;
    histStatus('HTTP ' + res.status + ' ' + url);
    return;
  }
  try {
    list = await res.json();
  } catch (e) {
    const raw = await res.text().catch(() => '');
    box.textContent = '返回不是 JSON：' + raw.slice(0, 300);
    histStatus('非 JSON（HTTP ' + res.status + '）');
    return;
  }
  if (!Array.isArray(list)) {
    box.textContent = '返回格式异常（不是数组）：' + JSON.stringify(list).slice(0, 300);
    histStatus('非数组（HTTP ' + res.status + '）');
    return;
  }
  histStatus('HTTP ' + res.status + ' · ' + list.length + ' 条 · ' + scope);
  if (!list.length) { box.textContent = '该会话暂无消息'; return; }
  for (const m of list) {
    const row = document.createElement('div');
    row.style.borderBottom = '1px solid rgba(128,128,128,.18)';
    row.style.padding = '3px 0';
    // 字段名兜底：万一后端改了大小写或结构，也要把内容显示出来，而不是留白
    const ts = m.ts ?? m.Ts ?? '?';
    const text = m.text ?? m.Text ?? JSON.stringify(m);
    const who = (m.dir ?? m.Dir) === 'out' ? '→ 机器人'
              : '← ' + (m.nick ?? m.Nick ?? '?') + '(' + (m.userId ?? m.UserId ?? '?') + ')';
    row.textContent = ts + '  ' + who + '  ' + ((m.recalled ?? m.Recalled) ? '[已撤回] ' : '') + text;
    box.appendChild(row);
  }
  box.scrollTop = box.scrollHeight;
}
$('histRefresh').onclick = () => loadHistory();
$('histScope').onchange = () => loadHistoryRecent();
