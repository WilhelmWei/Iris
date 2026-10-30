"""远程体检包的测试：内容、编码、可复现构建、包里没有 Key 和研判代码；有 pwsh 时实际跑一遍。

运行：python -m unittest discover -s tests
"""
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
sys.path.insert(0, os.path.join(ROOT, 'tools'))

from build_remote import build  # noqa: E402


class RemotePackageTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp()
        cls.path, cls.digest, _ = build(cls.tmp)
        with zipfile.ZipFile(cls.path) as zf:
            cls.files = {os.path.basename(n): zf.read(n) for n in zf.namelist()}

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_contents(self):
        self.assertEqual(sorted(self.files), ['README.txt', 'SHA256SUMS.txt', 'junk.json', 'pcdoctor-collect.ps1', 'remote.ps1', 'start.bat'])

    def test_encoding_for_windows(self):
        for name, data in self.files.items():
            self.assertNotIn(b'\n', data.replace(b'\r\n', b''), f'{name} 有非 CRLF 换行')
            if name.endswith(('.ps1', '.txt')) and name != 'SHA256SUMS.txt':
                self.assertTrue(data.startswith(b'\xef\xbb\xbf'), f'{name} 缺 BOM，5.1 / 记事本会按 GBK 读成乱码')
        self.files['start.bat'].decode('ascii')

    def test_checksums_match(self):
        for line in self.files['SHA256SUMS.txt'].decode('ascii').splitlines():
            digest, name = line.split('  ')
            self.assertEqual(hashlib.sha256(self.files[name]).hexdigest().upper(), digest, name)

    def test_reproducible(self):
        with tempfile.TemporaryDirectory() as d:
            _, digest, _ = build(d)
        self.assertEqual(digest, self.digest)

    def test_no_secrets_or_judge_code(self):
        for name, data in self.files.items():
            text = data.decode('utf-8', 'ignore')
            # 采集脚本结尾提到 judge-prompt.md 的文件名没关系，要防的是 Key 和接口地址
            for s in ('DEEPSEEK_API_KEY', 'api.deepseek.com', 'Bearer '):
                self.assertFalse(s in text, f'{name} 里出现了 {s}')

    @unittest.skipUnless(shutil.which('pwsh'), '需要 pwsh')
    def test_runs_end_to_end(self):
        with tempfile.TemporaryDirectory() as d:
            pkg, out = os.path.join(d, 'pkg'), os.path.join(d, 'out')
            os.makedirs(out)
            with zipfile.ZipFile(self.path) as zf:
                zf.extractall(pkg)
            script = os.path.join(pkg, os.listdir(pkg)[0], 'remote.ps1')
            env = dict(os.environ, HOME=d, USERPROFILE=d, TEMP=d, USERNAME='user', COMPUTERNAME='box')
            res = subprocess.run(['pwsh', '-NoProfile', '-File', script, '-NoElevate', '-NoPause', '-OutDir', out],
                                 capture_output=True, text=True, encoding='utf-8', env=env, timeout=300)
            self.assertEqual(res.returncode, 0, res.stdout + res.stderr)
            self.assertTrue(any(f.startswith('pcdoctor-report-') for f in os.listdir(out)))
            self.assertIn('下一步', res.stdout)
            self.assertFalse([f for f in os.listdir(d) if f.startswith('pcdoctor-remote-')], '临时日志没删')


if __name__ == '__main__':
    unittest.main()
