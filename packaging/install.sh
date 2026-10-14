#!/bin/bash
set -euo pipefail
version=0.1.0-alpha.4
archive=''
checksums=''
stage_only=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --archive) archive="$2"; shift 2 ;;
    --checksums) checksums="$2"; shift 2 ;;
    --stage-only) stage_only=1; shift ;;
    *) echo 'Use --version, --archive, --checksums or --stage-only.' >&2; exit 1 ;;
  esac
done
case "$version" in ''|*[!0-9A-Za-z.-]*) echo 'Invalid release version.' >&2; exit 1 ;; esac
[ "$(id -u)" = 0 ] || { echo 'Run this installer as root.' >&2; exit 1; }
for tool in curl tar sha256sum python3; do command -v "$tool" >/dev/null || { echo "Install $tool first." >&2; exit 1; }; done
case "$(uname -m)" in x86_64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) echo 'Use the portable .NET package on this architecture.' >&2; exit 1 ;; esac
console_port="${TECTANIUM_CONSOLE_PORT:-5380}"
[[ "$console_port" =~ ^[0-9]+$ ]] && [ "$console_port" -ge 1024 ] && [ "$console_port" -le 65535 ] || { echo 'Invalid console port.' >&2; exit 1; }
if [ "$stage_only" = 0 ]; then
  command -v systemctl >/dev/null || { echo 'Use the portable package on systems without systemd.' >&2; exit 1; }
  [ ! -e /etc/systemd/system/tectanium-ddi.service ] || { echo 'Existing teQtanium deployment detected. Use --stage-only, then follow the upgrade guide.' >&2; exit 1; }
  [ ! -e /etc/teqtanium-ddi/auth.config ] || { echo 'Existing configuration detected. Import a backup into a new deployment instead.' >&2; exit 1; }
  [ ! -e /etc/tectanium-ddi/auth.config ] || { echo 'Existing configuration detected. Use the migration guide.' >&2; exit 1; }
