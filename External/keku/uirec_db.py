"""The records the recorder mod kept in its database (F5 / uirecdump in the game -> Admin/Mods/RimeUiRecorder/mod.db, table uirec),
written back as the lines the editor's Import recording... reads:
    [UIREC] <run>.<kind><seq> {...}
usage: uirec_db.py <mod.db> [<out.txt>] [--run N] [--all]
  the latest run by default (each game session that ran the recorder is one run); --run N picks one, --all keeps every run.
"""
import sqlite3
import sys


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    db = argv[1]
    out = argv[2] if len(argv) > 2 and not argv[2].startswith('--') else None
    run = int(argv[argv.index('--run') + 1]) if '--run' in argv else None
    every = '--all' in argv
    con = sqlite3.connect(db)
    rows = con.execute('SELECT id, run, kind, seq, savedAt, json FROM uirec ORDER BY run, savedAt, seq').fetchall()
    con.close()
    runs = sorted({r[1] for r in rows})
    if not rows:
        print('# the table uirec is empty: press F5 in the game after a record completes', file=sys.stderr)
        return 1
    if run is None and not every:
        run = runs[-1]
    picked = [r for r in rows if every or r[1] == run]
    lines = ['[UIREC] %s %s' % (r[0], r[5]) for r in picked]
    text = '\n'.join(lines) + '\n'
    if out:
        with open(out, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
    else:
        sys.stdout.write(text)
    kinds = {}
    for r in picked:
        kinds[r[2]] = kinds.get(r[2], 0) + 1
    print('# runs in the database: %s; %s: %d record(s) (%s), %d chars%s' % (
        runs, 'every run' if every else 'run %d' % run, len(picked),
        ', '.join('%s=%d' % kv for kv in sorted(kinds.items())), sum(len(l) for l in lines),
        (' -> ' + out) if out else ''), file=sys.stderr)
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
