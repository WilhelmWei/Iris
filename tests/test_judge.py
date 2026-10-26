"""研判脚本的单元测试（不调用模型）。运行：python -m unittest discover -s tests"""
import http.server
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'judge'))

from evidence import build_evidence, SoftwareIndex  # noqa: E402
from known import confirm, load_known, match, save_known  # noqa: E402
from run_judge import call_model, judge, load_system_prompt, parse_json  # noqa: E402
from render import render_markdown  # noqa: E402
from scoring import health_score  # noqa: E402
from validate import validate  # noqa: E402


def flag(i, section, rule, severity, obj, evidence='证据'):
    return {'id': f'F{i:03d}', 'section': section, 'rule': rule, 'severity': severity, 'object': obj, 'evidence': evidence}


EXCL = {'paths': ['D:\\quickvpn'], 'processes': ['Update.exe', 'uninst.exe'], 'extensions': []}

REPORT = {
    'tool': {'version': '0.2.2', 'isAdmin': True, 'quick': False, 'incomplete': []},
    'machine': {'manufacturer': 'HUAWEI', 'model': 'TEST'},
    'flags': [
        flag(1, 'security', 'defender-exclusion', 'high', 'Defender 排除项'),
        flag(2, 'security', 'defender-exclusion', 'medium', 'Defender 排除项'),
        flag(3, 'security', 'defender-exclusion', 'medium', 'Defender 排除项'),
        flag(4, 'processes', 'runs-from-user-folder', 'low', 'wps.exe'),
        flag(5, 'drivers', 'seed-anticheat-accel', 'medium', 'XLWFP'),
        flag(6, 'drivers', 'third-party-class-filter', 'medium', 'PECKP'),
        flag(7, 'drivers', 'seed-bank-security', 'medium', 'msfilter'),
        flag(8, 'software', 'duplicate-category', 'info', '音乐'),
        flag(9, 'drivers', 'binary-missing', 'low', 'PECKP'),
    ],
    'sections': {
        'resources': {'os': {'caption': 'Windows 11'}, 'memory': {'totalGB': 16, 'usedPct': 54}},
        'processes': {'items': [{'name': 'wps.exe', 'path': 'C:\\Users\\[用户]\\AppData\\Local\\Kingsoft\\WPS Office\\12\\office6\\wps.exe',
                                 'company': 'Zhuhai Kingsoft Office Software Co.,Ltd', 'signature': 'Valid'}]},
        'drivers': {'kernelDrivers': {'items': [
            {'name': 'XLWFP', 'state': 'Running', 'startMode': 'Auto', 'path': 'C:\\Windows\\System32\\drivers\\xlwfp.sys',
             'company': '', 'signer': 'ShenZhen Thunder Networking Technologies Ltd.'},
            {'name': 'PECKP', 'state': 'Running', 'startMode': 'Manual', 'path': 'C:\\Windows\\System32\\drivers\\PECKP_x64.sys',
             'company': 'Bank', 'signer': 'Bank Ltd'},
            {'name': 'msfilter', 'state': 'Running', 'startMode': 'Manual', 'path': 'C:\\Windows\\System32\\drivers\\msf.sys',
             'company': 'Microsoft Corporation', 'signer': 'Microsoft Windows'}]},
            'classFilters': [{'className': 'Keyboard', 'kind': 'UpperFilters', 'driver': 'PECKP', 'thirdParty': True}],
            'packages': {'items': []}},
        'software': {'items': [{'name': 'WPS Office', 'publisher': 'Kingsoft Corp.', 'location': None},
                               {'name': '迅雷', 'publisher': '迅雷网络技术有限公司', 'location': 'D:\\迅雷\\Thunder'},
                               {'name': 'Edge', 'publisher': 'Microsoft Corporation', 'location': None}],
                     'duplicateCategories': {'音乐': ['QQ音乐', '网易云音乐']}},
        'security': {'defenderExclusions': EXCL, 'defender': {'realTimeProtection': True}},
    },
}


def finding(ids, action, verdict='leftover', confidence=0.9, **kw):
    f = {'flagIds': ids, 'object': kw.pop('object', 'x'), 'verdict': verdict, 'confidence': confidence,
         'evidence': 'e', 'proposedAction': action, 'userExplanation': '说明', 'questionForUser': None}
    f.update(kw)
    return f


