"""Edit the STAGE of a Scaleform screen .gfx: move a placed clip, or swap which symbol it draws.

    python gfx_stage.py <in.gfx> <out.gfx> OP [OP ...]

    OP:  move:<clipName>:<expectedX>:<newX>      X in TWIPS (20 per pixel)
         char:<clipName>:<expectedId>:<newId>    swap the symbol the clip draws
         list                                    print every placement and change nothing

    python gfx_stage.py screen.gfx out.gfx char:KitSelector_05:4:8 move:KitSelector_03:1920:7920

WHY THIS EXISTS (and why not ffdec -xml2swf): a no-edit ffdec round-trip of a BF3 screen movie already loses
2 bytes inside a tag next to its "Arial" font reference -- ffdec does not fully understand Scaleform's
GFX-only tags and silently re-emits them shorter. In a one-variable experiment that is a second variable.
This rewrites bytes in place: `move` touches one bit-field, `char` touches one u16, and the payload length
never changes.

⚠ REPLACES `gfx_patch.py`, WHICH WAS BROKEN: that one found `KitSelector_05` and silently missed
  `KitSelector_04`, which has exactly the same shape. The bug was that its scan only descended into the FIRST
  DefineSprite it met at a given level and then let `pos` continue from the wrong place on some paths. Here
  the walk is a single generator used by every op, and `list` exists precisely so the parse can be checked
  against ffdec before trusting any edit. A tool you have only ever seen fail is not a tested tool.
"""

import io
import sys
import zlib


class Bits:
    def __init__(self, data, byte_pos):
        self.d = data
        self.pos = byte_pos * 8

    def read(self, n):
        v = 0
        for _ in range(n):
            v = (v << 1) | ((self.d[self.pos >> 3] >> (7 - (self.pos & 7))) & 1)
            self.pos += 1
        return v

    def read_signed(self, n):
        v = self.read(n)
        return v - (1 << n) if n and (v >> (n - 1)) & 1 else v

    def write_signed_at(self, bit_pos, n, value):
        if value < 0:
            value += 1 << n
        assert 0 <= value < (1 << n), "%d does not fit in %d bits" % (value, n)
        for i in range(n):
            bit = (value >> (n - 1 - i)) & 1
            idx, off = (bit_pos + i) >> 3, 7 - ((bit_pos + i) & 7)
            self.d[idx] = (self.d[idx] & ~(1 << off)) | (bit << off)


def skip_rect(d, pos):
    return pos + ((5 + 4 * (d[pos] >> 3)) + 7) // 8


def placements(payload, pos, end):
    """Every named PlaceObject2 in the stream, recursing into DefineSprite. Yields a dict per clip."""
    while pos < end - 1:
        code_len = int.from_bytes(payload[pos:pos + 2], "little")
        pos += 2
        code, length = code_len >> 6, code_len & 0x3F

        if length == 0x3F:
            length = int.from_bytes(payload[pos:pos + 4], "little")
            pos += 4

        at = pos

        if code == 39:  # DefineSprite: spriteId u16 + frameCount u16, then a nested tag stream
            for item in placements(payload, at + 4, at + length):
                yield item

        if code == 26:  # PlaceObject2
            flags = payload[at]
            p = at + 1 + 2
            char_at = None

            if flags & 0x02:
                char_at = p
                p += 2

            x_bit = ntrans = tx = ty = None

            if flags & 0x04:
                b = Bits(payload, p)
                if b.read(1):
                    n = b.read(5)
                    b.read(n)
                    b.read(n)
                if b.read(1):
                    n = b.read(5)
                    b.read(n)
                    b.read(n)
                ntrans = b.read(5)
                x_bit = b.pos
                tx, ty = b.read_signed(ntrans), b.read_signed(ntrans)
                p = (b.pos + 7) // 8

            if flags & 0x08:
                raise SystemExit("colour transform present at %d; parser needs extending" % at)
            if flags & 0x10:
                p += 2

            if flags & 0x20:
                e = payload.index(b"\x00", p) + 1
                yield {
                    "name": bytes(payload[p:e]).rstrip(b"\x00").decode("ascii", "replace"),
                    "char_at": char_at,
                    "char": int.from_bytes(payload[char_at:char_at + 2], "little") if char_at else None,
                    "x_bit": x_bit, "ntrans": ntrans, "x": tx, "y": ty,
                }

        pos = at + length

        if code == 0:
            return


def main():
    if len(sys.argv) < 4:
        raise SystemExit(__doc__.strip())

    src, dst, ops = sys.argv[1], sys.argv[2], sys.argv[3:]
    blob = io.open(src, "rb").read()
    assert blob[:3] == b"CFX", "not a Scaleform CFX movie"

    header = blob[:8]
    payload = bytearray(zlib.decompress(blob[8:]))
    original_len = len(payload)

    index = {}
    for item in placements(payload, skip_rect(payload, 0) + 4, len(payload)):
        assert item["name"] not in index, "two placements named %s" % item["name"]
        index[item["name"]] = item

    if ops == ["list"]:
        for name, it in index.items():
            print("%-24s char=%-4s x=%-7s y=%s" % (name, it["char"], it["x"], it["y"]))
        return

    before = bytes(payload)

    for op in ops:
        kind, name, expected, new = op.split(":")
        expected, new = int(expected), int(new)
        assert name in index, "no placement named %s (run `list` to see them)" % name
        it = index[name]

        if kind == "move":
            assert it["x"] == expected, "%s x is %s, expected %d" % (name, it["x"], expected)
            Bits(payload, 0).write_signed_at(it["x_bit"], it["ntrans"], new)
            print("%-24s x %d -> %d" % (name, expected, new))
        elif kind == "char":
            assert it["char"] == expected, "%s char is %s, expected %d" % (name, it["char"], expected)
            payload[it["char_at"]:it["char_at"] + 2] = new.to_bytes(2, "little")
            print("%-24s char %d -> %d" % (name, expected, new))
        else:
            raise SystemExit("unknown op %r" % kind)

    assert len(payload) == original_len, "payload length changed"

    # Re-parse the RESULT and show it, so the edit is checked against the file that ships, not against intent.
    print("--- after ---")
    for item in placements(payload, skip_rect(payload, 0) + 4, len(payload)):
        if item["name"].startswith("KitSelector"):
            print("%-24s char=%-4s x=%-7s y=%s" % (item["name"], item["char"], item["x"], item["y"]))

    changed = sum(1 for a, b in zip(before, payload) if a != b)
    out = zlib.compress(bytes(payload), 9)
    io.open(dst, "wb").write(header[:4] + (len(payload) + 8).to_bytes(4, "little") + out)
    print("%d byte(s) changed; wrote %s (%d bytes, payload %d unchanged)"
          % (changed, dst, 8 + len(out), len(payload)))


main()
