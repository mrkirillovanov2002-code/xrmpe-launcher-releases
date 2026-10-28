"""Offline, local-only 3D emissive pack built from installed xrMPE resources.

No original OGF/DDS/DB or game installation is written. The 30 models are
individually projected onto their actual upper-arm triangles. This is a
static candidate, not a claim that every model was accepted in the engine.
"""
from pathlib import Path
import sys, json, struct, hashlib, os, zlib, runpy
import numpy as np

SCRIPT_DIR = Path(__file__).resolve().parent
HERE = Path(os.environ['ZL_BUILD_HERE'])
sys.path.insert(0, str(SCRIPT_DIR))
import inspect_geometry as g
from read_db import Archive, checked_path

def make_db(files):
    header=b'[header]\nauto_load = true\ncreator = ZoneLink local VFS research\nentry_point = $fs_root$\\gamedata\\\nlevel_name = single\nlevel_ver = 1.0\n'
    head=struct.pack('<II',666,len(header))+header
    start=len(head)+8;payload=b'';toc=b''
    for name,data in files:
        checked_path(name);n=name.encode('ascii');offset=start+len(payload)
        toc+=struct.pack('<HIII',16+len(n),len(data),len(data),zlib.crc32(data))+n+struct.pack('<I',offset)
        payload+=data
    return head+struct.pack('<II',0,len(payload))+payload+struct.pack('<II',1,len(toc))+toc

def sha(data): return hashlib.sha256(data).hexdigest().upper()
def chunks(items): return b''.join(struct.pack('<II', k, len(b)) + b for k,b in items)
def read_skeleton(blob):
    count = struct.unpack_from('<I', blob)[0]; p = 4; names = []
    for _ in range(count):
        end = blob.index(0,p); names.append(blob[p:end].decode('ascii')); p = end+1
        p = blob.index(0,p)+61
    assert p == len(blob)
    return names

def vertex_basis(blob, info, count):
    result = []
    for i in range(count):
        p = 8+i*info['stride']+(0 if info['links']==1 else 2*info['links'])+12
        result.append(struct.unpack_from('<9f',blob,p))
    return np.asarray(result).reshape(-1,3,3)

def pn(P,N,bc):
    def edge(i,j): return (2*P[i]+P[j]-np.dot(P[j]-P[i],N[i])*N[i])/3
    a,b,c,d,e,f = edge(0,1),edge(1,0),edge(1,2),edge(2,1),edge(2,0),edge(0,2)
    E=(a+b+c+d+e+f)/6; center=E+(E-P.mean(0))/2; u,v,w=bc
    return P[0]*u**3+P[1]*v**3+P[2]*w**3+a*3*u*u*v+b*3*u*v*v+f*3*u*u*w+c*3*v*v*w+e*3*u*w*w+d*3*v*w*w+center*6*u*v*w

def subdiv(tri,level):
    if not level:return [tri]
    a,b,c=tri; ab=(a+b)/2; bc=(b+c)/2; ca=(c+a)/2
    return sum((subdiv(t,level-1) for t in (np.array([a,ab,ca]),np.array([ab,b,bc]),np.array([ca,bc,c]),np.array([ab,bc,ca]))),[])