class EvidenceTest(unittest.TestCase):
    def test_owner_by_company_and_location(self):
        idx = SoftwareIndex(REPORT['sections']['software']['items'])
        self.assertEqual(idx.owner('C:\\x\\wps.exe', 'Zhuhai Kingsoft Office Software Co.,Ltd'), 'WPS Office')
        self.assertEqual(idx.owner('D:\\迅雷\\Thunder\\Program\\Thunder.exe'), '迅雷')
        self.assertEqual(idx.owner(None, '深圳市迅雷网络技术有限公司'), '迅雷')
        self.assertIsNone(idx.owner(None, 'Microsoft Corporation'))  # 微软太宽泛，不按厂商对

    def test_shared_context_and_items(self):
        ev = build_evidence(REPORT)
        self.assertEqual(len(ev['items']), len(REPORT['flags']))
        excl = [it for it in ev['items'] if it['rule'] == 'defender-exclusion']
        self.assertTrue(all(it.get('contextRef') == excl[0]['contextRef'] for it in excl))
        # 空字段会被去掉以节省字符
        self.assertEqual(ev['sharedContext'][excl[0]['contextRef']]['exclusions'],
                         {'paths': EXCL['paths'], 'processes': EXCL['processes']})
        wps = next(it for it in ev['items'] if it['object'] == 'wps.exe')
        self.assertEqual(wps['ownerSoftware'], 'WPS Office')
        peckp = [it for it in ev['items'] if it['object'] == 'PECKP']
        self.assertEqual(len({it.get('contextRef') for it in peckp}), 1)  # 两条 PECKP 共用一份上下文
        ctx = ev['sharedContext'][peckp[0]['contextRef']]
        self.assertEqual(ctx['classFilters'], [{'className': 'Keyboard', 'kind': 'UpperFilters'}])


class ScoringTest(unittest.TestCase):
    def test_same_rule_merged_and_capped(self):
        s = health_score(REPORT['flags'])
        sec = s['categories']['安全']
        # 30 + 10/3 + 10/3 = 36.7，上限 45
        self.assertEqual(sec['deductions'][0]['points'], 36.7)
        self.assertEqual(sec['score'], 63.3)
        self.assertEqual(len(sec['deductions']), 1)

    def test_condition_rules_not_clearable(self):
        flags = [flag(1, 'storage', 'system-drive-low', 'medium', 'C:')]
        s = health_score(flags, cleared_ids={'F001'})
        self.assertEqual(s['categories']['存储']['score'], 90)  # 磁盘不足是事实，判为正常也照样扣分

    def test_cleared_flags_not_deducted(self):
        s = health_score(REPORT['flags'], cleared_ids={'F004'})
        perf = s['categories']['性能与后台']
        self.assertEqual(perf['score'], 100)
        self.assertEqual(perf['deductions'][0]['note'], '研判为正常，不扣分')


class ValidateTest(unittest.TestCase):
    def setUp(self):
        self.ev = build_evidence(REPORT)

    def run_one(self, f):
        clean, issues = validate({'findings': [f]}, self.ev)
        return (clean['findings'][0] if clean['findings'] else None), issues

    def test_unknown_operation(self):
        f, issues = self.run_one(finding(['F001'], 'registry.delete'))
        self.assertEqual(f['proposedAction'], 'ask_user')
        self.assertTrue(f['needsUser'] and f['questionForUser'])

    def test_operation_not_for_section(self):
        f, _ = self.run_one(finding(['F004'], 'driver.remove_after_reboot'))
        self.assertEqual(f['proposedAction'], 'ask_user')

    def test_operation_limited_to_rules(self):
        # 驱动登记残留不能用"删除旧版驱动包"
        f, issues = self.run_one(finding(['F005'], 'driver.delete_old_version'))
        self.assertEqual(f['proposedAction'], 'ask_user')
        self.assertTrue(any('只能用于' in x for x in issues))

    def test_problem_verdict(self):
        f, issues = self.run_one(finding(['F004'], 'none', verdict='problem'))
        self.assertEqual(f['verdict'], 'problem')
        self.assertFalse(any('不合法' in x for x in issues))

    def test_red_line_microsoft_driver(self):
        f, issues = self.run_one(finding(['F007'], 'driver.remove_after_reboot'))
        self.assertEqual(f['proposedAction'], 'ask_user')
        self.assertTrue(any('红线 3' in x for x in issues))

    def test_red_line_class_filter(self):
        # 挂在键盘过滤器上的驱动，文件丢了也不能直接删登记
        f, issues = self.run_one(finding(['F006', 'F009'], 'service.delete_orphan'))
        self.assertEqual(f['proposedAction'], 'ask_user')
        self.assertTrue(any('红线 2' in x for x in issues))
        f, issues = self.run_one(finding(['F006'], 'driver.remove_after_reboot'))
        self.assertEqual(f['proposedAction'], 'driver.remove_after_reboot')
        self.assertEqual(f['risk'], 'red')  # 风险由操作库决定

    def test_uncertain_gets_question(self):
        f, issues = self.run_one(finding(['F005'], 'none', verdict='uncertain', confidence=0.4, object='XLWFP'))
        self.assertTrue(f['needsUser'])
        self.assertIn('XLWFP', f['questionForUser'])
        self.assertTrue(any('补默认问题' in x for x in issues))

    def test_suspicious_without_malware_flagged(self):
        clean, issues = validate({'malwareSuspected': False, 'findings': [finding(['F001'], 'defender.review', verdict='suspicious')]}, self.ev)
        self.assertTrue(any('前后不一致' in x for x in issues))
        clean, issues = validate({'malwareSuspected': True, 'findings': [finding(['F001'], 'defender.review', verdict='suspicious')]}, self.ev)
        self.assertFalse(any('前后不一致' in x for x in issues))

    def test_invalid_ids_and_coverage(self):
        clean, issues = validate({'findings': [finding(['F999'], 'none')]}, self.ev)
        self.assertEqual(clean['findings'], [])
        self.assertEqual(len(clean['unjudgedFlagIds']), len(REPORT['flags']))


