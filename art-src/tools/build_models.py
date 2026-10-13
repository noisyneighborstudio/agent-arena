"""Pez blockout model builder.
Conventions: 1 unit = 1 tile = 1 m, +Y up, +Z forward, pivot at ground centre.
Functional nodes: turret (yaw Y), barrel (child of turret, recoils -Z), spinner, bin.
"""
import math, json, os
import numpy as np
import trimesh
from trimesh.visual.material import PBRMaterial
from trimesh.transformations import translation_matrix as T, rotation_matrix as R

OUT = os.path.join(os.path.dirname(__file__), "pack", "models")

def hexc(h, a=255):
    h = h.lstrip('#'); return [int(h[i:i+2], 16) for i in (0, 2, 4)] + [a]

def lin(v):  # glTF colour factors are linear; the palette is sRGB
    v = v / 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

def mat(name, hx, metal=0.0, rough=0.4, emissive=False, alpha=255):
    c = hexc(hx, alpha)
    rgb = [lin(x) for x in c[:3]]
    kw = dict(name=name, baseColorFactor=rgb + [alpha / 255.0], metallicFactor=metal, roughnessFactor=rough,
              emissiveFactor=rgb if emissive else [0, 0, 0])
    if alpha < 255: kw['alphaMode'] = 'BLEND'
    return kw

MATS = {
 'team':     mat('M_Team', '#2E73FF', 0, .18),          # team mask: tint at runtime
 'cream':    mat('M_CreamPlastic', '#ECE4D2', 0, .22),
 'smoke':    mat('M_SmokePlastic', '#4A4F57', 0, .28),
 'steel':    mat('M_SpringSteel', '#8E979F', .7, .4),
 'foil':     mat('M_Foil', '#C8CDD3', .55, .35),
 'kraft':    mat('M_Kraft', '#A9845A', 0, .85),
 'licorice': mat('M_Licorice', '#1E1B1D', 0, .45),
 'dark':     mat('M_Dark', '#2A2E33', 0, .55),
 'pad':      mat('M_SugarPad', '#B9AE98', 0, .9),
 'bone':     mat('M_Bone', '#F6F2E8', 0, .3),
 'grape':    mat('M_GrapeVent', '#3A2440', 0, .5),
 'glass':    mat('M_FrostGlass', '#CFE3E6', 0, .1, alpha=110),
 'clear':    mat('M_ClearGlass', '#E6F2F4', 0, .05, alpha=40),  # see-through: for what's meant to be watched
 'cyan':     mat('M_E_Cyan', '#3FE6FF', emissive=True),
 'magenta':  mat('M_E_Magenta', '#FF3EC8', emissive=True),
 'acid':     mat('M_E_Acid', '#B6FF3B', emissive=True),
 'amber':    mat('M_E_Amber', '#FFA22E', emissive=True),
 'cinnamon': mat('M_Ore_Cinnamon', '#8A3A24', 0, .7),
 'copper':   mat('M_Ore_Copper', '#C8742F', .3, .5),
 'mint':     mat('M_Ore_Mint', '#6FC2A6', 0, .6),
 'crystal':  mat('M_Ore_Crystal', '#8FE4FF', 0, .25),     # crystal stock (not lit: glow means state)
 'uranium':  mat('M_Ore_Uranium', '#93BF3A', 0, .55),    # uranium stock (not lit: glow means state)
 'oretint':  mat('M_OreTint', '#C8742F', .2, .5),        # whatever ore is under it: Models.TintOre recolours it at runtime
}

class Asset:
    def __init__(self, key, kind, size):
        self.key, self.kind, self.size = key, kind, size
        self.nodes = {key: (None, np.eye(4))}
        self.parts = []
    def node(self, name, parent, pos=(0, 0, 0), rot=None):
        M = T(pos)
        if rot is not None: M = M @ rot
        self.nodes[name] = (parent, M); return name
    def add(self, node, m, mesh): self.parts.append((node or '__auto__', m, mesh))
    # primitives (centre coords in node-local space)
    def box(self, m, sx, sy, sz, x=0, y=0, z=0, node=None, rot=None):
        b = trimesh.creation.box(extents=(sx, sy, sz))
        if rot is not None: b.apply_transform(rot)
        b.apply_translation((x, y, z)); self.add(node, m, b)
    def cyl(self, m, r, h, x=0, y=0, z=0, axis='y', node=None, sections=20, rot=None):
        c = trimesh.creation.cylinder(radius=r, height=h, sections=sections)
        c.apply_transform(axis_rot(axis))
        if rot is not None: c.apply_transform(rot)
        c.apply_translation((x, y, z)); self.add(node, m, c)
    def cone(self, m, r, h, x=0, y=0, z=0, axis='y', node=None, sections=12):
        c = trimesh.creation.cone(radius=r, height=h, sections=sections)
        c.apply_transform(axis_rot(axis)); c.apply_translation((x, y, z)); self.add(node, m, c)
    def sphere(self, m, r, x=0, y=0, z=0, node=None, sub=2, scale=None, clip=False):
        s = trimesh.creation.icosphere(subdivisions=sub, radius=r)
        if scale: s.apply_scale(scale)
        s.apply_translation((x, y, z))
        if clip: s = s.slice_plane([0, 0.3, 0], [0, 1, 0], cap=False)
        self.add(node, m, s)
    def torus(self, m, R_, r, x=0, y=0, z=0, node=None):
        t = trimesh.creation.torus(major_radius=R_, minor_radius=r, major_sections=32, minor_sections=10)
        t.apply_transform(axis_rot('y')); t.apply_translation((x, y, z)); self.add(node, m, t)
    def rev(self, m, profile, x=0, z=0, node=None, sections=24):
        """A solid of revolution about the vertical axis through (x, z): profile is a closed, counterclockwise list of
        (radius, y) points (counterclockwise keeps the normals facing out)."""
        s = trimesh.creation.revolve(np.array(profile, float), sections=sections)
        s.apply_transform(axis_rot('y')); s.apply_translation((x, 0, z)); self.add(node, m, s)
    def strut(self, m, p0, p1, r, node=None, sections=8):
        """A round bar from p0 to p1 (a lattice member, a tie, a cable)."""
        self.add(node, m, trimesh.creation.cylinder(radius=r, segment=[p0, p1], sections=sections))
    def coil(self, m, R_, r, y0, y1, turns, x=0, z=0, node=None, per_turn=18, sides=6):
        """A helical spring of wire radius r around the vertical axis through (x, z), from y0 to y1."""
        n = int(turns * per_turn) + 1
        t = np.linspace(0, turns * 2 * math.pi, n)
        path = np.c_[x + R_ * np.cos(t), np.linspace(y0, y1, n), z + R_ * np.sin(t)]
        tan = np.gradient(path, axis=0); tan /= np.linalg.norm(tan, axis=1)[:, None]
        out = np.c_[np.cos(t), np.zeros(n), np.sin(t)]                          # radial, roughly normal to the wire
        out -= tan * (out * tan).sum(1)[:, None]; out /= np.linalg.norm(out, axis=1)[:, None]
        up = np.cross(tan, out)
        a = np.linspace(0, 2 * math.pi, sides, endpoint=False)
        v = (path[:, None, :] + r * (np.cos(a)[None, :, None] * out[:, None, :] + np.sin(a)[None, :, None] * up[:, None, :])).reshape(-1, 3)
        f = []
        for i in range(n - 1):
            for j in range(sides):
                p0, p1 = i * sides + j, i * sides + (j + 1) % sides
                f += [[p0, p1, p1 + sides], [p0, p1 + sides, p0 + sides]]
        v = np.vstack([v, path[0], path[-1]]); c0, c1 = len(v) - 2, len(v) - 1
        for j in range(sides):
            f += [[c0, (j + 1) % sides, j], [c1, (n - 1) * sides + j, (n - 1) * sides + (j + 1) % sides]]
        mesh = trimesh.Trimesh(v, np.array(f)); mesh.fix_normals()
        self.add(node, m, mesh)
    def sheet(self, m, P, t, node=None):
        """A curved plate of thickness t from a grid of points P (nu, nv, 3) on its mid-surface (a reflector, a cowl)."""
        nu, nv, _ = P.shape
        du = np.gradient(P, axis=0); dv = np.gradient(P, axis=1)
        n = np.cross(du, dv); n /= np.linalg.norm(n, axis=2)[:, :, None]
        v = np.vstack([(P + n * t / 2).reshape(-1, 3), (P - n * t / 2).reshape(-1, 3)])
        off, idx = nu * nv, lambda i, j: i * nv + j
        f = []
        for i in range(nu - 1):
            for j in range(nv - 1):
                a, b, c, d = idx(i, j), idx(i + 1, j), idx(i + 1, j + 1), idx(i, j + 1)
                f += [[a, b, c], [a, c, d], [off + a, off + c, off + b], [off + a, off + d, off + c]]
        ring = [idx(i, 0) for i in range(nu)] + [idx(nu - 1, j) for j in range(1, nv)] + \
               [idx(i, nv - 1) for i in range(nu - 2, -1, -1)] + [idx(0, j) for j in range(nv - 2, 0, -1)]
        for k in range(len(ring)):                                              # the rim
            a, b = ring[k], ring[(k + 1) % len(ring)]
            f += [[a, off + a, off + b], [a, off + b, b]]
        mesh = trimesh.Trimesh(v, np.array(f), process=False); mesh.fix_normals()
        self.add(node, m, mesh)
    def tube(self, m, path, r, node=None, sides=8):
        """A round pipe of radius r along a polyline (rails, pipes, cables), its rings mitred at the bends."""
        path = np.asarray(path, float); n = len(path)
        seg = np.diff(path, axis=0); seg /= np.linalg.norm(seg, axis=1)[:, None]
        tan = np.vstack([seg[:1], seg[:-1] + seg[1:], seg[-1:]]); tan /= np.linalg.norm(tan, axis=1)[:, None]
        ref = np.array([0, 1., 0]) if abs(tan[0][1]) < .9 else np.array([1., 0, 0])
        out = [np.cross(tan[0], ref)]; out[0] /= np.linalg.norm(out[0])
        for i in range(1, n):                                                   # carried along the path: no twist
            o = out[-1] - tan[i] * np.dot(out[-1], tan[i]); out.append(o / np.linalg.norm(o))
        out = np.array(out); up = np.cross(tan, out)
        a = np.linspace(0, 2 * math.pi, sides, endpoint=False)
        v = (path[:, None, :] + r * (np.cos(a)[None, :, None] * out[:, None, :] + np.sin(a)[None, :, None] * up[:, None, :])).reshape(-1, 3)
        f = []
        for i in range(n - 1):
            for j in range(sides):
                p0, p1 = i * sides + j, i * sides + (j + 1) % sides
                f += [[p0, p1, p1 + sides], [p0, p1 + sides, p0 + sides]]
        v = np.vstack([v, path[0], path[-1]]); c0, c1 = len(v) - 2, len(v) - 1
        for j in range(sides):
            f += [[c0, (j + 1) % sides, j], [c1, (n - 1) * sides + j, (n - 1) * sides + (j + 1) % sides]]
        mesh = trimesh.Trimesh(v, np.array(f)); mesh.fix_normals()
        self.add(node, m, mesh)
    def hoop(self, m, path, r, node=None, closed=True, sides=12, side=None, r2=None):
        """A tube of radius r along a polyline (a closed loop by default; an open one gets flat end caps). `side` fixes the
        cross-section's first axis (a planar loop's normal, so a band lies flat in it) and r2 makes the section an
        ellipse: r along `side`, r2 across it."""
        p = np.asarray(path, float); n = len(p)
        tan = (np.roll(p, -1, 0) - np.roll(p, 1, 0)) if closed else np.gradient(p, axis=0)
        tan /= np.linalg.norm(tan, axis=1)[:, None]
        if side is None:
            ref = np.where(np.abs(tan[:, 1:2]) < .9, [[0, 1, 0]], [[1, 0, 0]])
            u = np.cross(tan, ref)
        else:
            u = np.broadcast_to(np.asarray(side, float), p.shape).copy()
            u -= tan * (u * tan).sum(1)[:, None]
        u /= np.linalg.norm(u, axis=1)[:, None]
        w = np.cross(tan, u)
        a = np.linspace(0, 2 * math.pi, sides, endpoint=False)
        v = (p[:, None, :] + r * np.cos(a)[None, :, None] * u[:, None, :] + (r2 or r) * np.sin(a)[None, :, None] * w[:, None, :]).reshape(-1, 3)
        f = []
        for i in range(n if closed else n - 1):
            k = (i + 1) % n
            for j in range(sides):
                f += [[i * sides + j, i * sides + (j + 1) % sides, k * sides + (j + 1) % sides],
                      [i * sides + j, k * sides + (j + 1) % sides, k * sides + j]]
        if not closed:
            v = np.vstack([v, p[0], p[-1]]); c0, c1 = len(v) - 2, len(v) - 1
            for j in range(sides):
                f += [[c0, (j + 1) % sides, j], [c1, (n - 1) * sides + j, (n - 1) * sides + (j + 1) % sides]]
        mesh = trimesh.Trimesh(v, np.array(f)); mesh.fix_normals()
        self.add(node, m, mesh)
    def octa(self, m, r, h, x=0, y=0, z=0, node=None):
        v = np.array([[r,0,0],[-r,0,0],[0,0,r],[0,0,-r],[0,h,0],[0,-h,0]], float)
        f = [[4,0,2],[4,2,1],[4,1,3],[4,3,0],[5,2,0],[5,1,2],[5,3,1],[5,0,3]]
        o = trimesh.Trimesh(v, f); o.fix_normals(); o.apply_translation((x, y, z)); self.add(node, m, o)

    def export(self):
        scene = trimesh.Scene(base_frame=self.key)
        # Build stages: structures rise out of their pad in 4 stages (see MOTION.md).
        H = max(mesh.bounds[1][1] for _, _, mesh in self.parts)
        parts = []
        for node, m, mesh in self.parts:
            if node == '__auto__':
                if self.kind == 'structures':
                    top, cy = mesh.bounds[1][1], mesh.centroid[1]
                    st = 0 if top <= .13 else 1 if cy < .3 * H else 2 if cy < .65 * H else 3
                    node = f'stage_{st}'
                    self.nodes.setdefault(node, (self.key, np.eye(4)))
                else:
                    node = self.key
            parts.append((node, m, mesh))
        self.parts = parts
        order = [self.key]
        while len(order) < len(self.nodes):
            for n, (p, _) in self.nodes.items():
                if n not in order and p in order: order.append(n)
        for n in order[1:]:
            p, M = self.nodes[n]
            scene.graph.update(frame_from=p, frame_to=n, matrix=M)
        groups = {}
        for node, m, mesh in self.parts: groups.setdefault((node, m), []).append(mesh)
        tris = 0
        for (node, m), meshes in groups.items():
            g = trimesh.util.concatenate(meshes)
            g.visual = trimesh.visual.TextureVisuals(material=PBRMaterial(**MATS[m]))
            tris += len(g.faces)
            nm = f"{node}__{MATS[m]['name']}"
            scene.add_geometry(g, geom_name=nm, node_name=nm, parent_node_name=node)
        os.makedirs(os.path.join(OUT, self.kind), exist_ok=True)
        path = os.path.join(OUT, self.kind, f"{self.key}.glb")
        scene.export(path)
        b = scene.bounds
        return dict(key=self.key, kind=self.kind, file=f"models/{self.kind}/{self.key}.glb", tris=int(tris),
                    nodes=[n for n in order], bounds=[[round(x, 3) for x in r] for r in b])

def axis_rot(axis):
    if axis == 'y': return R(-math.pi / 2, [1, 0, 0])
    if axis == 'x': return R(math.pi / 2, [0, 1, 0])
    return np.eye(4)

rx = lambda d: R(math.radians(d), [1, 0, 0])
ry = lambda d: R(math.radians(d), [0, 1, 0])
rz = lambda d: R(math.radians(d), [0, 0, 1])

# ---------------------------------------------------------------- structures
def pad(a, n):
    s = n - 0.08; a.box('pad', s, 0.08, s, y=0.04)

def door(a, w, h, z, y0=0.08, lights=True, anim=False):
    if anim:   # roll-up door: pivot at the top edge; animate localScale.y 1 -> 0.05
        d = a.node('door', a.key, (0, y0 + h, z))
        a.box('dark', w, h, 0.06, y=-h / 2, node=d)
        a.box('licorice', w * .98, .02, .065, y=-h + .03, node=d)
    else:
        a.box('dark', w, h, 0.06, y=y0 + h / 2, z=z)
    a.box('licorice', w + .06, .05, .07, y=y0 + h + .025, z=z)
    if lights:
        for sx in (-1, 1): a.box('amber', .06, .06, .06, x=sx * (w / 2 + .1), y=y0 + h * .6, z=z)

