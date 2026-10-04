#!/usr/bin/env python3
"""Pezz v2 vehicle models: artillery, long_range_artillery and mining_truck (issues #3 and #4).

Writes glTF 2.0 binaries straight from numpy (no trimesh or Blender needed) into the game's model folder, plus a
manifest (triangles, team-mask share, nodes, bounds) next to this script. Optional preview renders (a small software
rasteriser, the game's default camera) are for iterating; the real check is the Unity build.

  python3 art-src/tools/build_units_v2.py                  # write the three .glb files and units_v2.json
  python3 art-src/tools/build_units_v2.py --preview /tmp/p  # also render stills (hero, poses, stream scale)

The asset contract (docs/art/ASSETS.md):
  - 1 tile = 1 unit, +Y up, +Z forward, pivot at ground centre.
  - Functional nodes: turret (yaws), barrel (child of turret, recoils along its own -Z; artillery pitched -60 deg about X),
    spinner (the cutter, axis X), bin (tips about its rear bottom edge), bin_ore (child of bin, scaled in Y by the load;
    material M_Ore_*, recoloured per ore by Models.TintOre). New: spade_l / spade_r (artillery stabilisers, hinged at
    the hull's rear; WorldView raises them for travel and plants them when the gun deploys).
  - Geometry nodes are <parent>__<material>. M_Team is the team mask (exported Blueberry blue), M_E_* glow.
"""
import argparse
import json
import math
import os
import struct

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
OUT = os.path.join(REPO, "unity", "Assets", "Pez", "Resources", "PezModels")


# ---------------------------------------------------------------- palette (docs/art/palette/pez_palette.json)
def lin(v):  # glTF colour factors are linear; the palette is sRGB
    v = v / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def hexrgb(h):
    h = h.lstrip("#")
    return [int(h[i:i + 2], 16) for i in (0, 2, 4)]


# name: (glTF material name, sRGB hex, metallic, roughness, emissive)
MATS = {
    "team": ("M_Team", "#2E73FF", 0.0, 0.18, False),
    "cream": ("M_CreamPlastic", "#ECE4D2", 0.0, 0.22, False),
    "smoke": ("M_SmokePlastic", "#4A4F57", 0.0, 0.28, False),
    "steel": ("M_SpringSteel", "#8E979F", 0.7, 0.4, False),
    "kraft": ("M_Kraft", "#A9845A", 0.0, 0.85, False),
    "licorice": ("M_Licorice", "#1E1B1D", 0.0, 0.45, False),
    "dark": ("M_Dark", "#2A2E33", 0.0, 0.55, False),
    "bone": ("M_Bone", "#F6F2E8", 0.0, 0.3, False),
    "ore": ("M_Ore_Cinnamon", "#8A3A24", 0.0, 0.7, False),
}


# ---------------------------------------------------------------- geometry
def rx(d):
    a = math.radians(d); c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def ry(d):
    a = math.radians(d); c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def rz(d):
    a = math.radians(d); c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


class Mesh:
    def __init__(self, v, f):
        self.v = np.asarray(v, float).reshape(-1, 3)
        self.f = np.asarray(f, int).reshape(-1, 3)

    def xf(self, R=None, t=(0, 0, 0)):
        v = self.v if R is None else self.v @ np.asarray(R).T
        return Mesh(v + np.asarray(t, float), self.f.copy())

    def mirror_x(self):
        v = self.v * [-1, 1, 1]
        return Mesh(v, self.f[:, ::-1].copy())


def orient_convex(v, faces):
    """Wind every triangle of a convex solid outward (CCW seen from outside), using its centroid."""
    v = np.asarray(v, float); c = v.mean(0); out = []
    for a, b, d in faces:
        n = np.cross(v[b] - v[a], v[d] - v[a])
        out.append((a, b, d) if np.dot(n, (v[a] + v[b] + v[d]) / 3 - c) >= 0 else (a, d, b))
    return Mesh(v, out)