class EndToEndTest(unittest.TestCase):
    def test_prompt_has_operations(self):
        p = load_system_prompt()
        self.assertIn('driver.remove_after_reboot', p)
        self.assertNotIn('{{OPERATIONS}}', p)

    def test_parse_fenced_json(self):
        self.assertEqual(parse_json('好的\n```json\n{"a": 1}\n```'), {'a': 1})

    def test_judge_and_render(self):
        raw = {
            'headline': '整体健康，有一个已卸载 VPN 的残留', 'malwareSuspected': False, 'malwareReasoning': '无可疑进程',
            'top3': ['删除 QuickVPN 留下的排除项'],
            'findings': [finding(['F001', 'F002', 'F003'], 'defender.review', object='Defender 排除项'),
                         finding(['F004'], 'none', verdict='normal', object='wps.exe'),
                         finding(['F005'], 'none', verdict='normal', confidence=0.8, object='XLWFP'),
                         finding(['F006', 'F009'], 'driver.remove_after_reboot', verdict='uncertain', confidence=0.5,
                                 object='PECKP', questionForUser='你还用这家银行的网银吗？'),
                         finding(['F007', 'F008'], 'none', verdict='normal', object='其他')],
            'tips': [{'title': '卸载软件后排除项不会自动删除', 'body': '……', 'relatedFlagIds': ['F001']}],
            'notCollected': [],
        }
        result = judge(REPORT, raw, 'test-model')
        self.assertEqual(result['issues'], [])
        self.assertEqual(result['judgement']['unjudgedFlagIds'], [])
        self.assertEqual(result['score']['categories']['性能与后台']['score'], 100)  # wps 研判为正常
        md = render_markdown(result)
        for s in ('健康分', '没有发现中毒迹象', '你还用这家银行的网银吗？', '卸载软件后排除项不会自动删除', '卸载残留',
                  'Microsoft Defender', '不修改任何安全设置'):
            self.assertIn(s, md)


class KnownTest(unittest.TestCase):
    """用户确认过的结论：下次不再问；识别类和 Defender 设置不再扣分，客观状态照样扣分。"""

    def setUp(self):
        self.flags = REPORT['flags'] + [flag(10, 'storage', 'system-drive-low', 'medium', 'C:')]

    def test_confirm_and_match(self):
        entries = []
        added = confirm(self.flags, ['F001', 'F010'], '是我自己加的', entries)
        self.assertEqual(len(added), 2)
        self.assertEqual(confirm(self.flags, ['F001'], '再确认一次', entries), [])  # 不重复记录
        self.assertTrue(match(self.flags[0], entries))
        changed = dict(self.flags[0], evidence='证据变了')
        self.assertIsNone(match(changed, entries))  # 证据变了就重新问
        with self.assertRaises(ValueError):
            confirm(self.flags, ['F999'], 'x', entries)

    def test_confirmed_scoring(self):
        s = health_score(self.flags, confirmed_ids={'F001', 'F002', 'F003', 'F010'})
        self.assertEqual(s['categories']['安全']['score'], 100)        # Defender 排除项：用户确认后不扣
        self.assertEqual(s['categories']['存储']['score'], 90)         # 磁盘不足：确认了也照样扣
        notes = [d.get('note') for d in s['categories']['安全']['deductions']]
        self.assertIn('你已确认，不扣分', notes)

    def test_evidence_carries_confirmation(self):
        entries = []
        confirm(REPORT['flags'], ['F005'], '迅雷是我装的', entries)
        ev = build_evidence(REPORT, entries)
        xl = next(it for it in ev['items'] if it['flagId'] == 'F005')
        self.assertEqual(xl['userConfirmed']['note'], '迅雷是我装的')

    def test_cli_list_and_confirm(self):
        with tempfile.TemporaryDirectory() as d:
            rp = os.path.join(d, 'pcdoctor-report-20261024-000000.json')
            with open(rp, 'w', encoding='utf-8') as fh:
                json.dump(REPORT, fh, ensure_ascii=False)
            script = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'judge', 'run_judge.py')
            env = dict(os.environ, PYTHONIOENCODING='utf-8')
            out = subprocess.run([sys.executable, script, rp, '--confirm', 'F005', '--note', '迅雷是我装的'],
                                 capture_output=True, text=True, encoding='utf-8', env=env)
            self.assertEqual(out.returncode, 0, out.stderr)
            self.assertEqual(len(load_known(os.path.join(d, 'pcdoctor-known.json'))), 1)
            out = subprocess.run([sys.executable, script, rp, '--list'], capture_output=True, text=True, encoding='utf-8', env=env)
            self.assertIn('F005', out.stdout)
            self.assertIn('[已确认]', out.stdout)
            out = subprocess.run([sys.executable, script, rp, '--confirm', 'F005'], capture_output=True, text=True, encoding='utf-8', env=env)
            self.assertNotEqual(out.returncode, 0)  # 没写说明不让确认


