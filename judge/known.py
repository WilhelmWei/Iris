"""用户确认过的结论：设计文档里"拿不准就问用户"的答案要记住，下次不再问。

存在报告所在目录的 pcdoctor-known.json，只在本机，不上传。
按"规则 + 对象 + 证据"匹配：同一条规则、同一个对象、证据一字不差，才算同一件事；
证据变了（比如排除项数量变了）就重新问。
"""
import datetime
import json
import os

KNOWN_FILE = 'pcdoctor-known.json'


def known_path(report_path):
    return os.path.join(os.path.dirname(os.path.abspath(report_path)), KNOWN_FILE)


def load_known(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding='utf-8') as fh:
        return json.load(fh).get('entries', [])


def save_known(path, entries):
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump({'schema': 'pcdoctor.known/0.1', 'entries': entries}, fh, ensure_ascii=False, indent=1)


def _key(d):
    return (d.get('rule'), d.get('object'), d.get('evidence'))


def match(flag, entries):
    k = _key(flag)
    for e in entries:
        if _key(e) == k:
            return e
    return None


def confirm(flags, ids, note, entries):
    """把报告里指定编号的预筛结果记为"用户已确认"，返回新增的条目。"""
    by_id = {f['id']: f for f in flags}
    unknown = [i for i in ids if i not in by_id]
    if unknown:
        raise ValueError(f'报告里没有这些编号：{unknown}')
    added = []
    for i in ids:
        f = by_id[i]
        if match(f, entries):
            continue
        e = {'rule': f.get('rule'), 'object': f.get('object'), 'evidence': f.get('evidence'),
             'note': note, 'confirmedAt': datetime.date.today().isoformat()}
        entries.append(e)
        added.append(e)
    return added
