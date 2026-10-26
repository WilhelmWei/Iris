"""健康分：由代码按规则命中计算，模型不参与打分（设计文档第 2、6 章）。

每个类别从 100 分起扣，总分按权重加权。同一条规则在一个类别里命中多次时合并扣分
（例如一个软件卸载后留下 6 条排除项，是同一个原因），避免"吓唬式低分"。
每一分扣分都对应具体的预筛结果；被研判为"正常"且置信度足够的结果不扣分。
"""

WEIGHTS = {'安全': 30, '性能与后台': 25, '存储': 20, '启动': 15, '驱动与系统设置': 10}

SECTION_CATEGORY = {
    'security': '安全',
    'resources': '性能与后台', 'processes': '性能与后台', 'services': '性能与后台',
    'scheduledTasks': '性能与后台', 'software': '性能与后台', 'browserNetwork': '性能与后台',
    'storage': '存储',
    'startup': '启动',
    'drivers': '驱动与系统设置', 'versions': '驱动与系统设置', 'power': '驱动与系统设置', 'stability': '驱动与系统设置',
}

# 虽然落在其他节、但本质是安全问题的规则
SECURITY_RULES = {
    'system-name-outside-windows', 'unsigned-user-folder', 'script-engine',
    'proxy-remote', 'proxy-pac', 'hosts-custom', 'svchost-third-party-dll',
}

# 研判为"正常"时可以免扣分的规则：只限"这个程序 / 驱动是什么、是否可信"这类识别问题。
# 磁盘不足、蓝屏、CPU 高、防护关闭这类状态问题是客观事实，模型判断"不是恶意"也照样扣分
CLEARABLE_RULES = {
    'runs-from-user-folder', 'unsigned', 'heavy-memory', 'unsigned-user-folder',
    'seed-bundle', 'seed-anticheat-accel', 'seed-bank-security', 'svchost-third-party-dll',
    'server-service-running', 'frequent', 'duplicate-versions', 'boot-start-third-party',
    'third-party-class-filter', 'edge-preload', 'duplicate-category',
}

# 用户确认后可以免扣分的规则：识别类规则，加上用户自己做的 Defender 设置（排除项、允许的威胁）
USER_CLEARABLE_RULES = CLEARABLE_RULES | {'defender-allowed-threat', 'defender-exclusion'}

PENALTY = {'high': 30, 'medium': 10, 'low': 3, 'info': 0}
EXTRA_SHARE = 1 / 3   # 同一规则的第二条起，每条只扣自身分值的三分之一
GROUP_CAP = 1.5       # 同一规则合计最多扣最高单条分值的 1.5 倍


def category_of(flag):
    if flag.get('rule') in SECURITY_RULES:
        return '安全'
    return SECTION_CATEGORY.get(flag.get('section'), '性能与后台')


def health_score(flags, cleared_ids=(), confirmed_ids=()):
    """flags：报告里的预筛结果；cleared_ids：研判为正常的 flag id；confirmed_ids：用户确认过的 flag id。"""
    confirmed = {f.get('id') for f in flags if f.get('id') in set(confirmed_ids) and f.get('rule') in USER_CLEARABLE_RULES}
    cleared = {f.get('id') for f in flags if f.get('id') in set(cleared_ids) and f.get('rule') in CLEARABLE_RULES}
    notes = {i: '你已确认，不扣分' for i in confirmed}
    notes.update({i: '研判为正常，不扣分' for i in cleared - confirmed})
    cleared = cleared | confirmed
    groups = {}
    for f in flags:
        if PENALTY.get(f.get('severity'), 0) and f.get('id') not in cleared:
            groups.setdefault((category_of(f), f.get('rule')), []).append(f)

    cats = {name: {'weight': w, 'score': 100, 'deductions': []} for name, w in WEIGHTS.items()}
    for (cat, rule), fs in groups.items():
        pts = sorted((PENALTY[f['severity']] for f in fs), reverse=True)
        points = min(pts[0] + sum(p * EXTRA_SHARE for p in pts[1:]), pts[0] * GROUP_CAP)
        points = round(points, 1)
        cats[cat]['deductions'].append({'rule': rule, 'flagIds': [f['id'] for f in fs],
                                        'objects': sorted({str(f.get('object')) for f in fs}), 'points': points})
        cats[cat]['score'] = max(0, round(cats[cat]['score'] - points, 1))
    for f in flags:
        if f.get('id') in cleared and PENALTY.get(f.get('severity'), 0):
            cats[category_of(f)]['deductions'].append({'rule': f.get('rule'), 'flagIds': [f['id']],
                                                       'objects': [str(f.get('object'))], 'points': 0,
                                                       'note': notes[f.get('id')]})
    total = round(sum(c['weight'] * c['score'] / 100 for c in cats.values()))
    return {'total': total, 'categories': cats}
