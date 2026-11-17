import struct,gzip,hashlib,collections,math,sys
b=open(sys.argv[1],'rb').read(); hl=b[8]; d=gzip.decompress(b[9+hl:]); assert hashlib.sha256(d).hexdigest()==b[9:9+hl].decode()
def runs():
    hits=[];i=d.find(b'\x05local')
    while i>=0: hits.append(i); i=d.find(b'\x05local',i+1)
    return hits
hits=runs()
# canister list: header nextId,count then 67-byte recs
def find_list(rec, test):
    for h in hits:
        o=h-12; nid,cnt=struct.unpack_from('<ii',d,o)
        if 5<cnt<100000 and nid>=cnt and test(o,cnt): return o,cnt
can_o,can_n=find_list(67, lambda o,c: all(d[o+8+k*67+4:o+8+k*67+10]==b'\x05local' for k in range(c)))
cans={}
for k in range(can_n):
    p=can_o+8+k*67; cid=struct.unpack_from('<i',d,p)[0]; pos=struct.unpack_from('<3f',d,p+26); cans[cid]=pos
def belt_ok(o,c):
    p=o+8
    for k in range(c):
        if d[p+4:p+10]!=b'\x05local': return False
        p+=58; w=d[p]; p+=1+24*w+1
    return True
bo,bn=find_list(60,belt_ok)
belts={}; p=bo+8
for k in range(bn):
    bid=struct.unpack_from('<i',d,p)[0]; v=struct.unpack_from('<12f',d,p+10); w=d[p+58]
    pts=[v[0:3]]+[struct.unpack_from('<3f',d,p+59+24*j) for j in range(w)]+[v[6:9]]
    belts[bid]=pts; p+=58+1+24*w+1
# kind map
ids=set(cans)
km=None
for o in range(0,len(d)-9):
    c=struct.unpack_from('<i',d,o)[0]
    if 1<=c<=len(ids) and struct.unpack_from('<i',d,o+4)[0] in ids:
        if all(struct.unpack_from('<i',d,o+4+k*5)[0] in ids and 1<=d[o+8+k*5]<=2 for k in range(c)):
            km={struct.unpack_from('<i',d,o+4+k*5)[0]:d[o+8+k*5] for k in range(c)}; break
kind=lambda c: km.get(c,0)
# structures (42-byte) between canisters and belts
srecs=[]
for h in hits:
    if can_o+8+can_n*67 <= h-8 < bo:
        k_,sid=struct.unpack_from('<ii',d,h-8); pos=struct.unpack_from('<3f',d,h+6); srecs.append((k_,sid,pos))
def segd(q,a,b_):
    ab=[b_[i]-a[i] for i in range(3)]; aq=[q[i]-a[i] for i in range(3)]; L2=sum(x*x for x in ab) or 1e-9
    t=max(0,min(1,sum(ab[i]*aq[i] for i in range(3))/L2)); c=[a[i]+t*ab[i] for i in range(3)]; return math.dist(q,c),t,math.sqrt(L2)
def on_belt(q):
    best=None
    for bid,pts in belts.items():
        acc=0
        for a,b_ in zip(pts,pts[1:]):
            dd,t,L=segd(q,a,b_)
            if best is None or dd<best[0]: best=(dd,bid,acc+t*L)
            acc+=L
    return best
NAMES={0:'dust',1:'sand',2:'stone'}
rider=collections.defaultdict(list); loose=0
for cid,q in cans.items():
    dd,bid,s=on_belt(q)
    if dd<1.2: rider[bid].append((s,kind(cid),cid))
    else: loose+=1
blen={bid:sum(math.dist(a,b_) for a,b_ in zip(pts,pts[1:])) for bid,pts in belts.items()}
def summary(bid):
    r=sorted(rider.get(bid,[]))
    if not r: return f'belt {bid:3} ({blen[bid]:5.1f} m): empty'
    c=collections.Counter(NAMES[k] for _,k,_ in r)
    # backed-up: capsules packed ~0.75 m at the downstream end
    packed=0
    for i in range(len(r)-1,0,-1):
        if r[i][0]-r[i-1][0]<0.95: packed+=1
        else: break
    full=len(r)/max(1,blen[bid]/0.75)
    return f'belt {bid:3} ({blen[bid]:5.1f} m): {len(r):3} capsules {dict(c)}; {full:4.0%} full; tail-packed {packed+1 if packed else 0}'
print(f'canisters {can_n} ({loose} not on belts), belts {bn}, structures {len(srecs)}, kind map {len(km)}')
# splitter candidates: structures with 1 belt end and 2 belt starts nearby
for k_,sid,pos in srecs:
    ins=[bid for bid,pts in belts.items() if math.dist(pts[-1],pos)<2.5]
    outs=[bid for bid,pts in belts.items() if math.dist(pts[0],pos)<2.5]
    if (len(ins),len(outs)) in ((1,2),(2,1)):
        role='SPLITTER?' if len(outs)==2 else 'MERGER?'
        print(f'\n{role} structure {sid} kind {k_} at ({pos[0]:.0f},{pos[2]:.0f})')
        for bid in ins: print('  in  ', summary(bid))
        for bid in outs: print('  out ', summary(bid))

print('\n==== graph trace')
def node_at(pt):
    best=min(srecs,key=lambda r: math.dist(r[2],pt)); return best if math.dist(best[2],pt)<3 else None
def describe(sid,pos):
    ins=[bid for bid,pts in belts.items() if math.dist(pts[-1],pos)<2.5]
    outs=[bid for bid,pts in belts.items() if math.dist(pts[0],pos)<2.5]
    return ins,outs
def trace(bid,depth=0,seen=None):
    seen=seen or set()
    if bid in seen or depth>6: return
    seen.add(bid)
    pts=belts[bid]; n=node_at(pts[-1])
    print('  '*depth+summary(bid))
    if n is None: print('  '*depth+'   -> ends at nothing within 3 m of a structure at', tuple(round(x) for x in pts[-1])); return
    ins,outs=describe(n[1],n[2])
    print('  '*depth+f'   -> structure {n[1]} (first-int {n[0]}) at ({n[2][0]:.0f},{n[2][2]:.0f}): belts in {ins}, belts out {outs}')
    for o in outs: trace(o,depth+1,seen)
for start in map(int, sys.argv[2:]): print(f'--- from belt {start}'); trace(start)
