/* ---------- 日志 ---------- */
// ---- ANSI 颜色：把子进程日志里的 \x1b[32m…\x1b[0m 解析成彩色 span ----
// 先转义 HTML 再拼标签，日志里出现的 < & 不会破坏结构；光标移动等非 SGR 序列直接丢弃。
const ANSI_FG = {
  30: '#8b949e', 31: '#ff7b72', 32: '#7ee787', 33: '#d29922', 34: '#79c0ff',
  35: '#d2a8ff', 36: '#56d4dd', 37: '#c9d1d9',
  90: '#6e7681', 91: '#ffa198', 92: '#aff5b4', 93: '#e3b341', 94: '#a5d6ff',
  95: '#d2a8ff', 96: '#76e3ea', 97: '#f0f6fc'
};
function escHtml(s) {
  return s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
}
function ansiToHtml(text) {
  const re = /\u001b\[([0-9;]*)m|\u001b\[[0-9;?]*[ -\/]*[@-~]/g;
  let out = '', last = 0, open = false, m;
  while ((m = re.exec(text)) !== null) {
    out += escHtml(text.slice(last, m.index));
    last = re.lastIndex;
    if (!m[0].endsWith('m')) continue;            // 非 SGR：丢弃
    const codes = (m[1] || '').split(';').filter(x => x !== '');
    let color = null, bold = false, reset = codes.length === 0;
    for (const c of codes) {
      const n = parseInt(c, 10);
      if (n === 0) reset = true;
      else if (n === 1) bold = true;
      else if (ANSI_FG[n]) color = ANSI_FG[n];
    }
    if (open) { out += '</span>'; open = false; }
    if (!reset && (color || bold)) {
      const style = (color ? 'color:' + color + ';' : '') + (bold ? 'font-weight:600;' : '');
      out += '<span style="' + style + '">';
      open = true;
    }
  }
  out += escHtml(text.slice(last));
  return out + (open ? '</span>' : '');
}
function classify(t) {
  if (/\[error\]|\[异常|失败|FAILED|Exception/.test(t)) return 'err';
  if (/\[warn\]|\[!\]/.test(t)) return 'warn';
  return '';
}
function applyFilter(el, t) {
  const f = $('logFilter').value.trim();
  el.classList.toggle('hid', f.length > 0 && !t.includes(f));
}
function appendLine(t, mini) {
  if (mini) {
    const box = $('miniLog');
    const d = document.createElement('div');
    d.className = 'l ' + classify(t);
    d.innerHTML = ansiToHtml(t);
    box.appendChild(d);
    while (box.children.length > 10) box.removeChild(box.firstChild);
    return;
  }
  const div = document.createElement('div');
  div.className = 'l ' + classify(t);
  div.innerHTML = ansiToHtml(t);
  applyFilter(div, t);
  const log = $('log');
  log.appendChild(div);
  while (log.children.length > 3000) log.removeChild(log.firstChild);
  $('logCount').textContent = log.children.length + ' 行';
  if ($('autoscroll').checked && !$('pauseLog').checked) log.scrollTop = log.scrollHeight;
}
let logLines = [];
async function pollLogs() {
  try {
    const d = await (await fetch(`/api/logs?after=${lastId}`)).json();
    if (d.lines.length && !$('pauseLog').checked) {
      d.lines.forEach(l => { logLines.push(l.t); appendLine(l.t); });
      if (logLines.length > 20) logLines = logLines.slice(-10);
    }
    lastId = d.next;
    if ($('page-dash').classList.contains('show') && d.lines.length && !$('pauseLog').checked)
      d.lines.slice(-3).forEach(l => appendLine(l.t, true));
  } catch {}
}
$('logFilter').addEventListener('input', () => {
  const f = $('logFilter').value.trim();
  document.querySelectorAll('#log .l').forEach(el =>
    el.classList.toggle('hid', f.length > 0 && !el.textContent.includes(f)));
});
$('clearLog').onclick = async () => {
  await fetch('/api/logs/clear', { method: 'POST' });
  $('log').innerHTML = ''; lastId = 0; logLines = [];
  $('logCount').textContent = '0 行'; toast('日志已清空');
};

