# Print every PlaceObject2 (recursing into sprites) with its full MATRIX (scale/rotate/translate) and name.
import struct, sys, zlib

class Bits:
    def __init__(self, data, pos=0):
        self.d = data; self.p = pos; self.bit = 0
    def read(self, n, signed=False):
        v = 0
        for _ in range(n):
            byte = self.d[self.p]
            b = (byte >> (7 - self.bit)) & 1
            v = (v << 1) | b
            self.bit += 1
            if self.bit == 8:
                self.bit = 0; self.p += 1
        if signed and n and (v >> (n - 1)):
            v -= 1 << n
        return v
    def align(self):
        if self.bit:
            self.bit = 0; self.p += 1

def matrix(body, p):
    b = Bits(body, p)
    m = {"sx": 1.0, "sy": 1.0, "r0": 0.0, "r1": 0.0, "tx": 0, "ty": 0}
    if b.read(1):
        n = b.read(5); m["sx"] = b.read(n, True) / 65536.0; m["sy"] = b.read(n, True) / 65536.0
    if b.read(1):
        n = b.read(5); m["r0"] = b.read(n, True) / 65536.0; m["r1"] = b.read(n, True) / 65536.0
    n = b.read(5); m["tx"] = b.read(n, True); m["ty"] = b.read(n, True)
    b.align()
    return m, b.p

def walk(buf, depth, out, sprite=""):
    p = 0
    while p + 2 <= len(buf):
        h = struct.unpack_from("<H", buf, p)[0]; code, ln = h >> 6, h & 0x3F; p += 2
        if ln == 0x3F: ln = struct.unpack_from("<I", buf, p)[0]; p += 4
        body = buf[p:p+ln]; p += ln
        if code == 26:
            fl = body[0]; q = 1; depth_ = struct.unpack_from("<H", body, q)[0]; q += 2
            cid = None
            if fl & 2: cid = struct.unpack_from("<H", body, q)[0]; q += 2
            m = None
            if fl & 4: m, q = matrix(body, q)
            if fl & 8:  # CXFORM: skip by parsing bits
                b = Bits(body, q); has_add = b.read(1); has_mul = b.read(1); n = b.read(5)
                for _ in range((4 if has_mul else 0) + (4 if has_add else 0)): b.read(n, True)
                b.align(); q = b.p
            if fl & 16: q += 2  # ratio
            name = None
            if fl & 32:
                e = body.index(b"\0", q); name = body[q:e].decode("latin1"); q = e + 1
            out.append("%s%-22s char=%-4s depth=%-3d %s" % ("  " * depth, sprite, cid, depth_, ("name=%-22s" % name) if name else " " * 27) +
                       (" sx=%.4f sy=%.4f r=%.3f/%.3f tx=%d ty=%d (px %d,%d)" % (m["sx"], m["sy"], m["r0"], m["r1"], m["tx"], m["ty"], m["tx"] // 20, m["ty"] // 20) if m else " (no matrix)"))
        if code == 39:
            sid = struct.unpack_from("<H", body, 0)[0]
            walk(body[4:], depth + 1, out, "sprite%d" % sid)
        if code == 0 and depth == 0: break

d = open(sys.argv[1], "rb").read()
payload = zlib.decompress(d[8:]) if d[:3] == b"CFX" else d[8:]
nbits = payload[0] >> 3; rect_bytes = (5 + 4 * nbits + 7) // 8
out = []
walk(payload[rect_bytes + 4:], 0, out)
print("\n".join(out))
