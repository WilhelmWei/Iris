"""校验模型输出（设计文档第 6 章"输出校验"）。

模型说什么都不直接算数：操作编号不在操作库里、不适用于该报告节、碰到红线的，
一律改成"先问用户"；风险等级由操作库决定；拿不准的结论必须带一个问题。
所有改动记进 issues，在报告末尾列出，方便人工复核模型的误判率。
"""
from oplib import OPERATIONS, DRIVER_OPS

VERDICTS = {'normal', 'problem', 'leftover', 'bloat', 'suspicious', 'uncertain'}
MIN_CONFIDENCE = 0.6


def _context(item, evidence):
    if 'context' in item:
        return item['context'] or {}
    return evidence.get('sharedContext', {}).get(item.get('contextRef'), {}) or {}


def _red_line(op, items, evidence):
    """返回违反的红线说明；没有违反返回 None。"""
    for it in items:
        ctx = _context(it, evidence)
        if op in DRIVER_OPS and it.get('section') == 'drivers':
            signer = str(ctx.get('signer') or '')
            company = str(ctx.get('company') or '')
            if signer.startswith('Microsoft') or company.startswith('Microsoft'):
                return '红线 3：不动微软签名或 Windows 自带的驱动'
            if op == 'driver.remove_after_reboot' and ctx.get('state') == 'Running' and ctx.get('startMode') in ('Boot', 'System'):
                return '红线 4：不动系统启动早期加载且正在运行的第三方驱动'
            if ctx.get('classFilters') and op != 'driver.remove_after_reboot':
                return '红线 2：挂在设备过滤器上的驱动只能先摘除过滤器、重启后再处理'
    return None


def _as_float(v):
    try:
        return max(0.0, min(1.0, float(v)))
    except (TypeError, ValueError):
        return 0.0


def validate(output, evidence):
    issues = []
    items_by_id = {it['flagId']: it for it in evidence.get('items', [])}
    if not isinstance(output, dict):
        return {'findings': []}, ['模型输出不是 JSON 对象']

    findings = []
    for i, f in enumerate(output.get('findings') or []):
        if not isinstance(f, dict):
            issues.append(f'第 {i + 1} 条 finding 不是对象，已丢弃')
            continue
        ids = [x for x in (f.get('flagIds') or []) if x in items_by_id]
        unknown = [x for x in (f.get('flagIds') or []) if x not in items_by_id]
        if unknown:
            issues.append(f'{f.get("object")}：引用了不存在的 flagId {unknown}，已忽略')
        if not ids:
            issues.append(f'{f.get("object")}：没有有效的 flagId，整条丢弃')
            continue
        items = [items_by_id[x] for x in ids]

        verdict = f.get('verdict')
        if verdict not in VERDICTS:
            issues.append(f'{f.get("object")}：结论 {verdict!r} 不合法，改为 uncertain')
            verdict = 'uncertain'
        confidence = _as_float(f.get('confidence'))

        op = f.get('proposedAction') or 'none'
        if op not in OPERATIONS:
            issues.append(f'{f.get("object")}：操作 {op!r} 不在操作库里，改为先问用户')
            op = 'ask_user'
        else:
            allowed = OPERATIONS[op][2]
            bad = sorted({it['section'] for it in items} - allowed) if '*' not in allowed else []
            only_rules = OPERATIONS[op][3]
            if bad:
                issues.append(f'{f.get("object")}：操作 {op} 不适用于 {bad}，改为先问用户')
                op = 'ask_user'
            elif only_rules and not any(it['rule'] in only_rules for it in items):
                issues.append(f'{f.get("object")}：操作 {op} 只能用于 {sorted(only_rules)} 规则命中的对象，改为先问用户')
                op = 'ask_user'
        line = _red_line(op, items, evidence)
        if line:
            issues.append(f'{f.get("object")}：建议的 {op} 触犯{line}，改为先问用户')
            op = 'ask_user'

        question = f.get('questionForUser') or None
        # 提示注入的保险：规则判为高风险、或数据里有像是写给 AI 的指令的项，模型判"正常"不算数（用户确认过的除外）
        risky = [it for it in items if (it.get('severity') == 'high' or it.get('injectionSuspect')) and 'userConfirmed' not in it]
        if verdict == 'normal' and risky:
            why = '数据里有像是写给 AI 的指令' if any(it.get('injectionSuspect') for it in risky) else '规则判为高风险'
            issues.append(f'{f.get("object")}：{why}，模型却判为正常，改为拿不准并请用户确认')
            verdict = 'uncertain'
            question = f'规则认为这一项风险较高（{why}），但 AI 判断为正常。这是你认识、自己安装的东西吗？'
        needs_user = verdict == 'uncertain' or confidence < MIN_CONFIDENCE or op == 'ask_user'
        if needs_user and not question:
            question = f'这一项需要你确认：{f.get("object")}（{f.get("userExplanation") or "原因见证据"}）'
            issues.append(f'{f.get("object")}：需要问用户但没给问题，已补默认问题')

        findings.append({
            'flagIds': ids, 'object': f.get('object'), 'verdict': verdict, 'confidence': confidence,
            'evidence': f.get('evidence'), 'proposedAction': op,
            'actionName': OPERATIONS[op][0], 'risk': OPERATIONS[op][1],  # 风险由操作库决定
            'userExplanation': f.get('userExplanation'), 'questionForUser': question if needs_user else None,
            'needsUser': needs_user,
            **({'kbId': f['kbId']} if f.get('kbId') else {}),  # 知识库直接给出的结论（judge() 保证模型伪造不了）
        })

    if any(f['verdict'] == 'suspicious' for f in findings) and not output.get('malwareSuspected'):
        issues.append('结论里有"可疑"，但整体判断为"没有中毒迹象"，前后不一致（未修改，留给人工复核）')

    covered = {x for f in findings for x in f['flagIds']}
    missing = [x for x in items_by_id if x not in covered]
    if missing:
        issues.append(f'有 {len(missing)} 条预筛结果没有被研判：{missing}')

    tips = []
    for t in (output.get('tips') or [])[:3]:
        if isinstance(t, dict) and t.get('title') and t.get('body'):
            tips.append({'title': str(t['title']), 'body': str(t['body']),
                         'relatedFlagIds': [x for x in (t.get('relatedFlagIds') or []) if x in items_by_id]})

    clean = {
        'headline': str(output.get('headline') or ''),
        'malwareSuspected': bool(output.get('malwareSuspected')),
        'malwareReasoning': str(output.get('malwareReasoning') or ''),
        'top3': [str(x) for x in (output.get('top3') or [])][:3],
        'findings': findings,
        'tips': tips,
        'notCollected': [str(x) for x in (output.get('notCollected') or [])],
        'unjudgedFlagIds': missing,
    }
    return clean, issues


def cleared_flag_ids(clean):
    """研判为正常且置信度足够的 flag，不再扣健康分。"""
    return {x for f in clean['findings']
            if f['verdict'] == 'normal' and f['confidence'] >= MIN_CONFIDENCE for x in f['flagIds']}
