"""gfx_edit.py <in.gfx> <out.gfx> OP [OP ...]   -- stage surgery on a Scaleform GFX/CFX movie

  scale:<clipName>:<sx>:<sy>
        rewrite the MATRIX scale of the named PlaceObject2 (translation/rotation kept)
  import:<url>:<charId>:<symbol>
        append an ImportAssets2 tag (url e.g. Grid.swf, symbol e.g. Grid) right after the movie's
        last top-level ImportAssets2 -- i.e. before the DefineSprite that will use the character
  add:<spriteId>:<clipName>:<charId>:<depth>:<xPx>:<yPx>:<sx>:<sy>
        append a PlaceObject2 (HasCharacter|HasMatrix|HasName) inside DefineSprite <spriteId>, before its
        first ShowFrame. Position in PIXELS (converted to twips), scale as factors. Depth must be unused.

Only the tags that change (and the DefineSprite that contains them) are re-serialised; everything else is
copied byte for byte and the CFX/GFX header length (payload + 8) is rewritten. Prints the placements seen
before and after, the "after" read back from the written file. Supersedes gfx_scale.py.
"""
import struct, sys, zlib


class BitReader:
    def __init__(self, data, pos=0):
        self.d, self.p, self.bit = data, pos, 0

    def read(self, n, signed=False):
        v = 0
        for _ in range(n):
            v = (v << 1) | ((self.d[self.p] >> (7 - self.bit)) & 1)
            self.bit += 1
            if self.bit == 8:
                self.bit, self.p = 0, self.p + 1
        if signed and n and (v >> (n - 1)):
            v -= 1 << n
        return v

    def align(self):
        if self.bit:
            self.bit, self.p = 0, self.p + 1


class BitWriter:
    def __init__(self):
        self.bits = []

    def write(self, v, n):
        for i in range(n - 1, -1, -1):
            self.bits.append((v >> i) & 1)

    def bytes(self):
        while len(self.bits) % 8:
            self.bits.append(0)
        return bytes(int("".join(map(str, self.bits[i:i + 8])), 2) for i in range(0, len(self.bits), 8))


def sbits(values):
    """Minimum signed bit width for all values."""
    n = 1
    for v in values:
        while not (-(1 << (n - 1)) <= v < (1 << (n - 1))):
            n += 1
    return n


def read_matrix(body, p):
    b = BitReader(body, p)
    m = {"scale": None, "rot": None}
    if b.read(1):
        n = b.read(5)
        m["scale"] = (b.read(n, True), b.read(n, True))
    if b.read(1):
        n = b.read(5)
        m["rot"] = (b.read(n, True), b.read(n, True))
    n = b.read(5)
    m["t"] = (b.read(n, True), b.read(n, True))
    b.align()
    return m, b.p


def write_matrix(m):
    w = BitWriter()
    if m["scale"] is not None:
        w.write(1, 1)
        n = sbits(m["scale"])
        w.write(n, 5)
        w.write(m["scale"][0] & ((1 << n) - 1), n)
        w.write(m["scale"][1] & ((1 << n) - 1), n)
    else:
        w.write(0, 1)
    if m["rot"] is not None:
        w.write(1, 1)
        n = sbits(m["rot"])
        w.write(n, 5)
        w.write(m["rot"][0] & ((1 << n) - 1), n)
        w.write(m["rot"][1] & ((1 << n) - 1), n)
    else:
        w.write(0, 1)
    n = sbits(m["t"]) if m["t"] != (0, 0) else 0
    w.write(n, 5)
    if n:
        w.write(m["t"][0] & ((1 << n) - 1), n)
        w.write(m["t"][1] & ((1 << n) - 1), n)
    return w.bytes()


def parse_tags(buf):
    tags, p = [], 0
    while p + 2 <= len(buf):
        start = p
        h = struct.unpack_from("<H", buf, p)[0]
        code, ln = h >> 6, h & 0x3F
        p += 2
        if ln == 0x3F:
            ln = struct.unpack_from("<I", buf, p)[0]
            p += 4
        tags.append((code, buf[p:p + ln], buf[start:p + ln]))
        p += ln
        if code == 0:
            break
    return tags


def emit_tag(code, body):
    # always the long form for anything we re-emit: unambiguous and legal for every length
    return struct.pack("<H", (code << 6) | 0x3F) + struct.pack("<I", len(body)) + body


def place_name(body):
    fl = body[0]
    q = 3
    if fl & 2:
        q += 2
    if fl & 4:
        _, q = read_matrix(body, q)
    if fl & 8:
        b = BitReader(body, q)
        has_add, has_mul, n = b.read(1), b.read(1), b.read(5)
        for _ in range((4 if has_mul else 0) + (4 if has_add else 0)):
            b.read(n, True)
        b.align()
        q = b.p
    if fl & 16:
        q += 2
    if fl & 32:
        e = body.index(b"\0", q)
        return body[q:e].decode("latin1")
    return None


def rescale_place(body, sx, sy):
    fl = body[0]
    if not (fl & 4):
        raise SystemExit("placement has no MATRIX; refusing")
    q = 3 + (2 if fl & 2 else 0)
    m, end = read_matrix(body, q)
    m["scale"] = (int(round(sx * 65536)), int(round(sy * 65536)))
    return body[:q] + write_matrix(m) + body[end:], m


