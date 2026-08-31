/* ---------- 日志 ---------- */
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
    d.textContent = t;
    box.appendChild(d);
    while (box.children.length > 10) box.removeChild(box.firstChild);
    return;
  }
  const div = document.createElement('div');
  div.className = 'l ' + classify(t);
  div.textContent = t;
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