def S_command_center():
    a = Asset('command_center', 'structures', 3); pad(a, 3)
    # The hall: a long cream deck with the truck door in front, a team wrapper band and porthole windows down its side;
    # behind it a taller wing that the dispenser stands on, and the corner beside that left to the crane's footing
    # (north-west in the pack's space: -x, +z).
    a.box('cream', 2.7, .7, 1.75, y=.43, z=-.345)
    a.box('cream', 1.75, .95, .82, x=.47, y=.555, z=.89)
    a.box('team', 2.74, .09, 1.79, y=.6, z=-.345)                 # wrapper bands
    a.box('team', 1.79, .09, .86, x=.47, y=.85, z=.89)
    door(a, .9, .6, -1.23, anim=True)
    for z in (-.85, -.45, -.05, .35, .75): a.cyl('dark', .07, .03, x=1.35, y=.38, z=z, axis='x', sections=16)
    # The dispenser: a smoke-plastic stem with grip slats, and the team flip-top head hinged at its back edge (the head
    # node's pivot), sitting a little open. HqWorks pops it open each time a structure is finished. The head and the dish
    # hang under stage_2, so they rise with the stem and the mast when a deployed HQ builds.
    a.node('stage_2', a.key)
    a.box('smoke', .8, .7, .6, x=.55, y=1.38, z=.85)
    for i in range(3): a.box('team', .5, .07, .03, x=.55, y=1.2 + i * .15, z=.54)
    a.box('dark', .5, .05, .08, x=.55, y=1.71, z=.55)             # the dispensing lip
    hd = a.node('head', 'stage_2', (.55, 1.73, 1.15), rot=rx(10))
    a.box('team', .96, .4, .78, y=.22, z=-.37, node=hd)
    a.box('team', .9, .1, .1, y=.04, z=-.74, node=hd)             # the snout over the lip
    a.cyl('steel', .05, .86, axis='x', node=hd)                   # hinge pin
    # Comms mast with a dish that turns (the dish node, yawed by HqWorks).
    a.cyl('steel', .035, .55, x=-1.05, y=1.05, z=-.9)
    a.cyl('steel', .07, .06, x=-1.05, y=.81, z=-.9)
    dish = a.node('dish', 'stage_2', (-1.05, 1.34, -.9))
    a.cyl('steel', .05, .08, node=dish)
    pr = [(0, .0), (.24, .1), (.26, .13), (.235, .13), (.0, .035)]
    bowl = trimesh.creation.revolve(np.array(pr[::-1], float), sections=24); bowl.apply_transform(axis_rot('y'))
    bowl.apply_transform(rx(-60)); bowl.apply_translation((0, .08, 0)); a.add(dish, 'foil', bowl)
    a.strut('steel', (0, .08, 0), (0, .08 + .2 * math.sin(math.radians(30)), -.2 * math.cos(math.radians(30))), .012, node=dish)
    a.sphere('dark', .03, y=.08 + .2 * math.sin(math.radians(30)), z=-.2 * math.cos(math.radians(30)), node=dish)
    # The tower crane, on its own footing: the tallest thing in any base. A square lattice mast; on top the slewing
    # spinner with the team cab, an A-frame apex with the amber beacon, the jib (along +x) with its trolley and hook,
    # and the counter-jib with its counterweights. PezMotion slews it to the site being built, runs the trolley out with
    # the distance and lowers the hook while it works.
    cx, cz, w, top = -.92, .93, .24, 2.55
    a.box('smoke', .62, .16, .62, x=cx, y=.16, z=cz)
    for sx in (-1, 1):
        for sz in (-1, 1): a.cyl('steel', .03, .04, x=cx + sx * .22, y=.25, z=cz + sz * .22, sections=10)
    h = w / 2
    corners = [(-h, -h), (h, -h), (h, h), (-h, h)]
    ys = [.24 + i * (top - .24) / 6 for i in range(7)]
    for k in range(6):                                             # six panels, so the mast rises with the stages
        y0, y1 = ys[k], ys[k + 1]
        for (px, pz) in corners: a.strut('steel', (cx + px, y0, cz + pz), (cx + px, y1, cz + pz), .022, sections=6)
        for i in range(4):
            (ax, az), (bx, bz) = corners[i], corners[(i + 1) % 4]
            a.strut('steel', (cx + ax, y1, cz + az), (cx + bx, y1, cz + bz), .012, sections=6)
            if k % 2: ax, az, bx, bz = bx, bz, ax, az
            a.strut('steel', (cx + ax, y0, cz + az), (cx + bx, y1, cz + bz), .01, sections=6)
    sp = a.node('spinner', a.key, (cx, top, cz))
    a.cyl('steel', .2, .07, y=.035, node=sp)                       # slewing ring
    a.box('team', .26, .24, .26, x=.1, y=.16, z=-.2, node=sp)      # the cab
    a.box('dark', .2, .1, .03, x=.1, y=.2, z=-.335, node=sp)       # its window
    a.box('dark', .03, .1, .16, x=.235, y=.2, z=-.2, node=sp)
    for sz in (-1, 1): a.strut('steel', (-.08, .07, sz * .08), (0, .55, 0), .016, node=sp)  # A-frame
    for sz in (-1, 1): a.strut('steel', (.08, .07, sz * .08), (0, .55, 0), .016, node=sp)
    a.sphere('steel', .035, y=.55, node=sp)
    a.cyl('dark', .025, .05, y=.6, node=sp)
    a.sphere('amber', .04, y=.65, node=sp)                         # beacon: blinks every 1.5 s
    # Jib: a triangular lattice, two bottom chords and a top chord, 1.65 long.
    L, jw, jh = 1.75, .08, .15
    a.strut('steel', (.1, .08, -jw), (L, .08, -jw), .016, node=sp)
    a.strut('steel', (.1, .08, jw), (L, .08, jw), .016, node=sp)
    a.strut('steel', (.1, .08 + jh, 0), (L - .05, .08 + jh * .4, 0), .016, node=sp)
    n = 7
    for i in range(n + 1):
        x0 = .1 + (L - .1) * i / n
        yt = .08 + jh * (1 - .6 * i / n)
        a.strut('steel', (x0, .08, -jw), (x0, .08, jw), .009, node=sp, sections=6)
        if i < n:
            x1 = .1 + (L - .1) * (i + .5) / n
            for sz in (-1, 1):
                a.strut('steel', (x0, .08, sz * jw), (x1, .08 + jh * (1 - .6 * (i + .5) / n), 0), .008, node=sp, sections=6)
                x2 = .1 + (L - .1) * (i + 1) / n
                a.strut('steel', (x1, .08 + jh * (1 - .6 * (i + .5) / n), 0), (x2, .08, sz * jw), .008, node=sp, sections=6)
    a.box('team', .1, .06, .2, x=L, y=.09, node=sp)                # jib tip
    # Counter-jib and its weights.
    for sz in (-1, 1): a.box('steel', .75, .045, .035, x=-.45, y=.09, z=sz * .07, node=sp)
    for i, x in enumerate((-.62, -.72)): a.box('smoke' if i else 'cream', .09, .2, .22, x=x, y=.01, node=sp)
    a.box('team', .2, .05, .23, x=-.67, y=.12, node=sp)
    # Pendant ties from the apex to the jib and to the counter-jib.
    a.strut('steel', (0, .55, 0), (L * .62, .08 + jh * .63, 0), .006, node=sp)
    a.strut('steel', (0, .55, 0), (-.78, .1, 0), .006, node=sp)
    # Trolley (slides along the jib) with the hook on its cable; the cable node hangs from the trolley and stretches with
    # the hook.
    tr = a.node('trolley', sp, (.75, .06, 0))
    a.box('steel', .12, .05, .2, node=tr)
    cb = a.node('cable', tr, (0, -.02, 0))
    for sz in (-1, 1): a.strut('dark', (0, 0, sz * .025), (0, -.36, sz * .025), .006, node=cb, sections=6)
    hk = a.node('hook', tr, (0, -.38, 0))
    a.box('team', .08, .09, .1, y=-.02, node=hk)
    t = trimesh.creation.torus(major_radius=.035, minor_radius=.011, major_sections=16, minor_sections=8)
    t.apply_translation((0, -.1, 0)); a.add(hk, 'steel', t)
    return a

def S_outpost():
    """A forward ore depot: a dispenser being filled. Trucks back up to the hopper on the south edge and tip their ore
    into it (its team trapdoors, flap_l and flap_r, swing open while a truck is in the bay); a cleated belt carries the
    ore up the ramp into the dispenser stem at the back, whose team flip-top head (hinged at its back edge) lifts to take
    each load, and the pressed ore stacks up behind the stem's window (silo_0..4) and is fed away underground. A
    blockhouse with a firing slit carries the lookout (vision 9) on a telescoping mast; a fuel tank and pump on the other
    side make it a refuelling point. OutpostWorks runs all of it from the sim (the docked truck's steps, its ore, enemies
    in sight), and telescopes the mast up as the outpost finishes deploying from its truck."""
    a = Asset('outpost', 'structures', 2); pad(a, 2)
    S1, S2, S3 = a.node('stage_1', a.key), a.node('stage_2', a.key), a.node('stage_3', a.key)
    # The hopper on the south edge, where the truck's bin end backs up: a cream rim round a licorice funnel, its two team
    # trapdoors hinged on the rim's sides (each node's pivot), lying shut. The amber dock lamps (DockView) sit either side
    # of its front lip, low, so DockView runs its pip row along the lip.
    hz0, hz1, hw, ry_ = -.9, -.46, .3, .2                          # front, back, half width, rim height
    a.box('cream', 2 * hw + .1, ry_ - .08, .06, y=(ry_ + .08) / 2, z=hz0 + .03)
    a.box('cream', 2 * hw + .1, ry_ - .08, .06, y=(ry_ + .08) / 2, z=hz1 - .03)
    for s in (-1, 1): a.box('cream', .06, ry_ - .08, hz1 - hz0, x=s * (hw + .02), y=(ry_ + .08) / 2, z=(hz0 + hz1) / 2)
    zc, inset = (hz0 + hz1) / 2, .11                                           # funnel: four sloped licorice plates
    t = math.degrees(math.atan2(ry_ - .1, inset))
    for s in (-1, 1):
        a.box('licorice', .02, math.hypot(ry_ - .1, inset), hz1 - hz0 - .02, x=s * (hw - inset / 2), y=(ry_ + .1) / 2 - .005, z=zc, rot=rz(-s * (90 - t)))
        a.box('licorice', 2 * hw - .02, math.hypot(ry_ - .1, inset), .02, y=(ry_ + .1) / 2 - .005, z=zc + s * ((hz1 - hz0) / 2 - inset / 2), rot=rx(s * (90 - t)))
    a.box('dark', 2 * (hw - inset) + .02, .02, hz1 - hz0 - 2 * inset + .02, y=.1, z=zc)
    for s, nm in ((1, 'flap_l'), (-1, 'flap_r')):
        f = a.node(nm, S1, (s * hw, ry_ + .02, zc))
        a.box('team', hw - .01, .025, hz1 - hz0 - .02, x=-s * (hw - .01) / 2, node=f)
        a.box('licorice', .03, .03, hz1 - hz0 - .06, x=-s * (hw - .03), y=.01, node=f)   # its rubber edge
        a.cyl('steel', .018, hz1 - hz0 - .04, axis='z', node=f, sections=8)             # hinge
    for s in (-1, 1): a.box('amber', .06, .06, .06, x=s * .44, y=.16, z=-.93)
    # The belt: a cleated licorice belt in a team frame, from the hopper's foot up the ramp to the top of the stem; the
    # drum at its head turns while it runs. The ore riding it is the view's (OutpostWorks), from feed_in to feed_out.
    P0, P1 = np.array([0, .2, hz1 + .02]), np.array([0, .9, .34])
    d = P1 - P0; L = float(np.linalg.norm(d)); u = d / L; slope = math.degrees(math.atan2(d[1], d[2]))
    mid = (P0 + P1) / 2
    nrm = np.array([0, u[2], -u[1]])                                                         # the belt's up
    a.box('team', .28, .07, L, y=mid[1], z=mid[2], rot=rx(-slope), node=S2)                  # frame
    top = mid + nrm * .045
    a.box('licorice', .2, .025, L - .04, y=top[1], z=top[2], rot=rx(-slope), node=S2)
    for k in range(1, 7):                                                                    # cleats
        c = P0 + u * (L * k / 7) + nrm * .065
        a.box('licorice', .2, .025, .018, y=c[1], z=c[2], rot=rx(-slope), node=S2)
    for s in (-1, 1):                                                                        # side rails
        r0, r1 = P0 + nrm * .09, P1 + nrm * .09
        a.strut('steel', (s * .13, r0[1], r0[2]), (s * .13, r1[1], r1[2]), .012, node=S2, sections=6)
    for zz in (-.1, .16):                                                                    # trestles under it
        yy = P0[1] + (zz - P0[2]) * d[1] / d[2] - .04
        for s in (-1, 1): a.strut('steel', (s * .1, .1, zz), (s * .12, yy, zz), .02, node=S2, sections=6)
        a.box('steel', .28, .03, .05, y=yy, z=zz, node=S2)
    dr = a.node('drum', S2, (0, P1[1], P1[2]))
    a.cyl('steel', .05, .26, axis='x', node=dr, sections=14)
    for k in range(3): a.box('dark', .27, .018, .1, rot=rx(60 * k), node=dr)
    fin = P0 + u * .06 + nrm * .09; fout = P1 + nrm * .09
    a.node('feed_in', a.key, tuple(fin)); a.node('feed_out', a.key, tuple(fout))
    # The dispenser stem at the back: smoke-plastic walls, a clear window on its +x side where the pressed ore stacks up
    # (silo_0..4, in crystal by default: the view tints them to the ore delivered), team grip slats on the front, the
    # licorice lip at the top front edge, and the team flip-top head hinged at its back edge.
    sz, sw, s0, s1 = .63, .42, .1, 1.0
    zf, zb = sz - sw / 2, sz + sw / 2
    a.box('smoke', sw, s1 - s0, .05, y=(s0 + s1) / 2, z=zb - .025, node=S2)                  # back
    a.box('smoke', sw, s1 - s0, .05, y=(s0 + s1) / 2, z=zf + .025, node=S2)                  # front
    a.box('smoke', .05, s1 - s0, sw, x=-sw / 2 + .025, y=(s0 + s1) / 2, z=sz, node=S2)       # -x side
    w0, w1 = .46, .9                                                                         # the window, +x side
    a.box('smoke', .05, w0 - s0, sw, x=sw / 2 - .025, y=(s0 + w0) / 2, z=sz, node=S2)
    a.box('smoke', .05, s1 - w1, sw, x=sw / 2 - .025, y=(w1 + s1) / 2, z=sz, node=S2)
    for s in (-1, 1): a.box('smoke', .05, w1 - w0, .07, x=sw / 2 - .025, y=(w0 + w1) / 2, z=sz + s * (sw / 2 - .035), node=S2)
    a.box('clear', .012, w1 - w0, sw - .14, x=sw / 2 - .01, y=(w0 + w1) / 2, z=sz, node=S2)
    a.box('dark', sw - .1, .02, sw - .1, y=s0 + .3, z=sz, node=S2)                          # the stem's floor under the stack
    for i in range(3):
        for s in (-1, 1): a.box('team', .1, .05, .03, x=s * .13, y=.36 + i * .12, z=zf - .005, node=S2)
    a.box('licorice', sw - .04, .04, .06, y=s1 - .01, z=zf + .03, node=S2)                  # the lip
    silo = a.node('silo', S2, (0, 0, sz))
    for k in range(5):
        b = a.node(f'silo_{k}', silo, (0, w0 + .05 + k * .085, 0))
        a.box('crystal', sw - .14, .07, sw - .14, node=b)
    hd = a.node('head', S3, (0, s1, zb + .02), rot=rx(4))
    a.box('team', sw + .08, .24, sw + .06, y=.12, z=-(sw + .06) / 2, node=hd)
    a.box('team', sw - .02, .08, .09, y=.04, z=-(sw + .06) - .02, node=hd)                    # snout
    a.cyl('steel', .03, sw + .06, axis='x', node=hd, sections=8)                               # hinge pin
    a.node('drop', a.key, (0, .9, sz))                                                         # where the ore falls in
    # The blockhouse on the +x side: cream with a team wrapper band and a cream roof, a firing slit on its two faces and a
    # door; on its roof the lookout's collar, its mast (lookout_mast: a telescoping steel tube, raised once deployed) and
    # the lookout (yaws: a little team periscope head with a dark lens and a whip aerial).
    bx0, bx1, bz0, bz1, bh = .3, .9, -.4, .9, .5
    bx, bzc = (bx0 + bx1) / 2, (bz0 + bz1) / 2
    a.box('cream', bx1 - bx0, bh - .08, bz1 - bz0, x=bx, y=.08 + (bh - .08) / 2, z=bzc)
    a.box('team', bx1 - bx0 + .04, .09, bz1 - bz0 + .04, x=bx, y=bh - .08, z=bzc)
    a.box('cream', bx1 - bx0 - .08, .05, bz1 - bz0 - .08, x=bx, y=bh, z=bzc)
    a.box('dark', .36, .06, .03, x=bx, y=.3, z=bz0 - .005)                                    # slits
    a.box('dark', .03, .06, .5, x=bx1 + .005, y=.3, z=.45)
    a.box('dark', .03, .26, .24, x=bx1 + .005, y=.21, z=-.05)                                 # door
    a.box('licorice', .04, .04, .3, x=bx1 + .01, y=.36, z=-.05)
    a.cyl('licorice', .11, .03, x=.6, y=bh + .03, z=.05, sections=16)                          # roof hatch
    a.cyl('cream', .09, .04, x=.6, y=bh + .05, z=.05, sections=16)
    a.box('steel', .03, .03, .1, x=.6, y=bh + .08, z=.05)
    mx, mz = .7, .7
    a.cyl('steel', .08, .06, x=mx, y=bh + .05, z=mz, sections=16)
    a.cyl('smoke', .045, .7, x=mx, y=bh + .38, z=mz, sections=12)                              # the outer tube
    a.cyl('team', .055, .05, x=mx, y=bh + .71, z=mz, sections=12)
    ms = a.node('lookout_mast', S3, (mx, bh + .7, mz))
    a.cyl('steel', .03, .6, node=ms, sections=10)                                              # the inner tube (slides)
    lk = a.node('lookout', ms, (0, .3, 0))
    a.cyl('steel', .05, .04, node=lk, sections=12)
    a.box('team', .16, .14, .18, y=.09, z=-.02, node=lk)
    a.box('team', .1, .07, .08, y=.11, z=-.14, node=lk)                                        # the snout
    a.cyl('dark', .035, .02, y=.11, z=-.185, axis='z', node=lk, sections=12)                   # lens
    a.cyl('steel', .008, .3, x=.05, y=.31, z=.04, node=lk, sections=6)                         # whip
    a.sphere('dark', .015, x=.05, y=.46, z=.04, node=lk, sub=1)
    # The fuel tank on the -x side, on two saddles, with team bands; its pump at the front with a licorice hose coiled on
    # it: vehicles refuel here.
    tx, ty, tr, tz0, tz1 = -.56, .32, .18, -.12, .78
    for zz in (tz0 + .15, tz1 - .15): a.box('steel', .34, .2, .08, x=tx, y=.18, z=zz)
    a.cyl('cream', tr, tz1 - tz0 - tr, x=tx, y=ty, z=(tz0 + tz1) / 2, axis='z', sections=18)
    for zz in (tz0 + tr * .5, tz1 - tr * .5):
        cap = trimesh.creation.icosphere(subdivisions=2, radius=tr); cap.apply_scale((1, 1, .5))
        cap.apply_translation((tx, ty, zz)); a.add(None, 'cream', cap)
    for zz in (tz0 + .3, tz1 - .3): a.cyl('team', tr + .015, .07, x=tx, y=ty, z=zz, axis='z', sections=18)
    a.cyl('steel', .04, .05, x=tx, y=ty + tr + .02, z=(tz0 + tz1) / 2 + .1, sections=10)        # filler cap
    px, pz = -.56, -.33
    a.box('cream', .2, .3, .14, x=px, y=.08 + .15, z=pz)
    a.box('team', .22, .05, .16, x=px, y=.4, z=pz)
    a.box('dark', .12, .07, .01, x=px, y=.3, z=pz - .075)                                     # its dial
    hose = trimesh.creation.torus(major_radius=.07, minor_radius=.018, major_sections=16, minor_sections=6)
    hose.apply_transform(axis_rot('x')); hose.apply_translation((px - .12, .22, pz)); a.add(None, 'licorice', hose)
    a.box('steel', .03, .08, .03, x=px - .12, y=.33, z=pz)
    return a

def S_power_plant():
    a = Asset('power_plant', 'structures', 2); pad(a, 2)
    # Two reactor towers of different heights on the diagonal (the open glowing stacks are built on their dark rims and
    # cyan cores by PowerCores in Unity, so those stay the only dark and cyan parts up top). Each wears a cream coil
    # spring under a collar (coil_0, coil_1, pivots at their feet, under stage_2 so they rise with the build):
    # PowerWorks presses them down with the base's power load, and they bottom out and chatter on low power.
    a.node('stage_2', a.key)
    for i, (x, z, h) in enumerate(((-.42, -.3, 1.3), (.42, .32, 1.5))):
        a.cyl('steel', .45, .1, x=x, y=.13, z=z, sections=24)                 # foot flange
        a.cyl('smoke', .27, h, x=x, y=.08 + h / 2, z=z, sections=24)
        a.cyl('cream', .385, .06, x=x, y=.2, z=z, sections=24)                # the spring's seat
        c = a.node(f'coil_{i}', 'stage_2', (x, .23, z))
        a.coil('cream', .37, .04, .02, h - .44, 4 if i == 0 else 5, node=c)
        a.cyl('cream', .4, .05, y=h - .4, node=c, sections=24)                # the collar pressing on it
        a.cyl('team', .37, .14, x=x, y=h - .04, z=z, sections=24)
        a.cyl('dark', .33, .04, x=x, y=.08 + h + .01, z=z)
        a.cyl('cyan', .24, .05, x=x, y=.08 + h + .02, z=z)
        a.node(f'stack_{i}', a.key, (x, h + .27, z))                          # the open stack's lip (PowerCores builds it)
    # Generator house in the free front corner: cream, team roof band, a dark louvred vent, and the flywheel (its own
    # node, turned by PowerWorks with the load) in a steel cradle on its side.
    gx, gz = .5, -.5
    a.box('cream', .62, .36, .55, x=gx, y=.26, z=gz)
    a.box('team', .66, .09, .59, x=gx, y=.475, z=gz)
    for k in range(3): a.box('dark', .34, .035, .03, x=gx - .05, y=.17 + k * .08, z=gz - .28)
    a.box('steel', .1, .2, .3, x=gx + .34, y=.18, z=gz)
    fw = a.node('flywheel', 'stage_2', (gx + .42, .3, gz))
    a.cyl('foil', .17, .06, axis='x', node=fw, sections=24)
    a.cyl('steel', .05, .1, axis='x', node=fw, sections=12)
    for k in range(4):                                                         # spokes, so its turning shows
        a.box('steel', .015, .3, .04, x=.035, rot=rx(45 * k), node=fw)
    # Conduits from each tower's foot into the generator.
    a.box('steel', .07, .07, .5, x=.47, y=.12, z=.0)
    a.box('steel', .6, .07, .07, x=.1, y=.12, z=-.42)
    # Transformer in the back corner: a steel box with three cream insulators, and the line it feeds runs off the pad.
    # PowerWorks throws electric arcs between the stacks' lips (stack_0, stack_1), now and then down to an insulator.
    tx, tz = -.48, .5
    a.box('steel', .42, .34, .3, x=tx, y=.25, z=tz)
    for k in range(3):
        for j in range(3): a.cyl('cream', .045 - j * .006, .035, x=tx - .13 + k * .13, y=.45 + j * .05, z=tz, sections=12)
        a.cyl('steel', .012, .1, x=tx - .13 + k * .13, y=.6, z=tz, sections=8)
        a.node(f'insulator_{k}', a.key, (tx - .13 + k * .13, .65, tz))         # where the arcs come down to
    a.box('dark', .3, .08, .02, x=tx, y=.22, z=tz - .16)
    a.box('steel', .07, .07, .5, x=tx, y=.12, z=.75)
    return a