def project(q, data):
    fs,P,basis,vertices,a,b,c,e,f,n,d00,d01,d11,den=data
    plane=q-((q-a)*n).sum(1)[:,None]*n
    d=plane-a; d20=(d*e).sum(1); d21=(d*f).sum(1)
    u=(d11*d20-d01*d21)/np.maximum(den,1e-16)
    v=(d00*d21-d01*d20)/np.maximum(den,1e-16)
    inside=(u>=0)&(v>=0)&(u+v<=1)&(den>1e-12)
    points=plane.copy();dist=((q-points)**2).sum(1);dist[~inside]=np.inf
    for x,y in ((a,b),(b,c),(c,a)):
        edge=y-x;t=np.clip(((q-x)*edge).sum(1)/np.maximum((edge*edge).sum(1),1e-16),0,1)
        candidate=x+t[:,None]*edge;ds=((q-candidate)**2).sum(1)
        ix=ds<dist;dist[ix]=ds[ix];points[ix]=candidate[ix]
    i=int(np.argmin(dist));d=points[i]-a[i]
    u=(d11[i]*np.dot(d,e[i])-d01[i]*np.dot(d,f[i]))/den[i]
    v=(d00[i]*np.dot(d,f[i])-d01[i]*np.dot(d,e[i]))/den[i]
    bc=np.maximum([1-u-v,u,v],0);bc/=sum(bc)
    ids=fs[i]
    B=np.einsum('i,ijk->jk',bc,basis[ids]);B/=np.maximum(np.linalg.norm(B,axis=1)[:,None],1e-10)
    pos=pn(P[ids],basis[ids,0],bc)+B[0]*.003
    weights={}
    for j,index in enumerate(ids):
        for bone,weight in zip(vertices[index]['bones'],vertices[index]['weights']):
            weights[bone]=weights.get(bone,0)+bc[j]*weight
    pairs=sorted(weights.items(),key=lambda z:-z[1])[:4]
    total=sum(z for _,z in pairs);pairs=sorted((b,z/total) for b,z in pairs)
    pairs += [(pairs[0][0],0.)]*(4-len(pairs))
    return pos,B,pairs,float(np.sqrt(dist[i]))

def shoulder_data(part, raw_child, bone_id):
    c=dict(g.chunks(raw_child)); v,info=g.parse_vertices(c[3])
    faces=np.asarray([f for f in part['faces'] if all(
        sum(w for bone,w in zip(v[i]['bones'],v[i]['weights']) if bone==bone_id)>.75 for i in f)])
    if len(faces)<8:return None
    P=np.asarray([x['xyz'] for x in v]); basis=vertex_basis(c[3],info,len(v))
    T=P[faces];a,b,c=T[:,0],T[:,1],T[:,2];e=b-a;f=c-a;n=np.cross(e,f)
    n/=np.maximum(np.linalg.norm(n,axis=1)[:,None],1e-15)
    d00=(e*e).sum(1);d01=(e*f).sum(1);d11=(f*f).sum(1);den=d00*d11-d01*d01
    return faces,P,basis,v,a,b,c,e,f,n,d00,d01,d11,den

