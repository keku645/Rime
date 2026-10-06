"""Per-widget settings, read from the widgets' own ActionScript (ffdec export of every ui/assets movie in as_export/):
  . the widget movie's frame script binds its symbol to a class: Object.registerClass("Grid", Widget.Grid.Grid)
  . the class (in the AS libraries) reads its WidgetProperties from initData.<Name> at initialize, declares defaults
    (var p_animTime = 1;) and converts (== "true" -> bool, Number()/parseInt -> number, String() -> string)
  . its update<Name>Data(...) methods are the data channels a binding's DataName can address
Base classes (extends chain) contribute too and are marked as inherited.
Output: widget_settings.json  { "widgets": { "<movie>": { symbol, class, chain, settings: {name: {default, kind, from}}, dataNames: [...] } } }
"""
import os, re, json, glob, collections, sys
# usage: as_widget_settings.py [<as_export dir>] [<output json>]  (defaults: next to this script)
root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'as_export')
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), 'widget_settings.json')

# 1. class index over every exported library / movie
classes = {}   # "Widget.Grid.Grid" -> (path, text)
for f in glob.glob(os.path.join(root, '*', 'scripts', '__Packages', '**', '*.as'), recursive=True):
    rel = f.split('__Packages' + os.sep, 1)[1][:-3].replace(os.sep, '.')
    if rel not in classes:
        classes[rel] = (f, open(f, encoding='utf-8', errors='replace').read())
print('classes', len(classes))

re_extends = re.compile(r'class\s+([\w.]+)(?:\s+extends\s+([\w.]+))?')
re_read = re.compile(r'initData\.([A-Za-z_]\w*)')
re_decl = re.compile(r'^\s*var\s+([A-Za-z_]\w*)\s*=\s*([^;]+);', re.M)
re_update = re.compile(r'function\s+update([A-Za-z_]\w*)Data\s*\(')
re_register = re.compile(r'Object\.registerClass\("([^"]+)",\s*([\w.]+)\)')

def kind_of(text, name):
    seg = ''.join(m.group(0) for m in re.finditer(r'[^\n]*initData\.' + re.escape(name) + r'\b[^\n]*(?:\n[^\n]*){0,3}', text))
    if re.search(r'initData\.' + re.escape(name) + r'\s*==\s*"(true|false)"', seg) or re.search(r'(true|false)\s*==\s*initData\.' + re.escape(name), seg): return 'bool'
    if re.search(r'Number\(\s*initData\.' + re.escape(name), seg) or re.search(r'parseInt\(\s*initData\.' + re.escape(name), seg) or re.search(r'parseFloat\(\s*initData\.' + re.escape(name), seg): return 'number'
    if re.search(r'initData\.' + re.escape(name) + r'\s*(!=|==)\s*(0|"1"|1)\b', seg): return 'flag'
    if re.search(r'isNaN\(\s*initData\.' + re.escape(name), seg): return 'number'
    if re.search(r'String\(\s*initData\.' + re.escape(name), seg): return 'string'
    return 'string'

def chain_of(cls):
    seen = []
    while cls and cls in classes and cls not in seen:
        seen.append(cls)
        m = re_extends.search(classes[cls][1])
        cls = m.group(2) if m else None
    return seen

def settings_of(cls):
    settings = {}; data_names = []
    for c in chain_of(cls):
        text = classes[c][1]
        decls = {m.group(1): m.group(2).strip() for m in re_decl.finditer(text)}
        for m in re_read.finditer(text):
            name = m.group(1)
            if name in ('data', 'events', 'name', 'path', 'type', 'screen', 'id', 'x', 'y', 'NumEvents', 'Align', 'ZDepthLevel') or name in settings: continue   # the last three are WidgetNode fields the base class reads, not properties
            settings[name] = {'default': decls.get(name), 'kind': kind_of(text, name), 'from': c}
        for m in re_update.finditer(text):
            n = m.group(1)
            if n not in data_names: data_names.append(n)
    return settings, data_names

widgets = {}
for f in glob.glob(os.path.join(root, '*', 'scripts', '*.as')):
    movie = os.path.basename(os.path.dirname(os.path.dirname(f)))
    text = open(f, encoding='utf-8', errors='replace').read()
    for m in re_register.finditer(text):
        symbol, cls = m.group(1), m.group(2)
        if cls not in classes: continue
        s, d = settings_of(cls)
        # the symbol (or class) named like the movie is the widget's class; a movie registers helper classes too (kitinfobox binds
        # "label" to Util.ScrollText after its own class), and a helper must never replace the widget's class — not even when the
        # widget's class declares no settings of its own (that was the case for kitinfobox: an empty settings dict let the helper win)
        by_name = symbol.lower() == movie.lower() or cls.split('.')[-1].lower() == movie.lower()
        w = widgets.get(movie)
        if w is None or (by_name and not w['byName']):
            widgets[movie] = {'symbol': symbol, 'class': cls, 'chain': chain_of(cls), 'settings': s, 'dataNames': d, 'byName': by_name}
for w in widgets.values(): w.pop('byName', None)
json.dump({'source': 'ActionScript of the ui/assets movies (initData reads, var defaults, update*Data methods)', 'widgets': widgets}, open(out, 'w', encoding='utf-8'), indent=1)
print('widgets', len(widgets), 'with settings', sum(1 for w in widgets.values() if w['settings']), 'with data names', sum(1 for w in widgets.values() if w['dataNames']))
for name in ('grid', 'tabbar', 'button', 'kitselector', 'pageheader', 'textfield', 'list', 'consolebuttonbar'):
    w = widgets.get(name)
    print(name, '->', w and w['class'], w and {k: (v['default'], v['kind']) for k, v in w['settings'].items()}, w and w['dataNames'])
