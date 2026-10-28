"""Render four local-only textured OGF views from the installed xrMPE files."""
from pathlib import Path
import sys, json, os, numpy as np
from PIL import Image

HERE=Path(__file__).resolve().parent
import inspect_geometry as g

work=Path(os.environ['ZL_BUILD_HERE'])
manifest=json.loads((HERE/'costumes.json').read_text(encoding='utf8'))
g.SOURCE=work/'LocalMods/meshes/actors/stalker_mp'
textures=work/'extracted/patches_xrmpe_meshes_actors.db0/textures'
out=Path(os.environ['ZL_PREVIEW_OUTPUT']);out.mkdir(parents=True,exist_ok=True)
cache={}
views={'front':0,'right':np.pi/2,'back':np.pi,'left':3*np.pi/2}

def texture(name):
    if name in cache:return cache[name]
    path=textures/Path(*name.split('\\')).with_suffix('.dds')
    if name=='act\\act_mp_tdm_green_1':
        path=work/'LocalMods/textures/act/act_mp_tdm_green_1.dds'
    if not path.is_file():raise FileNotFoundError(name)
    im=np.asarray(Image.open(path).convert('RGB'))
    cache[name]=im
    return im

def render(parts,angle):
    width,height=288,500
    canvas=np.full((height,width,3),(23,28,32),dtype='uint8')
    depth=np.full((height,width),-1e10,dtype='float32')
    co,si=np.cos(angle),np.sin(angle)
    for part in parts:
        tex=texture(part['texture']);th,tw=tex.shape[:2]
        verts=part['vertices']
        for face in part['faces']:
            xyz=np.asarray([verts[i]['xyz'] for i in face])
            uv=np.asarray([verts[i]['uv'] for i in face])
            horizontal=xyz[:,0]*co+xyz[:,2]*si
            distance=-xyz[:,2]*co+xyz[:,0]*si
            screen=np.column_stack((width/2+horizontal*214,465-xyz[:,1]*214))
            lo=np.maximum(np.floor(screen.min(0)).astype(int),(0,0))
            hi=np.minimum(np.ceil(screen.max(0)).astype(int)+1,(width,height))
            if np.any(hi<=lo):continue
            a=screen[0]-screen[2];b=screen[1]-screen[2]
            det=a[0]*b[1]-a[1]*b[0]
            if abs(det)<1e-8:continue
            yy,xx=np.mgrid[lo[1]:hi[1],lo[0]:hi[0]]
            q=np.stack((xx+.5,yy+.5),-1)-screen[2]
            u=(q[:,:,0]*b[1]-q[:,:,1]*b[0])/det
            v=(a[0]*q[:,:,1]-a[1]*q[:,:,0])/det
            bc=np.stack((u,v,1-u-v),-1)
            z=bc@distance
            sl=np.s_[lo[1]:hi[1],lo[0]:hi[0]]
            mask=(bc.min(2)>=-1e-6)&(z>depth[sl])
            if not mask.any():continue
            t=bc@uv
            rgb=tex[(np.clip(t[:,:,1],0,1)*(th-1)).astype(int),(np.clip(t[:,:,0],0,1)*(tw-1)).astype(int)]
            canvas[sl][mask]=rgb[mask];depth[sl][mask]=z[mask]
    return Image.fromarray(canvas)

for number,entry in enumerate(manifest,1):
    name=Path(entry['model'].replace('\\','/')).stem
    parts,_=g.load(name)
    for view,angle in views.items():
        render(parts,angle).save(out/(entry['costume']+'-'+view+'.png'))
    print(f"ZL_PREVIEW|{number}|{len(manifest)}|{entry['costume']}",flush=True)
assert len(list(out.glob('*.png')))==120
