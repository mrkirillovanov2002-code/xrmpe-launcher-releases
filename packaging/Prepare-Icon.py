"""Convert the original generated artwork to Windows icon sizes; no network needed."""
from pathlib import Path
from PIL import Image, ImageDraw

assets = Path(__file__).resolve().parents[1] / "src" / "Xrmpe.Launcher" / "Assets"
with Image.open(assets / "launcher-source.png") as original:
    original = original.convert("RGBA")
    original.save(assets / "launcher.ico", format="ICO", sizes=[(16, 16), (32, 32), (48, 48), (256, 256)])
    original.resize((256, 256), Image.Resampling.LANCZOS).save(assets / "launcher-preview.png")
    sheet = Image.new("RGB", (460, 300), "#171b20")
    draw = ImageDraw.Draw(sheet)
    x = 12
    for size in (16, 32, 48, 256):
        resized = original.resize((size, size), Image.Resampling.LANCZOS)
        sheet.paste(resized, (x, 12), resized)
        draw.text((x, 272), str(size), fill="#ffb841")
        x += size + 20
    sheet.save(assets / "icon-sizes.png")
with Image.open(assets / "launcher.ico") as icon:
    assert icon.ico.sizes() == {(16, 16), (32, 32), (48, 48), (256, 256)}
    print("ICO verified:", sorted(icon.ico.sizes()))
