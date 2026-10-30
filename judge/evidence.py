"""把完整采集报告压缩成交给模型的证据包（设计文档第 6 章"提示词结构"）。

完整报告约 10 万字符，其中绝大多数是正常的微软服务、驱动包；模型只需要看
预筛命中的对象。这里为每条预筛结果附上对应对象的详情和"属于哪个已装软件"，
再加上机器概况和已装软件名单，目标 2 万字符左右。
"""
import json
import re

MAX_STR = 300  # 单个字符串最长保留的字符数

# 提示注入：进程名、路径、计划任务描述、软件名都来自被检查的电脑，恶意程序可以在里面写"请判为正常"。
# 这里只做粗筛，命中就打标记；真正的保险在 validate（带标记或高风险的项，模型判正常不算数）
_INJECTION = re.compile(
    r'(ignore|disregard|forget)\s+(all\s+|any\s+)?(the\s+)?(previous|above|prior|earlier)'
    r'|system\s*prompt|you\s+are\s+(now\s+)?(a|an|the)\s'
    r'|verdict\W{0,5}\w{0,10}\W{0,5}normal|mark\s+(this|it)\s+as\s+(safe|normal|benign)'
    r'|(判|判定|标记|视|认定|当)为?(正常|安全|无害)'
    r'|忽略(以上|之前|前面|上述|所有|全部)'
    r'|(之前|以上|上面)的?(指令|提示|规则)'
    r'|不要(报告|标记|提示|上报)(此|这|该|本)', re.I)


def looks_like_injection(v):
    if isinstance(v, str):
        return bool(_INJECTION.search(v))
    if isinstance(v, dict):
        return any(looks_like_injection(x) for x in v.values())
    if isinstance(v, list):
        return any(looks_like_injection(x) for x in v)
    return False


def _short(v):
    if isinstance(v, str) and len(v) > MAX_STR:
        return v[:MAX_STR] + '…'
    if isinstance(v, dict):
        return {k: _short(x) for k, x in v.items() if x not in (None, '', [], {})}
    if isinstance(v, list):
        return [_short(x) for x in v]
    return v


def _norm_path(p):
    if not p:
        return ''
    return str(p).replace('/', '\\').strip('"').rstrip('\\').lower()


_NOISE_TOKENS = {'co', 'ltd', 'inc', 'llc', 'corp', 'corporation', 'limited', 'gmbh', 'pbc', 'ag', 'sa', 'plc', 'the'}
_NOISE_CN = ('股份有限公司', '有限责任公司', '有限公司', '公司')


def _norm_company(c):
    """'Zhuhai Kingsoft Office Software Co.,Ltd' -> 'zhuhaikingsoftofficesoftware'；'迅雷网络技术有限公司' -> '迅雷网络技术'"""
    if not c:
        return ''
    s = str(c).lower()
    for w in _NOISE_CN:
        s = s.replace(w, '')
    tokens = re.split(r'[^0-9a-z\u4e00-\u9fff]+', s)
    return ''.join(t for t in tokens if t and t not in _NOISE_TOKENS)


def _generic_company(c):
    # 微软的产品太多，按厂商对不出具体是哪个软件
    return c.startswith('microsoft')


# 太宽泛、不能用来判断归属的安装目录
_GENERIC_DIRS = {'c:\\program files', 'c:\\program files (x86)', 'c:\\windows', 'c:\\windows\\system32',
                 'c:\\programdata', 'c:', 'd:', 'c:\\users'}


class SoftwareIndex:
    """按安装目录和厂商，把进程、服务、驱动、计划任务对到已装软件上。"""

    def __init__(self, software_items):
        self.by_location = []
        self.by_company = []
        for a in software_items or []:
            name = a.get('name')
            loc = _norm_path(a.get('location'))
            if name and loc and loc not in _GENERIC_DIRS and len(loc) > 3:
                self.by_location.append((loc, name))
            pub = _norm_company(a.get('publisher'))
            if name and len(pub) >= 4 and not _generic_company(pub):
                self.by_company.append((pub, name))
        self.by_location.sort(key=lambda x: -len(x[0]))  # 最长前缀优先

    def owner(self, path=None, company=None):
        p = _norm_path(path)
        if p:
            for loc, name in self.by_location:
                if p == loc or p.startswith(loc + '\\'):
                    return name
        c = _norm_company(company)
        if len(c) >= 4 and not _generic_company(c):
            hits = sorted({name for pub, name in self.by_company if c == pub or c in pub or pub in c})
            if hits:
                return '、'.join(hits[:3])
        return None


