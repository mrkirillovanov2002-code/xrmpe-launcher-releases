"""Read-only X-Ray DB research tool. No game DLLs, subprocesses, or game writes.

Format references (read, not downloaded as programs): OpenXRay/xray-16
src/xrCore/{LocatorAPI.cpp,LzHuf.cpp}. This independent Python reader only
accepts bounded chunks/records and validates extracted sizes and CRC32.
No encryption guessing, no archive writing. Local research only.
"""
from pathlib import Path, PureWindowsPath
import argparse, struct, json, zlib, hashlib, os, stat

WORK = Path(__file__).resolve().parent
GAME = Path('.')  # Research-only discover() helper; production builder passes an explicit game directory.
LIMIT = 64 * 1024 * 1024

def checked_path(name):
    p = PureWindowsPath(name)
    if not name or p.is_absolute() or p.drive or any(x in ('..', '.', '') for x in name.replace('\\', '/').split('/')):
        raise ValueError('Unsafe path: '+repr(name))
    if any(':' in x or x.endswith((' ', '.')) or x.split('.')[0].upper() in {'CON','PRN','AUX','NUL',*(f'COM{i}' for i in range(1,10)),*(f'LPT{i}' for i in range(1,10))} for x in p.parts):
        raise ValueError('Unsafe Windows component')
    return Path(*p.parts)

