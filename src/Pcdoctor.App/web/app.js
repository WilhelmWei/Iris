/* Iris · 界面逻辑
   一个 state 对象 + 每个页面一个 render 函数；事件用 data-act 委托。没有框架、没有构建步骤。
   主区域（main）和右侧面板（sheet）分开渲染：打开、切换结论时面板不会重新滑入，主区域也不会重播进入动画。 */
(function () {
  'use strict';

  const ICONS = {
    home: '<path d="M4 11l8-7 8 7v8a1 1 0 0 1-1 1h-4v-6h-6v6H5a1 1 0 0 1-1-1z"/>',
    doc: '<path d="M7 3h7l5 5v13H7z"/><path d="M14 3v5h5M10 13h6M10 17h6"/>',
    list: '<path d="M9 6h11M9 12h11M9 18h11"/><path d="M4 6l1 1 2-2M4 12l1 1 2-2M4 18l1 1 2-2"/>',
    undo: '<path d="M9 14L4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/>',
    gear: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
    pulse: '<path d="M3 12h4l2.5-6 4 12 2.5-6H21"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>',
    eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
    eyeoff: '<path d="M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3.2 4.1M6.6 6.6C3.8 8.4 2 12 2 12s3.5 7 10 7a9.6 9.6 0 0 0 5.4-1.6M9.9 9.9a3 3 0 0 0 4.2 4.2"/>',
    moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
    shield: '<path d="M12 3l8 3v6c0 4.5-3.4 8.4-8 9-4.6-.6-8-4.5-8-9V6z"/><path d="M8.5 12l2.5 2.5 4.5-5"/>',
    alert: '<path d="M12 3l9.5 16.5h-19z"/><path d="M12 10v4M12 17.5v.01"/>',
    bolt: '<path d="M13 2L4 14h7l-1 8 9-12h-7z"/>',
    ghost: '<path d="M5 21V10a7 7 0 0 1 14 0v11l-2.5-2-2.3 2-2.2-2-2.2 2-2.3-2z"/><path d="M9.5 10v.01M14.5 10v.01"/>',
    layers: '<path d="M12 3l9 5-9 5-9-5z"/><path d="M3 13l9 5 9-5"/>',
    ask: '<circle cx="12" cy="12" r="9"/><path d="M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14M12 17.5v.01"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    x: '<path d="M6 6l12 12M18 6L6 18"/>',
    chev: '<path d="M9 6l6 6-6 6"/>',
    up: '<path d="M6 15l6-6 6 6"/>',
    down: '<path d="M6 9l6 6 6-6"/>',
    arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    disk: '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 14h18"/><circle cx="16.5" cy="16.5" r=".6"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.01"/>',
    spinner: '<path d="M12 3a9 9 0 1 0 9 9"/>',
    dot: '<circle cx="12" cy="12" r="3"/>',
    gift: '<path d="M4 11h16v9H4zM3 7h18v4H3zM12 7v13"/><path d="M12 7c-2-3.5-6-3.5-6-1s4 1 6 1zM12 7c2-3.5 6-3.5 6-1s-4 1-6 1z"/>',
    spark: '<path d="M12 3v4M12 17v4M3 12h4M17 12h4M6 6l2.5 2.5M15.5 15.5L18 18M6 18l2.5-2.5M15.5 8.5L18 6"/>',
    key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3"/>',
    lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
    book: '<path d="M4 5a2 2 0 0 1 2-2h14v16H6a2 2 0 0 0-2 2z"/><path d="M4 19V5"/>',
    trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
    code: '<path d="M8 8l-5 4 5 4M16 8l5 4-5 4"/>',
    cloud: '<path d="M7 18a5 5 0 0 1-.6-10A6 6 0 0 1 18 9a4.5 4.5 0 0 1-.5 9z"/>',
    scan: '<path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M4 12h16"/>',
    motion: '<path d="M4 12c3-6 5-6 8 0s5 6 8 0"/>',
    pro: '<path d="M4 6h16M4 12h10M4 18h6"/>',
    inbox: '<path d="M3 13l3-8h12l3 8v6H3z"/><path d="M3 13h5l1 2h6l1-2h5"/>',
  };
  const icon = (n) => `<span class="ico"><svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[n] || ''}</svg></span>`;
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  const VERDICT = { suspicious: '可疑', problem: '需处理', leftover: '卸载残留', bloat: '捆绑 / 常驻', uncertain: '拿不准', normal: '正常' };
  const VICON = { suspicious: 'alert', problem: 'bolt', leftover: 'ghost', bloat: 'layers', uncertain: 'ask', normal: 'check' };
  const ORDER = { suspicious: 0, problem: 1, leftover: 2, bloat: 3, uncertain: 4, normal: 5 };
  const RISK = { green: '低风险', yellow: '有代价', red: '专业操作' };
  const SECTIONS = ['资源占用', '进程', '开机自启动', '服务', '计划任务', '驱动', '已装软件', '存储', '电源与电池', '安全设置', '浏览器与网络', '驱动与系统版本', '稳定性'];
  const GUIDE = {
    'defender.review': { btn: '打开 Windows 安全中心', steps: '病毒和威胁防护 → “病毒和威胁防护”设置下的“管理设置” → 排除项 → 添加或删除排除项。Iris 不修改安全设置，这一步由你自己在 Windows 里完成。' },
  };

  // 等待时轮播：我们的理念和小知识
  const WAIT = [
    ['我们的理念', '每个结论都有证据', '不说“发现 300 个问题”。每一条都写清看到了什么、为什么这么判断、把握有多大、可能在哪里判断错。'],
    ['我们的理念', '不常驻，不弹窗', '体检完、处理完就退出。没有托盘图标、没有后台进程、没有开机自启，也不捆绑任何软件。'],
    ['我们的理念', '查病毒交给 Defender', 'Windows 自带的 Microsoft Defender 已经足够强。Iris 只读取它的结果、帮你看懂，不重复造一个杀毒引擎，也不改它的设置。'],
    ['小知识', '点“关机”不等于重启', 'Windows 默认开着快速启动，关机时把系统内核存进休眠文件。长期只关机不重启，内存占用和小毛病会积累。'],
    ['小知识', '“内存加速球”是伪优化', 'Windows 会把空闲内存用作缓存。强行清空只会让程序下次打开更慢。真正有用的是关掉不用的程序。'],
    ['小知识', '卸载软件，排除项不会自动删除', '有些软件会让 Defender 不扫描自己。卸载后排除项还在，等于留了一个不被检查的位置。这正是 Iris 要找的“信息差”。'],
    ['小知识', '装软件时选“自定义安装”', '国产软件安装页常默认勾选“顺便安装”。选自定义，把不需要的勾去掉，能省掉以后一大半麻烦。'],
    ['小知识', '正在运行的驱动不能硬停', '强行停止正在运行的内核驱动会直接蓝屏。Iris 遇到这类驱动只标记，等重启后再处理。'],
    ['我们的做法', '没有“一键加速”的魔法', '只有讲得清的证据。每条结论都写清看到了什么、为什么这么判断、把握有多大。'],
    ['我们的做法', '关掉就是真关掉', '没有托盘图标，没有开机自启，也没有半夜弹出来的新闻。'],
  ];
  // 只说我们的做法，不点名任何产品；每处只出现一句，轮换着来
  const YIN = [
    '不弹窗，不送“加速球”，不帮你顺手装上全家桶。',
    '我们不会告诉你“发现 386 个问题”。只说真有的，并且说清为什么。',
    '没有“一键加速”的魔法，只有讲得清的证据。',
    '关掉就是真关掉。没有托盘图标，没有开机自启，也没有半夜弹出来的新闻。',
  ];
  const YIN_MANAGER = '你装的电脑管家，自己可能就是开机变慢的原因之一。';
  const pick = (arr, seed) => arr[Math.abs(seed) % arr.length];
  const MARK = { wrong: '判断错了', inaccurate: '不太准', missed: '漏掉了', other: '其他' };
  const AI_FEE = '虽然 Iris 开源免费，但 AI 功能会调用第三方 AI 厂商的接口，可能由厂商收取费用（一次体检通常只要几分钱）。';
  // 申请 Key 的步骤：只写各家都有的通用步骤，页面名称以厂商官网为准
  const AI_GUIDE = {
    deepseek: { name: 'DeepSeek', tag: '推荐：中文好、价格低', site: 'https://platform.deepseek.com',
      steps: ['打开 DeepSeek 开放平台，用手机号注册并登录', '按页面提示完成实名认证，充值几块钱（够用很久）', '找到「API Keys」页面，创建一个新的 Key，复制以 sk- 开头的那一串', '粘贴到下面，点「测试连接」'] },
    kimi: { name: 'Kimi（月之暗面）', tag: '中文好', site: 'https://platform.moonshot.cn',
      steps: ['打开 Moonshot AI 开放平台，用手机号注册并登录', '按页面提示完成认证和充值', '在控制台找到「API Key 管理」，新建一个 Key 并复制', '粘贴到下面，点「测试连接」'] },
    qwen: { name: '通义千问（阿里云百炼）', tag: '有阿里云账号的话最方便', site: 'https://bailian.console.aliyun.com',
      steps: ['用阿里云账号登录百炼控制台（没有就先注册，需要实名认证）', '按页面提示开通模型服务', '在控制台找到「API-KEY」页面，创建一个 Key 并复制', '粘贴到下面，点「测试连接」'] },
  };

  // 教程卡片：五课，每课一句理念 + 一个能验证的动作
  const LESSONS = [
    ['Iris 不常驻', '关掉窗口就彻底退出：没有托盘图标、没有后台服务、没有开机自启。可以打开任务管理器验证。', null],
    ['体检只读', '体检只读取信息，不改任何东西。要不要处理、处理哪些，在报告里由你决定。', ['看一份报告', 'nav', 'report']],
    ['每一步都能撤销', '处理前先备份，处理后重新检测；没生效的自动恢复原状。所有改动都记在「处理」页里，可以一键撤回。', ['看看处理记录', 'nav', 'plan']],
    ['AI 会犯错，所以我们给证据', '每条结论都写着判断来源和把握程度。觉得不对就告诉它，下次研判会参考你的回答。', null],
    ['查病毒交给 Defender', '我们鼓励用好已有的强大工具。Iris 只读取 Defender 的结果、帮你看懂，不改任何安全设置。', null],
  ];

  const state = {
    view: 'home', app: null, report: null, loading: false, filter: 'todo', cat: null,
    selected: new Set(), dismissed: new Set(), pro: false, mode: 'offline', scan: false,
    progress: null, stream: [], streamFresh: 0, sectionEvents: false, runStart: 0, running: false,
    plan: null, planKeys: '', planStatus: {}, cacheChoice: {}, spaceProg: {}, spaceOpen: new Set(), executing: false, lastRun: null, carried: null,
    journal: [], sheet: null, tip: 0, lessonConfirm: false, enter: true,
    markOpen: null, markKind: 'wrong', fb: {}, ob: null, seed: Math.floor(Math.random() * 1000),
  };
  const S = () => state.app.settings;

  const $main = document.getElementById('main');
  const $layer = document.getElementById('layer');
  const toast = (msg) => {
    const t = document.getElementById('toast');
    t.textContent = msg; t.classList.add('show');
    clearTimeout(toast.timer); toast.timer = setTimeout(() => t.classList.remove('show'), 3400);
  };
  const call = async (method, params) => {
    try { return await api.call(method, params); } catch (e) { toast(e.message || String(e)); throw e; }
  };
  const quiet = (method, params) => api.call(method, params).catch(() => null);

  /** 演示模式：遮住用户名、电脑名、邮箱，方便截图发给别人 */
  function pii(text) {
    const s = String(text == null ? '' : text);
    if (!state.app || !S().demoMode) return s;
    return s.replace(/([A-Za-z]:\\Users\\)[^\\\s"“”，。]+/gi, '$1••••').replace(/[\w.+-]+@[\w-]+\.[\w.-]+/g, '••••@••••')
      .replace(/\b(LAPTOP|DESKTOP)-[A-Z0-9]+\b/g, '••••');
  }

  // ------------------------------------------------------------------ 通用部件
  function ring(score, size, label, anim) {
    const sw = size >= 140 ? 10 : size >= 80 ? 7 : 5;
    const r = size / 2 - sw, c = 2 * Math.PI * r;
    const off = c * (1 - Math.max(0, Math.min(100, score)) / 100);
    return `<div class="ring" style="width:${size}px;height:${size}px;font-size:${Math.round(size * .36)}px">
      <svg width="${size}" height="${size}"><circle class="bg" cx="${size / 2}" cy="${size / 2}" r="${r}" fill="none" stroke-width="${sw}"/>
      <circle class="fg" cx="${size / 2}" cy="${size / 2}" r="${r}" fill="none" stroke="url(#ring-g)" stroke-width="${sw}" stroke-linecap="round"
        stroke-dasharray="${c}" stroke-dashoffset="${anim ? c : off}" ${anim ? `data-off="${off}"` : ''}/></svg>
      <div class="val"><div><b class="num" ${anim ? `data-count="${score}"` : ''}>${anim ? 0 : score}</b>${label ? `<span>${label}</span>` : ''}</div></div></div>`;
  }
  /** 进入页面时：圆环画出来、数字滚动到位（学 Revolut 的余额动画） */
  function playIntro(root) {
    const reduce = document.documentElement.classList.contains('reduce') || matchMedia('(prefers-reduced-motion: reduce)').matches;
    root.querySelectorAll('[data-off]').forEach(el => {
      if (reduce) { el.style.strokeDashoffset = el.dataset.off; return; }
      requestAnimationFrame(() => requestAnimationFrame(() => { el.style.strokeDashoffset = el.dataset.off; }));
    });
    if (!reduce) root.querySelectorAll('.cats .bar > i').forEach((el, i) => {
      const w = el.style.width; el.style.width = '0'; el.style.transitionDelay = (250 + i * 70) + 'ms';
      requestAnimationFrame(() => requestAnimationFrame(() => { el.style.width = w; }));
    });
    root.querySelectorAll('[data-count]').forEach(el => {
      const to = +el.dataset.count;
      if (reduce) { el.textContent = to; return; }
      const t0 = performance.now(), dur = 1100;
      const step = (t) => { const k = Math.min(1, (t - t0) / dur), e = 1 - Math.pow(1 - k, 3); el.textContent = Math.round(to * e); if (k < 1) requestAnimationFrame(step); };
      requestAnimationFrame(step);
    });
  }
  // ------------------------------------------------------------------ 动效：跟手、连贯，但不花哨。"减少动效"时全部退化为直接切换
  const reduced = () => document.documentElement.classList.contains('reduce') || matchMedia('(prefers-reduced-motion: reduce)').matches;
  const canVT = () => !!document.startViewTransition && !reduced();
  /** 选中底色 / 下划线跟着点击滑过去：分段按钮、标签页、左侧导航。位置按容器记住，整页重绘后从旧位置滑到新位置 */
  const glideMem = new Map();
  function glide(root) {
    if (!root) return;
    const boxes = root.matches && root.matches('.seg, .tabs, #nav, .seg-mini') ? [root] : root.querySelectorAll('.seg, .tabs, #nav, .seg-mini');
    boxes.forEach(box => {
      const key = box.dataset.glide || box.id || box.className.split(' ')[0] + ':' + (box.getAttribute('aria-label') || '');
      const on = box.querySelector(':scope > [aria-pressed="true"], :scope > [aria-current]');
      let g = box.querySelector(':scope > .glider'), fresh = false;
      if (!g) { g = document.createElement('span'); g.className = 'glider'; g.style.transition = 'none'; box.prepend(g); box.classList.add('glide'); fresh = true; }
      if (!on || !on.offsetWidth) { g.style.opacity = 0; return; }
      const line = box.classList.contains('tabs');
      const r = { x: on.offsetLeft, y: line ? on.offsetTop + on.offsetHeight - 2 : on.offsetTop, w: on.offsetWidth, h: line ? 2 : on.offsetHeight };
      const prev = glideMem.get(key); glideMem.set(key, r);
      const place = p => { g.style.transform = `translate(${p.x}px, ${p.y}px)`; g.style.width = p.w + 'px'; g.style.height = p.h + 'px'; g.style.opacity = 1; };
      if (fresh && prev && !reduced()) { place(prev); void g.offsetWidth; g.style.transition = ''; place(r); }
      else { place(r); if (fresh) requestAnimationFrame(() => { g.style.transition = ''; }); }
    });
  }
  /** 换页：旧页面淡出，新页面的各块依次升起（浏览器支持时用 View Transitions） */
  function pageSwap(changed, fn) {
    if (!changed || !canVT()) return fn();
    document.documentElement.dataset.vt = 'page';
    const t = document.startViewTransition(fn);
    t.finished.finally(() => { delete document.documentElement.dataset.vt; });
  }
  /** 换主题：新主题从点击的位置圆形展开 */
  function reveal(x, y, fn) {
    if (!canVT()) return fn();
    document.documentElement.dataset.vt = 'theme';
    const t = document.startViewTransition(fn);
    t.ready.then(() => {
      const r = Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y));
      document.documentElement.animate({ clipPath: [`circle(0px at ${x}px ${y}px)`, `circle(${r}px at ${x}px ${y}px)`] },
        { duration: 700, easing: 'cubic-bezier(.32, .72, 0, 1)', pseudoElement: '::view-transition-new(root)' });
    }).catch(() => {});
    t.finished.finally(() => { delete document.documentElement.dataset.vt; });
  }
  function bump(el) { if (!el || reduced()) return; el.classList.remove('bump'); void el.offsetWidth; el.classList.add('bump'); }
  /** 鼠标跟随：卡片上的柔光、主按钮轻微被"吸"过去 */
  let magnet = null, glowCard = null, raf = 0, lastMove = null;
  document.addEventListener('pointermove', e => {
    lastMove = e; if (raf) return;
    raf = requestAnimationFrame(() => {
      raf = 0; const ev = lastMove, t = ev.target && ev.target.closest ? ev.target : null;
      const card = t && t.closest('.card');
      if (card) { const r = card.getBoundingClientRect(); card.style.setProperty('--mx', (ev.clientX - r.left) + 'px'); card.style.setProperty('--my', (ev.clientY - r.top) + 'px'); }
      glowCard = card;
      const m = t && !reduced() ? t.closest('.magnet') : null;
      if (magnet && magnet !== m) { magnet.style.setProperty('--tx', '0px'); magnet.style.setProperty('--ty', '0px'); }
      magnet = m;
      if (m) { const r = m.getBoundingClientRect(), k = .18;
        m.style.setProperty('--tx', Math.max(-7, Math.min(7, (ev.clientX - r.left - r.width / 2) * k)) + 'px');
        m.style.setProperty('--ty', Math.max(-5, Math.min(5, (ev.clientY - r.top - r.height / 2) * k)) + 'px'); }
    });
  }, { passive: true });
  document.addEventListener('pointerleave', () => { if (magnet) { magnet.style.setProperty('--tx', '0px'); magnet.style.setProperty('--ty', '0px'); magnet = null; } });
  addEventListener('resize', () => glide(document));
  /** 右侧面板可以按住顶部往右拖，松手超过一段距离（或甩得够快）就关上 */
  let drag = null;
  document.addEventListener('pointerdown', e => {
    const top = e.target.closest && e.target.closest('.sheet-top');
    if (!top || e.target.closest('button, a, input') || e.button !== 0) return;
    const sh = top.closest('.sheet'), sc = $layer.querySelector('.scrim');
    drag = { sh, sc, x0: e.clientX, t0: performance.now(), dx: 0, w: sh.offsetWidth };
    sh.style.animation = 'none'; sh.style.transition = 'none'; sh.classList.add('dragging');
    try { top.setPointerCapture(e.pointerId); } catch { }
  });
  document.addEventListener('pointermove', e => {
    if (!drag) return;
    const raw = e.clientX - drag.x0; drag.dx = raw > 0 ? raw : -Math.sqrt(-raw) * 2;  // 往左拖有阻尼
    drag.sh.style.transform = `translateX(${drag.dx}px)`;
    if (drag.sc) drag.sc.style.opacity = Math.max(0, 1 - Math.max(0, drag.dx) / drag.w);
  });
  const endDrag = () => {
    if (!drag) return;
    const d = drag; drag = null; d.sh.classList.remove('dragging');
    const v = d.dx / Math.max(1, performance.now() - d.t0);
    d.sh.style.transition = 'transform .32s cubic-bezier(.32, .72, 0, 1)';
    if (d.sc) d.sc.style.transition = 'opacity .32s';
    if (d.dx > d.w * .3 || (v > .5 && d.dx > 40)) {
      d.sh.style.transform = 'translateX(100%)'; if (d.sc) d.sc.style.opacity = 0;
      setTimeout(() => { state.sheet = null; $layer.innerHTML = ''; setChrome(); refreshRows(); renderTray(); }, 300);
    } else { d.sh.style.transform = ''; if (d.sc) d.sc.style.opacity = ''; }
  };
  document.addEventListener('pointerup', endDrag); document.addEventListener('pointercancel', endDrag);

  const keyOf = (f) => f.flagIds.join('+');
  const executable = (f) => state.app && state.app.executableOps.includes(f.proposedAction);
  const guideOf = (f) => state.app && state.app.guideOps.includes(f.proposedAction) ? GUIDE[f.proposedAction] || { btn: f.actionName, steps: '' } : null;
  const source = (f) => f.kbId ? '知识库' : (state.report && state.report.result.offline ? '规则' : 'AI');
  const answerOf = (k) => state.report && state.report.answers && state.report.answers[k];
  const altOf = (k) => { const R = state.report, a = answerOf(k); return R && R.altOps && R.altOps[k] && a && a.answer === 'no' ? R.altOps[k] : null; };
  /** 处理状态（来自操作记录）：done 已处理 / failed 上次没生效 / undone 已撤销 */
  const handledOf = (k) => state.report && state.report.handled && state.report.handled[k];
  const isDone = (k) => (handledOf(k) || {}).status === 'done';
  const canDo = (f) => state.report && state.report.canExecute !== false && !isDone(keyOf(f)) && ((executable(f) && f.verdict !== 'normal') || !!altOf(keyOf(f)));
  /** "最该先做的事"是一句话，找出它说的是哪条结论：句子里出现的最长的对象名（至少 2 个字） */
  function findingForText(t) {
    const fs = state.report.result.judgement.findings.filter(f => f.verdict !== 'normal' && f.object && f.object.length >= 2);
    const hit = fs.filter(f => t.includes(f.object) || t.includes(f.object.split(/[（(\s]/)[0]));
    return hit.sort((a, b) => b.object.length - a.object.length)[0] || null;
  }
  const markOf = (k) => state.report && state.report.marks && state.report.marks[k];
  const findingByKey = (k) => state.report && state.report.result.judgement.findings.find(f => keyOf(f) === k);
  const fmtSize = b => b == null ? '—' : b >= 1073741824 ? `${(b / 1073741824).toFixed(b >= 10737418240 ? 0 : 1)} GB` : `${Math.max(1, Math.round(b / 1048576))} MB`;
  const fmtMB = mb => fmtSize(mb * 1048576);
  const fmtSec = (s) => s == null ? '' : s >= 60 ? `${Math.floor(s / 60)} 分 ${Math.round(s % 60)} 秒` : `${s < 10 ? s.toFixed(1) : Math.round(s)} 秒`;

  // ------------------------------------------------------------------ 框架
  function setChrome() {
    document.querySelectorAll('#nav [data-nav]').forEach(b => {
      const on = b.dataset.nav === state.view || (state.view === 'running' && b.dataset.nav === 'home');
      if (on) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
      b.disabled = (b.dataset.nav === 'report' && !state.report && !(state.app && state.app.lastReport));
    });
    const n = state.selected.size, badge = document.getElementById('plan-count');
    if (String(n) !== badge.textContent && n) bump(badge);
    badge.hidden = !n; badge.textContent = n;
    glide(document.getElementById('nav'));
    document.getElementById('demo-badge').hidden = !S().demoMode;
  }
  function applyLook() {
    const t = S().theme || 'dark', root = document.documentElement;
    if (t === 'auto') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', t);
    root.classList.toggle('reduce', !!S().reduceMotion);
    const dark = t === 'auto' ? matchMedia('(prefers-color-scheme: dark)').matches : t !== 'light';
    quiet('setWindowTheme', { dark });  // 让 Windows 标题栏跟着变深 / 变浅
  }

  function render() {
    setChrome();
    const fn = { home: viewHome, running: viewRunning, report: viewReport, plan: viewPlan, settings: viewSettings }[state.view] || viewHome;
    $main.classList.toggle('enter', state.enter);
    $main.innerHTML = fn();
    hydrate($main);
    if (state.enter) playIntro($main);
    state.enter = false;
    renderTray(); glide(document);
  }
  function hydrate(root) {
    root.querySelectorAll('[data-ico]').forEach(el => { el.outerHTML = icon(el.dataset.ico); });
    root.querySelectorAll('.page > *').forEach((el, i) => el.style.setProperty('--i', Math.min(i, 10)));
    root.querySelectorAll('.list > .row').forEach((el, i) => el.style.setProperty('--i', Math.min(i, 14)));
  }
  async function go(view) {
    if (view === 'journal') view = 'plan';  // 「还原」已并进「处理」
    if ((view === 'report' || view === 'plan') && !state.report && state.app.lastReport) {
      state.view = view; state.loading = true; state.enter = true; render();
      await loadReport(state.app.lastReport.id); state.loading = false;
    }
    if (view === 'plan') { state.journal = await call('getJournal'); ensurePlan(); }
    const changed = state.view !== view;
    state.enter = changed || state.enter; state.view = view;
    pageSwap(changed, () => { render(); $main.scrollTop = 0; });
  }

  // ------------------------------------------------------------------ 概览
  function viewHome() {
    const a = state.app, last = a.lastReport, s = S();
    const aiReady = s.hasKey || s.allowHosted;
    const hero = last
      ? `<section class="hero">${ring(last.score, 188, '健康分', state.enter)}
          <div><div class="eyebrow">上次体检 · ${esc(last.time)}</div>
            <p class="headline">${esc(pii(last.headline))}</p>
            <div class="meta">${last.delta != null ? `<span class="delta ${last.delta < 0 ? 'down' : ''}">${last.delta >= 0 ? '+' : '−'}${Math.abs(last.delta)} 较上次</span>` : ''}
              <button class="btn ghost" data-act="nav" data-to="report">查看完整报告 <span class="arrow">${icon('arrow')}</span></button></div></div></section>`
      : `<section class="hero"><div class="ring" style="width:150px;height:150px">${logo(150)}</div>
          <div><div class="eyebrow">Iris · 看清这台电脑</div><h1 style="margin-top:8px">给这台电脑做一次体检</h1>
            <p class="headline" style="font-size:16px;color:var(--text-2)">大约 2 分钟。体检只读取信息，不改动任何东西；哪些要处理、怎么处理，看完报告由你决定。</p></div></section>`;
    const up = a.update;
    // 下一步该做什么：清单里还有没执行的 → 去处理；处理过了 → 重新体检看变化
    const next = !last ? '' : a.pending
      ? `<section class="card next"><span class="glyph">${icon('list')}</span><div><b>处理清单里还有 ${a.pending} 项没执行</b>
          <p>执行前会逐项写清会改什么、有什么代价、怎么撤销，再弹 Windows 确认框。</p></div>
          <button class="btn" data-act="nav" data-to="plan">去处理 <span class="arrow">${icon('arrow')}</span></button></section>`
      : a.handled
      ? `<section class="card next"><span class="glyph done">${icon('check')}</span><div><b>上次体检后你处理了 ${a.handled.count} 项</b>
          <p>重新体检一次，看看健康分和问题有什么变化。处理过的每一项都可以在「处理」页里撤销。</p></div>
          <button class="btn" data-act="start">重新体检</button></section>` : '';
    const hb = a.handledBefore;
    const handledRow = hb ? `<li><span class="k ${hb.still ? '' : 'fix'}">${hb.still ? '!' : '✓'}</span><span>你上次处理的 ${hb.total} 项，这次${hb.still ? `还有 ${hb.still} 项又出现了` : '都没有再出现'}</span>
      <span class="why">${hb.still ? '可能是软件更新或重新安装后又加了回去，报告里会再次列出' : '处理确实生效了'}</span></li>` : '';
    return `<div class="page">
      ${up ? `<button class="update-pill" data-act="open-update">${icon('spark')}有新版本 ${esc(up.version)}<span>${esc(up.notes || '点这里下载')}</span>${icon('arrow')}</button>` : ''}
      ${hero}
      ${next}
      <section class="card start">
        <div class="start-row">
          <button class="btn primary big magnet" data-act="start">${icon('pulse')}${last ? '重新体检' : '开始体检'}</button>
          <div class="start-opts">
            <div class="seg" role="group" aria-label="研判方式">
              <button data-act="mode" data-mode="ai" aria-pressed="${state.mode === 'ai'}" ${aiReady ? '' : 'disabled title="先在设置里填 API Key"'}>AI 研判</button>
              <button data-act="mode" data-mode="offline" aria-pressed="${state.mode === 'offline'}">纯规则 · 不联网</button></div>
            <label class="switch"><input type="checkbox" id="opt-scan" data-act="scan" ${state.scan ? 'checked' : ''}><span class="track"></span>先用 Defender 快速扫描</label>
          </div></div>
        <div class="start-note">${state.mode === 'ai'
          ? `用 ${esc(s.model)} 解释每一项（约 20–60 秒），发送前去掉用户名、电脑名等个人信息。`
          : aiReady ? '结论来自固定规则和知识库，完全不联网。' : '想要 AI 解释每一项？<a href="#" data-act="nav" data-to="settings">在设置里填 API Key</a>。'}
          ${state.scan ? 'Defender 扫描会多 1–5 分钟。' : ''}${a.isAdmin ? '' : '开始后会请求一次管理员权限，用来读取驱动和系统日志。'}</div>
      </section>
      ${s.showLessons ? lessonCard() : ''}
      <div class="quick">
        <button data-act="nav" data-to="report" ${last ? '' : 'disabled'}><span class="round">${icon('doc')}</span>报告</button>
        <button data-act="nav" data-to="plan"><span class="round">${icon('list')}</span>处理</button>
        <button data-act="open-file"><span class="round">${icon('inbox')}</span>朋友的报告</button>
      </div>
      ${(a.history && a.history.length > 1) || (a.changes && a.changes.length) || hb ? `<div class="widgets">
        ${a.history && a.history.length > 1 ? `<div class="card widget"><h3>健康分走势</h3>
          <div style="display:flex;align-items:baseline;gap:10px"><span class="big">${a.history[a.history.length - 1][1]}</span><span class="muted small">最近 ${a.history.length} 次体检</span></div>
          ${spark(a.history)}</div>` : ''}
        ${(a.changes && a.changes.length) || hb ? `<div class="card widget"><h3>上次体检以来的变化</h3>
          <ul class="changes">${handledRow}${(a.changes || []).map(c => `<li><span class="k ${c.kind === 'fix' ? 'fix' : ''}">${c.kind === 'fix' ? '✓' : '+'}</span><span>${esc(pii(c.text))}</span><span class="why">${esc(c.why)}</span></li>`).join('')}</ul></div>` : ''}
      </div>` : ''}
      <section class="card principles">
        <div><b>${icon('eye')}每个结论都有证据</b><p>看到了什么、为什么这么判断、把握多大、可能错在哪，都写清楚。</p></div>
        <div><b>${icon('moon')}不常驻、不弹窗</b><p>做完就退出。没有后台进程和开机自启，不捆绑任何软件。</p></div>
        <div><b>${icon('undo')}每一步都能撤销</b><p>改之前备份，改之后重新检测；不满意就在「处理」页里一键恢复。</p></div>
      </section>
      <p class="yin">${esc(pick(YIN, state.seed))}</p>
    </div>`;
  }
  function logo(size) {
    return `<svg width="${size}" height="${size}" viewBox="0 0 32 32" style="filter:var(--ring-glow)" aria-hidden="true">
      <circle cx="16" cy="16" r="12.5" fill="none" stroke="url(#ring-g)" stroke-width="2.2"/><circle cx="16" cy="16" r="5.2" fill="url(#ring-g)"/>
      <circle cx="17.9" cy="14.1" r="1.4" style="fill:var(--bg)"/></svg>`;
  }
  function spark(h) {
    const W = 300, H = 86, pad = 8, top = 14, bottom = 18;
    const vals = h.map(x => x[1]), lo = Math.min(...vals) - 4, hi = Math.max(...vals) + 4;
    const X = i => pad + i * (W - 2 * pad) / (h.length - 1), Y = v => top + (hi - v) / (hi - lo) * (H - top - bottom);
    const pts = h.map((x, i) => `${X(i).toFixed(1)},${Y(x[1]).toFixed(1)}`);
    const last = h.length - 1;
    return `<svg class="spark" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-label="健康分走势：${h.map(x => x[0] + ' ' + x[1]).join('，')}">
      <path class="area" d="M${pts[0]} L${pts.join(' L')} L${X(last)},${H - bottom} L${X(0)},${H - bottom} Z"/>
      <path class="line" d="M${pts.join(' L')}" vector-effect="non-scaling-stroke"/>
      <circle class="dot" cx="${X(last)}" cy="${Y(h[last][1])}" r="3.5"/>
      <text x="${X(0)}" y="${H - 3}">${esc(h[0][0])}</text><text x="${X(last)}" y="${H - 3}" text-anchor="end">${esc(h[last][0])}</text></svg>`;
  }
  function lessonCard() {
    const i = Math.min(S().lessonIndex || 0, LESSONS.length - 1), L = LESSONS[i];
    const c = 2 * Math.PI * 20, off = c * (1 - (i + 1) / LESSONS.length);
    if (state.lessonConfirm) return `<section class="card lesson"><div class="prog">${icon('book')}</div>
      <div class="confirm-close">永久关闭教程卡片？以后可以在「设置 → 教程与小知识」里重新打开。</div>
      <div class="acts"><button class="btn ghost" data-act="lesson-keep">算了</button><button class="btn solid" data-act="lesson-off">关闭</button></div></section>`;
    return `<section class="card lesson" aria-label="认识 Iris">
      <div class="prog"><svg width="46" height="46"><circle cx="23" cy="23" r="20" fill="none" stroke="var(--surface-3)" stroke-width="3"/>
        <circle cx="23" cy="23" r="20" fill="none" stroke="url(#ring-g)" stroke-width="3" stroke-linecap="round" stroke-dasharray="${c}" stroke-dashoffset="${off}"/></svg>${i + 1}/${LESSONS.length}</div>
      <div><h3>认识 Iris · ${esc(L[0])}</h3><p>${esc(L[1])}</p></div>
      <div class="acts">${L[2] ? `<button class="btn ghost" data-act="${L[2][1]}" data-to="${L[2][2]}">${esc(L[2][0])}</button>` : ''}
        <button class="btn solid" data-act="lesson-next">${i + 1 < LESSONS.length ? '下一课' : '完成'}</button></div>
      <button class="x" data-act="lesson-close" aria-label="关闭教程卡片">${icon('x')}</button></section>`;
  }

  // ------------------------------------------------------------------ 体检进行中：实时发现流
  function runPct() {
    const p = state.progress || {};
    if (p.phase === 'admin') return 2;
    if (p.phase === 'scan') return 4;
    if (p.phase === 'judge') return Math.min(97, 88 + Math.floor((p.elapsed0 ? (Date.now() - p.elapsed0) / 1000 : 0) / 4));
    return 5 + Math.round(Math.max(0, p.index || 0) / SECTIONS.length * 82);
  }
  function runPhase() {
    const p = state.progress || {};
    return { admin: '等待你确认管理员权限', scan: 'Defender 正在快速扫描', judge: state.mode === 'ai' ? 'AI 正在研判' : '按规则和知识库研判', collect: `正在检查：${p.title || ''}` }[p.phase] || '准备中';
  }
  /** 体检进度环：数字和 % 写在一起，% 小一号（学 Revolut 的「48,87 €」） */
  function pctRing(pct, done) {
    const size = 232, sw = 10, r = size / 2 - sw, c = 2 * Math.PI * r;
    return `<div class="ring" style="width:${size}px;height:${size}px;font-size:84px">
      <svg width="${size}" height="${size}"><circle class="bg" cx="${size / 2}" cy="${size / 2}" r="${r}" fill="none" stroke-width="${sw}"/>
      <circle class="fg" cx="${size / 2}" cy="${size / 2}" r="${r}" fill="none" stroke="url(#ring-g)" stroke-width="${sw}" stroke-linecap="round"
        stroke-dasharray="${c}" stroke-dashoffset="${c * (1 - pct / 100)}"/></svg>
      <div class="val">${done ? `<span class="done-check">${icon('check')}</span>` : `<b class="num">${pct}<small>%</small></b>`}</div></div>`;
  }
  function viewRunning() {
    return `<div class="page run">
      <button class="btn ghost run-cancel" data-act="cancel">取消体检</button>
      <section class="run-hero">
        <div class="eyebrow">正在看清这台电脑</div>
        <div id="run-ring">${pctRing(runPct())}</div>
        <div class="run-phase"><span id="run-phase">${esc(runPhase())}</span><span class="sep">·</span><span class="num" id="run-clock">00:00</span></div>
        <p class="muted" id="run-note">只读取信息，不改任何东西。可以离开电脑，结束后报告会自动打开。</p>
      </section>
      <div class="card carousel" id="carousel" data-hover="tip">${carousel()}</div>
      <div class="card stream-box"><div class="list stream" id="stream">${streamRows()}</div>
        <div class="stream-foot" id="stream-foot">${streamFoot()}</div></div>
    </div>`;
  }
  /** 发现流：新的一行在最上面，框的高度固定，旧的往下沉、在框里滚动，页面上的其他东西都不会被挤动 */
  function streamRows() {
    const rows = state.stream.map((r, i) => `<div class="stream-row ${r.flags ? 'warn' : 'ok'} ${i >= state.streamFresh ? 'fresh' : ''}">
      <span class="s-ico">${icon(r.flags ? 'dot' : 'check')}</span>
      <div><b>${esc(r.title)}</b>${r.detail ? `<div class="d">${esc(pii(r.detail))}</div>` : ''}</div>
      <span class="t">${r.sec != null ? fmtSec(r.sec) : ''}</span></div>`);
    if (!state.runDone) rows.push(`<div class="stream-row now"><span class="s-ico">${icon('spinner')}</span><div><b>${esc(runPhase())}</b><div class="d">${{ admin: '请在 Windows 弹出的窗口里点“是”', scan: '由 Windows 自带的 Defender 在后台扫描。「Windows 安全中心」里不显示这类扫描的进度，扫完后那里的「上次扫描」时间会更新' }[(state.progress || {}).phase] || '…'}</div></div><span class="t"></span></div>`);
    state.streamFresh = state.stream.length;
    return rows.reverse().join('');
  }
  function streamFoot() {
    const done = state.stream.filter(r => SECTIONS.includes(r.title)).length;
    const s = Math.floor((Date.now() - state.runStart) / 1000);
    return `已完成 ${done} / ${SECTIONS.length} 个方面<span class="sep">·</span>用时 ${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }
  function carousel() {
    const n = WAIT.length, i = ((state.tip % n) + n) % n, w = WAIT[i];
    return `<div class="body"><div class="tag">${esc(w[0])}</div><h3>${esc(w[1])}</h3><p>${esc(w[2])}</p></div>
      <div class="tip-nav"><button class="icon-btn sm" data-act="tip-step" data-d="-1" aria-label="上一条">${icon('chev')}</button>
        <div class="dots">${WAIT.map((_, k) => `<button data-act="tip" data-i="${k}" aria-label="第 ${k + 1} 条" ${k === i ? 'aria-current="true"' : ''}></button>`).join('')}</div>
        <button class="icon-btn sm" data-act="tip-step" data-d="1" aria-label="下一条">${icon('chev')}</button></div>`;
  }
  function updateRunning() {
    if (state.view !== 'running') return;
    const ringEl = document.getElementById('run-ring'); if (!ringEl) return;
    ringEl.innerHTML = pctRing(runPct());
    document.getElementById('run-phase').textContent = runPhase();
    const box = document.getElementById('stream');
    box.innerHTML = streamRows(); box.scrollTop = 0;
    document.getElementById('stream-foot').innerHTML = streamFoot();
  }
  setInterval(() => {
    if (state.view !== 'running') return;
    const el = document.getElementById('run-clock');
    if (el && !state.runDone) {
      const s = Math.floor((Date.now() - state.runStart) / 1000); el.textContent = `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
      const f = document.getElementById('stream-foot'); if (f) f.innerHTML = streamFoot();
    }
    if (state.progress && state.progress.phase === 'judge' && !state.runDone) { const r = document.getElementById('run-ring'); if (r) r.innerHTML = pctRing(runPct()); }
  }, 1000);
  // 小知识 12 秒换一条；鼠标停在卡片上时暂停
  setInterval(() => {
    if (state.view !== 'running' || state.hover === 'tip') return;
    state.tip++; const el = document.getElementById('carousel'); if (el) el.innerHTML = carousel();
  }, 12000);

  // ------------------------------------------------------------------ 报告
  function buckets(fs) {
    return {
      todo: fs.filter(f => f.verdict !== 'normal' && !f.needsUser && !isDone(keyOf(f))),
      ask: fs.filter(f => f.needsUser && !isDone(keyOf(f))),
      done: fs.filter(f => isDone(keyOf(f))),
      normal: fs.filter(f => f.verdict === 'normal'),
      all: fs,
    };
  }
  function visible() {
    const fs = state.report.result.judgement.findings;
    let list = buckets(fs)[state.filter];
    if (state.cat) list = list.filter(f => f.flagIds.some(id => state.cat.ids.includes(id)));
    return list.slice().sort((a, b) => (state.dismissed.has(keyOf(a)) - state.dismissed.has(keyOf(b))) || (ORDER[a.verdict] - ORDER[b.verdict]) || (b.confidence - a.confidence));
  }
  function viewReport() {
    const R = state.report;
    if (state.loading || !R) return `<div class="page">${state.loading ? `<div class="r-head"><div class="skel" style="width:168px;height:168px;border-radius:50%"></div>
      <div style="display:grid;gap:12px"><div class="skel" style="height:14px;width:40%"></div><div class="skel" style="height:22px;width:90%"></div><div class="skel" style="height:22px;width:70%"></div></div></div>
      <div class="skel" style="height:86px"></div><div class="skel" style="height:300px"></div>` : '<div class="card empty">还没有体检报告</div>'}</div>`;
    const res = R.result, j = res.judgement, sc = res.score, s = S();
    const B = buckets(j.findings);
    const mode = res.offline ? '纯规则研判 · 未联网' : `AI 研判 · ${esc(res.model)}`;
    const cats = Object.entries(sc.categories);
    const tabs = [['todo', '建议处理'], ['ask', '需要你确认'], ['done', '已处理'], ['normal', '正常'], ['all', '全部']].filter(t => t[0] !== 'done' || B.done.length);
    const list = visible();
    return `<div class="page">
      <section class="r-head">${ring(sc.total, 168, '健康分', state.enter)}
        <div><div class="eyebrow">${esc(R.meta.reportTime || res.generatedAt)} · ${mode}${R.meta.machine ? ' · ' + esc(pii(R.meta.machine)) : ''}</div>
          <p class="headline">${esc(pii(j.headline))}</p>
          ${R.meta.source === 'file' ? '<p class="muted small" style="margin:-6px 0 12px">这份报告来自别的电脑，只能查看，不能在这台电脑上执行处理。</p>' : ''}
          <div class="r-actions">
            <button class="btn" data-act="export">导出报告</button>
            <button class="btn" data-act="rejudge" title="用你回答过的问题重新研判这份报告">重新研判</button>
            <button class="btn ghost" data-act="toggle-demo" aria-pressed="${!!s.demoMode}">${icon(s.demoMode ? 'eyeoff' : 'eye')}${s.demoMode ? '演示模式：开' : '演示模式'}</button>
          </div></div></section>
      <section class="card cats" aria-label="分项得分">${cats.map(([k, v]) => `<button data-act="cat" data-cat="${esc(k)}" title="只看「${esc(k)}」相关的结论">
        <span class="k">${esc(k)}</span><span class="v">${Math.round(v.score)}</span><span class="bar"><i style="width:${Math.round(v.score)}%"></i></span></button>`).join('')}</section>
      ${verdictCard(j)}
      ${spaceCard()}
      ${j.top3 && j.top3.length ? `<div class="section-head"><h2>最该先做的 ${j.top3.length} 件事</h2></div>
        <ol class="top3">${j.top3.map(t => { const f = findingForText(t);
          return f ? `<li class="card link" data-act="open" data-k="${esc(keyOf(f))}" tabindex="0" role="button">${esc(pii(t))}<span class="go">查看 ${icon('arrow')}</span></li>`
                   : `<li class="card">${esc(pii(t))}</li>`; }).join('')}</ol>` : ''}
      ${feedbackCard()}
      <div class="section-head"><h2>逐项结论</h2>${state.cat ? `<button class="chip" data-act="cat-clear" style="cursor:pointer">只看：${esc(state.cat.name)} ${icon('x')}</button>` : ''}
        <span class="more muted hint" style="cursor:default">点一条看详情 · <kbd>↑</kbd><kbd>↓</kbd> 切换</span></div>
      <div>
        ${bulkBar()}
        <div class="tabs" role="group" aria-label="筛选">${tabs.map(([k, t]) =>
          `<button data-act="filter" data-f="${k}" aria-pressed="${state.filter === k}">${t}<span class="n">${B[k].length}</span></button>`).join('')}</div>
        <div class="card list" style="margin-top:14px">${list.map(rowHtml).join('') || '<div class="empty">这一类没有项目</div>'}</div>
        ${R.meta.source === 'local' ? '<button class="missed" data-act="missed">Iris 漏掉了什么？告诉我们</button>' : ''}
      </div>
      ${s.showTips !== false && j.tips && j.tips.length ? `<div class="section-head"><h2>小知识</h2><button class="more" data-act="tips-off">不再显示</button></div>
        <div class="tips">${j.tips.map(t => `<div class="card tip"><span class="tag">小知识</span><b>${esc(t.title)}</b><p>${esc(t.body)}</p></div>`).join('')}${managerNote()}</div>` : ''}
      <div>
        <details class="fold"><summary>健康分是怎么算的</summary><div class="fold-body">
          <div class="muted small">健康分由代码按规则计算，AI 不参与打分。同一个原因的多条结果合并扣分，避免“吓唬式低分”。</div>
          <ul class="deduct">${cats.map(([k, v]) => `<li class="cat"><span>${esc(k)} · 权重 ${v.weight}%</span><span class="num">${Math.round(v.score)}</span></li>
            ${v.deductions.map(d => `<li>${d.points ? '−' + d.points : '不扣分'}　${esc(pii(d.objects.join('、')))}${d.note ? '（' + esc(d.note) + '）' : ''}${state.pro ? ` <span class="mono muted">${esc(d.rule)}</span>` : ''}</li>`).join('')}`).join('')}</ul></div></details>
        ${R.meta.timings && R.meta.timings.length ? `<details class="fold"><summary>这次体检用了多久</summary><div class="fold-body"><div class="timing">
          ${R.meta.timings.map(t => `<span>${esc(t[0])}</span><span class="num">${fmtSec(t[1])}</span>`).join('')}
          <span style="color:var(--text)">合计</span><span class="num">${fmtSec(R.meta.timings.reduce((a, t) => a + t[1], 0))}</span></div></div></details>` : ''}
        ${j.notCollected && j.notCollected.length ? `<details class="fold"><summary>没能检查的方面</summary><div class="fold-body"><ul class="deduct">${j.notCollected.map(x => `<li>${esc(x)}</li>`).join('')}</ul></div></details>` : ''}
        ${state.pro && res.issues && res.issues.length ? `<details class="fold"><summary>校验记录（模型输出被代码修正的地方）</summary><div class="fold-body"><ul class="deduct">${res.issues.map(x => `<li>${esc(x)}</li>`).join('')}</ul></div></details>` : ''}
      </div>
    </div>`;
  }
  /** "有没有中毒"：分清这次有没有让 Defender 扫描，不把"Iris 没看到"说成"没有病毒" */
  function verdictCard(j) {
    const R = state.report, D = R.defender || null, sus = j.malwareSuspected, scanned = !!(D && D.scanned);
    const ago = n => n == null ? null : n >= 10000 ? '没有记录' : n < 1 ? '今天' : `${Math.round(n)} 天前`;
    const title = sus ? '发现需要核实的迹象' : scanned ? 'Defender 扫描没有发现威胁' : '没有看到中毒迹象';
    const last = D ? [['快速扫描', ago(D.quickScanAgeDays)], ['全盘扫描', ago(D.fullScanAgeDays)]].filter(x => x[1]) : [];
    const stale = D && !scanned && Math.min(D.quickScanAgeDays == null ? 1e9 : D.quickScanAgeDays, D.fullScanAgeDays == null ? 1e9 : D.fullScanAgeDays) > 30;
    const canScan = R.meta.source === 'local' && !state.running;
    const warn = D && D.realTime === false
      ? `<div class="warn">${icon('alert')}<span>Defender 的实时保护是关着的。装了其他杀毒软件时这是正常的；没有装的话，建议在 Windows 安全中心里打开。</span><button class="btn small" data-act="defender">打开安全中心</button></div>`
      : stale ? `<div class="warn">${icon('alert')}<span>Defender 已经 ${Math.round(Math.min(D.quickScanAgeDays, D.fullScanAgeDays))} 天没有扫描过这台电脑。</span></div>` : '';
    return `<section class="card verdict"><span class="glyph ${sus ? 'v-suspicious' : stale || (D && D.realTime === false) ? 'v-problem' : 'v-normal'}">${icon(sus ? 'alert' : 'shield')}</span>
      <div><b>${title}</b><p>${esc(pii(j.malwareReasoning))}</p>
        <div class="scope"><span class="k">检查范围</span><span>${scanned
          ? '体检前，Microsoft Defender 刚做完一次快速扫描；Iris 读取了它的结果，再用自己的规则检查了自启动、驱动、排除项和放行记录。'
          : D && D.scanFailed ? '这次请 Microsoft Defender 快速扫描了，但扫描没有正常结束（常见原因：装了其他杀毒软件、Defender 被关掉、没有给管理员权限），所以只用了 Iris 自己的检查规则。'
          : '这次只用了 Iris 自己的检查规则，没有调用更专业的 Microsoft Defender 扫描。想更放心，可以先让 Defender 快速扫描一遍再体检。'}</span>
          ${last.length ? `<span class="k">上次扫描</span><span>${last.map(x => `${x[0]} ${x[1]}`).join(' · ')}</span>` : ''}</div>
        ${warn}
        ${!scanned && canScan ? `<button class="btn" data-act="scan-again">${icon('shield')}用 Defender 扫描后重新体检<span class="muted small">多 1–5 分钟</span></button>` : ''}
        <div class="note">查病毒交给 Windows 自带的 Microsoft Defender：Iris 只读取它的结果，不修改任何安全设置。</div></div></section>`;
  }

  /** 「空间」：C 盘还剩多少、能直接清理多少、更大的空间在哪；「C 盘空间去哪了」在报告出来后继续扫描，这张卡片原地更新 */
  function spaceCard() {
    const R = state.report, st = R && R.storage;
    if (!st) return '';
    const c = (st.volumes || []).find(v => (v.drive || '').toUpperCase() === 'C:');
    const cacheF = R.result.judgement.findings.find(f => f.proposedAction === 'cache.clean');
    const ck = cacheF && keyOf(cacheF), inPlan = ck && state.selected.has(ck), done = ck && handledOf(ck) && handledOf(ck).status === 'done';
    const chatsOnC = (st.chatApps || []).filter(x => x.onC).sort((a, b) => b.sizeGB - a.sizeGB);
    const big = [];
    const pad = s => /[A-Za-z0-9]$/.test(s) ? s + ' ' : s, padL = s => /^[A-Za-z0-9]/.test(s) ? ' ' + s : s;
    chatsOnC.forEach(x => big.push({ name: `${pad(x.name)}聊天文件`, mb: x.sizeGB * 1024, how: `在${padL(pad(x.name))}的设置里清理，或者把存储位置改到其他盘` }));
    if (st.downloadsGB >= 2) big.push({ name: '下载文件夹', mb: st.downloadsGB * 1024, how: '多是用过的安装包和文件，可以自己整理' });
    (st.system || []).forEach(x => big.push({ name: x.name, mb: x.mb, how: x.why }));
    big.sort((a, b) => b.mb - a.mb);
    const lead = [];
    if (c) lead.push(`C 盘还剩 ${fmtMB(c.freeGB * 1024)}。`);
    if (st.cleanableMB >= 100) lead.push(`清缓存能腾出约 ${fmtMB(st.cleanableMB)}`);
    if (big.length && big[0].mb > st.cleanableMB) lead.push(`${lead.length > 1 ? '；' : ''}更大的是${big[0].name}（${fmtMB(big[0].mb)}）`);
    const canDoIt = R.canExecute !== false && cacheF;
    return `<section class="card space" id="space-card">${spaceInner(c, st, big, lead, { ck, inPlan, done, canDoIt })}</section>`;
  }
  function spaceInner(c, st, big, lead, a) {
    const R = state.report;
    const used = c ? Math.max(0, 100 - (c.freePct || 0)) : 0;
    return `<div class="sp-head"><span class="glyph">${icon('disk')}</span>
        <div><b>空间</b><p>${esc(lead.join('') + (lead.length > 1 ? '。' : ''))}</p></div>
        ${c ? `<div class="sp-vol"><span class="muted small">C 盘</span><span class="bar ${c.freePct < 15 ? 'low' : ''}"><i style="width:${used}%"></i></span><span class="num small">剩 ${fmtMB(c.freeGB * 1024)} / ${fmtMB(c.sizeGB * 1024)}</span></div>` : ''}</div>
      <div class="sp-grid">
        <div class="sp-col"><h4>可以直接清理${st.cleanableMB >= 1 ? ` <span class="num">${fmtMB(st.cleanableMB)}</span>` : ''}</h4>
          ${st.measured === false ? '<p class="muted small">这份报告是旧版采集的，没有按类别测量。重新体检后会列出来。</p>'
            : (st.groups || []).length ? `<ul class="sp-list">${st.groups.map(g => `<li><span>${esc(g.group)}${g.running ? `<i class="muted small"> · ${esc(g.running)} 开着时跳过</i>` : ''}</span><span class="num">${fmtMB(g.mb)}</span></li>`).join('')}</ul>`
            : '<p class="muted small">没有量到值得清理的缓存。</p>'}
          ${a.canDoIt ? a.done ? `<span class="handled">${icon('check')}已清理</span>`
            : a.inPlan ? `<span class="in-plan">${icon('check')}已加入处理清单</span>`
            : `<button class="btn" data-act="space-add" data-k="${esc(a.ck)}">${icon('list')}加入处理清单</button>` : ''}
          <p class="muted small">都是程序会自动重新生成的缓存，直接删除才真正腾出空间；不碰聊天记录、下载和你的文件。</p></div>
        <div class="sp-col"><h4>更大的空间在这里</h4>
          ${big.length ? `<ul class="sp-list">${big.slice(0, 5).map(x => `<li title="${esc(x.how || '')}"><span>${esc(x.name)}<i class="muted small">${esc(x.how ? ' · ' + x.how : '')}</i></span><span class="num">${fmtMB(x.mb)}</span></li>`).join('')}</ul>`
            : '<p class="muted small">没有发现特别占地方的聊天文件或系统遗留。</p>'}
          <p class="muted small">这些不是"垃圾"：聊天文件和下载是你的数据，系统遗留交给 Windows 自带的「磁盘清理」。把聊天文件挪到其他盘，Iris 下一版会帮你做。</p></div>
      </div>
      ${spaceTree()}
      ${(st.notCleaned || []).length ? `<details class="fold" data-fold="not-cleaned"><summary>这些有人当垃圾清，Iris 不清</summary><div class="fold-body"><ul class="deduct">${st.notCleaned.map(n => `<li><b>${esc(n.name)}</b>：${esc(n.why)}</li>`).join('')}</ul></div></details>` : ''}`;
  }
  const SP_KIND = { system: '系统', tool: '交给 Windows', clean: 'Iris 能清', move: '可以挪', yours: '你的文件', apps: '软件' };
  function spaceTree() {
    const R = state.report, sp = R.space;
    if (R.meta.source !== 'local') return '';
    if (!sp && !R.spaceRunning) return '';
    const { running, pctDone, status } = spaceStatus();
    const tree = sp && sp.tree;
    const max = tree ? Math.max(1, ...(tree.children || []).map(k => k.bytes)) : 1;
    const row = (n, depth) => {
      const open = state.spaceOpen.has(n.path), kids = (n.children || []).filter(k => k.bytes > 0);
      const l = n.label;
      return `<div class="sp-row d${depth}" style="--d:${depth}">
        <button class="sp-node" ${kids.length ? `data-act="space-open" data-p="${esc(n.path)}" aria-expanded="${open}"` : 'disabled'}>
          <span class="chev">${kids.length ? icon('chev') : ''}</span>
          <span class="nm">${esc(l ? l.name : pii(n.name))}${l ? `<i class="kind k-${esc(l.kind)}">${esc(SP_KIND[l.kind] || '')}</i>` : ''}${n.other ? '' : ''}</span>
          <span class="bar"><i style="width:${Math.max(1, n.bytes / max * 100)}%"></i></span>
          <span class="num">${fmtSize(n.bytes)}</span></button>
        ${l && open ? `<p class="advice">${esc(l.advice)}</p>` : ''}
        ${open ? kids.map(k => row(k, depth + 1)).join('') : ''}</div>`;
    };
    return `<div class="sp-tree"><div class="sp-tree-head"><h4>C 盘空间去哪了</h4><span class="muted small" id="sp-status">${esc(status)}</span></div>
      ${running ? `<div class="sp-progress" role="progressbar" aria-valuenow="${pctDone}"><i id="sp-bar" style="width:${pctDone}%"></i></div>
        <p class="muted small">报告已经出来了，这一项在后台继续分析，不影响你看报告和用电脑；分析完这里会自动更新。</p>` : ''}
      ${tree && tree.children ? `<div class="sp-rows">${tree.children.filter(k => k.bytes > 0).map(k => row(k, 0)).join('')}</div>` : ''}
      ${sp && (sp.bigFiles || []).length ? `<details class="fold" data-fold="big-files"><summary>最大的 ${Math.min(10, sp.bigFiles.length)} 个文件</summary><div class="fold-body"><ul class="sp-list">${sp.bigFiles.slice(0, 10).map(f =>
        `<li><span class="mono small">${esc(pii(f.path))}</span><span class="num">${fmtSize(f.bytes)}</span></li>`).join('')}</ul></div></details>` : ''}</div>`;
  }
  /** 「C 盘空间去哪了」现在的进度：进度事件（每秒）优先，否则用报告里存的快照 */
  function spaceStatus() {
    const R = state.report, sp = R.space, prog = state.spaceProg[R.id];
    const running = !!R.spaceRunning && !(sp && sp.finished);
    const scanned = prog ? prog.scannedBytes : sp ? sp.scannedBytes : 0, usedB = prog ? prog.usedBytes : sp ? sp.usedBytes : 0;
    const pctDone = usedB ? Math.min(99, Math.round(scanned / usedB * 100)) : 0;
    const status = running ? `正在分析，已看过 ${fmtSize(scanned)} / ${fmtSize(usedB)}`
      : sp && sp.finished ? `看完了 ${(sp.scannedFiles || 0).toLocaleString()} 个文件${sp.seconds ? `，用了 ${fmtSec(sp.seconds)}` : ''}`
      : sp ? `没扫完（看过 ${fmtSize(sp.scannedBytes)} / ${fmtSize(sp.usedBytes)}），下次体检会重新分析` : '';
    return { running, pctDone, status };
  }
  /** 每秒的进度只改文字和进度条，不重画卡片：用户正在看的展开项、滚动位置都不受影响 */
  function patchSpaceProgress() {
    if (state.view !== 'report' || !document.getElementById('space-card')) return;
    const { pctDone, status } = spaceStatus();
    const s = document.getElementById('sp-status'), b = document.getElementById('sp-bar');
    if (s) s.textContent = status;
    if (b) { b.style.width = pctDone + '%'; b.parentElement.setAttribute('aria-valuenow', pctDone); }
  }
  /** 快照更新时整张卡片重画：先记下哪些折叠项展开着，重画后原样打开（不重放展开动画） */
  function refreshSpace() {
    if (state.view !== 'report') return;
    const el = document.getElementById('space-card');
    if (!el) return;
    const open = [...el.querySelectorAll('details[data-fold]')].filter(d => d.open).map(d => d.dataset.fold);
    const tmp = document.createElement('div'); tmp.innerHTML = spaceCard();
    const fresh = tmp.firstElementChild;
    if (!fresh) return;
    el.innerHTML = fresh.innerHTML;
    el.querySelectorAll('details[data-fold]').forEach(d => { if (open.includes(d.dataset.fold)) { d.classList.add('kept'); d.open = true; } });
    hydrate(el);
  }
  /** 一键加入：只挑低风险、不需要回答、没被标"先不管"或"不对"的；清理缓存用目录表里的默认范围，也一起加 */
  function suggestions() {
    const R = state.report, out = { green: [], yellow: [] };
    if (!R || R.canExecute === false) return out;
    for (const f of R.result.judgement.findings) {
      const k = keyOf(f);
      if (!canDo(f) || state.selected.has(k) || state.dismissed.has(k) || markOf(k) || (f.needsUser && !answerOf(k))) continue;
      const a = altOf(k), op = a ? a.op : f.proposedAction, risk = (a || f).risk;
      if (risk === 'green') out.green.push(k); else if (risk === 'yellow') out.yellow.push(k);
    }
    return out;
  }
  function bulkBar() {
    const s = suggestions(), n = s.green.length + (state.bulkYellow ? s.yellow.length : 0);
    if (!s.green.length && !s.yellow.length) return '';
    return `<div class="bulk"><button class="btn primary" data-act="bulk-add" ${n ? '' : 'disabled'}>${icon('list')}一键加入建议的 ${n} 项</button>
      ${s.yellow.length ? `<label class="check"><input type="checkbox" data-act="bulk-yellow" ${state.bulkYellow ? 'checked' : ''}><span class="box">${icon('check')}</span>也包括 ${s.yellow.length} 项有代价的</label>` : ''}
      <span class="muted small">只是加入处理清单。执行前会逐项列出，再弹 Windows 确认框。</span></div>`;
  }
  function rowHtml(f) {
    const k = keyOf(f), ans = answerOf(k), inPlan = state.selected.has(k);
    const active = state.sheet && state.sheet.kind === 'finding' && state.sheet.key === k;
    const h = handledOf(k);
    const right = h && h.status === 'done' ? `<span class="handled">${icon('check')}已处理</span>`
      : inPlan ? `<span class="in-plan">${icon('check')}已加入</span>`
      : f.needsUser && !ans ? '<span>需要你回答</span>'
      : canDo(f) ? `<span class="risk ${(altOf(k) || f).risk || ''}">${esc(altOf(k) ? altOf(k).name : f.actionName)}</span>`
      : guideOf(f) ? '<span>按步骤处理</span>' : '';
    return `<button class="row ${active ? 'active' : ''} ${state.dismissed.has(k) ? 'dim' : ''} ${state.flash && state.flash.has(k) ? 'flash' : ''}" data-act="open" data-k="${esc(k)}">
      <span class="glyph v-${esc(f.verdict)}">${icon(VICON[f.verdict] || 'dot')}</span>
      <span class="grow"><span class="t"><span>${esc(pii(f.object))}</span>${f.needsUser && !ans ? '<i class="ask-dot" title="需要你回答"></i>' : ''}${markOf(k) ? `<i class="marked">${esc(MARK[markOf(k).kind])}</i>` : ''}${h && h.status === 'failed' ? '<i class="marked">上次没生效</i>' : h && h.status === 'undone' ? '<i class="marked">已撤销</i>' : ''}</span>
        <span class="e">${esc(pii(f.userExplanation))}</span></span>
      <span class="r">${right}${icon('chev')}</span></button>`;
  }
  /** 内测反馈：同意过的自动发送；没同意的可以一键发这一次；没有服务器的构建改为导出文件 */
  function feedbackCard() {
    const R = state.report, s = S();
    if (R.meta.source !== 'local') return '';
    const n = R.unknownObjects || 0;
    const lead = n ? `这次有 ${n} 个 Iris 还不认识的组件，你的反馈能让下一个人得到更准的判断。` : '你的反馈是 Iris 变准的唯一来源。';
    const st = state.fb[R.id] || (R.sent ? { ok: true, message: `已于 ${R.sent} 发送，谢谢你` } : null);
    if (s.betaConsent === 'yes' && s.feedbackServer) {
      return `<section class="card contrib slim"><span class="glyph ${st && st.ok ? 'v-normal' : ''}" style="${st && st.ok ? '' : 'color:var(--accent);background:var(--accent-soft)'}">${icon(st && st.ok ? 'check' : 'spinner')}</span>
        <div><b>内测反馈</b><p>${esc(st ? st.message : '正在把这次去隐私的反馈包发给作者…')}</p></div>
        <div class="acts"><button class="btn ghost" data-act="fb-preview">看看发了什么</button>${st && !st.ok ? '<button class="btn" data-act="fb-send">重新发送</button>' : ''}</div></section>`;
    }
    const action = s.feedbackServer ? '<button class="btn primary" data-act="fb-send">发送这次的反馈</button>' : '<button class="btn primary" data-act="fb-export">导出反馈包</button>';
    return `<section class="card contrib"><span class="glyph" style="color:var(--accent);background:var(--accent-soft)">${icon('spark')}</span>
      <div><b>${s.betaConsent === 'yes' ? '内测反馈' : '帮 Iris 变得更准'}</b>
        <p>${esc(lead)} ${s.feedbackServer ? '点一下就把这次去隐私的反馈包发给作者，不需要注册。' : '这一版还没有连上服务器：导出反馈包，发给作者就行。'}</p></div>
      <div class="acts"><button class="btn ghost" data-act="fb-preview">看看会发什么</button>${action}</div></section>`;
  }
  function managerNote() {
    const fs = state.report.result.judgement.findings;
    return fs.some(f => f.verdict === 'bloat' && /管家|卫士|毒霸|加速|清理大师/.test(f.object + f.userExplanation))
      ? `<div class="card tip"><span class="tag">我们的做法</span><b>${esc(YIN_MANAGER)}</b><p>这次体检里就有常驻后台的同类软件。用不上的话，关掉它的开机自启动，不影响你手动打开。</p></div>` : '';
  }

  // ------------------------------------------------------------------ 右侧面板：结论详情、处理清单
  function openSheet(sheet) {
    state.sheet = sheet;
    renderSheet(); setChrome(); refreshRows();
  }
  function closeSheet() {
    if (!state.sheet) return;
    state.sheet = null;
    const sh = $layer.querySelector('.sheet'), sc = $layer.querySelector('.scrim');
    if (sh) { sh.style.animation = ''; sh.style.transform = ''; sh.classList.add('closing'); sc && sc.classList.add('closing'); setTimeout(() => { if (!state.sheet) $layer.innerHTML = ''; else renderSheet(); }, 260); }
    setChrome(); refreshRows(); renderTray();
  }
  function renderSheet() {
    if (!state.sheet) { $layer.innerHTML = ''; return; }
    const html = findingSheet(state.sheet.key);
    const id = state.sheet.kind + ':' + (state.sheet.key || '');
    let sh = $layer.querySelector('.sheet');
    if (!sh || sh.classList.contains('closing')) {
      $layer.innerHTML = `<div class="scrim" data-act="sheet-close"></div><aside class="sheet" role="dialog" aria-modal="true"></aside>`;
      sh = $layer.querySelector('.sheet');
    }
    const body = sh.querySelector('.sheet-body'), keep = sh.dataset.id === id && body ? body.scrollTop : 0;
    sh.dataset.id = id; sh.innerHTML = html; hydrate(sh);
    const nb = sh.querySelector('.sheet-body'); if (nb) nb.scrollTop = keep;
    renderTray();
  }
  /** 只更新报告列表里的行（选中状态、已加入），不重绘整页，滚动位置不变 */
  function refreshRows() {
    if (state.view !== 'report' || !state.report) return;
    const box = $main.querySelector('.list'); if (!box) return;
    box.innerHTML = visible().map(rowHtml).join('') || '<div class="empty">这一类没有项目</div>';
    hydrate(box);
    const a = box.querySelector('.row.active'); if (a) a.scrollIntoView({ block: 'nearest' });
  }

  function caveat(f) {
    const src = source(f);
    if (src === '知识库') return '这条来自知识库：按文件路径和数字签名识别的已知组件。如果有程序冒用同样的名字和位置，签名校验不会通过，也就不会被这样识别。';
    if (src === 'AI') return 'AI 研判，经过代码校验：能做什么操作、风险等级、红线都由代码决定，AI 只负责解释。AI 可能看错上下文，不同意的话回答它的问题或「重新研判」。';
    return '这条来自固定规则，不结合上下文，可能比 AI 研判粗。';
  }
  /** 详情里说明这一条处理过没有、结果怎样、去哪里撤销 */
  function handledNote(k) {
    const h = handledOf(k); if (!h) return '';
    const go = `<button class="btn small" data-act="nav" data-to="plan">在「处理」页查看${h.status === 'done' ? '或撤销' : ''}</button>`;
    if (h.status === 'done') return `<div class="h-note ok">${icon('check')}<div><b>已于 ${esc(h.time)} 处理</b><span>${esc(h.message || '重新检测确认生效')}</span></div>${go}</div>`;
    if (h.status === 'undone') return `<div class="h-note">${icon('undo')}<div><b>处理过，${esc(h.message)}</b><span>需要的话可以重新加入处理清单。</span></div>${go}</div>`;
    return `<div class="h-note warn">${icon('alert')}<div><b>${esc(h.time)} 处理过，但没有生效</b><span>${esc(h.message || '没有留下改动')}。可以再试一次，或者按说明手动处理。</span></div>${go}</div>`;
  }
  function findingSheet(k) {
    const f = findingByKey(k); if (!f) return '';
    const list = visible(), idx = list.findIndex(x => keyOf(x) === k);
    const ans = answerOf(k), alt = altOf(k), guide = guideOf(f), R = state.report;
    const inPlan = state.selected.has(k), doable = canDo(f), cross = R.crossCheck && f.flagIds.map(id => R.crossCheck[id]).find(Boolean);
    const tip = (R.result.judgement.tips || []).find(t => (t.relatedFlagIds || []).some(id => f.flagIds.includes(id)));
    const risk = (alt || f).risk;
    let foot;
    if (doable) foot = inPlan
      ? `<button class="btn" data-act="unplan" data-k="${esc(k)}">从清单移出</button><button class="btn solid" data-act="nav" data-to="plan">去「处理」页（${state.selected.size} 项）<span class="arrow">${icon('arrow')}</span></button>`
      : `${state.dismissed.has(k) ? `<button class="btn ghost" data-act="undismiss" data-k="${esc(k)}">取消“先不管”</button>` : `<button class="btn ghost" data-act="dismiss" data-k="${esc(k)}">先不管</button>`}<button class="btn primary" data-act="plan-add" data-k="${esc(k)}">${icon('list')}加入处理清单</button>`;
    else if (guide) foot = `<button class="btn ghost" data-act="next">下一项</button><button class="btn primary" data-act="defender">${esc(guide.btn)} <span class="arrow">${icon('arrow')}</span></button>`;
    else if (f.needsUser && !ans) foot = `<span class="sum">回答之后，下次研判会参考你的说明</span><button class="btn ghost" data-act="next">跳过 · 下一项</button>`;
    else foot = `<button class="btn solid" data-act="next" ${idx >= list.length - 1 ? 'data-last="1"' : ''}>${idx >= list.length - 1 ? '知道了' : '知道了 · 下一项'}</button>`;
    return `<div class="sheet-top">
        <button class="icon-btn" data-act="sheet-close" aria-label="关闭（Esc）">${icon('x')}</button>
        <span class="pos">${idx + 1} / ${list.length}</span>
        <button class="icon-btn" data-act="prev" aria-label="上一项（↑）" ${idx <= 0 ? 'disabled' : ''}>${icon('up')}</button>
        <button class="icon-btn" data-act="next" aria-label="下一项（↓）" ${idx >= list.length - 1 ? 'disabled' : ''}>${icon('down')}</button></div>
      <div class="sheet-body">
        <div class="chips"><span class="chip v-${esc(f.verdict)}">${VERDICT[f.verdict] || esc(f.verdict)}</span><span class="chip src">${source(f)}</span>
          ${(R.result.confirmedFlagIds || []).some(id => f.flagIds.includes(id)) ? '<span class="chip src">你确认过</span>' : ''}</div>
        <h2>${esc(pii(f.object))}</h2>
        <p class="lead">${esc(pii(f.userExplanation))}</p>
        ${handledNote(k)}
        ${f.needsUser && !ans ? `<div class="question"><div class="q">${icon('ask')}<span>${esc(pii(f.questionForUser))}</span></div>
          <div class="answers"><button class="btn" data-act="answer" data-k="${esc(k)}" data-a="yes">是，我知道</button>
            <button class="btn" data-act="answer" data-k="${esc(k)}" data-a="no">不是 / 不认识</button></div>
          <input class="input" id="note-${esc(k)}" data-note="${esc(k)}" placeholder="补一句说明（可不填），例如：自己装来远程帮爸妈的"></div>` : ''}
        ${ans ? `<div class="answered">${icon('check')}<span>已记录你的回答：${ans.answer === 'yes' ? '是，我知道' : '不是 / 不认识'}${ans.note ? '（' + esc(ans.note) + '）' : ''}。下次研判时生效。</span></div>` : ''}
        ${alt ? `<div class="alt">既然你不认识它，可以「${esc(alt.name)}」。改之前会备份，可以撤销。</div>` : ''}
        ${guide ? `<div class="guide"><h4 style="margin:0">怎么处理</h4><div class="steps">${esc(guide.steps)}</div></div>` : ''}
        <div><h4>为什么这么判断</h4><div class="evidence">${esc(pii(f.evidence))}</div></div>
        <div class="spec">
          <div class="k">判断来源</div><div class="v">${source(f) === '知识库' ? '知识库 · 已弄清楚的常见组件' : source(f) === 'AI' ? 'AI 研判 · 代码校验' : '固定规则'}</div>
          <div class="k">把握程度</div><div class="v" style="display:flex;gap:10px;align-items:center;justify-content:flex-end"><span class="bar" style="width:90px"><i style="width:${Math.round(f.confidence * 100)}%"></i></span><span class="num">${Math.round(f.confidence * 100)}%</span></div>
          ${cross ? `<div class="k">交叉校验</div><div class="v">${esc(cross)}</div>` : ''}
          <div class="k">建议</div><div class="v">${isDone(k) ? `${esc(alt ? alt.name : f.actionName)}（已处理）` : doable || guide ? esc(alt ? alt.name : f.actionName) : f.needsUser && !ans ? '先回答上面的问题' : '不需要操作'}</div>
          ${risk ? `<div class="k">风险</div><div class="v"><span class="risk ${risk}">${RISK[risk] || risk}</span></div>` : ''}
          ${doable ? `<div class="k">能否撤销</div><div class="v">${(alt ? alt.op : f.proposedAction) === 'app.uninstall' ? '不能自动撤销（需要重新安装）' : '可以，一键还原'}</div>` : ''}
          ${state.pro ? `<div class="k">预筛编号</div><div class="v mono">${esc(f.flagIds.join(' '))} · ${esc(f.proposedAction)}${f.kbId ? ' · ' + esc(f.kbId) : ''}</div>` : ''}
        </div>
        <div class="caveat">${esc(caveat(f))}</div>
        ${R.meta.source === 'local' ? markBlock(k) : ''}
        ${tip && S().showTips !== false ? `<div class="card tip" style="box-shadow:none"><span class="tag">小知识</span><b>${esc(tip.title)}</b><p>${esc(tip.body)}</p></div>` : ''}
      </div>
      <div class="sheet-foot">${foot}</div>`;
  }

  /** "这条判断对吗？"：不对 / 不太准 + 一句说明。内测同意过的，提交后顺手发给作者 */
  function markBlock(k) {
    const m = markOf(k);
    if (m) return `<div class="mark done">${icon('check')}<span class="txt">已记录你的反馈：${esc(MARK[m.kind])}${m.note ? '（' + esc(m.note) + '）' : ''}。${m.sent ? '已发给作者，谢谢你。' : ''}</span>
      <button class="linkish" data-act="mark-undo" data-k="${esc(k)}">撤回</button></div>`;
    if (state.markOpen !== k) return `<div class="mark"><span>这条判断对吗？</span>
      <button class="btn ghost sm" data-act="mark-open" data-k="${esc(k)}" data-kind="wrong">不对</button>
      <button class="btn ghost sm" data-act="mark-open" data-k="${esc(k)}" data-kind="inaccurate">不太准</button></div>`;
    return `<div class="mark open">
      <div class="seg" role="group" aria-label="哪里不对">${['wrong', 'inaccurate'].map(x =>
        `<button data-act="mark-kind" data-kind="${x}" aria-pressed="${state.markKind === x}">${MARK[x]}</button>`).join('')}</div>
      <textarea class="input" id="mark-note" rows="3" placeholder="哪里不对？例如：这是我自己装的远程软件，不是可疑程序"></textarea>
      <div class="acts"><button class="btn ghost" data-act="mark-cancel">取消</button><button class="btn solid" data-act="mark-submit" data-k="${esc(k)}">提交反馈</button></div>
      <span class="muted small">${S().betaConsent === 'yes' && S().feedbackServer ? '提交后会连同这条结论一起发给作者（不含整份报告）。' : '只记在这台电脑上；发送或导出反馈包时会带上。'}</span></div>`;
  }

  // ------------------------------------------------------------------ 处理：待处理 → 执行 → 刚刚完成 → 已处理（随时可以撤销）
  // 同一条结论从"加入清单"到"撤销"都在这一页里看得到；报告里每条结论也标着它现在的处理状态
  const LATER = ['browser.disable_background', 'service.set_manual'];  // 下次开机后才看得到效果
  const JST = { done: ['ok', '已生效', 'check'], info: ['ok', '已完成', 'check'], rolledBack: ['fail', '没生效，已自动恢复', 'undo'], failed: ['fail', '没有执行', 'alert'], undone: ['undone', '已撤销', 'undo'] };
  const fkOf = (s) => s.findingKey || String(s.key).split('#')[0];
  function viewPlan() {
    if (state.loading) return `<div class="page"><div class="skel" style="height:90px"></div><div class="skel" style="height:240px"></div></div>`;
    return `<div class="page plan-page">
      <div><h1>处理</h1><p class="muted" style="margin:8px 0 0;max-width:48em">要改什么由你决定。改之前逐项写清会改什么、有什么代价、怎么撤销；改完重新检测，没生效的自动恢复原状。所有改动都记在下面，随时可以撤销。</p></div>
      <div id="plan-result">${resultCard()}</div>
      <section id="plan-pending">${pendingSection()}</section>
      <section><div class="section-head"><h2>已处理 · 可以撤销</h2></div>
        <div class="history">${historyHtml(state.journal || [])}</div></section>
    </div>`;
  }
  function pendingSection() {
    const R = state.report, P = state.plan, n = state.selected.size;
    const head = `<div class="section-head"><h2>待处理${n ? ` · <span class="num">${n}</span> 项` : ''}</h2></div>`;
    if (R && R.canExecute === false) return `${head}<div class="card empty">${icon('inbox')}<b>当前打开的是朋友电脑的报告</b>
      <span>别的电脑的报告只能查看，不能在这台电脑上执行。</span>${state.app.lastReport ? '<button class="btn" data-act="my-report">回到这台电脑的报告</button>' : ''}</div>`;
    if (!n) return `${head}<div class="card empty">${icon('list')}<b>清单是空的</b>
      <span>在报告里点开一条结论，按「加入处理清单」；也可以用报告里的「一键加入建议的项」。体检只读，不会自动改动你的电脑。</span>
      ${R ? '<button class="btn" data-act="nav" data-to="report">去看报告</button>' : ''}</div>`;
    const carried = state.carried ? `<p class="muted small carried">${icon('info')}从上次体检的清单里带过来 ${state.carried.n} 项${state.carried.dropped ? `；另有 ${state.carried.dropped} 项这次体检已经没有了，不用再处理` : ''}。</p>` : '';
    if (!P) return `${head}${carried}<div class="skel" style="height:120px"></div><div class="skel" style="height:120px"></div>`;
    const missing = P.steps.find(s => s.cacheOptions && !ccOf(s).categories.length);
    const irreversible = P.steps.filter(s => s.reversible === false).length;
    const statuses = Object.values(state.planStatus), finished = statuses.filter(x => ['done', 'info', 'failed', 'rolledBack', 'skipped'].includes(x.status)).length;
    let bar;
    if (state.confirming) bar = `<span class="sum"><b>等待你确认</b>Windows 确认框里逐项写着会改什么；点「取消」就什么也不做</span><button class="btn primary" disabled>${icon('spinner')}等待确认…</button>`;
    else if (state.executing) bar = `<span class="sum"><b>正在执行 ${Math.min(finished + 1, P.steps.length)} / ${P.steps.length}</b>每改完一项都会重新检测；请不要关闭 Iris</span><button class="btn primary" disabled>${icon('spinner')}执行中…</button>`;
    else bar = `<span class="sum"><b>${P.steps.length} 项</b>${irreversible ? `${irreversible} 项不能自动撤销` : '全部可以撤销'}${P.needsAdmin ? ' · 需要一次管理员确认' : ''}${missing ? `<br><span style="color:var(--c-prob)">「${esc(pii(missing.title))}」一类都没选：选一类，或者把这一项移出清单</span>` : ''}</span>
      <button class="btn primary" data-act="execute" ${missing || !P.steps.length ? 'disabled' : ''}>执行 ${P.steps.length} 项 <span class="arrow">${icon('arrow')}</span></button>`;
    return `${head}${carried}<div class="steps">${P.steps.map(stepCard).join('')}</div>
      ${P.skipped && P.skipped.length ? `<div class="card pad"><h4 style="margin:0 0 8px">这些不会处理</h4><ul class="deduct">${P.skipped.map(x => `<li><b>${esc(pii(x.title))}</b>：${esc(x.reason)}</li>`).join('')}</ul></div>` : ''}
      <div class="plan-bar">${bar}</div>`;
  }
  function stepCard(s) {
    const st = state.planStatus[s.key];
    const stTxt = st ? { running: ['run', '修改中', 'spinner'], verifying: ['run', '重新检测中', 'spinner'], done: ['ok', '已生效', 'check'], info: ['ok', '已完成', 'check'],
      failed: ['fail', '没有执行', 'alert'], rolledBack: ['fail', '没生效，已恢复', 'undo'], skipped: ['undone', '已跳过', 'dot'] }[st.status] : null;
    const cc = s.cacheOptions ? ccOf(s) : null;
    const locked = state.executing;
    return `<div class="step-card">
      <div class="hd"><span class="risk ${s.risk || ''}"></span><b>${esc(s.opName)}：${esc(pii(s.title))}</b>
        ${stTxt ? `<span class="status ${stTxt[0]}">${icon(stTxt[2])}${esc(stTxt[1])}</span>` : locked ? '' : `<button class="x" data-act="unplan" data-k="${esc(fkOf(s))}" aria-label="从清单移出">${icon('x')}</button>`}</div>
      <div class="spec"><div class="k">会改什么</div><div class="v">${esc(s.change)}</div>
        <div class="k">代价</div><div class="v">${esc(s.cost)}</div>
        <div class="k">怎么撤销</div><div class="v">${esc(s.undo)}</div>
        ${st && st.message && st.status !== 'running' && st.status !== 'verifying' ? `<div class="k">结果</div><div class="v">${esc(st.message)}</div>` : ''}</div>
      ${s.cacheOptions && !locked ? `<div class="opts">
        <span class="label">清理这些（体检时量到的大小；不需要的点一下取消）</span>
        <div class="cc-items">${s.cacheOptions.items.map(c => `<button class="cc-item" data-act="cc-cat" data-k="${esc(s.key)}" data-v="${esc(c.id)}" aria-pressed="${cc.categories.includes(c.id)}" title="${esc(c.why + ' ' + c.cost)}">
          <span class="box">${icon('check')}</span><span class="nm">${esc(c.name)}${c.running ? `<i class="muted small">体检时 ${esc(c.running)} 开着，清理时还开着就跳过</i>` : ''}</span>
          <span class="num">${c.sizeMB == null ? '' : fmtMB(c.sizeMB)}</span></button>`).join('')}</div>
        <details class="cc-more"><summary>只清更早的（可选）</summary>
          <div class="choices">${s.cacheOptions.ages.map(d => `<button data-act="cc-age" data-k="${esc(s.key)}" data-v="${d}" aria-pressed="${(cc.age || 0) === d}">${d ? d + ' 天前' : '按默认'}</button>`).join('')}</div>
          <span class="muted small">默认：临时文件只清 7 天前的、旧日志只清 30 天前的，其余缓存不限时间。</span></details>
      </div>` : ''}
    </div>`;
  }
  /** 清理缓存这一步当前的选择：没动过就是方案里的默认（全选） */
  function ccOf(s) {
    return state.cacheChoice[s.key] = state.cacheChoice[s.key] || { categories: (s.cacheOptions.categories || []).slice(), age: null };
  }
  /** 刚执行完的那一批：一句话说清结果，再给下一步 */
  function resultCard() {
    const L = state.lastRun; if (!L) return '';
    const items = (state.journal || []).filter(e => e.batchId === L.batchId).reverse();  // 记录是新的在前，这里按执行顺序
    if (!items.length) return '';
    const ok = items.filter(e => e.status === 'done' || e.status === 'info').length, undone = items.filter(e => e.status === 'undone').length;
    const bad = items.length - ok - undone;
    const later = items.filter(e => e.status === 'done' && LATER.includes(e.op)).length;
    const undoable = items.filter(e => e.status === 'done' && e.reversible).length;
    const parts = [ok && `${ok} 项已生效`, bad && `${bad} 项没生效、已恢复原状`, undone && `${undone} 项已撤销`].filter(Boolean).join('，');
    return `<section class="card result">
      <div class="r-top"><span class="glyph ${bad && !ok ? 'v-problem' : 'v-normal'}">${icon(bad && !ok ? 'alert' : 'check')}</span>
        <div><div class="eyebrow">刚刚完成 · ${esc(L.time)}</div><b>处理了 ${items.length} 项：${parts}</b>
          <p>每一项都重新检测过${later ? `；其中 ${later} 项下次开机后才能看到效果` : ''}。报告里这些结论已经标上「已处理」，下面的记录里随时可以撤销。</p></div>
        <button class="icon-btn" data-act="result-close" aria-label="收起">${icon('x')}</button></div>
      <ul class="r-list">${items.map(e => { const t = JST[e.status] || ['', e.status, 'dot'];
        return `<li><span class="status ${t[0]}">${icon(t[2])}${esc(t[1])}</span><span class="grow">${esc(e.opName)}：${esc(pii(e.title))}</span></li>`; }).join('')}</ul>
      <div class="r-actions"><button class="btn primary" data-act="start">${icon('pulse')}重新体检，看看变化</button>
        ${undoable ? `<button class="btn" data-act="undo-batch" data-batch="${esc(L.batchId)}">${icon('undo')}一键还原这一批</button>` : ''}
        <button class="btn ghost" data-act="nav" data-to="report">回到报告</button></div></section>`;
  }
  function historyHtml(J) {
    if (!J.length) return `<div class="card empty">${icon('undo')}<span>还没有任何改动。体检只读，不会自动改动你的电脑。</span></div>`;
    return batches(J).map(b => {
      const undoable = b.items.filter(e => e.status === 'done' && e.reversible).length;
      return `<section class="batch ${state.lastRun && b.id === state.lastRun.batchId ? 'fresh' : ''}"><div class="batch-head"><span class="t">${esc(b.items[0].time)} · 这一次处理了 ${b.items.length} 项</span>
        ${undoable > 1 && b.id ? `<button class="btn" data-act="undo-batch" data-batch="${esc(b.id)}">${icon('undo')}一键还原这 ${undoable} 项</button>` : ''}</div>
        <div class="card">${b.items.map(e => {
          const t = JST[e.status] || ['', e.status, 'dot'];
          return `<div class="j-row"><span class="glyph ${t[0] === 'ok' ? 'v-normal' : t[0] === 'fail' ? 'v-problem' : 'v-uncertain'}" style="width:34px;height:34px">${icon(t[2])}</span>
            <div class="grow"><div><b style="font-weight:500">${esc(e.opName)}</b>：${esc(pii(e.title))}</div><div class="m"><span class="status ${t[0]}">${esc(t[1])}</span>${e.message ? ' · ' + esc(pii(e.message)) : ''}</div></div>
            ${e.status === 'done' && e.reversible ? `<button class="btn" data-act="undo" data-id="${esc(e.id)}">${icon('undo')}撤销</button>`
              : e.status === 'done' && !e.reversible ? `<span class="muted small">${esc(e.undoHint || '这一步不能自动撤销')}</span>` : '<span></span>'}
          </div>`; }).join('')}</div></section>`; }).join('');
  }
  /** 执行过程中只重画"待处理"这一块，页面不跳动 */
  function renderPlan() {
    if (state.view !== 'plan') return;
    const box = document.getElementById('plan-pending'); if (!box) { render(); return; }
    box.innerHTML = pendingSection(); hydrate(box); glide(box);
  }
  /** 清单存进报告旁边的 selected.json：关掉 Iris 再打开还在 */
  function saveSel() {
    const R = state.report; if (!R || R.canExecute === false) return;
    quiet('setSelected', { reportId: R.id, keys: [...state.selected] });
    if (state.app.lastReport && state.app.lastReport.id === R.id) state.app.pending = state.selected.size;
  }
  /** 执行或撤销之后：重新读操作记录、报告里每条结论的处理状态、概览页的提示 */
  async function refreshAfterChange() {
    state.journal = (await quiet('getJournal')) || state.journal;
    if (state.report) { const r = await quiet('getReport', { id: state.report.id }); if (r) { state.report.handled = r.handled; } }
    const a = await quiet('getState'); if (a) state.app = a;
  }

  async function ensurePlan() {
    const keys = [...state.selected].sort().join('|');
    if (state.executing || !state.report || state.report.canExecute === false) return;
    if (!keys) { state.plan = null; state.planKeys = ''; return; }
    if (state.plan && state.planKeys === keys) return;
    state.planKeys = keys; state.plan = null;
    const p = await call('buildPlan', { reportId: state.report.id, keys: [...state.selected] });
    if (state.planKeys !== keys) return;
    state.plan = p; state.planStatus = {};
    renderPlan();
  }

  /** 悬浮的处理清单托盘：选了东西就一直在，告诉用户下一步点哪里 */
  function renderTray() {
    let t = document.getElementById('tray');
    const n = state.selected.size;
    const show = n && !state.sheet && state.view !== 'running' && state.view !== 'plan' && (!state.report || state.report.canExecute !== false);
    if (!show) { if (t) t.remove(); return; }
    const html = `<span class="n">${n}</span><span class="txt">处理清单 <span>· 改之前会逐项说明，可撤销</span></span>
      <button class="btn primary" data-act="nav" data-to="plan">检查并执行 <span class="arrow">${icon('arrow')}</span></button>`;
    if (!t) { t = document.createElement('div'); t.id = 'tray'; t.className = 'tray'; document.getElementById('app').appendChild(t); }
    const prev = t.dataset.n; t.dataset.n = n;
    t.innerHTML = html;
    if (prev && prev !== String(n)) bump(t.querySelector('.n'));
  }

  function batches(J) {
    const out = [], byId = {};
    for (const e of J) {
      const k = e.batchId || e.id;
      if (!byId[k]) { byId[k] = { id: e.batchId, items: [] }; out.push(byId[k]); }
      byId[k].items.push(e);
    }
    return out;
  }

  // ------------------------------------------------------------------ 设置
  const PROVIDERS = {
    deepseek: { name: 'DeepSeek', baseUrl: 'https://api.deepseek.com', model: 'deepseek-chat' },
    kimi: { name: 'Kimi（月之暗面）', baseUrl: 'https://api.moonshot.cn/v1', model: 'kimi-k2-0905-preview' },
    qwen: { name: '通义千问', baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1', model: 'qwen-plus' },
    custom: { name: '自定义（OpenAI 兼容接口）', baseUrl: '', model: '' },
  };
  const THEMES = [['dark', '余烬', 'ember'], ['space', '深空', 'space'], ['light', '米白', 'paper'], ['auto', '跟随系统', 'auto']];
  function swRow(ic, title, desc, key, extra) {
    return `<div class="set-row"><span class="ic">${icon(ic)}</span><div><b>${title}</b><p>${desc}</p>${extra || ''}</div>
      <label class="switch"><input type="checkbox" id="set-${key}" data-set="${key}" ${S()[key] ? 'checked' : ''}><span class="track"></span><span class="sr">${title}</span></label></div>`;
  }
  function btnRow(ic, title, desc, btn) {
    return `<div class="set-row"><span class="ic">${icon(ic)}</span><div><b>${title}</b><p>${desc}</p></div>${btn}</div>`;
  }
  function viewSettings() {
    const s = S();
    return `<div class="page" style="max-width:820px">
      <h1>设置</h1>
      <section class="set-group"><h2>外观</h2><div class="card set-card">
        <div class="themes">${THEMES.map(([k, n, pv]) => `<button class="theme-pick" data-act="theme" data-v="${k}" aria-pressed="${(s.theme || 'dark') === k}">
          <span class="pv ${pv}"><i class="a"></i><i class="b"></i><i class="c"></i><i class="d"></i></span>${n}</button>`).join('')}</div>
        ${swRow('motion', '减少动效', '关掉滑入、数字滚动等动画。Windows 里关闭了动画效果时会自动减少。', 'reduceMotion')}
        ${swRow('pro', '专业模式', '显示规则编号、知识库条目和模型输出的校验记录。', 'pro')}
      </div></section>

      <section class="set-group"><h2>AI 研判</h2><div class="card set-card">
        <div class="set-row"><span class="ic">${icon('key')}</span><div><b>${s.hasKey ? `已保存 API Key（末尾 ${esc(s.keyTail)}）` : '还没有 API Key'}</b>
          <p>只保存在这台电脑上，用 Windows 自带的 DPAPI 加密，只有你的账户能解开。Key 不会发给 Iris 作者。</p></div><span></span></div>
        <div class="set-form fee">${esc(AI_FEE)}</div>
        <div class="set-form">
          <div class="field"><label for="s-provider">服务商</label><select class="input" id="s-provider" data-act="provider">
            ${Object.entries(PROVIDERS).map(([k, p]) => `<option value="${k}" ${s.provider === k ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select></div>
          <div class="row2"><div class="field"><label for="s-base">接口地址</label><input class="input" id="s-base" value="${esc(s.baseUrl)}"></div>
            <div class="field"><label for="s-model">模型</label><input class="input" id="s-model" value="${esc(s.model)}"></div></div>
          <div class="field"><label for="s-key">API Key</label><input class="input" id="s-key" type="password" autocomplete="off" placeholder="${s.hasKey ? '留空表示不修改' : '粘贴你的 API Key'}"></div>
          <div style="display:flex;gap:8px;flex-wrap:wrap"><button class="btn solid" data-act="save-settings">保存</button><button class="btn" data-act="test-conn">测试连接</button></div>
        </div></div></section>

      <section class="set-group"><h2>体检</h2><div class="card set-card">
        ${swRow('scan', '默认先用 Defender 快速扫描', '体检前让 Windows 自带的 Microsoft Defender 快速扫描一遍，多 1–5 分钟。', 'defaultScan')}
      </div></section>

      <section class="set-group"><h2>内测反馈</h2><div class="card set-card">
        <div class="set-row"><span class="ic">${icon('spark')}</span><div><b>体检后自动发送去隐私的反馈包</b>
          <p>${s.feedbackServer ? '发到 Iris 作者的服务器（阿里云中国香港）。不需要注册，随时可以关掉；关掉后照常使用全部功能。' : '这一版还没有连上服务器：打开后可以在报告页导出反馈包，发给作者。'}
          ${state.app.outbox ? `还有 ${state.app.outbox} 个反馈包等联网后补发。` : ''}</p></div>
          <label class="switch"><input type="checkbox" id="set-betaConsent" data-set="betaConsent" ${s.betaConsent === 'yes' ? 'checked' : ''}><span class="track"></span><span class="sr">自动发送反馈</span></label></div>
        ${btnRow('eye', '看看会发什么', '列出反馈包里的每一部分，以及不会发送的内容。', '<button class="btn" data-act="fb-preview">查看</button>')}
        ${state.app.lastReport ? btnRow('folder', '导出最近一次的反馈包', '保存到桌面，可以用微信发给作者。', '<button class="btn" data-act="fb-export-last">导出</button>') : ''}
      </div></section>

      <section class="set-group"><h2>存储空间</h2><div class="card set-card" id="storage">${storageRows()}</div></section>

      <section class="set-group"><h2>隐私与数据</h2><div class="card set-card">
        ${swRow('eyeoff', '演示模式', '界面上遮住用户名、电脑名和邮箱，截图发给别人也不泄露。', 'demoMode')}
        ${swRow('cloud', '允许使用 Iris 托管服务', `免去自己申请 API Key。默认关闭：关闭时只用你自己的 Key，或只做本地检查。${s.hostedAvailable ? '' : '托管服务还没有开放。'}`, 'allowHosted')}
        ${btnRow('lock', '隐私政策', '我们收集什么、发给谁、存多久。', '<button class="btn" data-act="show-privacy">查看</button>')}
        ${btnRow('folder', '数据文件夹', '体检报告、操作记录、设置都在这台电脑的 %LOCALAPPDATA%\\pcdoctor。', '<button class="btn" data-act="open-data">打开</button>')}
        ${btnRow('trash', '清除所有数据', '删除报告、操作记录、设置和 API Key。不影响已经做过的修改。', '<button class="btn danger" data-act="clear-data">清除</button>')}
      </div></section>

      <section class="set-group"><h2>教程与小知识</h2><div class="card set-card">
        ${swRow('book', '显示教程卡片', `概览页的「认识 Iris」五课。${s.showLessons ? `现在在第 ${Math.min((s.lessonIndex || 0) + 1, 5)} 课。` : ''}`, 'showLessons')}
        ${swRow('info', '在报告里穿插小知识', '和这台电脑相关的避坑知识，例如“关机不等于重启”。', 'showTips')}
        ${btnRow('undo', '从头再看一遍教程', '把教程卡片重置到第一课。', '<button class="btn" data-act="lesson-reset">重新开始</button>')}
        ${btnRow('pulse', '重新运行快速设定', '开场、AI 设置、外观，再走一遍。', '<button class="btn" data-act="ob-start">运行</button>')}
      </div></section>

      <section class="set-group"><h2>关于</h2><div class="card set-card">
        ${btnRow('code', `Iris ${esc(state.app.version)}`, '以 AGPL-3.0 协议开源。不常驻、不弹窗、不捆绑；查病毒交给 Microsoft Defender。', '<button class="btn" data-act="open-repo">源码</button>')}
      </div></section>
    </div>`;
  }

  const fmtBytes = b => b == null ? '…' : b < 1048576 ? `${Math.max(1, Math.round(b / 1024))} KB` : `${(b / 1048576).toFixed(1)} MB`;
  function storageRows() {
    const st = state.storage;
    if (!st) { if (!state.storageLoading) { state.storageLoading = true; quiet('storageInfo').then(r => { state.storageLoading = false; state.storage = r || {}; const el = document.getElementById('storage'); if (el) { el.innerHTML = storageRows(); hydrate(el); glide(el); } }); }
      return '<div class="set-row"><span class="ic">' + icon('folder') + '</span><div><div class="skel" style="height:14px;width:40%"></div></div><span></span></div>'; }
    const r = st.reports || {}, b = st.backups || {}, w = st.webview || {}, o = st.outbox || {}, days = state.pruneDays || 90;
    return btnRow('doc', `体检报告　<span class="num muted">${r.count || 0} 份 · ${fmtBytes(r.bytes)}</span>`, '保留这台电脑最近 5 份报告；朋友发来的报告，打开超过 30 天的一起删除。', '<button class="btn" data-act="clean" data-kind="reports">清理</button>')
      + btnRow('undo', `操作备份　<span class="num muted">${b.count || 0} 条 · ${fmtBytes(b.bytes)}</span>`, `撤销过的、没生效的步骤的备份，以及所选天数以前的备份。最近一次执行的那一批始终保留；清理以后，这些步骤不能再一键撤销。${b.admin ? `其中 ${b.admin} 条由管理员权限保存，清理时会请求管理员权限。` : ''}`,
        `<span class="set-actions"><span class="seg-mini" data-glide="prune" role="group" aria-label="多少天以前">${[30, 90, 180].map(d => `<button data-act="prune-days" data-v="${d}" aria-pressed="${days === d}">${d} 天</button>`).join('')}</span><button class="btn" data-act="clean" data-kind="backups">清理</button></span>`)
      + btnRow('layers', `界面缓存　<span class="num muted">${fmtBytes(w.bytes)}</span>`, w.pending ? '已安排：下次启动 Iris 时清理。' : '显示界面用的浏览器缓存。Iris 运行时被占用，下次启动时再删除，会自动重新生成。', `<button class="btn" data-act="clean" data-kind="webview" ${w.pending ? 'disabled' : ''}>${w.pending ? '已安排' : '清理'}</button>`)
      + btnRow('cloud', `待发送的反馈　<span class="num muted">${o.count || 0} 个</span>`, '没联网时攒下的内测反馈包，联网后会自动补发。删除后就不会再发出去。', `<button class="btn" data-act="clean" data-kind="outbox" ${o.count ? '' : 'disabled'}>删除</button>`);
  }

  // ------------------------------------------------------------------ 弹窗：隐私政策、贡献预览
  /** 只认标题、列表、表格、粗体、行内代码和分隔线的小型 Markdown 渲染；所有文字先转义 */
  function md(text) {
    const inline = s => esc(s).replace(/\*\*(.+?)\*\*/g, '<b>$1</b>').replace(/`(.+?)`/g, '<code>$1</code>').replace(/\[([^\]]+)\]\(#[^)]*\)/g, '$1');
    const out = []; let list = false, table = null;
    const flush = () => { if (list) { out.push('</ul>'); list = false; } if (table) { out.push('<table>' + table.join('') + '</table>'); table = null; } };
    for (const raw of text.split(/\r?\n/)) {
      const line = raw.trimEnd();
      if (/^\|/.test(line)) {
        if (/^\|\s*-/.test(line)) continue;
        const cells = line.replace(/^\||\|$/g, '').split('|').map(c => inline(c.trim()));
        if (!table) { flush(); table = [`<tr>${cells.map(c => `<th>${c}</th>`).join('')}</tr>`]; } else table.push(`<tr>${cells.map(c => `<td>${c}</td>`).join('')}</tr>`);
        continue;
      }
      if (table) flush();
      let m;
      if ((m = line.match(/^(#{1,3}) (.*)/))) { flush(); out.push(`<h${m[1].length + 1}>${inline(m[2])}</h${m[1].length + 1}>`); }
      else if ((m = line.match(/^- (.*)/))) { if (!list) { out.push('<ul>'); list = true; } out.push(`<li>${inline(m[1])}</li>`); }
      else if (/^---$/.test(line)) { flush(); out.push('<hr>'); }
      else if (line) { flush(); out.push(`<p>${inline(line)}</p>`); }
      else flush();
    }
    flush();
    return out.join('');
  }
  function modal(title, sub, body, foot) {
    const box = document.createElement('div');
    box.className = 'modal-back';
    box.innerHTML = `<div class="modal" role="dialog" aria-modal="true" aria-label="${esc(title)}">
      <div class="modal-head"><h2>${esc(title)}</h2>${sub ? `<span class="muted small">${sub}</span>` : ''}</div>
      <div class="modal-body doc" tabindex="0">${body}</div><div class="modal-foot">${foot}</div></div>`;
    document.body.appendChild(box);
    hydrate(box);
    return box;
  }
  async function showPrivacy(firstRun) {
    const p = await call('getPrivacy');
    const box = modal(firstRun ? '开始之前，先看看我们怎么对待你的数据' : '隐私政策', `版本 ${esc(p.version)}`, md(p.text), firstRun
      ? '<button class="btn ghost" data-m="decline">不同意，退出</button><button class="btn primary" data-m="accept">同意并继续</button>'
      : '<button class="btn solid" data-m="close">关闭</button>');
    box.querySelector('.modal-body').focus();
    return new Promise(resolve => box.addEventListener('click', async e => {
      const b = e.target.closest('[data-m]'); if (!b) return;
      if (b.dataset.m === 'accept') await call('acceptPrivacy');
      if (b.dataset.m === 'decline') { await call('declinePrivacy'); return; }
      box.remove(); resolve();
    }));
  }
  async function previewFeedback() {
    const id = state.report && state.report.meta.source === 'local' ? state.report.id : (state.app.lastReport && state.app.lastReport.id);
    const p = await call('previewFeedback', { reportId: id || null });
    const items = p.items.length ? p.items : ['体检结果（软件名称、版本、厂商、安装位置）、研判结论、你的回答和“判断不对”的标记、用时和错误记录、「处理」页的操作和结果。做完第一次体检后，这里会列出具体内容。'];
    modal('反馈包里有什么', `${p.server ? '发送到 ' + esc(p.server) : '导出为文件，由你发给作者'}${p.sizeKB ? ' · 压缩后约 ' + p.sizeKB + ' KB' : ''} · 不需要注册`,
      `<h4>会发送</h4><div class="payload">${items.map(x => `<div class="item">${esc(x)}</div>`).join('')}</div>
       <h4>不会发送</h4><ul>${p.excluded.map(x => `<li>${esc(x)}</li>`).join('')}</ul>
       <p class="muted small">反馈只用来改进 Iris 的判断：看哪些结论错了、哪些组件 Iris 还不认识。内测结束后删除。</p>`,
      '<button class="btn solid" data-m="close">知道了</button>').addEventListener('click', e => { if (e.target.closest('[data-m]')) e.currentTarget.remove(); });
  }
  async function sendFeedback(id, exportOnly) {
    const r = await call(exportOnly || !S().feedbackServer ? 'exportFeedback' : 'sendFeedback', { reportId: id });
    state.fb[id] = r; toast(r.message || '已发送');
    if (state.view === 'report') render();
  }

  // ------------------------------------------------------------------ 首次使用的快速设定
  // 开场 → 怎么做 → 隐私（必须） → 内测反馈（必须选，同意 / 不同意都有 2 秒倒计时） → AI（可跳过） → 外观（可跳过） → 完成
  const OB = ['hello', 'promise', 'privacy', 'consent', 'ai', 'look', 'done'];
  function openOnboarding(step) {
    closeSheet();
    state.ob = { step: step || 0, provider: S().hasKey ? S().provider : null, count: 0, timer: null, testing: false, ok: S().hasKey, msg: '' };
    let el = document.getElementById('ob');
    if (!el) { el = document.createElement('div'); el.id = 'ob'; el.className = 'ob'; el.setAttribute('role', 'dialog'); el.setAttribute('aria-modal', 'true'); document.body.appendChild(el); }
    obRender(true);
  }
  function closeOnboarding() {
    const el = document.getElementById('ob');
    if (state.ob) clearInterval(state.ob.timer);
    state.ob = null;
    if (el) { el.classList.add('closing'); setTimeout(() => el.remove(), 380); }
  }
  function obGo(step) {
    clearInterval(state.ob.timer); state.ob.timer = null;
    state.ob.dir = step >= state.ob.step ? 'fwd' : 'back'; state.ob.step = step; obRender(true);
  }
  /** 跳过时也要经过必须的两步：隐私、内测反馈 */
  function obSkipTarget() {
    const st = state.ob.step;
    if (st < 2) return !state.app.privacy.accepted ? 2 : S().betaConsent == null ? 3 : 6;
    return st + 1;
  }
  function obLogo(size) {
    return `<svg class="ob-logo" width="${size}" height="${size}" viewBox="0 0 32 32" aria-hidden="true">
      <circle class="ob-ring" cx="16" cy="16" r="12.5" fill="none" stroke="url(#ring-g)" stroke-width="2.2"/>
      <circle class="ob-pupil" cx="16" cy="16" r="5.2" fill="url(#ring-g)"/><circle class="ob-glint" cx="17.9" cy="14.1" r="1.4" style="fill:var(--bg)"/></svg>`;
  }
  function obRender(animate) {
    const el = document.getElementById('ob'); if (!el || !state.ob) return;
    const name = OB[state.ob.step], st = state.ob.step;
    const canSkip = (st < 2 && name !== 'done') || name === 'ai' || name === 'look';
    el.innerHTML = `<div class="ob-top">
        ${st > 0 && name !== 'done' ? `<button class="icon-btn" data-act="ob-back" aria-label="上一步">${icon('chev')}</button>` : '<span></span>'}
        <div class="ob-dots">${OB.slice(1, 6).map((_, i) => `<i ${i + 1 === st ? 'class="on"' : i + 1 < st ? 'class="done"' : ''}></i>`).join('')}</div>
        ${canSkip ? '<button class="linkish" data-act="ob-skip">跳过</button>' : '<span></span>'}</div>
      <div class="ob-body ${animate ? 'in ' + (state.ob.dir || '') : ''}">${obStep(name)}</div>`;
    hydrate(el);
    if (name === 'consent' && state.ob.timer == null) {
      state.ob.count = 2; obCount();
      state.ob.timer = setInterval(() => { state.ob.count--; obCount(); if (state.ob.count <= 0) { clearInterval(state.ob.timer); state.ob.timer = 0; } }, 1000);
    }
    if (state.kbd) { const first = el.querySelector('.ob-body .btn.primary:not(:disabled), .ob-body .btn.solid:not(:disabled)'); if (first) first.focus({ preventScroll: true }); }
  }
  function obCount() {
    document.querySelectorAll('#ob [data-count-btn]').forEach(b => {
      const n = state.ob.count;
      b.disabled = n > 0;
      b.querySelector('.n').textContent = n > 0 ? `（${n}）` : '';
    });
  }
  function obStep(name) {
    const s = S();
    switch (name) {
      case 'hello': return `<div class="ob-hero">${obLogo(128)}
        <h1 class="ob-title"><span>电脑用久了，</span><span>又热、又卡、又慢？</span></h1>
        <p class="ob-sub">试试 Iris。<b>定位，动手，解决。</b></p>
        <button class="btn primary big" data-act="ob-next">开始设置 <span class="arrow">${icon('arrow')}</span></button>
        <p class="muted small">大约一分钟。AI 和外观可以跳过，以后在设置里改。</p></div>`;
      case 'promise': return `<div class="ob-page"><div class="eyebrow">Iris 怎么做</div><h2>先看清，再动手，每一步都能退回来</h2>
        <div class="ob-cards">
          <div><span class="glyph v-normal">${icon('eye')}</span><b>体检只读</b><p>只读取信息，不改任何东西。每个结论都写清看到了什么、为什么这么判断。</p></div>
          <div><span class="glyph v-normal">${icon('undo')}</span><b>每一步都能撤销</b><p>要处理什么由你决定。改之前备份，改之后重新检测，没生效的自动恢复。</p></div>
          <div><span class="glyph v-normal">${icon('moon')}</span><b>不常驻</b><p>做完就退出，不装后台服务，不捆绑任何软件。查病毒交给 Windows 自带的 Defender。</p></div>
        </div>
        <p class="yin">${esc(pick(YIN, state.seed + 1))}</p>
        <div class="ob-acts"><button class="btn primary big" data-act="ob-next">下一步 <span class="arrow">${icon('arrow')}</span></button></div></div>`;
      case 'privacy': return `<div class="ob-page"><div class="eyebrow">隐私</div><h2>你的数据怎么处理</h2>
        <ul class="ob-list">
          <li>${icon('lock')}<span>体检只在你的电脑上读取信息。报告、操作记录、设置都保存在这台电脑上。</span></li>
          <li>${icon('cloud')}<span>用 AI 研判时，把去掉用户名、电脑名、邮箱后的证据发给<b>你选的 AI 厂商</b>；纯规则模式完全不联网。</span></li>
          <li>${icon('key')}<span>你的 API Key 只加密保存在这台电脑上，不会发给 Iris 作者。</span></li>
          <li>${icon('spark')}<span>内测期间，经你同意，去隐私的反馈包会发到 Iris 作者的服务器（阿里云中国香港）。下一步单独问你。</span></li>
        </ul>
        <button class="linkish" data-act="ob-privacy-full">查看完整隐私政策</button>
        <div class="ob-acts">${state.app.privacy.accepted
          ? `<span class="muted small">你已经同意过当前版本（${esc(state.app.privacy.version)}）。</span><button class="btn primary big" data-act="ob-next">继续 <span class="arrow">${icon('arrow')}</span></button>`
          : `<button class="btn ghost big" data-act="ob-decline">不同意，退出</button><button class="btn primary big" data-act="ob-agree">同意并继续</button>`}</div></div>`;
      case 'consent': return `<div class="ob-page"><div class="eyebrow">内测反馈</div><h2>帮 Iris 变得更准</h2>
        <p class="ob-lead">现在是内测，Iris 的判断还会出错，你的反馈是改进它的唯一来源。${s.feedbackServer
          ? '同意后，每次体检完，Iris 会自动把<b>去隐私的反馈包</b>发到作者的服务器（阿里云中国香港），你不用再点任何按钮。'
          : '同意后，报告页会提示你导出<b>去隐私的反馈包</b>发给作者；以后的版本连上作者的服务器（阿里云中国香港）后，改为体检完自动发送。'}</p>
        <div class="ob-two">
          <div><h4>会发送</h4><p>体检结果（软件的名称、版本、厂商、安装位置）、研判结论、你标记的“判断不对”和你写的说明、每一步的用时、错误记录、在「处理」页做了哪些操作和结果（不含路径和备份）、C 盘里系统已知位置的大小、随机安装编号。</p></div>
          <div><h4>不会发送</h4><p>用户名、电脑名、邮箱、代理和 DNS 地址、API Key，你自己建的文件夹名和文件名，以及文档、照片、聊天记录、浏览记录、密码。</p></div>
        </div>
        <p class="muted small">不需要注册。随时可以在「设置 → 内测反馈」里关掉。不同意也可以照常使用全部功能。${s.betaConsent ? `你现在的选择：${s.betaConsent === 'yes' ? '同意' : '不同意'}。` : ''}</p>
        <div class="ob-acts"><button class="btn ghost big" data-act="ob-consent" data-v="no" data-count-btn disabled>不同意<span class="n"></span></button>
          <button class="btn primary big" data-act="ob-consent" data-v="yes" data-count-btn disabled>同意，帮 Iris 变准<span class="n"></span></button></div></div>`;
      case 'ai': {
        const pv = state.ob.provider, g = AI_GUIDE[pv];
        return `<div class="ob-page"><div class="eyebrow">AI 研判 · 可跳过</div><h2>要不要让 AI 解释每一条？</h2>
        <div class="fee">${icon('info')}<span>${esc(AI_FEE)}</span></div>
        <div class="ob-providers">${Object.entries(AI_GUIDE).map(([k, x]) => `<button class="pv-card" data-act="ob-provider" data-v="${k}" aria-pressed="${pv === k}"><b>${esc(x.name)}</b><span>${esc(x.tag)}</span></button>`).join('')}
          <button class="pv-card" data-act="ob-provider" data-v="none" aria-pressed="${pv === 'none'}"><b>先不用 AI</b><span>纯规则，完全不联网</span></button></div>
        ${g ? `<div class="ob-guide"><ol>${g.steps.map(x => `<li>${esc(x)}</li>`).join('')}</ol>
          <button class="btn ghost" data-act="ob-open-site" data-url="${esc(g.site)}">打开${esc(g.name.split('（')[0])}官网 ${icon('arrow')}</button>
          <div class="ob-key"><input class="input" id="ob-key" type="password" autocomplete="off" placeholder="${state.ob.ok ? '已保存，留空表示不修改' : '粘贴 API Key'}">
            <button class="btn solid" data-act="ob-test" ${state.ob.testing ? 'disabled' : ''}>${state.ob.testing ? '测试中…' : '测试连接并保存'}</button></div>
          ${state.ob.msg ? `<div class="ob-msg ${state.ob.ok ? 'ok' : 'bad'}">${icon(state.ob.ok ? 'check' : 'alert')}<span>${esc(state.ob.msg)}</span></div>` : ''}</div>` : ''}
        <div class="ob-acts"><button class="btn primary big" data-act="ob-next" ${g && !state.ob.ok ? 'disabled' : ''}>${pv === 'none' || !pv ? '先用纯规则，下一步' : '下一步'} <span class="arrow">${icon('arrow')}</span></button></div></div>`;
      }
      case 'look': return `<div class="ob-page"><div class="eyebrow">外观 · 可跳过</div><h2>选一个你喜欢的样子</h2>
        <div class="themes ob-themes">${THEMES.map(([k, n, pv]) => `<button class="theme-pick" data-act="theme" data-v="${k}" aria-pressed="${(s.theme || 'dark') === k}">
          <span class="pv ${pv}"><i class="a"></i><i class="b"></i><i class="c"></i><i class="d"></i></span>${n}</button>`).join('')}</div>
        <label class="switch"><input type="checkbox" id="ob-motion" data-set="reduceMotion" ${s.reduceMotion ? 'checked' : ''}><span class="track"></span>减少动效</label>
        <div class="ob-acts"><button class="btn primary big" data-act="ob-next">下一步 <span class="arrow">${icon('arrow')}</span></button></div></div>`;
      case 'done': return `<div class="ob-hero">${obLogo(112)}
        <h1 class="ob-title">准备好了</h1>
        <p class="ob-sub">定位，动手，解决。</p>
        <div class="ob-acts"><button class="btn ghost big" data-act="ob-finish" data-v="look">先看看</button>
          <button class="btn primary big" data-act="ob-finish" data-v="start">${icon('pulse')}开始第一次体检</button></div>
        <p class="muted small">${state.mode === 'ai' ? `用 ${esc(s.model)} 研判` : '用纯规则研判（不联网）'}${s.betaConsent === 'yes' ? '，体检完自动发送去隐私的反馈' : ''}。都可以在设置里改。</p></div>`;
    }
    return '';
  }

  // ------------------------------------------------------------------ 命令面板（Ctrl+K）
  function commands() {
    const c = [
      ['开始体检', '体检', () => startCheckup()], ['概览', '页面', () => go('home')], ['报告', '页面', () => go('report')],
      ['处理', '页面', () => go('plan')], ['还原 · 撤销做过的修改', '页面', () => go('plan')], ['设置', '页面', () => go('settings')],
      ['主题：余烬', '外观', () => setSetting('theme', 'dark')], ['主题：深空', '外观', () => setSetting('theme', 'space')],
      ['主题：米白', '外观', () => setSetting('theme', 'light')], ['主题：跟随系统', '外观', () => setSetting('theme', 'auto')],
      [S().demoMode ? '关闭演示模式' : '打开演示模式', '隐私', () => setSetting('demoMode', !S().demoMode)],
      [state.pro ? '关闭专业模式' : '打开专业模式', '显示', () => setSetting('pro', !state.pro)],
      ['打开朋友发来的报告', '文件', () => act('open-file')], ['查看隐私政策', '隐私', () => showPrivacy(false)],
      ['重新运行快速设定', '设置', () => openOnboarding(0)], ['看看反馈包里有什么', '内测', () => previewFeedback()],
    ];
    if (state.report) {
      c.push(['导出报告', '报告', () => act('export')]);
      state.report.result.judgement.findings.forEach(f => c.push([pii(f.object), VERDICT[f.verdict] || '结论', async () => { if (state.view !== 'report') await go('report'); openSheet({ kind: 'finding', key: keyOf(f) }); }]));
    }
    return c;
  }
  function openPalette() {
    if (document.querySelector('.palette-back')) return;
    const box = document.createElement('div');
    box.className = 'palette-back';
    box.innerHTML = `<div class="palette" role="dialog" aria-label="搜索和命令"><input id="palette-q" placeholder="搜索结论、页面、命令…" autocomplete="off"><ul role="listbox"></ul></div>`;
    document.body.appendChild(box);
    const input = box.querySelector('input'), ul = box.querySelector('ul');
    let items = [], sel = 0;
    const draw = () => {
      const q = input.value.trim().toLowerCase();
      items = commands().filter(x => !q || (x[0] + ' ' + x[1]).toLowerCase().includes(q)).slice(0, 40);
      sel = Math.min(sel, Math.max(0, items.length - 1));
      ul.innerHTML = items.map((x, i) => `<li role="option" data-i="${i}" aria-selected="${i === sel}">${esc(x[0])}<span class="sub">${esc(x[1])}</span></li>`).join('') || '<li class="muted">没有匹配的结果</li>';
    };
    const run = (i) => { const x = items[i]; box.remove(); if (x) x[2](); };
    input.addEventListener('input', () => { sel = 0; draw(); });
    input.addEventListener('keydown', e => {
      if (e.key === 'ArrowDown') { sel = Math.min(items.length - 1, sel + 1); draw(); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { sel = Math.max(0, sel - 1); draw(); e.preventDefault(); }
      else if (e.key === 'Enter') run(sel);
      else if (e.key === 'Escape') box.remove();
    });
    box.addEventListener('click', e => { const li = e.target.closest('li[data-i]'); if (li) run(+li.dataset.i); else if (e.target === box) box.remove(); });
    draw(); input.focus();
  }

  // ------------------------------------------------------------------ 动作
  async function setSetting(key, value) {
    if (key === 'pro') { state.pro = value; S().pro = value; render(); renderSheet(); quiet('saveSettings', { pro: value }); return; }
    const prev = S()[key];
    state.app.settings[key] = value;
    if (key === 'theme' || key === 'reduceMotion') { applyLook(); if (state.ob) obRender(false); }
    if (key === 'defaultScan') state.scan = value;
    render(); if (state.sheet) renderSheet();
    try { state.app.settings = Object.assign(state.app.settings, await call('saveSettings', { [key]: value })); }
    catch (e) { state.app.settings[key] = prev; applyLook(); render(); return; }
    const msg = {
      demoMode: value ? '演示模式已打开：界面上的用户名、电脑名已遮住' : '演示模式已关闭',
      contribute: value ? '已开启：之后的体检会自动匿名贡献样本' : '已关闭自动贡献',
      allowHosted: value ? '已允许使用托管服务' : '已关闭：只用你自己的 Key 或只做本地检查',
      showLessons: value ? '教程卡片已打开' : '教程卡片已关闭，可以在这里重新打开',
      betaConsent: value === 'yes' ? '已同意：体检完自动发送去隐私的反馈包' : '已关闭：不再自动发送反馈，照常使用全部功能',
    }[key];
    if (msg) toast(msg);
  }
  async function loadReport(id) {
    state.report = await call('getReport', { id });
    state.selected.clear(); state.dismissed = new Set(state.report.dismissed || []); state.plan = null; state.planKeys = ''; state.cat = null; state.contributed = false;
    state.planStatus = {}; state.lastRun = null;
    // 恢复保存的处理清单（只留下这份报告里还存在、还能处理的）
    const sel = state.report.selected || {};
    (sel.keys || []).forEach(k => { const f = findingByKey(k); if (f && canDo(f)) state.selected.add(k); });
    state.carried = sel.carried ? { n: sel.carried, dropped: sel.dropped || 0 } : null;
    const B = buckets(state.report.result.judgement.findings);
    state.filter = B.todo.length ? 'todo' : (B.ask.length ? 'ask' : 'all');
  }
  async function startCheckup() {
    closeSheet();
    state.running = true; state.runDone = null; state.progress = { phase: 'admin', elapsed: 0 }; state.stream = []; state.streamFresh = 0; state.sectionEvents = false;
    state.runStart = Date.now(); state.tip = Math.floor(Math.random() * WAIT.length);
    state.view = 'running'; state.enter = true; render();
    try { await call('startCheckup', { mode: state.mode, scan: state.scan }); }
    catch (e) { state.running = false; state.view = 'home'; render(); }
  }
  function step(dir) {
    if (!state.sheet || state.sheet.kind !== 'finding') return;
    const list = visible(), i = list.findIndex(x => keyOf(x) === state.sheet.key), j = i + dir;
    if (j < 0 || j >= list.length) { if (dir > 0) closeSheet(); return; }
    state.sheet = { kind: 'finding', key: keyOf(list[j]) }; renderSheet(); refreshRows();
    const b = $layer.querySelector('.sheet-body'); if (b) b.classList.add(dir > 0 ? 'from-down' : 'from-up');
  }

  api.on('progress', p => {
    const prev = state.progress || {};
    // 桌面程序还没推送 section 事件时，用进度序号补出发现流的行
    if (!state.sectionEvents && p.phase === 'collect' && p.index > (prev.index == null ? -1 : prev.index)) {
      for (let i = Math.max(0, prev.phase === 'collect' ? prev.index : 0); i < p.index; i++) if (!state.stream.some(r => r.title === SECTIONS[i])) state.stream.push({ title: SECTIONS[i], detail: '', flags: 0, sec: null });
    }
    state.progress = Object.assign({}, p, p.phase === 'judge' ? { elapsed0: prev.phase === 'judge' ? prev.elapsed0 : Date.now() } : {});
    updateRunning();
  });
  api.on('section', d => { state.sectionEvents = true; state.stream.push(d); updateRunning(); });
  // 采集完成后补上每个方面查到了什么
  api.on('sectionDetails', d => {
    (d.rows || []).forEach(row => {
      const r = state.stream.find(x => x.title === row.title);
      if (r) { r.detail = row.detail; r.flags = row.flags; if (r.sec == null) r.sec = row.sec; }
    });
    state.streamFresh = state.stream.length; updateRunning();
  });
  // 「C 盘空间去哪了」在报告出来后继续跑：进度每秒一次，快照几秒一次，只更新那张卡片
  api.on('spaceProgress', d => { state.spaceProg[d.id] = d; if (state.report && state.report.id === d.id) patchSpaceProgress(); });
  const spaceReload = async d => {
    if (!state.report || state.report.id !== d.id) return;
    const r = await call('getReport', { id: d.id });
    if (!r || !state.report || state.report.id !== d.id) return;
    state.report.space = r.space; state.report.spaceRunning = r.spaceRunning;
    refreshSpace();
  };
  api.on('spaceUpdated', spaceReload);
  api.on('spaceDone', d => { delete state.spaceProg[d.id]; spaceReload(d); });
  api.on('feedbackSent', d => { if (d && d.id) state.fb[d.id] = d; if (state.view === 'report') render(); });
  api.on('update', d => { state.app.update = d; if (state.view === 'home') render(); });
  api.on('checkupDone', d => {
    state.running = false;
    if (state.view !== 'running') { openReport(d.id); return; }
    state.runDone = { id: d.id, left: 2 };
    showRunDone();
    const tick = setInterval(() => {
      if (!state.runDone || state.view !== 'running') { clearInterval(tick); return; }
      if (state.hover === 'tip') return;  // 正在看小知识：等鼠标移开
      state.runDone.left--;
      if (state.runDone.left <= 0) { clearInterval(tick); openReport(state.runDone.id); } else showRunDone();
    }, 1000);
  });
  function showRunDone() {
    const r = state.runDone, ring = document.getElementById('run-ring'); if (!ring || !r) return;
    ring.innerHTML = pctRing(100, true);
    document.getElementById('run-phase').textContent = '体检完成';
    const note = document.getElementById('run-note');
    note.innerHTML = `<button class="btn primary" data-act="open-done">查看报告 <span class="arrow">${icon('arrow')}</span></button>
      <span class="muted small">${state.hover === 'tip' ? '正在看小知识，鼠标移开后自动打开' : r.left + ' 秒后自动打开'}</span>`;
    note.className = 'run-done';
    const c = document.querySelector('.run-cancel'); if (c) c.remove();
    const box = document.getElementById('stream'); if (box) box.innerHTML = streamRows();
    const f = document.getElementById('stream-foot'); if (f) f.innerHTML = streamFoot().replace(/用时 .*/, '用时 ' + document.getElementById('run-clock').textContent);
  }
  async function openReport(id) {
    state.runDone = null;
    state.app = await call('getState');
    state.view = 'report'; state.loading = true; state.enter = true; render();
    await loadReport(id); state.loading = false; state.enter = true; render(); $main.scrollTop = 0;
  }
  api.on('notice', e => setTimeout(() => toast(e.message), 600));
  api.on('checkupError', e => { state.running = false; state.view = 'home'; state.enter = true; render(); toast(e.message); });
  api.on('opProgress', p => { state.confirming = false; state.planStatus[p.key] = p; renderPlan(); });
  api.on('planError', e => { toast(e.message); });
  api.on('planDone', async d => {
    const batchId = d && d.batchId;
    await refreshAfterChange();
    const items = (state.journal || []).filter(e => batchId && e.batchId === batchId);
    // 有记录的（生效、没生效已恢复）移出清单；没有记录的（例如没同意管理员权限）留在清单里，可以再试
    const doneKeys = new Set(items.map(e => e.findingKey).filter(Boolean));
    if (!doneKeys.size && items.length && state.plan) state.plan.steps.forEach(s => doneKeys.add(fkOf(s)));
    doneKeys.forEach(k => state.selected.delete(k));
    state.executing = false; state.confirming = false; state.planStatus = {}; state.plan = null; state.planKeys = ''; state.carried = null;
    saveSel();
    state.lastRun = items.length ? { batchId, time: new Date().toTimeString().slice(0, 5) } : null;
    if (state.app.lastReport && state.report && state.app.lastReport.id === state.report.id) state.app.pending = state.selected.size;
    setChrome(); ensurePlan();
    if (state.view === 'plan') { render(); $main.scrollTop = 0; }
    toast(items.length ? '执行完毕：每一项都重新检测过' : '没有做任何修改');
  });

  async function act(a, el) {
    el = el || { dataset: {} };
    const k = el.dataset.k;
    switch (a) {
      case 'nav': if (state.sheet) closeSheet(); go(el.dataset.to); break;
      case 'my-report': await loadReport(state.app.lastReport.id); state.enter = true; go('plan'); break;
      case 'result-close': state.lastRun = null; { const r = document.getElementById('plan-result'); if (r) r.innerHTML = ''; } break;
      case 'palette': openPalette(); break;
      case 'mode': state.mode = el.dataset.mode; render(); { const n = $main.querySelector('.start-note'); if (n) n.classList.add('swap'); } break;
      case 'start': startCheckup(); break;
      case 'cancel': await call('cancel'); break;
      case 'tip': state.tip = +el.dataset.i; { const c = document.getElementById('carousel'); if (c) c.innerHTML = carousel(); } break;
      case 'tip-step': state.tip += +el.dataset.d; { const c = document.getElementById('carousel'); if (c) c.innerHTML = carousel(); } break;
      case 'open-done': if (state.runDone) openReport(state.runDone.id); break;
      case 'open-file': { const r = await call('openReportFile'); if (r && r.id) { await loadReport(r.id); state.enter = true; go('report'); } break; }
      case 'filter': state.filter = el.dataset.f; render(); { const l = $main.querySelector('.list'); if (l) l.classList.add('stagger'); } break;
      case 'cat': {
        const name = el.dataset.cat, c = state.report.result.score.categories[name];
        state.cat = { name, ids: c.deductions.flatMap(d => d.flagIds) }; state.filter = 'all'; render();
        const l = $main.querySelector('.tabs'); if (l) l.scrollIntoView({ block: 'start' });
        break;
      }
      case 'cat-clear': state.cat = null; render(); break;
      case 'open': openSheet({ kind: 'finding', key: k }); break;
      case 'sheet-close': closeSheet(); break;
      case 'prev': step(-1); break;
      case 'next': if (el.dataset.last) closeSheet(); else step(1); break;
      case 'plan-add': state.selected.add(k); state.dismissed.delete(k); saveSel(); toast('已加入处理清单'); renderSheet(); refreshRows(); setChrome(); setTimeout(() => step(1), 450); break;
      case 'unplan':
        if (state.executing) break;
        state.selected.delete(k); saveSel();
        if (state.plan) { state.plan.steps = state.plan.steps.filter(s => fkOf(s) !== k); state.planKeys = [...state.selected].sort().join('|'); if (!state.plan.steps.length) state.plan = null; }
        if (state.sheet) renderSheet(); refreshRows(); setChrome(); renderPlan(); break;
      case 'dismiss': state.dismissed.add(k); quiet('setDismissed', { reportId: state.report.id, key: k, on: true }); step(1); refreshRows(); break;
      case 'undismiss': state.dismissed.delete(k); quiet('setDismissed', { reportId: state.report.id, key: k, on: false }); renderSheet(); refreshRows(); toast('已恢复'); break;
      case 'answer': {
        const note = (document.querySelector(`[data-note="${CSS.escape(k)}"]`) || {}).value || '';
        await call('answer', { reportId: state.report.id, key: k, answer: el.dataset.a, note });
        state.report.answers = Object.assign({}, state.report.answers, { [k]: { answer: el.dataset.a, note } });
        renderSheet(); refreshRows(); toast('已记录。点「重新研判」或下次体检时生效'); break;
      }
      case 'rejudge': {
        toast('正在重新研判…');
        const r = await call('rejudge', { id: state.report.id, mode: state.report.result.offline ? 'offline' : 'ai' });
        await loadReport(r.id); state.enter = true; render(); toast('已按你的回答重新研判'); break;
      }
      case 'export': { const r = await call('exportReport', { id: state.report.id }); toast('已导出到 ' + r.path); break; }
      case 'scan-again': state.scan = true; startCheckup(); break;
      case 'bulk-yellow': state.bulkYellow = el.checked; { const b = $main.querySelector('.bulk .btn'); if (b) { const n = suggestions(); const c = n.green.length + (state.bulkYellow ? n.yellow.length : 0);
        b.disabled = !c; b.innerHTML = `${icon('list')}一键加入建议的 <span class="num tick">${c}</span> 项`; } } break;
      case 'bulk-add': {
        const s = suggestions(), keys = s.green.concat(state.bulkYellow ? s.yellow : []);
        keys.forEach(x => state.selected.add(x)); saveSel();
        state.flash = new Set(keys); setTimeout(() => { state.flash = null; }, 1600);
        toast(`已加入 ${keys.length} 项。执行前会逐项列出，再弹确认框`); setChrome(); render(); break;
      }
      case 'prune-days': state.pruneDays = +el.dataset.v; { const s = document.getElementById('storage'); if (s) { s.innerHTML = storageRows(); hydrate(s); glide(s); } } break;
      case 'clean': {
        const r = await call('cleanStorage', { kind: el.dataset.kind, days: state.pruneDays || 90 });
        if (r && !r.cancelled) { toast(r.message || '已清理'); state.storage = null;
          if (el.dataset.kind === 'reports') { state.report = null; state.app = await call('getState'); setChrome(); } const s = document.getElementById('storage'); if (s) { s.innerHTML = storageRows(); hydrate(s); } }
        break;
      }
      case 'defender': await call('openDefender'); toast('已打开 Windows 安全中心'); break;
      case 'toggle-demo': setSetting('demoMode', !S().demoMode); break;
      case 'tips-off': setSetting('showTips', false); break;
      case 'open-update': quiet('openUpdate'); break;
      case 'fb-preview': previewFeedback(); break;
      case 'fb-send': el.disabled = true; await sendFeedback(state.report.id); break;
      case 'fb-export': await sendFeedback(state.report.id, true); break;
      case 'fb-export-last': { const r = await call('exportFeedback', { reportId: state.app.lastReport.id }); toast(r.message); break; }
      case 'missed': {
        const box = modal('Iris 漏掉了什么？', '例如：电脑开机很慢、某个软件总弹广告、风扇一直响，但报告里没提', '<textarea class="input" id="missed-note" rows="5" placeholder="写下你遇到的问题，越具体越好"></textarea>',
          '<button class="btn ghost" data-m="close">取消</button><button class="btn solid" data-m="send">提交</button>');
        box.querySelector('textarea').focus();
        box.addEventListener('click', async e => {
          const b = e.target.closest('[data-m]'); if (!b) return;
          if (b.dataset.m === 'send') {
            const note = box.querySelector('textarea').value.trim();
            if (!note) { toast('先写一句遇到的问题'); return; }
            b.disabled = true;
            const key = 'missed-' + Date.now();
            const r = await call('markFinding', { reportId: state.report.id, key, kind: 'missed', note });
            state.report.marks = Object.assign({}, state.report.marks, { [key]: { kind: 'missed', note, sent: r.sent } });
            toast(r.sent ? '已记录并发给作者，谢谢你' : '已记录，谢谢你');
          }
          box.remove();
        });
        break;
      }
      case 'mark-open': state.markOpen = k; state.markKind = el.dataset.kind; renderSheet(); { const t = document.getElementById('mark-note'); if (t) t.focus(); } break;
      case 'mark-kind': state.markKind = el.dataset.kind; document.querySelectorAll('.mark.open [data-act="mark-kind"]').forEach(b => b.setAttribute('aria-pressed', b.dataset.kind === state.markKind)); break;
      case 'mark-cancel': state.markOpen = null; renderSheet(); break;
      case 'mark-submit': {
        const note = (document.getElementById('mark-note') || {}).value || '';
        el.disabled = true;
        const r = await call('markFinding', { reportId: state.report.id, key: k, kind: state.markKind, note: note.trim() });
        state.report.marks = Object.assign({}, state.report.marks, { [k]: { kind: state.markKind, note: note.trim(), sent: r.sent } });
        state.markOpen = null; renderSheet(); refreshRows();
        toast(r.sent ? '已记录并发给作者，谢谢你' : '已记录，谢谢你');
        break;
      }
      case 'mark-undo': {
        await call('markFinding', { reportId: state.report.id, key: k, kind: 'other', note: '' });
        const m = Object.assign({}, state.report.marks); delete m[k]; state.report.marks = m;
        renderSheet(); refreshRows(); toast('已撤回'); break;
      }
      case 'ob-start': openOnboarding(0); break;
      case 'ob-next': obGo(state.ob.step + 1); break;
      case 'ob-back': obGo(Math.max(0, state.ob.step - 1)); break;
      case 'ob-skip': obGo(obSkipTarget()); break;
      case 'ob-privacy-full': showPrivacy(false); break;
      case 'ob-agree': await call('acceptPrivacy'); state.app.privacy.accepted = true; obGo(3); break;
      case 'ob-decline': await call('declinePrivacy'); break;
      case 'ob-consent': await setSetting('betaConsent', el.dataset.v); obGo(4); break;
      case 'ob-provider': {
        const v = el.dataset.v;
        state.ob.provider = v; state.ob.msg = ''; state.ob.ok = v === 'none' || (S().hasKey && S().provider === v);
        if (v === 'none') { state.mode = 'offline'; quiet('saveSettings', { mode: 'offline' }); }
        obRender(false); { const i = document.getElementById('ob-key'); if (i) i.focus(); }
        break;
      }
      case 'ob-open-site': quiet('openUrl', { url: el.dataset.url }); break;
      case 'ob-test': {
        const pv = state.ob.provider, conf = PROVIDERS[pv], key = (document.getElementById('ob-key') || {}).value.trim();
        if (!key && !S().hasKey) { state.ob.msg = '先粘贴 API Key'; state.ob.ok = false; obRender(false); break; }
        state.ob.testing = true; state.ob.msg = ''; obRender(false);
        try {
          const settings = { provider: pv, baseUrl: conf.baseUrl, model: conf.model, mode: 'ai' };
          if (key) settings.apiKey = key;
          state.app.settings = Object.assign(state.app.settings, await api.call('saveSettings', settings));
          const r = await api.call('testConnection');
          state.ob.ok = true; state.ob.msg = r.message; state.mode = 'ai';
        } catch (e) { state.ob.ok = false; state.ob.msg = (e.message || String(e)) + '。可以检查 Key 是否复制完整、账户是否有余额，或者先跳过。'; }
        state.ob.testing = false; obRender(false);
        break;
      }
      case 'ob-finish': {
        await setSetting('onboarded', true);
        closeOnboarding();
        if (el.dataset.v === 'start') startCheckup(); else { state.enter = true; go('home'); }
        break;
      }
      case 'cc-cat': {
        const c = ccOf(state.plan.steps.find(s => s.key === k));
        const v = el.dataset.v; c.categories = c.categories.includes(v) ? c.categories.filter(x => x !== v) : c.categories.concat(v); renderPlan(); break;
      }
      case 'cc-age': { const c = ccOf(state.plan.steps.find(s => s.key === k)); c.age = +el.dataset.v || null; renderPlan(); break; }
      case 'space-add': state.selected.add(k); state.dismissed.delete(k); saveSel(); toast('已加入处理清单。执行前会列出要清的每一类，再弹确认框'); setChrome(); refreshSpace(); refreshRows(); break;
      case 'space-open': { const p = el.dataset.p; state.spaceOpen.has(p) ? state.spaceOpen.delete(p) : state.spaceOpen.add(p); refreshSpace(); break; }
      case 'execute': {
        if (state.executing || !state.plan) break;
        state.executing = true; state.confirming = true; state.planStatus = {}; state.lastRun = null; renderPlan();
        { const r = document.getElementById('plan-result'); if (r) r.innerHTML = ''; }
        const steps = state.plan.steps.map(s => Object.assign({}, s, s.cacheOptions ? { cacheChoice: ccOf(s) } : {}));
        try {
          // 桌面程序会先弹出 Windows 原生确认框，逐项写清改什么、能不能撤销；点了取消就什么也不做
          const r = await call('executePlan', { planId: state.plan.planId, reportId: state.report.id, steps });
          state.confirming = false;
          if (r && r.cancelled) { state.executing = false; toast('已取消，没有做任何修改'); }
          renderPlan();
        } catch (e) { state.executing = false; state.confirming = false; renderPlan(); }
        break;
      }
      case 'undo':
      case 'undo-batch': {
        // 撤销同样会修改系统：桌面程序会先弹出确认框
        el.disabled = true;
        try {
          const r = await call('undo', a === 'undo' ? { id: el.dataset.id } : { batchId: el.dataset.batch });
          toast(r.cancelled ? '已取消，没有做任何修改' : (r.message || '已撤销'));
          if (!r.cancelled) { await refreshAfterChange(); setChrome(); if (state.view === 'plan' || state.view === 'report') render(); }
        } catch (e) {} finally { el.disabled = false; }
        break;
      }
      case 'theme': { const r = el.getBoundingClientRect(); reveal(r.left + r.width / 2, r.top + r.height / 2, () => setSetting('theme', el.dataset.v)); break; }
      case 'lesson-next': {
        const i = (S().lessonIndex || 0) + 1;
        if (i >= LESSONS.length) { await setSetting('showLessons', false); state.app.settings.lessonIndex = 0; quiet('saveSettings', { lessonIndex: 0 }); toast('五课都看完了。可以在设置里重新打开'); }
        else setSetting('lessonIndex', i);
        break;
      }
      case 'lesson-close': state.lessonConfirm = true; render(); break;
      case 'lesson-keep': state.lessonConfirm = false; render(); break;
      case 'lesson-off': state.lessonConfirm = false; setSetting('showLessons', false); break;
      case 'lesson-reset': state.app.settings.lessonIndex = 0; await setSetting('showLessons', true); await setSetting('lessonIndex', 0); toast('已重置到第一课，回到概览就能看到'); break;
      case 'save-settings': {
        const s = { provider: document.getElementById('s-provider').value, baseUrl: document.getElementById('s-base').value.trim(), model: document.getElementById('s-model').value.trim() };
        const key = document.getElementById('s-key').value.trim();
        if (key) s.apiKey = key;
        state.app.settings = await call('saveSettings', s);
        if (state.app.settings.hasKey) state.mode = 'ai';
        render(); toast('已保存'); break;
      }
      case 'test-conn': { toast('正在测试连接…'); const r = await call('testConnection'); toast(r.message); break; }
      case 'open-data': await call('openPath', { what: 'data' }); break;
      case 'show-privacy': showPrivacy(false); break;
      case 'open-repo': await call('openUrl', { url: 'https://github.com/WilhelmWei/Iris' }); break;
      case 'clear-data': {
        const r = await call('clearAllData');  // 桌面程序会先弹出确认框
        if (r.cancelled) { toast('已取消'); break; }
        toast(r.message || '已清除'); state.app = await call('getState'); state.report = null; state.journal = []; state.selected.clear(); applyLook(); render(); break;
      }
    }
  }

  document.addEventListener('click', (ev) => {
    const el = ev.target.closest('[data-act], [data-nav]'); if (!el || el.disabled) return;
    if (el.tagName === 'A') ev.preventDefault();
    if (el.dataset.nav) {
      if (state.running && el.dataset.nav !== 'home') { toast('体检进行中，完成后再看'); return; }
      if (state.sheet && el.dataset.nav !== 'plan') closeSheet();
      go(el.dataset.nav === 'home' && state.running ? 'running' : el.dataset.nav); return;
    }
    act(el.dataset.act, el);
  });
  document.addEventListener('change', (ev) => {
    const el = ev.target;
    if (el.dataset.set) setSetting(el.dataset.set, el.dataset.set === 'betaConsent' ? (el.checked ? 'yes' : 'no') : el.checked);
    else if (el.dataset.act === 'scan') { state.scan = el.checked; render(); }
    else if (el.dataset.act === 'provider') {
      const p = PROVIDERS[el.value];
      if (p.baseUrl) { document.getElementById('s-base').value = p.baseUrl; document.getElementById('s-model').value = p.model; }
    }
  });
  document.addEventListener('pointerdown', () => { state.kbd = false; }, true);
  document.addEventListener('mouseover', (e) => {
    const h = e.target.closest('[data-hover]') ? 'tip' : null;
    if (h !== state.hover) { state.hover = h; if (state.runDone) showRunDone(); }
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Tab') state.kbd = true;
    if (e.key === 'Enter' && e.target.matches && e.target.matches('li.link[data-act]')) { e.target.click(); return; }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); openPalette(); return; }
    if (state.ob) return;  // 快速设定进行中，不响应其他快捷键
    if (document.querySelector('.modal-back, .palette-back')) { if (e.key === 'Escape') { const m = document.querySelector('.modal-back:not(:has(.win-dlg))'); if (m) m.remove(); } return; }
    if (/INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName)) return;
    if (e.key === 'Escape' && state.sheet) { closeSheet(); return; }
    if (state.sheet && state.sheet.kind === 'finding' && (e.key === 'ArrowDown' || e.key === 'j')) { step(1); e.preventDefault(); }
    else if (state.sheet && state.sheet.kind === 'finding' && (e.key === 'ArrowUp' || e.key === 'k')) { step(-1); e.preventDefault(); }
    else if (!state.sheet && state.view === 'report' && state.report && (e.key === 'ArrowDown' || e.key === 'j')) { const f = visible()[0]; if (f) { openSheet({ kind: 'finding', key: keyOf(f) }); e.preventDefault(); } }
  });
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { if (state.app && S().theme === 'auto') applyLook(); });

  // ------------------------------------------------------------------ 启动
  (async function boot() {
    hydrate(document.querySelector('.rail'));
    state.app = await call('getState');
    const s = S();
    s.theme = s.theme || 'dark'; if (s.showLessons == null) s.showLessons = true; if (s.showTips == null) s.showTips = true;
    applyLook();
    state.mode = s.hasKey || s.allowHosted ? (s.mode || 'ai') : 'offline';
    state.scan = !!s.defaultScan; state.pro = !!s.pro;
    document.getElementById('version').textContent = `Iris ${state.app.version} · 只读体检 · 不常驻`;
    const initial = location.hash.replace('#', '');
    if (initial === 'report' && state.app.lastReport) { await loadReport(state.app.lastReport.id); state.view = 'report'; }
    else if (initial === 'settings') state.view = initial;
    render();
    if (initial === 'plan' || initial === 'journal') go('plan');
    if (!s.onboarded) openOnboarding(0);  // 第一次运行：快速设定（里面包括隐私政策和内测反馈的同意）
    else if (state.app.privacy && !state.app.privacy.accepted) await showPrivacy(true);  // 政策更新后重新展示
  })();
})();
