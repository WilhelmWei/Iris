"""附录 A（2026-10-23 实战体检记录）回归测试（进度表 1.5）。

附录 A 的每一行都是一次真实教训。这里在"研判层"复现：构造采集脚本会产生的预筛结果，
检查纯规则模式 + 知识库 + 校验 + 红线 + 算分给出的处理是否符合当时的教训。
采集层（规则是否命中）需要模拟 Windows 接口，留到 .NET 采集器时用接口注入来测。

测不了的行写在 NOT_COVERED 里，测试会检查它和附录 A 的行数对得上，避免悄悄漏掉。
运行：python -m unittest discover -s tests
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'judge'))

from kb import load_kb  # noqa: E402
from offline import offline_output  # noqa: E402
from oplib import OPERATIONS  # noqa: E402
from run_judge import judge, load_system_prompt, prepare  # noqa: E402
from validate import validate  # noqa: E402

KB = load_kb()
COVERED = 12
NOT_COVERED = {
    '24 个服务批量改手动，第一版命令把失败也打印成成功': '属于执行阶段（阶段 3 操作库执行器：执行后必须重新检测验证），MVP 只出报告',
    '设置窗口被管理员窗口遮挡，点击失败': '属于执行架构（后台服务执行、不模拟点击），阶段 3',
    '华为电脑管家累计 1400 多秒 CPU': '缺规则：采集了 topByCpuSec，但没有"单个第三方进程累计 CPU 很高"的预筛规则（待补）',
}


def flag(i, section, rule, severity, obj, evidence='证据'):
    return {'id': f'F{i:03d}', 'section': section, 'rule': rule, 'severity': severity, 'object': obj, 'evidence': evidence}


def make_report(flags, sections=None, software=()):
    s = {'software': {'items': [{'name': n, 'publisher': ''} for n in software]}}
    s.update(sections or {})
    return {'tool': {'version': 'appendix-a'}, 'flags': flags, 'sections': s}


def run_offline(report, kb=KB):
    _, model_ev, _ = prepare(report, kb=kb)
    result = judge(report, offline_output(model_ev), '纯规则模式', kb=kb)
    by_flag = {x: f for f in result['judgement']['findings'] for x in f['flagIds']}
    return result, by_flag


def model_finding(ids, op, verdict='leftover', obj='x'):
    return {'flagIds': ids, 'object': obj, 'verdict': verdict, 'confidence': 0.9, 'evidence': 'e',
            'proposedAction': op, 'userExplanation': 'u', 'questionForUser': None}


DRIVERS = {'drivers': {'kernelDrivers': {'items': [
    {'name': 'PECKP', 'state': 'Running', 'startMode': 'Manual', 'path': 'C:\\Windows\\System32\\drivers\\PECKP_x64.sys',
     'company': 'Bank', 'signer': 'Bank Ltd', 'signature': 'Valid'},
    {'name': 'storahci', 'state': 'Running', 'startMode': 'Boot', 'path': 'C:\\Windows\\System32\\drivers\\storahci.sys',
     'company': 'Microsoft Corporation', 'signer': 'Microsoft Windows', 'signature': 'Valid'},
    {'name': 'vendorboot', 'state': 'Running', 'startMode': 'Boot', 'path': 'C:\\Windows\\System32\\drivers\\vb.sys',
     'company': 'Vendor', 'signer': 'Vendor Ltd', 'signature': 'Valid'}]},
    'classFilters': [{'className': 'Keyboard', 'kind': 'UpperFilters', 'driver': 'PECKP', 'thirdParty': True}]}}


class AppendixATest(unittest.TestCase):
    def test_bookkeeping(self):
        """附录 A 共 15 行：测到的 + 写明原因没测的 = 15。"""
        with open(os.path.join(HERE, '..', 'docs', 'core-design.md'), encoding='utf-8') as fh:
            text = fh.read()
        table = text[text.index('## 附录 A'):text.index('## 待定问题')]
        rows = [l for l in table.splitlines() if l.startswith('| ') and not l.startswith('| 发现') and '---' not in l]
        self.assertEqual(len(rows), COVERED + len(NOT_COVERED))
        for k in NOT_COVERED:
            self.assertTrue(any(k in r for r in rows), k)

    # 1. 开机即发热、内存 80%、Memory Compression 累计 CPU 第一 → 先诊断内存，不是病毒
    def test_01_memory_before_malware(self):
        r = make_report([flag(1, 'resources', 'memory-high', 'medium', '内存'),
                         flag(2, 'resources', 'memory-compression-busy', 'medium', 'Memory Compression')])
        result, f = run_offline(r)
        self.assertFalse(result['judgement']['malwareSuspected'])
        self.assertIn('内存', f['F002']['userExplanation'])
        self.assertIn('不是中毒', f['F002']['userExplanation'])
        self.assertIn('内存诊断优先', load_system_prompt())

    # 2. 怀疑挖矿：GPU 忙不等于中毒，结论附证据、先安抚
    def test_02_gpu_busy_is_not_malware_by_itself(self):
        result, f = run_offline(make_report([flag(1, 'resources', 'gpu-busy-idle', 'medium', 'GPU')]))
        self.assertFalse(result['judgement']['malwareSuspected'])
        self.assertIn('没有发现中毒迹象', result['judgement']['malwareReasoning'])
        self.assertNotEqual(f['F001']['verdict'], 'suspicious')

    # 3 附带（第 3 行本身未覆盖）：华为电脑管家的 dokan 驱动是 OEM 组件，启动早期加载，不能直接删
    def test_03b_oem_manager_driver(self):
        sections = {'drivers': {'kernelDrivers': {'items': [
            {'name': 'dokan2a', 'state': 'Running', 'startMode': 'System', 'path': 'C:\\Program Files\\Huawei\\PCManager\\dokan2a.sys',
             'company': 'Dokan Project', 'signer': 'Huawei Device Co.', 'signature': 'Valid'}]}}}
        r = make_report([flag(1, 'drivers', 'boot-start-third-party', 'info', 'dokan2a')], sections, software=['华为电脑管家'])
        result, f = run_offline(r)
        self.assertEqual(f['F001'].get('kbId'), 'huawei-dokan')
        self.assertEqual(f['F001']['proposedAction'], 'none')

    # 4. IIS、FTP 服务器在运行 → 改手动（黄色）
    def test_04_server_services(self):
        result, f = run_offline(make_report([flag(1, 'services', 'server-service-running', 'medium', 'W3SVC'),
                                             flag(2, 'services', 'server-service-running', 'medium', 'FTPSVC')]))
        for x in ('F001', 'F002'):
            self.assertEqual(f[x]['proposedAction'], 'service.set_manual')
            self.assertEqual(f[x]['risk'], 'yellow')

    # 6. 360、火绒、爱奇艺的计划任务残留（程序已不存在）→ 删除残留任务（绿色）
    def test_06_leftover_tasks(self):
        result, f = run_offline(make_report([flag(1, 'scheduledTasks', 'target-missing', 'medium', '360安全卫士升级任务')]))
        self.assertEqual(f['F001']['verdict'], 'leftover')
        self.assertEqual(f['F001']['proposedAction'], 'task.delete_leftover')
        self.assertEqual(f['F001']['risk'], 'green')

    # 7. 内存完整性打不开：PassGuard、TesMon、华为旧 USB 驱动 → 识别出来但先问用户，不自动删
    def test_07_memory_integrity_blockers(self):
        r = make_report([flag(1, 'drivers', 'memory-integrity-off', 'medium', '内存完整性'),
                         flag(2, 'drivers', 'binary-missing', 'low', 'PassGuard'),
                         flag(3, 'drivers', 'binary-missing', 'low', 'TESMON'),
                         flag(4, 'drivers', 'binary-missing', 'low', 'ew_usbccgpfilter')])
        result, f = run_offline(r)
        self.assertEqual(f['F001']['verdict'], 'problem')
        for x in ('F002', 'F003', 'F004'):
            self.assertTrue(f[x]['needsUser'], x)
        self.assertNotIn('F001', result['kbFlagIds'])  # 推测类知识不直接下结论

    # 8. 内核堆栈保护打不开：游戏卸载后留下的反作弊 → 先问还玩不玩
    #    （缺口：采集了 kernelStackProtection 的状态，但还没有"内核堆栈保护未开启"的规则）
    def test_08_anticheat_leftovers(self):
        result, f = run_offline(make_report([flag(1, 'drivers', 'seed-anticheat-accel', 'medium', 'EasyAntiCheat'),
                                             flag(2, 'services', 'seed-anticheat-accel', 'medium', 'UniFairy')]))
        for x in ('F001', 'F002'):
            self.assertEqual(f[x]['proposedAction'], 'ask_user')
            self.assertIn('游戏', f[x]['questionForUser'])

    # 9. 停止网银键盘保护 PECKP 蓝屏 0xCE → 没有"停止驱动"操作；挂在过滤器上的只能摘过滤器 + 重启后删
    def test_09_never_stop_filter_driver(self):
        self.assertFalse([op for op in OPERATIONS if 'stop' in op])
        r = make_report([flag(1, 'drivers', 'third-party-class-filter', 'medium', 'PECKP')], DRIVERS)
        ev, _, _ = prepare(r)
        for op in ('service.delete_orphan', 'driver.delete_old_version'):
            clean, issues = validate({'findings': [model_finding(['F001'], op)]}, ev)
            self.assertEqual(clean['findings'][0]['proposedAction'], 'ask_user', op)
        clean, issues = validate({'findings': [model_finding(['F001'], 'driver.remove_after_reboot')]}, ev)
        self.assertEqual(clean['findings'][0]['proposedAction'], 'driver.remove_after_reboot')
        self.assertEqual(clean['findings'][0]['risk'], 'red')
        result, f = run_offline(r)
        self.assertIn('网银', f['F001']['questionForUser'])  # 规则模式：先问还用不用

    # 9 附带：红线 3、4（微软驱动、启动早期加载且正在运行的第三方驱动）
    def test_09b_red_lines_3_and_4(self):
        r = make_report([flag(1, 'drivers', 'boot-start-third-party', 'info', 'storahci'),
                         flag(2, 'drivers', 'boot-start-third-party', 'info', 'vendorboot')], DRIVERS)
        ev, _, _ = prepare(r)
        clean, issues = validate({'findings': [model_finding(['F001'], 'driver.remove_after_reboot'),
                                               model_finding(['F002'], 'driver.remove_after_reboot')]}, ev)
        self.assertTrue(all(f['proposedAction'] == 'ask_user' for f in clean['findings']))
        self.assertTrue(any('红线 3' in x for x in issues))
        self.assertTrue(any('红线 4' in x for x in issues))

    # 10. 驱动仓库 34 个旧版重复驱动 → 删除旧版包，不加 /force（让系统保护在用版本）
    def test_10_old_driver_packages(self):
        result, f = run_offline(make_report([flag(1, 'drivers', 'duplicate-versions', 'low', 'bth.inf')]))
        self.assertEqual(f['F001']['proposedAction'], 'driver.delete_old_version')
        self.assertIn('不加 /force', OPERATIONS['driver.delete_old_version'][0])

    # 11. 华为定制显卡驱动压住 Intel 新版 → 换驱动是红色操作，必须先问；回滚是独立按钮（阶段 3）
    def test_11_newer_driver_not_used(self):
        result, f = run_offline(make_report([flag(1, 'versions', 'newer-driver-not-used', 'medium', 'Intel 显卡')]))
        self.assertTrue(f['F001']['needsUser'])
        self.assertEqual(OPERATIONS['driver.switch_to_newer'][1], 'red')
        self.assertEqual(OPERATIONS['driver.switch_to_newer'][3], {'newer-driver-not-used'})

    # 12. Steam 游戏库在 C 盘、Steam 本体在 D 盘 → 识别多个游戏库，C 盘不足时把游戏库交给模型
    def test_12_steam_library_on_c(self):
        sections = {'storage': {'volumes': [{'drive': 'C:', 'freePct': 8}], 'steamLibraries': [{'path': 'C:\\SteamLibrary', 'sizeGB': 22.4}]}}
        r = make_report([flag(1, 'storage', 'games-on-c', 'medium', 'Steam'), flag(2, 'storage', 'system-drive-low', 'medium', 'C:')], sections)
        ev, _, _ = prepare(r)
        c = next(it for it in ev['items'] if it['flagId'] == 'F002')
        self.assertIn('steamLibraries', str(c.get('context') or ev['sharedContext']))
        result, f = run_offline(r)
        self.assertEqual(f['F001']['verdict'], 'problem')

    # 13. Edge 预加载占 742 MB → 关闭浏览器后台（绿色，策略方式，红线 9 要提前告知"由你的组织管理"）
    def test_13_edge_preload(self):
        result, f = run_offline(make_report([flag(1, 'browserNetwork', 'edge-preload', 'low', 'Microsoft Edge')]))
        self.assertEqual(f['F001']['proposedAction'], 'browser.disable_background')
        self.assertEqual(f['F001']['risk'], 'green')

    # 14. 休眠文件 6.3 GB，用户拒绝关休眠 → 只关快速启动、保留休眠；操作库里没有关休眠
    def test_14_keep_hibernation(self):
        self.assertIn('保留休眠', OPERATIONS['power.disable_fast_startup'][0])
        self.assertFalse([op for op in OPERATIONS if 'hibernat' in op])
        result, f = run_offline(make_report([flag(1, 'power', 'fast-startup-on', 'low', '快速启动')]))
        self.assertEqual(f['F001']['proposedAction'], 'power.disable_fast_startup')

    # 14 附带：长期不重启也走"关快速启动"
    def test_14b_long_uptime(self):
        result, f = run_offline(make_report([flag(1, 'resources', 'long-uptime', 'low', '开机时长')]))
        self.assertEqual(f['F001']['proposedAction'], 'power.disable_fast_startup')


if __name__ == '__main__':
    unittest.main()