def describe(tags, depth, out):
    for code, body, _raw in tags:
        if code == 71:
            e = body.index(b"\0")
            out.append("%simport %s" % ("  " * depth, body[:e].decode("latin1")))
        if code == 26:
            name = place_name(body)
            fl = body[0]
            q = 3 + (2 if fl & 2 else 0)
            m = read_matrix(body, q)[0] if fl & 4 else None
            if name:
                out.append("%s%-22s scale=%s t=%s" % ("  " * depth, name,
                           ("%.4f/%.4f" % (m["scale"][0] / 65536, m["scale"][1] / 65536)) if m and m["scale"] else "1/1",
                           m["t"] if m else "-"))
        if code == 39:
            describe(parse_tags(body[4:]), depth + 1, out)


def make_place(name, char_id, depth, x_px, y_px, sx, sy):
    m = {"scale": (int(round(sx * 65536)), int(round(sy * 65536))), "rot": None,
         "t": (int(round(x_px * 20)), int(round(y_px * 20)))}
    return bytes([0x26]) + struct.pack("<HH", depth, char_id) + write_matrix(m) + name.encode("latin1") + b"\0"


def make_import(url, char_id, symbol):
    return url.encode("latin1") + b"\0" + bytes([1, 0]) + struct.pack("<H", 1) + struct.pack("<H", char_id) + symbol.encode("latin1") + b"\0"


def apply(tags, ops, hits, depth=0, sprite_id=None):
    """Untouched tags keep their original bytes (short or long header); only modified tags and the sprites
    that contain them are re-emitted."""
    new = []
    adds = [a for a in ops["add"] if a["sprite"] == sprite_id] if sprite_id is not None else []
    added = False
    last_import = max((i for i, (c, _, _) in enumerate(tags) if c == 71), default=None)
    for i, (code, body, raw) in enumerate(tags):
        if code == 1 and adds and not added:  # ShowFrame: place our clips before the first one
            for a in adds:
                new.append(emit_tag(26, make_place(a["name"], a["char"], a["depth"], a["x"], a["y"], a["sx"], a["sy"])))
                hits.append("add:" + a["name"])
            added = True
        if code == 26 and place_name(body) in ops["scale"]:
            sx, sy = ops["scale"][place_name(body)]
            body, m = rescale_place(body, sx, sy)
            hits.append(place_name(body))
            new.append(emit_tag(code, body))
        elif code == 39:
            sid = struct.unpack_from("<H", body, 0)[0]
            n_before = len(hits)
            inner = apply(parse_tags(body[4:]), ops, hits, depth + 1, sid)
            new.append(emit_tag(code, body[:4] + inner) if len(hits) > n_before else raw)
        else:
            new.append(raw)
        if depth == 0 and i == last_import:
            for imp in ops["import"]:
                new.append(emit_tag(71, make_import(imp["url"], imp["char"], imp["symbol"])))
                hits.append("import:" + imp["url"])
    return b"".join(new)


def main():
    src, dst = sys.argv[1], sys.argv[2]
    ops = {"scale": {}, "import": [], "add": []}
    for op in sys.argv[3:]:
        f = op.split(":")
        if f[0] == "scale":
            ops["scale"][f[1]] = (float(f[2]), float(f[3]))
        elif f[0] == "import":
            ops["import"].append({"url": f[1], "char": int(f[2]), "symbol": f[3]})
        elif f[0] == "add":
            ops["add"].append({"sprite": int(f[1]), "name": f[2], "char": int(f[3]), "depth": int(f[4]),
                               "x": float(f[5]), "y": float(f[6]), "sx": float(f[7]), "sy": float(f[8])})
        else:
            raise SystemExit("unknown op " + op)
    d = open(src, "rb").read()
    hdr, ver = d[:3], d[3]
    payload = zlib.decompress(d[8:]) if hdr == b"CFX" else d[8:]
    nbits = payload[0] >> 3
    head_len = (5 + 4 * nbits + 7) // 8 + 4
    head, tags = payload[:head_len], parse_tags(payload[head_len:])
    before = []
    describe(tags, 0, before)
    print("BEFORE:\n" + "\n".join(before))
    hits = []
    body = apply(tags, ops, hits)
    wanted = set(ops["scale"]) | {"import:" + i["url"] for i in ops["import"]} | {"add:" + a["name"] for a in ops["add"]}
    missing = wanted - set(hits)
    if missing:
        raise SystemExit("op target(s) not applied: %s" % ", ".join(sorted(missing)))
    new_payload = head + body
    out = hdr + bytes([ver]) + struct.pack("<I", len(new_payload) + 8)
    out += zlib.compress(new_payload, 9) if hdr == b"CFX" else new_payload
    open(dst, "wb").write(out)
    # read back what we just wrote, from the file
    d2 = open(dst, "rb").read()
    p2 = zlib.decompress(d2[8:]) if hdr == b"CFX" else d2[8:]
    after = []
    describe(parse_tags(p2[head_len:]), 0, after)
    print("AFTER (re-read from %s, payload %d -> %d):\n%s" % (dst, len(payload), len(p2), "\n".join(after)))


if __name__ == "__main__":
    main()
