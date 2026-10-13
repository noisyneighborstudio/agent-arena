"""The deep mines, one per ore, and the neutral derrick.

    python art-src/tools/build_mines.py [key ...]      (needs trimesh, numpy and scipy, like build_models.py)

Builds deep_mine_iron, deep_mine_copper, deep_mine_crystal, deep_mine_uranium and derrick, fillets every part with
round_models.py and writes them straight into unity/Assets/Pez/Resources/PezModels. deep_mine.glb (build_models.py's
S_deep_mine) stays as the fallback for a mine whose ore isn't known.

The four mines are one family: the same sugar pad and cream deck, the same team shaft collar, and the same dispenser stem in
the back corner (its window shows what's left in the deposit, one ore piece per sixth, and its team flip-top head pops one
out as each sixth is used: DeepMineWorks), with the amber lamp beside it. What changes is the machine that brings the ore up,
and each one comes from how that ore comes out of the ground:
  iron     the pithead: a stout enclosed winding tower with the team sheave on top; a skip runs up the slot in its face and
           tips cinnamon bricks down a chute into the hopper. The tallest.
  copper   the incline: a long belt climbs from the shaft collar to a crusher at the head; copper bricks ride it and drop
           into the crusher. Mint patina on the housings and the pipe. The longest and lowest.
  crystal  the core drill: a low, wide ring tank round a frosted-glass collar; the drill string turns, and a core barrel
           lifts a glowing core out of the shaft and tips its shards into a tray on the rim. The widest and flattest.
  uranium  the containment: an armoured dome sealed over the shaft; a jib swings acid rods out of its port into the casks
           on a carousel. The roundest.
The derrick is a different object: a pre-war landmark, a giant riveted steel dispenser on a stepped plinth that dispenses
steel bricks on its own (DerrickWorks).

Conventions as build_models.py: 1 unit = 1 tile, +Y up, +Z forward, pivot at the ground centre, doors and chutes face -Z.
In these coordinates the game camera looks from +x, -z (x is mirrored on import), so the faces it sees are -z and +x: the
back corner is (-x, +z), the front corner (+x, -z).
"""
import math, os, sys, tempfile
import numpy as np
import trimesh

sys.path.insert(0, os.path.dirname(__file__))
import build_models as bm
from build_models import Asset, MATS, mat, pad, rx, ry, rz
import round_models

SHIPPED = round_models.SHIPPED
# Mint patina on copper's housings and pipe: not an M_Ore_ material, so Models.TintOre leaves it alone.
MATS['patina'] = mat('M_Patina', '#6FC2A6', .1, .5)
# The derrick's age: dull, dusty steel plate.
MATS['oldsteel'] = mat('M_OldSteel', '#7A7F84', .5, .7)

DK = .2                    # the deck's top
STEM = (-.56, .56)         # the dispenser stem's centre: the back corner
STEM_TOP = .98             # its top (the head sits on it)


def frustum(a, m, b0, b1, y0, y1, x=0, z=0, node=None):
    """A box tapering from (b0x, b0z) at y0 to (b1x, b1z) at y1."""
    pts = [(sx * b[0] / 2, y, sz * b[1] / 2) for b, y in ((b0, y0), (b1, y1)) for sx in (-1, 1) for sz in (-1, 1)]
    h = trimesh.convex.convex_hull(np.array(pts))
    h.apply_translation((x, 0, z)); a.add(node, m, h)


def hull(a, m, pts, node=None):
    h = trimesh.convex.convex_hull(np.array(pts, float)); a.add(node, m, h)


def ring(a, m, r0, r1, y0, y1, x=0, z=0, node=None, sections=32):
    """A flat annulus (a wall, a band) from radius r0 to r1."""
    a.rev(m, [(r0, y0), (r1, y0), (r1, y1), (r0, y1)], x=x, z=z, node=node, sections=sections)


# ---------------------------------------------------------------- the family
def deck(a, S1):
    pad(a, 2)
    a.box('cream', 1.72, DK - .08, 1.72, y=.08 + (DK - .08) / 2, node=S1)


def collar(a, x, z, node, r=.27):
    """The shaft's mouth: dark, in a team collar ring on a steel plate."""
    a.cyl('steel', r, .03, x=x, y=DK + .015, z=z, node=node, sections=12)
    a.cyl('team', r - .03, .05, x=x, y=DK + .025, z=z, node=node, sections=12)
    a.cyl('dark', r - .09, .02, x=x, y=DK + .045, z=z, node=node, sections=10)


def piece(a, kind, node):
    """One piece of ore as the stem stacks it (M_OreTint: the view tints it to the deposit's ore)."""
    if kind == 'brick': a.box('oretint', .19, .062, .15, node=node)
    elif kind == 'nugget':
        a.box('oretint', .18, .062, .14, node=node, rot=ry(8))
    elif kind == 'shard':
        a.cyl('oretint', .045, .19, axis='x', node=node, sections=6)                      # a hexagonal crystal, lying
    elif kind == 'rod':
        a.cyl('oretint', .035, .2, axis='x', node=node, sections=6)                       # a fuel rod, lying


def stem(a, S2, S3, kind):
    """The dispenser stem in the back corner: the deposit's reserve. Smoke plastic round a clear window on its front (-z),
    six pieces of ore (dm_res_0..5, top down) on a spring (dm_spring) under its follower (dm_follower), the team flip-top
    head (dm_head, hinged at its back edge) on top, and the piece it pops (dm_candy)."""
    SX, SZ = STEM
    s0, s1, w = DK, STEM_TOP, .28
    a.box('smoke', w, s1 - s0, .035, x=SX, y=(s0 + s1) / 2, z=SZ + w / 2 - .018, node=S2)
    for s in (-1, 1): a.box('smoke', .035, s1 - s0, w, x=SX + s * (w / 2 - .018), y=(s0 + s1) / 2, z=SZ, node=S2)
    a.box('smoke', w, .1, .035, x=SX, y=s0 + .05, z=SZ - w / 2 + .018, node=S2)
    a.box('smoke', w, .06, .035, x=SX, y=s1 - .03, z=SZ - w / 2 + .018, node=S2)
    a.box('smoke', w - .04, .03, w - .04, x=SX, y=s0 + .045, z=SZ, node=S2)
    a.box('clear', w - .07, s1 - s0 - .16, .01, x=SX, y=(s0 + .1 + s1 - .06) / 2, z=SZ - w / 2 + .012, node=S2)
    for i in range(3): a.box('team', .03, .045, .18, x=SX + w / 2 + .004, y=.6 + i * .1, z=SZ, node=S2)
    a.box('dark', w - .05, .035, .045, x=SX, y=s1 + .017, z=SZ - w / 2 + .03, node=S2)
    PB = .086
    ytop = s1 - .06 - .031 - .008
    for i in range(6):
        m = a.node(f'dm_res_{i}', S2, (SX, ytop - i * PB, SZ))
        piece(a, kind, m)
    fl = s0 + .06
    yf = ytop - 5 * PB - .031
    sp = a.node('dm_spring', S2, (SX, fl, SZ), rot=np.diag([1, max(.02, yf - .02 - fl) / .3, 1, 1]))
    a.coil('steel', .065, .009, 0, .3, 4, node=sp, per_turn=7, sides=4)
    fo = a.node('dm_follower', S2, (SX, yf, SZ))
    a.box('steel', .19, .018, .15, y=-.009, node=fo)
    hd = a.node('dm_head', S3, (SX, s1 + .02, SZ + w / 2), rot=rx(6))
    a.box('team', w + .06, .19, w + .04, y=.1, z=-(w + .04) / 2, node=hd)
    a.box('team', w - .04, .065, .075, y=.03, z=-(w + .04) + .01, node=hd)
    a.cyl('steel', .026, w + .04, axis='x', node=hd, sections=8)
    cd = a.node('dm_candy', S3, (SX, s1 + .05, SZ - w / 2 + .06))
    piece(a, kind, cd)


