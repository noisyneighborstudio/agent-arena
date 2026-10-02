"""Pez terrain kit: tiered licorice massifs built from a rock grid, plus faceted boulders.

Algorithm (mirrored 1:1 in assets/unity/Scripts/PezCliffBuilder.cs):
  1. Input: bool rock[W, D] on the 1-unit tile grid (the pathing grid stays the source of truth).
  2. Tiers: tier 0 = rock, tier k = 8-neighbour erosion of tier k-1. Thick masses get 2-3 tiers,
     thin ridges stay as one mesa, so massifs read as mountains with height, not flat cut-outs.
  3. Field per tier: sample at cell centres, v = 0.6*self + 0.1*(4 neighbours), iso 0.5. Marching
     squares on the dual grid gives a smooth, organic outline that still hugs the blocked cells
     (a single blocked cell still shows as a small butte).
  4. Cap: per dual cell, the inside polygon, fan-triangulated at the tier top height.
  5. Walls: every iso segment (inside on the left) gets 5 rings stepping down and out:
     rim, lip, face, talus, buried skirt. Offsets follow the field gradient and are jittered
     by a position hash, so shared endpoints always agree and the mesh is crack-free.
  6. Dressing: boulders at the wall base (tier 0), crumble on caps.
Materials: per-tier crust (lighter as it climbs), licorice face, warm talus. Flat shaded.
"""
import math, os, json
import numpy as np
import trimesh
from trimesh.visual.material import PBRMaterial

HERE = os.path.dirname(__file__)
OUT = os.path.join(HERE, 'pack', 'models', 'terrain')

def lin(v):
    v = v / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
def M(name, hx, rough):
    c = [int(hx[i:i + 2], 16) for i in (1, 3, 5)]
    return PBRMaterial(name=name, baseColorFactor=[lin(x) for x in c] + [1.0], roughnessFactor=rough, metallicFactor=0.0)
MATS = {
    'crust0': M('M_Cliff_Crust_T0', '#7A604C', .95),
    'crust1': M('M_Cliff_Crust_T1', '#8E7259', .95),
    'crust2': M('M_Cliff_Crust_T2', '#A58A6C', .95),
    'face': M('M_Cliff_Licorice', '#2E2629', .45),
    'base': M('M_Cliff_Talus', '#4A3D3A', .75),
}
TIER_H = [1.0, 0.85, 0.7]
ISO = 0.5
# (outward offset, jitter out, jitter y)
RINGS = [(0.00, 0.00, 0.00), (0.07, 0.02, 0.00), (0.17, 0.07, 0.06), (0.32, 0.07, 0.00), (0.44, 0.04, 0.00)]

def ring_y(k, bottom, top, tier):
    return [top, top - 0.09, bottom + (top - bottom) * 0.58, bottom + (0.14 if tier == 0 else 0.06), bottom - 0.03][k]

def hash01(x, z, k):
    h = (int(round(x * 1000)) * 73856093) ^ (int(round(z * 1000)) * 19349663) ^ (k * 83492791)
    h &= 0xFFFFFFFF
    h = (((h >> 16) ^ h) * 0x45d9f3b) & 0xFFFFFFFF
    h = (((h >> 16) ^ h) * 0x45d9f3b) & 0xFFFFFFFF
    h = (h >> 16) ^ h
    return (h & 0xFFFF) / 65535.0

def chamfer_distance(g):
    """Distance (in cells) from each rock cell to the nearest non-rock cell, 8-neighbour chamfer (1, 1.4)."""
    W, D = g.shape
    INF = 1e9
    d = np.where(g, INF, 0.0)
    for _ in range(2):
        for x in range(W):
            for z in range(D):
                if not g[x, z]: continue
                for dx, dz, w in ((-1, 0, 1), (0, -1, 1), (-1, -1, 1.4), (1, -1, 1.4)):
                    X, Z = x + dx, z + dz
                    nd = (d[X, Z] if 0 <= X < W and 0 <= Z < D else 0.0) + w
                    if nd < d[x, z]: d[x, z] = nd
        for x in range(W - 1, -1, -1):
            for z in range(D - 1, -1, -1):
                if not g[x, z]: continue
                for dx, dz, w in ((1, 0, 1), (0, 1, 1), (1, 1, 1.4), (-1, 1, 1.4)):
                    X, Z = x + dx, z + dz
                    nd = (d[X, Z] if 0 <= X < W and 0 <= Z < D else 0.0) + w
                    if nd < d[x, z]: d[x, z] = nd
    return d