def hexa(c):
    """A convex hexahedron from 8 corners: bottom face 0-3, top face 4-7, both in the same order around."""
    q = [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    f = []
    for a, b, d, e in q:
        f += [(a, b, d), (a, d, e)]
    return orient_convex(c, f)


def box(sx, sy, sz, x=0, y=0, z=0, R=None):
    hx, hy, hz = sx / 2, sy / 2, sz / 2
    c = [(-hx, -hy, -hz), (hx, -hy, -hz), (hx, -hy, hz), (-hx, -hy, hz),
         (-hx, hy, -hz), (hx, hy, -hz), (hx, hy, hz), (-hx, hy, hz)]
    return hexa(c).xf(R, (x, y, z))


def span(x0, x1, y0, y1, z0, z1):
    return box(x1 - x0, y1 - y0, z1 - z0, (x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)


def beam(p0, p1, w, h, up=(0, 1, 0)):
    """A rectangular bar from p0 to p1 (w across, h along `up` projected off the bar's axis)."""
    p0, p1 = np.asarray(p0, float), np.asarray(p1, float)
    ax = p1 - p0; L = np.linalg.norm(ax); ax /= L
    u = np.asarray(up, float); u = u - ax * np.dot(u, ax); u /= np.linalg.norm(u)
    s = np.cross(u, ax)
    R = np.stack([s, u, ax], 1)
    return box(w, h, L, R=R).xf(None, (p0 + p1) / 2)


def frustum(r0, r1, length, seg=16, axis="z", caps=True, phase=0.0):
    """A cylinder or cone frustum along +axis from 0 to length (radius r0 at 0, r1 at the end)."""
    a = np.linspace(0, 2 * math.pi, seg, endpoint=False) + phase
    ring0 = np.stack([np.cos(a) * r0, np.sin(a) * r0, np.zeros(seg)], 1)
    ring1 = np.stack([np.cos(a) * r1, np.sin(a) * r1, np.full(seg, length)], 1)
    v = list(ring0) + list(ring1)
    f = []
    for i in range(seg):
        j = (i + 1) % seg
        f += [(i, j, seg + j), (i, seg + j, seg + i)]
    if caps:
        c0, c1 = len(v), len(v) + 1
        v += [(0, 0, 0), (0, 0, length)]
        for i in range(seg):
            j = (i + 1) % seg
            f += [(c0, j, i), (c1, seg + i, seg + j)]
    m = orient_convex(v, f)
    if axis == "x":
        m = m.xf(ry(90))
    elif axis == "y":
        m = m.xf(rx(-90))
    return m


def cyl(r, length, x=0, y=0, z=0, axis="z", seg=16, centered=True, r1=None, phase=0.0):
    m = frustum(r, r if r1 is None else r1, length, seg, "z", True, phase)
    if centered:
        m = m.xf(None, (0, 0, -length / 2))
    if axis == "x":
        m = m.xf(ry(90))
    elif axis == "y":
        m = m.xf(rx(-90))
    return m.xf(None, (x, y, z))


def ear_clip(poly):
    """Triangulate a simple polygon (list of 2D points, CCW). Returns index triples."""
    idx = list(range(len(poly)))
    P = np.asarray(poly, float)
    tris = []

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    def inside(p, a, b, c):
        return cross(a, b, p) >= -1e-12 and cross(b, c, p) >= -1e-12 and cross(c, a, p) >= -1e-12

    guard = 0
    while len(idx) > 3 and guard < 10000:
        guard += 1
        for k in range(len(idx)):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % len(idx)]
            a, b, c = P[i0], P[i1], P[i2]
            if cross(a, b, c) <= 1e-12:
                continue
            if any(inside(P[j], a, b, c) for j in idx if j not in (i0, i1, i2)):
                continue
            tris.append((i0, i1, i2)); idx.pop(k); break
    tris.append(tuple(idx))
    return tris


def extrude_x(profile, x0, x1):
    """Extrude a simple polygon given in the (z, y) plane along X from x0 to x1 (concave profiles are fine)."""
    p = [tuple(map(float, q)) for q in profile]
    area = sum(p[i][0] * p[(i + 1) % len(p)][1] - p[(i + 1) % len(p)][0] * p[i][1] for i in range(len(p)))
    if area < 0:
        p = p[::-1]  # CCW in (z, y)
    n = len(p)
    v = [(x0, y, z) for z, y in p] + [(x1, y, z) for z, y in p]
    f = []
    for a, b, c in ear_clip(p):
        # (z, y) CCW: the normal of (z,y) CCW points to -X in (x,y,z) right-handed space -> that's the x0 cap.
        f.append((a, b, c))
        f.append((n + a, n + c, n + b))
    for i in range(n):
        j = (i + 1) % n
        f += [(i, n + i, n + j), (i, n + j, j)]
    m = Mesh(v, f)
    # Check the winding once (signed volume must be positive) and flip everything if needed.
    vv = m.v[m.f]
    vol = np.einsum("ij,ij->i", vv[:, 0], np.cross(vv[:, 1], vv[:, 2])).sum()
    if vol < 0:
        m.f = m.f[:, ::-1]
    return m


def grid_heap(nx, nz, hx, hz, height, edge, seed, rough=0.035):
    """A closed, faceted heap: a jittered height grid over [-hx,hx]x[-hz,hz], skirts down to y=0, and a floor."""
    rng = np.random.default_rng(seed)
    xs = np.linspace(-hx, hx, nx); zs = np.linspace(-hz, hz, nz)
    V = np.zeros((nz, nx, 3))
    for j, z in enumerate(zs):
        for i, x in enumerate(xs):
            u, w = abs(x) / hx, abs(z) / hz
            k = (1 - u ** 2.2) * (1 - w ** 2.2)
            h = edge + (height - edge) * k
            border = i in (0, nx - 1) or j in (0, nz - 1)
            jx = 0 if i in (0, nx - 1) else rng.uniform(-0.3, 0.3) * (2 * hx / (nx - 1))
            jz = 0 if j in (0, nz - 1) else rng.uniform(-0.3, 0.3) * (2 * hz / (nz - 1))
            V[j, i] = (x + jx, max(0.02, h + (0 if border else rng.uniform(-rough, rough))), z + jz)
    v = list(V.reshape(-1, 3)); f = []
    for j in range(nz - 1):
        for i in range(nx - 1):
            a, b, c, d = j * nx + i, j * nx + i + 1, (j + 1) * nx + i + 1, (j + 1) * nx + i
            if (i + j) % 2:
                f += [(a, d, c), (a, c, b)]
            else:
                f += [(a, d, b), (b, d, c)]
    ring = [j * nx + 0 for j in range(nz)][::-1] + [i for i in range(nx)] + \
           [j * nx + nx - 1 for j in range(nz)] + [(nz - 1) * nx + i for i in range(nx)][::-1]
    ring2 = []
    for r in ring:
        if not ring2 or ring2[-1] != r:
            ring2.append(r)
    if ring2[0] == ring2[-1]:
        ring2.pop()
    base0 = len(v)
    for r in ring2:
        p = v[r]; v.append(np.array([p[0], 0.0, p[2]]))
    m = len(ring2)
    for k in range(m):
        a, b = ring2[k], ring2[(k + 1) % m]
        a0, b0 = base0 + k, base0 + (k + 1) % m
        f += [(a, a0, b0), (a, b0, b)]
    fl = len(v); v.append(np.array([0.0, 0.0, 0.0]))
    for k in range(m):
        f.append((fl, base0 + (k + 1) % m, base0 + k))
    mesh = Mesh(v, f)
    # Wind outward: the heap is star-shaped about a point above the floor centre.
    c = np.array([0, height * 0.35, 0])
    vv = mesh.v[mesh.f]
    n = np.cross(vv[:, 1] - vv[:, 0], vv[:, 2] - vv[:, 0])
    flip = np.einsum("ij,ij->i", n, vv.mean(1) - c) < 0
    mesh.f[flip] = mesh.f[flip][:, ::-1]
    return mesh


def chunk(r, seed):
    """A small faceted rock (an irregular octahedron-ish lump)."""
    rng = np.random.default_rng(seed)
    v = np.array([[1, 0, 0], [-1, 0, 0], [0, 1, 0], [0, -0.6, 0], [0, 0, 1], [0, 0, -1],
                  [0.6, 0.55, 0.6], [-0.6, 0.5, -0.6]], float)
    v *= rng.uniform(0.75, 1.15, (len(v), 1)) * r
    # Convex hull by brute force would be overkill: use the octahedron faces plus the two capping lumps.
    f = [(2, 4, 0), (2, 1, 4), (2, 5, 1), (2, 0, 5), (3, 0, 4), (3, 4, 1), (3, 1, 5), (3, 5, 0)]
    return orient_convex(v[:6], f)


# ---------------------------------------------------------------- model + glTF writer
class Model:
    def __init__(self, key):
        self.key = key
        self.nodes = {}       # name -> (parent or None, translation, rotation quaternion xyzw)
        self.order = []
        self.parts = []       # (node or None for the root, material key, Mesh)

    def node(self, name, parent=None, t=(0, 0, 0), R=None):
        q = quat(R) if R is not None else (0.0, 0.0, 0.0, 1.0)
        self.nodes[name] = (parent, tuple(map(float, t)), q, R if R is not None else np.eye(3))
        self.order.append(name)
        return name

    def add(self, mat, mesh, node=None):
        self.parts.append((node, mat, mesh))

    def world(self, node):
        """4x4 rest transform of a node (glTF space)."""
        M = np.eye(4)
        chain = []
        while node is not None:
            chain.append(node); node = self.nodes[node][0]
        for n in reversed(chain):
            _, t, _, R = self.nodes[n]
            L = np.eye(4); L[:3, :3] = R; L[:3, 3] = t
            M = M @ L
        return M

    def groups(self):
        g = {}
        for node, mat, mesh in self.parts:
            g.setdefault((node, mat), []).append(mesh)
        out = []
        for (node, mat), meshes in g.items():
            v, f, o = [], [], 0
            for m in meshes:
                v.append(m.v); f.append(m.f + o); o += len(m.v)
            out.append((node, mat, Mesh(np.concatenate(v), np.concatenate(f))))
        return out

    def stats(self):
        tris = 0; area = {}; lo = np.full(3, 1e9); hi = -lo
        for node, mat, mesh in self.groups():
            M = self.world(node) if node else np.eye(4)
            w = mesh.v @ M[:3, :3].T + M[:3, 3]
            tris += len(mesh.f)
            vv = w[mesh.f]
            a = 0.5 * np.linalg.norm(np.cross(vv[:, 1] - vv[:, 0], vv[:, 2] - vv[:, 0]), axis=1).sum()
            area[mat] = area.get(mat, 0) + a
            lo = np.minimum(lo, w.min(0)); hi = np.maximum(hi, w.max(0))
        return tris, area, lo, hi

    def write(self, path):
        groups = self.groups()
        mat_keys = []
        for _, m, _ in groups:
            if m not in mat_keys:
                mat_keys.append(m)
        materials = []
        for m in mat_keys:
            name, hx, metal, rough, emissive = MATS[m]
            rgb = [lin(c) for c in hexrgb(hx)]
            mm = {"name": name, "pbrMetallicRoughness": {"baseColorFactor": rgb + [1.0], "metallicFactor": metal,
                                                         "roughnessFactor": rough}}
            if emissive:
                mm["emissiveFactor"] = rgb
            materials.append(mm)
        nodes, meshes, accessors, views = [], [], [], []
        blob = bytearray()

        def push(data, target):
            while len(blob) % 4:
                blob.append(0)
            off = len(blob); blob.extend(data)
            views.append({"buffer": 0, "byteOffset": off, "byteLength": len(data), "target": target})
            return len(views) - 1

        index = {}
        for name in self.order:
            parent, t, q, _ = self.nodes[name]
            nd = {"name": name}
            if any(abs(c) > 1e-9 for c in t):
                nd["translation"] = [round(c, 6) for c in t]
            if abs(q[3] - 1) > 1e-9:
                nd["rotation"] = [round(c, 8) for c in q]
            index[name] = len(nodes); nodes.append(nd)
        roots = [index[n] for n in self.order if self.nodes[n][0] is None]
        for node, m, mesh in groups:
            v = mesh.v.astype(np.float32)
            f = mesh.f.astype(np.uint16 if len(v) < 65536 else np.uint32)
            pv = push(v.tobytes(), 34962)
            accessors.append({"bufferView": pv, "componentType": 5126, "count": len(v), "type": "VEC3",
                              "min": v.min(0).tolist(), "max": v.max(0).tolist()})
            pa = len(accessors) - 1
            pi = push(f.tobytes(), 34963)
            accessors.append({"bufferView": pi, "componentType": 5123 if f.dtype == np.uint16 else 5125,
                              "count": int(f.size), "type": "SCALAR"})
            ia = len(accessors) - 1
            gname = f"{node or self.key}__{MATS[m][0]}"
            meshes.append({"name": gname, "primitives": [{"attributes": {"POSITION": pa}, "indices": ia,
                                                         "material": mat_keys.index(m)}]})
            nodes.append({"name": gname, "mesh": len(meshes) - 1})
            gi = len(nodes) - 1
            if node is None:
                roots.append(gi)
            else:
                nodes[index[node]].setdefault("children", []).append(gi)
        for name in self.order:
            parent = self.nodes[name][0]
            if parent is not None:
                nodes[index[parent]].setdefault("children", []).insert(0, index[name])
        while len(blob) % 4:
            blob.append(0)
        gltf = {"asset": {"version": "2.0", "generator": "pezz art-src/tools/build_units_v2.py"},
                "scene": 0, "scenes": [{"name": self.key, "nodes": roots}], "nodes": nodes, "meshes": meshes,
                "materials": materials, "accessors": accessors, "bufferViews": views,
                "buffers": [{"byteLength": len(blob)}]}
        js = json.dumps(gltf, separators=(",", ":")).encode()
        while len(js) % 4:
            js += b" "
        out = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob))
        out += struct.pack("<II", len(js), 0x4E4F534A) + js
        out += struct.pack("<II", len(blob), 0x004E4942) + bytes(blob)
        with open(path, "wb") as fh:
            fh.write(out)


