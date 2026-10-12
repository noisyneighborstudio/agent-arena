"""Round the corners of Pezz models: Pez are rounded, so nothing should have a sharp edge.

    python art-src/tools/round_models.py [--only key,key] [--radius 0.22] [--max-radius 0.05] [--in DIR] [--out DIR]

Works on finished .glb files (default: the shipped ones in unity/Assets/Pez/Resources/PezModels, in place), so it covers
every model whatever built it. build_units_v2.py runs it on what it writes; art handed off as .glb files (build_models.py's pack,
the v0.4 deep-mining models) gets it when copied into PezModels. Each mesh is split into its parts (connected pieces of geometry). A convex part is
replaced by its rounded version: the part shrunk by r (every face plane moved in) with a sphere of radius r swept over
it, i.e. the convex hull of small direction fans at the shrunk corners. Faces stay exactly flat and every vertex gets an
exact normal, so the edges shade as smooth fillets. r is a share of the part's thinnest dimension, capped, so a slab and
a tiny lamp both read as rounded without the slab going soft. Parts that aren't convex keep their shape and get normals
smoothed across edges under 35 degrees. Nodes, names and materials are untouched. Idempotent enough: a part already
rounded is convex and gets a much smaller r the second time, so don't run it twice on purpose; it skips models whose
generator tag says they're rounded.
"""
import argparse, glob, json, os, struct
import numpy as np
import trimesh
from scipy.spatial import ConvexHull, HalfspaceIntersection

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SHIPPED = os.path.join(REPO, "unity", "Assets", "Pez", "Resources", "PezModels")
TAG = "rounded by art-src/tools/round_models.py"
# The house rounding (the owner picked it from side-by-side renders, 2026-10-11): 32% of a part's thinnest side, at most 0.08 tiles.
RADIUS, MAX_RADIUS, MIN_RADIUS = 0.32, 0.08, 0.006


# ---------------------------------------------------------------- glb in / out

def read_glb(path):
    b = open(path, "rb").read()
    jl = struct.unpack_from("<I", b, 12)[0]
    gltf = json.loads(b[20:20 + jl])
    off = 20 + jl
    bl = struct.unpack_from("<I", b, off)[0]
    blob = b[off + 8:off + 8 + bl]
    return gltf, blob


def accessor(gltf, blob, i):
    a = gltf["accessors"][i]
    v = gltf["bufferViews"][a["bufferView"]]
    dt = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}[a["componentType"]]
    n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
    start = v.get("byteOffset", 0) + a.get("byteOffset", 0)
    stride = v.get("byteStride", 0)
    if stride and stride != n * np.dtype(dt).itemsize:
        raw = np.frombuffer(blob, np.uint8, count=stride * a["count"], offset=start).reshape(a["count"], stride)
        return raw[:, :n * np.dtype(dt).itemsize].copy().view(dt).reshape(a["count"], n)
    return np.frombuffer(blob, dt, count=a["count"] * n, offset=start).reshape(a["count"], n) if n > 1 else \
        np.frombuffer(blob, dt, count=a["count"], offset=start).copy()