def tier_masks(rock, count=3, step=1.6, wobble=0.9):
    """Tier k = cells whose edge distance exceeds k*step plus a smooth per-cell wobble,
    so terraces vary in width instead of tracing perfect contour lines."""
    dist = chamfer_distance(rock)
    out = [rock]
    W, D = rock.shape
    for k in range(1, count):
        m = np.zeros_like(rock)
        for x in range(W):
            for z in range(D):
                if rock[x, z]:
                    n = (math.sin(x * 0.9 + k * 1.7) + math.sin(z * 0.7 - k * 2.3) + math.sin((x + z) * 0.45 + k)) / 3
                    m[x, z] = dist[x, z] > k * step + n * wobble
        m &= out[-1]
        out.append(m)
    return out

def erode8(g):
    W, D = g.shape; out = np.zeros_like(g)
    for x in range(W):
        for z in range(D):
            if not g[x, z]: continue
            ok = True
            for dx in (-1, 0, 1):
                for dz in (-1, 0, 1):
                    X, Z = x + dx, z + dz
                    if not (0 <= X < W and 0 <= Z < D and g[X, Z]): ok = False
            out[x, z] = ok
    return out

class Field:
    """Blurred occupancy sampled at cell centres; bilinear between them."""
    def __init__(self, g):
        self.W, self.D = g.shape
        G = np.pad(g.astype(float), 2)
        self.v = 0.6 * G[1:-1, 1:-1] + 0.1 * (G[:-2, 1:-1] + G[2:, 1:-1] + G[1:-1, :-2] + G[1:-1, 2:])
        # self.v[i+1, j+1] is the sample at cell (i, j) centre; index range i in [-1, W]
    def s(self, i, j):
        return self.v[i + 1, j + 1] if -1 <= i <= self.W and -1 <= j <= self.D else 0.0
    def at(self, x, z):
        fx, fz = x - 0.5, z - 0.5
        i, j = math.floor(fx), math.floor(fz); tx, tz = fx - i, fz - j
        a, b, c, d = self.s(i, j), self.s(i + 1, j), self.s(i + 1, j + 1), self.s(i, j + 1)
        return a * (1 - tx) * (1 - tz) + b * tx * (1 - tz) + c * tx * tz + d * (1 - tx) * tz
    def outward(self, x, z):
        e = 0.05
        gx = self.at(x + e, z) - self.at(x - e, z); gz = self.at(x, z + e) - self.at(x, z - e)
        L = math.hypot(gx, gz) or 1.0
        return -gx / L, -gz / L

def march(field):
    """Yields (cap_polys, segments). Polygons are CCW lists of (x, z); segments (A, B) have inside on the left."""
    polys, segs = [], []
    for i in range(-1, field.W):
        for j in range(-1, field.D):
            P = [(i + .5, j + .5), (i + 1.5, j + .5), (i + 1.5, j + 1.5), (i + .5, j + 1.5)]
            V = [field.s(i, j), field.s(i + 1, j), field.s(i + 1, j + 1), field.s(i, j + 1)]
            ins = [v > ISO for v in V]
            n = sum(ins)
            if n == 0: continue
            def cross(k):
                a, b = k, (k + 1) % 4
                t = (ISO - V[a]) / (V[b] - V[a])
                return (P[a][0] + (P[b][0] - P[a][0]) * t, P[a][1] + (P[b][1] - P[a][1]) * t)
            saddle = n == 2 and ins[0] == ins[2]
            if saddle and sum(V) / 4 <= ISO:   # separated: one triangle per inside corner
                for k in range(4):
                    if ins[k]:
                        poly = [P[k], cross(k), cross((k - 1) % 4)]
                        polys.append(poly); segs.append((poly[1], poly[2]))
                continue
            poly, kinds = [], []
            for k in range(4):
                if ins[k]: poly.append(P[k]); kinds.append(0)
                if ins[k] != ins[(k + 1) % 4]: poly.append(cross(k)); kinds.append(1)
            polys.append(poly)
            m = len(poly)
            for a in range(m):
                b = (a + 1) % m
                if kinds[a] == 1 and kinds[b] == 1 and n < 4:
                    segs.append((poly[a], poly[b]))
    return polys, segs