def lamp(a, S2, x, z, y0=DK):
    """The amber lamp: dark while the deposit holds up; it pulses as it runs low, and goes out when it's dry."""
    a.cyl('steel', .02, .3, x=x, y=y0 + .15, z=z, node=S2, sections=6)       # a beacon post, so it shows over the deck
    ln = a.node('dm_lamp', S2, (x, y0 + .36, z))
    a.cyl('amber', .08, .11, node=ln, sections=8)
    a.cyl('team', .085, .03, y=.07, node=ln, sections=8)


def family(key, kind, lamp_at=(-.2, .8)):
    a = Asset(key, 'structures', 2)
    S1, S2, S3 = a.node('stage_1', a.key), a.node('stage_2', a.key), a.node('stage_3', a.key)
    deck(a, S1)
    stem(a, S2, S3, kind)
    lamp(a, S2, *lamp_at)
    return a, S1, S2, S3


def rope_to(a, rp_node_name, parent, x, ytop, z, ybottom):
    """The rope below a sheave: a 1-long licorice line hanging from (x, ytop, z), held stretched to `ybottom`
    (DeepMineWorks stretches it to the skip's eye)."""
    rp = a.node(rp_node_name, parent, (x, ytop, z), rot=np.diag([1, max(.01, ytop - ybottom), 1, 1]))
    a.cyl('licorice', .011, 1.0, y=-.5, node=rp, sections=6)


def sheave(a, name, parent, c, r, axis, team=True):
    """A spoked sheave wheel turning about `axis` ('x' or 'z')."""
    sh = a.node(name, parent, c)
    ts = np.linspace(0, 2 * math.pi, 30, endpoint=False)
    if axis == 'x':
        loop, side, spoke = np.c_[np.zeros(30), r * np.sin(ts), r * np.cos(ts)], (1, 0, 0), lambda k: rx(60 * k)
        hub = dict(axis='x')
        bar = lambda k: a.box('steel', .03, .024, 2 * r - .02, node=sh, rot=rx(60 * k))
    else:
        loop, side = np.c_[r * np.cos(ts), r * np.sin(ts), np.zeros(30)], (0, 0, 1)
        hub = dict(axis='z')
        bar = lambda k: a.box('steel', 2 * r - .02, .024, .03, node=sh, rot=rz(60 * k))
    a.hoop('team' if team else 'steel', loop, .04, node=sh, side=side, r2=.024, sides=6)
    a.cyl('steel', .055, .09, node=sh, sections=10, **hub)
    for k in range(3): bar(k)
    return sh


