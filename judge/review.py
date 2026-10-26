"""误判复核表：每条结论人工标"对 / 错 / 不准"，自动算误判率，对照关卡 1（10 台机器、误判 < 10%）。

研判完会在报告旁边自动生成 pcdoctor-<时间>-review.csv，用 Excel / WPS 打开，在"判定"一列填：
    对      结论和说法都对
    错      结论错了（比如把正常软件判成残留、该处理的判成正常、建议的操作不对）
    不准    结论大体对，但说法有问题（夸大、推测、时间说错）
    漏报    模型漏掉的问题：自己在表格最后加一行，写上对象和备注
    （留空 = 还没复核，不参与统计）

汇总：python judge\\review.py stats 桌面\\pcdoctor-friends
    每个文件夹算一台机器，同一文件夹里有多份复核表时取最新一份。

Excel 另存时可能把编码改成 GBK，读取时两种都认。
"""
import argparse
import csv
import datetime
import glob
import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from render import VERDICT_NAMES  # noqa: E402

COLUMNS = ['编号', '对象', '模型结论', '来源', '置信度', '建议操作', '给用户的说明', '判定', '应该是', '备注']
HINT = '判定填：对 / 错 / 不准 / 漏报（漏报请在最后加一行）；"应该是"写正确的结论，可留空'
GATE_MACHINES = 10
GATE_RATE = 0.10  # 关卡看的是非知识库结论误判率的 95% 置信上限，不是点估计（10 台 × 20 条时，错 7% 的上限已超过 11%）

# 判定的写法尽量宽松：手机上、WPS 里打字习惯不同
MARKS = {
    'right': {'对', '正确', '✓', '√', 'y', 'yes', 'ok', '1'},
    'wrong': {'错', '错误', '误判', '✗', '×', 'x', 'n', 'no', '0'},
    'inexact': {'不准', '说法不准', '不太准', '~', '？', '?'},
    'missed': {'漏报', '漏', '漏判'},
}
MARK_NAMES = {'right': '对', 'wrong': '错', 'inexact': '不准', 'missed': '漏报'}


def review_path(judge_path):
    return judge_path[:-len('-judge.json')] + '-review.csv' if judge_path.endswith('-judge.json') \
        else os.path.splitext(judge_path)[0] + '-review.csv'


def rows_from_judge(result):
    rows = []
    for f in result['judgement']['findings']:
        rows.append({
            '编号': '、'.join(f['flagIds']),
            '对象': f['object'],
            '模型结论': VERDICT_NAMES.get(f['verdict'], f['verdict']),
            '来源': f'知识库 {f["kbId"]}' if f.get('kbId') else '模型',
            '置信度': f'{f["confidence"]:.2f}',
            '建议操作': f.get('actionName') or f.get('proposedAction') or '',
            '给用户的说明': f.get('userExplanation') or '',
            '判定': '', '应该是': '', '备注': '',
        })
    return rows


def write_review(path, rows):
    # utf-8-sig：Excel 双击打开时靠 BOM 识别 UTF-8，否则中文乱码
    with open(path, 'w', encoding='utf-8-sig', newline='') as fh:
        w = csv.DictWriter(fh, fieldnames=COLUMNS)
        w.writeheader()
        w.writerows(rows)


def init_review(judge_path, force=False):
    """生成复核表；已经存在时不覆盖（里面可能已经有人工标注）。返回 (路径, 是否新建)。"""
    path = review_path(judge_path)
    if os.path.exists(path):
        if not force:
            return path, False
        if any(classify(r.get('判定')) for r in read_review(path)):
            # 同一份报告重新研判：旧表里已经标过的判定不能丢，改名留着（不再参与统计）
            stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
            os.replace(path, path[:-len('.csv')] + f'-old-{stamp}.csv')
    with open(judge_path, encoding='utf-8') as fh:
        write_review(path, rows_from_judge(json.load(fh)))
    return path, True


