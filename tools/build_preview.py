"""把桌面版界面打包成一个可以单独打开的网页预览（设计评审用）。

界面在浏览器里运行时用 bridge.js 的模拟实现，数据是 web/demo/demo.json（虚构的一台电脑），
不读取任何真实报告。字体、样式、脚本、演示数据、隐私政策全部内联，生成的文件不依赖其他文件。

用法：
  python tools/build_preview.py                    输出 dist/iris-preview.html（完整网页）
  python tools/build_preview.py --fragment         只输出 <title>、<style>、正文和 <script>（给需要自己套外壳的托管页面用）
"""
import argparse
import base64
import json
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
WEB = os.path.join(ROOT, 'src', 'Pcdoctor.App', 'web')


def read(*parts):
    with open(os.path.join(*parts), encoding='utf-8') as fh:
        return fh.read()


def inline_fonts(css):
    def repl(m):
        with open(os.path.join(WEB, m.group(1)), 'rb') as fh:
            return 'url(data:font/woff2;base64,' + base64.b64encode(fh.read()).decode('ascii') + ')'
    return re.sub(r'url\((fonts/[^)]+\.woff2)\)', repl, css)


def script_safe(text):
    return text.replace('</', '<\\/')


def build(fragment):
    html = read(WEB, 'index.html')
    css = inline_fonts(read(WEB, 'app.css'))
    demo = json.loads(read(WEB, 'demo', 'demo.json'))
    privacy = read(ROOT, 'PRIVACY.md')
    data = (f'window.__DEMO__ = {json.dumps(demo, ensure_ascii=False)};\n'
            f'window.__PRIVACY__ = {json.dumps(privacy, ensure_ascii=False)};\n')
    body = re.search(r'<body>(.*)</body>', html, re.S).group(1)
    body = re.sub(r'\s*<script src="[^"]+"></script>', '', body)
    scripts = ''.join(f'<script>\n{script_safe(s)}\n</script>\n' for s in (data, read(WEB, 'bridge.js'), read(WEB, 'app.js')))
    head = ('<title>Iris 界面预览</title>\n<meta name="claude:viewport-fit-sides" content="cover">\n'
            f'<style>\n{css}\n</style>\n')
    if fragment:
        return head + body.strip() + '\n' + scripts
    return ('<!doctype html>\n<html lang="zh-CN" data-theme="dark">\n<head>\n<meta charset="utf-8">\n'
            '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n'
            f'{head}</head>\n<body>\n{body.strip()}\n{scripts}</body>\n</html>\n')


def main():
    ap = argparse.ArgumentParser(description='打包界面预览')
    ap.add_argument('--fragment', action='store_true', help='不带 <html>/<head>/<body> 外壳')
    ap.add_argument('--out', default=os.path.join(ROOT, 'dist', 'iris-preview.html'))
    args = ap.parse_args()
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, 'w', encoding='utf-8') as fh:
        fh.write(build(args.fragment))
    print(f'已生成：{args.out}（{os.path.getsize(args.out) / 1024:.0f} KB）')


if __name__ == '__main__':
    main()