# ---------------------------------------------------------------- iron: the pithead
def S_deep_mine_iron():
    # A stout enclosed winding tower over the shaft, a team cornice and roof, the team sheave on top. The skip (dm_skip,
    # its smoke bucket dm_bucket pivoted at its +x bottom edge, its cinnamon load dm_load) runs up the tall slot in the
    # tower's front face on two guide rails; at the top it tips through the dump door in the tower's +x wall, and the
    # bricks tumble down the chute into the hopper on legs beside it (dm_bin, its heap dm_heap). The rope (dm_rope) drops
    # from the sheave's front edge straight down the slot to the skip's eye (dm_eye). The fan house (dm_fan) breathes in
    # the front corner.
    a, S1, S2, S3 = family('deep_mine_iron', 'brick', lamp_at=(-.16, .82))
    XS, ZS = -.4, -.4                 # the shaft and the tower's centre: the right corner, clear of the stem
    TW, TT = .6, 1.5                 # the tower's width and top
    # (the shaft's collar is under the tower's plinth: the slot's step shows where it is)
    # The plinth the tower stands on, and the tower: a back wall, two side walls and two front pilasters round the slot.
    SL = .28                          # the slot's width
    a.box('cream', TW + .14, .26, TW + .14, x=XS, y=DK + .13 - .02, z=ZS + .02, node=S1)
    a.box('cream', .1, .26, .06, x=XS, y=DK + .11, z=ZS - (TW + .14) / 2 - .02, node=S1)   # a step under the slot
    a.box('cream', TW, TT - DK - .24, .1, x=XS, y=(DK + .24 + TT) / 2, z=ZS + TW / 2 - .05, node=S2)
    for s in (-1, 1):
        a.box('cream', .1, TT - DK - .24, TW, x=XS + s * (TW / 2 - .05), y=(DK + .24 + TT) / 2, z=ZS, node=S2)
        a.box('cream', (TW - SL) / 2, TT - DK - .24, .1, x=XS + s * (SL / 2 + (TW - SL) / 4), y=(DK + .24 + TT) / 2, z=ZS - TW / 2 + .05, node=S2)
    a.box('dark', SL + .02, TT - DK - .3, .02, x=XS, y=(DK + .24 + TT) / 2, z=ZS + TW / 2 - .11, node=S2)   # the slot's back, in shadow
    # Pilaster ribs and windows on the +x side, so the tower reads as a building, not a block.
    for y in (.72, 1.02): a.box('dark', .02, .16, .12, x=XS + TW / 2 + .002, y=y, z=ZS + .12, node=S2)
    for s in (-1, 1): a.box('cream', .05, TT - DK - .3, .05, x=XS + s * (SL / 2 + .025), y=(DK + .24 + TT) / 2, z=ZS - TW / 2 - .02, node=S2)
    # The cornice and the roof: team, the brightest thing on top.
    # The tower's crown is its head: a team cap over the winder, wider than the shaft below it, with a team roof.
    a.box('team', TW + .12, .34, TW + .12, x=XS, y=TT - .1, z=ZS, node=S3)
    a.box('team', TW - .04, .07, TW - .04, x=XS, y=TT + .1, z=ZS, node=S3)
    a.box('dark', .3, .08, .02, x=XS, y=TT - .08, z=ZS - (TW + .12) / 2 - .005, node=S3)   # the winder's window
    # The guide rails up the slot, and the dump door in the +x wall at the top (dark: open).
    for s in (-1, 1): a.box('steel', .025, TT - DK - .2, .025, x=XS + s * .1, y=(DK + TT - .06) / 2, z=ZS - .2, node=S2)
    a.box('dark', .03, .2, .2, x=XS + TW / 2 + .002, y=1.18, z=ZS - .12, node=S2)
    # The sheave on two steel pedestals on the roof, turning about x; the rope drops off its front edge down the slot.
    RS = .3
    ZR = ZS - .2                      # the rope's line, down the middle of the slot
    YSH = TT + .14 + RS + .02
    for s in (-1, 1): a.box('steel', .05, YSH - TT - .12, .14, x=XS + s * .08, y=(TT + .12 + YSH) / 2, z=ZR + RS, node=S3)
    a.cyl('steel', .02, .22, x=XS, y=YSH, z=ZR + RS, axis='x', node=S3, sections=10)
    sheave(a, 'dm_sheave', S3, (XS, YSH, ZR + RS), RS, 'x')
    # The rope's other fall, from the sheave's back into the winder under the roof.
    a.strut('licorice', (XS, YSH - .03, ZR + 2 * RS + .01), (XS, TT + .13, ZR + 2 * RS - .06), .011, node=S3, sections=6)
    YB = DK + .02                     # the skip at rest, at the bottom of the slot
    rope_to(a, 'dm_rope', S3, XS, YSH, ZR, YB + .36)
    # The skip: a steel bail on the rails, its eye on top; the bucket pivoted on its +x bottom edge, so it tips to the
    # hopper's side.
    a.node('dm_top', S2, (XS, 1.0, ZR))                 # the top of its run: the bucket at the dump door
    sk = a.node('dm_skip', S2, (XS, YB, ZR))
    for s in (-1, 1): a.box('steel', .022, .3, .04, x=s * .09, y=.15, node=sk)
    a.box('steel', .2, .035, .05, y=.3, node=sk)
    a.cyl('steel', .018, .05, y=.34, node=sk, sections=8)
    a.node('dm_eye', sk, (0, .36, 0))
    bk = a.node('dm_bucket', sk, (.08, .02, 0))
    a.box('team', .16, .18, .16, x=-.08, y=.09, node=bk)
    a.box('steel', .17, .025, .17, x=-.08, y=.185, node=bk)
    ld = a.node('dm_load', bk, (-.08, .19, 0))
    for (dx, dz, ry_) in ((-.03, -.03, 20), (.03, .03, -30), (-.01, .035, 70)):
        a.box('cinnamon', .09, .045, .06, x=dx, y=.022, z=dz, node=ld, rot=ry(ry_))
    # The chute from the dump door down to the hopper beside the tower, and the hopper on its legs: a cream funnel with a
    # team rim, heaped with cinnamon bricks.
    HX, HZ, HY = .3, ZS - .12, .84     # the hopper's mouth, in front of the tower
    c0, c1 = np.array([XS + TW / 2 + .02, 1.12]), np.array([HX - .12, HY + .04])
    cl = np.linalg.norm(c1 - c0); ca = math.degrees(math.atan2(c0[1] - c1[1], c1[0] - c0[0]))
    cm = (c0 + c1) / 2
    a.box('steel', cl, .02, .17, x=cm[0], y=cm[1], z=HZ, rot=rz(-ca), node=S2)
    for s in (-1, 1): a.box('steel', cl, .06, .015, x=cm[0], y=cm[1] + .03, z=HZ + s * .085, rot=rz(-ca), node=S2)
    a.node('dm_chute', S2, (c0[0] + .04, c0[1] + .04, HZ))
    for i in range(3):                                # the bricks of a dump, tumbling down the chute (DeepMineWorks)
        tb = a.node(f'dm_tumble_{i}', S2, (c0[0] + .04, c0[1] + .04, HZ))
        a.box('cinnamon', .09, .045, .06, node=tb, rot=ry(30 * i))
    for sx in (-1, 1):
        for sz in (-1, 1): a.box('steel', .035, HY - .3 - DK, .035, x=HX + sx * .1, y=(DK + HY - .3) / 2, z=HZ + sz * .1, node=S2)
    a.rev('cream', [(0, HY - .34), (.07, HY - .34), (.19, HY - .02), (.17, HY), (0, HY - .08)], x=HX, z=HZ, node=S2, sections=12)
    a.rev('team', [(.165, HY - .06), (.18, HY - .06), (.205, HY + .01), (.185, HY + .02)], x=HX, z=HZ, node=S2, sections=12)
    a.cyl('dark', .16, .02, x=HX, y=HY - .03, z=HZ, node=S2, sections=14)
    a.box('steel', .1, .12, .1, x=HX, y=HY - .4, z=HZ, node=S2)             # the hopper's spout
    a.node('dm_bin', a.key, (HX, HY, HZ))
    hp = a.node('dm_heap', S2, (HX, HY - .02, HZ))
    for (dx, dz, ry_) in ((0, 0, 10), (.07, -.05, 50), (-.06, .05, -25)):
        a.box('cinnamon', .09, .045, .06, x=dx, y=.0 + (.02 if dx == 0 else 0), z=dz, node=hp, rot=ry(ry_) @ rx(8))
    # The fan house in the left corner: a mine breathes. Cream, a team cap, the duct's mouth facing -z with the fan in it.
    FX, FZ = .52, .5
    a.box('cream', .42, .3, .3, x=FX, y=DK + .15, z=FZ, node=S1)
    a.box('team', .44, .1, .32, x=FX, y=DK + .28, z=FZ, node=S1)
    a.cyl('steel', .12, .03, x=FX, y=DK + .14, z=FZ - .155, axis='z', node=S1, sections=12)
    a.cyl('dark', .095, .03, x=FX, y=DK + .14, z=FZ - .16, axis='z', node=S1, sections=12)
    fn = a.node('dm_fan', S1, (FX, DK + .14, FZ - .175))
    a.cyl('steel', .025, .03, axis='z', node=fn, sections=8)
    for k in range(3): a.box('foil', .16, .04, .012, x=.08 * math.cos(math.radians(120 * k)), y=.08 * math.sin(math.radians(120 * k)), node=fn, rot=rz(120 * k) @ rx(25))
    # The banksman's door in the plinth's +x face.
    a.box('dark', .02, .2, .16, x=XS + (TW + .14) / 2 + .002, y=DK + .1, z=ZS + .14, node=S1)
    a.box('team', TW + .16, .07, TW + .16, x=XS, y=DK + .25, z=ZS + .02, node=S1)            # the plinth's team cap
    return a


