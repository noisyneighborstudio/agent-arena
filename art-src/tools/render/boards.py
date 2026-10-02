import re, asyncio, os, json
from playwright.async_api import async_playwright
SRC = '/tmp/claude-0/-home-claude/96b47b3f-8186-5585-99e5-92f4ab842b90/scratchpad/pez/project/'
A = '/tmp/claude-0/-home-claude/96b47b3f-8186-5585-99e5-92f4ab842b90/scratchpad/pez/assets/'
OUT = '/home/claude/pez/handoff/design/boards/'
BLOB = {
 '11d75f6d81e4a162b8b853faa4ca9d89':'map_offaxis.png','42dd65ab35ad91b660b040aeaf301367':'map_onaxis.png',
 '67c21869cccbf90e65d730331c0ae3b2':'light_tank.png','1012be89ed9b9f087d87eacf829ac3c4':'heavy_tank.png','c15331406f08b6114fb818caaa91d8d5':'laser_tank.png',
 '5b820ed3ac0cea089ebc95a41c0fdeea':'mining_truck.png','a1936fec4566dc358df406375391018a':'rifleman.png','c637e6d3d24e6b2badc452cad93c795d':'gunship.png',
 'd41637501b6f395cebba4c8a004b1973':'factory.png','be535b352884c2d5b90452243f2f3da3':'power_plant.png','35a7fbc12b4b876dcda7d933a9e66495':'laser_tower.png',
 'bfd525e47c95d3f6b9cc16855c64a10b':'artillery.png','c84fcd0cb08bd29d3c96aeb2f925132a':'hero_offaxis.png','4e870289e272d4e15e95134e5c80a555':'command_center_strip.png',
 '60a1286de57bda238ea4136f7e94a8c0':'factory_strip.png','17f228bc255212d84107d526097c5f38':'terrain_before.png','a64d144ad86e6a22d16143844862ba4f':'terrain_after.png'}
def flatten(name):
    s = open(SRC + name).read()
    body = re.search(r'<x-dc>(.*)</x-dc>', s, re.S).group(1)
    h = re.search(r'<helmet>(.*?)</helmet>', body, re.S)
    head = h.group(1) if h else ''
    if h: body = body.replace(h.group(0), '')
    body = re.sub(r'/_blob/([0-9a-f]{32})', lambda m: 'file://' + A + BLOB[m.group(1)], body)
    if name == 'Icons.dc.html':
        man = json.load(open('/home/claude/pez/pack/models/manifest.json'))
        sizes = {'command_center':'3×3','mining_refinery':'3×3','factory':'3×3','fusion_reactor':'3×3','airfield':'3×3','gun_turret':'1×1','sam_site':'1×1','laser_tower':'1×1'}
        def fig(k, size):
            return (f'<figure style="margin: 0; display: flex; flex-direction: column; gap: 8px"><img src="file://{A}{k}.png" style="width: 256px; height: 192px; border-radius: 8px; outline: 1px solid #2A3138">'
                    f'<figcaption style="display: flex; justify-content: space-between; font-size: 14px"><span style="font-family: \'IBM Plex Mono\', monospace">{k}</span><span style="color: #9BA5AE">{size}</span></figcaption></figure>')
        groups = {'structures': [], 'units': [], 'ores': []}
        for m in man:
            size = sizes.get(m['key'], '2×2') if m['kind'] == 'structures' else ('1×1' if m['kind'] == 'ores' else '')
            groups[m['kind']].append(fig(m['key'], size))
        parts = iter([''.join(groups['structures']), ''.join(groups['units']), ''.join(groups['ores'])])
        body = re.sub(r'<sc-for.*?</sc-for>', lambda m: next(parts), body, flags=re.S)
    return f'<!doctype html><html><head><meta charset="utf-8">{head}</head><body style="margin:0">{body}</body></html>'
async def main():
    canvas = json.load(open(SRC + 'canvas.json'))
    os.makedirs(OUT, exist_ok=True)
    async with async_playwright() as p:
        b = await p.chromium.launch(args=['--allow-file-access-from-files'])
        for i, name in enumerate(canvas['order']):
            bd = canvas['boards'][name]
            pg = await b.new_page(viewport={'width': bd['w'], 'height': bd['h']}, device_scale_factor=1.5)
            path = f'/tmp/board_{name}.html'
            open(path, 'w').write(flatten(name))
            await pg.goto('file://' + path, wait_until='networkidle')
            await pg.wait_for_timeout(600)
            await pg.screenshot(path=OUT + f"{i+1:02d}_{name.replace('.dc.html','')}.png", clip={'x':0,'y':0,'width':bd['w'],'height':bd['h']})
            await pg.close()
        await b.close()
asyncio.run(main())
