"""Which ID_* name hashes to a given localisation hash.

The text database stores hashes, not names (the name only exists where the data uses it), so a string found by
its English text can only be used from a script once its ID is known: the localiser takes the ID, hashes it and
looks it up. This walks the EBX dump for every ID_* token, hashes it the way the game does (djb2 with seed -1:
h = 0xFFFFFFFF; h = h*33 + c) and reports the ones that match.

usage: python find_text_ids.py <hash> [<hash> ...]
"""
import os
import re
import sys

ROOTS = [r"F:\Desktop\Venice Unleashed\EBX-Json",
         r"F:\Desktop\Venice Unleashed\UI-Swf-decompiled"]   # the widgets name ids the assets never mention
WANTED = {int(a) & 0xFFFFFFFF for a in [a for a in sys.argv[1:] if a.isdigit()]}
EXTS = (".json", ".as", ".xml", ".txt", ".lua")

if not WANTED and "--list" not in sys.argv:
    print(__doc__)
    sys.exit(2)


def string_hash(text):
    h = 0xFFFFFFFF
    for ch in text:
        h = (h * 33 + ord(ch)) & 0xFFFFFFFF
    return h


seen, found = set(), {}
token = re.compile(r"ID_[A-Z0-9_]+")

for base in ROOTS:
  for root, _dirs, files in os.walk(base):
    for name in files:
        if not name.lower().endswith(EXTS):
            continue
        try:
            text = open(os.path.join(root, name), encoding="utf-8", errors="ignore").read()
        except Exception:
            continue
        for match in token.findall(text):
            if match in seen:
                continue
            seen.add(match)
            h = string_hash(match)
            if h in WANTED:
                found.setdefault(h, []).append(match)

print("%d distinct ID_* token(s) in the dump" % len(seen))
for h in sorted(WANTED):
    print("%10d -> %s" % (h, ", ".join(found.get(h, [])) or "(no ID in the data hashes to it)"))

# --list <file>: every ID the game's data and widgets name, one per line, for the editor's --texts seam to
# resolve in every language (the database keys by hash, so this is the only way back to the names).
if "--list" in sys.argv:
    out = sys.argv[sys.argv.index("--list") + 1]
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        for name in sorted(seen):
            f.write(name + "\n")
    print("%d id(s) -> %s" % (len(seen), out))
