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
 'cyan':     mat('M_E_Cyan', '#3FE6FF', emissive=True),
 'magenta':  mat('M_E_Magenta', '#FF3EC8', emissive=True),
 'acid':     mat('M_E_Acid', '#B6FF3B', emissive=True),
 'amber':    mat('M_E_Amber', '#FFA22E', emissive=True),
 'cinnamon': mat('M_Ore_Cinnamon', '#8A3A24', 0, .7),
 'copper':   mat('M_Ore_Copper', '#C8742F', .3, .5),
 'mint':     mat('M_Ore_Mint', '#6FC2A6', 0, .6),
 'crystal':  mat('M_Ore_Crystal', '#8FE4FF', 0, .25),     # crystal stock (not lit: glow means state)
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
    a = Asset('outpost', 'structures', 2); pad(a, 2)
    a.box('smoke', 1.4, .5, 1.3, y=.33, z=.2)
    a.box('team', 1.2, .14, 1.1, y=.65, z=.2)
    a.box('dark', 1.0, .08, .05, y=.45, z=-.46)              # firing slit
    a.cyl('steel', .03, 1.1, x=.5, y=1.2, z=.6)
    a.sphere('amber', .05, x=.5, y=1.77, z=.6)
    a.box('kraft', .9, .06, .5, y=.11, z=-.68)                 # ore drop pad
    for sx in (-1, 1): a.box('amber', .05, .05, .05, x=sx * .5, y=.16, z=-.9)
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
    a.box('cream', 1.7, .6, 1.4, y=.38, z=.15)
    for x in (-.55, 0, .55): a.box('team', .38, .22, .38, x=x, y=.79, z=.2)
    door(a, .4, .42, -.57, anim=True)
    a.cyl('steel', .025, 1.3, x=.78, y=.73, z=-.75)
    a.box('team', .34, .2, .02, x=.62, y=1.25, z=-.75)
    return a

def S_factory():
    a = Asset('factory', 'structures', 3); pad(a, 3)
    a.box('foil', 2.75, 1.0, 2.55, y=.58, z=.1)
    for z in (-1.17, 1.37): a.box('steel', 2.8, .12, .12, y=1.1, z=z)  # crimps
    a.box('team', 2.78, 1.02, .55, y=.585, z=.25)                        # wrapper band
    door(a, 1.5, .8, -1.18, anim=True)
    a.cyl('steel', .12, .5, x=1.0, y=1.3, z=.9)
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
    a.cyl('cream', .85, .3, y=.23)
    a.cyl('team', .87, .08, y=.4)
    a.sphere('glass', .78, y=.3, sub=3, scale=(1, .9, 1), clip=True)
    sp = a.node('spinner', a.key, (0, .85, 0))
    a.octa('cyan', .16, .32, node=sp)
    door(a, .3, .3, -.86, lights=False)
    return a

def S_enrichment_plant():
    a = Asset('enrichment_plant', 'structures', 2); pad(a, 2)
    a.box('smoke', 1.7, .15, 1.7, y=.15)
    for x in (-.42, .42):
        for z in (-.42, .42):
            a.cyl('cream', .26, 1.0, x=x, y=.72, z=z)
            for k in (.45, .85): a.cyl('acid', .27, .05, x=x, y=k, z=z)
            a.cyl('team', .27, .12, x=x, y=1.25, z=z)
    a.box('steel', 1.0, .08, .08, y=1.0, z=-.42); a.box('steel', 1.0, .08, .08, y=1.0, z=.42)
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
    a.cyl('cream', 1.3, .35, y=.26)
    a.torus('steel', .95, .18, y=1.0)
    for k in range(4):
        ang = k * math.pi / 2 + math.pi / 4
        a.box('team', .16, .7, .16, x=math.cos(ang) * .95, y=.75, z=math.sin(ang) * .95)
    a.torus('magenta', .95, .05, y=1.0)
    sp = a.node('spinner', a.key, (0, 1.0, 0))
    a.sphere('magenta', .35, node=sp)
    a.torus('steel', .5, .03, node=sp)
    door(a, .7, .3, -1.31, y0=.08)
    return a