fi
install -d -m 755 /opt/teqtanium-ddi/releases
work=$(mktemp -d /opt/teqtanium-ddi/releases/.stage-XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
asset="teQtanium-$version-linux-$arch.tar.gz"
url="https://github.com/tekmyster/teQtanium-DDI-Server/releases/download/v$version"
if [ -z "$archive" ]; then curl --fail --location --proto '=https' --proto-redir '=https' "$url/$asset" -o "$work/$asset"; archive="$work/$asset"; fi
if [ -z "$checksums" ]; then curl --fail --location --proto '=https' --proto-redir '=https' "$url/SHA256SUMS" -o "$work/SHA256SUMS"; checksums="$work/SHA256SUMS"; fi
expected=$(awk -v name="$asset" '$2==name {print $1}' "$checksums")
[[ "$expected" =~ ^[0-9a-f]{64}$ ]] || { echo 'No unique checksum found for this package.' >&2; exit 1; }
[ "$(sha256sum "$archive" | awk '{print $1}')" = "$expected" ] || { echo 'Package checksum mismatch.' >&2; exit 1; }
python3 - "$archive" "$work/payload" <<'PY'
import hashlib,pathlib,sys,tarfile
root=pathlib.Path(sys.argv[2]);root.mkdir()
with tarfile.open(sys.argv[1],'r:gz') as tar:
    entries=tar.getmembers()
    if len(entries)>6000 or sum(e.size for e in entries)>1024**3: raise SystemExit('Package exceeds limits.')
    seen=set()
    for e in entries:
        path=pathlib.PurePosixPath(e.name)
        if path.is_absolute() or '..' in path.parts or path.as_posix() in seen or not (e.isfile() or e.isdir()): raise SystemExit('Unsafe package entry.')
        seen.add(path.as_posix())
        target=root.joinpath(*path.parts)
        if e.isdir(): target.mkdir(parents=True,exist_ok=True);continue
        target.parent.mkdir(parents=True,exist_ok=True)
        target.write_bytes(tar.extractfile(e).read())
        target.chmod(0o755 if target.name=='DnsServerApp' or target.suffix=='.sh' else 0o644)
    listed=set()
    for line in (root/'BUILD-SHA256SUMS').read_text().splitlines():
        expected,name=line.split('  ',1);path=pathlib.PurePosixPath(name)
        if path.is_absolute() or '..' in path.parts or name in listed: raise SystemExit('Unsafe package manifest.')
        listed.add(name)
        if hashlib.sha256(root.joinpath(*path.parts).read_bytes()).hexdigest()!=expected: raise SystemExit('File checksum mismatch.')
    actual={f.relative_to(root).as_posix() for f in root.rglob('*') if f.is_file() and f.name!='BUILD-SHA256SUMS'}
    if actual!=listed: raise SystemExit('Manifest coverage mismatch.')
PY
destination="/opt/teqtanium-ddi/releases/$version-${expected:0:12}"
[ ! -e "$destination" ] || { echo 'This package is already staged; its files will not be overwritten.' >&2; exit 1; }
"$work/payload/DnsServerApp" --icu-test
mv "$work/payload" "$destination"
if [ "$stage_only" = 1 ]; then echo "Verified package staged at $destination. Service configuration is unchanged."; exit 0; fi
id teqtanium-ddi >/dev/null 2>&1 || useradd --system --user-group --no-create-home --shell /usr/sbin/nologin teqtanium-ddi
install -d -o teqtanium-ddi -g teqtanium-ddi -m 700 /etc/teqtanium-ddi /var/log/teqtanium-ddi
python3 - <<'PY'
import pathlib,secrets
p=pathlib.Path('/etc/teqtanium-ddi/initial-admin-password.txt');p.write_text(secrets.token_urlsafe(32));p.chmod(0o600)
PY
chown -R teqtanium-ddi:teqtanium-ddi /etc/teqtanium-ddi
cat > "$work/tectanium-ddi.service" <<UNIT
[Unit]
Description=teQtanium DDI Server
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User=teqtanium-ddi
Group=teqtanium-ddi
WorkingDirectory=$destination
ExecStart=$destination/DnsServerApp --stop-if-bind-fails /etc/teqtanium-ddi
Environment=DNS_SERVER_WEB_SERVICE_LOCAL_ADDRESSES=127.0.0.1
Environment=DNS_SERVER_WEB_SERVICE_HTTP_PORT=$console_port
Environment=DNS_SERVER_LOG_FOLDER_PATH=/var/log/teqtanium-ddi
Environment=DNS_SERVER_ADMIN_PASSWORD_FILE=/etc/teqtanium-ddi/initial-admin-password.txt
Restart=on-failure
RestartSec=10
UMask=0077
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/etc/teqtanium-ddi /var/log/teqtanium-ddi
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
[Install]
WantedBy=multi-user.target
UNIT
install -m 644 "$work/tectanium-ddi.service" /etc/systemd/system/tectanium-ddi.service
systemctl daemon-reload
systemctl start tectanium-ddi.service
ready=0
for attempt in $(seq 1 30); do
  if python3 "$destination/check-service.py" "http://127.0.0.1:$console_port" /etc/teqtanium-ddi/initial-admin-password.txt --dns-port "${TECTANIUM_TEST_DNS_PORT:-53}"; then ready=1; break; fi
  sleep 1
done
if [ "$ready" = 0 ]; then systemctl stop tectanium-ddi.service; echo 'Administrative or DNS validation failed. The service is stopped; configuration is retained. Inspect journalctl -u tectanium-ddi.service.' >&2; exit 1; fi
systemctl enable tectanium-ddi.service
echo "Installed teQtanium DDI Server. Console: http://127.0.0.1:$console_port/"
echo 'Initial admin credential: /etc/teqtanium-ddi/initial-admin-password.txt'