def build_massif(rock):
    """Returns ({material: [tri,...]}, tiers, fields). tri = 3 (x, y, z) tuples."""
    out = {k: [] for k in MATS}
    tiers = tier_masks(rock, len(TIER_H))
    fields = [Field(t) for t in tiers]
    bottom = 0.0
    for k, (g, f) in enumerate(zip(tiers, fields)):
        if not g.any(): break
        top = bottom + TIER_H[k]
        nxt = fields[k + 1] if k + 1 < len(fields) and tiers[k + 1].any() else None
        polys, segs = march(f)
        crust = f'crust{k}'
        for poly in polys:
            if nxt is not None and all(nxt.at(x, z) > ISO + 0.15 for (x, z) in poly):
                continue  # fully under the next tier
            for a in range(1, len(poly) - 1):
                p0, p1, p2 = poly[0], poly[a], poly[a + 1]
                out[crust].append(((p0[0], top, p0[1]), (p2[0], top, p2[1]), (p1[0], top, p1[1])))
        for A, B in segs:
            rings = []
            for r, (off, jo, jy) in enumerate(RINGS):
                ring = []
                for (px, pz) in (A, B):
                    nx, nz = f.outward(px, pz)
                    o = off + (hash01(px, pz, r + 20 * k) * 2 - 1) * jo
                    y = ring_y(r, bottom, top, k) + (hash01(px, pz, r + 20 * k + 10) * 2 - 1) * jy * TIER_H[k]
                    ring.append((px + nx * o, y, pz + nz * o))
                rings.append(ring)
            mats = [crust, 'face', 'face', 'base']
            for r in range(len(RINGS) - 1):
                a, b = rings[r][0], rings[r][1]; c, d = rings[r + 1][1], rings[r + 1][0]
                out[mats[r]].append((a, c, d)); out[mats[r]].append((a, b, c))
        bottom = top
    return out, tiers, fields

def boulder(seed, r, crust='crust0'):
    rng = np.random.default_rng(seed)
    s = trimesh.creation.icosphere(subdivisions=1, radius=r)
    v = s.vertices.copy()
    v *= rng.uniform(0.82, 1.18, size=(len(v), 1))
    v[:, 1] *= rng.uniform(0.55, 0.8); v[:, 0] *= rng.uniform(0.9, 1.2)
    v[:, 1] = np.maximum(v[:, 1], -0.05 * r)
    v[:, 1] += r * 0.25
    s = trimesh.Trimesh(v, s.faces, process=False)
    s.apply_transform(trimesh.transformations.rotation_matrix(rng.uniform(0, 6.28), [0, 1, 0]))
    top = s.face_normals[:, 1] > 0.72
    parts = {}
    for m, mask in ((crust, top), ('face', ~top)):
        if mask.any():
            sub = trimesh.Trimesh(s.vertices, s.faces[mask], process=False); sub.unmerge_vertices(); parts[m] = sub
    return parts

