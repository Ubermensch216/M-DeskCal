"""Draws the program icon (the same calendar page as the tray icon, with a fixed day number) into src/DeskCal.App/app.ico.
Usage: python tools/make-icon.py   (needs Pillow)"""
from PIL import Image, ImageDraw, ImageFont
import os

def draw(size):
    k = 8  # supersample
    n = size * k
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    s = n / 32
    d.rounded_rectangle([2*s, 4*s, 30*s, 30*s], radius=5*s, fill=(0xF4, 0xF7, 0xFB, 255), outline=(0x9A, 0xA7, 0xBA, 255), width=max(1, int(s*0.6)))
    d.rounded_rectangle([2*s, 4*s, 30*s, 13*s], radius=5*s, fill=(0x3B, 0x82, 0xF6, 255))
    d.rectangle([2*s, 9*s, 30*s, 13*s], fill=(0x3B, 0x82, 0xF6, 255))
    ring = (0x1E, 0x29, 0x3B, 255)
    d.rectangle([9*s, 1*s, 12*s, 7*s], fill=ring)
    d.rectangle([20*s, 1*s, 23*s, 7*s], fill=ring)
    try:
        font = ImageFont.truetype("segoeuib.ttf", int(14 * s))
    except OSError:
        font = ImageFont.load_default()
    d.text((16*s, 22*s), "31", font=font, fill=ring, anchor="mm")
    return im.resize((size, size), Image.LANCZOS)

out = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "DeskCal.App", "app.ico")
sizes = [16, 24, 32, 48, 64, 128, 256]
draw(256).save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=[draw(s) for s in sizes[:-1]])
print("wrote", out, os.path.getsize(out), "bytes")