# ---------------------------------------------------------------- copper: the incline
def S_deep_mine_copper():
    # A long belt climbs from the shaft collar in the left corner to the crusher house at the head, over the hopper in the
    # right corner. Copper bricks (dm_carry_0..5) ride the belt from its tail (dm_belt_a) to its head (dm_belt_b) and drop
    # into the crusher, whose drum (dm_crusher) turns in the open mouth of its mint housing; the head and tail pulleys
    # (dm_pulley_0, dm_pulley_1) turn with the belt. Crushed ore falls into the hopper below (dm_bin, dm_heap). A mint pipe
    # runs up beside the belt, and a mint hood covers its upper run.
    a, S1, S2, S3 = family('deep_mine_copper', 'nugget', lamp_at=(-.14, .82))
    TX, TZ = .56, .5                  # the tail: over the shaft
    HX, HZ = -.46, -.46               # the head: over the hopper
    collar(a, TX, TZ, S1, r=.25)
    YT, YH = DK + .2, .98             # the belt's top at its tail and head
    d = np.array([HX - TX, YH - YT, HZ - TZ]); L = np.linalg.norm(d); u = d / L
    hz = math.hypot(d[0], d[2]); pitch = math.degrees(math.atan2(d[1], hz)); yaw = math.degrees(math.atan2(d[0], d[2]))
    Rb = ry(yaw) @ rx(-pitch)         # local +z runs up the belt, local +y is the belt's up
    def P(t, up=0., side=0.):
        """A point on the belt: t along it (0 tail .. 1 head), `up` above its top, `side` across (+ = local +x)."""
        n = Rb[:3, :3] @ np.array([0, 1., 0]); s = Rb[:3, :3] @ np.array([1., 0, 0])
        q = np.array([TX, YT, TZ]) + d * t + n * up + s * side
        return q
    def onbelt(m, sx, sy, sz, t, up=0., side=0., node=S2):
        q = P(t, up, side); a.box(m, sx, sy, sz, x=q[0], y=q[1], z=q[2], rot=Rb, node=node)
    W = .26                           # the belt's width
    # The girder: two cream stringers, the licorice belt between them, steel idlers under it.
    for s in (-1, 1):
        onbelt('cream', .045, .1, L + .1, .5, up=-.045, side=s * (W / 2 + .03))
        onbelt('team', .055, .03, L + .1, .5, up=.015, side=s * (W / 2 + .03))     # the team stripe down the incline
    onbelt('licorice', W, .02, L + .04, .5, up=-.01)
    for k in range(2):
        q = P(.12 + k * .2, up=-.05)
        a.cyl('steel', .028, W + .02, x=q[0], y=q[1], z=q[2], axis='x', rot=ry(yaw), node=S2, sections=6)
    # Trestles: two A-frames from the deck up to the girder.
    for t in (.32, .7):
        top = P(t, up=-.08)
        for s in (-1, 1):
            foot = P(t, side=s * .2); foot[1] = DK
            a.strut('steel', tuple(foot), tuple(P(t, up=-.08, side=s * .15)), .022, node=S2, sections=6)
        a.box('cream', .12, .04, .12, x=P(t, side=0)[0], y=DK + .02, z=P(t, side=0)[2], node=S1)
    # The tail pulley over the collar, and the tail's guard: a cream boot with a team top, where the bricks come up.
    tp = a.node('dm_pulley_1', S2, tuple(P(0, up=-.045)), rot=ry(yaw))
    a.cyl('steel', .045, W + .02, axis='x', node=tp, sections=8)
    a.box('dark', .02, .08, .02, x=0, node=tp)
    q = P(-.02, up=-.06)
    a.box('team', .38, .18, .2, x=q[0], y=DK + .09, z=q[2], rot=ry(yaw), node=S1)
    # The mint hood over the upper run (a half pipe in section), and the mint pipe beside the belt.
    t0, t1 = .56, .92
    hood = []
    for k in range(9):
        th = math.pi * k / 8
        hood.append((math.cos(th) * (W / 2 + .06), math.sin(th) * .14))
    for i in range(len(hood) - 1):
        (x0, y0), (x1, y1) = hood[i], hood[i + 1]
        mx, my = (x0 + x1) / 2, (y0 + y1) / 2; ln = math.hypot(x1 - x0, y1 - y0)
        q = P((t0 + t1) / 2, up=my + .01, side=mx)
        ang = math.degrees(math.atan2(y1 - y0, x1 - x0))
        a.box('team', ln + .01, .03, (t1 - t0) * L, x=q[0], y=q[1], z=q[2], rot=Rb @ rz(ang), node=S2)
    for t in (t0, t1):
        q = P(t, up=.07)
        a.box('patina', W + .18, .05, .05, x=q[0], y=q[1] + .02, z=q[2], rot=Rb, node=S2)
    pp = [tuple(P(t, up=.02, side=-(W / 2 + .1))) for t in np.linspace(.02, .9, 6)]
    a.tube('patina', pp, .022, node=S2, sides=8)
    # The bricks on the belt: the view runs them from the tail (dm_belt_a) to the head (dm_belt_b).
    a.node('dm_belt_a', S2, tuple(P(0, up=.045)), rot=Rb)
    a.node('dm_belt_b', S2, tuple(P(1, up=.045)), rot=Rb)
    for i in range(6):
        c = a.node(f'dm_carry_{i}', S2, tuple(P((i + .5) / 6, up=.045)), rot=Rb)
        a.box('copper', .15, .06, .1, node=c)
        a.sphere('mint', .022, x=.04, y=.03, z=.02, node=c, sub=1)
    # The crusher house at the head: a mint housing on steel legs over the hopper, its mouth open toward the belt (and the
    # camera), the drum turning inside it, a team roof on top.
    CX, CZ, CY = HX - .02, HZ - .02, YH - .1
    for sx in (-1, 1):
        for sz in (-1, 1): a.box('steel', .04, CY - .14 - DK, .04, x=CX + sx * .17, y=(DK + CY - .14) / 2, z=CZ + sz * .17, node=S2)
    a.box('patina', .44, .16, .4, x=CX, y=CY - .05, z=CZ, rot=ry(yaw), node=S2)
    a.box('team', .44, .14, .4, x=CX, y=CY + .1, z=CZ, rot=ry(yaw), node=S2)
    hd = np.array([-u[0], 0, -u[2]]); hd /= np.linalg.norm(hd)
    a.box('dark', .3, .18, .03, x=CX - hd[0] * .2, y=CY + .04, z=CZ - hd[2] * .2, rot=ry(yaw), node=S2)
    cr = a.node('dm_crusher', S2, (CX - hd[0] * .1, CY + .04, CZ - hd[2] * .1), rot=ry(yaw))
    a.cyl('steel', .09, .26, axis='x', node=cr, sections=10)
    for k in range(2):
        a.box('dark', .26, .035, .22, node=cr, rot=rx(90 * k + 45))                    # the crusher's teeth
    a.box('team', .5, .08, .46, x=CX, y=CY + .2, z=CZ, rot=ry(yaw), node=S3)
    a.box('team', .34, .08, .3, x=CX, y=CY + .27, z=CZ, rot=ry(yaw), node=S3)
    hp_ = a.node('dm_pulley_0', S2, tuple(P(1, up=-.045)), rot=ry(yaw))
    a.cyl('steel', .05, W + .02, axis='x', node=hp_, sections=8)
    a.box('dark', .02, .09, .02, node=hp_)
    # The hopper under the crusher: a cream bin with a team rim, its heap of crushed copper.
    BY = CY - .2
    a.rev('cream', [(0, BY - .2), (.06, BY - .2), (.18, BY), (.16, BY + .02), (0, BY - .06)], x=CX, z=CZ, node=S2, sections=14)
    a.node('dm_bin', a.key, (CX, BY + .02, CZ))
    hp = a.node('dm_heap', S2, (CX, BY - .02, CZ))
    for (dx, dz, r) in ((0, 0, .07), (.06, -.04, .05), (-.05, .04, .05)):
        a.sphere('copper', r, x=dx, y=r * .4, z=dz, node=hp, sub=1)
    a.sphere('mint', .025, x=.02, y=.06, z=-.02, node=hp, sub=1)
    return a


