"""Regenerate the exact starter OGF previously accepted in the game."""
from pathlib import Path
import sys, struct, json, hashlib, re, os
import numpy as np
sys.dont_write_bytecode = True
OUT = Path(os.environ['ZL_BUILD_HERE'])
import inspect_geometry as g

def sha(b): return hashlib.sha256(b).hexdigest().upper()
def pack(items): return b''.join(struct.pack('<II', k, len(b))+b for k,b in items)
def save(name, data):
    p=OUT/name; p.parent.mkdir(parents=True, exist_ok=True)
    if p.exists(): assert p.read_bytes()==data, 'Refusing to overwrite: '+str(p)
    else: p.write_bytes(data)
    return p

target='stalker_neutral_1_face_1'
donor='stalker_tdm_team_green_1'
source=(g.SOURCE/(target+'.ogf')).read_bytes()
donor_bytes=(g.SOURCE/(donor+'.ogf')).read_bytes()
stock=dict(g.chunks(source)); native=dict(g.chunks(donor_bytes))
original_children=list(g.chunks(stock[9])); donor_children=dict(g.chunks(native[9]))
body=dict(g.chunks(original_children[1][1])); band=dict(g.chunks(donor_children[2]))
assert body[2].split(b'\0')[:2]==[b'act\\act_stalker_neutral_1',b'models\\model_pn']
assert band[2].split(b'\0')[:2]==[b'act\\act_mp_tdm_green_1',b'models\\selflightl']
parts,_=g.load(target); donor_parts,_=g.load(donor)

def skeleton(b):
    count=struct.unpack_from('<I',b)[0]; p=4; result=[]
    for i in range(count):
        e=b.index(0,p); name=b[p:e].decode(); p=e+1
        e=b.index(0,p); parent=b[p:e].decode(); p=e+61; result.append((name,parent))
    assert p==len(b)
    return result

bones=skeleton(stock[13]); assert bones==skeleton(native[13])
bone_ids={name:i for i,(name,parent) in enumerate(bones)}
vertices,info=g.parse_vertices(body[3]); assert info['stride'] in (60,64,70,76)
P=np.array([v['xyz'] for v in vertices])
basis=np.array([struct.unpack_from('<9f',body[3],8+i*info['stride']+(0 if info['links']==1 else 2*info['links'])+12) for i in range(len(vertices))]).reshape(-1,3,3)
faces=np.array(parts[1]['faces'])
band_vertices,_=g.parse_vertices(band[3]); band_faces=donor_parts[2]['faces']
projectors={}
for side in ['l','r']:
    upper=bone_ids['bip01_'+side+'_upperarm']
    fs=np.array([f for f in faces if all(sum(z for b,z in zip(vertices[i]['bones'],vertices[i]['weights']) if b==upper)>.75 for i in f)])
    assert len(fs)>10
    T=P[fs]; a,b,c=T[:,0],T[:,1],T[:,2]; e=b-a; f=c-a; n=np.cross(e,f)
    n/=np.maximum(np.linalg.norm(n,axis=1)[:,None],1e-15)
    d00=(e*e).sum(1);d01=(e*f).sum(1);d11=(f*f).sum(1);den=d00*d11-d01*d01
    projectors[upper]=(fs,a,b,c,e,f,n,d00,d01,d11,den)

def pn(P,N,bc):
    def edge(i,j):return (2*P[i]+P[j]-np.dot(P[j]-P[i],N[i])*N[i])/3
    a,b,c,d,e,f=edge(0,1),edge(1,0),edge(1,2),edge(2,1),edge(2,0),edge(0,2)
    E=(a+b+c+d+e+f)/6;center=E+(E-P.mean(0))/2;u,v,w=bc
    return P[0]*u**3+P[1]*v**3+P[2]*w**3+a*3*u*u*v+b*3*u*v*v+f*3*u*u*w+c*3*v*v*w+e*3*u*w*w+d*3*v*w*w+center*6*u*v*w

