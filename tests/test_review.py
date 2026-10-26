"""误判复核表的单元测试。运行：python -m unittest discover -s tests"""
import csv
import glob
import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'judge'))

from review import (COLUMNS, classify, wilson, format_summary, init_review, read_review, rows_from_judge,  # noqa: E402
                    summarize, write_review)

SCRIPT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'judge', 'review.py')


def result(n):
    findings = [{'flagIds': [f'F{i:03d}'], 'object': f'对象{i}', 'verdict': 'leftover' if i % 2 else 'normal',
                 'confidence': 0.9, 'proposedAction': 'none', 'actionName': '不处理', 'userExplanation': '说明，含逗号',
                 'needsUser': False} for i in range(1, n + 1)]
    return {'judgement': {'findings': findings}}


def make_machine(root, name, marks, extra=()):
    """在 root/name 下放一份已经标好的复核表。marks 依次填进每一行的"判定"。"""
    d = os.path.join(root, name)
    os.makedirs(d)
    rows = rows_from_judge(result(len(marks)))
    for r, m in zip(rows, marks):
        r['判定'] = m
        if m == '错':
            r['应该是'] = '正常'
    rows += [dict({c: '' for c in COLUMNS}, **e) for e in extra]
    path = os.path.join(d, 'pcdoctor-20261025-000000-review.csv')
    write_review(path, rows)
    return path


class ReviewTest(unittest.TestCase):
    def test_init_writes_bom_csv_and_keeps_marks(self):
        with tempfile.TemporaryDirectory() as d:
            jp = os.path.join(d, 'pcdoctor-20261025-000000-judge.json')
            with open(jp, 'w', encoding='utf-8') as fh:
                json.dump(result(3), fh, ensure_ascii=False)
            path, created = init_review(jp)
            self.assertTrue(created)
            self.assertTrue(path.endswith('pcdoctor-20261025-000000-review.csv'))
            with open(path, 'rb') as fh:
                self.assertTrue(fh.read().startswith(b'\xef\xbb\xbf'))  # Excel 靠 BOM 识别 UTF-8
            rows = read_review(path)
            self.assertEqual(len(rows), 3)
            self.assertEqual(rows[0]['模型结论'], '卸载残留')
            self.assertEqual(rows[0]['给用户的说明'], '说明，含逗号')

            rows[0]['判定'] = '对'
            write_review(path, rows)
            self.assertFalse(init_review(jp)[1])  # 已存在时不覆盖
            self.assertEqual(read_review(path)[0]['判定'], '对')

            init_review(jp, force=True)  # 重新研判：已标过的旧表改名保留
            self.assertEqual(read_review(path)[0]['判定'], '')
            old = glob.glob(os.path.join(d, '*-review-old-*.csv'))
            self.assertEqual(len(old), 1)
            self.assertEqual(read_review(old[0])[0]['判定'], '对')

    def test_reads_gbk_saved_by_excel(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, 'x-review.csv')
            with open(path, 'w', encoding='gb18030', newline='') as fh:
                w = csv.DictWriter(fh, fieldnames=COLUMNS)
                w.writeheader()
                w.writerow(dict({c: '' for c in COLUMNS}, 编号='F001', 对象='迅雷', 判定='错'))
            self.assertEqual(read_review(path)[0]['判定'], '错')

    def test_classify(self):
        for m, k in (('对', 'right'), (' √ ', 'right'), ('OK', 'right'), ('错', 'wrong'), ('×', 'wrong'),
                     ('说法不准', 'inexact'), ('漏报', 'missed'), ('', None), ('大概吧', 'unknown')):
            self.assertEqual(classify(m), k, m)

    def test_summary_and_gate(self):
        with tempfile.TemporaryDirectory() as d:
            # 9 台全对 + 1 台 20 条里错 1 条、不准 1 条，外加 1 条漏报
            for i in range(9):
                make_machine(d, f'朋友{i}', ['对'] * 20)
            make_machine(d, '小王', ['对'] * 18 + ['错', '不准'], extra=[{'对象': '某驱动', '判定': '漏报'}])
            s = summarize([d])
            self.assertEqual(len(s['machines']), 10)
            self.assertEqual(s['total']['reviewed'], 200)
            self.assertEqual(s['total']['missed'], 1)
            self.assertAlmostEqual(s['rate'], 1 / 200)
            self.assertAlmostEqual(s['strictRate'], 2 / 200)
            self.assertTrue(s['gatePassed'])
            text = format_summary(s)
            self.assertIn('已通过', text)
            self.assertIn('卸载残留 → 正常', text)

    def test_gate_uses_confidence_upper_bound(self):
        lo, hi = wilson(14, 200)
        self.assertAlmostEqual(lo, 0.042, places=3)
        self.assertAlmostEqual(hi, 0.114, places=3)
        with tempfile.TemporaryDirectory() as d:
            for i in range(10):  # 10 台、200 条，错 14 条：点估计 7% < 10%，但上限 11.4%，不能放行
                make_machine(d, f'朋友{i}', ['对'] * 19 + ['错'] if i < 4 else ['对'] * 18 + ['错'] * 2 if i < 9 else ['对'] * 20)
            s = summarize([d])
            self.assertEqual(s['judged']['wrong'], 14)
            self.assertFalse(s['gatePassed'])
            self.assertIn('95% 置信区间', format_summary(s))

    def test_knowledge_base_rows_do_not_count_for_gate(self):
        with tempfile.TemporaryDirectory() as d:
            path = make_machine(d, 'A', ['对', '错'])
            rows = read_review(path)
            rows[1]['来源'] = '知识库 thunder-drivers'
            write_review(path, rows)
            s = summarize([d])
            self.assertEqual(s['judged'], {'reviewed': 1, 'wrong': 0})
            self.assertEqual(s['bySource']['知识库']['wrong'], 1)

    def test_gate_needs_complete_machines(self):
        with tempfile.TemporaryDirectory() as d:
            for i in range(9):
                make_machine(d, f'朋友{i}', ['对'] * 5)
            make_machine(d, '没标完', ['对', '', ''])  # 没标完的机器不算"复核完"
            s = summarize([d])
            self.assertEqual(s['completeMachines'], 9)
            self.assertFalse(s['gatePassed'])
            self.assertIn('还有 2 条没标', format_summary(s))

    def test_latest_review_per_folder(self):
        with tempfile.TemporaryDirectory() as d:
            make_machine(d, 'A', ['错'])
            newer = os.path.join(d, 'A', 'pcdoctor-20261026-000000-review.csv')
            rows = rows_from_judge(result(1))
            rows[0]['判定'] = '对'
            write_review(newer, rows)
            s = summarize([d])
            self.assertEqual(len(s['machines']), 1)
            self.assertEqual(s['total']['right'], 1)  # 同一台机器只看最新的一份

    def test_cli(self):
        with tempfile.TemporaryDirectory() as d:
            make_machine(d, '小王', ['对', '错'])
            env = dict(os.environ, PYTHONIOENCODING='utf-8')
            out = subprocess.run([sys.executable, SCRIPT, 'stats', d], capture_output=True, text=True, encoding='utf-8', env=env)
            self.assertEqual(out.returncode, 0, out.stderr)
            self.assertIn('误判率 50.0%', out.stdout)
            out = subprocess.run([sys.executable, SCRIPT, 'stats', os.path.join(d, '空')],
                                 capture_output=True, text=True, encoding='utf-8', env=env)
            self.assertNotEqual(out.returncode, 0)


if __name__ == '__main__':
    unittest.main()