# ---------------------------------------------------------------- crystal: the core drill
def S_deep_mine_crystal():
    # A low, wide ring tank round the shaft, its rim in team colour. In the middle, a frosted-glass collar over the shaft and
    # the drill string turning on its rotary table (dm_drill). A low steel bridge spans the ring to the hoist over the
    # collar. Each trip the core barrel (dm_skip) lifts a glowing core (dm_load) out of the shaft through the frosted
    # collar, tips (dm_bucket) at the top, and the shards slide down the chute into the tray on the rim (dm_bin, dm_heap).
    a, S1, S2, S3 = family('deep_mine_crystal', 'shard', lamp_at=(-.22, .86))
    CX, CZ = .14, -.14
    R0, R1, RH = .5, .76, .44         # the ring's inner and outer radius and its rim's height
    ring(a, 'cream', R0 + .06, R1, DK, RH - .18, x=CX, z=CZ, node=S1, sections=36)
    ring(a, 'team', R0 + .03, R1 + .02, RH - .18, RH, x=CX, z=CZ, node=S2, sections=36)
    ring(a, 'cream', R0, R0 + .08, DK, RH - .02, x=CX, z=CZ, node=S1, sections=36)
    a.cyl('dark', R0 + .02, .02, x=CX, y=DK + .01, z=CZ, node=S1, sections=16)     # the tank's floor, in shadow
    ring(a, 'steel', R1 - .01, R1 + .03, DK + .05, DK + .09, x=CX, z=CZ, node=S1, sections=36)   # a hoop round the tank's foot
    collar(a, CX, CZ, S1, r=.24)
    # The frosted-glass collar on its steel foot, a team ring round its top: the core waits down inside it, dark.
    GT = .56                          # the collar's top
    a.cyl('steel', .2, .06, x=CX, y=DK + .03, z=CZ, node=S2, sections=12)
    a.cyl('glass', .17, GT - DK - .06, x=CX, y=(DK + .06 + GT) / 2, z=CZ, node=S2, sections=12)
    ring(a, 'team', .15, .21, GT - .03, GT + .03, x=CX, z=CZ, node=S2, sections=14)
    # The rotary table on top of the collar with the drill string through it: they turn while the mine works.
    dr = a.node('dm_drill', S2, (CX, GT + .03, CZ))
    a.cyl('steel', .17, .05, y=.025, node=dr, sections=12)
    a.box('team', .14, .05, .14, y=.07, node=dr)
    a.box('dark', .3, .02, .03, y=.05, node=dr)
    # The bridge: from the stem's side of the rim across to the hoist over the collar, low and light.
    BY = 1.24
    a.box('steel', .06, .06, 1.0, x=CX - .05, y=BY, z=CZ + .45, node=S2)
    a.box('steel', .06, .06, 1.0, x=CX + .05, y=BY, z=CZ + .45, node=S2)
    for z in (CZ + .9, CZ + .6):
        for s in (-1, 1): a.strut('steel', (CX + s * .05, BY - .03, z), (CX + s * .05, RH, z + .06), .018, node=S2, sections=6)
    a.box('cream', .3, .06, .16, x=CX, y=RH + .03, z=CZ + .66, node=S2)       # the bridge's foot on the rim
    # The hoist block over the collar: a team cap, the sheave inside it.
    a.box('team', .24, .12, .22, x=CX, y=BY + .06, z=CZ, node=S3)
    a.box('team', .26, .05, .24, x=CX, y=BY + .14, z=CZ, node=S3)
    sheave(a, 'dm_sheave', S3, (CX, BY + .02, CZ + .0), .07, 'z', team=False)
    YB = DK + .04                     # the core barrel at rest, down in the collar
    YT = GT + .26                     # at the top: the core clear of the rotary table, glowing over the ring
    rope_to(a, 'dm_rope', S3, CX + .07, BY + .02, CZ, YB + .36)
    a.node('dm_top', S2, (CX, YT, CZ))
    sk = a.node('dm_skip', S2, (CX, YB, CZ))
    for s in (-1, 1): a.box('steel', .02, .32, .03, x=s * .08, y=.16, node=sk)
    a.box('steel', .18, .03, .04, y=.32, node=sk)
    a.node('dm_eye', sk, (.07, .36, 0))
    a.cyl('steel', .012, .06, x=.07, y=.34, node=sk, sections=6)
    bk = a.node('dm_bucket', sk, (0, .03, -.05))
    a.cyl('steel', .075, .02, y=.0, z=.05, node=bk, sections=10)
    for k in range(4):
        th = math.radians(90 * k + 45)
        a.box('steel', .015, .27, .015, x=.07 * math.cos(th), y=.135, z=.05 + .07 * math.sin(th), node=bk)
    ld = a.node('dm_load', bk, (0, .02, .05))
    a.cyl('cyan', .055, .24, y=.12, node=ld, sections=6)
    for k in range(3):
        th = math.radians(120 * k)
        a.cone('cyan', .032, .1, x=.03 * math.cos(th), y=.24, z=.03 * math.sin(th), node=ld, sections=5)
    # The chute from under the tipped barrel's lip down to the tray on the rim at the front, and the tray: a steel dish
    # with a team lip.
    TXr, TZr = CX + .1, CZ - .58
    c0, c1 = np.array([CZ - .2, YT + .08]), np.array([TZr + .1, RH + .12])
    cl = np.linalg.norm(c1 - c0); ca = math.degrees(math.atan2(c0[1] - c1[1], c0[0] - c1[0]))
    cm = (c0 + c1) / 2
    a.box('steel', .12, .018, cl, x=TXr - .02, y=cm[1], z=cm[0], rot=rx(ca), node=S2)
    for s in (-1, 1): a.box('steel', .012, .05, cl, x=TXr - .02 + s * .06, y=cm[1] + .025, z=cm[0], rot=rx(ca), node=S2)
    a.strut('steel', (TXr - .02, RH, cm[0]), (TXr - .02, cm[1] - .02, cm[0]), .015, node=S2, sections=6)
    a.rev('steel', [(0, RH), (.15, RH), (.2, RH + .1), (.18, RH + .11), (0, RH + .04)], x=TXr, z=TZr, node=S2, sections=16)
    a.rev('team', [(.18, RH + .08), (.205, RH + .08), (.21, RH + .12), (.19, RH + .12)], x=TXr, z=TZr, node=S2, sections=16)
    a.node('dm_bin', a.key, (TXr, RH + .1, TZr))
    hp = a.node('dm_heap', S2, (TXr, RH + .05, TZr))
    for (dx, dz, h, lean) in ((0, 0, .14, 0), (.07, -.03, .1, 25), (-.06, .04, .1, -25), (.02, .07, .08, 15), (-.04, -.06, .09, -15)):
        a.cone('crystal', .035, h, x=dx, y=0, z=dz, node=hp, sections=5)
    return a


