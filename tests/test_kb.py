"""知识库的单元测试。运行：python -m unittest discover -s tests"""
import os
import re
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'judge'))

from kb import is_direct, load_kb, lookup  # noqa: E402
from oplib import OPERATIONS  # noqa: E402
from render import render_markdown  # noqa: E402
from run_judge import judge, prepare  # noqa: E402
from validate import VERDICTS  # noqa: E402

KB = load_kb()
WPS = 'C:\\Users\\[用户]\\AppData\\Local\\Kingsoft\\WPS Office\\12.1.0.28802\\office6\\wps.exe'


def flag(i, section, rule, obj, evidence='证据', severity='low'):
    return {'id': f'F{i:03d}', 'section': section, 'rule': rule, 'severity': severity, 'object': obj, 'evidence': evidence}


def report(software=('WPS Office', '迅雷')):
    return {
        'tool': {'version': 'test'},
        'flags': [
            flag(1, 'processes', 'runs-from-user-folder', 'wps.exe', f'从用户可写目录运行（签名：Valid）：{WPS}'),
            flag(2, 'drivers', 'seed-anticheat-accel', 'XLWFP', '疑似下载器驱动（Running，签名者：ShenZhen Thunder Networking Technologies Ltd.）', 'medium'),
            flag(3, 'storage', 'system-drive-low', 'C:', '系统盘只剩 10 GB', 'medium'),
        ],
        'sections': {
            'processes': {'items': [{'name': 'wps.exe', 'path': WPS, 'company': 'Zhuhai Kingsoft Office Software Co.,Ltd', 'signature': 'Valid'}]},
            'drivers': {'kernelDrivers': {'items': [
                {'name': 'XLWFP', 'state': 'Running', 'startMode': 'Auto', 'path': 'C:\\WINDOWS\\system32\\drivers\\xlwfp.sys',
                 'company': '', 'signer': 'ShenZhen Thunder Networking Technologies Ltd.', 'signature': 'Valid'}]}},
            'software': {'items': [{'name': n, 'publisher': ''} for n in software]},
        },
    }


class SeedTest(unittest.TestCase):
    """种子表本身的格式检查：手工编辑 JSON 时最容易出错。"""

    def test_entries_well_formed(self):
        ids = [e['id'] for e in KB]
        self.assertEqual(len(ids), len(set(ids)), '编号重复')
        for e in KB:
            with self.subTest(e['id']):
                self.assertIn(e['verdict'], VERDICTS)
                self.assertIn(e['proposedAction'], OPERATIONS)
                self.assertIn(e['certainty'], ('confirmed', 'likely', 'guess'))
                self.assertTrue(0 < e['confidence'] <= 1)
                self.assertTrue(e.get('explanation') and e.get('source') and e.get('product'))
                for k in ('object', 'path', 'company', 'evidence'):
                    for p in (e['match'].get(k) if isinstance(e['match'].get(k), list) else [e['match'].get(k)]):
                        if p:
                            re.compile(p)
                # 操作要适用于条目限定的报告节
                allowed = OPERATIONS[e['proposedAction']][2]
                sections = e['match'].get('section')
                if '*' not in allowed:
                    self.assertTrue(sections, '有操作的条目必须限定报告节')
                    for s in (sections if isinstance(sections, list) else [sections]):
                        self.assertIn(s, allowed)

    def test_direct_entries_cannot_be_impersonated(self):
        """直接下结论的条目，不能只靠名字识别：必须同时要求签名有效，以及路径或厂商。"""
        for e in KB:
            if is_direct(e):
                with self.subTest(e['id']):
                    self.assertTrue(e['match'].get('signed'))
                    self.assertTrue(e['match'].get('path') or e['match'].get('company'))
                    self.assertNotIn(e['verdict'], ('suspicious', 'uncertain'))


