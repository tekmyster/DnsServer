"""Fresh-session administrative and DNS checks; never print credentials or tokens."""
import argparse
import json
import socket
import secrets
import struct
import urllib.parse
import urllib.request

p=argparse.ArgumentParser()
p.add_argument('url')
p.add_argument('credential_file')
p.add_argument('--dns-port', type=int, default=53)
a=p.parse_args()
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
def call(path, data=None, token=None):
    request=urllib.request.Request(a.url.rstrip('/')+path, data=urllib.parse.urlencode(data or {}).encode(), headers={'Authorization':'Bearer '+token} if token else {})
    with opener.open(request, timeout=10) as response:
        value=json.load(response)
    if value.get('status')!='ok': raise RuntimeError('Administrative request failed: '+path)
    return value
with open(a.credential_file, encoding='utf-8') as f: password=f.read().strip()
original=call('/api/user/login',{'user':'admin','pass':password,'includeInfo':'true'})['token']
fresh=call('/api/user/login',{'user':'admin','pass':password,'includeInfo':'true'})['token']
settings=call('/api/settings/get', token=fresh)
inventory=call('/api/ipam/list', token=fresh)['response']['inventory']
view=next(v for v in inventory['views'] if v['id']=='default')
call('/api/ipam/views/set', {'revision':inventory['revision'],'id':view['id'],'name':view['name'],'description':view.get('description','')}, fresh)
zone='package-check-'+secrets.token_hex(4)+'.example'
call('/api/zones/create', {'zone':zone,'type':'Primary'}, fresh)
question=b''.join(bytes([len(label)])+label.encode() for label in zone.split('.'))+b'\x00'+struct.pack('!HH',6,1)
packet=struct.pack('!HHHHHH',0x5454,0x0100,1,0,0,0)+question
for tcp in [False,True]:
    with socket.socket(socket.AF_INET,socket.SOCK_STREAM if tcp else socket.SOCK_DGRAM) as s:
        s.settimeout(10)
        if tcp:
            s.connect(('127.0.0.1',a.dns_port));s.sendall(struct.pack('!H',len(packet))+packet)
            def exact(n):
                result=b''
                while len(result)<n:
                    piece=s.recv(n-len(result))
                    if not piece: raise RuntimeError('DNS stream closed early')
                    result+=piece
                return result
            response=exact(struct.unpack('!H',exact(2))[0])
        else:
            s.sendto(packet,('127.0.0.1',a.dns_port));response,_=s.recvfrom(4096)
        identifier,flags,_,answers,_,_=struct.unpack('!HHHHHH',response[:12])
        if identifier!=0x5454 or flags & 15 or answers<1: raise RuntimeError('Packaged DNS query failed')
call('/api/zones/delete', {'zone':zone}, fresh)
call('/api/settings/get', token=original)
call('/api/user/logout', token=fresh)
call('/api/user/logout', token=original)
print('PASS: packaged service, separate fresh administrative read/modify sessions, UDP/TCP authoritative DNS.')
