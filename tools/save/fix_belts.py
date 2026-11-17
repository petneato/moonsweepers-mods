"""Remove specific canisters from a Moon Sweepers session.moon.

usage: python -I fix_belts.py <in session.moon> <out session.moon> <id> [<id> ...]
"""
import gzip, hashlib, struct, sys

REC = 67  # int32 id, str owner(5 'local'), 2 doubles, 3f pos, 3f up, bool, 4f rot


def load(path):
    b = open(path, 'rb').read()
    assert b[:4] == b'1PCM', b[:4]
    n = struct.unpack_from('<I', b, 4)[0]
    hl = b[8]
    h = b[9:9 + hl].decode()
    raw = gzip.decompress(b[9 + hl:])
    assert len(raw) == n and hashlib.sha256(raw).hexdigest() == h, 'header/hash mismatch'
    return raw


def save(path, raw):
    h = hashlib.sha256(raw).hexdigest().encode()
    out = b'1PCM' + struct.pack('<I', len(raw)) + bytes([len(h)]) + h + gzip.compress(raw, mtime=0)
    open(path, 'wb').write(out)


def find_canister_list(d):
    """Find (header_offset, count) of the canister list: int nextId, int count, count*67-byte records."""
    best = None
    i = d.find(b'\x05local')
    while i >= 0:
        o = i - 12  # header is 8 bytes, record id 4 bytes, then owner string
        if o >= 0:
            nid, cnt = struct.unpack_from('<ii', d, o)
            if 50 < cnt < 100000 and nid > cnt and o + 8 + cnt * REC <= len(d):
                ok = all(d[o + 8 + k * REC + 4:o + 8 + k * REC + 10] == b'\x05local' for k in range(cnt))
                if ok and (best is None or cnt > best[1]):
                    best = (o, cnt)
        i = d.find(b'\x05local', i + 1)
    assert best, 'canister list not found'
    return best


def find_kind_map(d, ids):
    """Find (offset, count) of map: int count, count * (int32 canisterId, byte kind)."""
    for o in range(0, len(d) - 9):
        cnt = struct.unpack_from('<i', d, o)[0]
        if not (1 <= cnt <= len(ids)):
            continue
        if struct.unpack_from('<i', d, o + 4)[0] not in ids:
            continue
        good = True
        for k in range(cnt):
            p = o + 4 + k * 5
            if p + 5 > len(d) or struct.unpack_from('<i', d, p)[0] not in ids or not (1 <= d[p + 4] <= 8):
                good = False
                break
        if good:
            return o, cnt
    raise AssertionError('kind map not found')


def main():
    src, dst = sys.argv[1], sys.argv[2]
    targets = [int(x) for x in sys.argv[3:]]
    d = bytearray(load(src))

    lo, lcnt = find_canister_list(d)
    ids = {struct.unpack_from('<i', d, lo + 8 + k * REC)[0]: k for k in range(lcnt)}
    mo, mcnt = find_kind_map(d, set(ids))
    kinds = {struct.unpack_from('<i', d, mo + 4 + k * 5)[0]: (k, d[mo + 8 + k * 5]) for k in range(mcnt)}
    print(f'canister list @0x{lo:x} count {lcnt}; kind map @0x{mo:x} count {mcnt}')
    for t in targets:
        assert t in ids, f'canister {t} not in save'
        print(f'  canister {t}: kind {kinds.get(t, (None, 0))[1]}')

    # Edit later section first so earlier offsets stay valid.
    assert mo > lo
    rm_map = sorted((kinds[t][0] for t in targets if t in kinds), reverse=True)
    for k in rm_map:
        p = mo + 4 + k * 5
        del d[p:p + 5]
    struct.pack_into('<i', d, mo, mcnt - len(rm_map))

    rm_list = sorted((ids[t] for t in targets), reverse=True)
    for k in rm_list:
        p = lo + 8 + k * REC
        del d[p:p + REC]
    struct.pack_into('<i', d, lo + 4, lcnt - len(rm_list))

    raw = bytes(d)
    save(dst, raw)

    # verify round trip
    r = load(dst)
    lo2, lcnt2 = find_canister_list(r)
    ids2 = {struct.unpack_from('<i', r, lo2 + 8 + k * REC)[0] for k in range(lcnt2)}
    mo2, mcnt2 = find_kind_map(r, ids2)
    assert lcnt2 == lcnt - len(targets) and not (set(targets) & ids2)
    assert mcnt2 == mcnt - len(rm_map)
    print(f'wrote {dst}: canisters {lcnt} -> {lcnt2}, kind map {mcnt} -> {mcnt2}, raw {len(raw)} bytes')


main()
