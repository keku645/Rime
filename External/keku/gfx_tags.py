# List the tag stream of a CFX/GFX movie (recursing into DefineSprite), with character ids where trivial.
import struct, sys, zlib

NAMES = {0:"End",1:"ShowFrame",2:"DefineShape",4:"PlaceObject",5:"RemoveObject",6:"DefineBits",7:"DefineButton",
 8:"JPEGTables",9:"SetBackgroundColor",10:"DefineFont",11:"DefineText",12:"DoAction",13:"DefineFontInfo",14:"DefineSound",
 20:"DefineBitsLossless",21:"DefineBitsJPEG2",22:"DefineShape2",24:"Protect",26:"PlaceObject2",28:"RemoveObject2",
 32:"DefineShape3",33:"DefineText2",34:"DefineButton2",35:"DefineBitsJPEG3",36:"DefineBitsLossless2",37:"DefineEditText",
 39:"DefineSprite",43:"FrameLabel",45:"SoundStreamHead2",46:"DefineMorphShape",48:"DefineFont2",56:"ExportAssets",
 57:"ImportAssets",58:"EnableDebugger",59:"DoInitAction",60:"DefineVideoStream",64:"EnableDebugger2",65:"ScriptLimits",
 66:"SetTabIndex",69:"FileAttributes",70:"PlaceObject3",71:"ImportAssets2",73:"DefineFontAlignZones",74:"CSMTextSettings",
 75:"DefineFont3",76:"SymbolClass",77:"Metadata",78:"DefineScalingGrid",83:"DefineShape4",84:"DefineMorphShape2",
 86:"DefineSceneAndFrameLabelData",88:"DefineFontName",
 1000:"GFxExporterInfo",1001:"GFxDefineExternalImage",1002:"GFxFontTextureInfo",1003:"GFxDefineExternalGradient",
 1004:"GFxDefineGradientMap",1005:"GFxDefineCompactedFont",1006:"GFxDefineExternalSound",1007:"GFxDefineExternalStreamSound",
 1008:"GFxDefineSubImage",1009:"GFxDefineExternalImage2",1010:"GFxTag1010(no loader in BF3)"}
ID_TAGS = {2,6,7,10,11,14,20,21,22,32,33,34,35,36,37,39,46,48,60,75,83,84,1001,1005,1008}

def walk(buf, depth, out):
    p = 0
    while p + 2 <= len(buf):
        h = struct.unpack_from("<H", buf, p)[0]; code, ln = h >> 6, h & 0x3F; p += 2
        if ln == 0x3F: ln = struct.unpack_from("<I", buf, p)[0]; p += 4
        body = buf[p:p+ln]; p += ln
        cid = struct.unpack_from("<H", body, 0)[0] if code in ID_TAGS and len(body) >= 2 else None
        extra = ""
        if code == 26:
            fl = body[0]; q = 1; q += 2
            if fl & 2: cid = struct.unpack_from("<H", body, q)[0]; q += 2
            extra = "charId=%s" % cid; cid = None
            # name
        if code in (56,):
            n = struct.unpack_from("<H", body, 0)[0]; q = 2; names = []
            for _ in range(n):
                i = struct.unpack_from("<H", body, q)[0]; q += 2; e = body.index(b"\0", q); names.append("%d:%s" % (i, body[q:e].decode("latin1"))); q = e + 1
            extra = " ".join(names)
        if code == 71:
            e = body.index(b"\0"); url = body[:e].decode("latin1"); q = e + 3; n = struct.unpack_from("<H", body, q)[0]; q += 2; names = []
            for _ in range(n):
                i = struct.unpack_from("<H", body, q)[0]; q += 2; e = body.index(b"\0", q); names.append("%d:%s" % (i, body[q:e].decode("latin1"))); q = e + 1
            extra = url + " " + " ".join(names)
        if code == 1000: extra = repr(body[:24])
        out.append("%s%-26s len=%-6d %s %s" % ("  " * depth, NAMES.get(code, "tag%d" % code), ln, ("id=%d" % cid) if cid is not None else "", extra))
        if code == 39:
            out.append("%s  (sprite id=%d frames=%d)" % ("  " * depth, struct.unpack_from("<H", body, 0)[0], struct.unpack_from("<H", body, 2)[0]))
            walk(body[4:], depth + 1, out)
        if code == 0 and depth == 0: break

d = open(sys.argv[1], "rb").read()
hdr = d[:3]; ver = d[3]; declared = struct.unpack("<I", d[4:8])[0]
payload = zlib.decompress(d[8:]) if hdr == b"CFX" else d[8:]
# skip RECT + frame rate + frame count
nbits = payload[0] >> 3; rect_bytes = (5 + 4 * nbits + 7) // 8
p = rect_bytes + 4
out = ["%s v%d declared=%d payload=%d header_skip=%d" % (hdr, ver, declared, len(payload), p)]
walk(payload[p:], 0, out)
print("\n".join(out))
