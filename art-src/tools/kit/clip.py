"""Capture a GIF (and a middle still) of the running kitchen sink.

    PEZPORT=<game port> python art-src/tools/kit/clip.py <out.gif> <x> <y> <distance> <frames> <cropW> <cropH> [yaw] [gap]

Points the camera at map point (x, y) (from /api/admin/showcase), takes <frames> screenshots <gap> s apart (default 0.12),
crops <cropW>x<cropH> around the screen centre, and writes out.gif with the real frame timing plus out_still.png.
Frames are kept in $CLIPDIR (default: a folder next to the GIF) for contact sheets (sheet.py). Distance 3 is the
closest the camera goes. Keep GIFs under ~12 MB: fewer frames or a smaller crop.
"""
import json, os, shutil, sys, time, urllib.request
from PIL import Image

out, x, y, dist, n, cw, ch = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6]), int(sys.argv[7])
yaw = float(sys.argv[8]) if len(sys.argv) > 8 else 45
gap = float(sys.argv[9]) if len(sys.argv) > 9 else 0.12
api = 'http://127.0.0.1:' + os.environ['PEZPORT']


def post(path, body):
    req = urllib.request.Request(api + path, data=json.dumps(body).encode(), method='POST')
    urllib.request.urlopen(req, timeout=10).read()


post('/api/admin/camera', {"x": x, "y": y, "distance": dist, "yaw": yaw})
time.sleep(2.5)
d = os.path.abspath(os.environ.get('CLIPDIR') or os.path.splitext(out)[0] + '_frames')
shutil.rmtree(d, ignore_errors=True)
os.makedirs(d)
for i in range(n):
    post(f'/api/admin/screenshot?path={d}/{i:03d}.png', {})  # written at the end of the frame
    time.sleep(gap)
time.sleep(2)
fs = sorted(f for f in os.listdir(d) if f.endswith('.png'))
ts = [os.path.getmtime(f'{d}/{f}') for f in fs]
W, H = Image.open(f'{d}/{fs[0]}').size
box = (W // 2 - cw // 2, H // 2 - ch // 2, W // 2 + cw // 2, H // 2 + ch // 2)
s = min(1.0, 560 / cw)
frames = [Image.open(f'{d}/{f}').convert('RGB').crop(box).resize((int(cw * s), int(ch * s)), Image.LANCZOS) for f in fs]
dur = [max(20, int((ts[i + 1] - ts[i]) * 1000)) for i in range(len(ts) - 1)] + [200]
frames[0].save(out, save_all=True, append_images=frames[1:], duration=dur, loop=0, optimize=True)
frames[len(frames) // 2].save(out.replace('.gif', '_still.png'))
print(out, len(fs), 'frames', round(ts[-1] - ts[0], 1), 's', os.path.getsize(out) // 1024, 'KB; frames in', d)
