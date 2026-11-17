import csv,collections,math,sys,statistics as S
rows=[r for r in csv.DictReader(open(sys.argv[1],encoding='utf-8')) if r['kind']=='robot' and r['state']!='none']
by=collections.defaultdict(list)
for r in rows: by[r['dock']].append(r)
clean_s=clean_zero=0; segs=[]; trav_speed=[]; clean_speed=[]; drops=[]
status=collections.Counter()
for d,rs in by.items():
    rs.sort(key=lambda r:float(r['t']))
    for a,b in zip(rs,rs[1:]):
        dt=float(b['t'])-float(a['t']);
        if dt<=0 or dt>2: continue
        dk=float(b['carriedKg'])-float(a['carriedKg'])
        if dk<-5: drops.append((d,a['t'],float(a['carriedKg']),float(b['carriedKg']),a['state'],b['state'],a['status']))
        mv=math.dist((float(a['x']),float(a['z'])),(float(b['x']),float(b['z'])))/dt
        if a['state']=='Cleaning':
            clean_s+=1; clean_speed.append(mv)
            if dk<=0.001: clean_zero+=1
        if a['state']=='Travel': trav_speed.append(mv)
    # sector segments
    cur=None
    for r in rs:
        c=r['cell']
        if cur is None or c!=cur['cell']:
            if cur: segs.append(cur)
            cur=dict(dock=d,cell=c,t0=float(r['t']),st=collections.Counter(),kg=0,last=float(r['carriedKg']),ds=int(r['hexDirtyStart']),dn=int(r['hexDirtyNow']),rows=r['rows'],lanes=r['lanes'])
        cur['st'][r['state']]+=1; k=float(r['carriedKg'])
        if k>cur['last']: cur['kg']+=k-cur['last']
        cur['last']=k; cur['dn']=int(r['hexDirtyNow']); cur['t1']=float(r['t'])
    if cur: segs.append(cur)
    for r in rs: status[r['status'].split(' Sector')[0][:40]]+=1
print(f'cleaning seconds with NO dust picked up: {clean_zero}/{clean_s} = {clean_zero/clean_s:.0%}')
print(f'speed while cleaning: median {S.median(clean_speed):.2f} m/s (nominal 2.0); travel: median {S.median(trav_speed):.2f} m/s (nominal 3.0)')
full=[s for s in segs if s['t0']>5 and s['t1']<560]
print(f'complete sectors: {len(full)}')
dur=[s['t1']-s['t0'] for s in full]; print(f'  seconds per sector: median {S.median(dur):.0f}, mean {S.mean(dur):.0f}')
for k in ('Cleaning','Travel','Aligning','Checking'):
    print(f'  {k}: median {S.median([s["st"][k] for s in full]):.0f} s per sector')
print(f'  dust per sector: median {S.median([s["kg"] for s in full]):.1f} kg; dirty cells start median {S.median([s["ds"] for s in full])}, left at end median {S.median([s["dn"] for s in full])}')
print('  rows/lanes per sector:', collections.Counter((s['rows'],s['lanes']) for s in full).most_common(5))
zero=[s for s in full if s['kg']<2]; print(f'  sectors with <2 kg dust collected: {len(zero)}/{len(full)}; their seconds: {sum(s["t1"]-s["t0"] for s in zero):.0f}')
print('drops in carried kg (unload/launch):', len(drops), drops[:5])
print('status texts:', status.most_common(12))
