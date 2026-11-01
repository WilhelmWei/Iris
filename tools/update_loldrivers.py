"""从 LOLDrivers（https://github.com/magicsword-io/LOLDrivers，Apache-2.0）生成 kb/loldrivers.json。

只取文件的 SHA-256 指纹、类别（被恶意软件利用过的正规驱动 / 恶意驱动）和名单里的文件名，不取其他内容。
体检时采集脚本给第三方驱动算 SHA-256，和这张表比对（只认指纹，不认文件名）。不联网，也不做漏洞扫描。

用法：
  python3 tools/update_loldrivers.py                    # 从 GitHub 下载最新的 drivers.json
  python3 tools/update_loldrivers.py 本地/drivers.json  # 用已经下载好的文件
"""
import json
import os
import re
import sys
import urllib.request
from datetime import date

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = 'https://raw.githubusercontent.com/magicsword-io/LOLDrivers/main/loldrivers.io/content/api/drivers.json'
OUT = os.path.join(ROOT, 'kb', 'loldrivers.json')
HEX64 = re.compile(r'^[0-9a-f]{64}$')


def build(entries):
    hashes = {}
    for e in entries:
        cat = 'malicious' if str(e.get('Category', '')).lower().startswith('malicious') else 'vulnerable'
        tags = [t for t in (e.get('Tags') or []) if isinstance(t, str)]
        name = (tags[0] if tags else '')[:60]
        for s in e.get('KnownVulnerableSamples') or []:
            h = str(s.get('SHA256') or '').strip().lower()
            if not HEX64.match(h):
                continue
            # 同一个指纹在两类里都出现时按"恶意"算
            if h in hashes and hashes[h][0] == 'malicious':
                continue
            hashes[h] = [cat, name]
    return {
        'source': 'LOLDrivers https://github.com/magicsword-io/LOLDrivers (Apache License 2.0)',
        'generated': date.today().isoformat(),
        'count': len(hashes),
        'hashes': dict(sorted(hashes.items())),
    }


def main():
    if len(sys.argv) > 1:
        with open(sys.argv[1], encoding='utf-8') as fh:
            entries = json.load(fh)
    else:
        with urllib.request.urlopen(URL, timeout=120) as r:
            entries = json.loads(r.read().decode('utf-8'))
    data = build(entries)
    with open(OUT, 'w', encoding='utf-8', newline='\n') as fh:
        json.dump(data, fh, ensure_ascii=False, separators=(',', ':'))
        fh.write('\n')
    print(f'已生成 {OUT}：{data["count"]} 个指纹')


if __name__ == '__main__':
    main()
