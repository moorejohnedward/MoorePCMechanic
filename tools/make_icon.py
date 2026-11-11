import math
from PIL import Image, ImageDraw, ImageFilter

S = 1024
ORANGE = (255, 138, 0, 255)
DARK = (23, 29, 37, 255)
STEEL = (205, 214, 224, 255)
STEEL_D = (120, 133, 150, 255)
FACE = (246, 248, 250, 255)
INK = (30, 36, 44, 255)

def rrect(d, box, r, fill, outline=None, width=0):
    d.rounded_rectangle(box, r, fill=fill, outline=outline, width=width)

# ---- background tile (same family as the old icon) ----
bg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(bg)
rrect(d, (24, 24, S - 24, S - 24), 190, ORANGE)
rrect(d, (64, 64, S - 64, S - 64), 155, DARK)

# ---- gear behind (muted orange so the wrench stands out) ----
GEAR = (190, 104, 6, 255)
gcx, gcy, gr = S/2, S/2 - 30, 300
teeth = 10
pts = []
for i in range(teeth * 4):
    ang = 2*math.pi*i/(teeth*4) + math.pi/teeth/2
    rad = gr + 58 if (i % 4) in (0, 1) else gr
    pts.append((gcx + rad*math.cos(ang), gcy + rad*math.sin(ang)))
d.polygon(pts, fill=GEAR)
d.ellipse((gcx-gr, gcy-gr, gcx+gr, gcy+gr), fill=GEAR)
d.ellipse((gcx-205, gcy-205, gcx+205, gcy+205), fill=DARK)

# ---- heartbeat line across the bottom ----
GREEN = (64, 224, 160, 255)
hy = 842
hb = [(150,hy),(360,hy),(410,hy-46),(455,hy+40),(505,hy-120),(560,hy+78),(605,hy-30),(650,hy),(874,hy)]
d.line([(x, y) for x, y in hb], fill=DARK, width=58, joint="curve")
d.line([(x, y) for x, y in hb], fill=GREEN, width=26, joint="curve")
for x, y in (hb[0], hb[-1]):
    d.ellipse((x-13, y-13, x+13, y+13), fill=GREEN)

# ---- wrench drawn horizontally, then rotated ----
w = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(w)
cy = S // 2
# beam
rrect(d, (250, cy - 34, 840, cy + 34), 30, STEEL, outline=STEEL_D, width=8)
# grip
rrect(d, (70, cy - 62, 360, cy + 62), 58, ORANGE, outline=(200, 105, 0, 255), width=8)
for x in range(130, 330, 44):
    d.rounded_rectangle((x, cy - 46, x + 14, cy + 46), 7, fill=(205, 108, 0, 255))
# ratchet head
hx = 850
d.ellipse((hx - 105, cy - 105, hx + 105, cy + 105), fill=STEEL, outline=STEEL_D, width=10)
d.ellipse((hx - 66, cy - 66, hx + 66, cy + 66), fill=(176, 188, 202, 255))
d.rectangle((hx - 36, cy - 36, hx + 36, cy + 36), fill=INK)  # square drive
ANG = 38
w = w.rotate(ANG, resample=Image.BICUBIC, center=(S / 2, S / 2))

# soft shadow under the wrench
sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
sh.paste((0, 0, 0, 150), (0, 0), w.split()[3])
sh = sh.filter(ImageFilter.GaussianBlur(14))
out = bg.copy()
out.alpha_composite(sh, (10, -24))
out.alpha_composite(w, (0, -40))

# ---- dial gauge, drawn upright on the beam ----
dx_, dy_ = 585 - S / 2, 0
a = math.radians(ANG)
dcx = S / 2 + dx_ * math.cos(a) + dy_ * math.sin(a)
dcy = S / 2 - dx_ * math.sin(a) + dy_ * math.cos(a) - 40
R = 185
d = ImageDraw.Draw(out)
# shadow
sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(sh).ellipse((dcx - R, dcy - R, dcx + R, dcy + R), fill=(0, 0, 0, 160))
sh = sh.filter(ImageFilter.GaussianBlur(16))
out.alpha_composite(sh, (10, 18))
d = ImageDraw.Draw(out)
d.ellipse((dcx - R, dcy - R, dcx + R, dcy + R), fill=STEEL, outline=STEEL_D, width=10)
r2 = R - 34
d.ellipse((dcx - r2, dcy - r2, dcx + r2, dcy + r2), fill=FACE)
# green "good" zone arc and ticks over 270 degrees
start, sweep = 135, 270
d.arc((dcx - r2 + 18, dcy - r2 + 18, dcx + r2 - 18, dcy + r2 - 18), start + 160, start + 230, fill=(64, 224, 160, 255), width=22)
for i in range(11):
    t = math.radians(start + sweep * i / 10)
    big = i % 5 == 0
    r_in = r2 - (58 if big else 44)
    r_out = r2 - 14
    d.line((dcx + r_in * math.cos(t), dcy + r_in * math.sin(t), dcx + r_out * math.cos(t), dcy + r_out * math.sin(t)),
           fill=INK, width=14 if big else 9)
# needle
t = math.radians(start + sweep * 0.72)
tip = (dcx + (r2 - 30) * math.cos(t), dcy + (r2 - 30) * math.sin(t))
perp = t + math.pi / 2
base = [(dcx + 16 * math.cos(perp), dcy + 16 * math.sin(perp)), (dcx - 16 * math.cos(perp), dcy - 16 * math.sin(perp))]
d.polygon([tip, base[0], base[1]], fill=ORANGE)
d.ellipse((dcx - 30, dcy - 30, dcx + 30, dcy + 30), fill=INK)
d.ellipse((dcx - 11, dcy - 11, dcx + 11, dcy + 11), fill=ORANGE)

out.save("./icon_1024.png")
sizes = [16, 24, 32, 48, 64, 128, 256]
imgs = [out.resize((s, s), Image.LANCZOS) for s in sizes]
imgs[-1].save("./app.ico", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
imgs[-1].save("./logo.png")
# preview sheet
sheet = Image.new("RGBA", (256 + 128 + 64 + 48 + 32 + 16 + 60, 256), (15, 19, 24, 255))
x = 0
for im in reversed(imgs):
    if im.width in (24,): continue
    sheet.alpha_composite(im, (x, (256 - im.height) // 2)); x += im.width + 10
sheet.save("./preview.png")
print("ok", dcx, dcy)
