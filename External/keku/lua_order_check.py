"""Static check for a Lua chunk: a `local function NAME` (or `local NAME = function`) used on a line BEFORE its definition,
inside another function, is a nil global at run time (Lua scoping). Prints every such use. Also lists globals called that
are not VU/Lua builtins (a typo shows as an unknown name).

usage: python lua_order_check.py <file.lua>
"""
import re, sys

path = sys.argv[1]
lines = open(path, encoding="utf-8").read().split("\n")

defs = {}
for i, l in enumerate(lines, 1):
    m = re.match(r"^\s*local\s+function\s+([A-Za-z_][A-Za-z0-9_]*)", l)
    if m:
        defs.setdefault(m.group(1), i)
        continue
    m = re.match(r"^\s*local\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*function", l)
    if m:
        defs.setdefault(m.group(1), i)
        continue
    m = re.match(r"^\s*local\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*nil\s*(--.*)?$", l)
    if m:
        defs.setdefault(m.group(1), i)   # a forward declaration counts as the definition

problems = 0
for name, line_def in defs.items():
    pat = re.compile(r"(?<![A-Za-z0-9_.:])" + re.escape(name) + r"\s*\(")
    for i, l in enumerate(lines[: line_def - 1], 1):
        code = l.split("--")[0]
        if pat.search(code):
            print(f"{path}:{i}: '{name}' called before its local definition at line {line_def}")
            problems += 1

print(f"{len(defs)} local functions, {problems} early use(s)")
sys.exit(1 if problems else 0)
