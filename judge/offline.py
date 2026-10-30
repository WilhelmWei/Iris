"""纯规则模式（进度表 1.8）：不联网、不用 Key，也能出一份基础报告。

生成和模型输出同样格式的结论，之后照样经过知识库合并、validate 校验、代码算分和渲染。
结论来源依次是：用户确认过的说明 → 知识库提示 → 下面按规则写好的默认结论。
没有模型就没法结合上下文判断"这个软件是不是你自己装的"，所以这类项一律写成"拿不准"并提问。
"""

# 规则 -> (结论, 置信度, 建议操作, 给用户的说明)。说明里的 {object} 会换成对象名
UNSURE = ('uncertain', 0.5, 'ask_user')
RULES = {
    # 资源
    'cpu-busy-idle':           ('problem', 0.7, 'none', '采集时 CPU 一直很忙。可以打开任务管理器按 CPU 排序，看看是谁在占用'),
    'gpu-busy-idle':           ('problem', 0.6, 'none', '没开游戏和视频时显卡也很忙，值得看看是哪个程序（挖矿程序也会这样，但更常见的是浏览器或壁纸软件）'),
    'defender-cpu-heavy':      ('problem', 0.7, 'none', 'Defender 开机以来一直占用大量 CPU，可能有扫描卡住了。可以在「Windows 安全中心 → 病毒和威胁防护」看看是否有扫描在进行'),
    'long-uptime':             ('problem', 0.8, 'power.disable_fast_startup', '很久没有真正重启过。开着快速启动时，点"关机"再开机并不等于重启，建议定期"重启"一次'),
    'memory-compression-busy': ('problem', 0.7, 'none', '内存压缩一直在忙，说明内存不够用了。卡顿、发热时先考虑内存不足，而不是中毒'),
    'memory-high':             ('problem', 0.7, 'none', '内存占用很高。关掉不用的程序和浏览器标签页，比任何"内存加速球"都有效'),
    # 进程
    'heavy-memory':            ('normal', 0.6, 'none', '{object} 占用内存较多，如果正在使用属于正常'),
    'runs-from-user-folder':   UNSURE + ('{object} 从用户目录运行。很多正规软件也这样安装，需要你确认是不是自己装的',),
    'unsigned':                UNSURE + ('{object} 没有有效的数字签名，需要你确认是不是自己装的、来源可靠的软件',),
    'system-name-outside-windows': ('suspicious', 0.7, 'defender.scan', '{object} 用了系统程序的名字，却不在 Windows 目录里，这是恶意程序常用的伪装手法，建议交给 Defender 扫描'),
    # 自启动 / 计划任务 / 服务
    'unsigned-user-folder':    UNSURE + ('{object} 开机自启动、在用户目录里且没有有效签名，需要你确认是不是自己装的',),
    'target-missing':          ('leftover', 0.85, None, '{object} 指向的程序已经不存在，是软件卸载后留下的残留'),
    'frequent':                ('normal', 0.6, 'none', '{object} 频繁定时运行，多数是软件的自动更新'),
    'script-engine':           UNSURE + ('{object} 通过脚本程序运行，正规软件少见这种做法，需要确认来源',),
    'seed-bundle':             UNSURE + ('{object} 属于常见的捆绑 / 全家桶软件，需要你确认是不是主动安装、还在用',),
    'seed-anticheat-accel':    UNSURE + ('{object} 像是游戏反作弊、加速器或下载器的组件，需要你确认还在用对应的软件',),
    'seed-bank-security':      UNSURE + ('{object} 像是网银安全控件，需要你确认还在用这家银行的网银',),
    'binary-missing':          ('leftover', 0.75, 'service.delete_orphan', '{object} 的程序文件已经不存在，只剩下登记信息，是卸载或手动清理后的残留'),
    'runs-from-user-folder-service': UNSURE + ('{object} 是从用户目录运行的服务，需要确认来源',),
    'server-service-running':  ('problem', 0.6, 'service.set_manual', '{object} 是服务器类服务，个人电脑一般用不到，可能是装软件时被顺带开启的'),
    'svchost-third-party-dll': UNSURE + ('{object} 是寄宿在系统 svchost 进程里的第三方组件，需要确认是哪个软件的',),
    # 驱动与系统设置
    'boot-start-third-party':  ('normal', 0.6, 'none', '{object} 是在系统启动早期加载的第三方驱动，仅供参考；这类驱动不能直接删除'),
    'duplicate-versions':      ('leftover', 0.8, 'driver.delete_old_version', '驱动仓库里保留了没有设备在用的旧版本，可以删除（系统会自动保护在用的版本）'),
    'memory-integrity-off':    ('problem', 0.7, 'none', '内存完整性没有开启，常见原因是装过不兼容的旧驱动'),
    'third-party-class-filter': UNSURE + ('{object} 挂在设备的过滤器上，处理不当会让键盘、USB 等设备失灵，需要确认它属于哪个软件',),
    'newer-driver-not-used':   UNSURE + ('{object} 驱动仓库里有更新的版本没有被使用，换驱动有风险，需要确认',),
    'battery-worn':            ('problem', 0.8, 'none', '电池已经明显老化，续航会变短'),
    'fast-startup-on':         ('problem', 0.6, 'power.disable_fast_startup', '开着快速启动：点"关机"其实是半休眠，长期不重启会积累内存占用和小毛病'),
    'slow-charging':           ('problem', 0.6, 'none', '充电功率偏低，可以检查充电器和数据线'),
    'bsod':                    ('problem', 0.8, 'none', '最近出现过蓝屏'),
    'bsod-repeated':           ('problem', 0.9, 'none', '最近反复蓝屏，需要尽快排查'),
    # 软件
    'duplicate-category':      ('normal', 0.6, 'none', '同类软件装了好几个（{object}），不常用的可以卸载'),
    'large-on-c':              ('problem', 0.6, 'none', '{object} 装在 C 盘且占用很大，C 盘紧张时可以考虑卸载或改装到其他盘'),
    # 存储
    'system-drive-low':        ('problem', 0.9, 'cache.clean', 'C 盘空间不足，会影响系统更新和运行速度'),
    'chat-cache-large':        ('problem', 0.8, 'none', '{object} 的聊天文件很大。图片、视频、文件属于你的数据，可以在软件设置里清理，或者把存储位置改到其他盘'),
    'junk-cleanable':          ('problem', 0.9, 'cache.clean', '程序留下的缓存可以直接清理，它们都会在需要时自动重新生成'),
    'temp-large':              ('problem', 0.8, 'cache.clean', '临时文件占用较大，可以清理'),
    'downloads-large':         ('problem', 0.6, 'none', '下载文件夹很大，里面多是用过的安装包和文件，可以自己整理一下'),
    'conda-cache':             ('problem', 0.7, 'none', 'conda 的软件包缓存可以用 conda clean -a 清理（不影响已建好的环境）'),
    'dev-cache':               ('problem', 0.6, 'cache.clean', '{object} 是开发工具的缓存，可以清理，之后需要时会重新下载'),
    'games-on-c':              ('problem', 0.6, 'none', '{object} 游戏库在 C 盘，C 盘紧张时可以在启动器里把游戏移到其他盘'),
    'system-leftover':         ('leftover', 0.8, 'none', '{object} 是系统升级或更新留下的文件，确认系统正常后，可以用 Windows 自带的「磁盘清理 → 清理系统文件」删除'),
    # 安全
    'active-threat':           ('suspicious', 0.9, 'defender.scan', 'Defender 报告了尚未处理的威胁：{object}'),
    'detection-not-remediated': ('suspicious', 0.7, 'defender.scan', 'Defender 检测到的威胁没有处理成功：{object}'),
    'defender-allowed-threat': UNSURE + ('{object} 曾被 Defender 检测到，后来被手动设为"允许"，之后不会再报。需要确认是不是你自己放行的',),
    'defender-exclusion':      UNSURE[:2] + ('defender.review', 'Defender 有排除项（不扫描的位置或程序），需要确认都是你自己加的'),
    'defender-exclusion-orphan': ('leftover', 0.85, 'defender.review', '排除的目录已经不存在，软件卸载后排除项没清掉，等于留了一个不被扫描的位置'),
    'defender-rtp-off':        ('problem', 0.9, 'none', 'Defender 实时保护是关着的。本工具不修改安全设置，建议你在 Windows 安全中心里打开'),
    'defender-signature-old':  ('problem', 0.8, 'none', 'Defender 的病毒库很久没更新了，可以在 Windows 安全中心里检查更新'),
    'firewall-off':            ('problem', 0.9, 'none', '防火墙是关着的。本工具不修改安全设置，建议你在 Windows 安全中心里打开'),
    'uac-off':                 ('problem', 0.9, 'none', '用户账户控制（UAC）被关闭了，程序可以不经确认获得管理员权限。建议在控制面板里打开'),
    # 浏览器与网络
    'edge-preload':            ('bloat', 0.7, 'browser.disable_background', 'Edge 开机就在后台预加载，即使不用也占内存'),
    'edge-idle-background':    ('bloat', 0.8, 'browser.disable_background', '你平时不用 Edge，它却开机就留在后台、占着内存。关掉启动增强和后台运行，不影响你偶尔打开它'),
    'chrome-idle-background':  ('bloat', 0.75, 'browser.disable_chrome_background', '你平时不用 Chrome，它关掉窗口后还在后台运行。关掉后台运行，不影响你偶尔打开它'),
    'browser-unused':          ('bloat', 0.7, 'app.uninstall', '{object} 很久没用过了，却还在开机自启动或后台运行。不用的话可以卸载；这类浏览器常是装别的软件时被捆绑装上的'),
    'hosts-custom':            UNSURE + ('hosts 文件里有自定义条目，可能是软件或你自己加的，也可能被篡改，需要确认',),
    'proxy-remote':            UNSURE + ('系统设置了代理服务器。如果你没有开 VPN 或代理软件，需要警惕',),
    'proxy-pac':               UNSURE + ('系统设置了自动代理脚本（PAC）。如果你没有开 VPN 或代理软件，需要警惕',),
}
# 同名规则在不同节里含义不同的，用 "节:规则" 覆盖
SECTION_RULES = {
    'services:runs-from-user-folder': RULES['runs-from-user-folder-service'],
    'startup:target-missing': ('leftover', 0.85, 'startup.disable', '{object} 开机自启动指向的程序已经不存在，是卸载残留'),
    'scheduledTasks:target-missing': ('leftover', 0.85, 'task.delete_leftover', '{object} 指向的程序已经不存在，是软件卸载后留下的残留'),
}

