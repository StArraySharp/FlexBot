/* ---------- 状态 ---------- */
function fmtUp(s) {
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60);
  return h > 0 ? `${h} 小时 ${m} 分` : m > 0 ? `${m} 分 ${s % 60} 秒` : `${s} 秒`;
}
let plugStat = { loaded: 0, total: 0 };
async function pollStatus() {
  try {
    const d = await (await fetch('/api/status')).json();
    const chip = $('stChip');
    chip.classList.toggle('on', d.connected);
    $('stChipTxt').textContent = d.connected ? '已连接' : '已断开';
    $('dConn').innerHTML = d.connected ? '<small style="color:var(--success)">● 在线</small>' : '<small style="color:var(--error)">● 离线重试中</small>';
    const official = d.transport === 'official';
    $('dWs').textContent = official ? 'QQ 官方机器人开放平台' : d.wsUrl;
    $('dTrans').textContent = official ? '官方 API' : 'OneBot 11';
    $('dSelf').textContent = d.selfId || '未登录';
    $('dStart').textContent = '启动于 ' + d.startedAt;
    $('dUp').textContent = fmtUp(d.uptimeSec);
    $('dPlug').innerHTML = `${plugStat.loaded} <small>/ ${plugStat.total}</small>`;
  } catch {}
}

