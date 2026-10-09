"""Builds launcher/app.ico (16-256 px) for the Propnight Revival launcher.

Original artwork (not the game's logo): a red rounded square in the game's
logo color (#EF4550) with a white "P" and a small crescent moon.
    python tools/make_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

HERE = Path(__file__).resolve().parents[1]
FONTS = [r"C:\Windows\Fonts\seguibl.ttf", r"C:\Windows\Fonts\segoeuib.ttf", r"C:\Windows\Fonts\arialbd.ttf"]


def draw(size=1024):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    # rounded square with a top-left to bottom-right red gradient
    grad = Image.new("RGBA", (size, size))
    gd = ImageDraw.Draw(grad)
    top, bottom = (246, 82, 92), (176, 30, 43)
    for y in range(size):
        t = y / (size - 1)
        gd.line([(0, y), (size, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * 0.23), fill=255)
    img.paste(grad, (0, 0), mask)
    # crescent moon, top right
    moon = Image.new("L", (size, size), 0)
    md = ImageDraw.Draw(moon)
    r = size * 0.10
    cx, cy = size * 0.76, size * 0.23
    md.ellipse([cx - r, cy - r, cx + r, cy + r], fill=255)
    md.ellipse([cx - r * 0.45, cy - r * 1.15, cx + r * 1.35, cy + r * 0.65], fill=0)
    moon_layer = Image.new("RGBA", (size, size), (255, 236, 220, 0))
    moon_layer.putalpha(moon)
    img = Image.alpha_composite(img, moon_layer)
    # the "P"
    font = next((ImageFont.truetype(f, int(size * 0.62)) for f in FONTS if Path(f).exists()), ImageFont.load_default())
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    box = ld.textbbox((0, 0), "P", font=font)
    x = (size - (box[2] - box[0])) / 2 - box[0] - size * 0.06
    y = (size - (box[3] - box[1])) / 2 - box[1] + size * 0.05
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).text((x, y + size * 0.02), "P", font=font, fill=(90, 10, 20, 150))
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(size * 0.02)))
    ld.text((x, y), "P", font=font, fill=(255, 255, 255, 255))
    return Image.alpha_composite(img, layer)


if __name__ == "__main__":
    big = draw()
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    big.resize((256, 256), Image.LANCZOS).save(HERE / "app.ico", sizes=[(s, s) for s in sizes])
    big.resize((512, 512), Image.LANCZOS).save(HERE / "app-icon.png")
    print("app.ico written")
