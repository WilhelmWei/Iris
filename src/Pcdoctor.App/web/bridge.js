/* 界面和桌面程序之间的桥。
   在桌面程序里：通过 WebView2 的 postMessage 发 {id, method, params}，桌面程序回 {id, result} 或 {id, error}；
   桌面程序主动推送的事件是 {event, data}（体检进度、操作进度）。
   在普通浏览器里打开（开发、截图、设计稿）时，用下面的模拟实现：数据来自 demo/demo.json（虚构的一台电脑）；
   地址后面加 ?real 时读 dev/sample.json（作者本机的真实报告，不进仓库）。 */
(function () {
  const host = window.chrome && window.chrome.webview;
  const pending = new Map();
  const listeners = {};
  let seq = 0;

  function emit(event, data) { (listeners[event] || []).forEach(fn => { try { fn(data); } catch (e) { console.error(e); } }); }

  if (host) {
    host.addEventListener('message', e => {
      const m = e.data;
      if (m && m.id && pending.has(m.id)) {
        const p = pending.get(m.id); pending.delete(m.id);
        if (m.error) p.reject(new Error(m.error)); else p.resolve(m.result);
      } else if (m && m.event) emit(m.event, m.data);
    });
  }

  window.api = {
    isHost: !!host,
    call(method, params) {
      if (!host) return mock(method, params || {});
      return new Promise((resolve, reject) => {
        const id = ++seq;
        pending.set(id, { resolve, reject });
        host.postMessage({ id, method, params: params || {} });
      });
    },
    on(event, fn) { (listeners[event] = listeners[event] || []).push(fn); },
  };
  if (host) return;

  // ---------------------------------------------------------------- 模拟实现（浏览器预览）
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const M = {
    settings: { mode: 'ai', provider: 'deepseek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat', hasKey: true, keyTail: '7f3a',
                allowHosted: false, hostedAvailable: false, theme: 'dark', reduceMotion: false, defaultScan: false, demoMode: false, pro: false,
                showLessons: true, lessonIndex: 0, showTips: true, onboarded: /[?&]onboarded\b/.test(location.search), betaConsent: null, feedbackServer: true },
    report: null, journal: [], answers: {}, privacyAccepted: true,
  };
  const EXEC = ['startup.disable', 'task.disable', 'task.delete_leftover', 'browser.disable_background', 'cache.clean', 'defender.scan',
                'service.set_manual', 'service.delete_orphan', 'app.uninstall', 'driver.delete_old_version', 'power.disable_fast_startup'];

  async function load() {
    if (M.report) return M.report;
    let d = window.__DEMO__;
    if (!d) {
      const real = /[?&]real\b/.test(location.search);
      try { const r = await fetch(real ? 'dev/sample.json' : 'demo/demo.json'); d = await r.json(); } catch (e) { d = null; }
      if (d && !d.result) d = { result: d, extra: {} };  // dev/sample.json 是研判结果本身
    }
    if (!d) return null;
    const x = d.extra || {};
    M.report = {
      id: 'demo', result: d.result, extra: x,
      meta: { source: 'local', reportTime: '2026-10-25 08:30', machine: 'LAPTOP-7Q2K · Windows 11 24H2', timings: x.timings || [] },
      altOps: { F021: { op: 'service.set_manual', name: '把 TeamViewer 服务改为手动启动', risk: 'yellow' } },
      crossCheck: x.crossCheck || {}, unknownObjects: x.unknownObjects || 0, changes: x.changes || [], history: x.history || [],
    };
    return M.report;
  }

  /** 浏览器里没有 Windows 的原生确认框，这里画一个样子相近的，说明桌面版是系统对话框 */
  function nativeConfirm(heading, items) {
    return new Promise(resolve => {
      const box = document.createElement('div');
      box.className = 'modal-back';
      box.innerHTML = `<div class="win-dlg" role="alertdialog" aria-modal="true">
        <div class="wt">Iris · 确认修改　<span class="tag">（预览里的模拟；桌面版是 Windows 原生对话框）</span></div>
        <div class="wb"><b>${heading}</b><ul>${items.map(i => `<li>${i}</li>`).join('')}</ul></div>
        <div class="wf"><button data-r="1">确认</button><button class="def" data-r="0">取消</button></div></div>`;
      document.body.appendChild(box);
      box.querySelector('.def').focus();
      box.addEventListener('click', e => { const b = e.target.closest('[data-r]'); if (!b) return; box.remove(); resolve(b.dataset.r === '1'); });
    });
  }
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  async function mock(method, p) {
    await sleep(90);
    switch (method) {
      case 'getState': {
        const r = await load();
        const h = r ? r.history : [];
        return { version: '1.0.0-rc.1', isAdmin: false, settings: Object.assign({}, M.settings), executableOps: EXEC, guideOps: ['defender.review'],
                 privacy: { version: '2026-10-27.1', accepted: M.privacyAccepted },
                 lastReport: r ? { id: r.id, score: r.result.score.total, headline: r.result.judgement.headline, time: r.meta.reportTime,
                                   delta: h.length > 1 ? h[h.length - 1][1] - h[h.length - 2][1] : null } : null,
                 history: h, changes: r ? r.changes : [], update: M.update || null, outbox: 0,
                 pending: (M.selected || []).length, handled: (() => { const n = Object.values(handled()).filter(x => x.status === 'done').length; return n ? { count: n, time: '今天' } : null; })(),
                 handledBefore: /[?&]handledBefore\b/.test(location.search) ? { total: 3, still: 1 } : null };
      }
      case 'getReport': { const r = await load(); await sleep(350); return Object.assign({}, r, { answers: M.answers, marks: M.marks || {}, dismissed: [...(M.dismissed || [])], sent: M.sent || null, canExecute: !/[?&]friend\b/.test(location.search),
        handled: handled(), selected: { keys: (M.selected || []).slice(), carried: M.carried || 0, dropped: M.carried ? 1 : 0 },
        defender: r.defender || { scanned: !!M.scanned && !/[?&]scanfail/.test(location.search), scanFailed: /[?&]scanfail/.test(location.search), realTime: true, quickScanAgeDays: M.scanned ? 0 : 12, fullScanAgeDays: 417, signatureAgeDays: 0 } }); }
      case 'storageInfo': await sleep(200); return M.storage = M.storage || { reports: { count: 9, local: 8, bytes: 14.2 * 1048576 }, backups: { count: 6, bytes: 412 * 1024, admin: 2 }, webview: { bytes: 38.5 * 1048576, pending: false }, outbox: { count: 1, bytes: 96 * 1024 } };
      case 'cleanStorage': {
        await sleep(500); const st = M.storage;
        if (p.kind === 'reports') { st.reports.count = 5; st.reports.local = 5; st.reports.bytes = 7.9 * 1048576; return { ok: true, message: '已删除 4 份旧报告' }; }
        if (p.kind === 'backups') { st.backups = { count: 1, bytes: 30 * 1024, admin: 0 }; return { ok: true, message: `已清理 5 条备份（${p.days} 天以前和没生效的），释放 382 KB` }; }
        if (p.kind === 'webview') { st.webview.pending = true; return { ok: true, message: '下次启动 Iris 时会清理界面缓存' }; }
        if (p.kind === 'outbox') { st.outbox = { count: 0, bytes: 0 }; return { ok: true, message: '已删除 1 个待发送的反馈包' }; }
        return {};
      }
      case 'setDismissed': M.dismissed = M.dismissed || new Set(); if (p.on) M.dismissed.add(p.key); else M.dismissed.delete(p.key); return { saved: true };
      case 'markFinding': {
        M.marks = M.marks || {};
        if (p.kind === 'other' && !p.note) delete M.marks[p.key]; else M.marks[p.key] = { kind: p.kind, note: p.note };
        return { saved: true, sent: M.settings.betaConsent === 'yes' };
      }
      case 'sendFeedback': await sleep(900); M.sent = '今天 ' + new Date().toTimeString().slice(0, 5); return { ok: true, id: p.reportId, message: '反馈已发送，谢谢你' };
      case 'exportFeedback': return { ok: true, exported: true, path: 'C:\\Users\\你\\Desktop\\Iris-反馈-' + p.reportId + '.zip', message: '反馈包已保存到桌面，发给作者就行' };
      case 'previewFeedback': return {
        items: ['去隐私的体检结果：51 条预筛结果，以及进程、自启动、服务、驱动、已装软件的名称、版本、厂商、安装位置', '研判结论：17 条，以及健康分',
                `你的回答和“判断不对”的标记：${Object.keys(M.answers).length} 条回答、${Object.keys(M.marks || {}).length} 条标记（包括你写的说明）`, '每一步的用时、程序版本、随机安装编号；这次有没有先做 Defender 扫描、扫了多久',
                '「处理」页的记录：3 步，每一步做了哪种操作、结果怎样（不含文件路径和备份）'],
        sizeKB: 38, server: M.settings.feedbackServer ? 'Iris 作者的服务器（阿里云中国香港）' : null,
        excluded: ['用户名、电脑名、邮箱（换成占位符）', '代理服务器、DNS、hosts 里的地址', 'API Key', '文档、照片、聊天记录、浏览记录、密码'] };
      case 'openUpdate': return {};
      case 'startCheckup': M.scanned = !!p.scan; runCheckup(p); return { started: true };
      case 'cancel': M.cancel = true; return {};
      case 'answer': M.answers[p.key] = { answer: p.answer, note: p.note || '' }; return { saved: true };
      case 'rejudge': { const r = await load(); await sleep(800); return { id: r.id }; }
      case 'buildPlan': return plan(p);
      case 'executePlan': {
        const ok = await nativeConfirm(`确认执行这 ${p.steps.length} 项修改？`, p.steps.map(s =>
          `<b style="font-size:13.5px;color:#1b1b1b">${esc(s.opName)}：${esc(s.title)}</b><br>会改：${esc(s.change)}<br>${s.reversible === false ? '不能自动撤销' : '可以撤销'}`));
        if (!ok) return { cancelled: true };
        runExecute(p); return { started: true };
      }
      case 'getJournal': return M.journal.map(e => Object.assign({}, e));
      case 'setSelected': M.selected = p.keys.slice(); return { saved: true };
      case 'undo': {
        const list = M.journal.filter(x => (p.batchId ? x.batchId === p.batchId : x.id === p.id) && x.status === 'done' && x.reversible);
        if (!await nativeConfirm(`确认撤销这 ${list.length} 步？`, list.map(x => `${esc(x.opName)}：${esc(x.title)}`))) return { cancelled: true };
        await sleep(700); list.forEach(e => { e.status = 'undone'; e.message = '已撤销，重新检测确认回到了原来的状态'; });
        return { ok: true, message: list.length > 1 ? `已撤销 ${list.length} 步` : '已撤销，重新检测确认回到了原来的状态' };
      }
      case 'saveSettings': {
        Object.assign(M.settings, p); delete M.settings.apiKey;
        if (p.apiKey) { M.settings.hasKey = true; M.settings.keyTail = p.apiKey.slice(-4); }
        return Object.assign({}, M.settings);
      }
      case 'testConnection': await sleep(900); return { ok: true, message: `连接成功（${M.settings.model}，用时 1.2 秒）` };
      case 'openReportFile': return { id: 'demo' };
      case 'exportReport': return { path: 'C:\\Users\\你\\Desktop\\Iris-体检报告-20261025-0830.md' };
      case 'openDefender': case 'openPath': case 'openUrl': case 'setWindowTheme': return {};
      case 'getPrivacy': {
        let text = '（浏览器预览：完整的隐私政策在仓库根目录 PRIVACY.md；桌面版会在这里显示全文。）';
        if (window.__PRIVACY__) text = window.__PRIVACY__;
        else try { const r = await fetch('dev/PRIVACY.md'); if (r.ok) text = await r.text(); } catch (e) {}
        return { version: '2026-10-27.1', text };
      }
      case 'acceptPrivacy': M.privacyAccepted = true; return { accepted: true };
      case 'declinePrivacy': return {};
      case 'clearAllData': {
        if (!await nativeConfirm('确认清除所有数据？', ['体检报告、操作记录、设置和 API Key 都会删除', '这一步不能撤销'])) return { cancelled: true };
        M.journal = []; return { ok: true, message: '已清除' };
      }
      default: throw new Error('未知方法 ' + method);
    }
  }

  // 体检过程：每查完一个方面推送一条"发现"，前端做成实时发现流
  const SECTIONS = [
    ['资源占用', 'CPU、内存、开机时长', 0, 3.1], ['进程', '96 个进程，签名都已校验', 0, 11.9], ['开机自启动', '18 项，2 项值得看', 2, 2.4],
    ['服务', '241 个服务，1 个第三方服务寄宿在 svchost', 1, 4.0], ['计划任务', '163 个任务，1 个指向的程序已不存在', 2, 3.3],
    ['驱动', '312 个驱动，1 条只剩登记', 1, 20.5], ['已装软件', '87 个软件', 0, 2.1], ['存储', 'C 盘剩余 6%，缓存 6.4 GB', 2, 11.8],
    ['电源与电池', '快速启动开着，23 天没真正重启', 1, 0.6], ['安全设置', 'Defender 正常；2 条失效的排除项', 3, 6.1],
    ['浏览器与网络', 'Edge 关闭后仍在后台运行', 1, 1.9], ['驱动与系统版本', 'Windows 11 24H2，补丁是最新的', 0, 4.2], ['稳定性', '30 天内 2 次蓝屏', 1, 2.8],
  ];
  async function runCheckup(p) {
    M.cancel = false;
    const t0 = Date.now(), el = () => (Date.now() - t0) / 1000;
    emit('progress', { phase: 'admin', elapsed: 0 });
    await sleep(1200);
    emit('section', { title: '管理员权限', detail: '你在 Windows 弹窗里点了“是”', flags: 0, sec: 4.8, kind: 'admin' });
    if (p.scan) {
      emit('progress', { phase: 'scan', elapsed: el() });
      await sleep(1500);
      emit('section', { title: 'Microsoft Defender 快速扫描', detail: '没有发现威胁', flags: 0, sec: 142.0 });
    }
    for (let i = 0; i < SECTIONS.length; i++) {
      if (M.cancel) { emit('checkupError', { message: '已取消，没有生成报告' }); return; }
      emit('progress', { phase: 'collect', index: i, total: SECTIONS.length, title: SECTIONS[i][0], elapsed: el() });
      await sleep(380 + SECTIONS[i][3] * 18);
      emit('section', { title: SECTIONS[i][0], detail: '', flags: 0, sec: SECTIONS[i][3] });
    }
    // 和桌面程序一样：采集完成后补上每个方面查到了什么
    emit('sectionDetails', { rows: SECTIONS.map(x => ({ title: x[0], detail: x[1], flags: x[2], sec: x[3] })) });
    emit('progress', { phase: 'judge', elapsed: el(), mode: p.mode });
    await sleep(1800);
    emit('section', { title: p.mode === 'ai' ? 'AI 研判' : '规则研判', detail: p.mode === 'ai' ? 'deepseek-chat · 16 640 token · 17 条结论，校验修正 0 处' : '17 条结论', flags: 0, sec: p.mode === 'ai' ? 21.4 : 0.3 });
    await sleep(500);
    emit('checkupDone', { id: 'demo' });
    if (M.settings.betaConsent === 'yes') { await sleep(2600); M.sent = '今天 ' + new Date().toTimeString().slice(0, 5); emit('feedbackSent', { ok: true, id: 'demo', message: '这次的去隐私反馈包已自动发送，谢谢你' }); }
  }

  function plan(p) {
    const r = M.report, steps = [], skipped = [];
    const CHANGE = {
      'startup.disable': ['从开机自启动里关掉这一项（登记会备份）', '没有明显代价；需要时手动打开软件即可', '可以在「处理」页里一键撤销'],
      'task.disable': ['把这个计划任务设为停用（不删除）', '软件不会在后台自动更新，打开软件时一般会提示更新', '可以在「处理」页里一键撤销'],
      'task.delete_leftover': ['删除这个计划任务（先导出备份）', '没有代价：它指向的程序已经不存在', '可以在「处理」页里一键恢复'],
      'service.set_manual': ['把服务的启动方式从“自动”改为“手动”', '开机不再自动运行；打开对应软件时一般会自己启动', '可以在「处理」页里一键撤销'],
      'service.delete_orphan': ['删除只剩登记、文件已不存在的驱动登记（先完整备份）', '没有代价：驱动文件早已不在', '可以在「处理」页里一键恢复登记'],
      'browser.disable_background': ['关闭 Edge 的“关闭后继续运行后台扩展和应用”', '关掉窗口后，Edge 的扩展不再在后台工作', '可以在「处理」页里一键撤销'],
      'power.disable_fast_startup': ['关闭快速启动（HiberbootEnabled 改为 0）', '冷启动会慢几秒；以后点“关机”就是真正的关机', '可以在「处理」页里一键撤销'],
      'cache.clean': ['把你选的类别里、超过所选天数的文件移到回收站', '程序第一次打开时可能慢一点，缓存会重新生成', '可以在回收站里还原'],
    };
    for (const key of p.keys || []) {
      const f = r.result.judgement.findings.find(x => x.flagIds.join('+') === key);
      if (!f) continue;
      const alt = r.altOps[key];
      const op = alt ? alt.op : f.proposedAction, c = CHANGE[op] || ['把这一项改回安全的状态', '没有明显代价', '可以在「处理」页里一键撤销'];
      steps.push({ key: key + '#0', findingKey: key, op, opName: alt ? alt.name : f.actionName, risk: alt ? alt.risk : f.risk, title: f.object, change: c[0], cost: c[1], undo: c[2],
                   reversible: op !== 'app.uninstall', needsAdmin: op !== 'startup.disable' && op !== 'browser.disable_background',
                   cacheOptions: op === 'cache.clean' ? { categories: ['临时文件', '开发工具缓存', '传递优化缓存'], ages: [7, 30, 90, 180, 365] } : null });
    }
    return { planId: 'plan-' + Date.now(), steps, needsAdmin: steps.some(s => s.needsAdmin), skipped };
  }

  /** 和桌面程序一样：按结论编号汇总操作记录，同一条看最近一批 */
  function handled() {
    const out = {}, groups = {};
    for (const e of M.journal) if (e.findingKey) (groups[e.findingKey] = groups[e.findingKey] || []).push(e);
    for (const [k, g] of Object.entries(groups)) {
      const latest = g.slice().sort((a, b) => b.t - a.t)[0], batch = g.filter(e => e.batchId === latest.batchId);
      out[k] = { status: batch.some(e => e.status === 'done' || e.status === 'info') ? 'done' : batch.some(e => e.status === 'undone') ? 'undone' : 'failed',
                 time: latest.time.replace('今天 ', '10-25 '), batchId: latest.batchId, message: latest.message };
    }
    return out;
  }
  if (/[?&]carried\b/.test(location.search)) { M.selected = ['F012', 'F017']; M.carried = 2; }

  async function runExecute(p) {
    const batch = 'batch-' + Date.now();
    for (const s of p.steps) {
      emit('opProgress', { key: s.key, status: 'running', message: '正在修改…' });
      await sleep(650);
      emit('opProgress', { key: s.key, status: 'verifying', message: '重新检测中…' });
      await sleep(550);
      const ok = s.op !== 'service.delete_orphan';
      const entry = { id: 'j' + Date.now() + Math.random().toString(16).slice(2, 6), batchId: batch, time: '今天 ' + new Date().toTimeString().slice(0, 5), t: Date.now(),
                      findingKey: s.findingKey, reportId: 'demo',
                      op: s.op, opName: s.opName, title: s.title, risk: s.risk, status: ok ? 'done' : 'rolledBack',
                      message: ok ? '已完成，重新检测确认生效' : '重新检测发现系统又恢复了这条登记（可能有程序在守护它），已自动恢复原状，没有留下改动',
                      reversible: s.reversible !== false };
      M.journal.unshift(entry);
      emit('opProgress', { key: s.key, status: entry.status, message: entry.message });
    }
    emit('planDone', { batchId: batch });
  }
})();
