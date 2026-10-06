"""Exports the ActionScript of every ui/assets movie in the editor cache with ffdec (one JVM per file, ~3 s each):
scratchpad/as_export/<movie>/scripts/**.as — the source for the per-widget property catalogue (what each widget's
code actually reads: p_* and the setup members the engine writes from WidgetProperties)."""
import os, subprocess, shutil, sys, time
base = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'rue_selftest', 'cache', '1f0bbddc1b0e', 'resources')
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'as_export')
tmp = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'as_tmp')
os.makedirs(out, exist_ok=True); os.makedirs(tmp, exist_ok=True)
ffdec = r'F:\Desktop\Venice Unleashed\ffdec\ffdec-cli.exe'
files = sorted(f for f in os.listdir(base) if f.startswith('ui_assets_') and f.endswith('.bin'))
only = sys.argv[1:]  # optional movie-name filters
t0 = time.time(); n = 0
for f in files:
    name = f[len('ui_assets_'):-4]
    if only and not any(o in name for o in only): continue
    dst = os.path.join(out, name)
    if os.path.exists(os.path.join(dst, 'scripts')): continue
    gfx = os.path.join(tmp, name + '.gfx')
    shutil.copyfile(os.path.join(base, f), gfx)
    r = subprocess.run([ffdec, '-export', 'script', dst, gfx], capture_output=True, text=True, timeout=300)
    n += 1
    if r.returncode != 0 or not os.path.exists(os.path.join(dst, 'scripts')):
        print('FAIL', name, r.returncode, (r.stderr or r.stdout)[-300:].replace('\n', ' '))
    if n % 25 == 0: print(f'{n} done, {time.time()-t0:.0f}s', flush=True)
print(f'exported {n} movies in {time.time()-t0:.0f}s')
