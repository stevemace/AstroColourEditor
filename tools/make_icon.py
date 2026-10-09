"""Draws the app icon: a star with diffraction spikes inside a hue wheel, on a night-sky tile.

    python tools/make_icon.py

Writes src/AstroColour.App/Assets/AppIcon.ico (16-256 px), AppIcon.png (1024 px) and AppIcon.icns (macOS). Needs Pillow.
"""
import colorsys
import math
import random
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 2048  # working size; downsampled for anti-aliasing
C = S / 2
OUT = Path(__file__).resolve().parent.parent / "src" / "AstroColour.App" / "Assets"


def radial_gradient(size, inner, outer, centre, radius):
    """RGBA image fading from inner colour at centre to outer colour at radius."""
    img = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(img)
    steps = 256
    for i in range(steps, 0, -1):
        t = i / steps
        r = radius * t
        col = tuple(round(a + (b - a) * t) for a, b in zip(inner, outer))
        draw.ellipse((centre[0] - r, centre[1] - r, centre[0] + r, centre[1] + r), fill=col + (255,))
    return img


def tile():
    """Rounded-square background: deep blue, lighter towards the upper middle."""
    margin, radius = S * 0.04, S * 0.2
    grad = radial_gradient(S, (38, 52, 104), (6, 9, 26), (C, C * 0.85), S * 0.75)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((margin, margin, S - margin, S - margin), radius, fill=255)
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(grad, (0, 0), mask)
    return out, mask


def hue_ring(r_outer, r_inner):
    """Annulus coloured by hue around the circle (red at the top, clockwise)."""
    wheel = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(wheel)
    box = (C - r_outer, C - r_outer, C + r_outer, C + r_outer)
    step = 0.5
    a = 0.0
    while a < 360:
        r, g, b = colorsys.hsv_to_rgb(a / 360, 0.78, 1.0)
        # PIL angles start at 3 o'clock and run clockwise; start at 12 o'clock.
        draw.pieslice(box, a - 90, a - 90 + step + 0.3, fill=(round(r * 255), round(g * 255), round(b * 255), 255))
        a += step
    mask = Image.new("L", (S, S), 0)
    md = ImageDraw.Draw(mask)
    md.ellipse(box, fill=255)
    md.ellipse((C - r_inner, C - r_inner, C + r_inner, C + r_inner), fill=0)
    wheel.putalpha(mask)
    return wheel


def light(alpha, colour=(240, 246, 255)):
    """A layer of solid colour shaped by an alpha mask (blurring the mask, not the colour, avoids dark fringes)."""
    layer = Image.new("RGBA", (S, S), colour + (0,))
    layer.putalpha(alpha)
    return layer


def star():
    """White star: soft halo, bright core and four thin diffraction spikes that fade outwards."""
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    halo = radial_gradient(S, (140, 140, 140), (0, 0, 0), (C, C), S * 0.2).getchannel("R")
    layer = Image.alpha_composite(layer, light(halo, (190, 215, 255)))

    spikes = Image.new("L", (S, S), 0)
    sd = ImageDraw.Draw(spikes)
    for angle in (0, 90, 180, 270):
        th = math.radians(angle)
        dx, dy = math.cos(th), math.sin(th)
        px, py = -dy, dx
        L, W = S * 0.42, S * 0.016
        sd.polygon([(C + px * W, C + py * W), (C + dx * L, C + dy * L), (C - px * W, C - py * W)], fill=255)
    spikes = spikes.filter(ImageFilter.GaussianBlur(S * 0.0025))
    falloff = radial_gradient(S, (255, 255, 255), (0, 0, 0), (C, C), S * 0.42).getchannel("R")
    falloff = falloff.point(lambda v: round(255 * (v / 255) ** 0.6))
    layer = Image.alpha_composite(layer, light(ImageChops.multiply(spikes, falloff)))

    core = Image.new("L", (S, S), 0)
    r = S * 0.05
    ImageDraw.Draw(core).ellipse((C - r, C - r, C + r, C + r), fill=255)
    glow = core.filter(ImageFilter.GaussianBlur(S * 0.03))
    layer = Image.alpha_composite(layer, light(glow))
    layer = Image.alpha_composite(layer, light(glow))
    layer = Image.alpha_composite(layer, light(core.filter(ImageFilter.GaussianBlur(S * 0.004)), (255, 255, 255)))
    return layer


def background_stars(mask):
    """A few faint stars scattered over the tile, kept out of the wheel area."""
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    rng = random.Random(7)
    placed = 0
    while placed < 22:
        x, y = rng.uniform(0, S), rng.uniform(0, S)
        d = math.hypot(x - C, y - C)
        if d < S * 0.4 or mask.getpixel((int(x), int(y))) < 255:
            continue
        r = rng.uniform(S * 0.003, S * 0.008)
        a = rng.randint(120, 230)
        draw.ellipse((x - r, y - r, x + r, y + r), fill=(220, 230, 255, a))
        placed += 1
    return layer


def main():
    bg, mask = tile()
    img = Image.alpha_composite(bg, background_stars(mask))

    ring = hue_ring(S * 0.36, S * 0.285)
    ring_glow = ring.filter(ImageFilter.GaussianBlur(S * 0.025))
    ring_glow.putalpha(ring_glow.getchannel("A").point(lambda v: v * 0.7))
    img = Image.alpha_composite(img, ring_glow)
    img = Image.alpha_composite(img, ring)

    img = Image.alpha_composite(img, star())

    # Keep everything inside the rounded tile.
    img.putalpha(ImageChops.multiply(img.getchannel("A"), mask))

    OUT.mkdir(parents=True, exist_ok=True)
    big = img.resize((1024, 1024), Image.LANCZOS)
    big.save(OUT / "AppIcon.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    frames = [img.resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(OUT / "AppIcon.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    save_icns(big, OUT / "AppIcon.icns")
    print(f"Wrote {OUT / 'AppIcon.ico'}, {OUT / 'AppIcon.png'} and {OUT / 'AppIcon.icns'}")


def save_icns(icon, path):
    """macOS icon: the tile drawn on 824 of 1024 px, the margin Apple's icon grid leaves around it."""
    canvas = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    canvas.paste(icon.resize((824, 824), Image.LANCZOS), (100, 100))
    canvas.save(path)


if __name__ == "__main__":
    main()
