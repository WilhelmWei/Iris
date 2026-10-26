"""从源码构建 Iris 桌面版。GitHub Actions 发布时调用的也是这个脚本，保证从源码可复现。

产物（默认放在 dist/）：
  Iris-<版本>-win-x64.zip         免安装版：Iris.exe + LICENSE + PRIVACY.md + 使用说明.txt + 内测说明.txt + SHA256SUMS.txt
  Iris-Setup-<版本>.exe            安装版（加 --installer，需要 Windows 上的 Inno Setup 6：ISCC.exe）
  SHA256SUMS.txt                   以上文件的校验码

Iris.exe 是单文件、自包含的 Windows 程序（用户不需要安装 .NET），也可以在 Linux 上交叉编译（不含安装版）。

用法：
  python tools/build_app.py                              版本取 src/Pcdoctor.App/Pcdoctor.App.csproj
  python tools/build_app.py --version 1.0.0-beta.1       指定版本（CI 里用 tag 名）
  python tools/build_app.py --installer                  同时生成安装程序（Windows）
  python tools/build_app.py --feedback-url https://… --latest-url https://…
                                                         写入 Iris 服务器地址（内测反馈、检查更新）；不写时程序不联系 Iris 服务器
"""
import argparse
import hashlib
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
PROJ = os.path.join(ROOT, 'src', 'Pcdoctor.App', 'Pcdoctor.App.csproj')
README = os.path.join(ROOT, 'src', 'Pcdoctor.App', '使用说明.txt')
BETA = os.path.join(ROOT, 'docs', 'beta', '内测说明.txt')
ISS = os.path.join(ROOT, 'installer', 'Iris.iss')
FIXED_TIME = (2026, 1, 1, 0, 0, 0)
BOM = b'\xef\xbb\xbf'


def csproj_version():
    with open(PROJ, encoding='utf-8') as fh:
        return re.search(r'<InformationalVersion>([^<]+)</InformationalVersion>', fh.read()).group(1)


def numeric(version):
    """1.0.0-alpha.1 -> 1.0.0（文件版本只能是数字）"""
    m = re.match(r'^(\d+)\.(\d+)\.(\d+)', version)
    if not m:
        sys.exit(f'版本号格式不对：{version}（应为 1.2.3 或 1.2.3-beta.1）')
    return m.group(0)


def text_for_windows(path):
    """CRLF 换行 + UTF-8 BOM：记事本和 Inno Setup 才能正确显示中文。"""
    with open(path, 'rb') as fh:
        data = fh.read().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
    return data if data.startswith(BOM) else BOM + data


def publish(version, out, feedback_url='', latest_url=''):
    for u in (feedback_url, latest_url):
        if u and not u.startswith('https://'):
            sys.exit(f'服务器地址必须是 https://：{u}')
    subprocess.run(['dotnet', 'publish', PROJ, '-c', 'Release', '-o', out,
                    f'-p:Version={numeric(version)}', f'-p:InformationalVersion={version}',
                    f'-p:IrisFeedbackUrl={feedback_url}', f'-p:IrisLatestUrl={latest_url}',
                    '-p:ContinuousIntegrationBuild=true', '-p:Deterministic=true'],
                   check=True, stdout=subprocess.DEVNULL)
    return os.path.join(out, 'Iris.exe')