# ---------------------------------------------------------------- uranium: the containment
def S_deep_mine_uranium():
    # An armoured dome sealed over the shaft: a smoke drum with steel bands and acid sight glasses, a smoke dome with steel
    # ribs and a team cap. A jib crane (dm_jib) beside it swings the hook (dm_skip) between the dome's port and the open
    # cask on the carousel (dm_carousel); each trip the hook lifts an acid rod (dm_load) out of the port, swings it over
    # and lowers it into the cask, and the carousel turns the next cask round.
    a, S1, S2, S3 = family('deep_mine_uranium', 'rod', lamp_at=(-.2, .84))
    DX, DZ = .06, -.06
    RD, YD = .6, .38                  # the dome's radius and the drum's top
    # (the shaft's collar is sealed under the dome: its port is the team ring on the shoulder)
    a.cyl('smoke', RD, YD - DK, x=DX, y=(DK + YD) / 2, z=DZ, node=S1, sections=16)
    ring(a, 'steel', RD - .01, RD + .025, DK + .02, DK + .06, x=DX, z=DZ, node=S1, sections=28)
    ring(a, 'team', RD - .01, RD + .03, YD - .05, YD + .01, x=DX, z=DZ, node=S1, sections=20)
    # Sight glasses round the drum, on the faces the camera sees: the rods glow behind them (sour acid is always lit).
    gl = a.node('dm_glow', S1)                         # lit while there's uranium (DeepMineWorks)
    for th in (-75, -40, -5, 30):
        t = math.radians(th)
        a.box('acid', .14, .07, .03, x=DX + (RD + .005) * math.cos(t), y=(DK + .06 + YD - .05) / 2, z=DZ + (RD + .005) * math.sin(t), rot=ry(-th + 90), node=gl)
    # The dome: squashed, smoke plastic, with steel ribs over it, the port hatch on its shoulder, the team cap on top.
    # (an open shell standing on the drum: already round, so round_models leaves it as it is)
    prof = [((RD - .02) * math.cos(p), .72 * (RD - .02) * math.sin(p)) for p in np.linspace(0, math.radians(58), 6)]
    sh = trimesh.creation.revolve(np.array(prof + [(prof[-1][0] - .001, prof[-1][1] + .0005)]), sections=28)
    sh.apply_transform(bm.axis_rot('y')); sh.apply_translation((DX, YD, DZ)); a.add(S2, 'smoke', sh)
    for k in range(6):
        th = math.radians(60 * k + 30)
        arc = [(DX + (RD - .005) * math.cos(p) * math.cos(th), YD + (RD - .005) * .72 * math.sin(p), DZ + (RD - .005) * math.cos(p) * math.sin(th)) for p in np.linspace(0, math.radians(56), 6)]
        a.tube('team', arc, .024, node=S2, sides=6)     # team ribs over the armour
    # The team cap: the top of the dome, a flip-top lid sealed down on it.
    cap = [(0, YD + .27), (.4, YD + .27), (.43, YD + .3), (.32, YD + .39), (.16, YD + .44), (0, YD + .45)]
    a.rev('team', cap, x=DX, z=DZ, node=S3, sections=16)
    a.cyl('steel', .06, .06, x=DX, y=YD + .47, z=DZ, node=S3, sections=10)
    # The jib: a steel post on the left-front edge, its arm swinging between the port on the dome's shoulder and the cask
    # on the carousel in the front corner (both a jib's length from the post).
    port = np.array([.38, -.04])                       # the port on the dome's shoulder
    CR, cc = .15, np.array([.6, -.68])                 # the carousel: its casks' radius and its centre
    cask = cc - CR * np.array([.7071, -.7071])         # the cask the hook lowers into: the carousel's inner one
    mid, dd = (port + cask) / 2, cask - port
    perp = np.array([-dd[1], dd[0]]); perp /= np.linalg.norm(perp)
    if perp[0] < 0: perp = -perp
    PX, PZ = mid + .3 * perp                           # the post, on the line half way between them
    L = float(np.linalg.norm(port - [PX, PZ]))
    a0 = math.atan2(port[1] - PZ, port[0] - PX)
    rp = float(np.linalg.norm(port - [DX, DZ]))
    yport = YD + .72 * math.sqrt(max(0., (RD - .02) ** 2 - rp ** 2)) - .01
    a.cyl('steel', .15, .05, x=port[0], y=yport + .02, z=port[1], node=S2, sections=10)
    a.rev('team', [(.08, yport + .02), (.12, yport + .02), (.12, yport + .08), (.08, yport + .08)], x=port[0], z=port[1], node=S2, sections=12)
    a.cyl('dark', .08, .03, x=port[0], y=yport + .05, z=port[1], node=S2, sections=10)
    JY = yport + .68                                   # the arm's height: the rod clears the port under it
    a.box('cream', .2, .06, .2, x=PX, y=DK + .03, z=PZ, node=S1)
    a.cyl('steel', .05, JY - DK, x=PX, y=(DK + JY) / 2, z=PZ, node=S2, sections=10)
    jb = a.node('dm_jib', S3, (PX, JY, PZ), rot=ry(-math.degrees(a0)))     # its local +x points along the arm, at the port
    a.box('team', L + .14, .1, .1, x=(L - .04) / 2, y=.03, node=jb)
    a.box('team', .18, .12, .14, x=-.08, y=.04, node=jb)                   # the counterweight and its winch
    a.cyl('steel', .06, .08, y=.12, node=jb, sections=10)
    a.box('steel', .1, .05, .08, x=L, y=-.02, node=jb)                     # the trolley
    yhook = yport + .1                                                    # the hook at rest, the rod down in the port
    a.node('dm_top', jb, (L, yport + .52 - JY, 0))                         # lifted: the rod's foot clear of the port
    sk = a.node('dm_skip', jb, (L, yhook - JY, 0))
    a.box('steel', .1, .03, .06, y=0, node=sk)
    for s in (-1, 1): a.box('steel', .015, .07, .02, x=s * .035, y=-.04, node=sk)
    a.node('dm_eye', sk, (0, .02, 0))
    rope_to(a, 'dm_rope', jb, L, -.05, 0, yhook - JY + .02)
    ld = a.node('dm_load', sk, (0, -.06, 0))
    a.cyl('acid', .055, .32, y=-.17, node=ld, sections=8)
    a.cyl('steel', .06, .04, y=-.01, node=ld, sections=8)
    a.node('dm_bin', a.key, (cask[0], DK + .7, cask[1]))                  # over the cask: the hook lowers to here
    a.node('dm_heap', S2, (cask[0], DK + .3, cask[1]))
    # The carousel in the front corner: a team turntable carrying four shielded casks (smoke drums); the one under the jib
    # takes each rod, and the turntable turns the next round.
    a.cyl('cream', CR + .1, .05, x=cc[0], y=DK + .025, z=cc[1], node=S1, sections=12)
    cs = a.node('dm_carousel', S2, (cc[0], DK + .05, cc[1]))
    a.cyl('team', CR + .09, .04, y=.02, node=cs, sections=12)
    for k in range(4):
        th = math.radians(90 * k + 45)
        x, z = CR * math.cos(th), CR * math.sin(th)
        a.cyl('smoke', .07, .24, x=x, y=.16, z=z, node=cs, sections=6)
        a.cyl('team', .06, .03, x=x, y=.29, z=z, node=cs, sections=6)
    return a


