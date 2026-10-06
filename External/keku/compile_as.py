"""Compiles one ActionScript 1/2 file into the body of a DoAction tag (End included) — a frame script the UI editor puts on a
screen's timeline (stage op `script:<name>:<vars>:<base64>`, GfxMovie.AddFrameScript) or the data recorder rides on.

The compiler is ffdec's ActionScript direct editation, run on a CARRIER movie that holds nothing but one placeholder frame
action: only our script gets compiled, no shipped script is ever re-emitted (re-emitting a movie's other scripts is what
broke widgets in the game).

    python External/keku/compile_as.py <script.as> <out.avm1> [--base64 <out.txt>] [--ffdec <ffdec-cli.exe>]

Prints the decompilation of the compiled action (ffdec -export script on the result) so the round trip can be read; with
--base64 also writes the body base64-encoded, ready to paste into a document's stage op.

Known limit of the compiler (measured 2026-09-16): a function literal called in place — `x = function(a){ return function(){…}; }(v)`
— comes out as the FACTORY assigned to x (each call returns the inner function and does nothing), with no error and a clean
decompilation. Write closures through a named factory (`var make = function(a){ return function(){…}; }; x = make(v);`).
"""
import base64, os, shutil, struct, subprocess, sys, tempfile, zlib

DEFAULT_FFDEC = r'F:\Desktop\Venice Unleashed\ffdec\ffdec-cli.exe'


def tag(code, body):
    return struct.pack('<HI', (code << 6) | 0x3F, len(body)) + body


def carrier():
    """FWS v8, 1280x720, 30 fps, one frame: FileAttributes(UseNetwork), black, DoAction(trace), ShowFrame, End."""
    bits = ''.join(format(v, f'0{n}b') for v, n in ((16, 5), (0, 16), (25600, 16), (0, 16), (14400, 16)))
    bits += '0' * (-len(bits) % 8)
    rect = bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8))
    payload = rect + bytes([0x00, 30]) + struct.pack('<H', 1)
    payload += tag(69, bytes([0x01, 0, 0, 0]))
    payload += tag(9, bytes([0, 0, 0]))
    trace = b'\x96' + struct.pack('<H', 9) + b'\x00carrier\x00' + b'\x26' + b'\x00'
    payload += tag(12, trace)
    payload += tag(1, b'')
    payload += tag(0, b'')
    return b'FWS' + bytes([8]) + struct.pack('<I', len(payload) + 8) + payload


def tags_of(swf):
    body = zlib.decompress(swf[8:]) if swf[:1] == b'C' else swf[8:]
    nbits = body[0] >> 3
    pos = (5 + 4 * nbits + 7) // 8 + 4
    while pos < len(body):
        code_len = struct.unpack_from('<H', body, pos)[0]
        code, length = code_len >> 6, code_len & 0x3F
        pos += 2
        if length == 0x3F:
            length = struct.unpack_from('<I', body, pos)[0]
            pos += 4
        yield code, body[pos:pos + length]
        pos += length
        if code == 0:
            break


def compile_as(src, out, ffdec=DEFAULT_FFDEC, base64_out=None, show=True):
    work = tempfile.mkdtemp(prefix='rue_as_')
    carrier_path = os.path.join(work, 'carrier.swf')
    out_path = os.path.join(work, 'carrier_out.swf')
    src_dir = os.path.join(work, 'src', 'scripts', 'frame_1')
    os.makedirs(src_dir)
    with open(carrier_path, 'wb') as f:
        f.write(carrier())
    shutil.copyfile(src, os.path.join(src_dir, 'DoAction.as'))
    r = subprocess.run([ffdec, '-importScript', carrier_path, out_path, os.path.join(work, 'src')], capture_output=True, text=True, timeout=600)
    if r.returncode != 0 or not os.path.exists(out_path):
        print('importScript exit', r.returncode)
        print((r.stdout or '').strip()[-2000:])
        print((r.stderr or '').strip()[-2000:])
        print('FAIL: no output movie')
        return 1
    actions = [body for code, body in tags_of(open(out_path, 'rb').read()) if code == 12]
    if len(actions) != 1:
        print('FAIL: expected one DoAction, found', len(actions))
        return 1
    body = actions[0]
    if b'carrier' in body:
        print('FAIL: the placeholder script is still there (the import did not replace it: a compile error — see below)')
        print((r.stdout or '').strip()[-3000:])
        print((r.stderr or '').strip()[-3000:])
        return 1
    if body[-1:] != b'\x00':
        body += b'\x00'
    with open(out, 'wb') as f:
        f.write(body)
    print(f'{out}: {len(body)} bytes')
    if base64_out:
        with open(base64_out, 'w', encoding='ascii') as f:
            f.write(base64.b64encode(body).decode('ascii'))
        print(f'{base64_out}: base64 written')
    if show:
        verify = os.path.join(work, 'verify')
        subprocess.run([ffdec, '-export', 'script', verify, out_path], capture_output=True, text=True, timeout=600)
        decompiled = os.path.join(verify, 'scripts', 'frame_1', 'DoAction.as')
        if os.path.exists(decompiled):
            text = open(decompiled, encoding='utf-8', errors='replace').read()
            print(f'--- decompiled ({len(text)} chars) ---')
            print(text)
        else:
            print('no decompilation produced')
    return 0


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    ffdec = DEFAULT_FFDEC
    b64 = None
    args = list(argv[1:])
    if '--ffdec' in args:
        i = args.index('--ffdec'); ffdec = args[i + 1]; del args[i:i + 2]
    if '--base64' in args:
        i = args.index('--base64'); b64 = args[i + 1]; del args[i:i + 2]
    return compile_as(args[0], args[1], ffdec, b64)


if __name__ == '__main__':
    sys.exit(main(sys.argv))
