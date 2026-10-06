"""Block/bracket balance check for a Lua chunk, with no Lua on the machine.

Strips long comments/strings ([[...]], [==[...]==]), line comments and quoted strings, then walks the
keywords that open and close a block. Reports the first line where a block closes that never opened, and
every block still open at the end (with the line it opened on). Also balances ( ) { } [ ].

usage: python lua_block_check.py <file.lua>
"""
import re
import sys

PATH = sys.argv[1]
SRC = open(PATH, encoding="utf-8", newline="").read()

# ---- strip comments and strings, keeping line numbers ---------------------------------------------------
out = []
i = 0
n = len(SRC)
while i < n:
    c = SRC[i]

    # long bracket, as a comment or as a string
    m = re.match(r"(--)?\[(=*)\[", SRC[i:])
    if m and (m.group(1) or c == "["):
        close = "]" + m.group(2) + "]"
        end = SRC.find(close, i + m.end())
        end = n if end < 0 else end + len(close)
        out.append(re.sub(r"[^\n]", " ", SRC[i:end]))
        i = end
        continue

    if SRC.startswith("--", i):
        end = SRC.find("\n", i)
        end = n if end < 0 else end
        out.append(" " * (end - i))
        i = end
        continue

    if c in "\"'":
        j = i + 1
        while j < n and SRC[j] != c:
            j += 2 if SRC[j] == "\\" else 1
        j = min(j + 1, n)
        out.append(re.sub(r"[^\n]", " ", SRC[i:j]))
        i = j
        continue

    out.append(c)
    i += 1

CODE = "".join(out)
LINES = CODE.split("\n")

# ---- walk the block keywords ----------------------------------------------------------------------------
stack = []      # (keyword, line)
problems = []
pending_do = 0  # a `do` that belongs to the for/while already pushed

for ln, line in enumerate(LINES, 1):
    for m in re.finditer(r"\b(function|if|for|while|do|repeat|until|end|then|else|elseif)\b", line):
        word = m.group(1)

        if word in ("function", "if"):
            stack.append((word, ln))
        elif word in ("for", "while"):
            stack.append((word, ln))
            pending_do += 1
        elif word == "do":
            if pending_do > 0:
                pending_do -= 1          # the `do` of that for/while: the block is already on the stack
            else:
                stack.append((word, ln))
        elif word == "repeat":
            stack.append((word, ln))
        elif word == "until":
            if stack and stack[-1][0] == "repeat":
                stack.pop()
            else:
                problems.append(f"line {ln}: 'until' with no 'repeat'")
        elif word == "end":
            if stack:
                stack.pop()
            else:
                problems.append(f"line {ln}: 'end' closes a block that never opened")

for word, ln in stack:
    problems.append(f"line {ln}: '{word}' is never closed")

# ---- brackets -------------------------------------------------------------------------------------------
pairs = {")": "(", "]": "[", "}": "{"}
open_stack = []
for ln, line in enumerate(LINES, 1):
    for ch in line:
        if ch in "([{":
            open_stack.append((ch, ln))
        elif ch in ")]}":
            if open_stack and open_stack[-1][0] == pairs[ch]:
                open_stack.pop()
            else:
                problems.append(f"line {ln}: '{ch}' does not close anything")

for ch, ln in open_stack:
    problems.append(f"line {ln}: '{ch}' is never closed")

if problems:
    print(f"FAIL {PATH}")
    for p in problems[:20]:
        print("  " + p)
    sys.exit(1)

print(f"PASS {PATH}: blocks and brackets balanced ({len(LINES)} lines)")
