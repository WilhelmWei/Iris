"""把研判结果渲染成给人看的中文 Markdown 报告。"""
from oplib import RISK_NAMES

VERDICT_NAMES = {'normal': '正常', 'problem': '需处理', 'leftover': '卸载残留', 'bloat': '捆绑 / 流氓',
                 'suspicious': '可疑', 'uncertain': '拿不准'}


def _cell(s):
    return str(s or '').replace('|', '\\|').replace('\n', ' ')


def render_markdown(result):
    j, score = result['judgement'], result['score']
    out = ['# 电脑体检报告', '']
    out.append(f'**健康分 {score["total"]} / 100**　{j["headline"]}')
    out.append('')
    if result.get('offline'):
        out.append(f'> 生成时间 {result["generatedAt"]} · 纯规则模式（没有联网、没有调用 AI） · 采集器 {result["collectorVersion"]}'
                   '　结论来自固定规则和知识库，不结合上下文判断，比 AI 研判粗；拿不准的都列成了问题')
    else:
        out.append(f'> 生成时间 {result["generatedAt"]} · 模型 {result["model"]} · 采集器 {result["collectorVersion"]}'
                   '　健康分由代码按规则计算，模型只负责判断和解释')
    out += ['', '## 有没有中毒', '']
    out.append(('**发现可疑迹象，需要进一步核实。** ' if j['malwareSuspected'] else '**没有发现中毒迹象。** ') + j['malwareReasoning'])
    out += ['', '> 查病毒交给 Windows 自带的 Microsoft Defender：本工具只读取它的检测结果（加 `-Scan` 运行时会先发起一次快速扫描），'
            '不修改任何安全设置。我们鼓励用好已有的强大工具，而不是重复造一个杀毒引擎。']

    if j['top3']:
        out += ['', '## 最该处理的 3 件事', '']
        out += [f'{i}. {t}' for i, t in enumerate(j['top3'], 1)]

    questions = [f for f in j['findings'] if f['needsUser']]
    if questions:
        out += ['', '## 需要你确认的问题', '']
        out += [f'- **{_cell(f["object"])}**：{f["questionForUser"]}' for f in questions]

    kb_ids = result.get('kbFlagIds') or []
    if kb_ids:
        out += ['', f'> 有 {len(kb_ids)} 项是已经弄清楚的常见软件组件，由知识库直接给出结论（表格里标"知识库"），没有交给模型']

    confirmed = result.get('confirmedFlagIds') or []
    if confirmed:
        out += ['', f'> 你以前确认过的 {len(confirmed)} 项这次不再提问（记录在报告旁边的 pcdoctor-known.json）']

    if j['tips']:
        out += ['', '## 小知识', '']
        for t in j['tips']:
            out += [f'**{t["title"]}**', '', t['body'], '']

    out += ['', '## 逐项结论', '', '| 对象 | 结论 | 置信度 | 建议 | 风险 | 说明 |', '| --- | --- | --- | --- | --- | --- |']
    order = {'suspicious': 0, 'problem': 1, 'leftover': 2, 'bloat': 3, 'uncertain': 4, 'normal': 5}
    for f in sorted(j['findings'], key=lambda f: order.get(f['verdict'], 9)):
        conf = f'{f["confidence"]:.2f}' + ('（知识库）' if f.get('kbId') else '')
        out.append(f'| {_cell(f["object"])} | {VERDICT_NAMES.get(f["verdict"], f["verdict"])} | {conf} '
                   f'| {_cell(f["actionName"])} | {RISK_NAMES.get(f["risk"], "—")} | {_cell(f["userExplanation"])} |')

    out += ['', '## 健康分扣分明细', '']
    for name, c in score['categories'].items():
        out.append(f'- **{name}**（权重 {c["weight"]}%）：{c["score"]} 分')
        for d in c['deductions']:
            note = f'，{d["note"]}' if d.get('note') else ''
            out.append(f'  - −{d["points"]}　{d["rule"]}：{"、".join(d["objects"])}（{"、".join(d["flagIds"])}{note}）')

    out += ['', '## 证据（专业模式）', '']
    for f in j['findings']:
        out.append(f'- **{_cell(f["object"])}**（{"、".join(f["flagIds"])}，建议 `{f["proposedAction"]}`）：{f["evidence"]}')

    if j['notCollected']:
        out += ['', '## 没能检查的方面', '']
        out += [f'- {x}' for x in j['notCollected']]

    if result['issues']:
        out += ['', '## 校验记录', '', '模型输出中被代码修正的地方（用于复核误判率）：', '']
        out += [f'- {x}' for x in result['issues']]
    return '\n'.join(out) + '\n'