class FakeDeepSeek(http.server.BaseHTTPRequestHandler):
    """本机假接口：按顺序返回 replies 里的内容，记录收到的请求。"""
    replies, requests = [], []

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])).decode('utf-8'))
        FakeDeepSeek.requests.append({'path': self.path, 'auth': self.headers.get('Authorization'), 'body': body})
        status, content = FakeDeepSeek.replies.pop(0)
        payload = json.dumps({'choices': [{'message': {'content': content}}], 'usage': {'total_tokens': 100}}
                             if status == 200 else {'error': {'message': content}}).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *args):
        pass


class CallModelTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        os.environ['no_proxy'] = '127.0.0.1,localhost'  # 本机请求不走代理
        cls.server = http.server.HTTPServer(('127.0.0.1', 0), FakeDeepSeek)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.base = f'http://127.0.0.1:{cls.server.server_port}'

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def setUp(self):
        FakeDeepSeek.replies, FakeDeepSeek.requests = [], []

    def test_request_shape_and_retry(self):
        FakeDeepSeek.replies = [(200, '这不是 JSON'), (200, '```json\n{"headline": "好"}\n```')]
        out, usage = call_model('系统提示', {'items': []}, 'deepseek-chat', self.base, 'sk-test', timeout=10)
        self.assertEqual(out, {'headline': '好'})
        self.assertEqual(len(usage), 2)
        first, second = FakeDeepSeek.requests
        self.assertEqual(first['path'], '/chat/completions')
        self.assertEqual(first['auth'], 'Bearer sk-test')
        self.assertEqual(first['body']['response_format'], {'type': 'json_object'})
        self.assertEqual(len(second['body']['messages']), 4)  # 带上了上次的错误输出和纠正要求

    def test_reasoner_has_no_json_mode(self):
        FakeDeepSeek.replies = [(200, '{}')]
        call_model('p', {}, 'deepseek-reasoner', self.base, 'k', timeout=10)
        self.assertNotIn('response_format', FakeDeepSeek.requests[0]['body'])

    def test_dns_failure_falls_back_to_ipv4(self):
        import socket
        import urllib.error
        import run_judge
        FakeDeepSeek.replies = [(200, '{"headline": "ok"}')]
        real_urlopen = run_judge.urllib.request.urlopen
        calls = []

        def flaky(req, timeout):
            calls.append(socket.getaddrinfo is run_judge._ipv4_only_getaddrinfo)
            if len(calls) == 1:  # 第一次模拟路由器 DNS 出错
                raise urllib.error.URLError(socket.gaierror(11001, 'getaddrinfo failed'))
            return real_urlopen(req, timeout=timeout)
        try:
            run_judge.urllib.request.urlopen = flaky
            out, _ = call_model('p', {}, 'deepseek-chat', self.base, 'k', timeout=10)
        finally:
            run_judge.urllib.request.urlopen = real_urlopen
            socket.getaddrinfo = run_judge._system_getaddrinfo
        self.assertEqual(out, {'headline': 'ok'})
        self.assertEqual(calls, [False, True])  # 第二次已改为只查 IPv4

    def test_bad_key_message(self):
        FakeDeepSeek.replies = [(401, 'invalid api key')]
        with self.assertRaises(SystemExit) as cm:
            call_model('p', {}, 'deepseek-chat', self.base, 'bad', timeout=10)
        self.assertIn('API Key 不对', str(cm.exception.code))


if __name__ == '__main__':
    unittest.main()
