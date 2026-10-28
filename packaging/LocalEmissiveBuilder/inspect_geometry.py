"""Static OGF/UV inspection only; no engine imports, launches or mesh writes.
Disk layout cross-checked with OpenXRay FMesh.hpp/SkeletonX.cpp and
PavelBlend/blender-xray formats/ogf/imp/verts.py (read as documentation only).
"""
from pathlib import Path
import struct,json,hashlib,math
from collections import defaultdict

WORK=Path(__file__).resolve().parent
PROJECT=WORK
INPUT=WORK/'extracted'
SOURCE=INPUT/'patches_xrmpe_meshes_actors.db0'/'meshes'/'actors'/'stalker_mp'

def chunks(data):
    pos=0;seen=set()
    while pos<len(data):
        if pos+8>len(data):raise ValueError('Short OGF chunk')
        kind,n=struct.unpack_from('<II',data,pos);pos+=8
        if kind in seen or kind&0x80000000 or pos+n>len(data):raise ValueError('Invalid OGF chunk')
        seen.add(kind);yield kind,data[pos:pos+n];pos+=n

def parse_vertices(b):
    fmt,count=struct.unpack_from('<II',b);assert 0<count<100000
    link={1:1,2:2,3:3,4:4,0x12071980:1,0x240e3300:2,0x481c6600:3,0x5a238f80:4}.get(fmt)
    if link is None:raise ValueError('Unknown vertex format '+hex(fmt))
    stride=(len(b)-8)//count
    expected={1:(36,60),2:(64,),3:(70,),4:(76,)}[link]
    if stride not in expected or 8+count*stride!=len(b):raise ValueError('Vertex size mismatch')
    verts=[]
    for i in range(count):
        p=8+i*stride
        if link==1:
            xyz=struct.unpack_from('<3f',b,p);uv=struct.unpack_from('<2f',b,p+stride-12);bones=(struct.unpack_from('<I',b,p+stride-4)[0],);weights=(1.,)
        else:
            bones=struct.unpack_from('<'+'H'*link,b,p)
            xyz=struct.unpack_from('<3f',b,p+2*link)
            uv=struct.unpack_from('<2f',b,p+stride-8)
            ws=struct.unpack_from('<'+'f'*(link-1),b,p+2*link+48);weights=(*ws,1-sum(ws))
        if not all(math.isfinite(v) for v in (*xyz,*uv,*weights)):raise ValueError('Nonfinite vertex')
        if max(bones)>255 or min(weights)<-0.0001 or max(weights)>1.0001:raise ValueError('Invalid skinning')
        verts.append(dict(xyz=xyz,uv=uv,bones=bones,weights=weights))
    return verts,dict(format=hex(fmt),links=link,stride=stride,count=count)

def components(verts,faces):
    parent={}
    def key(i):return tuple(round(v,6) for v in verts[i]['xyz'])
    def find(k):
        parent.setdefault(k,k)
        while parent[k]!=k:parent[k]=parent[parent[k]];k=parent[k]
        return k
    for tri in faces:
        r=find(key(tri[0]))
        for i in tri[1:]:parent[find(key(i))]=r
    groups=defaultdict(list)
    for tri in faces:groups[find(key(tri[0]))].append(tri)
    result=[]
    for fs in groups.values():
        ids=sorted({i for tri in fs for i in tri});vs=[verts[i] for i in ids]
        result.append(dict(triangles=len(fs),vertices=len(ids),xyz_min=[min(v['xyz'][j] for v in vs) for j in range(3)],xyz_max=[max(v['xyz'][j] for v in vs) for j in range(3)],uv_min=[min(v['uv'][j] for v in vs) for j in range(2)],uv_max=[max(v['uv'][j] for v in vs) for j in range(2)],bones=sorted({b for v in vs for b in v['bones']})))
    return result

