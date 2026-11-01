"""用户画像：「关于这台电脑」里的回答（用来做什么、最烦什么、是否常带出门、谁在用、补充说明）、
用户选的 Iris 性格（style）和问卷回答。

清洗后放进证据包的 userProfile，告诉模型这台电脑怎么用、用户最在意什么。都是用户自己填的文字，
同样只当数据看待；长度和条数都有上限。
"""


def _strs(v, n, m):
    if not isinstance(v, list):
        return []
    out = []
    for x in v:
        if isinstance(x, str) and x.strip():
            out.append(x.strip()[:m])
        if len(out) >= n:
            break
    return out


def clean_profile(p):
    """返回清洗后的画像；什么都没填时返回 None（证据包里就不出现 userProfile）。"""
    if not isinstance(p, dict):
        return None
    out = {}
    uses = _strs(p.get('uses'), 6, 20)
    if uses:
        out['uses'] = uses
    pains = _strs(p.get('pains'), 6, 20)
    if pains:
        out['pains'] = pains
    if isinstance(p.get('portable'), bool):
        out['portable'] = p['portable']
    who = p.get('who')
    if isinstance(who, str) and who.strip():
        out['who'] = who.strip()[:20]
    note = p.get('note')
    if isinstance(note, str) and note.strip():
        out['note'] = note.strip()[:300]
    style = p.get('style')
    if isinstance(style, str) and style.strip():
        out['style'] = style.strip()[:60]
    answers = []
    raw = p.get('answers')
    for a in (raw[-10:] if isinstance(raw, list) else []):
        if not isinstance(a, dict):
            continue
        q, ans = a.get('question'), a.get('answer')
        if not (isinstance(q, str) and q.strip() and isinstance(ans, str) and ans.strip()):
            continue
        item = {'question': q.strip()[:120], 'answer': ans.strip()[:200]}
        ids = _strs(a.get('flagIds'), 10, 10)
        if ids:
            item['flagIds'] = ids
        answers.append(item)
    if answers:
        out['answers'] = answers
    return out or None