def S_airfield():
    a = Asset('airfield', 'structures', 3)
    a.box('pad', 2.92, .06, 2.92, y=.03)
    a.box('dark', .9, .02, 2.8, x=-.6, y=.07)
    for z in (-1.1, -.5, .1, .7, 1.2): a.box('bone', .1, .02, .3, x=-.6, y=.08, z=z)
    lf = a.node('lift', a.key, (.75, 0, -.6))    # aircraft lift: rises from y=-0.6 when an aircraft is built
    a.cyl('kraft', .42, .06, y=.04, node=lf)
    a.torus('team', .45, .04, y=.08, node=lf)
    a.cyl('cream', .25, 1.2, x=.9, y=.66, z=.85)
    a.cyl('glass', .32, .25, x=.9, y=1.38, z=.85)
    a.cyl('team', .34, .06, x=.9, y=1.53, z=.85)
    for sx in (-1, 1):
        for z in (-1.38, 1.38): a.box('amber', .06, .06, .06, x=-.6 + sx * .48, y=.1, z=z)
    return a

def S_radar_dome():
    a = Asset('radar_dome', 'structures', 2); pad(a, 2)
    a.box('cream', 1.2, .4, 1.2, x=-.2, y=.28, z=.2)
    a.sphere('cream', .5, x=-.2, y=.8, z=.2, sub=3)
    a.cyl('team', .52, .08, x=-.2, y=.62, z=.2)
    a.cyl('steel', .06, .7, x=.55, y=.43, z=-.5)
    sp = a.node('spinner', a.key, (.55, .8, -.5))
    a.cone('steel', .32, .15, z=.05, axis='z', node=sp)
    a.cyl('dark', .02, .3, z=.15, axis='z', node=sp)
    a.sphere('amber', .06, x=-.2, y=1.33, z=.2)
    return a

def S_gun_turret():
    a = Asset('gun_turret', 'structures', 1)
    a.box('cream', .9, .28, .9, y=.14)
    tu = a.node('turret', a.key, (0, .28, 0))
    a.cyl('steel', .28, .06, y=.03, node=tu)
    a.box('team', .5, .3, .52, y=.21, node=tu)
    a.box('team', .5, .08, .1, y=.4, z=-.2, node=tu, rot=rx(-20))
    br = a.node('barrel', tu, (0, .2, .26))
    a.cyl('steel', .06, .36, z=.18, axis='z', node=br)
    a.box('steel', .16, .12, .12, z=.38, node=br)
    return a

def S_sam_site():
    a = Asset('sam_site', 'structures', 1)
    a.box('cream', .9, .22, .9, y=.11)
    tu = a.node('turret', a.key, (0, .22, 0))
    a.cyl('steel', .1, .25, y=.12, node=tu)
    a.box('team', .5, .14, .3, y=.3, node=tu)
    br = a.node('barrel', tu, (0, .42, 0), rot=rx(-25))
    for x in (-.11, .11):
        for y in (-.06, .06):
            a.cyl('dark', .05, .55, x=x, y=y, z=.05, axis='z', node=br)
            a.cyl('amber', .03, .01, x=x, y=y, z=.33, axis='z', node=br)
    a.box('team', .4, .04, .5, y=.12, z=.05, node=br)
    return a

def S_laser_tower():
    a = Asset('laser_tower', 'structures', 1)
    a.box('cream', .9, .22, .9, y=.11)
    a.cyl('smoke', .17, 1.4, y=.92, sections=6)
    a.cyl('team', .19, .14, y=.4, sections=6)
    for k in (.8, 1.05, 1.3): a.cyl('cyan', .185, .04, y=k, sections=6)
    tu = a.node('turret', a.key, (0, 1.62, 0))
    a.cyl('team', .2, .12, y=.06, node=tu)
    br = a.node('barrel', tu, (0, .2, 0))
    a.cyl('steel', .04, .35, z=.12, axis='z', node=br)
    a.octa('cyan', .09, .14, z=.33, node=br)
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
