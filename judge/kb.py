"""知识库（进度表 1.6）：已经弄清楚的对象直接下结论，其余作为提示交给模型。

数据在 kb/seed.json。每条的 match 条件全部满足才算命中；when 按"某软件装没装"区分情况。
- direct=true 且 certainty=confirmed：直接生成结论，不发给模型（更快、更稳、更省钱），
  但照样经过 validate 的操作库和红线检查
- 其余：在证据包对应条目上附 knowledge，模型结合证据判断
用户确认过的条目（userConfirmed）不直接下结论，让模型结合用户的说明。
"""
import copy
import json
import os
import re

KB_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'kb', 'seed.json')
TEXT_KEYS = ('object', 'path', 'company', 'evidence')


def load_kb(path=KB_FILE):
    with open(path, encoding='utf-8') as fh:
        return json.load(fh)['entries']


def _as_list(v):
    return v if isinstance(v, list) else [v]


def _search(patterns, text):
    return bool(text) and any(re.search(p, text, re.I) for p in _as_list(patterns))


def _installed(pattern, software):
    return any(re.search(pattern, f"{a.get('name') or ''} {a.get('publisher') or ''}", re.I) for a in software)


def lookup(flag, ctx, path, software, entries):
    """返回第一条命中的知识库条目；path 是对象所在位置（证据包里的 _first_path）。"""
    ctx = ctx if isinstance(ctx, dict) else {}
    texts = {
        'object': flag.get('object'),
        'path': path,
        # 厂商可能在上下文的不同字段里，svchost 那类规则只写在证据里
        'company': ' '.join(str(ctx.get(k) or '') for k in ('company', 'signer', 'provider', 'publisher'))
                   + ' ' + str(flag.get('evidence') or ''),
        'evidence': flag.get('evidence'),
    }
    for e in entries:
        m = e.get('match', {})
        if 'section' in m and flag.get('section') not in _as_list(m['section']):
            continue
        if 'rule' in m and flag.get('rule') not in _as_list(m['rule']):
            continue
        if not all(_search(m[k], texts[k]) for k in TEXT_KEYS if k in m):
            continue
        if m.get('signed') and not (ctx.get('signature') == 'Valid' or '签名：Valid' in str(flag.get('evidence') or '')):
            continue  # 按名字和路径识别时要求签名有效，防止恶意程序冒名
        w = e.get('when', {})
        if 'installed' in w and not _installed(w['installed'], software):
            continue
        if 'notInstalled' in w and _installed(w['notInstalled'], software):
            continue
        return e
    return None


def is_direct(entry):
    return bool(entry.get('direct')) and entry.get('certainty') == 'confirmed'


def note(entry):
    """附在证据包条目上的提示。"""
    return {'kbId': entry['id'], 'product': entry.get('product'), 'verdict': entry['verdict'],
            'proposedAction': entry.get('proposedAction'), 'explanation': entry.get('explanation'),
            'certainty': entry.get('certainty'), 'direct': is_direct(entry),
            **({'question': entry['question']} if entry.get('question') else {})}


def split_direct(evidence, entries):
    """把证据包拆成"发给模型的部分"和"知识库直接给出的结论"（与模型输出同格式，之后一起校验）。"""
    by_id = {e['id']: e for e in entries}
    groups, keep = {}, []
    for it in evidence['items']:
        k = it.get('knowledge')
        if k and k['direct'] and 'userConfirmed' not in it:
            groups.setdefault(k['kbId'], []).append(it)
        else:
            keep.append(it)

    findings = []
    for kb_id, items in groups.items():
        e = by_id[kb_id]
        objects = list(dict.fromkeys(it['object'] for it in items))
        findings.append({
            'flagIds': [it['flagId'] for it in items],
            'object': f'{e["product"]}：{"、".join(objects)}',
            'verdict': e['verdict'], 'confidence': e['confidence'],
            'evidence': '；'.join(f'{it["flagId"]} {it.get("evidence") or ""}' for it in items)
                        + f'（知识库 {kb_id}：{e.get("source") or ""}）',
            'proposedAction': e.get('proposedAction') or 'none',
            'userExplanation': e.get('explanation'),
            'questionForUser': e.get('question'),
            'kbId': kb_id,
        })

    model_ev = copy.deepcopy(evidence)
    model_ev['items'] = keep
    used = {it.get('contextRef') for it in keep}
    model_ev['sharedContext'] = {k: v for k, v in evidence.get('sharedContext', {}).items() if k in used}
    if findings:
        # 让模型知道这些已经有结论，写总结和"最该处理的 3 件事"时可以参考，但不用再逐条研判
        model_ev['handledByKnowledgeBase'] = [
            {'flagIds': f['flagIds'], 'object': f['object'], 'verdict': f['verdict'], 'proposedAction': f['proposedAction']}
            for f in findings]
    return model_ev, findings