def no_links(p):
    for x in [p, *p.parents]:
        if x.exists() and (x.lstat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise ValueError('Reparse point rejected: '+str(x))

class Bits:
    def __init__(self, data): self.data, self.bit = data, 0
    def get(self, n=1):
        if self.bit+n > len(self.data)*8: raise ValueError('Truncated LZH bits')
        v=0
        for _ in range(n):
            v=(v<<1)|((self.data[self.bit//8]>>(7-self.bit%8))&1); self.bit+=1
        return v

def unlzh(data):
    if len(data)<4: raise ValueError('Short LZH header')
    size=struct.unpack_from('<I',data)[0]
    if not 0<size<=LIMIT: raise ValueError('LZH output cap')
    bits=Bits(data[4:]); nc=314; t=627; root=626
    freq=[1]*nc+[0]*(t+1-nc); son=[0]*t; parent=[0]*(t+nc)
    for i in range(nc): son[i]=i+t; parent[i+t]=i
    i=0
    for j in range(nc,t):
        freq[j]=freq[i]+freq[i+1];son[j]=i;parent[i]=parent[i+1]=j;i+=2
    freq[t]=65535
    lens=[3]+[4]*3+[5]*8+[6]*12+[7]*24+[8]*16
    codes={};code=0;prev=3
    for sym,n in enumerate(lens):
        code<<=n-prev;codes[n,code]=sym;code+=1;prev=n
    def update(sym):
        if freq[root]==0x4000:
            leaves=[((freq[i]+1)//2,son[i]) for i in range(t) if son[i]>=t]
            for i,(f,s) in enumerate(leaves):freq[i]=f;son[i]=s
            i=0
            for j in range(nc,t):
                f=freq[i]+freq[i+1];k=j-1
                while k>=0 and f<freq[k]:k-=1
                k+=1
                freq[k+1:j+1]=freq[k:j];son[k+1:j+1]=son[k:j]
                freq[k]=f;son[k]=i;i+=2
            for i in range(t):
                k=son[i];parent[k]=i
                if k<t:parent[k+1]=i
        c=parent[sym+t]
        while True:
            freq[c]+=1;k=freq[c];l=c+1
            if k>freq[l]:
                while k>freq[l]:l+=1
                l-=1;freq[c]=freq[l];freq[l]=k
                i=son[c];parent[i]=l
                if i<t:parent[i+1]=l
                j=son[l];son[l]=i;parent[j]=c
                if j<t:parent[j+1]=c
                son[c]=j;c=l
            c=parent[c]
            if c==0:break
    ring=bytearray(b' '*4096);r=4036;out=bytearray()
    while len(out)<size:
        c=son[root]
        while c<t:c=son[c+bits.get()]
        c-=t;update(c)
        if c<256:
            out.append(c);ring[r]=c;r=(r+1)&4095
        else:
            code=0;upper=None
            for n in range(1,9):
                code=(code<<1)|bits.get()
                if (n,code) in codes:upper=codes[n,code];break
            if upper is None:raise ValueError('Invalid LZH position')
            pos=(upper<<6)|bits.get(6);start=(r-pos-1)&4095;length=c-253
            if len(out)+length>size:raise ValueError('LZH length overflow')
            for k in range(length):
                b=ring[(start+k)&4095];out.append(b);ring[r]=b;r=(r+1)&4095
    return bytes(out)

class Archive:
    def __init__(self,path,base=0,length=None,label=None):
        self.path=Path(path);no_links(self.path);self.base=base
        self.length=length if length is not None else self.path.stat().st_size
        if base<0 or self.length<0 or base+self.length>self.path.stat().st_size:raise ValueError('Invalid archive range')
        self.label=label or self.path.name;self.chunks=[];self.entries=[];self.header=''
        with self.path.open('rb') as f:
            pos=0
            while pos<self.length:
                if pos+8>self.length:raise ValueError('Short chunk header')
                f.seek(base+pos);cid,n=struct.unpack('<II',f.read(8));pos+=8
                if n>self.length-pos:raise ValueError('Chunk outside archive')
                self.chunks.append(dict(id=cid,offset=pos,size=n));pos+=n
            toc=[c for c in self.chunks if c['id']&0x7fffffff==1]
            if len(toc)!=1:raise ValueError('Expected exactly one TOC')
            h=next((c for c in self.chunks if c['id']==666),None)
            if h and h['size']<8192:
                f.seek(base+h['offset']);self.header=f.read(h['size']).decode('cp1251')
            c=toc[0]
            if c['size']>LIMIT:raise ValueError('TOC cap')
            f.seek(base+c['offset']);raw=f.read(c['size'])
            raw=unlzh(raw) if c['id']&0x80000000 else raw
        p=0;names=set();data_chunks=[c for c in self.chunks if c['id']==0]
        while p<len(raw):
            if p+18>len(raw):raise ValueError('Truncated TOC record')
            n=struct.unpack_from('<H',raw,p)[0];p+=2
            if n<16 or n>4096 or p+n>len(raw):raise ValueError('Invalid TOC record size')
            real,packed,crc=struct.unpack_from('<III',raw,p)
            name=raw[p+12:p+n-4].decode('cp1251');offset=struct.unpack_from('<I',raw,p+n-4)[0];p+=n
            if name.endswith('\\') and real==0 and packed==0:
                checked_path(name[:-1]);continue
            checked_path(name)
            if name.lower() in names:raise ValueError('Duplicate name')
            names.add(name.lower())
            if not any(offset>=c['offset'] and offset+packed<=c['offset']+c['size'] for c in data_chunks):
                raise ValueError('File extent outside data chunk: '+name)
            self.entries.append(dict(name=name,real=real,packed=packed,crc32=f'{crc:08x}',offset=offset))
            if len(self.entries)>200000:raise ValueError('Entry cap')
    def read(self,name):
        e=next(x for x in self.entries if x['name']==name)
        if max(e['real'],e['packed'])>LIMIT:raise ValueError('File size cap')
        with self.path.open('rb') as f:f.seek(self.base+e['offset']);b=f.read(e['packed'])
        if len(b)!=e['packed']:raise ValueError('Short payload')
        if e['packed']!=e['real']:b=unlzo(b,e['real'])
        if len(b)!=e['real'] or f'{zlib.crc32(b):08x}'!=e['crc32']:raise ValueError('Size/CRC mismatch: '+name)
        return b
    def extract(self,name):
        b=self.read(name);target=WORK/'extracted'/checked_path(self.label)/checked_path(name)
        if not target.resolve().is_relative_to(WORK):raise ValueError('Output escaped work')
        no_links(target);target.parent.mkdir(parents=True,exist_ok=True)
        if target.exists():
            if target.read_bytes()!=b:raise ValueError('Refusing overwrite')
        else:
            with target.open('xb') as f:f.write(b)
        return target

def tar_members(path):
    no_links(path)
    with Path(path).open('rb') as f:
        size=Path(path).stat().st_size
        for _ in range(4096):
            h=f.read(512)
            if len(h)!=512:raise ValueError('Truncated TAR')
            if not any(h):return
            expected=int(h[148:156].strip(b'\0 ') or b'0',8)
            if sum(h[:148])+256+sum(h[156:])!=expected:raise ValueError('TAR checksum')
            name=h[:100].split(b'\0')[0].decode('utf8');checked_path(name)
            n=int(h[124:136].strip(b'\0 ') or b'0',8);start=f.tell()
            if h[156:157] not in (b'0',b'\0') or start+n>size:raise ValueError('Unsupported TAR entry')
            yield name,start,n
            f.seek(start+((n+511)//512)*512)
        raise ValueError('TAR entry cap')

def discover():
    result=[]
    for folder in ('patches','resources'):
        for p in sorted((GAME/folder).glob('*.db*')):
            result.append(Archive(p,label=folder+'_'+p.name))
    p=GAME/'addons'/'xrmpe_pvp.addon'
    for name,offset,n in tar_members(p):
        if name in ('xrmpe_pvp_addon.db0','xrmpe_pvp_addon.db1'):
            result.append(Archive(p,offset,n,name))
    return result

def unlzo(data,size):
    raise ValueError('LZO reader not yet implemented; refusing to guess codec')

if __name__=='__main__':
    archives=discover()
    inventory=[dict(label=a.label,path=str(a.path),base=a.base,length=a.length,header=a.header,chunks=a.chunks,entries=a.entries) for a in archives]
    (WORK/'inventory.json').write_text(json.dumps(inventory,ensure_ascii=False,indent=2),encoding='utf8')
    print(json.dumps({a.label:len(a.entries) for a in archives},indent=2))