def S_mining_refinery():
    a = Asset('mining_refinery', 'structures', 3); pad(a, 3)
    a.box('cream', 2.7, .75, 1.6, y=.455, z=.3)
    a.box('cream', .9, .75, .9, x=-.9, y=.455, z=-.95)
    a.box('cream', .9, .75, .9, x=.9, y=.455, z=-.95)
    door(a, .9, .6, -.48, lights=False, anim=True)              # dock (south)
    a.box('kraft', .9, .04, .9, y=.1, z=-.95)                    # dock floor
    for sx in (-1, 1): a.box('amber', .07, .07, .07, x=sx * .55, y=.55, z=-1.42)
    # Ore belt over the dock: in under the intake hood (-x), crushed under the foil roll, into the smelter hood (+x).
    # The ore riding it is the view's (RefineryWorks), between the belt_in and belt_out markers.
    a.box('team', 2.5, .09, .34, y=.875, z=-.3)                 # belt frame
    a.box('licorice', 2.4, .02, .26, y=.93, z=-.3)              # belt
    for x in (-1.2, 1.2): a.cyl('steel', .055, .3, x=x, y=.92, z=-.3, axis='z')
    a.box('cream', .3, .2, .4, x=-1.12, y=1.06, z=-.3)          # intake hood
    a.box('licorice', .02, .12, .3, x=-.97, y=1.0, z=-.3)
    a.box('cream', .3, .26, .4, x=1.17, y=1.08, z=-.3)          # smelter hood
    a.box('licorice', .02, .16, .3, x=1.02, y=1.02, z=-.3)
    a.node('belt_in', a.key, (-.95, .94, -.3)); a.node('belt_out', a.key, (1.0, .94, -.3))
    for z in (-.55, -.05):                                     # the roll's cheeks
        a.box('steel', .24, .3, .04, x=.82, y=1.03, z=z)
        a.cyl('steel', .05, .06, x=.82, y=1.11, z=z, axis='z')
    # Crusher roll, turning while refining. Turned 180 deg about Y so PezMotion's positive spin rolls its underside along with
    # the ore (Unity mirrors x on import).
    sp = a.node('spinner', a.key, (.82, 1.11, -.3), rot=ry(180))
    a.cyl('foil', .15, .42, axis='z', node=sp, sections=24)
    for k in range(6):
        t = k * math.pi / 3
        a.box('smoke', .04, .04, .43, x=.15 * math.cos(t), y=.15 * math.sin(t), node=sp, rot=rz(math.degrees(t)))
    # Smelter: a squat drum fed by the belt, piped to the stacks.
    a.cyl('cream', .32, .5, x=.78, y=1.08, z=.68)
    a.cyl('team', .335, .1, x=.78, y=1.2, z=.68)
    a.cyl('cream', .26, .08, x=.78, y=1.37, z=.68)
    a.cyl('steel', .05, .16, x=.9, y=1.45, z=.6)
    a.cyl('steel', .06, .52, x=.22, y=1.2, z=.72, axis='x')
    # Two tapered smokestacks on a boiler house: cream with team bands, a catwalk ring, a steel crown round a sooty
    # throat. stack_0 / stack_1 mark the mouths the view's smoke leaves from.
    a.box('cream', 1.3, .2, .6, x=-.5, y=.93, z=.75)
    for i, (x, top, r0, r1) in enumerate(((-.82, 2.95, .25, .17), (-.16, 2.55, .22, .155))):
        b = 1.03
        rad = lambda y: r0 + (r1 - r0) * (y - b) / (top - .13 - b)
        a.cyl('cream', r0 + .07, .12, x=x, y=b + .06, z=.75, sections=24)
        a.rev('cream', [(0, b), (r0, b), (r1, top - .13), (0, top - .13)], x=x, z=.75)
        for y in (top - .42, top - .64):
            a.rev('team', [(0, y), (rad(y) + .014, y), (rad(y + .09) + .014, y + .09), (0, y + .09)], x=x, z=.75)
        cw = b + (top - b) * .52
        a.torus('steel', rad(cw) + .035, .017, x=x, y=cw, z=.75)
        ri, ro, yb = r1 - .03, r1 + .035, top - .16
        cx, cr = (ri + ro) / 2, (ro - ri) / 2
        bead = [(cx + cr * math.cos(t), top - cr + cr * math.sin(t)) for t in np.linspace(0, math.pi, 9)]
        a.rev('steel', [(ri, yb), (ro - .012, yb), (ro, yb + .02)] + bead + [(ri, yb + .02)], x=x, z=.75)
        a.rev('dark', [(ri - .012, top - .14), (ri + .002, top - .14), (ri + .002, top - .03), (ri - .012, top - .03)], x=x, z=.75)
        a.cyl('dark', ri, .02, x=x, y=top - .13, z=.75, sections=24)
        a.node(f'stack_{i}', a.key, (x, top, .75))
    return a

def S_barracks():
    a = Asset('barracks', 'structures', 2); pad(a, 2)
    # A soldier dispenser. A cream foot with a team band and two little toes; a smoke-plastic stem with grip slats; on
    # its front a clear display case where the next soldiers wait, stacked under the head like candy in a Pez (BarracksWorks
    # builds four: raw dough at the bottom, primed, then finished with a team helmet at the top, rising a slot per soldier
    # trained), with an oven strip at its foot that glows while one bakes; and the team flip-top head (head node, hinged at
    # its back edge) that the top one rises into and is spat out of, tucked, to land in front and roll to its feet
    # (pop: the mouth; land, exit: where it lands and where it stands up, and the unit appears).
    a.node('stage_2', a.key)
    cz = .12                                                       # the stem's axis is (0, cz)
    a.box('cream', 1.15, .4, .95, y=.28, z=.08)                   # the foot
    a.box('team', 1.19, .07, .99, y=.42, z=.08)
    for sx in (-1, 1): a.box('cream', .3, .09, .22, x=sx * .36, y=.125, z=-.44)   # toes
    a.box('smoke', .6, 1.1, .5, y=1.03, z=cz)                     # the stem
    for i in range(3): a.box('team', .03, .06, .34, x=.31, y=1.2 + i * .12, z=cz)   # grip slats, on the side
    # The display case on the front (-z) face: a cream frame, glass on three sides (the dark stem behind, so the figures
    # stand out), the oven strip below.
    zb, zf, xw, y0, y1 = cz - .25, cz - .47, .42, .62, 1.5
    for yy in (y0, y1): a.box('cream', xw + .08, .05, zb - zf + .02, y=yy, z=(zb + zf) / 2)
    for xx in (-xw / 2 - .02, xw / 2 + .02):
        for zz in (zb - .01, zf):                                  # corner posts; glass on three sides
            a.box('cream', .04, y1 - y0 + .05, .04, x=xx, y=(y0 + y1) / 2, z=zz)
        a.box('clear', .012, y1 - y0, zb - zf, x=xx, y=(y0 + y1) / 2, z=(zb + zf) / 2)
    a.box('clear', xw, y1 - y0, .012, y=(y0 + y1) / 2, z=zf)
    ov = a.node('oven', 'stage_2', (0, y0 + .035, (zb + zf) / 2))
    a.box('amber', xw - .04, .02, zb - zf - .06, node=ov)
    a.node('stack_lo', a.key, (0, y0 + .05, (zb + zf) / 2))
    a.node('stack_hi', a.key, (0, y1 - .25, (zb + zf) / 2))
    # The head: deep enough to roof the case, so the top soldier rises out of sight into it.
    hd = a.node('head', 'stage_2', (0, 1.58, cz + .27), rot=rx(6))
    a.box('team', .68, .34, .76, y=.17, z=-.38, node=hd)
    a.box('team', .6, .1, .1, y=.05, z=-.77, node=hd)              # the snout
    a.cyl('steel', .04, .7, axis='x', node=hd)
    a.node('pop', a.key, (0, 1.78, cz - .62))                      # just out of the mouth, clear of the case
    a.node('land', a.key, (0, 0, cz - 1.15))
    a.node('exit', a.key, (0, 0, cz - 1.5))
    # The flag: a pole at the back corner (left on screen); BarracksWorks hangs a cloth flag on the flag node that waves downwind.
    a.cyl('steel', .022, 1.5, x=.66, y=.83, z=.7)
    a.sphere('steel', .035, x=.66, y=1.59, z=.7)
    a.node('flag', 'stage_2', (.66, 1.54, .7))
    return a

def S_factory():
    """A Pez dispenser lying on its side, turning out vehicles. The door end is the dispenser's head; behind it the
    stem is an open tray (the candy channel) with a spring-loaded pusher that feeds each vehicle into the head; a
    gantry on the tray's walls carries a team head from the press at the back and drops it onto the hull; the press
    stamps heads off a foil wrapper roll. FactoryWorks runs all of it from the sim's production queue."""
    a = Asset('factory', 'structures', 3); pad(a, 3)
    s1, s2, s3 = a.node('stage_1', a.key), a.node('stage_2', a.key), a.node('stage_3', a.key)
    # The head house (the dispensing chamber) with the vehicle door in its face; units spawn inside it, 0.6 behind the
    # door. Lower half in stage 1, upper in stage 2, so the shell climbs in two steps.
    hz, hd = -.85, .7                                               # centre and depth: face at z -1.2, back at -0.5
    a.box('cream', 1.96, .37, hd, y=.265, z=hz)
    a.box('cream', 1.96, .5, hd, y=.7, z=hz)
    door(a, 1.5, .8, -1.21, anim=True)
    a.box('dark', .62, .34, .04, y=.64, z=-.49)                     # the chamber's mouth on the tray side
    a.node('chamber', s2, (0, .6, -.5))                              # where the pusher stops: the vehicle is in the head
    a.box('smoke', 1.56, .05, .6, y=.94, z=hz)                      # the neck: a dark seam under the head
    # The flip-top head: team, hinged at its back edge (the head node's pivot), with a chin over the door (the door is
    # its mouth) and two porthole eyes (the line's control room). FactoryWorks lifts it as the door rolls up, so the
    # head opens as each vehicle is dispensed.
    hd_ = a.node('head', s3, (0, .96, -.5))
    a.box('team', 1.62, .2, .66, y=.1, z=-.37, node=hd_)
    loaf = trimesh.creation.cylinder(radius=.33, height=1.62, sections=24)  # the head's rounded crown
    loaf.apply_transform(axis_rot('x')); loaf.apply_scale((1, .55, 1)); loaf.apply_translation((0, .2, -.37)); a.add(hd_, 'team', loaf)
    a.box('team', 1.5, .12, .14, y=0, z=-.74, node=hd_)             # chin over the door
    for sx in (-1, 1):
        a.cyl('steel', .1, .03, x=sx * .42, y=.17, z=-.705, axis='z', node=hd_, sections=12)
        a.cyl('dark', .072, .03, x=sx * .42, y=.17, z=-.718, axis='z', node=hd_, sections=12)
    a.cyl('steel', .04, 1.5, axis='x', node=hd_, sections=12)       # hinge pin
    # The stem: a cream base with smoke grip ribs, carrying the tray (smoke U-channel, steel floor, dark line rails).
    a.box('cream', 1.44, .37, 1.45, y=.265, z=.225)
    for sx in (-1, 1):
        for k in range(4): a.box('cream', .04, .24, .1, x=sx * .73, y=.25, z=-.27 + k * .34)
    for sx in (-1, 1):
        a.box('smoke', .15, .3, 1.35, x=sx * .545, y=.6, z=.175)    # tray walls
        a.box('steel', .07, .025, 1.35, x=sx * .545, y=.762, z=.175)  # the gantry's runway
    a.box('steel', .94, .02, 1.35, y=.46, z=.175)                   # tray floor
    for sx in (-1, 1): a.box('dark', .04, .03, 1.35, x=sx * .2, y=.485, z=.175)  # line rails
    # The spring and its pusher. The spring node's +y runs along -z from its seat on the press's front face;
    # FactoryWorks stretches it (scale y) to keep it on the pusher, which it moves with the vehicle's progress.
    zs, L0, zp = .85, .74, .5
    a.cyl('cream', .15, .03, y=.6, z=zs - .015, axis='z', sections=24)       # spring seat
    sp = a.node('spring', s2, (0, .6, zs), rot=rx(-90) @ np.diag([1, (zs - zp) / L0, 1, 1]))
    a.coil('cream', .13, .017, 0, L0, 8, node=sp, per_turn=16, sides=5)
    pu = a.node('pusher', s2, (0, .6, zp))
    a.box('steel', .84, .26, .05, node=pu)
    a.cyl('cream', .09, .06, z=.03, axis='z', node=pu, sections=12)
    # The vehicle on the line: a hull blank on tracks, facing the door; its team head (blank_head) is dropped on by the
    # gantry. Hidden while the line is empty.
    bl = a.node('blank', s2, (0, .47, zp - .3))
    for sx in (-1, 1): a.box('licorice', .1, .14, .5, x=sx * .17, y=.07, node=bl)
    a.box('cream', .26, .12, .46, y=.15, node=bl)                   # a raw cream blank: the candy before its head
    a.box('dark', .12, .02, .1, y=.215, z=-.15, node=bl)
    bh = a.node('blank_head', bl, (0, .21, .02), rot=np.diag([1e-3, 1e-3, 1e-3, 1]))  # not on yet (static pose)
    a.box('team', .22, .1, .2, y=.05, node=bh)
    a.box('dark', .22, .02, .02, y=.05, z=-.1, node=bh)
    # The gantry: end trucks on the runway, legs, a team bridge, a trolley; the hoist (hook block and the head it
    # carries, hook_head) hangs from the cable, which FactoryWorks stretches as it lets the hoist down.
    ga = a.node('gantry', s2, (0, .775, zp - .3))
    for sx in (-1, 1):
        a.box('steel', .1, .06, .22, x=sx * .545, y=.03, node=ga)
        a.strut('steel', (sx * .545, .05, -.06), (sx * .5, .33, 0), .022, node=ga, sections=6)
        a.strut('steel', (sx * .545, .05, .06), (sx * .5, .33, 0), .022, node=ga, sections=6)
    a.box('team', 1.18, .09, .12, y=.36, node=ga)
    a.box('steel', .16, .07, .17, y=.285, node=ga)
    hy = .02                                                       # the hoist's height under the bridge (static pose: half way down)
    cb = a.node('cable', ga, (0, .25, 0), rot=np.diag([1, (.25 - hy) / .1, 1, 1]))
    for sx in (-1, 1): a.strut('dark', (sx * .025, 0, 0), (sx * .025, -.1, 0), .006, node=cb, sections=6)
    ho = a.node('hoist', ga, (0, hy, 0))
    a.box('dark', .14, .05, .1, y=-.025, node=ho)
    hh = a.node('hook_head', ho, (0, -.15, 0))                     # each head node's origin is its base (hook_head,
    a.box('team', .22, .1, .2, y=.05, node=hh)                     # die_head, blank_head), so they can swap in place
    # The press at the back: a cream frame with a team crown, the ram (its own node) stamping onto a dark die. The
    # stamped head waits on the press's front edge (die_head) for the gantry.
    a.box('cream', 1.24, .67, .56, y=.415, z=1.13)
    a.box('dark', .5, .03, .3, y=.765, z=1.18)
    for sx in (-1, 1): a.box('cream', .16, .62, .32, x=sx * .5, y=1.06, z=1.18)
    a.box('team', 1.04, .12, .36, y=1.41, z=1.18)
    crown = trimesh.creation.cylinder(radius=.18, height=1.04, sections=20)  # the press's crown, a smaller team head
    crown.apply_transform(axis_rot('x')); crown.apply_scale((1, .55, 1)); crown.apply_translation((0, 1.46, 1.18)); a.add(None, 'team', crown)
    a.box('steel', .7, .04, .42, y=1.33, z=1.18)
    rm = a.node('ram', s3, (0, 1.31, 1.18))
    a.cyl('steel', .06, .2, y=-.06, node=rm, sections=10)
    a.box('steel', .5, .14, .26, y=-.2, node=rm)
    dh = a.node('die_head', s2, (0, .75, .93))
    a.box('team', .22, .1, .2, y=.05, node=dh)
    # The foil wrapper roll on its cradle beside the press, feeding its strip in; spool turns as the press indexes.
    sx_, sz_ = 1.06, 1.05
    for dz in (-.2, .2): a.box('steel', .26, .3, .05, x=sx_, y=.23, z=sz_ + dz)
    spl = a.node('spool', s2, (sx_, .4, sz_))
    a.cyl('foil', .27, .3, axis='z', node=spl, sections=18)
    for dz in (-.16, .16): a.cyl('cream', .3, .03, z=dz, axis='z', node=spl, sections=18)
    a.cyl('steel', .05, .44, axis='z', node=spl, sections=10)
    for k in range(3): a.box('steel', .03, .5, .02, z=-.175, rot=rz(60 * k), node=spl)
    a.box('foil', .46, .012, .22, x=.85, y=.672, z=1.05)               # the strip into the press
    # The press's exhaust stack in the back corner; stack_0 marks its mouth.
    kx, kz = -1.06, 1.06
    a.cyl('cream', .22, .22, x=kx, y=.19, z=kz, sections=24)
    a.rev('cream', [(0, .3), (.13, .3), (.09, 1.55), (0, 1.55)], x=kx, z=kz)
    a.cyl('team', .115, .09, x=kx, y=1.3, z=kz, sections=24)
    a.cyl('steel', .11, .06, x=kx, y=1.56, z=kz, sections=16)
    a.cyl('dark', .075, .02, x=kx, y=1.59, z=kz, sections=16)
    a.node('stack_0', a.key, (kx, 1.6, kz))
    # Spare tyres stacked in the yard.
    for k in range(2): a.cyl('licorice', .13, .07, x=1.1, y=.12 + k * .075, z=-.25, sections=12)
    return a