def quat(R):
    R = np.asarray(R, float)
    tr = R[0, 0] + R[1, 1] + R[2, 2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        w, x, y, z = 0.25 * s, (R[2, 1] - R[1, 2]) / s, (R[0, 2] - R[2, 0]) / s, (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = math.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2
        w, x, y, z = (R[2, 1] - R[1, 2]) / s, 0.25 * s, (R[0, 1] + R[1, 0]) / s, (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = math.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2
        w, x, y, z = (R[0, 2] - R[2, 0]) / s, (R[0, 1] + R[1, 0]) / s, 0.25 * s, (R[1, 2] + R[2, 1]) / s
    else:
        s = math.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2
        w, x, y, z = (R[1, 0] - R[0, 1]) / s, (R[0, 2] + R[2, 0]) / s, (R[1, 2] + R[2, 1]) / s, 0.25 * s
    return (x, y, z, w)


# The artillery's travel elevation (deg, view-only): WorldView pitches the cradle and barrel nodes from the -60 deg
# firing rest pose down by 57 deg to this, onto the bow's travel lock.
STOW_PITCH = -3.0


# ---------------------------------------------------------------- shared running gear
def track_pod(a, L, x0, x1, h=0.19, wheels=5, wr=0.058, node=None, grousers=True):
    """A licorice track pod (side profile with raked ends), road wheels on the outer face, grousers on the top run."""
    hl = L / 2
    prof = [(-hl + 0.07, 0.0), (hl - 0.07, 0.0), (hl, h * 0.42), (hl - 0.025, h * 0.78), (hl - 0.07, h),
            (-hl + 0.07, h), (-hl + 0.025, h * 0.78), (-hl, h * 0.42)]
    a.add("licorice", extrude_x(prof, x0, x1), node)
    outer = x1 if abs(x1) > abs(x0) else x0
    sgn = 1 if outer > 0 else -1
    # Road wheels with steel hubs, and the bigger sprocket and idler at the ends.
    zs = np.linspace(-hl + 0.16, hl - 0.16, wheels)
    for z in zs:
        a.add("dark", cyl(wr, 0.02, outer + sgn * 0.01, wr + 0.012, z, axis="x", seg=12), node)
        a.add("steel", cyl(wr * 0.45, 0.012, outer + sgn * 0.024, wr + 0.012, z, axis="x", seg=8), node)
    for z in (-hl + 0.07, hl - 0.07):
        a.add("dark", cyl(h * 0.36, 0.022, outer + sgn * 0.011, h * 0.45, z, axis="x", seg=12), node)
        a.add("steel", cyl(h * 0.16, 0.014, outer + sgn * 0.026, h * 0.45, z, axis="x", seg=8), node)
    if grousers:
        n = int(L / 0.075)
        for k in range(n):
            z = -hl + 0.09 + k * (L - 0.18) / (n - 1)
            a.add("licorice", span(min(x0, x1) - 0.004, max(x0, x1) + 0.004, h, h + 0.016, z - 0.016, z + 0.016), node)


# ---------------------------------------------------------------- artillery family
def artillery(key, L, W, barrel_len, lra):
    """
    Hero idea: a gun that happens to have a hull. A long, low tracked chassis whose rear is two splayed spades digging
    into the ground; on it, a squat turntable carrying an oversized recoil cradle (recuperators, buffer, trunnion cheeks)
    and a long gun with a muzzle brake. The team mask is the gun house, the cradle's jacket, a band (two on the
    long-range gun) near the muzzle and a full-length stripe on each fender, so the colour sits where a high camera sees
    it. Ammunition rides on the rear deck. The rest pose is the firing pose (gun at -60 deg, spades planted); the view
    stows the gun (the cradle and barrel nodes pitch down 57 deg, onto the bow's travel lock) and folds the spades up
    for travel, so parked, moving and dug-in read apart on the stream.
    long_range_artillery: a sibling, not a scale-up: a longer hull on seven road wheels, a far longer gun (bore
    evacuator, triple-baffle brake, two bands), bigger spades and a second ammo rack.
    """
    a = Model(key)
    hl = L / 2
    tw = 0.135 if not lra else 0.145   # track width
    xo = W / 2                         # track outer face
    xi = xo - tw
    for s in (-1, 1):
        track_pod(a, L, s * xi, s * xo, h=0.18, wheels=5 if not lra else 7)
    # Lower hull between the tracks, then the upper deck over them (fenders), with a raked bow glacis.
    a.add("smoke", span(-xi, xi, 0.05, 0.2, -hl + 0.05, hl - 0.06))
    deck_y0, deck_y1 = 0.19, 0.25
    fx = xo + 0.012
    a.add("smoke", hexa([(-fx, deck_y0, -hl + 0.02), (fx, deck_y0, -hl + 0.02), (fx, deck_y0, hl - 0.01), (-fx, deck_y0, hl - 0.01),
                         (-fx, deck_y1, -hl + 0.03), (fx, deck_y1, -hl + 0.03), (fx, deck_y1, hl - 0.13), (-fx, deck_y1, hl - 0.13)]))
    # Team stripes the full length of both fenders: the hull's outline carries the colour at stream scale.
    for s in (-1, 1):
        x0, x1 = s * (fx - 0.12), s * (fx + 0.004)
        a.add("team", span(min(x0, x1), max(x0, x1), deck_y1, deck_y1 + 0.012, -hl + 0.05, hl - 0.14))
    # The gun's geometry, needed by the bow's travel lock: turret ring, trunnion, tube radii.
    tz = -0.1 if not lra else -0.05            # turret ring centre
    tr = np.array([0.0, 0.205, 0.05])          # trunnion (barrel pivot) in turret space
    Lb = barrel_len
    rb0, rb1 = (0.052, 0.04) if not lra else (0.055, 0.041)   # a fat tube: it has to read as a line at 45 px a tile

    def r_at(z):
        return rb0 + (rb1 - rb0) * z / (Lb - 0.1)
    # Bow: the exhaust, and the travel lock the stowed gun (STOW_PITCH) rests in: two struts and a U cradle.
    lock_z = hl - 0.1
    dz = lock_z - (tz + tr[2])
    axis_y = deck_y1 + tr[1] + dz * math.tan(math.radians(-STOW_PITCH))
    rr = r_at(dz)
    seat = axis_y - rr - 0.003
    for s in (-1, 1):
        a.add("steel", beam((s * 0.1, deck_y1, lock_z + 0.04), (s * 0.04, seat - 0.02, lock_z), 0.024, 0.024))
    a.add("steel", span(-rr - 0.035, rr + 0.035, seat - 0.028, seat, lock_z - 0.02, lock_z + 0.02))
    for s in (-1, 1):
        x0, x1 = s * (rr + 0.006), s * (rr + 0.034)
        a.add("steel", span(min(x0, x1), max(x0, x1), seat - 0.005, axis_y + 0.01, lock_z - 0.02, lock_z + 0.02))
    a.add("steel", cyl(0.028, 0.12, -xo + 0.07, deck_y1 + 0.06, hl - 0.33, axis="y", seg=10))
    a.add("dark", cyl(0.032, 0.02, -xo + 0.07, deck_y1 + 0.125, hl - 0.33, axis="y", seg=10))
    # One fender toolbox, on the right (restraint: the hull is the quiet part).
    a.add("smoke", span(xo - 0.12, xo - 0.01, deck_y1, deck_y1 + 0.05, hl - 0.42, hl - 0.24))
    # Rear deck: the ammunition rack(s), shells lying across, kraft cases with steel noses.
    racks = [(-hl + 0.05, -hl + 0.19)] if not lra else [(-hl + 0.05, -hl + 0.19), (-hl + 0.21, -hl + 0.33)]
    for z0, z1 in racks:
        a.add("kraft", span(-0.21, 0.21, deck_y1, deck_y1 + 0.05, z0, z1))
        n = 4
        for k in range(n):
            z = z0 + 0.025 + k * (z1 - z0 - 0.05) / (n - 1)
            a.add("kraft", cyl(0.02, 0.24, -0.02, deck_y1 + 0.072, z, axis="x", seg=8))
            a.add("steel", cyl(0.02, 0.07, 0.135, deck_y1 + 0.072, z, axis="x", seg=8, centered=False, r1=0.004))
    # Rear plate with the spade hinges.
    a.add("dark", span(-xi + 0.02, xi - 0.02, 0.08, deck_y1, -hl - 0.005, -hl + 0.04))

    # Spades: hinged at the rear plate, splayed outward, the blade's toothed lower edge on the ground at rest
    # (deployed); WorldView raises them for travel.
    sp_len = 0.27 if not lra else 0.40
    blade_w = 0.2 if not lra else 0.28
    hinge_y = 0.17
    for s, name in ((1, "spade_l"), (-1, "spade_r")):
        n = a.node(name, None, (s * 0.15, hinge_y, -hl - 0.01), ry(-s * 22))
        drop = hinge_y
        tip = np.array([0, -drop + 0.02, -sp_len])
        for ox in (-0.045, 0.045):
            a.add("steel", beam((ox, 0.0, 0.0), (ox * 1.6, tip[1] + 0.06, tip[2] + 0.03), 0.03, 0.035), n)
        a.add("dark", cyl(0.025, 0.14, 0, 0, 0, axis="x", seg=10), n)
        # The blade: a broad plate, leaning back, its lower edge toothed.
        bz = tip[2]
        a.add("steel", hexa([(-blade_w / 2, -drop + 0.03, bz + 0.0), (blade_w / 2, -drop + 0.03, bz + 0.0),
                             (blade_w / 2, -drop + 0.03, bz + 0.03), (-blade_w / 2, -drop + 0.03, bz + 0.03),
                             (-blade_w / 2, -drop + 0.16, bz + 0.07), (blade_w / 2, -drop + 0.16, bz + 0.07),
                             (blade_w / 2, -drop + 0.16, bz + 0.1), (-blade_w / 2, -drop + 0.16, bz + 0.1)]), n)
        for k in range(4):
            x = -blade_w / 2 + 0.025 + k * (blade_w - 0.05) / 3
            a.add("steel", hexa([(x - 0.02, -drop + 0.03, bz), (x + 0.02, -drop + 0.03, bz), (x + 0.02, -drop + 0.03, bz + 0.03),
                                 (x - 0.02, -drop + 0.03, bz + 0.03), (x - 0.006, -drop, bz - 0.01), (x + 0.006, -drop, bz - 0.01),
                                 (x + 0.006, -drop, bz + 0.005), (x - 0.006, -drop, bz + 0.005)]), n)
        # Its hydraulic ram, from high on the rear plate to the blade's back.
        a.add("steel", beam((0, 0.06, 0.03), (0, -drop + 0.13, bz + 0.1), 0.026, 0.026), n)
        a.add("dark", beam((0, 0.062, 0.028), (0, -drop + 0.11, bz * 0.45), 0.034, 0.034), n)

    # Turret: a low turntable and gun house, team roof wings either side of the gun slot.
    tu = a.node("turret", None, (0, deck_y1, tz))
    a.add("steel", cyl(0.2, 0.03, 0, 0.015, 0, axis="y", seg=20), tu)
    gw0, gw1, gl0, gl1, gh = 0.44, 0.38, 0.46, 0.38, 0.12
    a.add("smoke", hexa([(-gw0 / 2, 0.02, -gl0 / 2), (gw0 / 2, 0.02, -gl0 / 2), (gw0 / 2, 0.02, gl0 / 2 - 0.04), (-gw0 / 2, 0.02, gl0 / 2 - 0.04),
                         (-gw1 / 2, gh, -gl1 / 2), (gw1 / 2, gh, -gl1 / 2), (gw1 / 2, gh, gl1 / 2 - 0.08), (-gw1 / 2, gh, gl1 / 2 - 0.08)]), tu)
    slot = 0.075
    for s in (-1, 1):
        x0, x1 = s * slot, s * (gw1 / 2 + 0.006)
        a.add("team", span(min(x0, x1), max(x0, x1), gh, gh + 0.022, -gl1 / 2 - 0.006, gl1 / 2 - 0.07), tu)
    a.add("dark", span(-slot + 0.005, slot - 0.005, gh - 0.01, gh + 0.004, -gl1 / 2 + 0.02, gl1 / 2 - 0.1), tu)
    # Rear bustle with a hatch, and the loader's shell tray on the turret's left.
    a.add("smoke", span(-0.15, 0.15, 0.03, gh - 0.015, -gl0 / 2 - 0.07, -gl0 / 2 + 0.01), tu)
    a.add("dark", cyl(0.045, 0.012, 0.11, gh + 0.026, -0.08, axis="y", seg=10), tu)
    # Trunnion: cheeks either side of the gun, a pin through them.
    for s in (-1, 1):
        a.add("steel", hexa([(s * 0.07, gh, -0.1), (s * 0.115, gh, -0.1), (s * 0.115, gh, 0.12), (s * 0.07, gh, 0.12),
                             (s * 0.07, tr[1] + 0.05, tr[2] - 0.04), (s * 0.115, tr[1] + 0.05, tr[2] - 0.04),
                             (s * 0.115, tr[1] + 0.05, tr[2] + 0.06), (s * 0.07, tr[1] + 0.05, tr[2] + 0.06)]), tu)
    a.add("dark", cyl(0.035, 0.27, *tr, axis="x", seg=12), tu)
    # The cradle (its own node on the trunnion, pitched with the gun; the view stows it with the barrel): the jacket
    # the barrel slides in, two recuperators above it and a buffer below, end caps. The barrel recoils 0.2 into it.
    pitch = rx(-60)
    cn = a.node("cradle", tu, tuple(tr), pitch)
    cl = 0.42 if not lra else 0.5
    a.add("team", span(-0.062, 0.062, -0.055, 0.055, -0.1, cl - 0.12), cn)
    a.add("dark", span(-0.07, 0.07, -0.062, 0.062, cl - 0.12, cl - 0.07), cn)
    for ox in (-0.04, 0.04):
        a.add("steel", cyl(0.028, cl, ox, 0.085, cl / 2 - 0.08, axis="z", seg=12), cn)
        a.add("dark", cyl(0.032, 0.03, ox, 0.085, cl - 0.08, axis="z", seg=12), cn)
    a.add("steel", cyl(0.03, cl * 0.8, 0, -0.085, cl * 0.4 - 0.08, axis="z", seg=12), cn)
    a.add("dark", span(-0.03, 0.03, -0.12, -0.055, -0.06, 0.02), cn)

    # The gun: breech block behind the trunnion, a tapering tube, team band(s), a muzzle brake. Symmetric about its
    # axis, so Models.BarrelTip finds the muzzle at the middle of its far end.
    br = a.node("barrel", tu, tuple(tr), pitch)
    a.add("dark", span(-0.058, 0.058, -0.052, 0.052, -0.12, 0.0), br)
    a.add("steel", span(-0.045, 0.045, -0.045, 0.045, -0.15, -0.12), br)
    a.add("steel", cyl(rb0, Lb - 0.1, 0, 0, 0, axis="z", seg=14, centered=False, r1=rb1), br)
    bands = [Lb * 0.72] if not lra else [Lb * 0.66, Lb * 0.78]
    for z in bands:
        a.add("team", cyl(r_at(z) + 0.012, 0.075, 0, 0, z, axis="z", seg=14), br)
    if lra:
        z = Lb * 0.42
        a.add("steel", cyl(r_at(z) + 0.022, 0.12, 0, 0, z, axis="z", seg=14), br)   # bore evacuator
        a.add("dark", cyl(r_at(z) + 0.024, 0.016, 0, 0, z - 0.06, axis="z", seg=14), br)
        a.add("dark", cyl(r_at(z) + 0.024, 0.016, 0, 0, z + 0.06, axis="z", seg=14), br)
    # Muzzle brake: baffles with ports between them (a dark core shows through the ports).
    mb = 0.12 if not lra else 0.16
    a.add("dark", span(-0.032, 0.032, -0.03, 0.03, Lb - mb - 0.01, Lb - 0.004), br)
    nb = 2 if not lra else 3
    bl = 0.03
    for k in range(nb):
        z1 = Lb - k * (mb - bl) / (nb - 1)
        a.add("steel", span(-0.066, 0.066, -0.05, 0.05, z1 - bl, z1), br)
    a.add("dark", cyl(0.024, 0.004, 0, 0, Lb + 0.001, axis="z", seg=10), br)
    return a


# ---------------------------------------------------------------- mining truck
def mining_truck():
    """
    Hero idea: a brick hauler. An oversized open hopper (twice the cab's footprint, deep enough to heap ore above its
    rim) on two chunky track pods; a small cream cab tucked forward-left (it shares the refinery's cream), the engine
    and exhaust stack on the right; a full-width toothed cutter drum on short arms at the bow. The hopper's outer skins
    are the team mask, ribbed in steel; inside it's dark, so an empty truck shows a dark pit and a laden one shows
    its ore (tinted per ore type by the view). The bin hinges at its rear bottom edge over a raked tail spout.
    """
    a = Model("mining_truck")
    L, W = 1.0, 0.68
    hl = L / 2
    tw = 0.16
    xo = W / 2; xi = xo - tw
    for s in (-1, 1):
        track_pod(a, L - 0.02, s * xi, s * xo, h=0.2, wheels=4, wr=0.066)
    # Chassis: lower body and two rails the hopper rests on; the hinge brackets at the tail.
    a.add("smoke", span(-xi, xi, 0.06, 0.2, -hl + 0.04, hl - 0.05))
    a.add("smoke", span(-xo - 0.01, xo + 0.01, 0.19, 0.225, hl - 0.3, hl - 0.04))     # fender deck at the bow
    for s in (-1, 1):
        a.add("steel", span(s * 0.11 - 0.025, s * 0.11 + 0.025, 0.2, 0.24, -hl + 0.04, hl - 0.3))
        a.add("dark", span(s * 0.17 - 0.02, s * 0.17 + 0.02, 0.17, 0.27, -hl + 0.04, -hl + 0.12))
    # Fender over each track behind the bow deck (the hopper overhangs them).
    for s in (-1, 1):
        a.add("smoke", span(min(s * xi, s * (xo + 0.01)), max(s * xi, s * (xo + 0.01)), 0.2, 0.218, -hl + 0.02, hl - 0.3))
        # A team stripe on each fender's outer edge (the same hull-outline stripe as the artillery's: world DNA).
        x0, x1 = s * (xo - 0.05), s * (xo + 0.014)
        a.add("team", span(min(x0, x1), max(x0, x1), 0.218, 0.226, -hl + 0.02, hl - 0.04))

    # Cab: small, forward-left (+X is the model's left), cream like the refinery, team roof, dark glazing.
    cx0, cx1, cz0, cz1 = 0.07, xo + 0.005, hl - 0.27, hl - 0.04
    cy0, cy1 = 0.225, 0.47
    a.add("cream", hexa([(cx0, cy0, cz0), (cx1, cy0, cz0), (cx1, cy0, cz1), (cx0, cy0, cz1),
                         (cx0 + 0.01, cy1, cz0), (cx1 - 0.01, cy1, cz0), (cx1 - 0.01, cy1, cz1 - 0.06), (cx0 + 0.01, cy1, cz1 - 0.06)]))
    a.add("dark", hexa([(cx0 + 0.02, cy0 + 0.12, cz1 + 0.002), (cx1 - 0.02, cy0 + 0.12, cz1 + 0.002),
                        (cx1 - 0.02, cy0 + 0.12, cz1 + 0.012), (cx0 + 0.02, cy0 + 0.12, cz1 + 0.012),
                        (cx0 + 0.025, cy1 - 0.025, cz1 - 0.06 + 0.004), (cx1 - 0.025, cy1 - 0.025, cz1 - 0.06 + 0.004),
                        (cx1 - 0.025, cy1 - 0.025, cz1 - 0.05), (cx0 + 0.025, cy1 - 0.025, cz1 - 0.05)]))
    for s_x in (cx1 + 0.002, cx0 - 0.002):
        a.add("dark", span(min(s_x, s_x + (0.008 if s_x > cx0 else -0.008)), max(s_x, s_x + (0.008 if s_x > cx0 else -0.008)),
                           cy0 + 0.12, cy1 - 0.03, cz0 + 0.04, cz1 - 0.08))
    a.add("team", span(cx0 - 0.005, cx1 + 0.012, cy1, cy1 + 0.025, cz0 - 0.01, cz1 - 0.04))
    a.add("dark", span(cx0 + 0.04, cx0 + 0.07, cy1 + 0.025, cy1 + 0.06, cz0 + 0.04, cz0 + 0.07))   # beacon housing
    # Engine on the right: a smoke block, dark grille, a tall exhaust stack (it reads in silhouette).
    ex0, ex1 = -xo - 0.005, 0.03
    a.add("team", span(ex0, ex1, 0.225, 0.37, hl - 0.28, hl - 0.03))
    for k in range(3):
        y = 0.25 + k * 0.04
        a.add("dark", span(ex0 + 0.03, ex1 - 0.03, y, y + 0.022, hl - 0.03, hl - 0.018))
    a.add("steel", cyl(0.03, 0.34, -0.23, 0.37 + 0.17, hl - 0.22, axis="y", seg=10))
    a.add("dark", cyl(0.036, 0.03, -0.23, 0.37 + 0.33, hl - 0.22, axis="y", seg=10))
    # A boarding ladder up the engine block's front (hero-distance detail).
    for s in (-1, 1):
        a.add("steel", span(-0.2 + s * 0.04 - 0.006, -0.2 + s * 0.04 + 0.006, 0.05, 0.37, hl - 0.012, hl))
    for k in range(4):
        y = 0.09 + k * 0.075
        a.add("steel", span(-0.245, -0.155, y, y + 0.01, hl - 0.012, hl - 0.002))
    # Cutter arms (static): from the bow to the drum's axle ends, with a hydraulic ram each.
    drum_z, drum_y = hl + 0.08, 0.15
    for s in (-1, 1):
        x = s * (xi + 0.035)
        a.add("steel", beam((x, 0.16, hl - 0.06), (x, drum_y, drum_z), 0.035, 0.05))
        a.add("dark", beam((x * 0.55, 0.22, hl - 0.03), (x * 0.8, drum_y + 0.03, drum_z - 0.05), 0.026, 0.026))

    # The cutter drum (spinner, axis X): dark drum, steel end rings, staggered steel teeth so the spin reads.
    sp = a.node("spinner", None, (0, drum_y, drum_z))
    dl = 2 * xi + 0.04
    a.add("dark", cyl(0.095, dl, axis="x", seg=14), sp)
    for s in (-1, 1):
        a.add("steel", cyl(0.112, 0.024, s * (dl / 2 - 0.012), axis="x", seg=14), sp)
        a.add("steel", cyl(0.04, 0.05, s * (dl / 2 + 0.02), axis="x", seg=10), sp)
    rings, per = 6, 6
    for i in range(rings):
        x = -dl / 2 + 0.06 + i * (dl - 0.12) / (rings - 1)
        for k in range(per):
            ang = 2 * math.pi * k / per + i * (math.pi / per) * 0.9
            d = np.array([0, math.sin(ang), math.cos(ang)])
            t = np.array([0, math.cos(ang), -math.sin(ang)])
            base = d * 0.09
            # A wedge tooth leaning against the spin (axis X).
            c = [(x - 0.018, *(base[1:] - t[1:] * 0.022)), (x + 0.018, *(base[1:] - t[1:] * 0.022)),
                 (x + 0.018, *(base[1:] + t[1:] * 0.022)), (x - 0.018, *(base[1:] + t[1:] * 0.022))]
            tip = d * 0.145 - t * 0.02
            c2 = [(x - 0.008, tip[1] - t[1] * 0.006, tip[2] - t[2] * 0.006), (x + 0.008, tip[1] - t[1] * 0.006, tip[2] - t[2] * 0.006),
                  (x + 0.008, tip[1] + t[1] * 0.006, tip[2] + t[2] * 0.006), (x - 0.008, tip[1] + t[1] * 0.006, tip[2] + t[2] * 0.006)]
            a.add("steel", hexa(c + c2), sp)

    # The hopper (bin): hinge on its rear bottom edge, the bed tips nose-up about it. Flared walls, dark inside, team
    # skins outside with steel ribs, a cream top rail, a raked tail spout over the hinge, a short canopy lip at the front.
    hinge = (0.0, 0.24, -hl + 0.1)
    bn = a.node("bin", None, hinge)
    blen = 0.64                     # floor from z=0 to blen (bin space)
    fw, tw2 = 0.25, 0.32            # half widths: floor, top
    H = 0.31                        # wall height
    th = 0.028
    a.add("dark", span(-fw, fw, 0.0, 0.03, 0.0, blen), bn)                                   # floor
    a.add("dark", hexa([(-fw, 0.0, -0.075), (fw, 0.0, -0.075), (fw, 0.0, 0.0), (-fw, 0.0, 0.0),
                        (-fw - 0.01, 0.13, -0.11), (fw + 0.01, 0.13, -0.11), (fw, 0.03, 0.02), (-fw, 0.03, 0.02)]), bn)  # tail spout
    for s in (-1, 1):
        # Side wall slab (dark), then the team skin on its outer face.
        def wall_pt(y, z, off):
            x = fw + (tw2 - fw) * (y / H) + off
            return (s * x, y, z)
        rear_z = lambda y: -0.11 + 0.0 * y
        c = [wall_pt(0.0, -0.075, -th), wall_pt(0.0, -0.075, 0), wall_pt(0.0, blen, 0), wall_pt(0.0, blen, -th),
             wall_pt(H, -0.11, -th), wall_pt(H, -0.11, 0), wall_pt(H, blen + 0.03, 0), wall_pt(H, blen + 0.03, -th)]
        a.add("dark", hexa(c), bn)
        sk = [wall_pt(0.02, -0.06, 0.0), wall_pt(0.02, -0.06, 0.008), wall_pt(0.02, blen - 0.01, 0.008), wall_pt(0.02, blen - 0.01, 0.0),
              wall_pt(H - 0.03, -0.095, 0.0), wall_pt(H - 0.03, -0.095, 0.008), wall_pt(H - 0.03, blen + 0.02, 0.008), wall_pt(H - 0.03, blen + 0.02, 0.0)]
        a.add("team", hexa(sk), bn)
        # Ribs: three steel stiffeners over the skin, and the top rail.
        for z in (0.12, 0.32, 0.52):
            r = [wall_pt(0.0, z - 0.016, 0.0), wall_pt(0.0, z - 0.016, 0.026), wall_pt(0.0, z + 0.016, 0.026), wall_pt(0.0, z + 0.016, 0.0),
                 wall_pt(H, z - 0.016, 0.0), wall_pt(H, z - 0.016, 0.026), wall_pt(H, z + 0.016, 0.026), wall_pt(H, z + 0.016, 0.0)]
            a.add("steel", hexa(r), bn)
        # The top rail is a licorice keyline, so the load never merges with the team colour below it.
        rail = [wall_pt(H - 0.02, -0.11, -th - 0.006), wall_pt(H - 0.02, -0.11, 0.03), wall_pt(H - 0.02, blen + 0.04, 0.03), wall_pt(H - 0.02, blen + 0.04, -th - 0.006),
                wall_pt(H + 0.028, -0.115, -th - 0.006), wall_pt(H + 0.028, -0.115, 0.03), wall_pt(H + 0.028, blen + 0.045, 0.03), wall_pt(H + 0.028, blen + 0.045, -th - 0.006)]
        a.add("licorice", hexa(rail), bn)
    # Front wall (raked forward) with a short canopy lip, and its outer face's rib.
    fz0, fz1 = blen, blen + 0.03
    a.add("dark", hexa([(-fw, 0.0, fz0 - th), (fw, 0.0, fz0 - th), (fw, 0.0, fz0), (-fw, 0.0, fz0),
                        (-tw2, H, fz1 - th), (tw2, H, fz1 - th), (tw2, H, fz1), (-tw2, H, fz1)]), bn)
    a.add("team", hexa([(-fw, 0.02, fz0), (fw, 0.02, fz0), (fw, 0.02, fz0 + 0.01), (-fw, 0.02, fz0 + 0.01),
                         (-tw2, H - 0.02, fz1), (tw2, H - 0.02, fz1), (tw2, H - 0.02, fz1 + 0.01), (-tw2, H - 0.02, fz1 + 0.01)]), bn)
    # A short canopy lip over the cab (team), its front edge a cream strip (the refinery's cream).
    a.add("team", hexa([(-tw2 - 0.02, H - 0.01, fz1 - th - 0.005), (tw2 + 0.02, H - 0.01, fz1 - th - 0.005),
                        (tw2 + 0.02, H - 0.01, fz1 + 0.02), (-tw2 - 0.02, H - 0.01, fz1 + 0.02),
                        (-tw2 - 0.02, H + 0.022, fz1 - th - 0.005), (tw2 + 0.02, H + 0.022, fz1 - th - 0.005),
                        (tw2 + 0.02, H + 0.024, fz1 + 0.02), (-tw2 - 0.02, H + 0.024, fz1 + 0.02)]), bn)
    a.add("cream", span(-tw2 - 0.02, tw2 + 0.02, H - 0.01, H + 0.024, fz1 + 0.02, fz1 + 0.04), bn)
    # Hinge pins and a crossmember under the floor.
    a.add("steel", cyl(0.026, 2 * fw - 0.06, 0, 0.0, 0.0, axis="x", seg=10), bn)
    a.add("smoke", span(-fw + 0.03, fw - 0.03, -0.02, 0.0, blen * 0.55, blen * 0.62), bn)

    # The load (bin_ore, scaled in Y by the view): a faceted heap that crowns above the rim when full, plus lumps.
    # Its origin sits inside the floor slab (top at 0.03), so an empty bin (Y scale ~0) hides the flattened heap under
    # the floor instead of leaving an ore-coloured film on it.
    ore = a.node("bin_ore", bn, (0, 0.006, blen / 2 + 0.01))
    hz = blen / 2 - 0.035
    a.add("ore", grid_heap(7, 9, fw + 0.005, hz, 0.385, 0.145, seed=7), ore)
    rng = np.random.default_rng(11)
    for k in range(9):
        x = rng.uniform(-0.13, 0.13); z = rng.uniform(-hz * 0.7, hz * 0.7)
        u, w = abs(x) / (fw + 0.005), abs(z) / hz
        y = 0.145 + (0.385 - 0.145) * (1 - u ** 2.2) * (1 - w ** 2.2)
        a.add("ore", chunk(rng.uniform(0.035, 0.05), 100 + k).xf(ry(rng.uniform(0, 180)) @ rx(rng.uniform(-20, 20)), (x, y - 0.005, z)), ore)
    return a


BUILDERS = {
    "artillery": lambda: artillery("artillery", L=1.1, W=0.66, barrel_len=1.36, lra=False),
    "long_range_artillery": lambda: artillery("long_range_artillery", L=1.4, W=0.7, barrel_len=1.85, lra=True),
    "mining_truck": mining_truck,
}


# ---------------------------------------------------------------- preview renderer (software, the game's camera)
SRGB = {k: np.array(hexrgb(v[1]), float) / 255 for k, v in MATS.items()}
GROUND = np.array(hexrgb("#7D6E55"), float) / 255


def posed_triangles(model, pose=None, yaw=0.0):
    """World-space triangles (in Unity's frame: glTF X negated) with their material keys, a rest pose plus overrides.
    pose: {node: 3x3 rotation applied after the node's rest rotation (Unity's localRotation = rest * extra)}."""
    pose = pose or {}
    cache = {}

    def M(node):
        if node is None:
            return np.eye(4)
        if node in cache:
            return cache[node]
        parent, t, _, R = model.nodes[node]
        Lm = np.eye(4); Lm[:3, :3] = R @ pose.get(node, np.eye(3)); Lm[:3, 3] = t
        cache[node] = M(parent) @ Lm
        return cache[node]
    tris, mats = [], []
    Y = np.eye(4); Y[:3, :3] = ry(yaw)
    for node, mat, mesh in model.groups():
        W4 = Y @ M(node)
        w = mesh.v @ W4[:3, :3].T + W4[:3, 3]
        w = w * [-1, 1, 1]   # glTF -> Unity (glTFast negates X)
        tris.append(w[mesh.f][:, [0, 2, 1]]); mats += [mat] * len(mesh.f)
    return np.concatenate(tris), mats


def render(scene, px_per_tile, size, center=(0, 0.3, 0), cam_yaw=45, cam_pitch=55, team_rgb=None, ore_rgb=None, bg=None,
           shadows=True, ids=None):
    """scene: list of (triangles, mats, offset). Orthographic, the game's default view. Returns an HxWx3 uint8 image.
    ids: optional dict, filled with 'mat' (HxW array of material keys, '' for ground) for coverage measurements."""
    W, H = size
    p, yw = math.radians(cam_pitch), math.radians(cam_yaw)
    f = np.array([math.sin(yw) * math.cos(p), -math.sin(p), math.cos(yw) * math.cos(p)])
    r = np.array([math.cos(yw), 0, -math.sin(yw)])
    u = np.array([math.sin(p) * math.sin(yw), math.cos(p), math.sin(p) * math.cos(yw)])
    light = np.array([-0.35, 0.85, -0.25]); light /= np.linalg.norm(light)   # Unity space, from the upper left
    img = np.zeros((H, W, 3)); img[:] = GROUND if bg is None else bg
    zb = np.full((H, W), np.inf)
    matbuf = np.full((H, W), "", dtype=object)
    c = np.asarray(center, float)
    # Ground shadows (a crude projected shadow along the light, darkening the ground).
    shadow = np.zeros((H, W), bool)
    for tris, mats, off in scene:
        T = tris + np.asarray(off, float)
        for shadow_pass in ((True, False) if shadows else (False,)):
            if shadow_pass:
                d = -light
                k = (T[..., 1] / -d[1])[..., None]
                P = T + k * d
                P[..., 1] = 0.0
            else:
                P = T
            sx = ((P - c) @ r) * px_per_tile + W / 2
            sy = H / 2 - ((P - c) @ u) * px_per_tile
            sz = (P - c) @ f
            for i in range(len(T)):
                xs, ys = sx[i], sy[i]
                x0, x1 = int(max(0, math.floor(xs.min()))), int(min(W - 1, math.ceil(xs.max())))
                y0, y1 = int(max(0, math.floor(ys.min()))), int(min(H - 1, math.ceil(ys.max())))
                if x0 > x1 or y0 > y1:
                    continue
                gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
                (ax, bx, cx), (ay, by, cy) = xs, ys
                den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
                if abs(den) < 1e-12:
                    continue
                l1 = ((by - cy) * (gx - cx) + (cx - bx) * (gy - cy)) / den
                l2 = ((cy - ay) * (gx - cx) + (ax - cx) * (gy - cy)) / den
                l3 = 1 - l1 - l2
                m = (l1 >= -1e-6) & (l2 >= -1e-6) & (l3 >= -1e-6)
                if not m.any():
                    continue
                if shadow_pass:
                    shadow[y0:y1 + 1, x0:x1 + 1] |= m
                    continue
                z = l1 * sz[i][0] + l2 * sz[i][1] + l3 * sz[i][2]
                sub = zb[y0:y1 + 1, x0:x1 + 1]
                m &= z < sub
                if not m.any():
                    continue
                n = np.cross(T[i][1] - T[i][0], T[i][2] - T[i][0])
                nn = np.linalg.norm(n)
                n = n / nn if nn > 0 else n
                if np.dot(n, f) > 0:
                    n = -n
                base = SRGB[mats[i]]
                if mats[i] == "team" and team_rgb is not None:
                    base = team_rgb
                if mats[i] == "ore" and ore_rgb is not None:
                    base = ore_rgb
                shade = 0.42 + 0.68 * max(0.0, float(np.dot(n, light)))
                col = np.clip(base * shade, 0, 1)
                sub[m] = z[m]
                img[y0:y1 + 1, x0:x1 + 1][m] = col
                matbuf[y0:y1 + 1, x0:x1 + 1][m] = mats[i]
    if ids is not None:
        ids["mat"] = matbuf
    sh = shadow & np.isinf(zb)
    img[sh] *= 0.62
    return (np.clip(img, 0, 1) * 255).astype(np.uint8)


def visible_share(model, mat="team"):
    """Share of the model's on-screen pixels in a material, from the game's camera, averaged over 8 headings (the
    team mask's real read: what a high camera sees, not hidden faces)."""
    hit = tot = 0
    for yaw in range(0, 360, 45):
        t, mt = posed_triangles(model, yaw=yaw)
        ids = {}
        render([(t, mt, (0, 0, 0))], 60, (140, 140), center=(0, 0.4, 0), shadows=False, ids=ids)
        b = ids["mat"]
        tot += int((b != "").sum()); hit += int((b == mat).sum())
    return hit / max(tot, 1)


def load_glb_triangles(path):
    """Triangles of a shipped .glb (any node tree, matrices or TRS) in Unity's frame, materials mapped to MATS keys."""
    b = open(path, "rb").read()
    off = 12; js = None; bin_ = None
    while off < len(b):
        ln, ty = struct.unpack("<II", b[off:off + 8]); ch = b[off + 8:off + 8 + ln]
        if ty == 0x4E4F534A:
            js = json.loads(ch)
        else:
            bin_ = ch
        off += 8 + ln
    names = {v[0]: k for k, v in MATS.items()}

    def acc(i):
        a = js["accessors"][i]; bv = js["bufferViews"][a["bufferView"]]
        n = {"SCALAR": 1, "VEC3": 3}[a["type"]]
        dt = {5126: np.float32, 5123: np.uint16, 5125: np.uint32}[a["componentType"]]
        o = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
        x = np.frombuffer(bin_, dtype=dt, count=a["count"] * n, offset=o)
        return x.reshape(-1, n) if n > 1 else x

    def m4(nd):
        if "matrix" in nd:
            return np.array(nd["matrix"]).reshape(4, 4).T
        x, y, z, w = nd.get("rotation", [0, 0, 0, 1])
        R = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                      [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                      [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        M = np.eye(4); M[:3, :3] = R * np.array(nd.get("scale", [1, 1, 1])); M[:3, 3] = nd.get("translation", [0, 0, 0])
        return M
    tris, mats = [], []

    def walk(i, P):
        nd = js["nodes"][i]; M = P @ m4(nd)
        if "mesh" in nd:
            for pr in js["meshes"][nd["mesh"]]["primitives"]:
                v = acc(pr["attributes"]["POSITION"]).astype(float)
                idx = acc(pr["indices"]).reshape(-1, 3)
                w = v @ M[:3, :3].T + M[:3, 3]
                w = w * [-1, 1, 1]
                tris.append(w[idx][:, [0, 2, 1]])
                mats.extend([names.get(js["materials"][pr["material"]]["name"], "smoke")] * len(idx))
        for c in nd.get("children", []):
            walk(c, M)
    for r in js["scenes"][js.get("scene", 0)]["nodes"]:
        walk(r, np.eye(4))
    return np.concatenate(tris), mats


def previews(models, outdir, before_dir):
    from PIL import Image
    os.makedirs(outdir, exist_ok=True)
    ore_rgb = {"iron": np.array(hexrgb("#8A3A24")) / 255, "copper": np.array(hexrgb("#C8742F")) / 255,
               "crystal": np.array(hexrgb("#8FE4FF")) / 255, "uranium": np.array(hexrgb("#B6FF3B")) / 255}
    red = np.array(hexrgb("#F22E1F")) / 255
    # Hero stills: each model at four headings, rest pose, Blueberry and Cherry.
    for key, m in models.items():
        row = []
        for yaw in (0, 90, 180, 270):
            t, mm = posed_triangles(m, yaw=yaw)
            row.append(render([(t, mm, (0, 0, 0))], 230, (420, 420), center=(0, 0.45, 0)))
        Image.fromarray(np.concatenate(row, 1)).save(os.path.join(outdir, f"{key}_hero.png"))
    # Poses: the truck tipping (35 deg) and parked (12 deg), loads per ore; artillery travel (spades up).
    if "mining_truck" in models:
        m = models["mining_truck"]
        tiles = []
        for name, pose, ore in (("empty", {}, None), ("full iron", {}, "iron"), ("tipped copper", {"bin": rx(-35)}, "copper"),
                                ("parked", {"bin": rx(-12)}, None), ("crystal", {}, "crystal"), ("uranium", {}, "uranium")):
            mm = Model(m.key); mm.nodes = dict(m.nodes); mm.order = m.order
            mm.parts = [(n, mat, msh) for n, mat, msh in m.parts if not (mat == "ore" and ore is None)]
            if ore is not None and name.startswith("tipped"):
                mm.parts = [(n, mat, msh.xf(np.diag([1, 0.6, 1])) if mat == "ore" else msh) for n, mat, msh in mm.parts]
            t, mt = posed_triangles(mm, pose, yaw=200)
            tiles.append(render([(t, mt, (0, 0, 0))], 230, (380, 380), center=(0, 0.4, 0), ore_rgb=ore_rgb.get(ore)))
        Image.fromarray(np.concatenate(tiles, 1)).save(os.path.join(outdir, "mining_truck_states.png"))
    for key in ("artillery", "long_range_artillery"):
        if key in models:
            m = models[key]
            tiles = []
            for pose in ({}, {"spade_l": rx(32), "spade_r": rx(32)}):
                t, mt = posed_triangles(m, pose, yaw=-60)
                tiles.append(render([(t, mt, (0, 0, 0))], 200, (440, 440), center=(0, 0.55, 0)))
            Image.fromarray(np.concatenate(tiles, 1)).save(os.path.join(outdir, f"{key}_poses.png"))
    # Stream scale (40 px per tile, JPEG q60): before (shipped) vs after, at four headings, Blueberry and Cherry.
    from io import BytesIO
    rows = []
    for key in ("artillery", "long_range_artillery", "mining_truck"):
        for label, src in (("before", os.path.join(before_dir, f"{'artillery' if key == 'long_range_artillery' else key}.glb")), ("after", None)):
            if src is not None and not os.path.exists(src):
                continue
            scene = []
            for i, yaw in enumerate((0, 90, 180, 270, 45, 225)):
                if src:
                    t, mt = load_glb_triangles(src)
                    R = ry(-yaw)  # Unity frame
                    t = t @ R.T
                else:
                    t, mt = posed_triangles(models[key], yaw=yaw)
                k = i * 1.6 - 4.0
                scene.append((t, mt, (k * math.cos(math.radians(45)), 0, -k * math.sin(math.radians(45)))))
            team = None
            img_b = render(scene, 40, (400, 90), center=(0, 0.3, 0), team_rgb=team, shadows=False)
            img_r = render(scene, 40, (400, 90), center=(0, 0.3, 0), team_rgb=red, shadows=False,
                           ore_rgb=ore_rgb["copper"] if key == "mining_truck" else None)
            rows.append(np.concatenate([img_b, img_r], 1))
    sheet = Image.fromarray(np.concatenate(rows, 0))
    buf = BytesIO(); sheet.save(buf, "JPEG", quality=60)
    open(os.path.join(outdir, "stream_scale_40px_q60.jpg"), "wb").write(buf.getvalue())
    Image.open(BytesIO(buf.getvalue())).resize((sheet.width * 3, sheet.height * 3), Image.NEAREST).save(
        os.path.join(outdir, "stream_scale_40px_q60_x3.png"))


# ---------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", default="", help="also render preview stills into this folder")
    ap.add_argument("--before", default="", help="folder of the shipped .glb files to compare against (preview)")
    ap.add_argument("--only", default="", help="comma-separated keys")
    ap.add_argument("--no-write", action="store_true")
    a = ap.parse_args()
    keys = [k for k in BUILDERS if not a.only or k in a.only.split(",")]
    models = {k: BUILDERS[k]() for k in keys}
    manifest = []
    for k, m in models.items():
        tris, area, lo, hi = m.stats()
        tot = sum(area.values())
        entry = {"key": k, "file": f"unity/Assets/Pez/Resources/PezModels/{k}.glb", "tris": int(tris),
                 "team_share_area": round(area.get("team", 0) / tot, 3),
                 "team_share_visible": round(visible_share(m), 3),
                 "material_share": {MATS[mk][0]: round(v / tot, 3) for mk, v in sorted(area.items(), key=lambda kv: -kv[1])},
                 "nodes": [n for n in m.order], "bounds": [[round(float(x), 3) for x in lo], [round(float(x), 3) for x in hi]]}
        manifest.append(entry)
        print(f"{k:22s} {tris:5d} tris  team {entry['team_share_area'] * 100:4.1f}% of area, {entry['team_share_visible'] * 100:4.1f}% seen  bounds {entry['bounds']}")
        if not a.no_write:
            m.write(os.path.join(OUT, f"{k}.glb"))
    if not a.no_write and not a.only:
        with open(os.path.join(HERE, "units_v2.json"), "w") as fh:
            json.dump(manifest, fh, indent=1)
    if a.preview:
        previews(models, a.preview, a.before or OUT)


if __name__ == "__main__":
    main()
