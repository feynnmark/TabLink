"""Hardware integration: installed USB host, profiles, bounded video and reconnect.

Temporarily closes the receiver and restores it on exit. Local output is private.
Requires one authorized USB Android device and TabLink-managed VDD.
"""
import json
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import time

ROOT=Path(__file__).resolve().parents[1]
LOCAL=ROOT/'tests/local'
LOCAL.mkdir(exist_ok=True)
DATA=Path(os.environ['LOCALAPPDATA'])/'TabLink'
APP=DATA/'app'
ADB=APP/'adb/adb.exe'
EXE=APP/'TabLink.exe'
TOKEN=(DATA/'pairing.key').read_text().strip()
RESULTS=[]

def run(*args):
    return subprocess.run([str(x) for x in args],check=True,capture_output=True,text=True,
                          timeout=45,creationflags=subprocess.CREATE_NO_WINDOW).stdout

def screens():
    run(EXE,'--diagnose')
    lines=(DATA/'displays.txt').read_text().splitlines()
    virtual={line.split(' ')[0] for line in lines if 'Virtual Display Driver flags=' in line}
    return tuple(line for line in lines if '{X=' in line and line.split(' ')[0] not in virtual)

def virtual_active():
    run(EXE,'--diagnose')
    lines=(DATA/'displays.txt').read_text().splitlines()
    return any('Virtual Display Driver flags=1' in line for line in lines)

def wait_for(check,timeout=15):
    until=time.monotonic()+timeout
    while time.monotonic()<until:
        if check():return
        time.sleep(.25)
    raise AssertionError('Hardware state did not settle')

def exact(sock,length):
    data=bytearray()
    while len(data)<length:
        chunk=sock.recv(length-len(data))
        if not chunk:raise EOFError('Host closed connection; check the local host log')
        data.extend(chunk)
    return bytes(data)

def frame(sock,ack=True):
    length,pts=struct.unpack('!iq',exact(sock,12))
    assert 0<length<=4*1024*1024
    data=exact(sock,length)
    types=[(data[m.end()]>>1)&63 for m in re.finditer(rb'\x00\x00\x00\x01|\x00\x00\x01',data)]
    assert {32,33,34,35}<=set(types),types
    assert any(t in (19,20) for t in types) and not any(t in (0,1) for t in types),types
    if ack:sock.sendall(f'ACK {pts}\n'.encode())
    return data,pts

def record(name):
    RESULTS.append(dict(check=name,passed=True))
    print('PASS',name,flush=True)

try:
    time.sleep(3)
    run(ADB,'-d','shell','am','force-stop','com.tablink.display')
    time.sleep(4)
    wait_for(lambda:not virtual_active())
    baseline=screens()
    with socket.create_connection(('127.0.0.1',27839),timeout=10) as sock:
        sock.sendall(('TABLINK/5 '+'0'*64+' 1920 1080 60 8\n').encode())
        assert sock.recv(1)==b''
    assert screens()==baseline
    assert not virtual_active()
    record('authentication rejected before display activation')
    # A broad Main-10 request exercises mode selection on the installed driver.
    # The 8-bit request verifies the fallback on the same hardware pipeline.
    for cycle,(w,h,rate,bits) in enumerate(((3840,2160,90,10),(1920,1200,60,8),(3840,2160,90,10))):
        with socket.create_connection(('127.0.0.1',27839),timeout=30) as sock:
            sock.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
            sock.sendall(f'TABLINK/5 {TOKEN} {w} {h} {rate} {bits}\n'.encode())
            magic,width,height,fps,depth=struct.unpack('!iiiii',exact(sock,20))
            assert magic==0x544c5635 and 0<width<=w and 0<height<=h and 0<fps<=rate and depth==bits
            if cycle==0:
                held=[frame(sock,False) for _ in range(4)]
                sock.settimeout(.2)
                try:
                    sock.recv(1)
                    raise AssertionError('Exceeded four unacknowledged pictures')
                except socket.timeout:pass
                time.sleep(.3);sock.settimeout(30)
                sock.sendall(f'ACK {held[-1][1]}\n'.encode())
                _,pts=frame(sock)
                assert pts-held[-1][1]>200000,'Stale FIFO instead of latest capture'
                record('bounded transport and stale capture replacement')
            frames=[frame(sock)[0] for _ in range(12)]
            sample=LOCAL/f'profile-{bits}.hevc'
            sample.write_bytes(b''.join(frames))
            ffprobe=APP/'video/ffprobe.exe'
            if not ffprobe.exists():ffprobe=next((ROOT/'tools/ffmpeg').rglob('ffprobe.exe'))
            metadata=json.loads(run(ffprobe,'-v','error','-show_entries','stream=profile,width,height,pix_fmt,has_b_frames','-of','json',sample))['streams'][0]
            assert metadata['profile']==('Main 10' if bits==10 else 'Main')
            assert metadata['pix_fmt']==('yuv420p10le' if bits==10 else 'yuv420p')
            assert metadata['width']==width and metadata['height']==height and metadata['has_b_frames']==0
            assert screens()==baseline,'Physical monitor geometry changed'
            record(f'{bits}-bit independent HEVC and negotiated dimensions, cycle {cycle+1}')
        time.sleep(4)
        wait_for(lambda:not virtual_active())
        assert screens()==baseline
        record(f'physical layout preserved after reconnect cycle {cycle+1}')
finally:
    run(ADB,'-d','reverse','tcp:27839','tcp:27839')
    run(ADB,'-d','shell','am','start','-n','com.tablink.display/.MainActivity','--es','token',TOKEN)
    (LOCAL/'video-validation.json').write_text(json.dumps(RESULTS,indent=2),encoding='utf-8')
