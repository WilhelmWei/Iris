"""AI 电脑体检 · LLM 研判（MVP）

读取采集报告 → 生成精简证据包 → 调用 OpenAI 兼容接口（默认 DeepSeek）→
校验输出 → 代码计算健康分 → 写出 JSON 和中文 Markdown 报告。

用法：
    set DEEPSEEK_API_KEY=sk-...            （PowerShell：$env:DEEPSEEK_API_KEY="sk-..."）
    python judge\\run_judge.py              默认取桌面上最新的 pcdoctor-report-*.json
    python judge\\run_judge.py 报告.json --dry-run    只生成证据包，不调用模型（看看会发送什么）
"""
import argparse
import datetime
import glob
import json
import os
import re
import socket
import sys
import threading
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from evidence import build_evidence  # noqa: E402
from kb import load_kb, split_direct  # noqa: E402
from offline import offline_output  # noqa: E402
from known import confirm, known_path, load_known, match, save_known  # noqa: E402
from oplib import operations_table  # noqa: E402
from render import render_markdown  # noqa: E402
from review import init_review  # noqa: E402
from scoring import health_score  # noqa: E402
from validate import validate, cleared_flag_ids  # noqa: E402

PROMPT_FILE = os.path.join(HERE, '..', 'prompts', 'judge-prompt.md')
EVIDENCE_WARN_CHARS = 60000


def load_system_prompt():
    with open(PROMPT_FILE, encoding='utf-8') as fh:
        text = fh.read()
    start = text.index('## 系统提示')
    body = text[start + len('## 系统提示'):].strip()
    return body.replace('{{OPERATIONS}}', operations_table())


def find_latest_report():
    cands = []
    for d in (os.path.join(os.path.expanduser('~'), 'Desktop'), os.getcwd()):
        cands += glob.glob(os.path.join(d, 'pcdoctor-report-*.json'))
    if not cands:
        sys.exit('没有找到 pcdoctor-report-*.json，请在命令里写上报告路径')
    return max(cands, key=os.path.getmtime)


def parse_json(text):
    """模型偶尔会在 JSON 外面包一层 ```json 代码块。"""
    text = text.strip()
    m = re.search(r'```(?:json)?\s*(\{.*\})\s*```', text, re.S)
    if m:
        text = m.group(1)
    return json.loads(text)


DNS_RETRIES = 4
_system_getaddrinfo = socket.getaddrinfo


def _ipv4_only_getaddrinfo(host, port, family=0, type=0, proto=0, flags=0):
    return _system_getaddrinfo(host, port, socket.AF_INET, type, proto, flags)


def post_chat(url, api_key, body, timeout):
    """直接调用 OpenAI 兼容的 /chat/completions 接口，只用标准库，不需要安装任何包。"""
    req = urllib.request.Request(url, data=json.dumps(body, ensure_ascii=False).encode('utf-8'), method='POST',
                                 headers={'Content-Type': 'application/json', 'Authorization': f'Bearer {api_key}'})
    for attempt in range(1, DNS_RETRIES + 1):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return json.loads(resp.read().decode('utf-8'))
        except urllib.error.HTTPError as e:
            detail = e.read().decode('utf-8', 'replace')[:500]
            hint = {401: '（API Key 不对）', 402: '（账户余额不足）', 429: '（请求太频繁，稍后再试）'}.get(e.code, '')
            sys.exit(f'模型接口返回错误 {e.code}{hint}：{detail}')
        except (urllib.error.URLError, TimeoutError) as e:
            reason = getattr(e, 'reason', e)
            if not isinstance(reason, socket.gaierror) or attempt == DNS_RETRIES:
                sys.exit(f'连不上模型接口 {url}：{reason}')
            # 域名解析失败：有些路由器处理 IPv6 地址查询（AAAA）会出错，导致整次解析失败。
            # 第一次失败后改为只查 IPv4，再失败就隔几秒重试
            if socket.getaddrinfo is not _ipv4_only_getaddrinfo:
                socket.getaddrinfo = _ipv4_only_getaddrinfo
                print('域名解析失败，改为只查 IPv4 地址后重试…', flush=True)
            else:
                print(f'域名解析又失败了，{2 * attempt} 秒后第 {attempt + 1} 次重试…', flush=True)
                time.sleep(2 * attempt)