def S_electronics_plant():
    # A circuit-candy line on a clean-room hall. Blank boards drop out of a steel magazine onto a belt, the pick-and-place
    # carriage (a little team head on a gantry) brings copper chips from the reel's feeder and pecks one onto each board,
    # the reflow oven cures them (its vent fumes), and the finished boards stack up in the dispenser stem until its team
    # flip-top head pops and dispenses the batch. CircuitWorks in Unity runs it all from the sim (Entity.Working): one
    # board a second, half that on low power, parked with the amber lamp lit when the plant has no copper or steel.
    # Build stages: the hall is stage_1, the machinery on its roof stage_2, the dispenser stem and the signal tower
    # stage_3 (given by hand, so each moving part rises with what carries it).
    a = Asset('electronics_plant', 'structures', 2); pad(a, 2)
    S2, S3 = a.node('stage_2', a.key), a.node('stage_3', a.key)
    a.node('stage_1', a.key)
    deck = .5                                                            # the hall's roof
    a.box('cream', 1.72, deck - .08, 1.58, y=.08 + (deck - .08) / 2, z=-.02)
    a.box('team', 1.76, .11, 1.62, y=deck - .06, z=-.02)                # wrapper band
    a.box('cream', 1.6, .03, 1.46, y=deck + .005, z=-.02)                # roof deck
    # Clean-room windows (the panes node: lit while the line runs, dim when it stands; CircuitWorks) in a dark band on
    # both faces the camera sees, and the door round the corner from them.
    pn = a.node('panes', 'stage_1')
    a.box('dark', .8, .17, .02, x=.36, y=.27, z=-.815)
    for x in (.62, .36, .1): a.box('acid', .19, .13, .03, x=x, y=.27, z=-.82, node=pn)
    a.box('dark', .02, .17, 1.27, x=.865, y=.27, z=-.03)
    for z in (-.55, -.29, -.03, .23, .49): a.box('acid', .03, .13, .19, x=.87, y=.27, z=z, node=pn)
    a.box('dark', .26, .32, .06, x=-.42, y=.24, z=-.82)                  # door
    a.box('licorice', .32, .05, .07, x=-.42, y=.425, z=-.82)
    # The belt along x at z = .2, from the magazine (+x) through the oven into the stem (-x).
    bz, by = .2, deck + .1
    a.box('smoke', 1.3, .08, .26, x=-.05, y=deck + .055, z=bz, node=S2)  # belt frame
    a.box('licorice', 1.26, .02, .2, x=-.05, y=by, z=bz, node=S2)        # belt
    for x in (.58, -.68): a.cyl('steel', .045, .24, x=x, y=by - .015, z=bz, axis='z', node=S2, sections=10)
    # Blank-board magazine over the belt's start: four steel posts round a stack of blanks (foil: the steel input; the
    # blanks node is hidden while the team is out of steel) under a team cap.
    mx = .47
    for sx in (-1, 1):
        for sz in (-1, 1): a.cyl('steel', .016, .3, x=mx + sx * .1, y=by + .18, z=bz + sz * .11, node=S2, sections=6)
    bl = a.node('blanks', S2, (mx, by, bz))
    for k in range(5): a.box('foil', .17, .022, .2, y=.1 + k * .04, node=bl)
    a.box('team', .25, .04, .27, x=mx, y=by + .34, z=bz, node=S2)
    # Pick-and-place gantry across the belt at the placing station (over the second board slot): a steel bridge on two
    # legs; the carriage (gantry node: runs along z between the feeder and the board) is a little team head with a
    # snout, and its nozzle (nozzle node) dips to pick a chip (held) and place it.
    gx, gy = mx - .23, 1.02
    for z in (-.66, .5):
        a.box('steel', .06, gy - deck, .06, x=gx, y=deck + (gy - deck) / 2, z=z, node=S2)
        a.box('steel', .14, .03, .12, x=gx, y=deck + .02, z=z, node=S2)
    for sx in (-1, 1): a.box('steel', .03, .05, 1.22, x=gx + sx * .055, y=gy + .02, z=-.08, node=S2)
    car = a.node('gantry', S2, (gx, gy, -.4))
    a.box('team', .2, .16, .2, y=-.01, node=car)
    a.box('team', .16, .05, .06, y=-.07, z=-.12, node=car)               # snout
    a.box('dark', .14, .03, .01, y=.02, z=-.105, node=car)               # visor
    a.cyl('steel', .03, .04, y=-.11, node=car, sections=10)
    nz = a.node('nozzle', car, (0, -.06, 0))
    a.cyl('steel', .012, .2, y=-.1, node=nz, sections=6)
    a.cyl('dark', .02, .04, y=-.2, node=nz, sections=8)
    hc = a.node('held', nz, (0, -.235, 0))
    a.box('copper', .05, .025, .05, node=hc)
    # Copper reel and its feeder in front of the gantry: the tape runs off the reel's foot along the feeder with copper
    # chips to the pick point under the carriage's home. The reel (reel node) turns while the line runs; its copper
    # (winding) runs down to the hub while the team is out of copper.
    fz = -.4
    for sz in (-1, 1): a.box('steel', .05, .26, .03, x=.58, y=deck + .13, z=fz + sz * .065, node=S2)
    rl = a.node('reel', S2, (.58, deck + .23, fz))
    wd = a.node('winding', rl)
    a.cyl('copper', .2, .07, axis='z', node=wd, sections=20)
    a.cyl('cream', .215, .014, z=.045, axis='z', node=rl, sections=20)   # back flange (the empty spool shows it)
    a.cyl('cream', .075, .1, axis='z', node=rl, sections=12)             # hub
    for k in range(3):                                                   # cream spokes on its face, so it reads turning
        a.box('cream', .022, .19, .012, z=-.04, rot=rz(120 * k) @ T((0, .09, 0)), node=rl)
    a.box('dark', .5, .035, .1, x=.17, y=deck + .04, z=fz, node=S2)      # feeder track
    for k in range(3): a.box('copper', .045, .03, .05, x=.33 - k * .085, y=deck + .07, z=fz, node=S2)
    # Reflow oven: a cream tunnel over the belt with a team band and a dark mouth each end; its vent stack's mouth (fume)
    # is where the solder fumes leave.
    ox = -.26
    a.box('cream', .3, .22, .34, x=ox, y=by + .1, z=bz, node=S2)
    a.box('team', .32, .05, .36, x=ox, y=by + .2, z=bz, node=S2)
    for x in (ox + .155, ox - .155): a.box('licorice', .02, .1, .22, x=x, y=by + .045, z=bz, node=S2)
    a.cyl('steel', .045, .3, x=ox, y=by + .36, z=bz + .08, node=S2, sections=10)
    a.cyl('steel', .07, .03, x=ox, y=by + .5, z=bz + .08, node=S2, sections=12)
    a.node('fume', a.key, (ox, by + .54, bz + .08))
    # Boards on the belt (board_0..4, each with the chip the carriage places on it, chip_N): CircuitWorks indexes them
    # along the belt one slot a cycle, from under the magazine into the stem.
    for k in range(5):
        b = a.node(f'board_{k}', S2, (mx - k * .23, by + .025, bz))
        a.box('mint', .16, .025, .17, node=b)
        c = a.node(f'chip_{k}', b, (0, .022, 0))
        a.box('copper', .06, .025, .06, node=c)
    # Clean-room air handler in the back corner: a cream box with a team cap and a filter fan (fan node: turns while the
    # plant has power, half speed on low power).
    hx, hz = .55, .45
    a.box('cream', .42, .2, .4, x=hx, y=deck + .1, z=hz, node=S2)
    a.box('team', .44, .05, .42, x=hx, y=deck + .2, z=hz, node=S2)
    a.cyl('dark', .15, .02, x=hx, y=deck + .23, z=hz, node=S2, sections=20)
    fn = a.node('fan', S2, (hx, deck + .25, hz))
    a.cyl('steel', .035, .03, node=fn, sections=10)
    for k in range(4): a.box('foil', .27, .012, .055, node=fn, rot=ry(45 * k) @ rx(18))
    # Clean-room skylight in the front-right quarter: the lit room under frosted glass in a steel grid, part of the panes
    # (from the high camera it's the plant's biggest light, so it says working or standing from across the map).
    kx, kz, kw, kd = -.33, -.43, .54, .5
    a.box('team', kw + .08, .06, kd + .08, x=kx, y=deck + .03, z=kz, node=S2)
    a.box('acid', kw - .02, .02, kd - .02, x=kx, y=deck + .055, z=kz, node=pn)
    a.box('glass', kw, .02, kd, x=kx, y=deck + .075, z=kz, node=S2)
    for k in range(-1, 2): a.box('steel', .022, .025, kd, x=kx + k * kw / 4, y=deck + .085, z=kz, node=S2)
    a.box('steel', kw, .025, .022, x=kx, y=deck + .085, z=kz, node=S2)
    # The dispenser stem: smoke-plastic walls round an open window (the finished boards stack inside it, mag_0..4),
    # grip slats in team colour, and the team flip-top head hinged at its back edge (the head node), popped by
    # CircuitWorks every five boards to dispense the batch (candy is the board it pops out).
    sx_, sz_, s0, s1, w = -.62, .2, deck, 1.36, .36
    a.box('smoke', w, s1 - s0, .05, x=sx_, y=(s0 + s1) / 2, z=sz_ + w / 2 - .025, node=S3)                 # back
    for s in (-1, 1): a.box('smoke', .05, s1 - s0, w, x=sx_ + s * (w / 2 - .025), y=(s0 + s1) / 2, z=sz_, node=S3)
    a.box('smoke', w, .18, .05, x=sx_, y=s0 + .09, z=sz_ - w / 2 + .025, node=S3)                           # under the window
    a.box('smoke', w, .08, .05, x=sx_, y=s1 - .04, z=sz_ - w / 2 + .025, node=S3)                           # over it
    a.box('licorice', .03, .1, .22, x=sx_ + w / 2 - .01, y=by + .04, z=bz, node=S3)                          # the belt goes in
    for i in range(3): a.box('team', .03, .06, .26, x=sx_ + w / 2 + .005, y=.86 + i * .13, z=sz_, node=S3)
    a.box('dark', w - .06, .04, .06, x=sx_, y=s1 + .02, z=sz_ - w / 2 + .04, node=S3)                       # dispensing lip
    for k in range(5):
        m = a.node(f'mag_{k}', S3, (sx_, s0 + .23 + k * .065, sz_))
        a.box('mint', .26, .03, .26, node=m)
        a.box('copper', .07, .025, .07, x=.04, y=.025, z=-.03, node=m)
    hd = a.node('head', S3, (sx_, s1 + .02, sz_ + w / 2), rot=rx(8))
    a.box('team', w + .06, .22, w + .04, y=.12, z=-(w + .04) / 2, node=hd)
    a.box('team', w - .02, .08, .08, y=.03, z=-(w + .04) + .01, node=hd)                                    # snout over the lip
    a.cyl('steel', .03, w + .04, axis='x', node=hd, sections=8)                                             # hinge pin
    cd = a.node('candy', S3, (sx_, s1 + .06, sz_ - w / 2 + .05))
    a.box('mint', .18, .025, .18, node=cd)
    a.box('copper', .06, .025, .06, y=.02, node=cd)
    # Signal tower on the front-right corner: run (acid, lamp_run) over waiting-for-inputs (amber, lamp_wait), on a pole.
    lx, lz = -.74, -.69
    a.cyl('steel', .05, .04, x=lx, y=deck + .02, z=lz, node=S3, sections=10)
    a.cyl('steel', .018, .24, x=lx, y=deck + .14, z=lz, node=S3, sections=6)
    for name, m, y in (('lamp_wait', 'amber', .31), ('lamp_run', 'acid', .42)):
        ln = a.node(name, S3, (lx, deck + y, lz))
        a.cyl(m, .065, .095, node=ln, sections=14)
        a.cyl('dark', .068, .012, y=-.053, node=ln, sections=14)
    a.cyl('team', .068, .035, x=lx, y=deck + .485, z=lz, node=S3, sections=14)
    return a

def S_optics_lab():
    a = Asset('optics_lab', 'structures', 2); pad(a, 2)
    # Grinds crystal into lenses (Defs: 2 crystal -> 1 lens, 0.5/s; -40 power). An observatory drum with a turning team
    # dome and its telescope; the light it gathers comes down the drum and out of a collimator on the front, through a
    # glass prism that splits it into three rays grinding lens blanks on a turntable (the lathe). Crystal waits in a
    # hopper on the right; finished lenses stack up in the dispenser on the left, a Pez stem whose team head flips back
    # to give one out. OpticsWorks drives the moving parts: head (yaw), scope (pitch), light and rays (lit while it
    # works), lathe (turns), stack, candy and cap (a lens dispensed every 2 s at full power).
    for s in ('stage_1', 'stage_2', 'stage_3'): a.node(s, a.key)
    dx, dz = -.18, .2
    a.cyl('cream', .55, .52, x=dx, y=.34, z=dz, sections=20)
    a.cyl('steel', .575, .04, x=dx, y=.6, z=dz, sections=20)                   # the dome's rail
    for ang in (15, -105, -150):                                                # portholes
        r = math.radians(ang)
        a.cyl('dark', .055, .04, axis='x', rot=ry(-ang), x=dx + .55 * math.cos(r), y=.3, z=dz + .55 * math.sin(r), sections=8)
    # The dome: the lab's team head, turning on its rail, its observing slit open from the rail to 70 degrees up and the
    # telescope looking out of it (local +z).
    hd = a.node('head', 'stage_3', (dx, .62, dz), rot=ry(-165))
    R0, n, gap = .54, 36, 3                                                    # 10-degree segments; the slit is 3 wide
    V = [(R0 * math.cos(math.radians(e)) * math.sin(math.radians(10 * k - 5 * gap)), R0 * math.sin(math.radians(e)), R0 * math.cos(math.radians(e)) * math.cos(math.radians(10 * k - 5 * gap)))
         for e in range(0, 81, 10) for k in range(n)] + [(0, R0, 0)]
    F = []
    for r in range(9):
        for k in range(n):
            k1 = (k + 1) % n
            if r < 7 and k < gap: continue                                      # the slit, centred on +z
            if r < 8: F += [[r * n + k, r * n + k1, (r + 1) * n + k1], [r * n + k, (r + 1) * n + k1, (r + 1) * n + k]]
            else: F.append([r * n + k, r * n + k1, 9 * n])
    dome = trimesh.Trimesh(np.array(V), np.array(F), process=False)
    flip = np.einsum('ij,ij->i', dome.face_normals, dome.triangles_center) < 0
    F = np.array(F); F[flip] = F[flip][:, ::-1]
    a.add(hd, 'team', trimesh.Trimesh(np.array(V), F))
    ri = R0 - .025
    inner = trimesh.creation.revolve(np.array([(ri * math.cos(math.radians(t)), ri * math.sin(math.radians(t))) for t in range(0, 91, 15)]), sections=16)
    inner.apply_transform(axis_rot('y')); a.add(hd, 'dark', inner)          # the dark inside, seen through the slit
    for sx in (-1, 1):                                                          # the slit's shutter rails
        g = math.radians(5 * gap + 3) * sx
        a.tube('steel', [(R0 * math.cos(math.radians(t)) * math.sin(g), R0 * math.sin(math.radians(t)) + .01, R0 * math.cos(math.radians(t)) * math.cos(g))
                         for t in range(0, 75, 7)], .02, node=hd)
    sc = a.node('scope', hd, (0, .22, .1), rot=rx(-34))
    a.cyl('steel', .035, .26, axis='x', node=sc, sections=6)                    # trunnion
    a.cyl('cream', .1, .9, z=.2, axis='z', node=sc, sections=12)
    for z in (.3, .52): a.cyl('team', .11, .05, z=z, axis='z', node=sc, sections=12)
    a.cyl('steel', .115, .16, z=.7, axis='z', node=sc, sections=12)             # dew shield
    a.cyl('cyan', .09, .02, z=.77, axis='z', node=sc, sections=12)             # objective: lit while it observes
    a.cyl('foil', .032, .24, y=.13, z=.42, axis='z', node=sc, sections=6)      # finder
    for z in (.34, .5): a.strut('steel', (0, .09, z), (0, .12, z), .012, node=sc)
    # The bench in front: a collimator out of the drum, the prism, and the lathe's turntable.
    u = np.array([math.cos(math.radians(-45)), 0, math.sin(math.radians(-45))])
    w = np.array([-u[2], 0, u[0]])
    P0 = np.array([dx, 0, dz]) + .55 * u
    def at(k, y, side=0.): q = P0 + k * u + side * w; return (q[0], y, q[2])
    a.strut('steel', at(-.06, .36), at(.1, .36), .055, sections=10)
    a.cyl('foil', .065, .04, axis='x', rot=ry(45), x=at(.1, 0)[0], y=.36, z=at(.1, 0)[2], sections=8)
    a.box('cream', .32, .05, .7, rot=ry(-45), x=at(.42, 0)[0], y=.24, z=at(.42, 0)[2])     # the bench top
    a.box('team', .34, .03, .72, rot=ry(-45), x=at(.42, 0)[0], y=.205, z=at(.42, 0)[2])
    for k in (.14, .7):
        a.cyl('steel', .04, .14, x=at(k, 0)[0], y=.14, z=at(k, 0)[2], sections=6)
    qx, _, qz = at(.36, 0)
    a.cyl('dark', .14, .02, x=qx, y=.275, z=qz, sections=3, rot=ry(20))        # the prism in its frame
    a.cyl('glass', .13, .2, x=qx, y=.385, z=qz, sections=3, rot=ry(20))
    a.cyl('dark', .14, .02, x=qx, y=.495, z=qz, sections=3, rot=ry(20))
    bm = a.node('light', 'stage_1', at(.11, .36))
    a.strut('cyan', (0, 0, 0), tuple(np.array(at(.31, .37)) - np.array(at(.11, .36))), .024, node=bm, sections=6)
    ry_ = a.node('rays', 'stage_1', (qx, .37, qz))
    a.cyl('cyan', .095, .16, y=.015, node=ry_, sections=3, rot=ry(20))           # the prism lit from inside
    C = at(.68, 0)
    a.cyl('steel', .06, .08, x=C[0], y=.29, z=C[2], sections=8)
    for s_ in (-.1, 0, .1):
        e = np.array(at(.68, .37, s_)) - np.array([qx, .37, qz])
        a.strut('cyan', tuple(.12 * e / np.linalg.norm(e)), tuple(e), .013, node=ry_, sections=6)
    lt = a.node('lathe', 'stage_1', (C[0], .34, C[2]))
    a.cyl('cream', .17, .04, node=lt, sections=12)
    a.cyl('team', .18, .025, y=-.025, node=lt, sections=12)
    for k in range(4):
        t = math.radians(90 * k + 45)
        a.cyl('glass', .045, .02, x=.11 * math.cos(t), y=.03, z=.11 * math.sin(t), node=lt, sections=8)
    a.cyl('steel', .025, .04, y=.035, node=lt, sections=6)
    # Crystal hopper on the right, its chute running to the lathe.
    hx, hz = -.58, -.55
    for sx in (-1, 1):
        for sz in (-1, 1): a.cyl('steel', .022, .32, x=hx + sx * .13, y=.24, z=hz + sz * .13, sections=6)
    a.rev('cream', [(.06, .3), (.25, .58), (.25, .62), (.21, .62), (.05, .36), (.0, .36), (0, .3)], x=hx, z=hz, sections=20)
    a.torus('team', .24, .035, x=hx, y=.62, z=hz)
    for ox, oz, h, tilt in ((0, 0, .26, 0), (.09, .05, .2, 14), (-.08, .07, .18, -12), (.04, -.1, .2, 10), (-.09, -.05, .16, -16), (.12, -.06, .14, 20)):
        c = trimesh.Trimesh([[.05, 0, 0], [-.05, 0, 0], [0, 0, .05], [0, 0, -.05], [0, h, 0], [0, -h * .4, 0]],
                            [[4, 0, 2], [4, 2, 1], [4, 1, 3], [4, 3, 0], [5, 2, 0], [5, 1, 2], [5, 3, 1], [5, 0, 3]])
        c.fix_normals(); c.apply_transform(rx(tilt) @ rz(-tilt) @ ry(tilt * 3)); c.apply_translation((hx + ox, .5, hz + oz)); a.add(None, 'cyan', c)   # crystal shards
    a.tube('steel', [(hx, .3, hz), (hx + .25, .28, hz - .12), at(.68, .3, -.19)], .03)
    # The dispenser: a lens Pez. A cream foot, the spring, a smoke stem open at the front with the lens candies stacked
    # in it, and the team head on a hinge at the back, with a loupe for an eye.
    S = np.array([.6, 0, .56])
    rot = ry(-45)
    def L(x, y, z): q = S + rot[:3, :3] @ np.array([x, y, z]); return (q[0], q[1], q[2])
    a.cyl('cream', .2, .12, x=S[0], y=.14, z=S[2], sections=12)
    a.cyl('team', .205, .04, x=S[0], y=.18, z=S[2], sections=12)
    a.box('smoke', .26, .9, .06, rot=rot, x=L(0, 0, .1)[0], y=.65, z=L(0, 0, .1)[2])        # back
    for sx in (-1, 1):
        a.box('smoke', .05, .9, .2, rot=rot, x=L(sx * .13, 0, 0)[0], y=.65, z=L(sx * .13, 0, 0)[2])  # side rails
        for k in range(5): a.box('smoke', .02, .03, .14, rot=rot, x=L(sx * .16, 0, 0)[0], y=.42 + k * .1, z=L(sx * .16, 0, 0)[2])  # grip ribs
    a.coil('steel', .07, .012, .2, .3, 3, x=S[0], z=S[2])
    st = a.node('stack', 'stage_2', (S[0], 0, S[2]))
    for k in range(5): a.cyl('cyan', .085, .07, y=.36 + k * .12, node=st, sections=6)
    cd = a.node('candy', 'stage_2', (S[0], .96, S[2]))
    a.cyl('cyan', .085, .07, node=cd, sections=6)
    cp = a.node('cap', 'stage_2', L(0, 1.1, .12), rot=rot)
    a.box('team', .3, .2, .3, y=.1, z=-.13, node=cp)
    a.box('team', .26, .07, .1, y=-.01, z=-.3, node=cp)                         # the snout over the throat
    a.cyl('steel', .035, .3, axis='x', node=cp, sections=6)                    # hinge pin
    a.cyl('foil', .075, .03, axis='z', y=.12, z=-.285, node=cp, sections=12)    # the loupe
    a.cyl('cyan', .055, .02, axis='z', y=.12, z=-.3, node=cp, sections=12)
    # The lens line: from the lathe round the left side to the dispenser's foot.
    a.tube('steel', [at(.68, .3, .19), (.82, .17, -.3), (.82, .17, .3), (S[0] + .16, .17, S[2] - .06)], .03)
    return a

