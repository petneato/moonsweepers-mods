"""Summarise BepInEx/PerfLog.csv written by the Perf Logger mod.

usage: python perf_summary.py PerfLog.csv

Prints per-minute fps / worst frame, flags stretches of ~30 fps with the frame cap, vsync, focus and menu state at
that time, and how fps correlates with world load (capsules, belt riders, robots, pending cleaning strokes).
"""
import csv
import statistics as S
import sys
from collections import defaultdict


def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def corr(xs, ys):
    pairs = [(x, y) for x, y in zip(xs, ys) if x is not None and y is not None]
    if len(pairs) < 10:
        return None
    a = [p[0] for p in pairs]
    b = [p[1] for p in pairs]
    ma, mb = S.mean(a), S.mean(b)
    va = sum((x - ma) ** 2 for x in a)
    vb = sum((y - mb) ** 2 for y in b)
    if va == 0 or vb == 0:
        return None
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / (va * vb) ** 0.5


def main(path):
    rows, settings = [], []
    with open(path, encoding='utf-8', errors='replace') as f:
        for r in csv.DictReader(f):
            if r.get('kind') == 'perf':
                rows.append(r)
            elif r.get('kind') == 'settings':
                settings.append((r['t'], r.get('fps')))
    if not rows:
        print('no perf rows')
        return
    for t, s in settings:
        print(f'settings @ {t}s: {s}')
    print()

    per = defaultdict(list)
    for r in rows:
        per[int(float(r['t']) // 60)].append(r)
    print(' min   fps(med)  fps(min)  worst-ms  frames>50ms  focused  menu  target  vsync  capsules  riders  robots  pending')
    for m in sorted(per):
        g = per[m]
        fps = [num(r['fps']) for r in g if num(r['fps']) is not None]
        worst = max(num(r['worstMs']) or 0 for r in g)
        o50 = sum(int(num(r['over50']) or 0) for r in g)
        foc = S.mean(num(r['focused']) or 0 for r in g)
        menu = S.mean(num(r['menuOpen']) or 0 for r in g)
        last = g[-1]
        print(f" {m:3d}   {S.median(fps):6.0f}  {min(fps):7.0f}  {worst:8.1f}  {o50:11d}   {foc:5.0%}  {menu:4.0%}  {last['targetFps']:>6}  {last['vSync']:>5}"
              f"  {last['capsules']:>8}  {last['ridersOnBelts']:>6}  {last['robots']:>6}  {last['pendingStrokes']:>7}")

    # Stretches at ~30 fps (28-32) lasting >= 10 s, with the state during them.
    print('\n~30 fps stretches (>= 10 s):')
    run = []
    found = False
    for r in rows + [None]:
        f = num(r['fps']) if r else None
        if r is not None and f is not None and 28 <= f <= 32:
            run.append(r)
            continue
        if len(run) >= 10:
            found = True
            foc = S.mean(num(x['focused']) or 0 for x in run)
            menu = S.mean(num(x['menuOpen']) or 0 for x in run)
            pages = sorted({x['menuPage'] for x in run if x['menuPage']})
            print(f"  {run[0]['t']}s - {run[-1]['t']}s ({len(run)} s): focused {foc:.0%}, menu open {menu:.0%} {pages}, "
                  f"targetFps {run[-1]['targetFps']}, vSync {run[-1]['vSync']}, timeScale {run[-1]['timeScale']}")
        run = []
    if not found:
        print('  none')

    print('\nCorrelation of fps with world load (-1 = more of it, lower fps):')
    fps = [num(r['fps']) for r in rows]
    for col in ('capsules', 'ridersOnBelts', 'conveyors', 'depots', 'robots', 'pendingStrokes', 'strokeCount', 'gc0', 'managedMB', 'unityAllocMB'):
        c = corr([num(r[col]) for r in rows], fps)
        print(f'  {col:15s} {"n/a" if c is None else f"{c:+.2f}"}')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'PerfLog.csv')