def call_model(system_prompt, evidence, model, base_url, api_key, timeout=300):
    url = base_url.rstrip('/') + '/chat/completions'
    messages = [{'role': 'system', 'content': system_prompt},
                {'role': 'user', 'content': json.dumps(evidence, ensure_ascii=False)}]
    body = {'model': model, 'messages': messages, 'max_tokens': 8000}
    if 'reasoner' not in model:
        body['temperature'] = 0.2
        body['response_format'] = {'type': 'json_object'}
    usage = []
    for attempt in range(2):
        data = post_chat(url, api_key, body, timeout)
        usage.append(data.get('usage'))
        text = (data.get('choices') or [{}])[0].get('message', {}).get('content') or ''
        try:
            return parse_json(text), usage
        except json.JSONDecodeError:
            if attempt == 1:
                raise
            body['messages'] = messages + [{'role': 'assistant', 'content': text},
                                           {'role': 'user', 'content': '上面的输出不是合法 JSON。请只输出一个符合格式的 JSON 对象。'}]


def prepare(report, known=(), kb=()):
    """返回 (完整证据包, 发给模型的证据包, 知识库直接给出的结论)。"""
    evidence = build_evidence(report, known, kb)
    model_ev, kb_findings = split_direct(evidence, kb)
    return evidence, model_ev, kb_findings


def merge_kb(raw_output, kb_findings):
    """把知识库的结论并进模型输出；模型的结论里去掉知识库已经处理过的编号，也不许它自称来自知识库。"""
    if not isinstance(raw_output, dict):
        return raw_output, []
    handled = {x for f in kb_findings for x in f['flagIds']}
    issues, findings = [], []
    for f in raw_output.get('findings') or []:
        if isinstance(f, dict):
            f = {k: v for k, v in f.items() if k != 'kbId'}
            dup = [x for x in (f.get('flagIds') or []) if x in handled]
            if dup:
                issues.append(f'{f.get("object")}：{dup} 已由知识库下结论，模型的这部分已忽略')
                f['flagIds'] = [x for x in f['flagIds'] if x not in handled]
                if not f['flagIds']:
                    continue
        findings.append(f)
    return dict(raw_output, findings=findings + kb_findings), issues


def judge(report, raw_output, model, known=(), kb=()):
    """把模型原始输出变成最终结果（与调用模型分开，便于测试）。"""
    evidence, _, kb_findings = prepare(report, known, kb)
    merged, kb_issues = merge_kb(raw_output, kb_findings)
    clean, issues = validate(merged, evidence)
    issues = kb_issues + issues
    confirmed = [f['id'] for f in report.get('flags', []) if match(f, known)]
    return {
        'schema': 'pcdoctor.judge/0.1',
        'generatedAt': datetime.datetime.now().strftime('%Y-%m-%d %H:%M'),
        'model': model,
        'collectorVersion': report.get('tool', {}).get('version'),
        'score': health_score(report.get('flags', []), cleared_flag_ids(clean), confirmed),
        'judgement': clean,
        'confirmedFlagIds': confirmed,
        'kbFlagIds': [x for f in kb_findings for x in f['flagIds']],
        'issues': issues,
    }


