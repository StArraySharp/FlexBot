/* ---------- 配置（核心项：连接/管理员/数据目录；模型人格前缀在插件设置里） ---------- */
const v = id => $(id).value.trim();
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

function parseAdmins() {
  return [...new Set($('cfgAdmins').value.split(/[\s,，]+/).map(x => Number(x)).filter(Number.isSafeInteger).filter(x => x > 0))];
}

async function loadConfig() {
  try {
    const c = await (await fetch('/api/config')).json();
    $('cfgWs').value = c.wsUrl; $('cfgToken').value = c.token;
    $('cfgOwner').value = c.ownerUin || '';
    $('cfgAdmins').value = (c.adminUins || []).join('\n'); $('cfgMem').value = c.memoryDir;
    $('cfgReload').checked = !!c.reloadPluginsAfterSave;
    $('cfgNapCatCmd').value = c.napCatCmd || '';
    $('cfgLLBotCmd').value = c.llBotCmd || '';
    $('cfgLogDir').value = c.logDir || '';
    $('cfgIsolateLogs').checked = c.isolateDependencyLogs !== false;
    $('cfgWebUiPort').value = c.webUiPort || '';
    $('cfgBindAll').checked = !!c.webUiBindAll;
    $('cfgWebUiPwd').value = ''; // 密码不回显（服务端只存 SHA256）
    // 注意：不设 placeholder——浮动 label 会与 placeholder 文字重叠；说明在下方 desc 行
    $('cfgWebUiPwd').placeholder = ' ';
  } catch (e) { toast('读取配置失败: ' + e); }
}

// 配置保存消息 2 秒后自动隐藏
let cfgMsgTimer;
function scheduleCfgMsgClear() {
  clearTimeout(cfgMsgTimer);
  cfgMsgTimer = setTimeout(() => { $('cfgMsg').textContent = ''; }, 2000);
}

$('cfgSave').onclick = async () => {
  const btn = $('cfgSave'); btn.disabled = true;
  $('cfgMsg').textContent = '保存中…';
  try {
    const ownerUin = parseInt($('cfgOwner').value) || 0;
    if (ownerUin <= 0) { toast('必须填写机器人主人 QQ。'); return; }
    const adminUins = parseAdmins();
    const d = await (await fetch('/api/config')).json();
    const webUiPort = parseInt($('cfgWebUiPort').value) || 0;
    if (webUiPort < 0 || webUiPort > 65535) { toast('WebUI 端口必须在 0-65535 之间。'); return; }
    const payload = {
      wsUrl: v('cfgWs'), token: $('cfgToken').value,
      ownerUin, adminUins, memoryDir: v('cfgMem'),
      reloadPluginsAfterSave: $('cfgReload').checked,
      napCatCmd: v('cfgNapCatCmd'),
      llBotCmd: v('cfgLLBotCmd'),
      logDir: v('cfgLogDir'),
      isolateDependencyLogs: $('cfgIsolateLogs').checked,
      webUiPort: webUiPort,
      webUiBindAll: $('cfgBindAll').checked,
      ...($('cfgWebUiPwd').value.length > 0 ? { webUiPassword: $('cfgWebUiPwd').value } : {}),
      pluginAutoload: d.pluginAutoload || {},
      reloadPlugins: $('cfgReload').checked
    };
    const r = await fetch('/api/config', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    }).then(r => r.json());
    if (!r.ok) {
      $('cfgMsg').textContent = '保存失败'; toast('保存失败: ' + (r.error || ''));
      scheduleCfgMsgClear();
      return;
    }
    let msg = '已保存';
    if (r.reloaded && r.reloaded.length) msg += `，重载: ${r.reloaded.join(', ')}`;
    if (r.wsChanged) msg += '，正在用新地址重连';
    $('cfgMsg').textContent = msg; toast(msg);
    scheduleCfgMsgClear();
    pollPlugins();
    // 保存后未连接 → 自动触发启动+连接
    if (!r.wsChanged) {
      const s = await (await fetch('/api/status')).json();
      if (!s.connected) startAndConnect();
    }
  } catch (e) { $('cfgMsg').textContent = ''; toast('请求失败: ' + e); }
  finally { btn.disabled = false; }
};

/* ---------- 立即启动并连接 ----------
   两个命令都配置且都未运行 → prompt 让用户选择；单个 → 直接启动；都已在跑 → 仅连接 */
async function startAndConnect() {
  const msg = $('connectMsg'), btn = $('btnConnectNow');
  btn.disabled = true;
  msg.textContent = '检查依赖状态…';
  try {
    const d = await (await fetch('/api/deps')).json();
    let start = null;
    const needNap = d.napcatConfigured && !d.napcatRunning;
    const needLl = d.llbotConfigured && !d.llbotRunning;
    if (needNap && needLl) {
      const choice = prompt(
        'NapCat 与 LLBot 都已配置且未运行，启动哪个？\n\n1 = 只启动 NapCat\n2 = 只启动 LLBot\n3 = 都启动\n（取消 = 不启动，仅连接）', '1');
      if (choice === '1') start = 'napcat';
      else if (choice === '2') start = 'llbot';
      else if (choice === '3') start = 'both';
    } else if (needNap) start = 'napcat';
    else if (needLl) start = 'llbot';

    msg.textContent = start ? `启动 ${start === 'both' ? 'NapCat+LLBot' : start} 并连接…` : '连接中…';
    const send = async which => (await fetch('/api/start-connect', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ start: which })
    })).json();
    let r;
    if (start === 'both') { await send('napcat'); r = await send('llbot'); }
    else r = await send(start);
    if (r && r.message) msg.textContent = r.message;
    // 轮询至连接成功或超时（25s，启动进程就绪需要时间）
    for (let i = 0; i < 50; i++) {
      await new Promise(r2 => setTimeout(r2, 500));
      const s = await (await fetch('/api/status')).json();
      if (s.connected) { msg.textContent = '✓ 已连接'; break; }
      if (i === 49) msg.textContent = '仍未能连接（进程未就绪？看门狗会继续重试）';
    }
  } catch (e) { msg.textContent = '请求失败: ' + e; }
  finally { btn.disabled = false; setTimeout(() => msg.textContent = '', 5000); }
}

$('btnConnectNow').onclick = startAndConnect;