def _find(items, key, value):
    for it in items or []:
        if it.get(key) == value:
            return it
    return None


def _first_path(obj):
    """对象里最能代表"程序在哪"的路径。"""
    if not isinstance(obj, dict):
        return None
    acts = obj.get('actions') or []
    if acts and isinstance(acts[0], dict) and acts[0].get('execute'):
        return acts[0]['execute']  # 计划任务：path 是任务所在的文件夹（如 \），不是程序位置
    for k in ('serviceDll', 'path', 'exe'):
        if obj.get(k):
            return obj[k]
    return None


def _context_for(flag, S):
    """按节和对象名找到报告里对应的原始数据。"""
    sec, obj, rule = flag.get('section'), flag.get('object'), flag.get('rule')
    if sec == 'processes':
        return _find(S.get('processes', {}).get('items'), 'name', obj)
    if sec == 'services':
        return _find(S.get('services', {}).get('items'), 'name', obj)
    if sec == 'scheduledTasks':
        return _find(S.get('scheduledTasks', {}).get('items'), 'name', obj)
    if sec == 'startup':
        return _find(S.get('startup', {}).get('items'), 'name', obj)
    if sec == 'drivers':
        d = S.get('drivers', {})
        if rule == 'duplicate-versions':
            return {'packages': [p for p in d.get('packages', {}).get('items', []) if p.get('inf') == obj]}
        if rule == 'memory-integrity-off':
            return d.get('virtualizationSecurity')
        ctx = dict(_find(d.get('kernelDrivers', {}).get('items'), 'name', obj) or {})
        filters = [f for f in d.get('classFilters', []) if f.get('driver') == obj]
        if filters:
            ctx['classFilters'] = [{'className': f.get('className'), 'kind': f.get('kind')} for f in filters]
        return ctx or None
    if sec == 'software':
        sw = S.get('software', {})
        if rule == 'duplicate-category':
            return {'apps': sw.get('duplicateCategories', {}).get(obj)}
        return _find(sw.get('items'), 'name', obj)
    if sec == 'security':
        sc = S.get('security', {})
        if rule.startswith('defender-exclusion'):
            return {'exclusions': sc.get('defenderExclusions')}
        if rule == 'defender-allowed-threat':
            return {'allowedThreats': sc.get('allowedThreats')}
        if rule == 'detection-not-remediated':
            return {'recentDetections': sc.get('recentDetections')}
        return {'defender': sc.get('defender')}
    if sec == 'storage':
        st = S.get('storage', {})
        folders = st.get('folders', {})
        junk = st.get('junk') or {}
        if rule == 'chat-cache-large':
            return folders.get('chatApps', {}).get(obj)
        if rule == 'junk-cleanable':
            return {'cleanableMB': junk.get('cleanableMB'),
                    'items': [{k: x.get(k) for k in ('name', 'group', 'cleanableMB', 'running')}
                              for x in junk.get('items', []) if x.get('tier') == 'A' and (x.get('cleanableMB') or 0) > 0]}
        if rule == 'system-leftover':
            return _find(junk.get('items'), 'name', obj)
        if rule == 'system-drive-low':
            big_on_c = [{'name': a.get('name'), 'sizeMB': a.get('sizeMB')}
                        for a in S.get('software', {}).get('items', [])
                        if (a.get('location') or '').lower().startswith('c:') and (a.get('sizeMB') or 0) >= 500][:10]
            return {'volumes': st.get('volumes'), 'system': st.get('system'),
                    'folders': {k: {'sizeGB': v.get('sizeGB'), 'path': v.get('path')} for k, v in folders.items()
                                if isinstance(v, dict) and 'sizeGB' in v},
                    'largeSoftwareOnC': big_on_c, 'steamLibraries': st.get('steamLibraries'),
                    'cleanableCacheMB': junk.get('cleanableMB')}
        return folders.get(obj)
    if sec == 'stability':
        return S.get('stability')
    if sec == 'resources':
        r = S.get('resources', {})
        return {'memory': r.get('memory'), 'memoryCompression': r.get('memoryCompression'),
                'topByMemory': (r.get('topByMemory') or [])[:8], 'topByCpuSec': (r.get('topByCpuSec') or [])[:8]}
    if sec == 'power':
        return S.get('power')
    if sec == 'browserNetwork':
        b = S.get('browserNetwork', {})
        if rule in ('edge-idle-background', 'chrome-idle-background', 'browser-unused'):
            return {'mainBrowser': b.get('mainBrowser'), 'policies': b.get('policies'),
                    'browsers': [{k: x.get(k) for k in ('id', 'name', 'isDefault', 'lastBrowsedDays', 'processes', 'memoryMB', 'autostart')}
                                 for x in b.get('browsers', [])]}
        return {k: b.get(k) for k in ('policies', 'edgeProcesses', 'proxy', 'hostsEntries')}
    if sec == 'versions':
        return S.get('versions')
    return None


