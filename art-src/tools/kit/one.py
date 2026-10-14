"""Build ONE model from art-src/tools/build_models.py, unrounded, into a scratch folder.

    python art-src/tools/kit/one.py <key> <out_dir>

Writes <out_dir>/<kind>/<key>.glb (kind is structures, units or ores) and prints the triangle count, the node names
and the bounds. Round it next with round_models.py (see docs/art/ASSET_GUIDE.md, "The model pipeline").
"""
import os, sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import build_models as bm

key, out = sys.argv[1], sys.argv[2]
bm.OUT = out
builder = getattr(bm, 'S_' + key, None) or getattr(bm, 'V_' + key, None)
asset = builder() if builder else None
if asset is None:  # parameterised builders (V_tank(...), O_ore(...)) only live in BUILDERS
    for b in bm.BUILDERS:
        a = b()
        if a.key == key:
            asset = a
            break
if asset is None:
    sys.exit(f'no builder for {key!r}: add S_{key} or V_{key} to build_models.py')
m = asset.export()
print(m['tris'], 'tris', os.path.join(out, asset.kind, key + '.glb'))
print('nodes', m['nodes'])
print('bounds', m['bounds'])