def load(name):
    path=SOURCE/(name+'.ogf');data=path.read_bytes();root=dict(chunks(data));assert root[1][0]==4
    bone_data=root[13];bone_count=struct.unpack_from('<I',bone_data)[0];bp=4;bone_names=[]
    assert 0<bone_count<=256
    for _ in range(bone_count):
        end=bone_data.index(0,bp);bone_names.append(bone_data[bp:end].decode('ascii'));bp=end+1
        bp=bone_data.index(0,bp)+1+60
    assert bp==len(bone_data)
    result=[]
    for idx,b in chunks(root[9]):
        c=dict(chunks(b));tex,shader=c[2].split(b'\0')[:2]
        vertices,info=parse_vertices(c[3]);n=struct.unpack_from('<I',c[4])[0]
        assert all(b<bone_count for v in vertices for b in v['bones'])
        if n%3 or len(c[4])!=4+n*2:raise ValueError('Index size mismatch')
        indices=struct.unpack_from('<'+'H'*n,c[4],4)
        if max(indices)>=len(vertices):raise ValueError('Index outside vertices')
        windows=[]
        if 6 in c:
            sw=c[6];count=struct.unpack_from('<I',sw,16)[0]
            if count==0 or len(sw)!=20+8*count:raise ValueError('SWI size mismatch')
            for j in range(count):
                offset,tris,verts=struct.unpack_from('<IHH',sw,20+8*j)
                if offset+3*tris>n or verts>len(vertices) or max(indices[offset:offset+3*tris])>=verts:raise ValueError('SWI range mismatch')
                windows.append(dict(offset=offset,triangles=tris,vertices=verts))
            first=windows[0];draw_indices=indices[first['offset']:first['offset']+3*first['triangles']]
        else:draw_indices=indices
        faces=[draw_indices[i:i+3] for i in range(0,len(draw_indices),3)]
        groups=components(vertices,faces)
        for group in groups:group['bone_names']=[bone_names[i] for i in group['bones']]
        result.append(dict(child=idx,texture=tex.decode('cp1251'),shader=shader.decode('cp1251'),vertex_info=info,index_buffer_triangles=n//3,highest_detail_triangles=len(faces),has_progressive_windows=6 in c,progressive_window_count=len(windows),first_window=windows[0] if windows else None,last_window=windows[-1] if windows else None,components=groups,vertices=vertices,faces=faces))
    return result,hashlib.sha256(data).hexdigest()

def texture(name):
    from PIL import Image
    for folder in ('patches_xrmpe_meshes_actors.db0','xrmpe_pvp_addon.db0','resources_vanilla.db1'):
        path=INPUT/folder/'textures'/Path(*name.split('\\')).with_suffix('.dds')
        if path.exists():return Image.open(path).convert('RGB')
    raise ValueError('Texture not found '+name)

def draw_model(name,parts):
    from PIL import Image,ImageDraw
    sheet=Image.new('RGB',(1500,1000),(20,24,27));d=ImageDraw.Draw(sheet)
    allverts=[v for p in parts for v in p['vertices']]
    lo=[min(v['xyz'][j] for v in allverts) for j in range(3)]
    hi=[max(v['xyz'][j] for v in allverts) for j in range(3)]
    for view,(axis,label) in enumerate(((0,'X/Y'),(2,'Z/Y'))):
        ox=view*750
        d.text((ox+15,15),name+' '+label,fill='white')
        scale=min(680/(hi[axis]-lo[axis]),900/(hi[1]-lo[1]))
        def pt(i,p):
            xyz=p['vertices'][i]['xyz'];return (ox+375+(xyz[axis]-(lo[axis]+hi[axis])/2)*scale,960-(xyz[1]-lo[1])*scale)
        for p in parts:
            glow=p['shader']=='models\\selflightl'
            for face in p['faces']:
                poly=[pt(i,p) for i in face]
                d.line(poly+[poly[0]],fill=(255,180,45) if glow else (83,103,113),width=3 if glow else 1)
    sheet.save(WORK/(name+'-geometry.png'))

def uv(name,parts,tex):
    from PIL import ImageDraw
    image=texture(tex).resize((1024,1024));d=ImageDraw.Draw(image)
    for part in parts:
        if part['texture']!=tex:continue
        for face in part['faces']:
            pts=[tuple(v*1024 for v in part['vertices'][i]['uv']) for i in face]
            d.line(pts+[pts[0]],fill=(255,180,40) if part['shader']=='models\\selflightl' else (75,170,220),width=2 if part['shader']=='models\\selflightl' else 1)
    image.save(WORK/(name+'-uv.png'))

if __name__=='__main__':
    summaries=[]
    for name,tex in [('stalker_neutral_1_face_1','act\\act_stalker_neutral_1'),('stalker_tdm_team_green_1','act\\act_mp_tdm_green_1')]:
        parts,digest=load(name);draw_model(name,parts);uv(name,parts,tex)
        summaries.append(dict(name=name,sha256=digest,parts=[{k:v for k,v in p.items() if k not in ('vertices','faces')} for p in parts]))
    (WORK/'geometry.json').write_text(json.dumps(summaries,indent=2),encoding='utf8')
    print(json.dumps([dict(name=s['name'],parts=len(s['parts']),index_buffer_triangles=sum(p['index_buffer_triangles'] for p in s['parts']),selflight=[p for p in s['parts'] if p['shader']=='models\\selflightl']) for s in summaries],indent=2))