def dressing(tiers, fields):
    """(x, y, z, radius, variant, crust) for base boulders and cap crumble."""
    out = []
    polys, segs = march(fields[0])
    for A, B in segs:
        mx, mz = (A[0] + B[0]) / 2, (A[1] + B[1]) / 2
        if hash01(mx, mz, 99) > 0.30: continue
        nx, nz = fields[0].outward(mx, mz)
        o = 0.5 + hash01(mx, mz, 98) * 0.3
        r = [0.14, 0.2, 0.28][int(hash01(mx, mz, 97) * 2.999)]
        out.append((mx + nx * o, 0.0, mz + nz * o, r, int(hash01(mx, mz, 96) * 9), 'crust0'))
    bottom = 0.0
    for k, g in enumerate(tiers):
        if not g.any(): break
        top = bottom + TIER_H[k]
        nxt = tiers[k + 1] if k + 1 < len(tiers) else None
        W, D = g.shape
        for x in range(W):
            for z in range(D):
                if g[x, z] and (nxt is None or not nxt[x, z]) and hash01(x, z, 50 + k) < 0.16:
                    jx, jz = hash01(x, z, 51) - .5, hash01(x, z, 52) - .5
                    out.append((x + .5 + jx * .5, top - 0.02, z + .5 + jz * .5, 0.09 + 0.06 * hash01(x, z, 53), int(hash01(x, z, 54) * 9), f'crust{k}'))
        bottom = top
    return out

def soup_to_mesh(tris):
    v = np.array([p for t in tris for p in t], dtype=float)
    return trimesh.Trimesh(v, np.arange(len(v)).reshape(-1, 3), process=False)

def export_scene(name, nodes):
    sc = trimesh.Scene(base_frame=name)
    for node, (M4, parts) in nodes.items():
        sc.graph.update(frame_from=name, frame_to=node, matrix=M4)
        for m, mesh in parts.items():
            mesh = mesh.copy(); mesh.visual = trimesh.visual.TextureVisuals(material=MATS[m])
            nm = f'{node}__{MATS[m].name}'
            sc.add_geometry(mesh, geom_name=nm, node_name=nm, parent_node_name=node)
    os.makedirs(OUT, exist_ok=True)
    sc.export(os.path.join(OUT, f'{name}.glb'))

def demo_grid(W=40, D=34):
    g = np.zeros((W, D), bool)
    blobs = [(28, 26, 5.5), (31, 23, 4), (22, 19, 3.2), (27, 11, 4.6), (30, 9, 3.4), (24, 12, 3), (3, 6, 2.6), (36, 30, 2.2), (12, 28, 1.2)]
    for x in range(W):
        for z in range(D):
            for (bx, bz, r) in blobs:
                d = math.hypot(x + .5 - bx, z + .5 - bz)
                wob = 0.7 * math.sin(math.atan2(z - bz, x - bx) * 3 + bx)
                if d < r + wob: g[x, z] = True
    g[8, 8] = True  # single blocked tile -> small butte
    return g

if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT): os.remove(os.path.join(OUT, f))
    keys = []
    for si, (sz, r) in enumerate((('s', .16), ('m', .26), ('l', .4))):
        for v in range(3):
            key = f'boulder_{sz}{v + 1}'
            export_scene(key, {'body': (np.eye(4), boulder(100 * si + v, r))}); keys.append(key)
    rock = demo_grid()
    soup, tiers, fields = build_massif(rock)
    nodes = {'massif': (np.eye(4), {m: soup_to_mesh(t) for m, t in soup.items() if t})}
    for i, (x, y, z, r, var, crust) in enumerate(dressing(tiers, fields)):
        nodes[f'rock_{i}'] = (trimesh.transformations.translation_matrix([x, y, z]), boulder(1000 + var, r, crust))
    export_scene('demo_massif', nodes)
    json.dump({'width': rock.shape[0], 'depth': rock.shape[1], 'rock': rock.astype(int).tolist()}, open(os.path.join(OUT, 'demo_rock_grid.json'), 'w'))
    print('boulders', len(keys), 'massif tris', sum(len(t) for t in soup.values()), 'tiers', [int(t.sum()) for t in tiers], 'dressing', len(nodes) - 1)