def read_review(path):
    with open(path, 'rb') as fh:
        data = fh.read()
    for enc in ('utf-8-sig', 'gb18030'):
        try:
            text = data.decode(enc)
            break
        except UnicodeDecodeError:
            continue
    else:
        raise ValueError(f'{path}：既不是 UTF-8 也不是 GBK 编码')
    return list(csv.DictReader(io.StringIO(text)))


def classify(mark):
    m = (mark or '').strip().lower()
    if not m:
        return None
    for kind, words in MARKS.items():
        if m in words:
            return kind
    return 'unknown'


def machine_stats(rows):
    s = {'total': 0, 'right': 0, 'wrong': 0, 'inexact': 0, 'missed': 0, 'unmarked': 0, 'unknown': []}
    for r in rows:
        kind = classify(r.get('判定'))
        if kind == 'missed':
            s['missed'] += 1
            continue
        if not (r.get('编号') or r.get('对象')):
            continue  # 空行
        s['total'] += 1
        if kind is None:
            s['unmarked'] += 1
        elif kind == 'unknown':
            s['unknown'].append(r.get('判定'))
            s['unmarked'] += 1
        else:
            s[kind] += 1
    s['reviewed'] = s['right'] + s['wrong'] + s['inexact']
    return s


def rate(wrong, reviewed):
    return wrong / reviewed if reviewed else None


def wilson(k, n, z=1.96):
    """误判率的 95% Wilson 置信区间，样本少时比"正负两个标准差"可靠。返回 (下限, 上限)。"""
    if not n:
        return None, None
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * ((p * (1 - p) / n + z * z / (4 * n * n)) ** 0.5) / d
    return max(0.0, c - h), min(1.0, c + h)


def source_of(row):
    s = row.get('来源') or ''
    return '知识库' if s.startswith('知识库') else ('模型' if s in ('模型', '') else s)


def find_reviews(paths):
    """每个文件夹算一台机器，取最新的一份复核表（文件名里有时间，按名字排序即可）。"""
    files = []
    for p in paths:
        if os.path.isfile(p):
            files.append(p)
        else:
            files += glob.glob(os.path.join(p, '**', '*-review.csv'), recursive=True)
    latest = {}
    for f in sorted(files):
        latest[os.path.dirname(os.path.abspath(f))] = f
    return sorted(latest.values())


def summarize(paths):
    machines, wrong_by, by_src = [], {}, {}
    for f in find_reviews(paths):
        rows = read_review(f)
        for r in rows:
            k = classify(r.get('判定'))
            if k in ('right', 'wrong', 'inexact'):
                b = by_src.setdefault(source_of(r), {'reviewed': 0, 'wrong': 0})
                b['reviewed'] += 1
                b['wrong'] += k == 'wrong'
        s = machine_stats(rows)
        s['name'] = os.path.basename(os.path.dirname(os.path.abspath(f))) or f
        s['file'] = f
        machines.append(s)
        for r in rows:
            if classify(r.get('判定')) in ('wrong', 'inexact'):
                key = (r.get('模型结论') or '?', r.get('应该是') or '（没写）')
                wrong_by.setdefault(key, []).append(f'{s["name"]}：{r.get("对象")}')
    tot = {k: sum(m[k] for m in machines) for k in ('total', 'reviewed', 'right', 'wrong', 'inexact', 'missed', 'unmarked')}
    done = [m for m in machines if m['reviewed'] and not m['unmarked']]
    r = rate(tot['wrong'], tot['reviewed'])
    # 知识库直接结论是查表，不能拿来拉低"判断"的误判率：关卡只看规则和模型的结论
    judged = {'reviewed': sum(b['reviewed'] for s, b in by_src.items() if s != '知识库'),
              'wrong': sum(b['wrong'] for s, b in by_src.items() if s != '知识库')}
    lo, hi = wilson(judged['wrong'], judged['reviewed'])
    gate = len(done) >= GATE_MACHINES and hi is not None and hi < GATE_RATE
    return {'machines': machines, 'total': tot, 'rate': r, 'bySource': by_src, 'judged': judged, 'ci': (lo, hi),
            'strictRate': rate(tot['wrong'] + tot['inexact'], tot['reviewed']),
            'completeMachines': len(done), 'gatePassed': gate, 'wrongBy': wrong_by}