class LookupTest(unittest.TestCase):
    def test_wps_needs_path_and_signature(self):
        r = report()
        f, ctx = r['flags'][0], r['sections']['processes']['items'][0]
        sw = r['sections']['software']['items']
        self.assertEqual(lookup(f, ctx, WPS, sw, KB)['id'], 'wps-user-folder')
        fake = 'C:\\Users\\[用户]\\AppData\\Local\\Temp\\wps.exe'
        self.assertIsNone(lookup(f, dict(ctx, path=fake), fake, sw, KB))           # 同名但不在 WPS 目录
        unsigned = dict(f, evidence='从用户可写目录运行（签名：NotSigned）')
        self.assertIsNone(lookup(unsigned, dict(ctx, signature='NotSigned'), WPS, sw, KB))  # 没有有效签名

    def test_thunder_depends_on_installed(self):
        r = report()
        f, ctx = r['flags'][1], r['sections']['drivers']['kernelDrivers']['items'][0]
        self.assertEqual(lookup(f, ctx, ctx['path'], r['sections']['software']['items'], KB)['id'], 'thunder-drivers')
        hit = lookup(f, ctx, ctx['path'], [{'name': 'WPS Office'}], KB)
        self.assertEqual(hit['id'], 'thunder-drivers-leftover')  # 迅雷已卸载：残留，交给模型并问用户
        self.assertFalse(is_direct(hit))


class JudgeWithKbTest(unittest.TestCase):
    def test_direct_findings_skip_model(self):
        r = report()
        evidence, model_ev, kb_findings = prepare(r, kb=KB)
        self.assertEqual([it['flagId'] for it in model_ev['items']], ['F003'])  # 只有磁盘不足交给模型
        self.assertEqual(len(evidence['items']), 3)
        self.assertEqual({f['kbId'] for f in kb_findings}, {'wps-user-folder', 'thunder-drivers'})
        self.assertEqual(len(model_ev['handledByKnowledgeBase']), 2)

        raw = {'headline': '还行', 'malwareSuspected': False, 'malwareReasoning': '无', 'top3': [],
               'findings': [
                   {'flagIds': ['F003'], 'object': 'C:', 'verdict': 'problem', 'confidence': 0.9, 'evidence': 'e',
                    'proposedAction': 'cache.clean', 'userExplanation': '清理一下'},
                   # 模型越权：研判了知识库已经处理的项，还自称来自知识库
                   {'flagIds': ['F001'], 'object': 'wps.exe', 'verdict': 'suspicious', 'confidence': 0.9, 'evidence': 'e',
                    'proposedAction': 'none', 'userExplanation': 'x', 'kbId': 'fake'}],
               'tips': [], 'notCollected': []}
        result = judge(r, raw, 'test', kb=KB)
        j = result['judgement']
        self.assertEqual(j['unjudgedFlagIds'], [])
        self.assertNotIn('suspicious', [f['verdict'] for f in j['findings']])  # 模型越权的那条被整条去掉
        self.assertEqual(len(j['findings']), 3)
        kb_rows = [f for f in j['findings'] if f.get('kbId')]
        self.assertEqual({f['kbId'] for f in kb_rows}, {'wps-user-folder', 'thunder-drivers'})
        self.assertTrue(any('已由知识库下结论' in x for x in result['issues']))
        self.assertEqual(sorted(result['kbFlagIds']), ['F001', 'F002'])
        # WPS 研判为正常 → 不扣分；磁盘不足照样扣
        self.assertEqual(result['score']['categories']['性能与后台']['score'], 100)
        self.assertEqual(result['score']['categories']['存储']['score'], 90)
        md = render_markdown(result)
        self.assertIn('（知识库）', md)
        self.assertIn('由知识库直接给出结论', md)

    def test_user_confirmation_goes_to_model(self):
        r = report()
        known = [{'rule': 'runs-from-user-folder', 'object': 'wps.exe', 'evidence': r['flags'][0]['evidence'], 'note': '我自己装的'}]
        _, model_ev, kb_findings = prepare(r, known, KB)
        self.assertIn('F001', [it['flagId'] for it in model_ev['items']])  # 用户回答过的，让模型结合用户的说明
        self.assertEqual({f['kbId'] for f in kb_findings}, {'thunder-drivers'})

    def test_no_kb_unchanged(self):
        r = report()
        evidence, model_ev, kb_findings = prepare(r)
        self.assertEqual(kb_findings, [])
        self.assertEqual(len(model_ev['items']), 3)
        self.assertNotIn('knowledge', str(evidence))


if __name__ == '__main__':
    unittest.main()
