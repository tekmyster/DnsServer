"""Create clean distributable archives, preserving Linux execute bits."""
import argparse
import hashlib
import json
import pathlib
import subprocess
import tarfile
import zipfile

p = argparse.ArgumentParser()
p.add_argument('folder', type=pathlib.Path)
p.add_argument('archive', type=pathlib.Path)
p.add_argument('--version', required=True)
p.add_argument('--runtime', required=True)
a = p.parse_args()
root = pathlib.Path(__file__).resolve().parent.parent
if (root / 'PUBLICATION-HOLD.txt').exists():
    raise SystemExit('Public publication is on hold by owner instruction. Private builds may continue.')
files = sorted(f for f in a.folder.rglob('*') if f.is_file())
banned = {'auth.config', 'dns.config', 'security.json', 'syslog.json', 'credentials.json', 'lab-connection.json'}
if any(f.name in banned or f.suffix in {'.key', '.kdbx'} for f in files):
    raise SystemExit('Deployment data must not be included in a package.')
metadata = {'product': 'teQnatium DDI Server', 'release': a.version, 'runtime': a.runtime,
            'sourceCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
            'libraryCommit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root.parent / 'TechnitiumLibrary', text=True).strip(),
            'engineVersion': '15.6.1'}
(a.folder / 'BUILD.json').write_text(json.dumps(metadata, indent=2) + '\n', encoding='utf-8')
files = sorted(f for f in a.folder.rglob('*') if f.is_file() and f.name != 'BUILD-SHA256SUMS')
(a.folder / 'BUILD-SHA256SUMS').write_text(''.join(hashlib.sha256(f.read_bytes()).hexdigest() + '  ' + f.relative_to(a.folder).as_posix() + '\n' for f in files), encoding='ascii')
files.append(a.folder / 'BUILD-SHA256SUMS')
if a.archive.suffix == '.zip':
    with zipfile.ZipFile(a.archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for f in files:
            z.write(f, f.relative_to(a.folder).as_posix())
else:
    def mode(info):
        info.uid = info.gid = 0
        info.uname = info.gname = 'root'
        info.mode = 0o755 if info.name.endswith('.sh') or pathlib.PurePosixPath(info.name).name == 'DnsServerApp' else 0o644
        return info
    with tarfile.open(a.archive, 'w:gz') as t:
        for f in files:
            t.add(f, arcname=f.relative_to(a.folder).as_posix(), filter=mode, recursive=False)
print('Packaged ' + a.archive.name + ' with ' + str(len(files)) + ' files.')