def _pad(text, width):
    """按显示宽度补空格：中文占两格，否则表格对不齐。"""
    text = str(text)
    while sum(2 if ord(c) > 0x2E80 else 1 for c in text) > width:
        text = text[:-1]
    return text + ' ' * (width - sum(2 if ord(c) > 0x2E80 else 1 for c in text))


def _pct(x):
    return '—' if x is None else f'{x * 100:.1f}%'


def format_summary(s):
    out = [_pad('机器', 22) + ' 已复核/结论   对   错 不准 漏报   误判率', '-' * 68]
    for m in s['machines']:
        tail = f'　（还有 {m["unmarked"]} 条没标）' if m['unmarked'] else ''
        out.append(f'{_pad(m["name"], 22)} {m["reviewed"]:>5}/{m["total"]:<5} {m["right"]:>4} {m["wrong"]:>4} '
                   f'{m["inexact"]:>4} {m["missed"]:>4}   {_pct(rate(m["wrong"], m["reviewed"])):>6}{tail}')
        if m['unknown']:
            out.append(f'    看不懂的判定：{"、".join(map(str, m["unknown"]))}（请改成 对 / 错 / 不准 / 漏报）')
    t = s['total']
    out += ['-' * 68,
            f'合计 {len(s["machines"])} 台：已复核 {t["reviewed"]} / {t["total"]} 条，'
            f'错 {t["wrong"]}，不准 {t["inexact"]}，漏报 {t["missed"]}',
            f'误判率 {_pct(s["rate"])}（只算"错"）；严格误判率 {_pct(s["strictRate"])}（"错"和"不准"都算）',
            '按来源：' + '；'.join(f'{k} {b["wrong"]} / {b["reviewed"]} 错（{_pct(rate(b["wrong"], b["reviewed"]))}）'
                                  for k, b in sorted(s['bySource'].items())),
            f'规则和模型的结论：误判率 {_pct(rate(s["judged"]["wrong"], s["judged"]["reviewed"]))}，'
            f'95% 置信区间 {_pct(s["ci"][0])} – {_pct(s["ci"][1])}',
            f'关卡 1：{GATE_MACHINES} 台复核完的机器，且规则和模型结论误判率的 95% 上限 < {GATE_RATE:.0%}　→　'
            + ('✅ 已通过' if s['gatePassed'] else
               f'还没通过（复核完 {s["completeMachines"]} / {GATE_MACHINES} 台，上限 {_pct(s["ci"][1])}）')]
    if s['wrongBy']:
        out += ['', '错和不准的分布（模型结论 → 应该是），改提示词或加知识库时优先看数量多的：']
        for (got, want), items in sorted(s['wrongBy'].items(), key=lambda kv: -len(kv[1])):
            out.append(f'  {got} → {want}：{len(items)} 条　' + '；'.join(items[:5]) + ('…' if len(items) > 5 else ''))
    return '\n'.join(out)


def main():
    ap = argparse.ArgumentParser(description='误判复核表')
    sub = ap.add_subparsers(dest='cmd', required=True)
    p = sub.add_parser('init', help='从 -judge.json 生成复核表（已存在时不覆盖）')
    p.add_argument('judge', nargs='+')
    p.add_argument('--force', action='store_true', help='覆盖已有的复核表（会丢掉已经标好的判定）')
    p = sub.add_parser('stats', help='汇总复核表，算误判率')
    p.add_argument('paths', nargs='+', help='复核表文件或文件夹（会在子文件夹里查找）')
    args = ap.parse_args()

    if args.cmd == 'init':
        for j in args.judge:
            path, created = init_review(j, args.force)
            print(('已生成：' if created else '已存在，没有覆盖（要重来请加 --force）：') + path)
        print(HINT)
    else:
        s = summarize(args.paths)
        if not s['machines']:
            sys.exit('没有找到 *-review.csv 复核表')
        print(format_summary(s))


if __name__ == '__main__':
    main()
