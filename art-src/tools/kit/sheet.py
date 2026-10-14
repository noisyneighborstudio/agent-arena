"""Contact sheet: a grid of frames with their index and time, for reading motion frame by frame.

    python art-src/tools/kit/sheet.py <out.png> <frames_dir> [--cols 6] [--every 1] [--crop WxH] [--width 2000]

Frames are the PNGs in frames_dir (clip.py keeps them), in name order; the label is the index and the seconds since the
first frame (file times). --crop takes a centred crop of each frame first.
"""
import argparse, os
from PIL import Image, ImageDraw

ap = argparse.ArgumentParser()
ap.add_argument('out'); ap.add_argument('dir')
ap.add_argument('--cols', type=int, default=6); ap.add_argument('--every', type=int, default=1)
ap.add_argument('--crop'); ap.add_argument('--width', type=int, default=2000)
a = ap.parse_args()
fs = sorted(f for f in os.listdir(a.dir) if f.endswith(('.png', '.jpg')))[::a.every]
t0 = os.path.getmtime(os.path.join(a.dir, fs[0]))
ims = []
for f in fs:
    im = Image.open(os.path.join(a.dir, f)).convert('RGB')
    if a.crop:
        cw, ch = map(int, a.crop.split('x'))
        W, H = im.size
        im = im.crop((W // 2 - cw // 2, H // 2 - ch // 2, W // 2 + cw // 2, H // 2 + ch // 2))
    ims.append((f, os.path.getmtime(os.path.join(a.dir, f)) - t0, im))
cw = a.width // a.cols
w, h = ims[0][2].size
ch = int(h * cw / w)
rows = (len(ims) + a.cols - 1) // a.cols
sheet = Image.new('RGB', (cw * a.cols, ch * rows), (20, 20, 20))
draw = ImageDraw.Draw(sheet)
for i, (f, t, im) in enumerate(ims):
    x, y = (i % a.cols) * cw, (i // a.cols) * ch
    sheet.paste(im.resize((cw, ch), Image.LANCZOS), (x, y))
    draw.text((x + 4, y + 3), f'{os.path.splitext(f)[0]} {t:.2f}s', fill=(255, 255, 255))
sheet.save(a.out)
print(a.out, len(ims), 'frames')