# 拿不准时问用户的具体问题（没有的用通用问题）
QUESTIONS = {
    'defender-allowed-threat': '这是你自己在 Windows 安全中心点"允许"放行的吗？对应的软件现在还在用吗？',
    'defender-exclusion': '这些排除项都是你自己加的吗？对应的软件还在用吗？',
    'seed-bank-security': '你还在用需要这个安全控件的网银吗？',
    'seed-anticheat-accel': '你还在用对应的游戏、加速器或下载软件吗？',
    'seed-bundle': '这是你主动安装、现在还在用的软件吗？',
    'hosts-custom': 'hosts 文件里的条目是你或你信任的软件加的吗？',
    'proxy-remote': '你现在开着 VPN 或代理软件吗？',
    'proxy-pac': '你现在开着 VPN 或代理软件吗？',
}

MALWARE_RULES = {'active-threat', 'detection-not-remediated', 'system-name-outside-windows'}

# 信息差小知识：和这台电脑命中的规则相关才给
TIPS = [
    ({'defender-exclusion', 'defender-exclusion-orphan'}, '卸载软件后，Defender 排除项不会自动删除',
     '有些软件安装时会让 Defender 不扫描自己的目录或进程名，卸载后这些排除项还在。按进程名的排除（比如 Update.exe）对任何同名程序都生效，等于留了一个口子。'),
    ({'long-uptime', 'fast-startup-on'}, '点"关机"不等于重启',
     'Windows 默认开着快速启动，关机时把系统内核存进休眠文件，下次开机直接恢复。所以长期"关机"也会积累内存占用和小毛病，真正清空要点"重启"。'),
    ({'chat-cache-large'}, '微信、QQ 的聊天文件默认存在 C 盘',
     '聊天里收到的图片、视频、文件默认存在 C 盘"文档"里，几年下来几十 GB 很常见。可以在微信 / QQ 的设置里把文件位置改到其他盘。'),
    ({'seed-bundle', 'target-missing'}, '装软件时留意捆绑勾选',
     '国产软件安装界面常默认勾选"顺便安装"其他软件；"全家桶"卸载后也常留下计划任务、服务和驱动。安装时选"自定义安装"，把不需要的勾去掉。'),
    ({'memory-high', 'memory-compression-busy'}, '"内存加速球"是伪优化',
     '清理内存只是把数据挤到硬盘上，过一会儿又会读回来，反而更慢。真正有用的是关掉不用的程序和浏览器标签页，或者加内存。'),
    ({'junk-cleanable', 'system-drive-low'}, '清缓存腾出的空间，过一阵会慢慢回来',
     '浏览器缓存、临时文件清掉后，程序用到时会重新生成，所以"一键清理"腾出的空间会慢慢回来。C 盘真正的大头通常是聊天文件、下载文件夹和游戏，把它们挪到其他盘效果更持久。'),
    ({'edge-idle-background', 'chrome-idle-background', 'browser-unused'}, '不用的浏览器也在后台占内存',
     '很多浏览器为了"秒开"，开机就在后台预加载，即使你从来不点它。只关掉不用的那几个的后台运行就好，常用的浏览器不用动。'),
    ({'defender-allowed-threat'}, '"允许"一个威胁之后，Defender 就不会再提醒',
     '在 Windows 安全中心点了"允许"的项目会一直被放行，即使文件后来换成了别的东西。不再需要时，可以在「保护历史记录 → 允许的威胁」里撤销。'),
]


