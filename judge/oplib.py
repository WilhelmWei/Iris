"""操作库 v0.3（MVP 阶段只用于"建议"，不执行）。

这里是操作编号的唯一来源：提示词里的操作表由 operations_table() 生成，
校验时也只认这里的编号。风险等级由代码决定，模型的输出改不了它。
"""

# 编号 -> (名称, 风险, 允许作用的报告节, 限定的预筛规则)
# 风险：green / yellow / red / None（不是操作）；节里 '*' 表示任何节；
# 限定规则为 None 表示不限，否则 finding 里至少要有一条预筛结果属于这些规则
OPERATIONS = {
    'none':                       ('不需要处理', None, {'*'}, None),
    'ask_user':                   ('需要先问用户', None, {'*'}, None),
    'startup.disable':            ('关闭开机自启动', 'green', {'startup'}, None),
    'task.disable':               ('禁用计划任务', 'green', {'scheduledTasks'}, None),
    'task.delete_leftover':       ('删除指向已不存在文件的计划任务', 'green', {'scheduledTasks'}, {'target-missing'}),
    'browser.disable_background': ('关闭 Edge 的启动增强和后台运行', 'green', {'browserNetwork', 'processes'}, None),
    'browser.disable_chrome_background': ('关闭 Chrome 关掉窗口后的后台运行（Chrome 不是主力浏览器时）', 'green', {'browserNetwork'}, {'chrome-idle-background'}),
    'cache.clean':                ('清理程序会自动重建的缓存（临时文件、浏览器缓存、错误报告等，按内置的清理目录表）；不碰聊天文件、下载文件夹等用户数据', 'green', {'storage'}, {'junk-cleanable', 'temp-large', 'dev-cache', 'system-drive-low'}),
    'system.cleanup':             ('交给 Windows 自带的磁盘清理，删除系统升级和更新留下的文件（Windows.old、升级临时文件、传递优化缓存等）', 'yellow', {'storage'}, {'system-leftover'}),
    'downloads.recycle_installers': ('把下载文件夹里很久以前的安装包移到回收站（不永久删除）', 'green', {'storage'}, {'downloads-installers'}),
    'leftover.remove_dir':        ('把卸载残留的文件夹移到 Iris 的备份区（可以撤销）', 'yellow', {'software'}, {'leftover-dir'}),
    'defender.scan':              ('交给 Defender 扫描或隔离', 'green', {'*'}, None),
    'defender.review':            ('引导用户在 Windows 安全中心复核并删除不需要的排除项或"允许的威胁"', 'green', {'security'}, None),
    'service.set_manual':         ('第三方服务改为手动启动', 'yellow', {'services'}, None),
    'service.delete_orphan':      ('删除程序文件已不存在的服务或驱动登记（先确认没有挂在设备过滤器上）', 'yellow', {'services', 'drivers'}, {'binary-missing'}),
    'app.uninstall':              ('卸载软件（用户确认后）', 'yellow', {'software', 'services', 'drivers', 'processes', 'startup', 'scheduledTasks', 'storage', 'browserNetwork'}, None),
    'driver.delete_old_version':  ('删除驱动仓库里没有设备在用的旧版驱动包（不加 /force）', 'yellow', {'drivers'}, {'duplicate-versions'}),
    'power.disable_fast_startup': ('关闭快速启动（保留休眠）', 'yellow', {'power', 'resources'}, {'fast-startup-on', 'long-uptime'}),
    'driver.remove_after_reboot': ('删除第三方驱动：先摘除设备过滤器、标记删除、重启后移走文件', 'red', {'drivers', 'services'}, None),
    'driver.switch_to_newer':     ('让设备改用驱动仓库里更新的版本', 'red', {'versions', 'drivers'}, {'newer-driver-not-used'}),
}

RISK_NAMES = {'green': '绿', 'yellow': '黄', 'red': '红', None: '—'}

# 要过红线 2/3/4 检查的操作：删除登记也算，挂在设备过滤器上的驱动登记被删、过滤器还在，设备会失灵
DRIVER_OPS = {'driver.delete_old_version', 'driver.remove_after_reboot', 'driver.switch_to_newer', 'service.delete_orphan'}


def operations_table():
    """生成提示词里的操作表（Markdown）。"""
    lines = ['| 编号 | 含义 | 风险 | 适用的报告节 | 只用于这些规则 |', '| --- | --- | --- | --- | --- |']
    for op, (name, risk, sections, rules) in OPERATIONS.items():
        where = '任意' if '*' in sections else '、'.join(sorted(sections))
        only = '、'.join(sorted(rules)) if rules else '不限'
        lines.append(f'| `{op}` | {name} | {RISK_NAMES[risk]} | {where} | {only} |')
    return '\n'.join(lines)
