"""A top-level `local` read BEFORE its declaration line is a nil GLOBAL, silently.

Lua resolves a name at compile time: code above `local X = ...` sees the GLOBAL X, which is nil, and the
chunk still loads, runs and logs happily. `lua_order_check.py` catches that for local FUNCTIONS; this one
catches it for every top-level local, which is how a knob can read "table" in the file and behave as nil
in the game (measured 2026-09-19: CAMO_NAMES declared at line 921 and read at 462/465/482/773/775, so the
borrowed-description mechanism it was meant to retire stayed alive for days).

    python lua_local_order_check.py <file.lua> [more.lua ...]

Prints one line per offence and exits non-zero if there is any. With --selftest it builds a file that has
the defect and one that does not, and checks it reports exactly the first -- a check that cannot fail is
not a check.
"""
import io
import re
import sys

DECL = re.compile(r"^local\s+(?!function\b)([A-Za-z_][A-Za-z_0-9]*)\s*(?:,|=|$)")
NAME = re.compile(r"[A-Za-z_][A-Za-z_0-9]*")


def strip_noise(text):
    """Blank out long comments/strings, then line comments and quoted strings, keeping line numbers."""
    out = []
    i, n = 0, len(text)
    while i < n:
        if text.startswith("--[[", i) or text.startswith("--[==[", i):
            end = text.find("]]", i)
            end = n if end == -1 else end + 2
            out.append("".join(c if c == "\n" else " " for c in text[i:end]))
            i = end
        elif text.startswith("--", i):
            end = text.find("\n", i)
            end = n if end == -1 else end
            out.append(" " * (end - i))
            i = end
        elif text[i] in "\"'":
            quote = text[i]
            j = i + 1
            while j < n and text[j] != quote:
                j += 2 if text[j] == "\\" else 1
            j = min(j + 1, n)
            out.append("".join(c if c == "\n" else " " for c in text[i:j]))
            i = j
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def check(path):
    text = strip_noise(io.open(path, encoding="utf-8", newline="").read())
    lines = text.split("\n")

    declared = {}
    for number, line in enumerate(lines, 1):
        match = DECL.match(line)
        if match and match.group(1) not in declared:      # the FIRST declaration is the one that counts
            declared[match.group(1)] = number

    offences = []
    for number, line in enumerate(lines, 1):
        if DECL.match(line):
            continue
        for word in set(NAME.findall(line)):
            if word in declared and number < declared[word]:
                offences.append((number, word, declared[word], line.strip()[:90]))

    for number, word, at, snippet in offences:
        print("   line %-5d reads %-22s declared at line %-5d -> it is nil there   | %s"
              % (number, word, at, snippet))
    print("%-58s %s" % (path.split("\\")[-1], "PASS" if not offences else "FAIL (%d)" % len(offences)))
    return len(offences)


def selftest():
    bad = "C:/Users/keku/AppData/Local/Temp/lloc_bad.lua"
    good = "C:/Users/keku/AppData/Local/Temp/lloc_good.lua"
    io.open(bad, "w", encoding="utf-8", newline="").write(
        'local function f()\n\tif KNOB ~= "table" then return 1 end\n\treturn 2\nend\nlocal KNOB = "table"\n')
    io.open(good, "w", encoding="utf-8", newline="").write(
        'local KNOB = "table"\nlocal function f()\n\tif KNOB ~= "table" then return 1 end\n\treturn 2\nend\n')
    n_bad, n_good = check(bad), check(good)
    ok = n_bad == 1 and n_good == 0
    print("SELFTEST %s (defect reported %d, clean reported %d)" % ("PASS" if ok else "FAIL", n_bad, n_good))
    return 0 if ok else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sys.exit(selftest())
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(1 if sum(check(p) for p in sys.argv[1:]) else 0)
