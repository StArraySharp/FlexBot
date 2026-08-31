const $ = id => document.getElementById(id);
let lastId = 0;


/* ---------- 页面切换 ---------- */
const PAGE_META = {
  dash: ['概览', '运行状态一览'], plugins: ['插件', '加载 / 卸载 / 热重载'],
  config: ['配置', '连接 · 模型 · 提示词'], logs: ['日志', '实时事件流'],
};
document.querySelectorAll('.navitem').forEach(item => {
  item.onclick = () => switchPage(item.dataset.page);
});
function switchPage(p) {
  document.querySelectorAll('.navitem').forEach(i => i.classList.toggle('active', i.dataset.page === p));
  document.querySelectorAll('.page').forEach(s => s.classList.toggle('show', s.id === 'page-' + p));
  $('pageTitle').textContent = PAGE_META[p][0];
  $('pageSub').textContent = PAGE_META[p][1];
  if (p === 'config') {
    $('page-config').scrollTop = 0;
    loadConfig();
  }
}
$('gotoLogs').onclick = e => { e.preventDefault(); switchPage('logs'); };


/* ---------- Snackbar ---------- */
let toastTimer;
function toast(msg) {
  $('toast').textContent = msg;
  $('toast').classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => $('toast').classList.remove('show'), 2600);
}

