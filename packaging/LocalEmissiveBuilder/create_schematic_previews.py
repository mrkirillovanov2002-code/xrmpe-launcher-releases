"""Original, schematic preview art. Does not read or reproduce game assets."""
from pathlib import Path
import json
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
OUT = ROOT / 'Previews'
OUT.mkdir(exist_ok=True)
items = json.loads((ROOT / 'costumes.json').read_text(encoding='utf-8-sig'))

def colors(name):
    if 'bandit' in name or 'coat' in name: return (80, 67, 53)
    if 'ecolog' in name or 'scientific' in name: return (92, 105, 62)
    if 'dolg' in name or 'monolith' in name: return (69, 76, 78)
    if 'svoboda' in name or 'cs_' in name: return (74, 94, 76)
    if 'soldier' in name or 'military' in name or 'specops' in name: return (91, 97, 91)
    return (91, 86, 70)

for item in items:
    name = item['costume']
    for angle in ('front', 'right', 'back', 'left'):
        image = Image.new('RGB', (288, 500), '#151b22')
        draw = ImageDraw.Draw(image)
        draw.rectangle((12, 12, 276, 488), outline='#354350', width=2)
        draw.text((22, 23), 'ZONELINK / 3D', fill='#b6ff00')
        draw.text((22, 43), 'SCHEMATIC PREVIEW', fill='#aebbc7')
        color = colors(name)
        edge = tuple(min(255, x + 38) for x in color)
        if angle in ('front', 'back'):
            draw.ellipse((112, 100, 176, 166), fill=color, outline=edge, width=3)
            draw.polygon([(102, 166), (186, 166), (198, 318), (90, 318)], fill=color)
            draw.polygon([(89, 169), (101, 169), (88, 306), (67, 306), (66, 215)], fill=color)
            draw.polygon([(187, 169), (199, 169), (222, 306), (200, 306)], fill=color)
            draw.polygon([(94, 318), (140, 318), (134, 442), (96, 442)], fill=color)
            draw.polygon([(148, 318), (194, 318), (192, 442), (154, 442)], fill=color)
            draw.line([(97, 170), (190, 170), (201, 312), (87, 312), (97, 170)], fill=edge, width=3)
            for x in (74, 196):
                draw.rounded_rectangle((x, 191, x + 18, 211), radius=5, fill='#758b22', outline='#b6ff00', width=2)
                draw.line((x + 4, 198, x + 14, 198), fill='#ddffa5', width=2)
        else:
            draw.ellipse((115, 99, 178, 162), fill=color, outline=edge, width=3)
            draw.polygon([(112, 162), (181, 170), (183, 319), (100, 319)], fill=color)
            draw.polygon([(169, 173), (194, 181), (207, 306), (183, 309)], fill=color)
            draw.polygon([(111, 172), (98, 187), (91, 306), (111, 306)], fill=color)
            draw.polygon([(105, 319), (144, 319), (140, 441), (104, 441)], fill=color)
            draw.polygon([(147, 319), (181, 319), (180, 441), (146, 441)], fill=color)
            draw.line([(113, 162), (180, 170), (183, 319), (101, 319), (113, 162)], fill=edge, width=3)
            x = 180 if angle == 'right' else 96
            draw.rounded_rectangle((x, 192, x + 17, 213), radius=4, fill='#758b22', outline='#b6ff00', width=2)
        if 'exo' in name:
            draw.line((85, 171, 79, 315, 110, 330), fill='#a4aaa1', width=5)
            draw.line((200, 171, 209, 315, 181, 330), fill='#a4aaa1', width=5)
        if 'coat' in name:
            draw.polygon([(101, 305), (185, 305), (208, 447), (83, 447)], fill=color, outline=edge)
        draw.text((22, 460), angle.upper() + ' / NOT IN-GAME FOOTAGE', fill='#9baab4')
        image.save(OUT / f'{name}-{angle}.png', optimize=True)

print('Generated', len(list(OUT.glob('*.png'))), 'original schematic preview images')
