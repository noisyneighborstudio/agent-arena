"""What's in a .glb: triangles, named nodes (with positions) and materials.

    python art-src/tools/kit/glbinfo.py <model.glb> [...]
"""
import json, struct, sys

for p in sys.argv[1:]:
    b = open(p, 'rb').read()
    g = json.loads(b[20:20 + struct.unpack_from('<I', b, 12)[0]])
    tris = sum(g['accessors'][pr['indices']]['count'] // 3 for m in g['meshes'] for pr in m['primitives'])
    print(f'{p}: {tris} tris, {len(b) // 1024} KB')
    print('  nodes:', ', '.join(f"{n['name']}{tuple(round(v, 3) for v in n['translation']) if 'translation' in n else ''}"
                              for n in g['nodes'] if 'mesh' not in n))
    print('  materials:', ', '.join(m.get('name', '?') for m in g.get('materials', [])))
