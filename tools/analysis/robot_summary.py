import csv,collections,math,sys
rows=[r for r in csv.DictReader(open(sys.argv[1],encoding='utf-8')) if r['kind']=='robot' and r['state']!='none']
T=max(float(r['t']) for r in rows); print(f'duration {T/60:.1f} min, samples {len(rows)}')
by=collections.defaultdict(list)
for r in rows: by[r['dock']].append(r)
tot=collections.Counter(); totn=0
for d,rs in sorted(by.items(), key=lambda kv:int(kv[0])):
    rs.sort(key=lambda r:float(r['t']))
    st=collections.Counter(r['state'] for r in rs); n=len(rs); tot.update(st); totn+=n
    # dust gathered: positive increments of carriedKg
    gained=0; dist=collections.Counter()
    for a,b in zip(rs,rs[1:]):
        dk=float(b['carriedKg'])-float(a['carriedKg'])
        if dk>0: gained+=dk
        dist[a['state']]+=math.dist((float(a['x']),float(a['z'])),(float(b['x']),float(b['z'])))
    hex0,hex1=int(rs[0]['hexesDone']),int(rs[-1]['hexesDone'])
    print(f"dock {d}: {n}s  " + ' '.join(f"{k} {v/n:.0%}" for k,v in st.most_common()) + f" | dust +{gained:.0f} kg ({gained/(n/60):.1f} kg/min) | sectors done {hex0}->{hex1} | recov {rs[-1]['recoveries']} | travel {dist['Travel']:.0f} m, cleaning {dist['Cleaning']:.0f} m")
print('ALL: ' + ' '.join(f"{k} {v/totn:.0%}" for k,v in tot.most_common()))