def S_enrichment_plant():
    # A centrifuge hall. Uranium comes in at the back: a cream hopper heaped with the ore feeds a chute through a sight
    # glass into the hall, where a cascade of six squat rotors (rotor_0..5: foil drums with team caps) spins under a
    # skylight, piped together along a header. The enriched product comes out at the far end as plasma candy: magenta
    # bricks stacking up behind the window of the dispenser stem on the service block (mag_0..3), under the team flip-top
    # head (head, hinged at its back edge), which pops every fourth brick and flicks one out (candy). EnrichWorks runs it
    # from the sim: one brick per plasma (2.5 s; 5 s on low power), the rotors spinning up and down with the state, the
    # hopper's ore (ore) draining when the uranium runs out.
    # Build stages: the hall and the rotors stage_1, the skylight and the hopper stage_2, the stem and its head stage_3.
    a = Asset('enrichment_plant', 'structures', 2); pad(a, 2)
    S1, S2, S3 = a.node('stage_1', a.key), a.node('stage_2', a.key), a.node('stage_3', a.key)
    X0, X1, Z0, Z1, H = -.84, .84, -.8, .56, .55      # the hall
    XS, HS = -.3, .68                                  # the service block (-x of XS, a little taller) / the cascade bay
    t, fl = .1, .2                                     # the bay's walls; its floor
    bx0, bx1, bz0, bz1 = XS, X1 - t, Z0 + t, Z1 - t    # the bay's inside
    # The service block: solid cream, its own team band, a door on the front.
    a.box('cream', XS - X0, HS - .08, Z1 - Z0, x=(X0 + XS) / 2, y=.08 + (HS - .08) / 2, z=(Z0 + Z1) / 2, node=S1)
    a.box('team', XS - X0 + .04, .09, Z1 - Z0 + .04, x=(X0 + XS) / 2 - .02, y=HS - .12, z=(Z0 + Z1) / 2, node=S1)
    a.box('dark', .24, .28, .03, x=-.6, y=.22, z=Z0 - .005, node=S1)
    a.box('licorice', .3, .04, .04, x=-.6, y=.375, z=Z0 - .005, node=S1)
    # The bay: a smoke floor, cream walls with a row of windows on the two faces the camera sees, the team band.
    a.box('smoke', bx1 - bx0, fl - .08, bz1 - bz0, x=(bx0 + bx1) / 2, y=.08 + (fl - .08) / 2, z=(bz0 + bz1) / 2, node=S1)
    a.box('cream', X1 - XS, H - .08, t, x=(XS + X1) / 2, y=.08 + (H - .08) / 2, z=Z1 - t / 2, node=S1)        # back wall
    w0, w1 = .26, .38                                                                                       # windows
    for (y0, y1) in ((.08, w0), (w1, H)):
        a.box('cream', X1 - XS, y1 - y0, t, x=(XS + X1) / 2, y=(y0 + y1) / 2, z=Z0 + t / 2, node=S1)       # front
        a.box('cream', t, y1 - y0, bz1 - bz0, x=X1 - t / 2, y=(y0 + y1) / 2, z=(bz0 + bz1) / 2, node=S1)   # side
    for x in (.07, .41):
        a.box('cream', .08, w1 - w0, t, x=x, y=(w0 + w1) / 2, z=Z0 + t / 2, node=S1)
    a.box('cream', t, w1 - w0, t, x=X1 - t / 2, y=(w0 + w1) / 2, z=Z0 + t / 2, node=S1)
    a.box('cream', t, w1 - w0, .08, x=X1 - t / 2, y=(w0 + w1) / 2, z=-.12, node=S1)
    a.box('cream', t, w1 - w0, t, x=X1 - t / 2, y=(w0 + w1) / 2, z=Z1 - t / 2, node=S1)
    a.box('clear', X1 - XS - .1, w1 - w0, .01, x=(XS + X1) / 2 - .05, y=(w0 + w1) / 2, z=Z0 + t / 2, node=S1)
    a.box('clear', .01, w1 - w0, bz1 - bz0, x=X1 - t / 2, y=(w0 + w1) / 2, z=(bz0 + bz1) / 2, node=S1)
    for (sx, sz, x, z) in ((X1 - XS + .02, .03, (XS + X1) / 2 + .01, Z0 - .005), (.03, Z1 - Z0 + .02, X1 + .005, (Z0 + Z1) / 2),
                           (X1 - XS + .02, .03, (XS + X1) / 2 + .01, Z1 + .005)):
        a.box('team', sx, .09, sz, x=x, y=H - .1, z=z, node=S1)
    # The skylight: a low glass roof pitched to a cream ridge over the header, on cream rafters.
    rz_, rh = -.12, .1                                                   # the ridge's z and its rise
    for (za, zb) in ((bz0, rz_), (rz_, bz1)):
        run = zb - za; ang = math.degrees(math.atan2(rh, run)) * (1 if za == bz0 else -1)
        L = math.hypot(run, rh)
        a.box('clear', bx1 - bx0, .012, L, x=(bx0 + bx1) / 2, y=H - .01 + rh / 2, z=(za + zb) / 2, rot=rx(-ang), node=S2)
        for x in (bx0 + .02, .24, bx1 - .02):
            a.box('cream', .04, .035, L, x=x, y=H + rh / 2, z=(za + zb) / 2, rot=rx(-ang), node=S2)
    a.box('cream', bx1 - bx0 + .04, .06, .06, x=(bx0 + bx1) / 2, y=H + rh, z=rz_, node=S2)
    gab = trimesh.convex.convex_hull(np.array([[bx1 - .005, H - .01, bz0], [bx1 - .005, H - .01, bz1], [bx1 - .005, H + rh - .01, rz_],
                                               [bx1 - .015, H - .01, bz0], [bx1 - .015, H - .01, bz1], [bx1 - .015, H + rh - .01, rz_]]))
    a.add(S2, 'clear', gab)
    # The cascade: six rotors in two rows, each in a dark collar on the floor, piped to the header between the rows that
    # runs into the service block.
    k = 0
    for z in (-.44, .2):
        for x in (.57, .24, -.1):
            r = a.node(f'rotor_{k}', S1, (x, fl, z)); k += 1
            a.cyl('foil', .12, .24, y=.12, node=r, sections=10)
            a.box('licorice', .03, .2, .02, x=.117, y=.12, node=r)                   # a stripe, so its turning shows
            a.cyl('team', .135, .07, y=.275, node=r, sections=10)
            a.box('licorice', .22, .02, .045, y=.315, node=r)
    hz = -.12
    a.cyl('steel', .035, bx1 - bx0 + .02, x=(bx0 + bx1) / 2 - .01, y=fl + .06, z=hz, axis='x', node=S1, sections=8)
    for x in (.57, .24, -.1): a.box('steel', .035, .035, .3, x=x, y=fl + .04, z=hz, node=S1)
    # The service block's roof: the exhaust vent (vent: where it puffs) and the waiting lamp (lamp_wait) on a post.
    a.cyl('steel', .07, .2, x=-.7, y=HS + .1, z=-.55, node=S2, sections=10)
    a.cyl('team', .08, .05, x=-.7, y=HS + .14, z=-.55, node=S2, sections=10)
    a.cyl('steel', .085, .03, x=-.7, y=HS + .2, z=-.55, node=S2, sections=10)
    a.cyl('dark', .055, .01, x=-.7, y=HS + .215, z=-.55, node=S2, sections=12)
    a.node('vent', a.key, (-.7, HS + .23, -.55))
    a.cyl('steel', .02, .12, x=-.42, y=HS + .06, z=-.66, node=S2, sections=6)
    ln = a.node('lamp_wait', S2, (-.42, HS + .165, -.66))
    a.cyl('amber', .068, .09, node=ln, sections=12)
    a.cyl('team', .072, .03, y=.058, node=ln, sections=12)
    # The uranium hopper behind the bay: a cream funnel with a team rim, heaped with ore (ore node) on its dark bed, on
    # a cream pedestal with a sight glass on its side (sight: lit while ore runs through) and a chute into the back wall.
    hx, hzz = .42, .76
    a.box('cream', .3, .44, .26, x=hx, y=.3, z=hzz + .02, node=S2)                      # its pedestal
    a.box('team', .32, .06, .28, x=hx, y=.44, z=hzz + .02, node=S2)
    a.rev('cream', [(0, .52), (.07, .52), (.24, .86), (.22, .88), (0, .8)], x=hx, z=hzz, node=S2, sections=16)
    a.rev('team', [(.2, .78), (.215, .78), (.255, .88), (.235, .89)], x=hx, z=hzz, node=S2, sections=16)
    a.cyl('dark', .2, .02, x=hx, y=.84, z=hzz, node=S2, sections=16)
    orn = a.node('ore', S2, (hx, .85, hzz))
    for (dx, dz, r) in ((.02, .01, .1), (.1, .07, .065), (-.09, .03, .06), (-.03, -.1, .055)):
        a.sphere('uranium', r, x=dx, y=-.04 + r * .5, z=dz, node=orn, sub=1)
    a.box('steel', .12, .1, .12, x=hx, y=.3, z=Z1 + .05, node=S2)                      # the chute into the hall
    a.cyl('steel', .075, .03, x=hx + .155, y=.3, z=hzz + .02, axis='x', node=S2, sections=10)   # the sight glass's bezel
    sg = a.node('sight', S2, (hx + .17, .3, hzz + .02))
    a.cyl('acid', .05, .02, axis='x', node=sg, sections=10)
    # The dispenser stem on the service block: smoke plastic round a window on its front where the plasma bricks stack,
    # team grip slats on its side, a dark lip, and the team flip-top head hinged at its back edge.
    sx_, sz_, s0, s1, w = -.57, .06, HS, 1.44, .38
    a.box('smoke', w, s1 - s0, .05, x=sx_, y=(s0 + s1) / 2, z=sz_ + w / 2 - .025, node=S3)
    for s in (-1, 1): a.box('smoke', .05, s1 - s0, w, x=sx_ + s * (w / 2 - .025), y=(s0 + s1) / 2, z=sz_, node=S3)
    a.box('smoke', w, .14, .05, x=sx_, y=s0 + .07, z=sz_ - w / 2 + .025, node=S3)
    a.box('smoke', w, .1, .05, x=sx_, y=s1 - .05, z=sz_ - w / 2 + .025, node=S3)
    a.box('clear', w - .1, s1 - s0 - .24, .01, x=sx_, y=(s0 + .14 + s1 - .1) / 2, z=sz_ - w / 2 + .01, node=S3)
    for i in range(3): a.box('team', .03, .06, .26, x=sx_ + w / 2 + .005, y=.92 + i * .13, z=sz_, node=S3)
    a.box('dark', w - .06, .04, .06, x=sx_, y=s1 + .02, z=sz_ - w / 2 + .04, node=S3)
    for i in range(4):
        m = a.node(f'mag_{i}', S3, (sx_, s0 + .2 + i * .12, sz_ - .02))
        a.box('magenta', .26, .09, .2, node=m)
    hd = a.node('head', S3, (sx_, s1 + .02, sz_ + w / 2), rot=rx(8))
    a.box('team', w + .06, .22, w + .04, y=.12, z=-(w + .04) / 2, node=hd)
    a.box('team', w - .02, .08, .08, y=.03, z=-(w + .04) + .01, node=hd)
    a.cyl('steel', .03, w + .04, axis='x', node=hd, sections=8)
    cd = a.node('candy', S3, (sx_, s1 + .07, sz_ - w / 2 + .06))
    a.box('magenta', .2, .07, .15, node=cd)
    return a

def crystal(a, m, x, y, z, r, h, lean=0, turn=0, node=None):
    """A hexagonal crystal standing on (x, y, z): a six-sided prism with a pointed tip, leaning `lean` deg toward `turn`."""
    ang = np.linspace(0, 2 * math.pi, 6, endpoint=False)
    ring = np.c_[r * np.cos(ang), np.zeros(6), r * np.sin(ang)]
    pts = np.vstack([ring, ring + [0, h * .7, 0], [[0, h, 0]]])
    c = trimesh.convex.convex_hull(pts)
    c.apply_transform(ry(turn) @ rz(lean)); c.apply_translation((x, y, z)); a.add(node, m, c)

def S_composite_foundry():
    a = Asset('composite_foundry', 'structures', 2); pad(a, 2)
    # Steel and crystal in, stealth composite out, in one line from back to front. The furnace house at the back takes
    # the steel (billets waiting on the pallet) and the crystal (heaped in the open hopper on its roof) and pushes a blank
    # out of the slot in its front wall onto the bed of a two-post press. The press clamps it under the heated platen (the
    # ram node, its hot seam underneath) and fuses it into a black tile, which slides along the rails into the magazine
    # in front: a Pez stem with a team cap, its open front showing the stack of tiles, which steps down a tile as each
    # one goes in (the stack node). FoundryWorks runs a blank through for every composite the sim makes (one every 2 s,
    # 4 s on low power) and lights the furnace's peephole while it works; the grape vents on the roof puff after each
    # stroke. Markers: press_feed (inside the slot), press_bed (the work surface), press_out (where the next tile sits on
    # the stack), vent_0 / vent_1 (the vents' mouths).
    px, pz = .44, -.12                                                # the press line (feed, bed, magazine) runs along z
    # The furnace house: cream, a team wrapper band, grape louvres on its side, the feed slot facing the press and the
    # door the steel goes in by.
    a.box('cream', 1.72, .64, .7, y=.4, z=.51)
    a.box('team', 1.76, .1, .74, y=.62, z=.51)
    for k in range(3): a.box('grape', .03, .05, .26, x=.87, y=.24 + k * .1, z=.34)
    a.cyl('steel', .13, .04, x=.865, y=.36, z=.66, axis='x', sections=16)   # the furnace's peephole: lit while it fires
    a.cyl('amber', .085, .03, x=.88, y=.36, z=.66, axis='x', sections=16)
    a.box('dark', .3, .1, .03, x=px, y=.5, z=.155)                    # the feed slot
    a.box('licorice', .38, .03, .04, x=px, y=.565, z=.155)
    a.box('dark', .42, .36, .04, x=-.4, y=.26, z=.155)                # the door
    a.box('licorice', .48, .05, .05, x=-.4, y=.465, z=.155)
    for k in range(3): a.box('licorice', .4, .012, .045, x=-.4, y=.15 + k * .1, z=.155)
    # Grape vents on the roof: short stacks with team bands and steel lips round dark throats.
    for i, z in enumerate((.36, .66)):
        a.cyl('grape', .085, .3, x=-.68, y=.87, z=z, sections=10)
        a.cyl('team', .095, .06, x=-.68, y=.95, z=z, sections=10)
        a.cyl('steel', .1, .03, x=-.68, y=1.03, z=z, sections=10)
        a.cyl('dark', .065, .01, x=-.68, y=1.045, z=z, sections=10)
        a.node(f'vent_{i}', a.key, (-.68, 1.06, z))
    # The crystal hopper: an open cream funnel on the roof heaped with crystal, a team band round its rim.
    hx, hz = -.22, .52
    a.rev('cream', [(0, .72), (.12, .72), (.27, 1.06), (.25, 1.08), (0, 1.0)], x=hx, z=hz, sections=16)
    a.rev('team', [(0, .95), (.235, .95), (.265, 1.03), (0, 1.03)], x=hx, z=hz, sections=16)
    a.cyl('dark', .23, .02, x=hx, y=1.05, z=hz, sections=16)
    for (dx, dz, r, h, l, t) in ((0, 0, .055, .24, 6, 0), (.1, .07, .045, .17, 22, 30), (-.1, .06, .045, .16, 24, 150),
                                 (.05, -.11, .04, .15, 20, 280), (-.09, -.08, .04, .13, 26, 220)):
        crystal(a, 'crystal', hx + dx, 1.0, hz + dz, r, h, l, t)
    # The press: a cream plinth with the steel bed, two steel posts with the team crown across them, the hydraulic
    # cylinder standing on it.
    a.box('cream', .5, .32, .52, x=px, y=.24, z=pz)
    for sx in (-1, 1): a.box('cream', .16, .26, .26, x=px + sx * .31, y=.21, z=pz)  # the posts' feet
    a.box('steel', .4, .05, .34, x=px, y=.425, z=pz)                  # the bed: its top is the work surface (y .45)
    a.node('press_bed', a.key, (px, .45, pz))
    a.node('press_feed', a.key, (px, .45, .4))
    for sx in (-1, 1): a.cyl('steel', .045, .92, x=px + sx * .31, y=.8, z=pz, sections=12)
    a.node('stage_3', a.key)
    a.box('team', .84, .24, .22, x=px, y=1.27, z=pz)
    a.box('team', .5, .06, .3, x=px, y=1.14, z=pz)                    # the platen's guide shoe under the crown
    a.cyl('cream', .14, .3, x=px, y=1.54, z=pz, sections=16)
    a.cyl('steel', .155, .04, x=px, y=1.71, z=pz, sections=16)
    # The ram: the platen (pivot on its underside, at rest up), the hot seam under it, collars riding the posts, the rod up
    # through the crown into the cylinder (long enough to stay inside over the whole stroke).
    rm = a.node('ram', 'stage_3', (px, .79, pz))
    a.box('amber', .52, .045, .4, y=.0225, node=rm)                  # the hot seam: the platen's lower band
    a.box('steel', .52, .085, .4, y=.0875, node=rm)
    a.box('cream', .44, .03, .3, y=.145, node=rm)
    for sx in (-1, 1): a.cyl('steel', .065, .14, x=sx * .31, y=.08, node=rm, sections=10)
    a.cyl('steel', .05, .7, y=.5, node=rm, sections=8)
    # The rails from the bed to the magazine.
    for sx in (-1, 1): a.box('steel', .03, .03, .2, x=px + sx * .09, y=.435, z=-.46)
    # The magazine: a cream stem open on the two faces the camera sees (corner posts, a lip at the foot), so the stack of
    # finished tiles shows, a team cap on top. A tile comes in over the low back wall from the rails.
    mx, mz, mw = px, -.68, .3
    a.box('cream', mw, .36, .03, x=mx, y=.26, z=mz + .135)           # back (low: the tile comes in over it)
    a.box('cream', .03, .42, mw, x=mx - .135, y=.29, z=mz)            # the closed side
    for (cx, cz) in ((.13, -.13), (-.13, -.13), (.13, .13)): a.box('cream', .04, .42, .04, x=mx + cx, y=.29, z=mz + cz)
    a.box('cream', mw, .08, .03, x=mx, y=.12, z=mz - .135)            # lips
    a.box('cream', .03, .08, mw, x=mx + .135, y=.12, z=mz)
    a.box('cream', mw, .02, mw, x=mx, y=.09, z=mz)                    # floor
    a.box('team', .36, .12, .36, x=mx, y=.56, z=mz)                   # the cap
    a.box('team', .26, .04, .06, x=mx, y=.52, z=mz + .2)              # its lip over the entry
    a.node('stage_1', a.key)
    st = a.node('stack', 'stage_1', (mx, 0, mz))
    for k in range(4): a.box('licorice', .22, .04, .22, y=.23 + k * .06, node=st)
    a.node('press_out', a.key, (mx, .45, mz))
    # The blank FoundryWorks runs through the press: a steel tile and the same tile in composite (shown one at a time), its
    # pivot on its underside, at rest inside the house behind the feed slot.
    bl = a.node('blank', 'stage_1', (px, .45, .4))
    for m in ('steel', 'licorice'): a.box(m, .22, .04, .22, y=.02, node=bl)
    # Steel billets waiting on a kraft pallet in the front corner.
    a.box('kraft', .5, .06, .42, x=-.42, y=.11, z=-.5)
    for k in range(3):
        for j in range(3):
            if k % 2 == 0: a.box('steel', .44, .07, .1, x=-.42, y=.18 + k * .075, z=-.62 + j * .12)
            else: a.box('steel', .1, .07, .36, x=-.54 + j * .12, y=.18 + k * .075, z=-.5)
    return a

