"""Splice motion.py's entries for the given keys into the shipped docs and profiles, and nothing else.

    python art-src/tools/kit/motion_splice.py <key> [key ...]          # write
    python art-src/tools/kit/motion_splice.py --check <key> [key ...]  # report ok / DIFF, change nothing
    python art-src/tools/kit/motion_splice.py --check --all            # every key motion.py knows

art-src/tools/motion.py is the source of truth for every asset's motion spec, but the shipped docs/art/MOTION.md,
docs/art/motion.json and unity/Assets/Pez/Art/PezMotionProfiles.cs have hand-spliced entries that a full regeneration
would clobber. So: generate into a temp dir, then replace only these keys' entries (a `### \\`key\\`` section, the
motion.json object with that key, the profile row). A key the docs don't have yet goes in after the entry that
precedes it in motion.py's order.
"""
import json, os, re, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
MD, JS = os.path.join(REPO, 'docs/art/MOTION.md'), os.path.join(REPO, 'docs/art/motion.json')
CS = os.path.join(REPO, 'unity/Assets/Pez/Art/PezMotionProfiles.cs')

args = sys.argv[1:]
check = '--check' in args
every = '--all' in args
keys = [a for a in args if a not in ('--check', '--all')]
if not keys and not every:
    sys.exit(__doc__)

# Generate everything into a temp dir with motion.py's OUT redirected.
tmp = tempfile.mkdtemp(prefix='pezmotion.')
src = open(os.path.join(REPO, 'art-src/tools/motion.py')).read()
src = re.sub(r'^OUT\s*=.*$', f'OUT = {tmp!r}', src, count=1, flags=re.M)
open(os.path.join(tmp, 'm.py'), 'w').write(src)
subprocess.run([sys.executable, os.path.join(tmp, 'm.py')], check=True, stdout=subprocess.DEVNULL)
gen_md = open(os.path.join(tmp, 'MOTION.md')).read()
gen_js = json.load(open(os.path.join(tmp, 'motion.json')))
gen_cs = open(os.path.join(tmp, 'Scripts', 'PezMotionProfiles.cs')).read()
order = [e['key'] for e in gen_js]
if every:
    keys = order
for k in keys:
    if k not in order:
        sys.exit(f'{k!r} is not in motion.py')

# MOTION.md: a section runs from "### `key`" to the next heading or the next unindented bold note ("**Every ...").
SEC = r'^### `%s`\n.*?(?=^### |^## |^\*\*|\Z)'


def md_section(text, k):
    m = re.search(SEC % re.escape(k), text, re.S | re.M)
    return m


def splice_md(text, k):
    new = md_section(gen_md, k).group(0)
    m = md_section(text, k)
    if m:
        return text[:m.start()] + new + text[m.end():]
    for prev in reversed(order[:order.index(k)]):
        p = md_section(text, prev)
        if p:
            return text[:p.end()] + new + text[p.end():]
    sys.exit(f'no place for {k} in MOTION.md')


def row(text, k):
    return re.search(r'^\s*\{"%s", new PezMotionProfile .*$\n' % re.escape(k), text, re.M)


def splice_cs(text, k):
    new = row(gen_cs, k).group(0)
    m = row(text, k)
    if m:
        return text[:m.start()] + new + text[m.end():]
    for prev in reversed(order[:order.index(k)]):
        p = row(text, prev)
        if p:
            return text[:p.end()] + new + text[p.end():]
    sys.exit(f'no place for {k} in PezMotionProfiles.cs')


md, cs, js = open(MD).read(), open(CS).read(), json.load(open(JS))
gen_by = {e['key']: e for e in gen_js}
bad = False
for k in keys:
    have = {e['key']: e for e in js}
    states = []
    m = md_section(md, k)
    states.append(('MOTION.md', m is not None and m.group(0) == md_section(gen_md, k).group(0)))
    states.append(('motion.json', have.get(k) == gen_by[k]))
    r = row(cs, k)
    g = row(gen_cs, k)
    states.append(('PezMotionProfiles.cs', (r is None and g is None) or (r is not None and g is not None and r.group(0) == g.group(0))))
    if check:
        print(k, ' '.join(f'{n}:{"ok" if ok else "DIFF"}' for n, ok in states))
        bad |= not all(ok for _, ok in states)
        continue
    md = splice_md(md, k)
    if row(gen_cs, k):
        cs = splice_cs(cs, k)
    if k in have:
        js[[e['key'] for e in js].index(k)] = gen_by[k]
    else:
        ks = [e['key'] for e in js]
        prev = next((p for p in reversed(order[:order.index(k)]) if p in ks), None)
        js.insert(ks.index(prev) + 1 if prev else len(js), gen_by[k])
    print(k, 'spliced')
if not check:
    open(MD, 'w').write(md)
    open(CS, 'w').write(cs)
    json.dump(js, open(JS, 'w'), indent=1)
sys.exit(1 if bad else 0)
