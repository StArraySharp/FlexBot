/* ---------- 退出登录 ---------- */
$('navLogout').onclick = async () => {
  await fetch('/api/auth/logout', { method: 'POST' }).catch(() => {});
  location.href = '/login';
};


/* ---------- 401 全局拦截：登出/过期自动回登录页 ---------- */
const _fetch = window.fetch;
window.fetch = async (...args) => {
  const resp = await _fetch(...args);
  if (resp.status === 401 && !String(args[0]).includes('/api/auth'))
    location.href = '/login';
  return resp;
};