def write_glb(path, gltf, prims):
    """Rewrite every primitive's geometry: prims[(mesh index, primitive index)] = (positions, normals, faces)."""
    blob = bytearray()
    views, accs = [], []

    def push(arr, target, ctype, typ, minmax=False):
        while len(blob) % 4:
            blob.append(0)
        data = arr.tobytes()
        off = len(blob); blob.extend(data)
        views.append({"buffer": 0, "byteOffset": off, "byteLength": len(data), "target": target})
        acc = {"bufferView": len(views) - 1, "componentType": ctype, "count": int(len(arr) if arr.ndim > 1 else arr.size), "type": typ}
        if minmax:
            acc["min"] = arr.min(0).tolist(); acc["max"] = arr.max(0).tolist()
        accs.append(acc)
        return len(accs) - 1

    for mi, mesh in enumerate(gltf["meshes"]):
        for pi, prim in enumerate(mesh["primitives"]):
            v, n, f = prims[(mi, pi)]
            v = v.astype(np.float32); n = n.astype(np.float32)
            f = f.astype(np.uint16 if len(v) < 65536 else np.uint32).reshape(-1)
            attrs = {"POSITION": push(v, 34962, 5126, "VEC3", True), "NORMAL": push(n, 34962, 5126, "VEC3")}
            prim["attributes"] = attrs
            prim["indices"] = push(f, 34963, 5123 if f.dtype == np.uint16 else 5125, "SCALAR")
    while len(blob) % 4:
        blob.append(0)
    gltf["accessors"], gltf["bufferViews"] = accs, views
    gltf["buffers"] = [{"byteLength": len(blob)}]
    gltf.setdefault("asset", {})["generator"] = (gltf["asset"].get("generator", "") + "; " + TAG).lstrip("; ")
    js = json.dumps(gltf, separators=(",", ":")).encode()
    while len(js) % 4:
        js += b" "
    out = struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(blob))
    out += struct.pack("<II", len(js), 0x4E4F534A) + js + struct.pack("<II", len(blob), 0x004E4942) + bytes(blob)
    open(path, "wb").write(out)


# ---------------------------------------------------------------- rounding

def planes_of(points):
    """The convex hull's distinct face planes (unit normal n, offset d with n.x <= d inside)."""
    h = ConvexHull(points)
    out = []
    for eq in h.equations:  # n.x + c <= 0 inside
        n, d = eq[:3], -eq[3]
        if not any(np.dot(n, m) > 1 - 1e-6 and abs(d - e) < 1e-6 for m, e in out):
            out.append((n, d))
    return out


def fan(normals, seg, sharp=0.9):
    """Directions covering the corner's normal cone: the faces' normals, arcs across each sharp edge (normals more than
    ~25 degrees apart; a cylinder's facets aren't an edge worth rounding), and the corner's middle where three meet."""
    ns = [np.asarray(n, float) for n in normals]
    dirs = list(ns)
    sharp_pairs = 0
    for i in range(len(ns)):
        for j in range(i + 1, len(ns)):
            if np.dot(ns[i], ns[j]) > sharp:
                continue
            sharp_pairs += 1
            for k in range(1, seg):
                t = k / seg
                dirs.append(ns[i] * (1 - t) + ns[j] * t)
    if len(ns) >= 3 and sharp_pairs >= 2:
        dirs.append(sum(ns))
    out = []
    for d in dirs:
        d = d / np.linalg.norm(d)
        if not any(np.dot(d, e) > 0.9995 for e in out):
            out.append(d)
    return out


def smooth_already(points, limit_deg=30):
    """A part whose neighbouring faces all meet at under ~30 degrees (a sphere, a dome) has no edge to round."""
    h = ConvexHull(points)
    eq = h.equations[:, :3]
    worst = 1.0
    for i, nb in enumerate(h.neighbors):
        for j in nb:
            c = float(np.dot(eq[i], eq[j]))
            if c < 1 - 1e-6:
                worst = min(worst, c)
    return worst > np.cos(np.radians(limit_deg))


def round_convex(points, r, seg):
    """Vertices, normals and faces of the convex hull of `points`, shrunk by r with a sphere of radius r swept over it."""
    planes = planes_of(points)
    hs = np.array([np.r_[n, -(d - r)] for n, d in planes])
    inner = HalfspaceIntersection(hs, points.mean(0))
    corners = []
    for p in inner.intersections:
        if not any(np.linalg.norm(p - q) < 1e-7 for q in corners):
            corners.append(p)
    P, N = [], []
    for c in corners:
        touching = [n for n, d in planes if abs(np.dot(n, c) - (d - r)) < 1e-5]
        for dvec in fan(touching, seg):
            P.append(c + dvec * r); N.append(dvec)
    P, N = np.array(P), np.array(N)
    h = ConvexHull(P)
    used = np.unique(h.simplices)
    remap = -np.ones(len(P), int); remap[used] = np.arange(len(used))
    faces = remap[h.simplices]
    # Orient every triangle outward.
    v = P[used]
    centre = v.mean(0)
    tri = v[faces]
    cross = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    flip = np.einsum("ij,ij->i", cross, tri.mean(1) - centre) < 0
    faces[flip] = faces[flip][:, ::-1]
    return v, N[used], faces


