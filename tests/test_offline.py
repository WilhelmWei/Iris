"""纯规则模式的单元测试。运行：python -m unittest discover -s tests"""
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'judge'))
sys.path.insert(0, HERE)

from kb import load_kb  # noqa: E402
from offline import RULES, SECTION_RULES, offline_output  # noqa: E402
from oplib import OPERATIONS  # noqa: E402
from render import render_markdown  # noqa: E402
from run_judge import judge, prepare  # noqa: E402
from test_judge import REPORT, flag  # noqa: E402
from validate import VERDICTS  # noqa: E402

COLLECTOR = os.path.join(HERE, '..', 'collector', 'pcdoctor-collect.ps1')


def collector_rules():
    with open(COLLECTOR, encoding='utf-8-sig') as fh:
        return set(re.findall(r"Add-Flag '(\w+)' '([\w-]+)'", fh.read()))


class OfflineTest(unittest.TestCase):
    def test_every_collector_rule_has_default(self):
        """采集脚本新增规则时，纯规则模式也要补上默认结论。"""
        pairs = collector_rules()
        self.assertGreater(len(pairs), 50)
        missing = sorted(f'{s}:{r}' for s, r in pairs if f'{s}:{r}' not in SECTION_RULES and r not in RULES)
        self.assertEqual(missing, [])

    def test_defaults_valid_for_their_sections(self):
        for s, r in collector_rules():
            verdict, conf, action, text = SECTION_RULES.get(f'{s}:{r}') or RULES[r]
            with self.subTest(f'{s}:{r}'):
                self.assertIn(verdict, VERDICTS)
                self.assertTrue(text)
                if action:
                    self.assertIn(action, OPERATIONS)
                    allowed = OPERATIONS[action][2]
                    self.assertTrue('*' in allowed or s in allowed, f'{action} 不适用于 {s}')
                    only = OPERATIONS[action][3]
                    self.assertTrue(not only or r in only, f'{action} 只用于 {only}')

    def test_judge_offline_report(self):
        _, model_ev, _ = prepare(REPORT, kb=load_kb())
        raw = offline_output(model_ev)
        result = judge(REPORT, raw, '纯规则模式', kb=load_kb())
        result['offline'] = True
        j = result['judgement']
        self.assertEqual(j['unjudgedFlagIds'], [])
        self.assertFalse(j['malwareSuspected'])
        # PECKP：知识库提示"先问还用不用这家网银"
        peckp = [f for f in j['findings'] if 'F009' in f['flagIds']][0]
        self.assertEqual(peckp['proposedAction'], 'ask_user')
        self.assertIn('网银', peckp['questionForUser'])
        # 同一规则、同一对象的 3 条排除项合并成一条
        excl = [f for f in j['findings'] if 'F001' in f['flagIds']][0]
        self.assertEqual(sorted(excl['flagIds']), ['F001', 'F002', 'F003'])
        self.assertTrue(excl['needsUser'])
        md = render_markdown(result)
        self.assertIn('纯规则模式', md)
        self.assertIn('卸载软件后，Defender 排除项不会自动删除', md)  # 小知识和命中的规则相关

    def test_red_line_without_kb(self):
        # 没有知识库时，PECKP 的登记残留按规则默认建议 service.delete_orphan；它挂在键盘过滤器上，被红线 2 拦下
        _, model_ev, _ = prepare(REPORT)
        result = judge(REPORT, offline_output(model_ev), '纯规则模式')
        peckp = [f for f in result['judgement']['findings'] if 'F009' in f['flagIds']][0]
        self.assertEqual(peckp['proposedAction'], 'ask_user')
        self.assertTrue(any('红线 2' in x for x in result['issues']))

    def test_malware_rules(self):
        rep = dict(REPORT, flags=REPORT['flags'] + [flag(20, 'security', 'active-threat', 'high', 'Trojan:Win32/Test')])
        _, model_ev, _ = prepare(rep)
        raw = offline_output(model_ev)
        self.assertTrue(raw['malwareSuspected'])
        self.assertIn('Trojan:Win32/Test', raw['malwareReasoning'])
        self.assertIn('Trojan:Win32/Test', raw['top3'][0])  # 可疑项排在最前面

    def test_cli_offline_needs_no_key(self):
        with tempfile.TemporaryDirectory() as d:
            rp = os.path.join(d, 'pcdoctor-report-20261025-000000.json')
            with open(rp, 'w', encoding='utf-8') as fh:
                json.dump(REPORT, fh, ensure_ascii=False)
            env = {k: v for k, v in os.environ.items() if k != 'DEEPSEEK_API_KEY'}
            env['PYTHONIOENCODING'] = 'utf-8'
            script = os.path.join(HERE, '..', 'judge', 'run_judge.py')
            out = subprocess.run([sys.executable, script, rp, '--offline'], capture_output=True, text=True, encoding='utf-8', env=env)
            self.assertEqual(out.returncode, 0, out.stderr)
            self.assertIn('纯规则模式', out.stdout)
            for suffix in ('-judge.json', '-judge.md', '-review.csv'):
                self.assertTrue(os.path.exists(os.path.join(d, 'pcdoctor-20261025-000000' + suffix)), suffix)
            self.assertFalse(os.path.exists(os.path.join(d, 'pcdoctor-20261025-000000-judge-raw.json')))


if __name__ == '__main__':
    unittest.main()
