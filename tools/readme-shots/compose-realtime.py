"""Composes 即時翻譯預覽.png from the two shots shoot.ps1 -Phase realtime takes.

realtime-profiles-full.png  the block framed, the 模式 panel open over the bar, 模式 pressed
realtime-appearance-full.png  the same screen with the 顯示外觀 panel open instead

The first is the picture; the 顯示外觀 panel is lifted out of the second and set under the game. Both
were shot on one flat colour (viewer.ps1), so anything that is not that colour is the app.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

out = Path(sys.argv[1])
bg = tuple(int(v) for v in sys.argv[2].split(','))        # the viewer's background, e.g. 30,30,32
sx, sy, sw, sh = (int(v) for v in sys.argv[3].split(','))  # the game picture on the screen
target = Path(sys.argv[4])

prof = Image.open(out / 'realtime-profiles-full.png').convert('RGB')
appe = Image.open(out / 'realtime-appearance-full.png').convert('RGB')


def lit(img):
    a = np.asarray(img).astype(int)
    return np.abs(a - np.array(bg)).sum(2) > 6


def box(mask, x0, y0, x1, y1):
    m = mask[y0:y1, x0:x1]
    ys = np.where(m.any(1))[0]
    xs = np.where(m.any(0))[0]
    if not len(ys):
        raise SystemExit('nothing found in %r' % ((x0, y0, x1, y1),))
    return x0 + xs[0], y0 + ys[0], x0 + xs[-1] + 1, y0 + ys[-1] + 1


P = lit(prof)
A = lit(appe)
H, W = P.shape
# Everything the app drew in the first shot: the framed game, the 模式 panel and the bar.
l, t, r, b = box(P, 0, 0, W, H)
# The 顯示外觀 panel: its window as UI Automation gave it. It opens over the bar, so its edges are
# read from the panel itself: the top rows hold nothing else, and under its middle the panel hides the
# bar. Cut tight and rounded, without the shadow, so no sliver of the bar comes along.
ix, iy, iw, ih = (int(v) for v in sys.argv[5].split(','))
wx, wy, ww, wh = (int(v) for v in sys.argv[6].split(','))
# The window can reach past the screen's edge (its top was -20 once): keep to the shot.
if wy < 0:
    wh, wy = wh + wy, 0
if wx < 0:
    ww, wx = ww + wx, 0
ww, wh = min(ww, W - wx), min(wh, H - wy)
# Lighter than the background: the panel, not its shadow, which is darker.
win = np.asarray(appe).astype(int)[wy:wy + wh, wx:wx + ww].mean(2) > sum(bg) / 3 + 3
mid = ww // 2
col = np.where(win[:, mid])[0]
# When the panel opens above the bar, the bar is the last light run, short and below a gap: leave it
# out. Darker controls inside the panel break the column too, so only that last short run is dropped.
runs = np.split(col, np.where(np.diff(col) > 1)[0] + 1)
if len(runs) > 1 and len(runs[-1]) < 90:
    runs = runs[:-1]
pt, pb = runs[0][0], runs[-1][-1] + 1
band = np.where(win[pt + 20])[0]
# Where a wide panel overlaps the game, the game is lighter too: walk left from the middle along the
# row while the pixels stay the panel's neutral greys.
row = np.asarray(appe).astype(int)[wy + pt + 20, wx:wx + ww]
def panel_like(p):
    return abs(p[0] - p[1]) <= 6 and abs(p[1] - p[2]) <= 6 and p.mean() > sum(bg) / 3 + 3
lo = mid
while lo > 0 and panel_like(row[lo - 1]):
    lo -= 1
# The game is only ever to the left: the right edge is the last light pixel, as before.
band = np.arange(lo, band[-1] + 1)
# Two pixels in: the panel's soft outer edge lets the bar under it show through.
pl, pr = band[0] + 2, band[-1] - 1
pt, pb = pt + 2, pb - 2
panel = appe.crop((wx + pl, wy + pt, wx + pr, wy + pb))
S0 = 4
big = Image.new('L', (panel.width * S0, panel.height * S0), 0)
ImageDraw.Draw(big).rounded_rectangle((0, 0, big.width - 1, big.height - 1), radius=16 * S0, fill=255)
panel_mask = big.resize(panel.size, Image.LANCZOS)

M = 48
gap = 28
# Two groups from the first shot: the framed game, and the 模式 panel with the bar under it. The bar
# reaches left under the game, so the game is cut by its own rectangle, not by a column.
gl, gt, gr, gb = box(P, 0, 0, sx + sw + 6, sy + sh + 6)
rest = P.copy()
rest[gt:gb, gl:gr] = False
rl, rt, rr, rb = box(rest, 0, 0, W, H)
# The panel's top edge, under its shadow: the first row lighter than the background.
light = np.asarray(prof).astype(int).mean(2) > sum(bg) / 3 + 3
panel_top = rt + np.where(light[rt:rb, rl:rr].any(1))[0][0]
# The game's top level with the 模式 panel's, the 顯示外觀 panel under the game, and the right group
# where it was beside the game.
# The 模式 panel a fixed gap right of the game, wherever the bar was moved to for the shots; the bar
# keeps its place under the panel.
panel_left = rl + np.where(light[panel_top + 20, rl:rr])[0][0]
rx, ry = M + (gr - gl) + 50 - (panel_left - rl), M
gx, gy = M, M + (panel_top - rt) - 2
ax, ay = gx + (sx - gl), gy + (gb - gt) + gap
width = max(rx + (rr - rl), ax + panel.width) + M
height = max(ry + (rb - rt), ay + panel.height) + M
canvas = Image.new('RGB', (width, height), bg)
right_group = prof.crop((rl, rt, rr, rb))
# The bar reaches left under the game, so this rectangle takes a strip of the game too: blank it.
ImageDraw.Draw(right_group).rectangle((gl - rl, gt - rt, gr - rl - 1, gb - rt - 1), fill=bg)
canvas.paste(right_group, (rx, ry))
# The game after the right group, which is blank where it reaches under the game.
canvas.paste(prof.crop((gl, gt, gr, gb)), (gx, gy))
canvas.paste(panel, (ax, ay), panel_mask)

# A dashed arrow from the 顯示外觀 panel to the 外觀 button on the bar it came from: out of the
# panel's right edge, then a right angle to the button.
icx = ix + iw / 2 - rl + rx
icon_top, icon_bottom = iy - rt + ry, iy + ih - rt + ry
px1 = ax + panel.width + 14          # just clear of the panel
p_top, p_bottom = ay, ay + panel.height
sy0 = min(max((p_top + p_bottom) / 2, icon_bottom + 40), p_bottom - 24)
up = sy0 > icon_bottom
end_y = icon_bottom + 10 if up else icon_top - 10

S = 4
layer = Image.new('RGBA', (canvas.width * S, canvas.height * S), (0, 0, 0, 0))
dl = ImageDraw.Draw(layer)
color = (96, 196, 250, 255)
width = 3 * S
dash, space = 10 * S, 7 * S


def dashed(x0, y0, x1, y1):
    length = abs(x1 - x0) + abs(y1 - y0)
    pos = 0
    while pos < length:
        seg = min(dash, length - pos)
        if x0 == x1:
            dy = 1 if y1 > y0 else -1
            dl.line([(x0, y0 + dy * pos), (x0, y0 + dy * (pos + seg))], fill=color, width=width)
        else:
            dx = 1 if x1 > x0 else -1
            dl.line([(x0 + dx * pos, y0), (x0 + dx * (pos + seg), y0)], fill=color, width=width)
        pos += dash + space


X0, Y0, XC, YE = px1 * S, sy0 * S, icx * S, end_y * S
head = 12 * S
dashed(X0, Y0, XC, Y0)
dashed(XC, Y0, XC, YE + (head if up else -head))
if up:
    dl.polygon([(XC, YE), (XC - head * 0.7, YE + head), (XC + head * 0.7, YE + head)], fill=color)
else:
    dl.polygon([(XC, YE), (XC - head * 0.7, YE - head), (XC + head * 0.7, YE - head)], fill=color)
dl.ellipse([X0 - 5 * S, Y0 - 5 * S, X0 + 5 * S, Y0 + 5 * S], fill=color)
canvas = Image.alpha_composite(canvas.convert('RGBA'), layer.resize(canvas.size, Image.LANCZOS)).convert('RGB')
canvas.save(target)
print('%s %dx%d' % (target, canvas.width, canvas.height))
