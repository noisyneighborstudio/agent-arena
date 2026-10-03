#!/usr/bin/env python3
"""Record one unit doing something: follow it with the main camera, grab frames, log its state, and write an MP4
(to watch) and a captioned contact sheet (to review frame by frame).

  python3 arena/record_unit.py --api 7967 --frames 7968 --key mining_truck --until-docked --out /tmp/rec
  python3 arena/record_unit.py --api 7957 --frames 7958 --id 123 --seconds 20 --out /tmp/rec

Only for test and preview copies of the game (never the live arena's ports). Needs ffmpeg and Pillow.
"""
import argparse
import json
import math
import os
import subprocess
import time
import urllib.request

from PIL import Image, ImageDraw, ImageFont


def get(url, timeout=5):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return r.read()


def post(url, body):
    req = urllib.request.Request(url, data=json.dumps(body).encode(), method="POST")
    with urllib.request.urlopen(req, timeout=5) as r:
        return r.read()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", type=int, required=True)
    ap.add_argument("--frames", type=int, required=True)
    ap.add_argument("--key", default="mining_truck")
    ap.add_argument("--id", type=int, default=0)
    ap.add_argument("--team", type=int, default=-1, help="only units of this team")
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--fps", type=float, default=6)
    ap.add_argument("--distance", type=float, default=9)
    ap.add_argument("--until-docked", action="store_true", help="wait for a truck heading to unload; stop after it pulls out")
    ap.add_argument("--crop", type=float, default=0.5, help="keep this fraction of the frame around the centre (the camera follows the unit)")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    if a.api in (7777, 7778) or a.frames in (7777, 7778):
        raise SystemExit("not on the live arena's ports")
    api, fr = f"http://127.0.0.1:{a.api}", f"http://127.0.0.1:{a.frames}"
    os.makedirs(a.out, exist_ok=True)

    def unit(uid):
        try:
            return json.loads(get(f"{api}/api/view/unit?id={uid}"))
        except Exception:
            return None

    def frame():
        return json.loads(get(f"{api}/api/view/frame"))

    # Pick the unit: a given id, or (until-docked) a loaded truck on its way to unload that hasn't reached the bay yet.
    uid = a.id
    deadline = time.time() + 240
    while not uid and time.time() < deadline:
        f = frame()
        for e in f["entities"]:
            if e[1] != a.key or (a.team >= 0 and e[2] != a.team):
                continue
            d = unit(e[0])
            if not d:
                continue
            if not a.until_docked or (d.get("order") == "return_ore" and not d.get("dock")):
                uid = e[0]
                break
        if not uid:
            time.sleep(1)
    if not uid:
        raise SystemExit("no suitable unit found")
    post(f"{api}/api/admin/camera", {"menu": "close", "edge_pan": False, "select": [uid], "distance": a.distance})
    print("recording unit", uid)

    log, n, t0, saw_dock, end_at = [], 0, time.time(), False, None
    while True:
        now = time.time() - t0
        d = unit(uid) or {}
        ent = next((e for e in frame()["entities"] if e[0] == uid), None)
        # Keep the camera on it (selection follows mobile units; re-assert in case something deselected it).
        if n % 12 == 0:
            post(f"{api}/api/admin/camera", {"select": [uid], "distance": a.distance})
        try:
            jpg = get(f"{fr}/frame.jpg")
        except Exception:
            jpg = b""
        if jpg:
            path = os.path.join(a.out, f"f{n:04d}.jpg")
            open(path, "wb").write(jpg)
            if a.crop < 1:
                im = Image.open(path)
                # The HUD's right-hand panels sit off centre; crop a band around the middle where the followed unit is.
                cw, ch = int(im.width * a.crop), int(im.height * a.crop)
                cx, cy = im.width // 2, im.height // 2
                im.crop((cx - cw // 2, cy - ch // 2, cx + cw // 2, cy + ch // 2)).save(path, quality=90)
            row = {"n": n, "t": round(now, 2), "dock": d.get("dock"), "order": d.get("order"), "cargo": d.get("cargo"),
                   "x": d.get("x"), "y": d.get("y"), "facing_deg": round(math.degrees(ent[6]), 1) if ent else None}
            log.append(row)
            n += 1
        if d.get("dock"):
            saw_dock = True
        if a.until_docked and saw_dock and d.get("order") == "harvest" and end_at is None:
            end_at = now + 2.0  # a couple of seconds after it pulls out and heads back to mine
        if (end_at is not None and now >= end_at) or now >= (a.seconds if not a.until_docked else 120):
            break
        time.sleep(max(0, (n / a.fps) - (time.time() - t0)))

    json.dump(log, open(os.path.join(a.out, "log.json"), "w"), indent=1)
    # Video for people.
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(a.fps), "-i", os.path.join(a.out, "f%04d.jpg"),
                    "-vf", "scale=1280:-2", "-pix_fmt", "yuv420p", "-movflags", "+faststart", os.path.join(a.out, "record.mp4")], check=False)
    # Contact sheets for review: every frame, captioned with time and state.
    cols, w = 5, 384
    for s in range(0, len(log), 30):
        chunk = log[s:s + 30]
        rows = math.ceil(len(chunk) / cols)
        first = Image.open(os.path.join(a.out, f"f{chunk[0]['n']:04d}.jpg"))
        h = int(first.height * w / first.width)
        sheet = Image.new("RGB", (cols * w, rows * (h + 18)), (20, 17, 19))
        draw = ImageDraw.Draw(sheet)
        for i, r in enumerate(chunk):
            im = Image.open(os.path.join(a.out, f"f{r['n']:04d}.jpg")).resize((w, h))
            x, y = (i % cols) * w, (i // cols) * (h + 18)
            sheet.paste(im, (x, y + 18))
            draw.text((x + 4, y + 3), f"{r['t']:5.1f}s {r['dock'] or r['order']} c{r['cargo']} f{r['facing_deg']}", fill=(236, 228, 210))
        sheet.save(os.path.join(a.out, f"sheet{s // 30:02d}.jpg"), quality=85)
    print(f"{n} frames -> {a.out}/record.mp4 and sheet*.jpg")


if __name__ == "__main__":
    main()