def project(q,bone):
    fs,a,b,c,e,f,n,d00,d01,d11,den=projectors[bone]
    plane=q-((q-a)*n).sum(1)[:,None]*n;d=plane-a;d20=(d*e).sum(1);d21=(d*f).sum(1)
    u=(d11*d20-d01*d21)/np.maximum(den,1e-16);v=(d00*d21-d01*d20)/np.maximum(den,1e-16)
    inside=(u>=0)&(v>=0)&(u+v<=1)&(den>1e-12)
    points=plane.copy();dist=((q-points)**2).sum(1);dist[~inside]=np.inf
    for x,y in [(a,b),(b,c),(c,a)]:
        edge=y-x;t=np.clip(((q-x)*edge).sum(1)/np.maximum((edge*edge).sum(1),1e-16),0,1)
        p=x+t[:,None]*edge;ds=((q-p)**2).sum(1);ix=ds<dist;dist[ix]=ds[ix];points[ix]=p[ix]
    i=int(np.argmin(dist));d=points[i]-a[i]
    u=(d11[i]*np.dot(d,e[i])-d01[i]*np.dot(d,f[i]))/den[i]
    v=(d00[i]*np.dot(d,f[i])-d01[i]*np.dot(d,e[i]))/den[i]
    bc=np.maximum([1-u-v,u,v],0);bc/=sum(bc)
    ids=fs[i];B=np.einsum('i,ijk->jk',bc,basis[ids]);B/=np.maximum(np.linalg.norm(B,axis=1)[:,None],1e-10)
    # Bake the installed PN formula into the band; selflightl itself does not tessellate.
    pos=pn(P[ids],basis[ids,0],bc)+B[0]*.003
    weights={}
    for j,index in enumerate(ids):
        for bone,weight in zip(vertices[index]['bones'],vertices[index]['weights']): weights[bone]=weights.get(bone,0)+bc[j]*weight
    pairs=sorted((bone,weight) for bone,weight in weights.items() if weight>1e-7)
    assert len(pairs)<=4
    total=sum(z for b,z in pairs); pairs=[(b,z/total) for b,z in pairs]
    pairs += [(pairs[0][0],0.)]*(4-len(pairs))
    return pos,B,pairs,float(np.sqrt(dist[i]))

def subdiv(tri,level):
    if not level:return [tri]
    a,b,c=tri;ab=(a+b)/2;bc=(b+c)/2;ca=(c+a)/2
    return sum((subdiv(t,level-1) for t in [np.array([a,ab,ca]),np.array([ab,b,bc]),np.array([ca,bc,c]),np.array([ab,bc,ca])]),[])

records=[];idx=[];positions=[];uvs=[];moves=[];cache={}
for face in band_faces:
    vs=[band_vertices[i] for i in face];bone=vs[0]['bones'][0]
    assert all(v['bones']==(bone,) for v in vs)
    xyz=np.array([v['xyz'] for v in vs]);uv=np.array([v['uv'] for v in vs])
    for bary in subdiv(np.eye(3),2):
        for bc in bary:
            q=bc@xyz;tc=bc@uv;key=(bone,*np.round(q,8),*np.round(tc,8))
            if key not in cache:
                p,B,pairs,move=project(q,bone);index=len(records);cache[key]=index
                # Interpolate the target cloth skinning, retaining its 4W layout.
                records.append(struct.pack('<4H12f3f2f',*(b for b,z in pairs),*p,*B.flatten(),*(z for b,z in pairs[:3]),*tc))
                positions.append(p);uvs.append(tc);moves.append(move)
            idx.append(cache[key])
assert len(records)<65536 and len(idx)%3==0
xyz=np.array(positions);low=xyz.min(0);high=xyz.max(0);center=(low+high)/2
header=band[1][:4]+struct.pack('<10f',*low,*high,*center,float(np.linalg.norm(xyz-center,axis=1).max()))
vb=struct.pack('<II',4,len(records))+b''.join(records)
ib=struct.pack('<I',len(idx))+struct.pack('<'+'H'*len(idx),*idx)
swi=band[6][:16]+struct.pack('<IIHH',1,0,len(idx)//3,len(records))
new_band=pack([(1,header),(2,band[2]),(3,vb),(4,ib),(6,swi)])
child_id=max(k for k,b in original_children)+1
candidate=pack([(k,pack(original_children+[(child_id,new_band)]) if k==9 else b) for k,b in g.chunks(source)])
new=dict(g.chunks(candidate));assert list(g.chunks(new[9]))[:-1]==original_children
for k,b in g.chunks(source):
    if k!=9:assert new[k]==b
assert len(g.parse_vertices(vb)[0])==len(records)
mesh_name='meshes\\actors\\stalker_mp\\'+target+'.ogf'
assert sha(candidate)=='9DBC9F9DEF31CDDFE5C732C594AD1CD01F22F86C70698C51DF5ECEC50DD90F63'
