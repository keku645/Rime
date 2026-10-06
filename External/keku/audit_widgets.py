"""Widgets vs stage clips: for every screen partition, which WidgetNode InstanceNames have a named placement in
the screen movie (frame 1, any depth) and which do not; and which named placements have no node.
Movies are read from the cache (resources/ui_assets_<name>.bin) with a minimal SWF/CFX tag walker."""
import json, os, sys, struct, zlib, collections

CACHE = os.path.join(os.path.dirname(__file__), 'rue_selftest', 'cache', '1f0bbddc1b0e')
PART = os.path.join(CACHE, 'partitions')
RES = os.path.join(CACHE, 'resources')

class Bits:
    def __init__(self, data, pos):
        self.data = data; self.pos = pos; self.bit = 0
    def read(self, n):
        v = 0
        for _ in range(n):
            byte = self.data[self.pos]
            v = (v << 1) | ((byte >> (7 - self.bit)) & 1)
            self.bit += 1
            if self.bit == 8:
                self.bit = 0; self.pos += 1
        return v
    def align(self):
        if self.bit:
            self.bit = 0; self.pos += 1

def rect_len(data, pos):
    b = Bits(data, pos); n = b.read(5); b.read(4 * n); b.align(); return b.pos - pos

def tags(data, pos, end):
    while pos + 2 <= end:
        code_len = struct.unpack_from('<H', data, pos)[0]; pos += 2
        code = code_len >> 6; ln = code_len & 0x3F
        if ln == 0x3F:
            ln = struct.unpack_from('<I', data, pos)[0]; pos += 4
        yield code, data[pos:pos + ln]
        pos += ln
        if code == 0:
            break

def place_names(body):
    """PlaceObject2 (26) / PlaceObject3 (70): returns the instance name when the tag has one."""
    flags = body[0]
    has_char = flags & 2; has_matrix = flags & 4; has_cx = flags & 8; has_ratio = flags & 16; has_name = flags & 32; has_clip = flags & 64
    return has_name

def parse_po(code, body):
    p = 0
    flags = body[0]; p = 1
    if code == 70:
        flags2 = body[1]; p = 2
    else:
        flags2 = 0
    depth = struct.unpack_from('<H', body, p)[0]; p += 2
    if code == 70 and (flags2 & 8):   # class name
        while body[p] != 0: p += 1
        p += 1
    if flags & 2:
        p += 2
    if flags & 4:
        b = Bits(body, p)
        if b.read(1): n = b.read(5); b.read(2 * n)
        if b.read(1): n = b.read(5); b.read(2 * n)
        n = b.read(5); b.read(2 * n); b.align(); p = b.pos
    if flags & 8:
        b = Bits(body, p); has_add = b.read(1); has_mul = b.read(1); n = b.read(4)
        if has_mul: b.read(4 * n)
        if has_add: b.read(4 * n)
        b.align(); p = b.pos
    if flags & 16: p += 2
    name = None
    if flags & 32:
        e = body.index(b'\0', p); name = body[p:e].decode('latin-1'); p = e + 1
    return depth, name

def movie_names(path):
    data = open(path, 'rb').read()
    sig = data[:3]
    if sig in (b'CFX', b'CWS'):
        data = data[:8] + zlib.decompress(data[8:])
    pos = 8
    pos += rect_len(data, pos)
    pos += 4  # frame rate + count
    names = {}   # sprite id -> [names]
    top = []
    for code, body in tags(data, pos, len(data)):
        if code in (26, 70):
            d, n = parse_po(code, body)
            if n: top.append(n)
        elif code == 39:
            sid = struct.unpack_from('<H', body, 0)[0]
            inner = []
            for c2, b2 in tags(body, 4, len(body)):
                if c2 in (26, 70):
                    d, n = parse_po(c2, b2)
                    if n: inner.append(n)
            names[sid] = inner
    return top, names

tot_w = tot_ok = tot_missing = 0
missing_examples = collections.Counter()
no_movie = 0
graph_widgets = 0
extra_clips = collections.Counter()
for fn in sorted(os.listdir(PART)):
    if not (fn.startswith('ui_flow_') or (fn.startswith('ui_xp') and '_flow_' in fn)):   # base game + the expansions' ui/xp5/flow
        continue
    j = json.load(open(os.path.join(PART, fn), encoding='utf-8'))
    inst = j['Instances']
    widgets = [i for i in inst.values() if i.get('$type') == 'WidgetNode']
    if fn.startswith('ui_flow_graph_'):
        graph_widgets += len(widgets)
        continue
    if not fn.startswith('ui_flow_screen_'):
        continue
    name = fn[len('ui_flow_screen_'):-5].split('_')[-1]
    mv = os.path.join(RES, 'ui_assets_' + name + '.bin')
    if not os.path.exists(mv):
        no_movie += 1; continue
    top, sprites = movie_names(mv)
    clips = set(top)
    for lst in sprites.values(): clips.update(lst)
    wn = [w.get('InstanceName') for w in widgets]
    tot_w += len(wn)
    for n in wn:
        if n in clips: tot_ok += 1
        else:
            tot_missing += 1; missing_examples[(fn, n, next((w.get('Name') for w in widgets if w.get('InstanceName') == n), ''))] += 1
    for c in clips - set(wn):
        extra_clips[c] += 1
print(f'screen widgets={tot_w} with clip={tot_ok} without clip={tot_missing} screens without movie={no_movie} widgets in graph partitions={graph_widgets}')
print('widgets without a clip (first 40):')
for (fn, n, nm), c in list(missing_examples.items())[:40]:
    print('  ', fn, n, nm)
print('named clips with no widget node (top 30):', extra_clips.most_common(30))