def S_fusion_reactor():
    a = Asset('fusion_reactor', 'structures', 3); pad(a, 3)
    # A tokamak built as a toy: the power plant's big brother. A cream vacuum vessel (a doughnut) open along its crown,
    # with the magenta plasma ring (the plasma node, turned by FusionWorks) running round inside it; eight team
    # toroidal-field coils clasp it like wrapper bands, and the central solenoid stands in the hole, a smoke-plastic stem
    # wound with foil modules under a team cap. All of it on an octagonal cream deck.
    R, yc = .78, .74                                   # the vessel's major radius and the height of its midplane
    ro, ri, slot = .30, .19, math.radians(36)          # vessel wall outer/inner radius; half-width of the crown slot
    a.cyl('cream', 1.3, .14, y=.15, sections=8, rot=ry(22.5))
    # Vessel: a C section revolved, open along the crown, its lips rounded.
    rm, rc = (ro + ri) / 2, (ro - ri) / 2
    def arc(r, t0, t1, n): return [(R + r * math.cos(t), yc + r * math.sin(t)) for t in np.linspace(t0, t1, n)]
    def lip(t, sgn):                                   # a half round from the outer wall to the inner one
        c = np.array([R + rm * math.cos(t), yc + rm * math.sin(t)])
        er, et = np.array([math.cos(t), math.sin(t)]), np.array([-math.sin(t), math.cos(t)]) * sgn
        return [tuple(c + rc * (math.cos(s) * er + math.sin(s) * et)) for s in np.linspace(0, math.pi, 6)[1:-1]]
    t0, t1 = math.pi / 2 - slot, math.pi / 2 + slot - 2 * math.pi
    prof = arc(ro, t0, t1, 18) + lip(t1, 1) + arc(ri, t1, t0, 14) + lip(t0, -1)
    if sum(p[0] * q[1] - q[0] * p[1] for p, q in zip(prof, prof[1:] + prof[:1])) < 0: prof = prof[::-1]
    a.rev('cream', prof, sections=32)
    a.rev('steel', [(.48, .22), (.68, .22), (.7, .24), (.7, .29), (.68, .31), (.48, .31), (.46, .29), (.46, .24)], sections=24)  # the coils' seat
    for k in range(8):                                 # gravity supports between the coils
        f = math.radians(45 * k)
        a.box('steel', .09, yc - ro - .2, .09, x=R * math.cos(f), y=.22 + (yc - ro - .2) / 2, z=R * math.sin(f), rot=ry(-45 * k))
    # Toroidal-field coils: D-shaped team bands hugging the vessel, straight leg on the inside, each on a steel foot.
    si, sa, sb, w = .43, .73, .42, .06
    def dee(t):                                        # outer half an ellipse; inner half a flat leg with round corners
        c, s_ = math.cos(t), math.sin(t)
        if c >= 0: return si + sa * c, yc + sb * s_
        return si - w * abs(c) ** (1 / 3), yc + sb * math.copysign(abs(s_) ** (1 / 3), s_)
    for k in range(8):
        f = math.radians(22.5 + 45 * k)
        er, et = np.array([math.cos(f), 0, math.sin(f)]), np.array([-math.sin(f), 0, math.cos(f)])
        pts = [dee(t) for t in np.linspace(0, 2 * math.pi, 26, endpoint=False)]
        a.hoop('team', [s * er + np.array([0, y, 0]) for s, y in pts], .085, side=et, r2=.032, sides=8)

    # Central solenoid: a smoke stem wound with foil modules, under a stepped team cap.
    a.cyl('smoke', .24, 1.38, y=.22 + .69, sections=12)
    for k in range(4): a.cyl('foil', .275, .19, y=.4 + k * .26, sections=12)
    a.cyl('team', .31, .12, y=1.58, sections=20)
    a.cyl('team', .24, .08, y=1.66, sections=20)
    a.node('solenoid', a.key, (0, 1.6, 0))
    # Pellet injector (left corner): the fuel is a magazine of magenta pellets, Pez bricks, in an open smoke cage, its
    # open side to the camera. A spring in the steel base pushes the stack up into the team head, which fires the top
    # pellet down its barrel into the vessel's crown slot (port marker, at the muzzle). FusionWorks steps the stack (the
    # pellets node) up one pellet per shot: the bottom pellet hides in the base and the top one in the head, so the step
    # loops seamlessly. Out of plasma the stack sinks into the base and the cage stands empty.
    d = np.array([1, 0, 1]) / math.sqrt(2)             # from the centre out to the corner
    cam = np.array([1, 0, -1]) / math.sqrt(2)          # towards the default camera
    m = 1.5 * d
    h, p, n = .065, .085, 9
    yb = .36                                           # the spring base's top
    y0 = yb - h / 2 - .006                             # the bottom pellet's centre at rest (inside the base)
    yh = y0 + (n - 1) * p + h / 2 + .01                # the head's underside (the top pellet goes into it on a shot)
    hc = yh - yb                                       # the cage's height
    a.box('steel', .3, yb - .08, .3, x=m[0], y=.08 + (yb - .08) / 2, z=m[2], rot=ry(45))
    q = m - cam * .12
    a.box('smoke', .04, hc, .28, x=q[0], y=yb + hc / 2, z=q[2], rot=ry(45))
    for sgn in (-1, 1):
        q = m + sgn * .13 * d - cam * .04
        a.box('smoke', .2, hc, .035, x=q[0], y=yb + hc / 2, z=q[2], rot=ry(45))
    a.box('team', .34, .2, .34, x=m[0], y=yh + .1, z=m[2], rot=ry(45))
    pe = a.node('pellets', 'stage_2', (m[0], y0, m[2]), rot=ry(45))
    for i in range(n): a.box('magenta', .13, h, .2, y=i * p, node=pe)
    a.strut('steel', tuple(1.36 * d + [0, yh + .1, 0]), tuple(.9 * d + [0, 1.05, 0]), .045, sections=10)
    a.cyl('steel', .06, .05, x=.93 * d[0], y=1.06, z=.93 * d[2], sections=10)
    a.node('port', a.key, tuple(.88 * d + [0, 1.04, 0]))
    # Steam generator (right corner): the heat leaves the blanket by a pipe into a cream drum and steams off its vent (vent_0).
    g = -1.55 * d
    a.cyl('cream', .24, .66, x=g[0], y=.08 + .33, z=g[2], sections=10)
    a.cyl('team', .25, .09, x=g[0], y=.62, z=g[2], sections=10)
    a.cyl('cream', .18, .08, x=g[0], y=.78, z=g[2], sections=10)
    a.cyl('steel', .06, .24, x=g[0], y=.92, z=g[2], sections=10)
    a.node('vent_0', a.key, (g[0], 1.05, g[2]))
    a.strut('steel', tuple(-1.33 * d + [0, .5, 0]), tuple(-1.0 * d + [0, .6, 0]), .05, sections=10)
    # Switchyard (back corner): the output bus up to three tall cream insulators, the line running off the pad.
    b = np.array([-1.1, 0, 1.1])
    a.box('steel', .3, .32, .5, x=b[0], y=.24, z=b[2], rot=ry(45))
    for k in (-1, 0, 1):
        x, z = b[0] + k * .15 * d[0], b[2] + k * .15 * d[2]
        a.rev('cream', [(0, .4), (.03, .4)] + [pt for j in range(3) for pt in ((.055 - j * .007, .44 + j * .07), (.055 - j * .007, .46 + j * .07), (.03, .5 + j * .07))] + [(0, .6)], x=x, z=z, sections=10)
        a.cyl('steel', .013, .12, x=x, y=.64, z=z, sections=6)
    a.strut('steel', (b[0] - .2, .14, b[2] + .2), (-1.42, .14, 1.42), .04)
    # The plasma ring in the vessel: a core with two helical strands (the field lines), turned by FusionWorks.
    a.node('stage_2', a.key)
    pl = a.node('plasma', 'stage_2', (0, yc, 0))
    a.hoop('magenta', [(R * math.cos(t), 0, R * math.sin(t)) for t in np.linspace(0, 2 * math.pi, 48, endpoint=False)], .065, node=pl, sides=8)
    ts = np.linspace(0, 2 * math.pi, 84, endpoint=False)
    for k in range(2):
        q = ts * 7 + k * math.pi
        a.hoop('magenta', np.c_[(R + .09 * np.cos(q)) * np.cos(ts), .09 * np.sin(q), (R + .09 * np.cos(q)) * np.sin(ts)], .026, node=pl, sides=4)
    return a

def S_airfield():
    a = Asset('airfield', 'structures', 3)
    a.box('pad', 2.92, .06, 2.92, y=.03)                          # flush pad: Plinths keeps its height (the lift sits in it)
    # The runway runs along Z through the middle; aircraft leave toward -Z (south). Bone edge lines, piano keys at the
    # north end, a centre dash; the south half carries the approach lights instead.
    a.box('dark', 1.2, .02, 2.86, y=.07)
    for sx in (-1, 1): a.box('bone', .035, .012, 2.72, x=sx * .53, y=.083)
    for sx in (-1, 1):
        for k in range(4): a.box('bone', .055, .012, .26, x=sx * (.12 + k * .1), y=.083, z=1.22)
    a.box('bone', .04, .012, .18, y=.083, z=.84)
    # The aircraft lift in the middle of the runway (where the sim puts new and landing aircraft): a cream collar with the
    # team landing ring round a dark well, and the lift deck in it, flush. Before a launch the deck goes down the well
    # (PezMotion's lift, -0.6) and comes back up with the new aircraft on it.
    a.cyl('dark', .45, .01, y=.085, sections=32)
    a.rev('cream', [(.45, .06), (.6, .06), (.6, .11), (.56, .15), (.45, .15)], sections=32)
    a.torus('team', .525, .035, y=.15)
    lf = a.node('lift', a.key, (0, 0, 0))
    a.cyl('foil', .43, .05, y=.12, node=lf, sections=32)
    for sx in (-1, 1): a.box('smoke', .06, .012, .3, x=sx * .1, y=.148, node=lf)       # the H
    a.box('smoke', .14, .012, .06, y=.148, node=lf)
    # Approach lights: four amber chevrons down the south half pointing the way out, and a threshold bar at the end. Each
    # row is its own node (approach_N) so AirfieldWorks can chase them toward -Z when an aircraft is about to launch.
    rows = (-.74, -.9, -1.06, -1.22)
    for i, z in enumerate(rows):
        n = a.node(f'approach_{i}', 'stage_0', (0, .085, z))
        for sx in (-1, 1):
            a.box('amber', .2, .016, .05, x=sx * .075, y=.008, z=.035, node=n, rot=ry(-sx * 32))
    n = a.node(f'approach_{len(rows)}', 'stage_0', (0, .085, -1.37))
    for k in range(5): a.box('amber', .14, .016, .035, x=-.4 + k * .2, y=.008, node=n)
    # The control tower in the back corner: a cream ops block with a team band, a smoke stem with team grip slats, a glass
    # cab and a team roof. On the roof a radar bar (radar) turns, with the amber beacon on its mast.
    tx, tz = -1.0, .85
    a.node('stage_3', a.key)
    a.box('cream', .7, .32, .8, x=tx, y=.22, z=tz)
    a.box('team', .74, .07, .84, x=tx, y=.33, z=tz)
    a.box('dark', .03, .18, .22, x=tx + .355, y=.15, z=tz - .15)       # door to the runway
    a.box('smoke', .32, .86, .32, x=tx, y=.8, z=tz)
    for k in range(3): a.box('team', .22, .05, .03, x=tx, y=.62 + k * .14, z=tz - .165)
    a.cyl('cream', .32, .05, x=tx, y=1.255, z=tz, sections=24)
    a.cyl('glass', .28, .2, x=tx, y=1.38, z=tz, sections=8)
    for k in range(8):
        t = (k + .5) * math.pi / 4
        a.box('smoke', .025, .2, .025, x=tx + .27 * math.cos(t), y=1.38, z=tz + .27 * math.sin(t))
    a.cyl('cream', .31, .04, x=tx, y=1.5, z=tz, sections=24)
    a.box('team', .62, .17, .66, x=tx, y=1.605, z=tz)                  # the roof
    a.box('team', .5, .05, .08, x=tx, y=1.54, z=tz - .36)              # its visor
    rd = a.node('radar', 'stage_3', (tx, 1.69, tz))
    a.cyl('steel', .02, .14, y=.07, node=rd, sections=10)
    a.box('smoke', .36, .035, .05, y=.13, node=rd)
    a.box('team', .05, .06, .06, x=.18, y=.13, node=rd)
    a.sphere('amber', .035, y=.18, node=rd, sub=1)
    # A fuel tank in the front corner, styled as a Pez candy roll (cream, team wrapper, foil twists at both ends), on steel
    # saddles.
    fx, fz = -1.02, -.62
    for z in (-.25, .25): a.box('steel', .3, .16, .06, x=fx, y=.14, z=fz + z)
    a.cyl('cream', .19, .62, x=fx, y=.32, z=fz, axis='z', sections=24)
    a.cyl('team', .195, .36, x=fx, y=.32, z=fz, axis='z', sections=24)
    for s in (-1, 1):
        c = trimesh.creation.revolve(np.array([(0, 0), (.185, 0), (.06, .09), (.1, .14), (0, .14)], float), sections=12)
        c.apply_transform(axis_rot('y')); c.apply_transform(rx(90 * s)); c.apply_translation((fx, .32, fz + s * .31)); a.add(None, 'foil', c)
    # A Quonset hangar down the +x side, its open end to the south: a foil arch with team ribs, a team frame round the dark
    # doorway, and a cream back wall.
    def half_cyl(m, r, L, x, y, z, n=16):
        th = np.linspace(0, math.pi, n + 1)
        ring = np.c_[r * np.cos(th), r * np.sin(th)]
        v = np.vstack([np.c_[ring[:, 0], ring[:, 1], np.full(n + 1, -L / 2)], np.c_[ring[:, 0], ring[:, 1], np.full(n + 1, L / 2)]])
        f = []
        for i in range(n):
            f += [[i, i + 1, n + 2 + i], [i, n + 2 + i, n + 1 + i]]
        for i in range(1, n):
            f += [[0, i + 1, i], [n + 1, n + 1 + i, n + 2 + i]]
        f += [[0, n + 1, n], [n, n + 1, 2 * n + 1]]                    # the floor
        h = trimesh.Trimesh(v, np.array(f)); h.fix_normals(); h.apply_translation((x, y, z)); a.add(None, m, h)
    hx, hz0, hz1 = 1.02, -.3, 1.15
    L = hz1 - hz0; hzc = (hz0 + hz1) / 2
    half_cyl('foil', .33, L - .04, hx, .06, hzc)
    for k in range(3): half_cyl('team', .345, .05, hx, .06, hz0 + .4 + k * (L - .55) / 2)
    half_cyl('team', .35, .06, hx, .06, hz0 + .01)
    half_cyl('dark', .26, .02, hx, .06, hz0 - .015)
    half_cyl('cream', .32, .02, hx, .06, hz1 - .01)
    # The windsock on the front corner in team and cream wrapper stripes (windsock, on its pole top): AirfieldWorks points
    # it downwind and lets it droop when the wind drops.
    wx, wz = 1.02, -1.02
    a.cyl('cream', .09, .06, x=wx, y=.09, z=wz, sections=16)
    a.cyl('steel', .018, .78, x=wx, y=.5, z=wz, sections=8)
    ws = a.node('windsock', 'stage_1', (wx, .86, wz))
    for k in range(4):
        r0, r1 = .06 - k * .008, .06 - (k + 1) * .008
        c = trimesh.creation.revolve(np.array([(r0 - .012, k * .09), (r0, k * .09), (r1, (k + 1) * .09), (r1 - .012, (k + 1) * .09)], float), sections=16)
        c.apply_transform(axis_rot('y')); c.apply_transform(rz(-90)); c.apply_translation((.03, 0, 0)); a.add(ws, 'team' if k % 2 == 0 else 'bone', c)
    t = trimesh.creation.torus(major_radius=.06, minor_radius=.01, major_sections=16, minor_sections=6)
    t.apply_transform(ry(90)); t.apply_translation((.03, 0, 0)); a.add(ws, 'steel', t)
    a.strut('steel', (0, 0, 0), (.03, 0, 0), .012, node=ws)
    return a

def S_radar_dome():
    a = Asset('radar_dome', 'structures', 2); pad(a, 2)
    # A cream radome (the short-range set that sees stealth) on an equipment drum with a team wrapper band, a team cap on
    # its crown, and on a smoke-plastic stem out of the cap the long-range search antenna (the spinner): a curved foil
    # reflector with a team top rail, its feed horn on a boom, and the amber beacon on top. RadarWorks sweeps it while
    # the base has power, swings it onto each new contact, and lets it stall and lurch on low power (the sim switches
    # the air search off then; the dome still sees stealth).
    cx, cz = -.1, .12
    a.cyl('steel', .66, .06, x=cx, y=.11, z=cz, sections=24)                 # footing ring
    a.cyl('cream', .62, .34, x=cx, y=.25, z=cz, sections=28)                 # equipment drum
    a.cyl('team', .635, .1, x=cx, y=.36, z=cz, sections=28)                  # wrapper band
    for k in range(5):                                                       # vent slots round the back of the drum
        ang = math.radians(100 + k * 28)
        a.box('dark', .03, .1, .1, x=cx + .615 * math.cos(ang), y=.22, z=cz + .615 * math.sin(ang), rot=ry(-math.degrees(ang)))
    yc, R = .76, .56                                                         # the dome: a sphere sunk in the drum
    a.sphere('cream', R, x=cx, y=yc, z=cz, sub=3)
    a.torus('steel', .46, .02, x=cx, y=.43, z=cz)                            # its base ring on the drum deck
    ca, Rc = math.radians(28), R * 1.03                                      # the team cap on its crown, with a lip
    yb = yc + Rc * math.cos(ca) - .02
    cap = [(0, yb), (Rc * math.sin(ca), yb)] + [(Rc * math.sin(t), yc + Rc * math.cos(t)) for t in np.linspace(ca, 0, 7)[1:]]
    a.rev('team', cap, x=cx, z=cz, sections=28)
    top = yc + Rc
    a.cyl('smoke', .085, .2, x=cx, y=top + .06, z=cz, sections=14)          # the stem
    a.cyl('steel', .13, .04, x=cx, y=top + .02, z=cz, sections=16)          # bearing ring
    # The search antenna, pivot on the stem's axis. Its beam looks along +z (the feed marker sits on the horn).
    sp = a.node('spinner', a.key, (cx, top + .16, cz))
    a.cyl('steel', .12, .04, y=.02, node=sp, sections=16)                    # turntable
    a.box('team', .28, .13, .24, y=.105, node=sp)                            # rotator housing
    W, H, sag, tilt = 1.16, .34, .12, math.radians(14)
    us, vs = np.linspace(-W / 2, W / 2, 25), np.linspace(0, H, 7)
    U, V = np.meshgrid(us, vs, indexing='ij')
    zc = -.1 + sag * (U / (W / 2)) ** 2                                      # parabolic across, concave toward +z
    P = np.stack([U, .2 + V * math.cos(tilt), zc - V * math.sin(tilt) + .05 * (V / H - .5) ** 2], axis=2)
    a.sheet('foil', P, .025, node=sp)
    for u in (-.42, -.14, .14, .42):                                         # ribs on its back
        k = np.argmin(abs(us - u))
        a.strut('steel', P[k, 0] + (0, 0, -.02), P[k, -1] + (0, 0, -.02), .012, node=sp, sections=6)
    a.strut('steel', (-.08, .14, -.06), P[8, 2] + (0, 0, -.02), .016, node=sp)   # struts from the housing
    a.strut('steel', (.08, .14, -.06), P[16, 2] + (0, 0, -.02), .016, node=sp)
    a.strut('steel', (0, .16, -.08), P[12, 1] + (0, 0, -.02), .02, node=sp)
    tl, tr = P[0, -1], P[-1, -1]
    a.strut('team', tl + (0, .03, 0), tr + (0, .03, 0), .03, node=sp, sections=10)   # the team top rail
    bl, br = P[0, 0], P[-1, 0]
    a.strut('steel', bl + (0, -.01, 0), br + (0, -.01, 0), .014, node=sp, sections=8)
    mid = P[12, -1]
    a.cyl('steel', .012, .07, x=0, y=mid[1] + .08, z=mid[2], node=sp, sections=8)
    a.sphere('amber', .045, y=mid[1] + .13, z=mid[2], node=sp, sub=1)               # beacon
    fz, fy = .26, .3                                                         # feed horn on its boom
    a.strut('steel', (0, .15, .1), (0, fy - .02, fz - .02), .016, node=sp)
    a.box('dark', .14, .1, .09, y=fy, z=fz, node=sp)
    a.box('steel', .17, .13, .025, y=fy, z=fz - .055, node=sp)
    a.node('feed', sp, (0, fy, fz))
    # The equipment shelter in the front corner, a waveguide up the drum, and a ladder.
    sx, sz = .55, -.52
    a.box('cream', .5, .34, .4, x=sx, y=.25, z=sz)
    a.box('team', .56, .07, .46, x=sx, y=.45, z=sz)
    a.box('dark', .16, .24, .02, x=sx + .08, y=.2, z=sz - .205)            # door
    a.box('licorice', .2, .03, .03, x=sx + .08, y=.335, z=sz - .21)
    for k in range(3): a.box('dark', .02, .03, .22, x=sx + .255, y=.16 + k * .06, z=sz)   # louvres
    a.box('steel', .18, .06, .14, x=sx - .06, y=.51, z=sz + .04)             # roof AC unit
    a.box('steel', .06, .06, .4, x=.3, y=.38, z=-.26, rot=ry(45))            # waveguide into the drum
    ang = math.radians(225)
    lx, lz = cx + .64 * math.cos(ang), cz + .64 * math.sin(ang)
    for s in (-1, 1):
        a.strut('steel', (lx + s * .06 * math.sin(ang), .08, lz - s * .06 * math.cos(ang)),
                (lx + s * .06 * math.sin(ang), .44, lz - s * .06 * math.cos(ang)), .01, sections=6)
    for k in range(5):
        y = .12 + k * .07
        a.strut('steel', (lx + .06 * math.sin(ang), y, lz - .06 * math.cos(ang)), (lx - .06 * math.sin(ang), y, lz + .06 * math.cos(ang)), .008, sections=6)
    # The standby generator's fuel tank on saddles in the side corner, piped to the shelter.
    tx, tz = .55, .6
    for s in (-1, 1): a.box('steel', .06, .1, .26, x=tx + s * .15 * .707, y=.13, z=tz + s * .15 * .707, rot=ry(-45))
    a.cyl('cream', .11, .46, x=tx, y=.25, z=tz, axis='x', rot=ry(-45), sections=18)
    a.cyl('team', .115, .05, x=tx, y=.25, z=tz, axis='x', rot=ry(-45), sections=18)
    a.box('steel', .04, .04, .62, x=.68, y=.1, z=.0)
    return a

