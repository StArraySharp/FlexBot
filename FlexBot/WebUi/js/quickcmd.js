/* ---------- 快捷命令 ---------- */
async function runCmd(text) {
  text = (text || '').trim();
  if (!text) return;
  const out = $('dCmdOut');
  out.style.display = 'block';
  out.textContent += '> ' + text + '\n';
  try {
    const d = await fetch('/api/command', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text })
    }).then(r => r.json());
    out.textContent += (d.message || '(无输出)') + '\n\n';
  } catch (e) { out.textContent += '请求失败: ' + e + '\n\n'; }
  out.scrollTop = out.scrollHeight;
}
$('dSend').onclick = () => { runCmd($('dCmd').value); $('dCmd').value = ''; };
$('dCmd').addEventListener('keydown', e => { if (e.key === 'Enter') { runCmd($('dCmd').value); $('dCmd').value = ''; } });
document.querySelectorAll('[data-cmd]').forEach(b => b.onclick = () => runCmd(b.dataset.cmd));

