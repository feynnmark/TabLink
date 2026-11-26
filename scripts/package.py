"""Build a public release from an explicit allowlist, without local data or keys."""
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DIST = ROOT / 'dist'
VERSION = ET.parse(ROOT/'android/AndroidManifest.xml').getroot().get('{http://schemas.android.com/apk/res/android}versionName')
NAMES = ['TabLink.exe','TabLink.apk','Install.cmd','Install-TabLink.ps1','Setup-Dependencies.ps1',
         'Prepare-Driver.ps1','Prepare-90Hz.ps1','Uninstall-TabLink.ps1','README.md','LICENSE',
         'SECURITY.md','THIRD_PARTY.md','dependencies.json']
for name in NAMES:
    assert (DIST/name).is_file(), name
manifest = dict(app='TabLink',version=VERSION,wireProtocol=5,platform='Windows x64 + Android',
                codec='HEVC Main / Main 10',refreshRates=[30,60,75,90,120],maxUnacknowledgedPictures=4)
(DIST/'BUILD.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
NAMES.append('BUILD.json')
sums=''.join(hashlib.sha256((DIST/name).read_bytes()).hexdigest()+'  '+name+'\n' for name in NAMES)
(DIST/'SHA256SUMS.txt').write_text(sums,encoding='utf-8')
NAMES.append('SHA256SUMS.txt')
archive=ROOT/f'TabLink-{VERSION}-Windows.zip'
with zipfile.ZipFile(archive,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as zipped:
    for name in NAMES:zipped.write(DIST/name,name)
with zipfile.ZipFile(archive) as zipped:
    assert zipped.testzip() is None
    assert set(zipped.namelist())==set(NAMES)
print(f'Created {archive.name}: {archive.stat().st_size} bytes; {len(NAMES)} public files')