def S_gun_turret():
    a = Asset('gun_turret', 'structures', 1); pad(a, 1)
    # The stem: a cream octagonal bunker tapering up to the smoke-plastic collar the head turns on. A
    # magazine window in its front face shows the candy-brick rounds stacked inside, waiting to be fed up into the gun.
    bunker = trimesh.creation.revolve(np.array([(0, .08), (.43, .08), (.37, .27), (0, .27)], float), sections=8)
    bunker.apply_transform(axis_rot('y')); bunker.apply_transform(ry(22.5)); a.add(None, 'cream', bunker)
    a.cyl('smoke', .29, .05, y=.275, sections=24)                      # the collar the head turns on
    a.box('dark', .17, .14, .02, y=.17, z=-.372, rot=rx(17))           # magazine window (the face leans in 17 deg)
    for k in range(3): a.box('bone', .13, .03, .025, y=.125 + k * .043, z=-.388 + k * .013, rot=rx(17))
    # A kraft ammo crate on the plinth's back corner, a few rounds on its lid.
    a.box('kraft', .16, .12, .13, x=.33, y=.14, z=.33, rot=ry(45))
    for k in range(2): a.box('bone', .05, .035, .1, x=.33 + (k - .5) * .045, y=.22, z=.33 + (.5 - k) * .045, rot=ry(45))
    # The head (turret): the team flip-top, hinged at its back edge, on a steel turntable. In front of it the gun is a
    # revolver: a steel drum (the drum node, its axis along +Z) holds six candy-brick rounds and turns one chamber per
    # shot, lining the next one up with the barrel above it. The barrel slides back in its sleeve on firing (PezMotion's
    # recoil) and compresses the spring steel recoil spring (the spring node, along +Z) against a collar; the brick
    # muzzle is its far end. Everything on the turret clears the stem (nothing on it reaches below the turntable).
    tu = a.node('turret', a.key, (0, .3, 0))
    a.cyl('steel', .24, .04, y=.02, node=tu, sections=24)             # turntable
    # The head: a team wedge, tall at the back and sloping down to its face (so it shows which way it looks even from
    # straight above), under a lid that overhangs it a little.
    hw, zb, zf, yb, yf = .21, -.27, .10, .26, .19
    head = trimesh.convex.convex_hull(np.array([(sx * hw, y, z) for sx in (-1, 1) for (y, z) in ((.03, zb), (yb, zb), (yf, zf), (.03, zf))], float))
    a.add(tu, 'team', head)
    tilt = math.degrees(math.atan2(yb - yf, zf - zb))
    n = np.array([0, math.cos(math.radians(tilt)), math.sin(math.radians(tilt))])
    c = np.array([0, (yb + yf) / 2, (zb + zf) / 2]) + n * .03
    a.box('team', .46, .06, .39, y=c[1], z=c[2], node=tu, rot=rx(tilt))  # the lid
    a.cyl('steel', .025, .38, y=yb + .02, z=zb - .02, axis='x', node=tu)  # its hinge pin
    a.box('dark', .22, .035, .02, y=.15, z=zf, node=tu)                 # the gunner's sight slit
    dy, dz, dr, dl = .165, .19, .115, .13                               # drum axis height, centre, radius, length
    ry_ = .095                                                          # the rounds' radius in the drum
    by = dy + ry_                                                       # barrel axis: level with the drum's top chamber
    a.cyl('steel', .025, .2, y=dy, z=dz, axis='z', node=tu)            # drum axle, from the head to the yoke
    a.box('steel', .08, by + .03 - dy, .03, y=(dy + by + .03) / 2, z=.29, node=tu)  # the yoke: axle up to the sleeve
    a.cyl('steel', .065, .12, y=by, z=.34, axis='z', node=tu)          # barrel sleeve
    dr_ = a.node('drum', tu, (0, dy, dz))
    a.cyl('smoke', dr, dl, axis='z', node=dr_, sections=6)
    for sz in (-1, 1): a.cyl('foil', .055, .02, z=sz * (dl / 2 + .005), axis='z', node=dr_, sections=12)  # hub caps
    for k in range(6):                                                  # the rounds, seated in its six flats
        t = math.radians(90 + 60 * k)
        a.box('bone', .062, .032, dl + .01, x=ry_ * math.cos(t), y=ry_ * math.sin(t), node=dr_, rot=rz(60 * k))
    a.node('eject', tu, (-.17, dy + .03, dz))                           # where spent cases leave (the drum's side)
    sp = a.node('spring', tu, (0, by, .40), rot=rx(90))                # recoil spring: local +Y runs along the barrel
    a.coil('steel', .058, .011, .0, .15, 3, node=sp, per_turn=16, sides=6)
    br = a.node('barrel', tu, (0, by, .28))
    a.cyl('steel', .045, .27, z=.235, axis='z', node=br, sections=16)  # tube (its breech end stays in the sleeve)
    a.cyl('foil', .072, .03, z=.285, axis='z', node=br, sections=16)    # the collar the spring presses on
    a.box('team', .15, .12, .12, z=.43, node=br)                        # brick muzzle
    a.cyl('dark', .036, .01, z=.49, axis='z', node=br, sections=12)     # the bore
    for sx in (-1, 1): a.box('dark', .01, .055, .028, x=sx * .076, z=.43, node=br)  # side vents
    return a

def S_sam_site():
    a = Asset('sam_site', 'structures', 1)
    # A twin-pod missile launcher on a jacked turntable. The base stays under 0.27 so nothing on it is in the pods' way:
    # four outrigger jacks splayed to the corners, a cream drum with a team band and a steel slewing ring.
    for sx in (-1, 1):
        for sz in (-1, 1):
            fx, fz = sx * .37, sz * .37
            a.strut('steel', (sx * .22, .1, sz * .22), (fx, .05, fz), .024, sections=6)
            a.cyl('cream', .07, .05, x=fx, y=.025, z=fz, sections=12)             # the jack's foot
    a.rev('cream', [(0, 0), (.37, 0), (.37, .05), (.33, .07), (.31, .22), (.27, .245), (0, .245)], sections=24)
    a.cyl('team', .332, .05, y=.135, sections=24)                               # wrapper band
    a.cyl('steel', .22, .03, y=.255, sections=24)                               # slewing ring
    # The turret (yawed by PezMotion from the sim's aim): a turntable deck, the team head between the pods with the
    # tracker's dark visor, and the search radar on a mast behind it (radar, turned by SamWorks).
    tu = a.node('turret', a.key, (0, .27, 0))
    a.cyl('cream', .15, .05, y=.025, node=tu, sections=20)
    a.box('team', .22, .27, .27, y=.185, z=.01, node=tu)                        # the head
    a.box('dark', .16, .065, .02, y=.24, z=.15, node=tu)                        # the tracker's visor
    a.box('amber', .045, .03, .02, y=.14, z=-.13, node=tu)                      # armed lamp (dims on low power / offline)
    a.cyl('steel', .02, .17, y=.39, z=-.075, node=tu, sections=8)               # radar mast
    rd = a.node('radar', tu, (0, .47, -.075))
    a.cyl('steel', .03, .03, node=rd, sections=10)
    dd = a.node('radar_dish', rd, (0, .02, 0), rot=rx(70))                     # facing forward, tipped up 20 deg
    a.rev('foil', [(0, 0), (.11, .038), (.11, .054), (0, .022)], node=dd, sections=20)
    a.cyl('steel', .013, .075, y=.055, node=dd, sections=8)                     # the feed horn
    a.sphere('steel', .022, y=.1, node=dd, sub=1)
    # The barrel node is the elevation axle (pitched by SamWorks, nose up 25 deg at rest): a steel axle through the head
    # and a pod on each end. Each pod is a cream candy-brick canister of two tubes, a foil wrapper band at its back, a
    # team cap round its mouths, and a round in each tube (missile_0..3, pivots at the mouths, team noses out). A fired
    # tube shows its dark mouth until SamWorks slides a new round up into it.
    br = a.node('barrel', tu, (0, .32, 0), rot=rx(-25))
    a.cyl('steel', .032, .54, axis='x', node=br, sections=10)
    k = 0
    for x in (-.235, .235):
        a.box('cream', .15, .28, .56, x=x, z=.04, node=br)
        a.box('foil', .16, .29, .11, x=x, z=-.185, node=br)
        a.box('team', .16, .29, .065, x=x, z=.29, node=br)                      # team cap round the mouths
        a.cyl('steel', .055, .03, x=x - math.copysign(.085, x), axis='x', node=br, sections=10)   # trunnion boss
        for y in (.07, -.07):
            a.cyl('dark', .053, .012, x=x, y=y, z=.324, axis='z', node=br, sections=12)  # the tube's mouth
            m = a.node(f'missile_{k}', br, (x, y, .32)); k += 1
            a.cyl('cream', .042, .07, z=0, axis='z', node=m, sections=10)          # the round's body (the rest is inside)
            a.cone('team', .042, .095, z=.035, axis='z', node=m, sections=10)
    return a

def S_laser_tower():
    a = Asset('laser_tower', 'structures', 1)
    # The foot: an octagonal cream plinth with a team wrapper band, and the charge bank round the stem's root: three
    # cream capacitor cans with team caps.
    a.cyl('cream', .45, .14, y=.07, sections=8)
    a.cyl('team', .455, .045, y=.115, sections=8)
    for k in range(3):
        ang = math.radians(30 + 120 * k)
        cx, cz = .3 * math.cos(ang), .3 * math.sin(ang)
        a.cyl('cream', .075, .22, x=cx, y=.25, z=cz, sections=8)
        a.cyl('team', .082, .05, x=cx, y=.38, z=cz, sections=8)           # its cap
    # The pylon: a smoke-plastic stem wound with four cyan coils (coil_0 at the bottom .. coil_3 at the top, each its
    # own node under stage_2 so it rises with the stem) under a cream clamp ring. LaserWorks lights them bottom to
    # top as the shot charges.
    a.node('stage_2', a.key)
    a.cyl('smoke', .13, 1.18, y=.79, sections=12)
    for k in range(4):
        c = a.node(f'coil_{k}', 'stage_2', (0, .52 + k * .22, 0))
        a.coil('cyan', .152, .024, -.045, .045, 2.0, node=c, per_turn=14, sides=5)
    a.cyl('cream', .19, .07, y=.175, sections=12)                          # the stem's foot flange
    a.cyl('cream', .175, .04, y=1.26, sections=12)                         # a clamp ring over the coils
    # The top: a team band and a cream collar the head's bearing sits in.
    a.cyl('team', .19, .08, y=1.36, sections=14)
    a.cyl('cream', .22, .06, y=1.43, sections=14)
    # The head (turret): a steel turntable with a cream yoke, and between its arms the team emitter head (barrel),
    # pitched on trunnions like a searchlight. The lens at its snout is where the beam leaves (Models.BarrelTip: the far
    # +Z end of the barrel's meshes).
    tu = a.node('turret', a.key, (0, 1.46, 0))
    a.cyl('steel', .2, .05, y=.025, node=tu, sections=12)
    P = .36                                                                # trunnion height above the turntable
    for sx in (-1, 1):
        a.box('cream', .05, P + .0, .14, x=sx * .19, y=.05 + P / 2, node=tu)
    br = a.node('barrel', tu, (0, P, 0))
    a.box('team', .32, .26, .42, z=.02, node=br)                           # the head
    a.box('team', .26, .05, .34, y=.14, z=.0, node=br)                     # its crown
    for x in (-.075, 0, .075): a.box('foil', .024, .05, .24, x=x, y=.185, z=-.03, node=br)   # heat-sink fins
    a.box('dark', .2, .09, .02, y=-.01, z=-.19, node=br)                   # the rear vent, where it breathes out heat
    a.node('vent', br, (0, 0, -.22))
    a.cyl('steel', .045, .5, axis='x', node=br, sections=10)              # the trunnion pin through the yoke's arms
    # The lens barrel sits a little high, on the middle of the barrel node's meshes, so the muzzle Models.BarrelTip
    # finds is the lens's centre.
    a.cyl('steel', .1, .15, y=.04, z=.295, axis='z', node=br, sections=14)
    a.cyl('cyan', .075, .03, y=.04, z=.365, axis='z', node=br, sections=14)   # the lens
    return a

