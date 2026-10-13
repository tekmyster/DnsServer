"""Opt-in, single-writer Hyper-V fencing controller. No timeout grants guest authority."""
import argparse
import json
import msvcrt
import os
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path

class Api:
    def __init__(self, node, credentials, timeout=5):
        self.node=node; self.token=None; self.timeout=timeout
        context=ssl.create_default_context(cadata=Path(node['certificate']).read_text()); context.check_hostname=False
        self.opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPSHandler(context=context))
        self.token=self.call('/api/user/login',user=credentials[node['address']]['user'],**{'pass':credentials[node['address']]['password']})['token']
    def call(self,path,**fields):
        request=urllib.request.Request('https://'+self.node['address']+':53443'+path,data=urllib.parse.urlencode(fields).encode())
        if self.token:request.add_header('Authorization','Bearer '+self.token)
        with self.opener.open(request,timeout=self.timeout) as response:
            raw=response.read(9*1024*1024+1)
        if len(raw)>9*1024*1024:raise ValueError('API response exceeds recovery limit.')
        data=json.loads(raw)
        if data.get('status')!='ok':raise ValueError(path+': '+data.get('errorMessage','Request rejected'))
        return data.get('response',data)

class Controller:
    def __init__(self,config):
        self.config=config; self.root=Path(config['stateDirectory']);self.root.mkdir(parents=True,exist_ok=True)
        self.credentials=json.loads(Path(config['credentials']).read_text())
        self.statePath=self.root/'controller-state.json'
        self.state=json.loads(self.statePath.read_text()) if self.statePath.exists() else {'operationId':uuid.uuid4().hex,'phase':'Watching'}
        self.lock=open(self.root/'controller.lock','a+b'); self.lock.seek(0)
        if self.lock.read(1)==b'':self.lock.write(b'0');self.lock.flush()
        self.lock.seek(0);msvcrt.locking(self.lock.fileno(),msvcrt.LK_NBLCK,1)
        if config.get('pythonPackages'):sys.path.insert(0,config['pythonPackages'])
        import paramiko
        self.paramiko=paramiko
        self.save(self.state['phase'],message='Hyper-V controller started with an exclusive writer lock.')
    def save(self,phase,**values):
        self.state.update(phase=phase,updatedAt=time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),**values)
        temp=self.statePath.with_suffix('.tmp')
        with open(temp,'w') as f:json.dump(self.state,f,indent=2);f.flush();os.fsync(f.fileno())
        os.replace(temp,self.statePath)
        print(self.state['updatedAt'],phase,values.get('message',''),flush=True)
    def ps(self,command):
        result=subprocess.run(['powershell.exe','-NoProfile','-Command',command],capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError(result.stderr[:1500])
        return result.stdout.strip()
    def fence(self,phase='Fenced'):
        node=self.config['primary']
        result=subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',self.config['fenceScript'],'-VMName',node['vmName'],'-VMId',node['vmId'],'-ManagementSource',self.config['managementSource']],capture_output=True,text=True,timeout=90)
        if result.returncode:raise RuntimeError('Hyper-V fencing could not be verified: '+result.stderr[:1500])
        receipt=json.loads(result.stdout)
        if receipt['vmId'].lower()!=node['vmId'].lower():raise ValueError('Fencing receipt names a different VM.')
        self.save(phase,fence=receipt)
    def connect(self,node):
        client=self.paramiko.SSHClient();client.load_host_keys(self.config['knownHosts']);client.set_missing_host_key_policy(self.paramiko.RejectPolicy())
        client.connect(node['address'],username=self.config['sshUser'],key_filename=self.config['sshKey'],look_for_keys=False,allow_agent=False,timeout=5,banner_timeout=5,auth_timeout=5)
        return client
    def run(self,client,command):
        _,out,err=client.exec_command(command,timeout=60);text=out.read().decode();error=err.read().decode()
        if out.channel.recv_exit_status():raise RuntimeError(error[:1000])
        return text.strip()
    def recover_guest(self):
        node=self.config['primary']
        # Names and UUIDs are validated as configuration, never interpolated arbitrary shell text.
        self.ps("Import-Module Hyper-V; $v=Hyper-V\\Get-VM -Id '"+node['vmId']+"'; if ($v.State -eq 'Off') {Hyper-V\\Start-VM -VM $v}")
        deadline=time.monotonic()+120
        while True:
            try:return self.connect(node)
            except (OSError,self.paramiko.SSHException):
                if time.monotonic()>deadline:raise
                time.sleep(2)
    def fresh_access(self,original,node,api):
        fresh=self.connect(node)
        try:
            if self.run(fresh,'sudo -n id -u')!='0':raise ValueError('Fresh SSH session lacks sudo privileges.')
            source=self.run(fresh,'printf "%s\\n" "$SSH_CONNECTION"').split()[0]
            if source!=self.config['managementSource']:raise ValueError('Management source differs from the enrolled source.')
            fresh_api=Api(node,self.credentials)
            fresh_api.call('/api/settings/get')
            fresh_api.call('/api/settings/set',enableCheckForUpdate='false')
            self.save('AccessVerified',managementSourceSeen=source)
        finally:fresh.close()
    def transfer(self,donor,receiver):
        snapshot=donor.call('/api/ddi/dhcp/state/get');owner=snapshot['node'];target=receiver.call('/api/ddi/dhcp/state/get')['node']
        plans=[p for p in snapshot['state']['plans'] if owner in p['owners']]
        if not plans:raise ValueError('No enrolled DHCP authority plans to recover.')
        for plan in plans:
            name=plan['scope']
            donor.call('/api/dhcp/scopes/disable',name=name)
            addresses=donor.call('/api/ddi/dhcp/ownedAddresses',scope=name)['addresses']
            state=donor.call('/api/ddi/dhcp/state/get')['state']
            replica=next((r for r in state['replicas'] if r['scope']==name and r['node']==owner),{'sequence':0,'entries':[]})
            if any(e.get('authorityOwner') for e in replica['entries']):raise ValueError('Recover borrowed authority through its original donor before this failover.')
            entries={socket.inet_ntoa(e['address'].to_bytes(4,'big')):e for e in replica['entries']}
            remaining=[]
            for address in addresses:
                prior=entries.get(address)
                if prior and prior['state']=='Grant':
                    if prior['borrower']!=target:raise ValueError('Capacity was already granted to a different receiver.')
                else:remaining.append(address)
            for offset in range(0,len(remaining),256):
                batch=remaining[offset:offset+256]
                current=donor.call('/api/ddi/dhcp/state/get')['state']
                sequence=next((r['sequence'] for r in current['replicas'] if r['scope']==name and r['node']==owner),0)
                request_id=uuid.uuid5(uuid.UUID(self.state['operationId']),name+':'+batch[0]).hex
                donor.call('/api/ddi/dhcp/handoff',scope=name,receiver=target,addresses=','.join(batch),requestId=request_id,expectedSequence=sequence)
            self.save('Surrendered',lastScope=name)
        final=donor.call('/api/ddi/dhcp/state/get')['state']
        final={'version':final['version'],'plans':plans,'replicas':[r for r in final['replicas'] if r['node']==owner and r['scope'] in [p['scope'] for p in plans]]}
        receiver.call('/api/ddi/dhcp/recoverFencedOwner',owner=owner,state=json.dumps(final))
        self.save('ReceiverCommitted')
    def failover(self):
        primary=self.config['primary'];target=self.config['secondary'];original=None
        try:
            try:original=self.connect(primary)
            except (OSError,self.paramiko.SSHException):pass
            self.fence() # No surrender or role change occurs before read-back verification.
            if original is None:original=self.recover_guest()
            self.run(original,'sudo -n systemctl start ddi-lab.service')
            deadline=time.monotonic()+30
            while True:
                try:donor=Api(primary,self.credentials,timeout=10);break
                except (OSError,urllib.error.URLError):
                    if time.monotonic()>deadline:raise
                    time.sleep(1)
            self.fresh_access(original,primary,donor)
            receiver=Api(target,self.credentials,timeout=30)
            self.transfer(donor,receiver)
            self.run(original,'sudo -n systemctl stop ddi-lab.service')
            self.save('Promoting')
            state=receiver.call('/api/admin/cluster/state')
            if next(n for n in state['clusterNodes'] if n['state']=='Self')['type']!='Primary':
                receiver.call('/api/admin/cluster/secondary/promote',forceDeletePrimary='true',retainFencedPrimary='true')
            state=receiver.call('/api/admin/cluster/state')
            if next(n for n in state['clusterNodes'] if n['state']=='Self')['type']!='Primary':raise ValueError('Promotion outcome is not confirmed.')
            if not any(n['state']=='Fenced' and primary['address'] in n['ipAddresses'] for n in state['clusterNodes']):raise ValueError('Former primary is not recorded as fenced.')
            fresh=Api(target,self.credentials);fresh.call('/api/settings/get');fresh.call('/api/settings/set',enableCheckForUpdate='false')
            self.save('Completed',message='Survivor owns surrendered capacity and is cluster primary; former primary remains fenced.')
        except Exception as error:
            self.save('Blocked',message=str(error))
            # Retain the physical fence. Never undo grants or reconnect the old data plane.
            raise
        finally:
            # Access was unchanged on SSH; after any ACL change repair reachability and revalidate
            # before releasing a previously working original administrative session.
            if original is not None:
                while True:
                    try:
                        fresh=self.connect(primary)
                        if self.run(fresh,'sudo -n id -u')!='0':raise RuntimeError('Fresh sudo check failed.')
                        fresh.close();break
                    except Exception as error:
                        self.save('AccessRecoveryRequired',message='Original session retained until a fresh privileged login succeeds: '+str(error))
                        time.sleep(5)
                original.close()
    def watch(self,once=False):
        failures=0
        while True:
            if self.state['phase']=='Completed':
                # Quarantine is persistent. Rejoining/unfencing is a separate reviewed recovery.
                if once:return
                time.sleep(30);self.fence('Completed');continue
            try:
                api=Api(self.config['primary'],self.credentials,timeout=3)
                state=api.call('/api/admin/cluster/state')
                if next(n for n in state['clusterNodes'] if n['state']=='Self')['type']!='Primary':raise ValueError('Configured primary is not the current primary.')
                failures=0
            except urllib.error.URLError as error:
                if isinstance(error.reason,ssl.SSLError):raise
                failures+=1
            except (TimeoutError,ConnectionError):failures+=1
            if failures>=self.config.get('failureCount',3):
                self.failover()
                if once:return
            if once:return
            time.sleep(self.config.get('pollSeconds',2))

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--config',required=True);parser.add_argument('--once',action='store_true');args=parser.parse_args()
    config=json.loads(Path(args.config).read_text(encoding='utf-8-sig'))
    if sys.platform!='win32':raise ValueError('The Hyper-V fencing provider runs on the Windows host.')
    for name in ('primary','secondary'):
        node=config[name];uuid.UUID(node['vmId']);socket.inet_aton(node['address'])
    Controller(config).watch(args.once)

if __name__=='__main__':main()
