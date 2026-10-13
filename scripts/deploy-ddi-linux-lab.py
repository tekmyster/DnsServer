"""Verified immutable deployment for enrolled Linux lab guests; configuration is retained."""
import argparse,hashlib,json,re,secrets,ssl,sys,time
from pathlib import Path
import urllib.request,urllib.parse

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--config',required=True);parser.add_argument('--archive',required=True);args=parser.parse_args()
    config=json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    if config.get('pythonPackages'):sys.path.insert(0,config['pythonPackages'])
    import paramiko
    credentials=json.loads(Path(config['credentials']).read_text())
    archive=Path(args.archive).resolve();digest=hashlib.sha256(archive.read_bytes()).hexdigest()
    def connect(node):
        c=paramiko.SSHClient();c.load_host_keys(config['knownHosts']);c.set_missing_host_key_policy(paramiko.RejectPolicy())
        c.connect(node['address'],username=config['sshUser'],key_filename=config['sshKey'],look_for_keys=False,allow_agent=False,timeout=10);return c
    def run(c,command):
        _,o,e=c.exec_command(command,timeout=90);out=o.read().decode();error=e.read().decode()
        if o.channel.recv_exit_status():raise RuntimeError(error[:1000])
        return out.strip()
    for key in ('primary','secondary'):
        node=config[key];original=connect(node);operation=secrets.token_hex(8)
        destination='/opt/ddi-lab-build-'+digest[:12]+'-'+operation
        unit=run(original,'sudo -n cat /etc/systemd/system/ddi-lab.service')
        was_active=run(original,'systemctl is-active ddi-lab.service || true')=='active'
        home=original.open_sftp().normalize('.')
        if not re.fullmatch(r'/[A-Za-z0-9_./-]+',home):raise ValueError('Unsupported lab account home path.')
        uploaded=home+'/ddi-build-'+operation+'.tar.gz';unit_file=home+'/ddi-unit-'+operation+'.service'
        backup='/etc/systemd/system/ddi-lab.service.predeploy-'+operation
        with original.open_sftp() as s:s.put(str(archive),uploaded)
        # Extraction never touches a running installation; all bytes and metadata are synced first.
        run(original,'sudo -n mkdir '+destination+' && sudo -n tar -xzf '+uploaded+' -C '+destination+' && cd '+destination+' && sudo -n sha256sum --quiet -c BUILD-SHA256SUMS && sudo -n chmod -R go-w '+destination+' && sudo -n chmod u+x '+destination+'/DnsServerApp && sudo -n sync -f '+destination)
        updated=re.sub(r'^WorkingDirectory=.*$', 'WorkingDirectory='+destination,unit,flags=re.M)
        updated=re.sub(r'^ExecStart=\S+/DnsServerApp\b','ExecStart='+destination+'/DnsServerApp',updated,flags=re.M)
        if updated==unit:raise ValueError('Expected DDI executable unit was not found; existing service is untouched.')
        with original.open_sftp() as s:
            with s.file(unit_file,'w') as f:f.write(updated)
        run(original,'sudo -n cp /etc/systemd/system/ddi-lab.service '+backup+' && sudo -n systemctl stop ddi-lab.service && sudo -n install -m 644 '+unit_file+' /etc/systemd/system/ddi-lab.service && sudo -n sync -f /etc/systemd/system/ddi-lab.service && sudo -n systemctl daemon-reload && sudo -n systemctl start ddi-lab.service')
        fresh=None
        try:
            fresh=connect(node)
            if run(fresh,'sudo -n id -u')!='0':raise ValueError('Fresh session lacks required administrative privilege.')
            if run(fresh,'printf "%s\\n" "$SSH_CONNECTION"').split()[0]!=config['managementSource']:raise ValueError('Management source does not match configuration.')
            context=ssl.create_default_context(cadata=Path(node['certificate']).read_text());context.check_hostname=False
            opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=context))
            token=None
            def call(path,**fields):
                request=urllib.request.Request('https://'+node['address']+':53443'+path,data=urllib.parse.urlencode(fields).encode())
                if token:request.add_header('Authorization','Bearer '+token)
                with opener.open(request,timeout=30) as response:result=json.load(response)
                if result.get('status')!='ok':raise ValueError('Fresh DDI verification failed: '+result.get('errorMessage','Request rejected'))
                return result.get('response',result)
            end=time.monotonic()+30
            while True:
                try:
                    token=call('/api/user/login',user=credentials[node['address']]['user'],**{'pass':credentials[node['address']]['password']})['token'];break
                except OSError:
                    if time.monotonic()>end:raise
                    time.sleep(1)
            call('/api/settings/get')
            call('/api/settings/set',enableCheckForUpdate='false',webServiceWwwFolderPath=destination+'/www')
            run(fresh,'cd '+destination+' && sudo -n sha256sum --quiet -c BUILD-SHA256SUMS')
            if not was_active:run(original,'sudo -n systemctl stop ddi-lab.service')
            print(node['address']+' deployed to '+destination+'; checksums, source, authentication, and read/modify privilege verified.',flush=True)
        except Exception:
            # Roll back the executable selection, never roll back allocation journals or grants.
            run(original,'sudo -n systemctl stop ddi-lab.service && sudo -n cp '+backup+' /etc/systemd/system/ddi-lab.service && sudo -n sync -f /etc/systemd/system/ddi-lab.service && sudo -n systemctl daemon-reload')
            if was_active:run(original,'sudo -n systemctl start ddi-lab.service')
            raise
        finally:
            if fresh is not None:fresh.close()
            original.close()

if __name__=='__main__':main()