def _default(item):
    key = f'{item.get("section")}:{item.get("rule")}'
    d = SECTION_RULES.get(key) or RULES.get(item.get('rule'))
    if not d:
        return UNSURE + ('{object}：规则预筛命中，纯规则模式下无法判断，需要你确认',)
    verdict, conf, action, text = d
    return verdict, conf, action or 'none', text


def offline_output(model_evidence):
    """对发给模型的那部分证据包，按规则生成和模型同格式的输出。"""
    groups = {}
    for it in model_evidence['items']:
        groups.setdefault((it['rule'], it['object']), []).append(it)  # 同一规则、同一对象（如 5 条排除项）合并

    findings = []
    for (rule, obj), items in groups.items():
        first = items[0]
        k, confirmed = first.get('knowledge'), first.get('userConfirmed')
        if confirmed:
            verdict, conf, action = 'normal', 0.8, 'none'
            text = f'你以前确认过：{confirmed.get("note")}'
        elif k:
            verdict, conf, action = k['verdict'], 0.7 if k.get('certainty') != 'guess' else 0.5, k.get('proposedAction') or 'ask_user'
            text = k.get('explanation') or ''
        else:
            verdict, conf, action, text = _default(first)
            text = text.format(object=obj)
        question = None
        if verdict == 'uncertain' or conf < 0.6 or action == 'ask_user':
            question = (k or {}).get('question') or QUESTIONS.get(rule) or '这是你自己安装或设置的吗？现在还在用吗？'
        findings.append({
            'flagIds': [it['flagId'] for it in items], 'object': obj, 'verdict': verdict, 'confidence': conf,
            'evidence': '；'.join(f'{it["flagId"]} {it.get("evidence") or ""}' for it in items),
            'proposedAction': action, 'userExplanation': text, 'questionForUser': question,
        })

    rules = {it['rule'] for it in model_evidence['items']}
    bad = [it for it in model_evidence['items'] if it['rule'] in MALWARE_RULES]
    order = {'high': 0, 'medium': 1, 'low': 2, 'info': 3}
    todo = sorted((f for f in findings if f['verdict'] in ('suspicious', 'problem', 'leftover')),
                  key=lambda f: min(order.get(it['severity'], 9) for it in model_evidence['items'] if it['flagId'] in f['flagIds']))
    n_q = sum(1 for f in findings if f['questionForUser'])
    return {
        'headline': f'纯规则模式（没有调用 AI）：{len(todo)} 项建议处理，{n_q} 项需要你确认',
        'malwareSuspected': bool(bad),
        'malwareReasoning': ('规则发现了需要核实的迹象：' + '；'.join(f'{it["object"]}（{it["evidence"]}）' for it in bad)) if bad else
                            '规则没有发现中毒迹象：没有 Defender 未处理的威胁，没有伪装成系统程序的进程。纯规则模式不结合上下文判断，结论比 AI 研判粗。',
        'top3': [f'{f["object"]}：{f["userExplanation"]}' for f in todo[:3]],
        'findings': findings,
        'tips': [{'title': t, 'body': b, 'relatedFlagIds': [it['flagId'] for it in model_evidence['items'] if it['rule'] in rs]}
                 for rs, t, b in TIPS if rs & rules][:3],
        'notCollected': [],
    }
