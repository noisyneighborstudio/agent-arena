import re, asyncio
from playwright.async_api import async_playwright
SRC = '/tmp/claude-0/-home-claude/96b47b3f-8186-5585-99e5-92f4ab842b90/scratchpad/pez/project/'
def flatten(name):
    s = open(SRC + name).read()
    body = re.search(r'<x-dc>(.*)</x-dc>', s, re.S).group(1)
    helmet = re.search(r'<helmet>(.*?)</helmet>', body, re.S)
    head = helmet.group(1) if helmet else ''
    body = body.replace(helmet.group(0), '') if helmet else body
    return f'<!doctype html><html><head><meta charset="utf-8">{head}</head><body style="margin:0">{body}</body></html>'
async def main():
    async with async_playwright() as p:
        b = await p.chromium.launch()
        for name, out, w, h in (('Main.dc.html', 'style_guide.png', 1600, 1840), ('Hero.dc.html', 'hero_illustration.png', 1600, 900)):
            pg = await b.new_page(viewport={'width': w, 'height': h}, device_scale_factor=2)
            await pg.set_content(flatten(name), wait_until='networkidle')
            await pg.wait_for_timeout(800)
            await pg.screenshot(path=f'/home/claude/pez/pack/concept/{out}', clip={'x': 0, 'y': 0, 'width': w, 'height': h})
            if name == 'Hero.dc.html':
                svg = await pg.eval_on_selector('svg', 'e => e.outerHTML')
                open('/home/claude/pez/pack/concept/hero_illustration.svg', 'w').write(svg.replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ', 1))
        await b.close()
asyncio.run(main())
