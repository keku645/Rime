"""Prints the accessory camo test's readings: Admin/Mods/AccessoryCamoTest/mod.db, table acctest (seq, at, realm, text)."""
import os, sqlite3, sys

DB = r"C:\Users\keku\Documents\Battlefield 3\Server\Admin\Mods\AccessoryCamoTest\mod.db"

path = sys.argv[1] if len(sys.argv) > 1 else DB
if not os.path.exists(path):
    print("no database yet:", path)
    sys.exit(1)

con = sqlite3.connect(path)
rows = con.execute("SELECT seq, at, realm, text FROM acctest ORDER BY seq").fetchall()
print(len(rows), "reading(s)")
for seq, at, realm, text in rows:
    print(f"{seq:4} {at:>10} {realm:14} {text}")