def surface_band(projectors):
    """For sparse shells, clip the *actual* shoulder surface to a narrow strip.

    This uses the same source vertices/weights and PN interpolation, avoiding
    nearest-point collapse and keeping original geometry untouched.
    """
    records=[]; indices=[]; points=[]; normals=[]; cache={}; widths=[]
    for bone,data in projectors.items():
        faces,P,basis,vertices,*_=data
        side=1 if np.mean(P[faces,0])>0 else -1
        arm=P[np.unique(faces)]; radial=np.abs(arm[:,0]);
        center=float(np.quantile(radial,.50));low=center-.030;high=center+.030
        widths.append((bone,round(low,4),round(high,4)))
        yzcenter=arm[:,1:3].mean(0)
        for face in faces:
            tri=P[face];poly=[np.eye(3)[i] for i in range(3)]
            for bound,keep_above in ((low,True),(high,False)):
                if not poly:break
                new=[]
                for ia in range(len(poly)):
                    a=poly[ia];b=poly[(ia+1)%len(poly)]
                    va=float(side*np.dot(a,tri[:,0])-bound);vb=float(side*np.dot(b,tri[:,0])-bound)
                    inside_a=va>=-1e-9 if keep_above else va<=1e-9
                    inside_b=vb>=-1e-9 if keep_above else vb<=1e-9
                    if inside_a:new.append(a)
                    if inside_a!=inside_b:new.append(a+(b-a)*va/(va-vb))
                poly=new
            if len(poly)<3:continue
            for k in range(1,len(poly)-1):
                # Extra midpoint samples follow the installed PN contour.
                for sub in subdiv(np.array([poly[0],poly[k],poly[k+1]]),1):
                    local=[]
                    for bc in sub:
                        bc=np.maximum(bc,0);bc/=sum(bc)
                        B=np.einsum('i,ijk->jk',bc,basis[face]);B/=np.maximum(np.linalg.norm(B,axis=1)[:,None],1e-10)
                        pos=pn(P[face],basis[face,0],bc)+B[0]*.003
                        radial_x=float(abs(np.dot(bc,tri[:,0])));t=np.clip((radial_x-low)/(high-low),0,1)
                        angle=np.arctan2(pos[1]-yzcenter[0],pos[2]-yzcenter[1])
                        uv=(.4798+.0545*t,.0069+.2844*(angle+np.pi)/(2*np.pi))
                        weights={}
                        for j,vid in enumerate(face):
                            for bid,w in zip(vertices[vid]['bones'],vertices[vid]['weights']):
                                weights[bid]=weights.get(bid,0)+bc[j]*w
                        pairs=sorted(weights.items(),key=lambda z:-z[1])[:4]
                        total=sum(z for _,z in pairs);pairs=sorted((bid,w/total) for bid,w in pairs)
                        pairs += [(pairs[0][0],0.)]*(4-len(pairs))
                        key=(bone,*np.round(pos,6),*np.round(uv,6))
                        if key not in cache:
                            cache[key]=len(records)
                            records.append(struct.pack('<4H12f3f2f',*(bid for bid,w in pairs),*pos,*B.flatten(),*(w for bid,w in pairs[:3]),*uv))
                            points.append(pos);normals.append(B[0])
                        local.append(cache[key])
                    indices.append(local)
    if not indices:return None
    pos=np.asarray(points);normals=np.asarray(normals);index=np.asarray(indices,dtype='int32')
    geom=np.cross(pos[index[:,1]]-pos[index[:,0]],pos[index[:,2]]-pos[index[:,0]])
    area=np.linalg.norm(geom,axis=1)
    keep=area>=1e-9
    index=index[keep];geom=geom[keep];area=area[keep]
    if len(index)<24:return None
    flip=np.einsum('ij,ij->i',geom,normals[index].mean(1))<0
    index[flip]=index[flip][:,[0,2,1]]
    return records,index.ravel().tolist(),points,normals,area,widths

manifest={'entries':json.loads((SCRIPT_DIR/'costumes.json').read_text(encoding='utf-8-sig'))}
assert len(manifest['entries'])==30
donor_path=g.SOURCE/'stalker_tdm_team_green_1.ogf';donor_raw=dict(g.chunks(donor_path.read_bytes()))
donor_parts,_=g.load('stalker_tdm_team_green_1')
donor_band=dict(g.chunks(dict(g.chunks(donor_raw[9]))[2]))
assert donor_band[2].split(b'\0')[:2]==[b'act\\act_mp_tdm_green_1',b'models\\selflightl']
donor_vertices,_=g.parse_vertices(donor_band[3]); donor_faces=donor_parts[2]['faces']
donor_names=read_skeleton(donor_raw[13]);donor_side={donor_names[v['bones'][0]].split('_')[1] for v in donor_vertices}
assert donor_side=={'l','r'}
approved_starter=runpy.run_path(str(SCRIPT_DIR/'build_starter.py'))['candidate']
donor_centers={bone:np.mean([v['xyz'] for v in donor_vertices if v['bones'][0]==bone],axis=0)
               for bone in {v['bones'][0] for v in donor_vertices}}
# The narrower shell on these models cannot accept the donor's full span.
# Scale each arm in its own local donor frame, preserving the native UV/design.
fit_scale={
    'black_coat_outfit':.65,'brown_coat_outfit':.65,
    'military_outfit':.75,'dolg_heavy_outfit':.75,
    'exo_outfit':.65,'dolg_exo_outfit':.65,'svoboda_exo_outfit':.65,
    'svoboda_heavy_outfit':.75,'soldier_outfit_light':.75}