def smooth_normals(v, f, angle_deg=35):
    m = trimesh.Trimesh(v, f, process=False)
    s = trimesh.graph.smooth_shade(m, angle=np.radians(angle_deg), facet_minarea=None)
    return np.asarray(s.vertices), np.asarray(s.vertex_normals), np.asarray(s.faces)


def process_primitive(v, f, ratio, rmax, rmin, stats):
    m = trimesh.Trimesh(v, f, process=False)
    out_v, out_n, out_f = [], [], []
    base = 0
    for comp in m.split(only_watertight=False):
        cv, cf = np.asarray(comp.vertices), np.asarray(comp.faces)
        rounded = None
        if comp.is_watertight and len(cv) >= 4:
            try:
                hull_vol = ConvexHull(cv).volume
                convex = hull_vol > 0 and abs(comp.volume) / hull_vol > 0.995
            except Exception:
                convex = False
            if convex:
                thin = float(np.min(comp.bounding_box_oriented.primitive.extents))
                r = min(ratio * thin, rmax)
                # Already smooth (spheres, many-sided cylinders' sides): rounding only its sharp edges still helps
                # (a cylinder's rims), so the test is just whether r is visible at all.
                if r >= rmin and not smooth_already(cv):
                    try:
                        rounded = round_convex(cv, r, 3 if r >= 0.035 else 2 if r >= 0.012 else 1)  # tiny parts: one bevel, shaded round
                        stats["rounded"] += 1
                    except Exception as e:
                        stats["failed"] += 1
        if rounded is None:
            rounded = smooth_normals(cv, cf)
            stats["kept"] += 1
        rv, rn, rf = rounded
        out_v.append(rv); out_n.append(rn); out_f.append(rf + base); base += len(rv)
    return np.concatenate(out_v), np.concatenate(out_n), np.concatenate(out_f)


def round_file(src, dst, ratio, rmax, rmin):
    gltf, blob = read_glb(src)
    if TAG in gltf.get("asset", {}).get("generator", ""):
        return None
    prims, stats = {}, {"rounded": 0, "kept": 0, "failed": 0, "tris_before": 0, "tris_after": 0}
    for mi, mesh in enumerate(gltf["meshes"]):
        for pi, prim in enumerate(mesh["primitives"]):
            v = accessor(gltf, blob, prim["attributes"]["POSITION"]).astype(float)
            f = accessor(gltf, blob, prim["indices"]).astype(np.int64).reshape(-1, 3)
            stats["tris_before"] += len(f)
            pv, pn, pf = process_primitive(v, f, ratio, rmax, rmin, stats)
            stats["tris_after"] += len(pf)
            prims[(mi, pi)] = (pv, pn, pf)
    write_glb(dst, gltf, prims)
    return stats


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="src", default=SHIPPED)
    ap.add_argument("--out", default="", help="write here instead of in place")
    ap.add_argument("--only", default="")
    ap.add_argument("--radius", type=float, default=RADIUS, help="share of a part's thinnest dimension")
    ap.add_argument("--max-radius", type=float, default=MAX_RADIUS, help="tiles")
    ap.add_argument("--min-radius", type=float, default=MIN_RADIUS, help="below this a part stays sharp-edged (but smooth-shaded)")
    a = ap.parse_args()
    out = a.out or a.src
    os.makedirs(out, exist_ok=True)
    for p in sorted(glob.glob(os.path.join(a.src, "*.glb"))):
        k = os.path.basename(p)[:-4]
        if a.only and k not in a.only.split(","):
            continue
        s = round_file(p, os.path.join(out, k + ".glb"), a.radius, a.max_radius, a.min_radius)
        print(f"{k:24s} " + ("already rounded" if s is None else
              f"parts rounded {s['rounded']:3d}, kept {s['kept']:3d}, failed {s['failed']}; tris {s['tris_before']} -> {s['tris_after']}"))