def main():
    ap = argparse.ArgumentParser(description='AI 电脑体检 · LLM 研判')
    ap.add_argument('report', nargs='?', help='采集报告路径，默认取桌面上最新的一份')
    ap.add_argument('--model', default='deepseek-chat')
    ap.add_argument('--base-url', default='https://api.deepseek.com')
    ap.add_argument('--api-key-env', default='DEEPSEEK_API_KEY', help='从哪个环境变量读取 API Key')
    ap.add_argument('--dry-run', action='store_true', help='只生成证据包，不调用模型')
    ap.add_argument('--list', action='store_true', help='列出报告里的预筛结果和编号')
    ap.add_argument('--confirm', nargs='+', metavar='编号', help='把这些预筛结果记为"已确认"，下次不再问（配合 --note）')
    ap.add_argument('--note', help='确认时的说明，例如"这是我自己装的远程软件"')
    ap.add_argument('--no-kb', action='store_true', help='不用知识库（对比知识库的效果时用）')
    ap.add_argument('--offline', action='store_true', help='纯规则模式：不联网、不用 Key，按固定规则和知识库出一份基础报告')
    args = ap.parse_args()

    path = args.report or find_latest_report()
    with open(path, encoding='utf-8-sig') as fh:
        report = json.load(fh)
    kpath = known_path(path)
    known = load_known(kpath)

    if args.list:
        for f in report.get('flags', []):
            mark = '　[已确认]' if match(f, known) else ''
            print(f'{f["id"]}　{f["severity"]:<6} {f["rule"]:<26} {f["object"]}{mark}')
            print(f'      {f["evidence"][:100]}')
        return
    if args.confirm:
        if not args.note:
            sys.exit('请用 --note 写一句说明，例如 --note "这是我自己装的远程软件"')
        try:
            added = confirm(report.get('flags', []), args.confirm, args.note, known)
        except ValueError as e:
            sys.exit(str(e))
        save_known(kpath, known)
        for e in added:
            print(f'已记住：{e["object"]}（{e["rule"]}）——{e["note"]}')
        print(f'共 {len(known)} 条确认记录，保存在 {kpath}')
        return

    stem = os.path.splitext(path)[0].replace('pcdoctor-report-', 'pcdoctor-')
    kb = [] if args.no_kb else load_kb()
    _, evidence, kb_findings = prepare(report, known, kb)
    ev_text = json.dumps(evidence, ensure_ascii=False, indent=1)
    with open(stem + '-evidence.json', 'w', encoding='utf-8') as fh:
        fh.write(ev_text)
    compact = len(json.dumps(evidence, ensure_ascii=False, separators=(',', ':')))
    print(f'报告：{path}')
    if kb_findings:
        print(f'知识库直接下结论 {sum(len(f["flagIds"]) for f in kb_findings)} 条（{len(kb_findings)} 组），不发给模型')
    print(f'证据包：{len(evidence["items"])} 条预筛结果，{compact} 字符（完整报告 {os.path.getsize(path)} 字节）')
    print(f'将要发送的内容已保存：{stem}-evidence.json')
    if compact > EVIDENCE_WARN_CHARS:
        print('提示：证据包偏大，可能超出模型上下文或费用偏高')
    if args.dry_run:
        return
    if args.offline:
        t = time.time()
        result = judge(report, offline_output(evidence), '纯规则模式', known, kb)
        result['offline'] = True
        result['elapsedSec'] = round(time.time() - t, 1)
        write_outputs(stem, None, result)
        return

    api_key = os.environ.get(args.api_key_env)
    if not api_key:
        sys.exit(f'没有找到 API Key：请先设置环境变量 {args.api_key_env}')
    print(f'正在调用 {args.model}，通常需要 30–90 秒…', flush=True)
    t = time.time()
    done = threading.Event()
    if sys.stdout.isatty():  # 直接在终端运行时显示等待秒数；被 run.ps1 调用、输出写进日志时不显示
        def tick():
            while not done.wait(1):
                print(f'\r  已等待 {time.time() - t:.0f} 秒', end='', flush=True)
            print('\r' + ' ' * 20 + '\r', end='', flush=True)
        threading.Thread(target=tick, daemon=True).start()
    try:
        raw, usage = call_model(load_system_prompt(), evidence, args.model, args.base_url, api_key)
    finally:
        done.set()
    result = judge(report, raw, args.model, known, kb)
    result['usage'] = usage
    result['elapsedSec'] = round(time.time() - t, 1)
    write_outputs(stem, raw, result)


def write_outputs(stem, raw, result):
    if raw is not None:
        with open(stem + '-judge-raw.json', 'w', encoding='utf-8') as fh:
            json.dump(raw, fh, ensure_ascii=False, indent=1)
    with open(stem + '-judge.json', 'w', encoding='utf-8') as fh:
        json.dump(result, fh, ensure_ascii=False, indent=1)
    with open(stem + '-judge.md', 'w', encoding='utf-8') as fh:
        fh.write(render_markdown(result))
    review, _ = init_review(stem + '-judge.json', force=True)  # 新的研判对应新的结论，旧表作废

    j = result['judgement']
    print(f'\n健康分 {result["score"]["total"]} / 100　{j["headline"]}')
    if result.get('offline'):
        print('纯规则模式：没有联网、没有调用模型；装好 Key 后去掉 --offline 可以得到更细的 AI 研判')
    else:
        print(f'模型用时 {result["elapsedSec"]} 秒，token 用量：{result.get("usage")}')
    print(f'需要你确认的问题 {sum(f["needsUser"] for f in j["findings"])} 个，校验修正 {len(result["issues"])} 处')
    print(f'报告已保存：{stem}-judge.md')
    print(f'误判复核表：{review}（用 Excel / WPS 打开，在"判定"一列填 对 / 错 / 不准）')


if __name__ == '__main__':
    main()