starter=['stalker_neutral_1_face_1','stalker_neutral_1_mask','stalker_neutral_1']
costumes=[(row['costume'],Path(row['model'].replace('\\','/')).stem) for row in manifest['entries']]
costumes += [('starter_brown_'+str(i+1),name) for i,name in enumerate(starter)]
models=[];entries=[];issues=[]
for costume,name in costumes:
    source_path=next(g.INPUT.glob('*/meshes/actors/stalker_mp/'+name+'.ogf'),None)
    if not source_path:issues.append(dict(costume=costume,reason='original OGF missing'));continue
    source=source_path.read_bytes();root=dict(g.chunks(source));names=read_skeleton(root[13]);
    if names!=donor_names:issues.append(dict(costume=costume,reason='skeleton order differs from native donor'));continue
    g.SOURCE=source_path.parent;parts,_=g.load(name)
    original=list(g.chunks(root[9]));raw_children=dict(original)
    projectors={};selection={}
    forearm_fit=costume in ('exo_outfit','dolg_exo_outfit','svoboda_exo_outfit')
    for side in ('l','r'):
        bone_id=names.index('bip01_'+side+('_forearm' if forearm_fit else '_upperarm'))
        available=[]
        for part in parts:
            found=shoulder_data(part,raw_children[part['child']],bone_id)
            if found:available.append((len(found[0]),part['child'],found))
        if not available:break
        # The broadest actual upper-arm surface is a safer shell than sparse
        # straps. Ties resolve to the lower original child ID.
        _,child,data=sorted(available,key=lambda z:(-z[0],z[1]))[0]
        # Donor side IDs only select the side; interpolated vertices retain
        # the target forearm bone weights for these armored costumes.
        projectors[names.index('bip01_'+side+'_upperarm')]=data;selection[side]=child
    if len(projectors)!=2:
        issues.append(dict(costume=costume,reason='both upper-arm shells not found'));continue
    records=[];index=[];points=[];normals=[];distances=[];cache={}
    for face in donor_faces:
        vs=[donor_vertices[i] for i in face];bone=vs[0]['bones'][0]
        assert all(v['bones']==(bone,) for v in vs)
        xyz=np.asarray([v['xyz'] for v in vs]);uv=np.asarray([v['uv'] for v in vs])
        for bary in subdiv(np.eye(3),2):
            for bc in bary:
                q=bc@xyz;tc=bc@uv
                q=donor_centers[bone]+(q-donor_centers[bone])*fit_scale.get(costume,1.)
                key=(bone,*np.round(q,8),*np.round(tc,8))
                if key not in cache:
                    p,B,pairs,move=project(q,projectors[bone]);cache[key]=len(records)
                    records.append(struct.pack('<4H12f3f2f',*(b for b,z in pairs),*p,*B.flatten(),*(z for b,z in pairs[:3]),*tc))
                    points.append(p);normals.append(B[0]);distances.append(move)
                index.append(cache[key])
    pos=np.asarray(points);normals=np.asarray(normals);indices=np.asarray(index).reshape(-1,3)
    triangles=pos[indices];geom=np.cross(triangles[:,1]-triangles[:,0],triangles[:,2]-triangles[:,0])
    areas=np.linalg.norm(geom,axis=1)
    placement='donor_projection';strip_widths=None
    if np.any(areas<1e-9) or forearm_fit:
        fallback=surface_band(projectors)
        if fallback is None:
            issues.append(dict(costume=costume,reason='target shoulder surface could not form a strip'));continue
        records,index,points,normals,areas,strip_widths=fallback
        pos=np.asarray(points);normals=np.asarray(normals);indices=np.asarray(index).reshape(-1,3)
        triangles=pos[indices]
        geom=np.cross(triangles[:,1]-triangles[:,0],triangles[:,2]-triangles[:,0])
        placement='clipped_original_forearm_surface' if forearm_fit else 'clipped_original_arm_surface'
    alignment=np.einsum('ij,ij->i',geom,normals[indices].mean(1))
    flipped=alignment<0
    indices[flipped]=indices[flipped][:,[0,2,1]]
    low=pos.min(0);high=pos.max(0);center=(low+high)/2
    header=donor_band[1][:4]+struct.pack('<10f',*low,*high,*center,float(np.linalg.norm(pos-center,axis=1).max()))
    vb=struct.pack('<II',4,len(records))+b''.join(records)
    flat=indices.ravel();ib=struct.pack('<I',len(flat))+struct.pack('<'+'H'*len(flat),*flat)
    swi=donor_band[6][:16]+struct.pack('<IIHH',1,0,len(flat)//3,len(records))
    band=chunks([(1,header),(2,donor_band[2]),(3,vb),(4,ib),(6,swi)])
    child_id=max(raw_children)+1
    candidate=chunks([(k,chunks(original+[(child_id,band)]) if k==9 else b) for k,b in g.chunks(source)])
    if name=='stalker_neutral_1_face_1':
        candidate=approved_starter
    parsed=dict(g.chunks(candidate));assert list(g.chunks(parsed[9]))[:-1]==original
    assert all(parsed[k]==b for k,b in g.chunks(source) if k!=9)
    filename='meshes\\actors\\stalker_mp\\'+name+'.ogf'
    out=HERE/'LocalMods/meshes/actors/stalker_mp'/f'{name}.ogf';out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(candidate)
    entries.append((filename,candidate))
    g.SOURCE=out.parent;check,_=g.load(name)
    assert len(check)==len(parts)+1 and check[-1]['shader']=='models\\selflightl'
    assert check[-1]['highest_detail_triangles']==len(flat)//3
    item=dict(costume=costume,model=name,original_sha256=sha(source),candidate_sha256=sha(candidate),
        original_children=len(parts),new_child=child_id,arm_surface_children=selection,
        band_vertices=len(records),band_triangles=len(flat)//3,placement=placement,strip_widths=strip_widths,
        fit_scale=fit_scale.get(costume,1.),projection_mm_p50=round(float(np.median(distances)*1000),2),
        projection_mm_max=round(float(max(distances)*1000),2),min_triangle_area_mm2=round(float(min(areas)*1e6),3),
        unchanged_original_chunks=True,status='static_candidate_only')
    models.append(item)
    print(costume,name,'max mm',item['projection_mm_max'],flush=True)

native_path=g.INPUT/'patches_xrmpe_meshes_actors.db0/textures/act/act_mp_tdm_green_1.dds'
native=native_path.read_bytes();texture_name='textures\\act\\act_mp_tdm_green_1.dds'
dest=HERE/'LocalMods/textures/act/act_mp_tdm_green_1.dds';dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(native)
entries.append((texture_name,native))
db=HERE/'LocalMods/zonelink_emissive_30.db';db.write_bytes(make_db(entries))
archive=Archive(db)
assert len({name for name,_ in entries})==len(entries)
for name,data in entries:assert archive.read(name)==data
result=dict(costumes_requested=30,purchased_candidates=sum(x['costume'] in {c for c,_ in costumes[:30]} for x in models),
    starter_candidates=sum(x['model'] in starter for x in models),issues=issues,models=models,
    db_sha256=sha(db.read_bytes()),db_bytes=db.stat().st_size,archive_entries=len(entries),
    native_dds_sha256=sha(native),material='models\\selflightl',
    original_skeleton_animation_LOD_unchanged=True,source_rights_public_distribution_confirmed=False,
    game_tested_models=['stalker_neutral_1_face_1 only, in earlier single-model prototype'],
    other_models_runtime_verified=False)
(HERE/'VALIDATION.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf8')
print('DONE',result['purchased_candidates'],'of 30 +',result['starter_candidates'],'starters;',len(issues),'issues; DB',result['db_sha256'])
