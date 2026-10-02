import asyncio, base64, json, os, sys
from playwright.async_api import async_playwright
ROOT = '/home/claude/pez/pack'
async def main():
    man = json.load(open(f'{ROOT}/models/manifest.json'))
    async with async_playwright() as p:
        b = await p.chromium.launch(args=['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
        pg = await b.new_page(viewport={'width': 1920, 'height': 1080})
        pg.on('console', lambda m: print('console:', m.text) if m.type == 'error' else None)
        await pg.goto('http://localhost:8765/render/render.html')
        await pg.wait_for_function('window.ready === true', timeout=60000)
        def save(path, data):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            open(path, 'wb').write(base64.b64decode(data.split(',')[1]))
        what = sys.argv[1] if len(sys.argv) > 1 else 'all'
        if what in ('all', 'icons'):
            for m in man:
                d = await pg.evaluate('([k,n]) => renderIcon(k,n)', [m['kind'], m['key']])
                save(f"{ROOT}/icons/{m['key']}.png", d)
            print('icons done')
        if what in ('all', 'build'):
            for key in ('command_center', 'factory'):
                for i, prog in enumerate((0.08, 0.3, 0.6, 0.88, 1.0)):
                    d = await pg.evaluate('([k,p]) => renderBuild("structures",k,p)', [key, prog])
                    save(f"{ROOT}/renders/build_sequence/{key}_{i}.png", d)
            print('build done')
        if what in ('all', 'map'):
            for mode in ('onaxis', 'offaxis'):
                d = await pg.evaluate('(m) => renderMap(m)', mode)
                save(f"{ROOT}/renders/map_{mode}.png", d)
            print('map done')
        if what == 'hero':
            d = await pg.evaluate('() => renderMap("offaxis", 3200, 1800, true)')
            save(f"{ROOT}/concept/hero_offaxis.png", d)
            print('hero done')
        await b.close()
asyncio.run(main())
