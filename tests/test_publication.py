"""Check tracked public contents and the exact release payload before publishing."""
import hashlib
import json
import os
import re
import subprocess
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT=Path(__file__).resolve().parents[1]
PRIVATE_EXT={'.jks','.keystore','.key','.pfx','.p12','.pem','.log','.png','.jpg','.hevc','.h264','.pdb'}
PATTERNS={
    'personal Windows directory':re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+[^\s<>"\']+',re.I),
    'GitHub credential':re.compile(r'gh[pousr]_[A-Za-z0-9_]{25,}|github_pat_[A-Za-z0-9_]{40,}'),
    'private key':re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'device identifier':re.compile(r'\bR[0-9A-Z]{10}\b'),
    'emoji':re.compile('[\U0001f300-\U0001faff\u2600-\u27bf]'),
}
def scan(name,data):
    assert Path(name).suffix.lower() not in PRIVATE_EXT,name
    for encoding in ('utf-8','utf-16le'):
        text=data.decode(encoding,errors='ignore')
        for label,pattern in PATTERNS.items():
            if label=='emoji' and (encoding!='utf-8' or Path(name).suffix in {'.exe','.apk','.dex'}):continue
            assert not pattern.search(text),f'{name}: {label}'

class PublicationTests(unittest.TestCase):
    def test_public_source(self):
        # git ls-files also works against the initial staged tree, before first commit.
        files=subprocess.check_output(['git','ls-files','-z'],cwd=ROOT).decode().split('\0')
        self.assertTrue(any(files),'Stage the intended public tree first')
        for name in filter(None,files):
            self.assertFalse(name.startswith(('tools/','build/','dist/','tests/local/')),name)
            scan(name,subprocess.check_output(['git','show',':'+name],cwd=ROOT))
    @unittest.skipUnless(os.name=='nt','Windows compiler required')
    def test_negotiation(self):
        compiler=Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        build=ROOT/'build'
        build.mkdir(exist_ok=True)
        exe=build/'ProfileTests.exe'
        subprocess.run([str(compiler),'/nologo','/out:'+str(exe),str(ROOT/'windows/StreamProfile.cs'),str(ROOT/'tests/ProfileTests.cs')],check=True)
        result=subprocess.check_output([str(exe)],text=True)
        self.assertIn('PASS 23',result)
    def test_device_modes_are_consistent(self):
        java=(ROOT/'android/src/com/tablink/display/DeviceProfile.java').read_text()
        java_sizes={tuple(map(int,m)) for m in re.findall(r'\{(\d+),(\d+)\}',java) if int(m[0])>=640}
        ps=(ROOT/'scripts/Prepare-90Hz.ps1').read_text()
        ps_sizes={tuple(map(int,m)) for m in re.findall(r"'(\d+)x(\d+)'",ps)}
        self.assertEqual(java_sizes,ps_sizes)
        self.assertGreaterEqual(len(java_sizes),20)
    def test_dependency_pins(self):
        dependencies=json.loads((ROOT/'dependencies.json').read_text())
        for name,dependency in dependencies.items():
            self.assertTrue(dependency['url'].startswith('https://'),name)
            self.assertRegex(dependency['sha256'],r'^[a-f0-9]{64}$')
    def test_release(self):
        version=ET.parse(ROOT/'android/AndroidManifest.xml').getroot().get('{http://schemas.android.com/apk/res/android}versionName')
        archive=ROOT/f'TabLink-{version}-Windows.zip'
        if not archive.exists():self.skipTest('Run scripts/package.py to check release payload')
        with zipfile.ZipFile(archive) as zipped:
            self.assertIsNone(zipped.testzip())
            names=set(zipped.namelist())
            self.assertTrue({'TabLink.exe','TabLink.apk','Install.cmd','SHA256SUMS.txt'}<=names)
            self.assertFalse(any('/' in name or '\\' in name for name in names))
            for name in names:scan(name,zipped.read(name))
            for line in zipped.read('SHA256SUMS.txt').decode().splitlines():
                digest,name=line.split('  ',1)
                self.assertEqual(hashlib.sha256(zipped.read(name)).hexdigest(),digest,name)
            with zipfile.ZipFile(ROOT/'dist/TabLink.apk') as apk:
                for name in apk.namelist():
                    if name.endswith(('.dex','.xml')):scan(name,apk.read(name))

if __name__=='__main__':unittest.main(verbosity=2)