def S_deep_mine():
    # A headframe over a shaft, built as a toy. The cream headframe stands over the shaft's mouth on the yard side,
    # braced by its backstays onto the long hoist house beside it, with the team sheave wheel (dm_sheave) on its crown.
    # The rope runs from the winding drum (dm_drum) on the hoist house's roof up over the sheave and down the shaft to
    # the skip (dm_skip: a steel bail riding two guide rails, with a team bucket, dm_bucket, pivoted at its back bottom
    # edge and a load of ore candy in it, dm_load). DeepMineWorks hauls the skip up out of the shaft, tips it at the top
    # so the ore pours down the chute into the bin behind (its heap: dm_heap; dm_bin marks its mouth), and lowers it
    # again, the drum and the sheave turning with the rope (the rope below the sheave, dm_rope, stretched to the skip's
    # eye, dm_eye). In the front corner the ventilation fan (dm_fan) turns while the mine works. On the hoist house's
    # front end the dispenser stem shows what's left in the deposit: a stack of ore bricks (dm_res_0..5, top down) on a
    # spring (dm_spring, under its follower plate dm_follower); as the deposit runs down the team flip-top head (dm_head,
    # hinged at its back edge) pops, a brick (dm_candy) flips out and the spring pushes the rest up a slot. Run dry, the
    # stem is empty, the head hangs open and the amber lamp (dm_lamp) blinks. The ore parts are M_OreTint: the view
    # tints them to the deposit's ore. The rope's plane is z = ZS, so the sheave and the drum turn about z; the bucket
    # tips about x, toward +z.
    # Build stages: the deck, the hoist house, the fan house and the skip stage_1; the headframe, drum, bin and stem
    # stage_2; the crown, sheave, rope and the stem's head stage_3.
    a = Asset('deep_mine', 'structures', 2); pad(a, 2)
    S1, S2, S3 = a.node('stage_1', a.key), a.node('stage_2', a.key), a.node('stage_3', a.key)
    XS, ZS, DK = .38, .06, .22                        # the shaft's centre, the deck's top
    HX0, HX1, HZ0, HZ1, HH = -.84, -.16, -.84, .84, .72   # the hoist house
    TOP = 1.5                                         # the headframe's posts
    # The deck and the hoist house: cream, a team wrapper band, a door and windows on its yard side.
    a.box('cream', 1.72, DK - .08, 1.72, y=.08 + (DK - .08) / 2, node=S1)
    a.box('cream', HX1 - HX0, HH - DK + .02, HZ1 - HZ0, x=(HX0 + HX1) / 2, y=(DK - .02 + HH) / 2, z=(HZ0 + HZ1) / 2, node=S1)
    a.box('team', HX1 - HX0 + .04, .08, HZ1 - HZ0 + .04, x=(HX0 + HX1) / 2, y=HH - .1, z=(HZ0 + HZ1) / 2, node=S1)
    a.box('dark', .03, .24, .22, x=HX1 + .005, y=DK + .12, z=-.56, node=S1)
    a.box('licorice', .04, .04, .28, x=HX1 + .005, y=DK + .26, z=-.56, node=S1)
    for z in (-.2, .12, .44): a.box('dark', .03, .1, .14, x=HX1 + .005, y=.47, z=z, node=S1)
    # The shaft's mouth: dark, in a team collar ring on a steel plate.
    a.cyl('steel', .27, .03, x=XS, y=DK + .005, z=ZS, node=S1, sections=14)
    a.cyl('team', .24, .05, x=XS, y=DK + .025, z=ZS, node=S1, sections=14)
    a.cyl('dark', .18, .02, x=XS, y=DK + .045, z=ZS, node=S1, sections=12)
    # The headframe: four cream posts round the shaft, girts (the top ones team; a middle one only on the hoist side), two
    # guide rails for the skip. The faces the camera sees stay open between the low and high girts, so the skip's run
    # shows, and so does the back, for the bucket to tip through.
    for sx in (-1, 1):
        for sz in (-1, 1): a.box('cream', .08, TOP - DK, .08, x=XS + sx * .2, y=(DK + TOP) / 2, z=ZS + sz * .2, node=S2)
    for y, m in ((.6, 'cream'), (.98, 'cream'), (1.38, 'team')):
        for sx in (-1, 1):
            if y != .98 or sx == -1: a.box(m, .06, .06, .34, x=XS + sx * .2, y=y, z=ZS, node=S2)
        for sz in (-1, 1):
            if y != .98: a.box(m, .34, .06, .06, x=XS, y=y, z=ZS + sz * .2, node=S2)
    for sx in (-1, 1): a.box('steel', .03, TOP - DK - .06, .03, x=XS + sx * .135, y=(DK + TOP - .06) / 2, z=ZS, node=S2)
    # Backstays from the crown down onto the hoist house's roof (they take the rope's pull).
    for sz in (-1, 1): a.strut('cream', (XS - .2, TOP - .02, ZS + sz * .2), (HX1 - .07, HH, ZS + sz * .2), .04, node=S2, sections=8)
    for sz in (-1, 1): a.box('steel', .1, .03, .1, x=HX1 - .07, y=HH + .015, z=ZS + sz * .2, node=S2)
    # The crown: a team collar round the rope's way through, the sheave on two steel pedestals over its back bar.
    for sx in (-1, 1): a.box('team', .12, .12, .56, x=XS + sx * .22, y=TOP + .06, z=ZS, node=S3)
    for sz in (-1, 1): a.box('team', .32, .12, .12, x=XS, y=TOP + .06, z=ZS + sz * .22, node=S3)
    RS, YSH = .25, 1.94
    XSH = XS - RS                                     # the rope drops off the sheave's front edge straight down the shaft
    for sz in (-1, 1): a.box('steel', .1, YSH - TOP - .12 + .02, .04, x=XSH, y=(TOP + .12 + YSH) / 2, z=ZS + sz * .075, node=S3)
    a.cyl('steel', .022, .2, x=XSH, y=YSH, z=ZS, axis='z', node=S3, sections=10)
    sh = a.node('dm_sheave', S3, (XSH, YSH, ZS))
    ts = np.linspace(0, 2 * math.pi, 28, endpoint=False)
    a.hoop('team', np.c_[RS * np.cos(ts), RS * np.sin(ts), np.zeros(28)], .035, node=sh, side=(0, 0, 1), r2=.022, sides=6)
    a.cyl('steel', .05, .08, axis='z', node=sh, sections=10)
    for k in range(3): a.box('steel', 2 * RS - .02, .024, .03, node=sh, rot=rz(60 * k))
    # The winding drum on the hoist house's roof, in a steel cradle: the rope wound on it between team flanges, each with
    # a licorice bar across it so its turning shows.
    DX, DY, RD = -.5, HH + .17, .12
    for sz in (-1, 1): a.box('steel', .18, .2, .04, x=DX, y=HH + .1, z=ZS + sz * .22, node=S2)
    a.cyl('steel', .02, .5, x=DX, y=DY, z=ZS, axis='z', node=S2, sections=8)
    dr = a.node('dm_drum', S2, (DX, DY, ZS))
    a.cyl('licorice', RD, .32, axis='z', node=dr, sections=14)
    for sz in (-1, 1):
        a.cyl('team', .17, .03, z=sz * .175, axis='z', node=dr, sections=12)
        a.box('licorice', .28, .012, .045, z=sz * .195, node=dr)
    # The rope: from the drum's top up to the sheave, over it and (dm_rope) down the shaft.
    D, C, rd, rs = np.array([DX, DY]), np.array([XSH, YSH]), RD + .008, RS + .012
    d = C - D; L = np.linalg.norm(d); al = math.atan2(d[1], d[0])
    be = al + math.acos((rd - rs) / L)                # the outer tangent on the upper side
    n = np.array([math.cos(be), math.sin(be)])
    pd, pc = D + rd * n, C + rs * n
    a.strut('licorice', (pd[0], pd[1], ZS), (pc[0], pc[1], ZS), .011, node=S3, sections=6)
    a.tube('licorice', [(XSH + rs * math.cos(p), YSH + rs * math.sin(p), ZS) for p in np.linspace(be, 0, 12)], .011, node=S3, sides=6)
    YB = -.1                                          # the skip at rest, at the bottom of the shaft (inside the deck)
    rp = a.node('dm_rope', S3, (XSH + rs, YSH, ZS), rot=np.diag([1, YSH - (YB + .38), 1, 1]))
    a.cyl('licorice', .011, 1.0, y=-.5, node=rp, sections=6)   # 1 long: DeepMineWorks stretches it to the skip's eye
    # The skip: a steel bail riding the rails, its crossbar where the rope's eye takes it, and the team
    # bucket pivoted at its back (+z, the bin's side) bottom edge.
    sk = a.node('dm_skip', S1, (XS, YB, ZS))
    for sx in (-1, 1): a.box('steel', .025, .32, .05, x=sx * .11, y=.16, node=sk)
    a.box('steel', .23, .04, .06, y=.32, node=sk)
    a.cyl('steel', .02, .05, y=.36, node=sk, sections=8)
    a.node('dm_eye', sk, (0, .38, 0))
    bk = a.node('dm_bucket', sk, (0, 0, .1))
    a.box('team', .18, .2, .2, y=.1, z=-.1, node=bk)
    a.box('dark', .14, .012, .16, y=.2, z=-.1, node=bk)
    ld = a.node('dm_load', bk, (0, .2, -.1))
    for (dx, dz, r) in ((-.02, -.03, .058), (.035, .04, .05), (-.04, .035, .045)):
        a.sphere('oretint', r, x=dx, y=r * .45, z=dz, node=ld, sub=1)
    # The chute from under the tipped bucket's lip down into the bin behind, and the bin: a cream funnel with a team rim
    # on a pedestal, heaped with ore (dm_heap).
    BZ = ZS + .6
    c0, c1 = np.array([ZS + .15, .92]), np.array([ZS + .48, .78])     # (z, y): from under the lip to over the bin
    cl = np.linalg.norm(c1 - c0); ca = math.degrees(math.atan2(c0[1] - c1[1], c0[0] - c1[0]))
    cm = (c0 + c1) / 2
    a.box('steel', .16, .02, cl, x=XS, y=cm[1], z=cm[0], rot=rx(-ca), node=S2)
    for sx in (-1, 1): a.box('steel', .015, .06, cl, x=XS + sx * .08, y=cm[1] + .03, z=cm[0], rot=rx(-ca), node=S2)
    a.box('cream', .3, .24, .3, x=XS, y=DK + .12, z=BZ, node=S2)
    a.box('team', .32, .06, .32, x=XS, y=DK + .21, z=BZ, node=S2)
    a.rev('cream', [(0, .48), (.08, .48), (.21, .78), (.19, .8), (0, .72)], x=XS, z=BZ, node=S2, sections=14)
    a.rev('team', [(.18, .7), (.195, .7), (.225, .8), (.205, .81)], x=XS, z=BZ, node=S2, sections=14)
    a.cyl('dark', .18, .02, x=XS, y=.76, z=BZ, node=S2, sections=14)
    a.node('dm_bin', a.key, (XS, .8, BZ))
    hp = a.node('dm_heap', S2, (XS, .77, BZ))
    for (dx, dz, r) in ((0, .01, .09), (.08, -.06, .06), (-.08, -.04, .06)):
        a.sphere('oretint', r, x=dx, y=-.03 + r * .5, z=dz, node=hp, sub=1)
    # The dispenser stem on the hoist house's front end: smoke plastic round a clear window on its front, team grip slats
    # on its side, a dark lip; inside, the stack of ore bricks on its spring and follower; the team flip-top head on top.
    SX, SZ, s0, s1, w = -.52, -.56, HH, 1.5, .3
    a.box('smoke', w, s1 - s0, .04, x=SX, y=(s0 + s1) / 2, z=SZ + w / 2 - .02, node=S2)
    for s in (-1, 1): a.box('smoke', .04, s1 - s0, w, x=SX + s * (w / 2 - .02), y=(s0 + s1) / 2, z=SZ, node=S2)
    a.box('smoke', w, .14, .04, x=SX, y=s0 + .07, z=SZ - w / 2 + .02, node=S2)
    a.box('smoke', w, .08, .04, x=SX, y=s1 - .04, z=SZ - w / 2 + .02, node=S2)
    a.box('smoke', w - .04, .03, w - .04, x=SX, y=s0 + .055, z=SZ, node=S2)   # the floor the spring stands on
    a.box('clear', w - .08, s1 - s0 - .22, .01, x=SX, y=(s0 + .14 + s1 - .08) / 2, z=SZ - w / 2 + .015, node=S2)
    for i in range(3): a.box('team', .03, .05, .2, x=SX + w / 2 + .005, y=.98 + i * .12, z=SZ, node=S2)
    a.box('dark', w - .06, .04, .05, x=SX, y=s1 + .02, z=SZ - w / 2 + .035, node=S2)
    PB, HB = .095, .075                               # the stack's pitch and a brick's height
    ytop = s1 - .08 - HB / 2 - .01                    # the top brick's centre
    for i in range(6):
        m = a.node(f'dm_res_{i}', S2, (SX, ytop - i * PB, SZ))
        a.box('oretint', .2, HB, .16, node=m)
    fl = s0 + .07                                     # the spring's foot
    yf = ytop - 5 * PB - HB / 2                       # the follower under a full stack
    sp = a.node('dm_spring', S2, (SX, fl, SZ), rot=np.diag([1, (yf - .02 - fl) / .3, 1, 1]))
    a.coil('steel', .07, .01, 0, .3, 5, node=sp, per_turn=8, sides=4)   # built .3 tall, held squashed under a full stack
    fo = a.node('dm_follower', S2, (SX, yf, SZ))
    a.box('steel', .2, .02, .16, y=-.01, node=fo)
    hd = a.node('dm_head', S3, (SX, s1 + .02, SZ + w / 2), rot=rx(6))
    a.box('team', w + .06, .2, w + .04, y=.11, z=-(w + .04) / 2, node=hd)
    a.box('team', w - .04, .07, .08, y=.03, z=-(w + .04) + .01, node=hd)
    a.cyl('steel', .028, w + .04, axis='x', node=hd, sections=8)
    cd = a.node('dm_candy', S3, (SX, s1 + .06, SZ - w / 2 + .06))
    a.box('oretint', .2, HB, .16, node=cd)
    # The ventilation fan house in the front corner: a mine breathes. Cream, a team band, the duct's mouth on its front
    # with the fan in it (dm_fan), turning while the mine works.
    FX, FZ = .44, -.58
    a.box('cream', .5, .36, .36, x=FX, y=DK + .18, z=FZ, node=S1)
    a.box('team', .54, .07, .4, x=FX, y=DK + .3, z=FZ, node=S1)
    a.cyl('steel', .15, .03, x=FX, y=DK + .16, z=FZ - .185, axis='z', node=S1, sections=12)
    a.cyl('dark', .12, .03, x=FX, y=DK + .16, z=FZ - .19, axis='z', node=S1, sections=12)
    fn = a.node('dm_fan', S1, (FX, DK + .16, FZ - .205))
    a.cyl('steel', .03, .03, axis='z', node=fn, sections=8)
    for k in range(3): a.box('foil', .2, .045, .012, x=.1 * math.cos(math.radians(120 * k)), y=.1 * math.sin(math.radians(120 * k)), node=fn, rot=rz(120 * k) @ rx(25))
    # The lamp on the hoist house's roof: dark while the deposit holds up; it pulses as it runs low, and blinks when dry.
    a.cyl('steel', .018, .12, x=-.26, y=HH + .06, z=-.72, node=S2, sections=6)
    ln = a.node('dm_lamp', S2, (-.26, HH + .165, -.72))
    a.cyl('amber', .06, .09, node=ln, sections=8)
    a.cyl('team', .065, .03, y=.058, node=ln, sections=8)
    return a

# ---------------------------------------------------------------- vehicles
def tracks(a, L, W, h=.18):
    for sx in (-1, 1): a.box('licorice', W * .22, h, L, x=sx * W * .39, y=h / 2)

def wheels(a, L, W, n=2, r=.11):
    for sx in (-1, 1):
        for k in range(n):
            z = -L / 2 + r + .03 + k * (L - 2 * r - .06) / max(n - 1, 1)
            a.cyl('licorice', r, .08, x=sx * (W / 2 - .03), y=r, z=z, axis='x')

def hull(a, L, W, y0, h=.16):
    a.box('smoke', W * .58, h, L * .94, y=y0 + h / 2)
    a.box('dark', W * .3, .03, L * .22, y=y0 + h + .01, z=-L * .3)          # magazine window
    for k in range(3): a.box('team', W * .26, .02, .03, y=y0 + h + .03, z=-L * .37 + k * .05)

def head_turret(a, top, w, d, h=.14):
    tu = a.node('turret', a.key, (0, top, 0))
    a.box('team', w, h, d, y=h / 2, node=tu)
    a.box('dark', w, .02, .02, y=h * .5, z=-d / 2, node=tu)                 # hinge line
    return tu

def V_tank(key, L, W, twin=False, laser=False, art=False):
    a = Asset(key, 'units', L); tracks(a, L, W); hull(a, L, W, .1)
    tu = head_turret(a, .26, W * .5, L * .4)
    if art:
        br = a.node('barrel', tu, (0, .1, .05), rot=rx(-60))
        a.cyl('steel', .045, L * .9, z=L * .45, axis='z', node=br)
        a.box('steel', .1, .1, .08, z=L * .9, node=br)
        return a
    br = a.node('barrel', tu, (0, .07, L * .18))
    if laser:
        a.cyl('steel', .022, L * .5, z=L * .25, axis='z', node=br)
        for k in range(3): a.cyl('cyan', .05, .02, z=L * (.12 + k * .1), axis='z', node=br)
        a.octa('cyan', .05, .07, z=L * .52, node=br)
    else:
        for x in ((-.045, .045) if twin else (0,)):
            a.cyl('steel', .03 if twin else .035, L * .55, x=x, z=L * .27, axis='z', node=br)
            a.box('steel', .07, .07, .06, x=x, z=L * .55, node=br)
    return a

def V_mining_truck():
    L, W = 1.0, .62; a = Asset('mining_truck', 'units', L); tracks(a, L, W)
    a.box('smoke', W * .6, .16, L * .9, y=.18)
    a.box('team', W * .55, .18, .28, y=.35, z=.25)
    sp = a.node('spinner', a.key, (0, .17, L / 2 - .16))
    a.cyl('steel', .15, W * .7, axis='x', node=sp)
    for k in range(6):
        ang = k * math.pi / 3
        a.box('steel', W * .7, .05, .06, y=math.sin(ang) * .16, z=math.cos(ang) * .16, node=sp, rot=rx(math.degrees(ang)))
    bn = a.node('bin', a.key, (0, .26, -.405))      # pivot on the rear bottom edge, so it tips to unload
    a.box('dark', W * .55, .2, .45, y=.1, z=.225, node=bn)
    ore = a.node('bin_ore', bn, (0, .16, .225))      # scale Y 0..1 with load
    for (x, z, r) in ((-.08, -.08, 10), (.08, .05, -20), (0, .1, 30), (-.06, .1, 0)):
        a.box('cinnamon', .12, .06, .07, x=x, y=.06, z=z, node=ore, rot=ry(r))
    return a

def V_repair_truck():
    L, W = .95, .55; a = Asset('repair_truck', 'units', L); wheels(a, L, W)
    a.box('smoke', W * .8, .16, L * .9, y=.22)
    a.box('team', W * .78, .18, .26, y=.39, z=.3)
    tu = a.node('turret', a.key, (0, .3, -.15))
    a.cyl('steel', .09, .1, y=.05, node=tu)
    a.box('team', .16, .1, .16, y=.12, node=tu)
    br = a.node('barrel', tu, (0, .16, 0), rot=rx(-25))
    a.box('steel', .05, .05, .55, z=.27, node=br)
    a.cyl('amber', .03, .06, z=.57, axis='z', node=br)
    return a

def V_outpost_truck():
    L, W = 1.1, .62; a = Asset('outpost_truck', 'units', L); wheels(a, L, W, n=3)
    a.box('dark', W * .8, .08, L * .95, y=.2)
    a.box('team', W * .78, .22, .24, y=.35, z=.4)
    a.box('smoke', W * .7, .26, .6, y=.37, z=-.1)
    a.box('team', W * .6, .06, .5, y=.53, z=-.1)
    a.cyl('steel', .02, .3, x=.15, y=.66, z=-.3, axis='z', rot=rx(0))
    return a

def V_scout_buggy():
    L, W = .7, .5; a = Asset('scout_buggy', 'units', L); wheels(a, L, W, r=.1)
    a.box('smoke', W * .6, .08, L * .9, y=.15)
    a.box('team', W * .55, .1, .25, y=.24, z=.18)
    for sx in (-1, 1): a.box('steel', .02, .18, .02, x=sx * .12, y=.32, z=-.05)
    a.box('steel', .26, .02, .02, y=.41, z=-.05)
    tu = a.node('turret', a.key, (0, .28, -.12))
    a.cyl('steel', .02, .12, y=.06, node=tu)
    a.box('team', .1, .06, .08, y=.13, node=tu)
    br = a.node('barrel', tu, (0, .14, .04))
    a.cyl('dark', .015, .26, z=.13, axis='z', node=br)
    return a

def V_gunship():
    L = 1.2; a = Asset('gunship', 'units', L)
    a.box('smoke', .24, .22, .6, y=.2)
    a.box('team', .26, .24, .26, y=.22, z=.38)
    a.box('glass', .2, .1, .14, y=.3, z=.47)
    a.box('smoke', .08, .08, .55, y=.24, z=-.55)
    a.box('team', .02, .2, .14, y=.32, z=-.78)
    a.box('steel', .7, .04, .1, y=.16)
    for sx in (-1, 1): a.cyl('dark', .05, .22, x=sx * .35, y=.12, axis='z')
    for sx in (-1, 1): a.box('steel', .02, .1, .5, x=sx * .1, y=.04, z=0)
    sp = a.node('spinner', a.key, (0, .38, 0))
    a.cyl('steel', .03, .08, y=.04, node=sp)
    for r in (0, 90): a.box('licorice', 1.4, .015, .06, y=.08, node=sp, rot=ry(r))
    tu = a.node('turret', a.key, (0, .07, .42))
    a.sphere('dark', .06, node=tu, sub=1)
    br = a.node('barrel', tu, (0, -.01, .05))
    a.cyl('steel', .015, .18, z=.09, axis='z', node=br)
    return a

def V_stealth_bomber():
    a = Asset('stealth_bomber', 'units', 1.0)
    for s in (-1, 1):
        a.box('licorice', .75, .06, .32, x=s * .3, y=.1, z=-.05, rot=ry(-s * 32))
        a.box('team', .75, .02, .03, x=s * .34, y=.12, z=.08, rot=ry(-s * 32))
    a.box('licorice', .3, .1, .7, y=.12, z=.05)
    a.box('dark', .16, .04, .2, y=.18, z=.2)
    return a

def V_infantry(key, kind):
    a = Asset(key, 'units', .55)
    for sx in (-1, 1): a.box('smoke', .06, .22, .07, x=sx * .045, y=.11)
    a.box('smoke' if kind != 'laser' else 'cream', .17, .18, .1, y=.31)
    a.sphere('team', .07, y=.48, sub=1, scale=(1, .9, 1))                    # helmet (team)
    for sx in (-1, 1): a.box('team', .06, .04, .09, x=sx * .1, y=.39)       # shoulder pads
    if kind == 'medic':
        a.box('bone', .14, .14, .06, y=.32, z=-.08)
        a.box('team', .1, .03, .065, y=.33, z=-.085); a.box('team', .03, .1, .065, y=.33, z=-.085)
        return a
    tu = a.node('turret', a.key, (0, .33, 0))
    for sx in (-1, 1): a.box('smoke', .04, .04, .14, x=sx * .07, z=.06, node=tu)
    br = a.node('barrel', tu, (.05, 0, .1))
    if kind == 'rocket':
        a.cyl('kraft', .04, .32, y=.07, z=0, axis='z', node=br)
        a.cyl('amber', .03, .01, y=.07, z=.16, axis='z', node=br)
    elif kind == 'laser':
        a.box('dark', .03, .04, .22, z=.08, node=br)
        a.box('cyan', .035, .015, .14, y=.025, z=.06, node=br)
    else:
        a.box('dark', .025, .035, .22, z=.08, node=br)
    return a

# ---------------------------------------------------------------- ores
def O_ore(key):
    a = Asset(key, 'ores', 1)
    rng = np.random.default_rng(abs(hash(key)) % 2**32)
    spots = [(-.28, -.25), (.25, -.3), (.05, .05), (-.3, .28), (.3, .3)]
    for i, (cx, cz) in enumerate(spots):
        n = a.node(f'cluster_{i}', a.key, (cx, 0, cz))
        s = .7 + .3 * rng.random()
        for j in range(3):
            ox, oz, rot = rng.uniform(-.08, .08), rng.uniform(-.08, .08), rng.uniform(0, 180)
            if key == 'iron_ore':
                a.box('cinnamon', .16 * s, .07 * s, .09 * s, x=ox, y=.05 + j * .05 * s, z=oz, node=n, rot=ry(rot) @ rz(rng.uniform(-15, 15)))
            elif key == 'copper_ore':
                a.box('copper', .15 * s, .07 * s, .09 * s, x=ox, y=.05 + j * .05 * s, z=oz, node=n, rot=ry(rot))
                a.sphere('mint', .025 * s, x=ox + .04, y=.09 + j * .05 * s, z=oz, node=n, sub=1)
            elif key == 'crystal':
                h = (.18 + .17 * rng.random()) * s
                a.cone('cyan', .04 * s, h, x=ox, z=oz, node=n, sections=5)
            else:
                h = (.14 + .16 * rng.random()) * s
                a.cyl('acid', .03 * s, h, x=ox, y=h / 2, z=oz, node=n, sections=8)
    return a

BUILDERS = [S_command_center, S_outpost, S_power_plant, S_mining_refinery, S_barracks, S_factory,
            S_electronics_plant, S_optics_lab, S_enrichment_plant, S_composite_foundry, S_fusion_reactor,
            S_airfield, S_radar_dome, S_gun_turret, S_sam_site, S_laser_tower,
            V_mining_truck, V_repair_truck, V_outpost_truck, V_scout_buggy,
            lambda: V_tank('light_tank', .9, .62), lambda: V_tank('heavy_tank', 1.2, .8, twin=True),
            lambda: V_tank('artillery', .9, .62, art=True), lambda: V_tank('laser_tank', 1.1, .7, laser=True),
            V_gunship, V_stealth_bomber,
            lambda: V_infantry('rifleman', 'rifle'), lambda: V_infantry('rocket_soldier', 'rocket'),
            lambda: V_infantry('laser_trooper', 'laser'), lambda: V_infantry('medic', 'medic'),
            lambda: O_ore('iron_ore'), lambda: O_ore('copper_ore'), lambda: O_ore('crystal'), lambda: O_ore('uranium')]

if __name__ == '__main__':
    manifest = [b().export() for b in BUILDERS]
    os.makedirs(os.path.join(os.path.dirname(__file__), 'pack'), exist_ok=True)
    json.dump(manifest, open(os.path.join(os.path.dirname(__file__), 'pack', 'models', 'manifest.json'), 'w'), indent=1)
    for m in manifest: print(f"{m['key']:20s} {m['tris']:6d} tris  nodes={[n for n in m['nodes'] if n in ('turret','barrel','spinner','bin')]}  bounds={m['bounds']}")
