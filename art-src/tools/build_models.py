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
    a.box('cream', 2.6, .9, 2.5, y=.53)
    door(a, .9, .6, -1.27, anim=True)
    a.box('smoke', 1.2, 1.0, 1.0, y=1.48, z=.2)
    a.box('dark', .45, .7, .05, y=1.45, z=-.32)
    for i in range(4): a.box('team', .38, .1, .03, y=1.2 + i * .17, z=-.35)
    a.box('team', 1.4, .32, 1.2, y=2.14, z=.2, rot=rx(-8))   # flip-top head
    a.box('steel', 1.2, .08, .08, y=2.0, z=.78)               # hinge
    sp = a.node('spinner', a.key, (.75, .98, -.6))             # crane
    a.cyl('steel', .09, 1.3, y=.65, node=sp)
    a.box('steel', .9, .1, .1, x=.2, y=1.3, node=sp)
    a.box('dark', .22, .22, .22, x=-.2, y=1.3, node=sp)
    a.cyl('steel', .015, .5, x=.58, y=1.05, node=sp)
    a.box('dark', .1, .08, .1, x=.58, y=.78, node=sp)
    a.cyl('steel', .04, .5, x=-1.0, y=1.23, z=-.9)
    a.cyl('foil', .22, .05, x=-1.0, y=1.5, z=-.9)
    a.box('amber', .06, .06, .06, x=-1.0, y=1.53, z=-.9)
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
    for (x, z, h) in ((-.42, -.3, 1.3), (.42, .32, 1.5)):
        a.cyl('steel', .36, h, x=x, y=.08 + h / 2, z=z)
        for k in range(5): a.cyl('cream', .385, .05, x=x, y=.25 + k * h / 5.5, z=z)
        a.cyl('team', .39, .16, x=x, y=.08 + h * .55, z=z)
        a.cyl('dark', .33, .04, x=x, y=.08 + h + .01, z=z)
        a.cyl('cyan', .24, .05, x=x, y=.08 + h + .02, z=z)
    a.box('dark', .5, .3, .05, x=.4, y=.23, z=-.92)
    return a

def S_mining_refinery():
    a = Asset('mining_refinery', 'structures', 3); pad(a, 3)
    a.box('cream', 2.7, .75, 1.6, y=.455, z=.3)
    a.box('cream', .9, .75, .9, x=-.9, y=.455, z=-.95)
    a.box('cream', .9, .75, .9, x=.9, y=.455, z=-.95)
    door(a, .9, .6, -.48, lights=False, anim=True)              # dock (south)
    a.box('kraft', .9, .04, .9, y=.1, z=-.95)                    # dock floor
    for sx in (-1, 1): a.box('amber', .07, .07, .07, x=sx * .55, y=.55, z=-1.42)
    for x in (-.75, .1):
        a.cyl('cream', .38, 1.5, x=x, y=.83 + .75, z=.85)
        a.cyl('team', .41, .14, x=x, y=1.6 + .75 - .05, z=.85)
    a.cyl('foil', .26, 1.0, x=.95, y=1.1, z=.2, axis='x')
    a.box('steel', .2, .08, 1.2, x=.95, y=.9, z=-.3)
    a.box('team', 2.7, .06, .3, y=.86, z=-.3)
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
    a = Asset('electronics_plant', 'structures', 2); pad(a, 2)
    a.box('cream', 1.6, .7, 1.6, y=.43)
    a.box('team', 1.4, .08, 1.4, y=.82)
    for x in (-.4, 0, .4): a.box('acid', .25, .3, .03, x=x, y=.45, z=-.81)
    for z in (-.4, .4): a.box('acid', .03, .3, .25, x=.81, y=.45, z=z)
    for x in (-.4, .4): a.cyl('steel', .1, .3, x=x, y=1.0, z=.4)
    door(a, .35, .4, -.82, lights=False)
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

def S_composite_foundry():
    a = Asset('composite_foundry', 'structures', 2); pad(a, 2)
    a.box('smoke', 1.6, .7, 1.4, y=.43, z=.1, rot=ry(8))
    a.box('licorice', 1.0, .5, .9, x=-.2, y=.9, z=.2, rot=ry(-14) @ rz(6))
    a.box('team', 1.62, .07, .3, y=.8, z=-.3, rot=ry(8))
    for x in (.3, .6): a.cyl('grape', .1, .5, x=x, y=1.0, z=.5)
    door(a, .5, .4, -.66, lights=False)
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
