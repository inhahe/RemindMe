"""Draws the app icon (an alarm clock) and writes every size both apps need.

Run from the repository root:  python tools/make-icons.py
Outputs:
  RemindMe.Desktop/app.ico                       Windows exe, window and tray icon
  RemindMe.Android/Resources/mipmap-*/appicon*.png  launcher icons (legacy + adaptive layers)
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
S = 1024  # drawing size; everything is downscaled from this
BODY = (230, 81, 0, 255)       # deep orange
BELL = (255, 160, 0, 255)      # amber
FACE = (255, 255, 255, 255)
INK = (33, 33, 33, 255)
BG = (25, 118, 210, 255)       # blue, Android adaptive background


def clock(size_frac=1.0, cx=0.5, cy=0.54):
    """The alarm clock on a transparent square; size_frac scales it within the square."""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def p(x):  # fraction of the canvas → pixels, scaled about the centre
        return x * S * size_frac

    ox, oy = cx * S, cy * S
    r = p(0.36)
    # legs
    for sx in (-1, 1):
        d.line([(ox + sx * r * 0.55, oy + r * 0.75), (ox + sx * r * 0.85, oy + r * 1.12)], fill=INK, width=int(p(0.05)))
    # bells
    for sx in (-1, 1):
        bx, by, br = ox + sx * r * 0.74, oy - r * 0.84, p(0.13)
        d.ellipse([bx - br, by - br * 0.95, bx + br, by + br * 0.95], fill=BELL)
    # hammer
    d.line([(ox, oy - r * 1.0), (ox, oy - r * 1.22)], fill=INK, width=int(p(0.04)))
    d.ellipse([ox - p(0.035), oy - r * 1.22 - p(0.035), ox + p(0.035), oy - r * 1.22 + p(0.035)], fill=INK)
    # body and face
    d.ellipse([ox - r, oy - r, ox + r, oy + r], fill=BODY)
    fr = r * 0.80
    d.ellipse([ox - fr, oy - fr, ox + fr, oy + fr], fill=FACE)
    # hour ticks
    import math
    for i in range(12):
        a = math.radians(i * 30)
        r1, r2 = fr * 0.80, fr * (0.95 if i % 3 else 0.98)
        w = int(p(0.025 if i % 3 == 0 else 0.014))
        d.line([(ox + r1 * math.sin(a), oy - r1 * math.cos(a)), (ox + r2 * math.sin(a), oy - r2 * math.cos(a))], fill=INK, width=w)
    # hands at 10:10
    def hand(deg, length, width):
        a = math.radians(deg)
        d.line([(ox, oy), (ox + length * math.sin(a), oy - length * math.cos(a))], fill=INK, width=int(width))
    hand(-60, fr * 0.50, p(0.05))   # hour
    hand(60, fr * 0.72, p(0.035))   # minute
    d.ellipse([ox - p(0.04), oy - p(0.04), ox + p(0.04), oy + p(0.04)], fill=INK)
    return img


def windows_ico():
    img = clock(size_frac=1.0, cy=0.56)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    out = ROOT / "RemindMe.Desktop" / "app.ico"
    img.save(out, sizes=[(s, s) for s in sizes])
    print("wrote", out)


def android():
    res = ROOT / "RemindMe.Android" / "Resources"
    dens = {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}
    # Adaptive icon layers are 108dp with the central 72dp safe zone.
    fg = clock(size_frac=0.62, cy=0.53)
    bg = Image.new("RGBA", (S, S), BG)
    # Legacy square icon: the clock on a blue rounded square.
    legacy = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.18), fill=255)
    legacy.paste(bg, (0, 0), mask)
    legacy.alpha_composite(clock(size_frac=0.95, cy=0.54))
    for name, k in dens.items():
        folder = res / f"mipmap-{name}"
        folder.mkdir(parents=True, exist_ok=True)
        layer = int(108 * k)
        fg.resize((layer, layer), Image.LANCZOS).save(folder / "appicon_foreground.png")
        bg.resize((layer, layer), Image.LANCZOS).save(folder / "appicon_background.png")
        legacy.resize((int(48 * k), int(48 * k)), Image.LANCZOS).save(folder / "appicon.png")
    # Notification icon: white silhouette on transparent, 24dp.
    sil = clock(size_frac=1.0, cy=0.56)
    alpha = sil.getchannel("A")
    white = Image.new("RGBA", (S, S), (255, 255, 255, 0))
    white.putalpha(alpha)
    for name, k in dens.items():
        folder = res / f"drawable-{name}"
        folder.mkdir(parents=True, exist_ok=True)
        white.resize((int(24 * k), int(24 * k)), Image.LANCZOS).save(folder / "ic_stat_alarm.png")
    print("wrote android icons")


if __name__ == "__main__":
    windows_ico()
    android()
