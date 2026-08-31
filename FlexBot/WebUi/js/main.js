/* ---------- 轮询 ---------- */
pollLogs(); pollStatus(); pollPlugins();
setInterval(pollLogs, 1000);
setInterval(pollStatus, 1000);
setInterval(pollPlugins, 6000);

