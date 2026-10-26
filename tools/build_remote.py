"""打出远程体检包：dist/pcdoctor-remote-<采集器版本>-<校验码前 8 位>.zip

包里只有采集端（只读、不联网），没有 API Key、提示词和研判代码。
可复现构建：同样的源码永远打出字节完全相同的压缩包，任何人都能自己打一遍核对校验码。

用法：python tools/build_remote.py
"""
import hashlib
import io
import os
import re
import sys
import zipfile

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
FILES = [  # (源文件, 包内文件名)
    ('remote/start.bat', 'start.bat'),
    ('remote/remote.ps1', 'remote.ps1'),
    ('remote/README.txt', 'README.txt'),
    ('collector/pcdoctor-collect.ps1', 'pcdoctor-collect.ps1'),
]
BOM = b'\xef\xbb\xbf'
FIXED_TIME = (2026, 1, 1, 0, 0, 0)  # 固定时间戳，保证可复现


def normalize(name, data):
    """统一成 Windows 需要的样子：CRLF 换行；.ps1 / .txt 带 BOM（否则 5.1 和记事本按 GBK 读）；.bat 只能是 ASCII。"""
    data = data.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
    if name.endswith('.bat'):
        data.decode('ascii')  # cmd 按系统代码页读 .bat，有中文会乱码，这里直接报错
    elif not data.startswith(BOM):
        data = BOM + data
    return data


def collector_version():
    with open(os.path.join(ROOT, 'collector/pcdoctor-collect.ps1'), encoding='utf-8-sig') as fh:
        return re.search(r"\$ScriptVersion = '([^']+)'", fh.read()).group(1)


def build(out_dir=None):
    version = collector_version()
    prefix = f'pcdoctor-remote-{version}/'
    out_dir = out_dir or os.path.join(ROOT, 'dist')
    os.makedirs(out_dir, exist_ok=True)

    contents = {}
    for src, name in FILES:
        with open(os.path.join(ROOT, src), 'rb') as fh:
            contents[name] = normalize(name, fh.read())
    sums = ''.join(f'{hashlib.sha256(d).hexdigest().upper()}  {n}\r\n' for n, d in contents.items())
    contents['SHA256SUMS.txt'] = sums.encode('ascii')

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as zf:
        for name in sorted(contents):
            info = zipfile.ZipInfo(prefix + name, date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            zf.writestr(info, contents[name])
    digest = hashlib.sha256(buf.getvalue()).hexdigest().upper()
    # 文件名带校验码前 8 位：只改了 remote.ps1、采集器版本不变时，新旧两个包也不会同名
    path = os.path.join(out_dir, f'pcdoctor-remote-{version}-{digest[:8].lower()}.zip')
    with open(path, 'wb') as fh:
        fh.write(buf.getvalue())
    return path, digest, contents


if __name__ == '__main__':
    path, digest, contents = build(sys.argv[1] if len(sys.argv) > 1 else None)
    print(f'已生成：{os.path.abspath(path)}')
    print(f'压缩包 SHA-256：{digest}')
    print('发给朋友时，把这个校验码单独再发一条消息，方便对方核对文件没被改过。')