# ---------------------------------------------------------------- the derrick
def S_derrick():
    # A pre-war landmark: a giant riveted steel dispenser on a stepped stone plinth, older and heavier than anything a
    # team builds. Its window shows a stack of steel ingots on a great spring; its huge team flip-top head (dk_head, hinged
    # at its back edge) is grey while nobody holds it. Held, the flywheel on its side (dk_wheel) turns, and on a steady beat
    # a steel brick (dk_brick) is pushed out under the chin, slides down the chute and drops into the tray at its foot
    # (dk_tray), lit by the amber pour lamp under the chin (dk_lamp). Captured, the head flips open and comes down in its
    # new colours.
    a = Asset('derrick', 'structures', 2)
    S1, S2, S3 = a.node('stage_1', a.key), a.node('stage_2', a.key), a.node('stage_3', a.key)
    pad(a, 2)
    # The plinth: two steps of old sugar-pad stone, kraft-stained, with a worn edge.
    a.box('pad', 1.8, .1, 1.8, y=.13, node=S1)
    a.box('kraft', 1.5, .1, 1.5, y=.23, node=S1)
    a.box('pad', 1.44, .02, 1.44, y=.285, node=S1)
    Y0 = .29
    SX, SZ, SW, SD, ST = 0., .12, .86, .74, 1.42       # the stem: centre, width, depth, top
    # The stem: old steel plate on a smoke frame, riveted bands, a window on its front, buttresses at its corners.
    frustum(a, 'oldsteel', (SW + .14, SD + .1), (SW, SD), Y0, ST, x=SX, z=SZ, node=S2)
    for y in (Y0 + .08, .86, ST - .06):
        g = .14 * (ST - y) / (ST - Y0)
        a.box('steel', SW + g + .05, .06, SD + g * .7 + .05, x=SX, y=y, z=SZ, node=S2)
        if y > 1.: continue
        for k in range(5):
            a.box('dark', .025, .025, .01, x=SX - SW / 2 + .1 + k * (SW - .2) / 4, y=y, z=SZ - SD / 2 - .05 - (.03 if y < .5 else 0), node=S2)
    for sx in (-1, 1):
        for sz in (-1, 1):
            hull(a, 'cream', [(SX + sx * (SW / 2 + .02), Y0, SZ + sz * (SD / 2 - .1)), (SX + sx * (SW / 2 + .14), Y0, SZ + sz * (SD / 2 - .1)),
                              (SX + sx * (SW / 2 + .02), Y0, SZ + sz * (SD / 2 + .02)), (SX + sx * (SW / 2 + .14), Y0, SZ + sz * (SD / 2 + .02)),
                              (SX + sx * (SW / 2 + .02), .8, SZ + sz * (SD / 2 - .1)), (SX + sx * (SW / 2 + .02), .8, SZ + sz * (SD / 2 + .02))], node=S1)
    # Grip ribs down the stem's front and side, as on any dispenser's stem.
    for k in range(3): a.box('oldsteel', .05, .38, .04, x=SX + .24 + k * .09, y=.62, z=SZ - SD / 2 - .055, node=S2)
    # A patch plate on the +x face (kraft: an old repair), and a dent's worth of tilt on another.
    a.box('kraft', .02, .2, .22, x=SX + SW / 2 + .01, y=.66, z=SZ + .18, node=S2, rot=rx(4))
    a.box('kraft', .2, .34, .015, x=SX - .1, y=.6, z=SZ - SD / 2 - .045, node=S2)          # rust run down from the window
    # The window: smoke frame, clear glass, steel ingots stacked on the great spring.
    WZ = SZ - SD / 2
    a.box('smoke', .5, .62, .03, x=SX - .1, y=1.08, z=WZ - .01, node=S2)
    a.box('clear', .4, .52, .01, x=SX - .1, y=1.08, z=WZ - .028, node=S2)
    a.box('dark', .38, .5, .02, x=SX - .1, y=1.08, z=WZ - .0, node=S2)
    for i in range(4):
        a.box('foil', .3, .07, .2, x=SX - .1, y=1.28 - i * .09, z=WZ + .02, node=S2)
    a.coil('steel', .11, .016, .86, 1.0, 3, x=SX - .1, z=WZ + .04, node=S2, per_turn=10, sides=5)
    # The flywheel on the +x side, on a steel bearing block: it turns while a team holds the derrick.
    WX, WY, WZr, WR = SX + SW / 2 + .2, .74, SZ + .02, .36
    a.box('smoke', .14, .2, .2, x=WX - .1, y=WY, z=WZr, node=S2)
    a.box('steel', .08, WY - Y0, .14, x=WX + .02, y=(Y0 + WY) / 2, z=WZr, node=S2)
    wh = a.node('dk_wheel', S2, (WX + .08, WY, WZr))
    ts = np.linspace(0, 2 * math.pi, 32, endpoint=False)
    a.hoop('oldsteel', np.c_[np.zeros(32), WR * np.sin(ts), WR * np.cos(ts)], .05, node=wh, side=(1, 0, 0), r2=.035, sides=8)
    a.cyl('steel', .07, .1, axis='x', node=wh, sections=12)
    for k in range(6): a.box('oldsteel', .04, .04, 2 * WR - .04, node=wh, rot=rx(30 * k))
    a.box('team', .05, .1, .05, y=WR - .02, node=wh)                       # a team tab on the rim, so its turning shows
    # The head: huge, team, hinged at the back; a chin over the chute at the front.
    HW, HD, HH = SW + .12, SD + .12, .54
    hd = a.node('dk_head', S3, (SX, ST + .02, SZ + HD / 2), rot=rx(4))
    a.box('team', HW, HH * .62, HD, y=HH * .31, z=-HD / 2, node=hd)                       # the brim
    hull(a, 'team', [(sx * (HW / 2 - .02), HH * .6, z) for sx in (-1, 1) for z in (-.02, -HD + .02)] +
                    [(sx * (HW / 2 - .14), HH + .04, z) for sx in (-1, 1) for z in (-.12, -HD + .16)], node=hd)   # the crown
    a.box('team', HW - .24, .12, .16, y=-.02, z=-HD + .02, node=hd)                       # the chin
    a.box('dark', HW - .3, .04, .02, y=HH * .45, z=-HD - .004, node=hd)                   # the brim's seam
    a.cyl('steel', .05, HW + .04, axis='x', node=hd, sections=10)
    a.box('team', SW + .08, .14, SD + .08, x=SX, y=ST - .05, z=SZ, node=S3)            # the collar the head closes on
    # The mouth under the chin, the amber pour lamp in it, and the chute down to the tray at the derrick's foot.
    MY = ST - .08
    a.box('dark', .4, .12, .04, x=SX + .18, y=MY, z=WZ - .02, node=S2)
    ln = a.node('dk_lamp', S2, (SX + .18, MY + .07, WZ - .06))
    a.box('amber', .32, .05, .05, node=ln)
    c0, c1 = np.array([WZ - .06, MY - .04]), np.array([-.74, Y0 + .16])
    cl = np.linalg.norm(c1 - c0); ca = math.degrees(math.atan2(c0[1] - c1[1], c0[0] - c1[0]))
    cm = (c0 + c1) / 2
    a.box('foil', .3, .03, cl, x=SX + .18, y=cm[1], z=cm[0], rot=rx(ca), node=S2)
    for s in (-1, 1): a.box('oldsteel', .03, .08, cl, x=SX + .18 + s * .16, y=cm[1] + .035, z=cm[0], rot=rx(ca), node=S2)
    a.box('licorice', .2, .022, .03, x=SX + .18, y=c1[1] + .02, z=c1[0] + .015, node=S2)   # a hazard lip: licorice
    a.box('cream', .2, .022, .03, x=SX + .18, y=c1[1] + .02, z=c1[0] + .05, node=S2)
    a.node('dk_chute_top', S2, (SX + .18, MY - .02, WZ - .04))
    a.node('dk_chute_end', S2, (SX + .18, c1[1] + .06, c1[0] - .02))
    # The tray: a heavy steel dish on the plinth's front step, a grate in its floor.
    TX_, TZ_ = SX + .14, -.76
    a.box('steel', .6, .06, .3, x=TX_, y=Y0 - .01, z=TZ_ + .02, node=S1)
    for s in (-1, 1): a.box('oldsteel', .05, .1, .3, x=TX_ + s * .28, y=Y0 + .03, z=TZ_ + .02, node=S1)
    a.box('oldsteel', .6, .1, .05, x=TX_, y=Y0 + .03, z=TZ_ - .12, node=S1)
    # The day's pay so far: steel ingots heaped in the tray (dk_pile bobs as each brick lands on it).
    pl = a.node('dk_pile', S1, (TX_ - .1, Y0 + .02, TZ_ + .02))
    for (dx, dy, dz, r) in ((-.05, 0, -.03, 10), (.06, 0, .02, -15), (0, .06, -.01, 30), (-.08, .0, .07, 70)):
        a.box('foil', .2, .06, .1, x=dx, y=dy + .03, z=dz, node=pl, rot=ry(r))
    a.node('dk_tray', S1, (TX_ + .12, Y0 + .04, TZ_ + .02))
    br = a.node('dk_brick', S2, (SX + .18, MY - .02, WZ - .04))
    a.box('foil', .26, .09, .15, node=br)
    a.box('steel', .2, .02, .1, y=.05, node=br)
    # A chain hanging off the head's hinge bracket at the back, and a lantern bracket on the -x side: old fittings.
    a.tube('steel', [(SX - SW / 2 - .02, ST - .05, SZ + .3), (SX - SW / 2 - .05, ST - .3, SZ + .32), (SX - SW / 2 - .03, ST - .5, SZ + .28)], .012, node=S2, sides=5)
    return a


BUILDERS = {'deep_mine_iron': S_deep_mine_iron, 'deep_mine_copper': S_deep_mine_copper, 'deep_mine_crystal': S_deep_mine_crystal,
            'deep_mine_uranium': S_deep_mine_uranium, 'derrick': S_derrick}

if __name__ == '__main__':
    keys = sys.argv[1:] or list(BUILDERS)
    out = os.environ.get('PEZ_MINES_OUT', SHIPPED)
    with tempfile.TemporaryDirectory() as tmp:
        bm.OUT = tmp
        for k in keys:
            info = BUILDERS[k]().export()
            src = os.path.join(tmp, 'structures', k + '.glb')
            s = round_models.round_file(src, os.path.join(out, k + '.glb'), round_models.RADIUS, round_models.MAX_RADIUS, round_models.MIN_RADIUS)
            b = info['bounds']
            print(f"{k:20s} tris {info['tris']:6d} -> {s['tris_after']:6d} rounded  bounds x {b[0][0]:.2f}..{b[1][0]:.2f} y {b[0][1]:.2f}..{b[1][1]:.2f} z {b[0][2]:.2f}..{b[1][2]:.2f}")