def build_zip(version, exe_path, out_dir):
    with open(exe_path, 'rb') as fh:
        exe = fh.read()
    files = {
        'Iris.exe': exe,
        'LICENSE.txt': text_for_windows(os.path.join(ROOT, 'LICENSE')),
        'PRIVACY.md': text_for_windows(os.path.join(ROOT, 'PRIVACY.md')),
        '使用说明.txt': text_for_windows(README),
    }
    if '-' in version and os.path.isfile(BETA):  # 预发布版（beta、rc）附上内测说明
        files['内测说明.txt'] = text_for_windows(BETA)
    files['SHA256SUMS.txt'] = ''.join(f'{hashlib.sha256(d).hexdigest().upper()}  {n}\r\n' for n, d in files.items()).encode('utf-8')
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as zf:
        for name in sorted(files):
            info = zipfile.ZipInfo(f'Iris-{version}/{name}', date_time=FIXED_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            info.flag_bits |= 0x800  # 文件名用 UTF-8，Windows 资源管理器才能正确显示中文
            zf.writestr(info, files[name])
    path = os.path.join(out_dir, f'Iris-{version}-win-x64.zip')
    with open(path, 'wb') as fh:
        fh.write(buf.getvalue())
    return path


def find_iscc():
    for c in [shutil.which('ISCC'), shutil.which('iscc'),
              os.path.expandvars(r'%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe'),
              os.path.expandvars(r'%ProgramFiles%\Inno Setup 6\ISCC.exe'),
              os.path.expandvars(r'%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe')]:
        if c and os.path.isfile(c):
            return c
    return None


def build_installer(version, exe_path, out_dir):
    iscc = find_iscc()
    if not iscc:
        sys.exit('没有找到 Inno Setup 6（ISCC.exe）。Windows 上可以用 "choco install innosetup" 或从 jrsoftware.org 安装')
    with tempfile.TemporaryDirectory() as stage:
        # 安装程序里要显示的协议和隐私政策：转成带 BOM 的 Windows 文本
        for src, name in ((os.path.join(ROOT, 'LICENSE'), 'LICENSE.txt'), (os.path.join(ROOT, 'PRIVACY.md'), 'PRIVACY.txt'),
                          (README, '使用说明.txt')):
            with open(os.path.join(stage, name), 'wb') as fh:
                fh.write(text_for_windows(src))
        shutil.copy2(exe_path, os.path.join(stage, 'Iris.exe'))
        subprocess.run([iscc, '/Qp', f'/DAppVersion={version}', f'/DAppNumericVersion={numeric(version)}',
                        f'/DStageDir={stage}', f'/DRepoRoot={ROOT}', f'/O{out_dir}', ISS], check=True)
    return os.path.join(out_dir, f'Iris-Setup-{version}.exe')


def main():
    ap = argparse.ArgumentParser(description='构建 Iris 桌面版')
    ap.add_argument('--version', help='版本号，默认取 csproj 里的 InformationalVersion；CI 里传 tag 名（去掉开头的 v）')
    ap.add_argument('--installer', action='store_true', help='同时生成 Inno Setup 安装程序（Windows）')
    ap.add_argument('--out', default=os.path.join(ROOT, 'dist'), help='输出目录')
    ap.add_argument('--feedback-url', default=os.environ.get('IRIS_FEEDBACK_URL', ''), help='接收内测反馈的地址（https）')
    ap.add_argument('--latest-url', default=os.environ.get('IRIS_LATEST_URL', ''), help='最新版本信息 latest.json 的地址（https）')
    args = ap.parse_args()
    if not shutil.which('dotnet'):
        sys.exit('需要 .NET SDK 10（dotnet 命令）')
    version = (args.version or csproj_version()).lstrip('v')
    numeric(version)
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    print(f'dotnet SDK {subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()}，版本 {version}')

    with tempfile.TemporaryDirectory() as pub:
        exe = publish(version, pub, args.feedback_url, args.latest_url)
        if args.feedback_url or args.latest_url:
            print(f'Iris 服务器：反馈 {args.feedback_url or "（无）"}；更新 {args.latest_url or "（无）"}')
        outputs = [build_zip(version, exe, out)]
        if args.installer:
            outputs.append(build_installer(version, exe, out))
    with open(os.path.join(out, 'SHA256SUMS.txt'), 'w', encoding='utf-8', newline='\r\n') as fh:
        for p in outputs:
            with open(p, 'rb') as f:
                fh.write(f'{hashlib.sha256(f.read()).hexdigest().upper()}  {os.path.basename(p)}\n')
    for p in outputs:
        print(f'已生成：{p}（{os.path.getsize(p) / 1048576:.1f} MB）')


if __name__ == '__main__':
    main()