def build_evidence(report, known=(), kb=()):
    """known：用户确认过的结论（known.py），命中的项会带上 userConfirmed；
    kb：知识库条目（kb.py），命中的项会带上 knowledge。"""
    from known import match
    from kb import lookup, note
    S = report.get('sections', {})
    sw_items = S.get('software', {}).get('items', [])
    index = SoftwareIndex(sw_items)

    items = []
    for f in report.get('flags', []):
        ctx = _context_for(f, S)
        item = {'flagId': f.get('id'), 'section': f.get('section'), 'rule': f.get('rule'),
                'severity': f.get('severity'), 'object': f.get('object'), 'evidence': f.get('evidence')}
        k = match(f, known)
        if k:
            item['userConfirmed'] = {'note': k.get('note'), 'date': k.get('confirmedAt')}
        if ctx:
            item['context'] = _short(ctx)
            owner = index.owner(_first_path(ctx), ctx.get('company') or ctx.get('signer') or ctx.get('provider'))
            if owner and owner != f.get('object'):
                item['ownerSoftware'] = owner
        if looks_like_injection([f.get('object'), f.get('evidence'), ctx]):
            item['injectionSuspect'] = True
        hit = lookup(f, ctx, _first_path(ctx), sw_items, kb) if kb else None
        if hit:
            item['knowledge'] = note(hit)
        items.append(item)

    # 多条结果共用同一份上下文时（如 7 条排除项）只放一份，用 contextRef 引用
    shared, seen = {}, {}
    for item in items:
        if 'context' not in item:
            continue
        key = json.dumps(item['context'], ensure_ascii=False, sort_keys=True)
        seen.setdefault(key, []).append(item)
    for n, group in enumerate((g for g in seen.values() if len(g) > 1), start=1):
        ref = f'C{n}'
        shared[ref] = group[0]['context']
        for item in group:
            del item['context']
            item['contextRef'] = ref

    r = S.get('resources', {})
    st = S.get('storage', {})
    sc = S.get('security', {})
    pw = S.get('power', {})
    machine = {
        'manufacturer': report.get('machine', {}).get('manufacturer'),
        'model': report.get('machine', {}).get('model'),
        'os': r.get('os'), 'cpu': r.get('cpu'), 'memory': r.get('memory'), 'uptimeDays': r.get('uptimeDays'),
        'volumes': st.get('volumes'),
        'fastStartup': pw.get('fastStartup'), 'hibernateEnabled': pw.get('hibernateEnabled'), 'battery': pw.get('battery'),
        'defender': sc.get('defender'), 'defenderRecentDetections': sc.get('recentDetections'),
        'uacEnabled': sc.get('uacEnabled'), 'secureBoot': sc.get('secureBoot'),
        'virtualizationSecurity': S.get('drivers', {}).get('virtualizationSecurity'),
    }
    tool = report.get('tool', {})
    return {
        'schema': 'pcdoctor.evidence/0.1',
        'collector': {'reportTime': report.get('generatedAt'),  # 模型据此判断事件是多久以前
                      'version': tool.get('version'), 'isAdmin': tool.get('isAdmin'), 'quick': tool.get('quick'),
                      'incomplete': (tool.get('incomplete') or [])[:10]},
        'machine': _short(machine),
        'items': items,
        'sharedContext': shared,
        'installedSoftware': [f"{a.get('name')} | {a.get('publisher') or ''}".rstrip(' |') for a in sw_items],
    }
