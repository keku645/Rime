"""Print the type/field descriptors of a raw Frostbite 2 EBX partition, plus the payload of every
array whose element type is one of the shader-parameter structs. Diagnostic only: the goal is to
compare a partition Rime wrote against the game's own for the same types, byte for byte.

usage: python ebxdesc.py <partition.bin> [type-name-filter ...]
"""
import struct, sys


def hash_quick(s: str) -> int:
    # fb::hashQuick: basis 0x1505, prime 0x21, multiply then xor (a djb2 variant); measured on the
    # descriptor of "$" = 0x0002b581 in a shipped partition.
    h = 0x1505
    for b in s.encode("utf-8"):
        h = ((h * 0x21) ^ b) & 0xFFFFFFFF
    return h


def hash_djb2(s: str) -> int:
    h = 5381
    for b in s.encode("utf-8"):
        h = ((h * 33) + b) & 0xFFFFFFFF
    return h


def align16(x):
    return (x + 15) & ~15


def parse(path):
    d = open(path, "rb").read()
    (magic, meta, payload, imports, _z, inst_count, tcount, fcount, tstr, strs, acount, aoff) = struct.unpack_from("<12I", d, 0)
    if magic not in (0x0FB2D1CE, 0xCED1B20F):
        print("not an EBX partition (magic %08x)" % magic)
        return None
    pos = 80
    pos += 32 * imports
    names_blob = d[pos:pos + tstr]
    pos += tstr
    names = [n for n in names_blob.decode("utf-8", "replace").split("\0") if n]
    by_hash = {}
    for n in names:
        by_hash[hash_quick(n)] = n
        by_hash.setdefault(hash_djb2(n), n)
    fields = []
    for i in range(fcount):
        nh, flags, ftype, off, soff = struct.unpack_from("<IHHii", d, pos)
        fields.append((by_hash.get(nh, "%08x" % nh), flags, ftype, off, soff))
        pos += 16
    pos = align16(pos)
    types = []
    for i in range(tcount):
        nh, layout, fc, al, flags, size, ssize = struct.unpack_from("<IIBBHHH", d, pos)
        types.append((by_hash.get(nh, "%08x" % nh), layout, fc, al, flags, size, ssize))
        pos += 16
    insts = []
    for i in range(inst_count):
        insts.append(struct.unpack_from("<III", d, pos))
        pos += 12
    pos = align16(pos)
    arrays = []
    for i in range(acount):
        arrays.append(struct.unpack_from("<III", d, pos))
        pos += 12
    return dict(data=d, meta=meta, strs=strs, aoff=aoff, fields=fields, types=types, insts=insts, arrays=arrays)


def hexdump(buf, base=0, width=16):
    out = []
    for i in range(0, len(buf), width):
        chunk = buf[i:i + width]
        out.append("    %06x  %-48s  %s" % (base + i, " ".join("%02x" % b for b in chunk),
                                            "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)))
    return "\n".join(out)


def main():
    path = sys.argv[1]
    want = set(sys.argv[2:]) or {"VectorShaderParameter", "TextureShaderParameter", "BoolShaderParameter",
                                 "SurfaceShaderInstanceDataStruct", "MeshMaterialVariation", "ObjectVariation",
                                 "MeshVariationDatabaseEntry", "MeshVariationDatabaseMaterial"}
    p = parse(path)
    if p is None:
        return
    print("== %s  (%d types, %d fields, %d arrays, meta=%d strings=%d arrayOffset=%d)" % (
        path, len(p["types"]), len(p["fields"]), len(p["arrays"]), p["meta"], p["strs"], p["aoff"]))
    for ti, (name, layout, fc, al, flags, size, ssize) in enumerate(p["types"]):
        if name not in want:
            continue
        print("  type[%d] %-32s fields=%d align=%d flags=0x%04x size=%d ssize=%d" % (ti, name, fc, al, flags, size, ssize))
        for fi in range(layout, layout + fc):
            fname, fflags, ftype, off, soff = p["fields"][fi]
            tname = p["types"][ftype][0] if ftype < len(p["types"]) else "?"
            print("      field[%d] %-24s flags=0x%04x type=%d(%s) offset=%d soff=%d" % (fi, fname, fflags, ftype, tname, off, soff))
    base = p["meta"] + p["strs"] + p["aoff"]
    for ai, (off, count, ti) in enumerate(p["arrays"]):
        tname = p["types"][ti][0] if ti < len(p["types"]) else "?"
        if tname not in want or count == 0:
            continue
        size = p["types"][ti][5]
        start = base + off
        print("  array[%d] of %s x%d (type size %d) at 0x%x, count word before = %d" % (
            ai, tname, count, size, start, struct.unpack_from("<I", p["data"], start - 4)[0]))
        print(hexdump(p["data"][start:start + min(count * size, 8 * 32)], start))


if __name__ == "__main__":
    main()
