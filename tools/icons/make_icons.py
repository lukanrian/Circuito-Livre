"""
Gera os ícones da ribbon do Circuito Livre (16 e 32 px) em src/Resources.
Desenhos próprios, simples e chapados. Uso: python tools/icons/make_icons.py
"""
import math
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "src", "Resources")
S = 128  # desenha grande e reduz

BLUE = (33, 102, 172)
DBLUE = (20, 60, 110)
LBLUE = (120, 175, 225)
ORANGE = (240, 140, 20)
YELLOW = (250, 200, 30)
GREEN = (40, 150, 70)
RED = (210, 50, 50)
GRAY = (110, 110, 110)
LGRAY = (205, 205, 205)
WHITE = (255, 255, 255)
BLACK = (40, 40, 40)
W = 8  # espessura padrão


def font(size):
    for name in ("DejaVuSans-Bold.ttf", "arialbd.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def text(d, xy, t, size, fill=BLACK):
    f = font(size)
    box = d.textbbox((0, 0), t, font=f)
    x = xy[0] - (box[2] - box[0]) / 2 - box[0]
    y = xy[1] - (box[3] - box[1]) / 2 - box[1]
    d.text((x, y), t, font=f, fill=fill)


def bolt(d, cx, cy, s, fill=YELLOW, outline=ORANGE):
    pts = [(0.15, -0.5), (-0.25, 0.08), (0.02, 0.08), (-0.15, 0.5), (0.28, -0.1), (0.0, -0.1), (0.15, -0.5)]
    d.polygon([(cx + x * s, cy + y * s) for x, y in pts], fill=fill, outline=outline, width=3)


def tag(d, x, y, w, h, fill=WHITE, outline=BLUE):
    d.polygon([(x, y + h / 2), (x + h / 2, y), (x + w, y), (x + w, y + h), (x + h / 2, y + h)], fill=fill, outline=outline, width=W - 2)
    d.ellipse([x + h / 2 - 6, y + h / 2 - 6, x + h / 2 + 6, y + h / 2 + 6], fill=outline)


def magnifier(d, cx, cy, r, color=DBLUE):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=color, width=W)
    a = math.radians(45)
    d.line([cx + r * math.cos(a), cy + r * math.sin(a), cx + r * 2 * math.cos(a), cy + r * 2 * math.sin(a)], fill=color, width=W + 4)


def arrow(d, x1, y1, x2, y2, color=BLUE, w=W, head=18):
    d.line([x1, y1, x2, y2], fill=color, width=w)
    ang = math.atan2(y2 - y1, x2 - x1)
    p1 = (x2 - head * math.cos(ang - 0.5), y2 - head * math.sin(ang - 0.5))
    p2 = (x2 - head * math.cos(ang + 0.5), y2 - head * math.sin(ang + 0.5))
    d.polygon([(x2, y2), p1, p2], fill=color)


def pipe(d, pts, color=GRAY, w=16):
    d.line(pts, fill=color, width=w, joint="curve")
    d.line(pts, fill=LGRAY, width=w - 8, joint="curve")


def panel_box(d, x, y, w, h):
    d.rounded_rectangle([x, y, x + w, y + h], 6, fill=LGRAY, outline=DBLUE, width=W - 2)
    for i in range(3):
        yy = y + 14 + i * (h - 24) / 3
        d.line([x + 12, yy, x + w - 12, yy], fill=GRAY, width=5)


def table(d, x, y, w, h, rows=4, cols=3, head=BLUE):
    d.rectangle([x, y, x + w, y + h], fill=WHITE, outline=DBLUE, width=5)
    d.rectangle([x, y, x + w, y + h / rows], fill=head)
    for i in range(1, rows):
        d.line([x, y + i * h / rows, x + w, y + i * h / rows], fill=DBLUE, width=3)
    for j in range(1, cols):
        d.line([x + j * w / cols, y, x + j * w / cols, y + h], fill=DBLUE, width=3)


def document(d, x, y, w, h):
    d.polygon([(x, y), (x + w - 22, y), (x + w, y + 22), (x + w, y + h), (x, y + h)], fill=WHITE, outline=DBLUE, width=6)
    for i in range(4):
        yy = y + 34 + i * 16
        d.line([x + 12, yy, x + w - 14, yy], fill=GRAY, width=5)


def circle_num(d, cx, cy, r, t, fill=YELLOW, outline=ORANGE):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=fill, outline=outline, width=6)
    text(d, (cx, cy), t, int(r * 1.3))


# ---------------------------------------------------------------- desenhos

def i_settings(d):
    d.ellipse([10, 10, 118, 118], outline=BLUE, width=10)
    bolt(d, 64, 64, 90)


def i_tags(d):
    tag(d, 10, 50, 80, 44)
    magnifier(d, 82, 40, 20)


def i_tagdim(d):
    pipe(d, [(10, 90), (118, 90)])
    tag(d, 24, 14, 84, 44)
    text(d, (78, 36), "Ø", 30, BLUE)


def i_taglevel(d):
    pipe(d, [(40, 120), (40, 10)])
    d.line([8, 64, 72, 64], fill=DBLUE, width=4)
    tag(d, 54, 70, 70, 40)
    arrow(d, 96, 56, 96, 10, GREEN)


def i_links(d):
    table(d, 8, 14, 84, 74, 4, 3)
    d.ellipse([70, 74, 100, 104], outline=ORANGE, width=8)
    d.ellipse([92, 74, 122, 104], outline=ORANGE, width=8)


def i_levels(d):
    for i, y in enumerate((24, 64, 104)):
        d.line([10, y, 92, y], fill=BLUE, width=6)
        d.polygon([(96, y - 12), (118, y), (96, y + 12)], fill=ORANGE)


def i_importloads(d):
    d.rectangle([10, 70, 118, 118], fill=LGRAY, outline=DBLUE, width=6)
    bolt(d, 52, 40, 70)
    arrow(d, 92, 10, 92, 92, GREEN)


def i_lamp(d):
    d.line([64, 8, 64, 34], fill=GRAY, width=6)
    d.polygon([(24, 72), (40, 34), (88, 34), (104, 72)], fill=LGRAY, outline=DBLUE, width=6)
    d.chord([40, 54, 88, 100], 0, 180, fill=YELLOW, outline=ORANGE, width=4)
    for a in (200, 270, 340):
        r = math.radians(a)


def i_outlet(d):
    d.rounded_rectangle([14, 14, 114, 114], 18, fill=WHITE, outline=DBLUE, width=8)
    d.ellipse([34, 34, 94, 94], outline=GRAY, width=6)
    d.ellipse([46, 54, 58, 66], fill=BLACK)
    d.ellipse([70, 54, 82, 66], fill=BLACK)
    d.ellipse([58, 72, 70, 84], fill=BLACK)


def i_switch(d):
    d.rounded_rectangle([24, 8, 104, 120], 14, fill=WHITE, outline=DBLUE, width=8)
    d.rounded_rectangle([46, 28, 82, 100], 8, fill=LBLUE, outline=BLUE, width=5)
    d.line([52, 52, 76, 52], fill=DBLUE, width=6)


def i_cota(d):
    d.line([16, 20, 16, 108], fill=GRAY, width=6)
    d.line([112, 20, 112, 108], fill=GRAY, width=6)
    arrow(d, 64, 64, 18, 64, BLUE, 6, 16)
    arrow(d, 64, 64, 110, 64, BLUE, 6, 16)
    text(d, (64, 38), "1,20", 26, DBLUE)


def i_conduit(d):
    pipe(d, [(10, 104), (64, 104), (64, 24), (118, 24)])
    d.ellipse([52, 92, 76, 116], fill=ORANGE)
    d.ellipse([52, 12, 76, 36], fill=ORANGE)


def i_tray(d):
    d.polygon([(10, 60), (90, 30), (118, 50), (38, 80)], fill=LGRAY, outline=DBLUE, width=6)
    d.line([38, 80, 38, 100], fill=DBLUE, width=6)
    d.line([118, 50, 118, 70], fill=DBLUE, width=6)
    d.line([38, 100, 118, 70], fill=DBLUE, width=6)
    d.line([10, 60, 10, 80], fill=DBLUE, width=6)
    d.line([10, 80, 38, 100], fill=DBLUE, width=6)


def i_detour(d):
    pipe(d, [(8, 96), (38, 96), (38, 36), (90, 36), (90, 96), (120, 96)], BLUE)


def i_supports(d):
    pipe(d, [(8, 40), (120, 40)])
    for x in (30, 98):
        d.line([x, 50, x, 118], fill=DBLUE, width=8)
        d.line([x - 16, 118, x + 16, 118], fill=DBLUE, width=8)
    d.rectangle([56, 28, 72, 52], fill=ORANGE)


def i_central(d):
    document(d, 18, 8, 76, 104)
    bolt(d, 92, 86, 56)


def i_adjusttags(d):
    for i, y in enumerate((24, 56, 88)):
        tag(d, 30 + (i % 2) * 14, y, 76, 26)
    arrow(d, 16, 110, 16, 18, GREEN, 6, 14)


def i_visibility(d):
    d.ellipse([8, 34, 120, 94], outline=DBLUE, width=8)
    d.ellipse([44, 44, 84, 84], fill=BLUE)
    d.ellipse([56, 56, 72, 72], fill=WHITE)


def i_tagsview(d):
    d.rectangle([8, 8, 88, 88], outline=GRAY, width=5)
    d.rectangle([40, 40, 120, 120], fill=WHITE, outline=DBLUE, width=5)
    tag(d, 50, 66, 62, 30)


def i_route(d):
    d.ellipse([8, 52, 32, 76], fill=DBLUE)
    arrow(d, 32, 64, 110, 22, ORANGE)
    arrow(d, 32, 64, 110, 106, GREEN)
    arrow(d, 32, 64, 114, 64, BLUE)


def i_legend(d):
    circle_num(d, 64, 64, 50, "1")


def i_legendview(d):
    table(d, 30, 20, 90, 96, 4, 2, LBLUE)
    circle_num(d, 28, 34, 22, "1")
    circle_num(d, 28, 90, 22, "2")


def i_legendreset(d):
    circle_num(d, 58, 58, 44, "1")
    d.arc([60, 60, 122, 122], 0, 270, fill=GREEN, width=8)
    d.polygon([(122, 80), (108, 92), (124, 98)], fill=GREEN)


def i_legendauto(d):
    circle_num(d, 30, 64, 24, "1")
    pipe(d, [(54, 64), (120, 64)], BLUE)
    circle_num(d, 98, 64, 18, "2", LBLUE, BLUE)


def i_sizeinfra(d):
    for r, c in ((54, BLUE), (38, LBLUE), (22, GREEN)):
        d.ellipse([64 - r, 64 - r, 64 + r, 64 + r], outline=c, width=12)


def i_center(d):
    for r in (54, 34, 14):
        d.ellipse([64 - r, 64 - r, 64 + r, 64 + r], outline=DBLUE, width=6)
    d.line([64, 4, 64, 124], fill=RED, width=4)
    d.line([4, 64, 124, 64], fill=RED, width=4)


def i_centerpanel(d):
    i_center(d)
    d.rectangle([84, 84, 124, 124], fill=LGRAY, outline=DBLUE, width=5)


def i_dimension(d):
    d.rounded_rectangle([20, 10, 108, 118], 10, fill=WHITE, outline=DBLUE, width=8)
    bolt(d, 64, 50, 56)
    arrow(d, 30, 98, 98, 98, GREEN, 6, 14)


def i_dimproject(d):
    d.rounded_rectangle([8, 8, 82, 92], 8, fill=LGRAY, outline=GRAY, width=6)
    d.rounded_rectangle([38, 34, 120, 120], 8, fill=WHITE, outline=DBLUE, width=8)
    bolt(d, 79, 72, 52)


def i_grouping(d):
    d.ellipse([10, 10, 118, 118], outline=RED, width=10)
    for x in (44, 64, 84):
        d.ellipse([x - 12, 52, x + 12, 76], fill=GRAY)
    d.line([30, 98, 98, 30], fill=RED, width=10)


def i_update(d):
    panel_box(d, 14, 14, 70, 90)
    d.arc([60, 60, 122, 122], 30, 300, fill=GREEN, width=9)
    d.polygon([(110, 64), (124, 80), (100, 82)], fill=GREEN)


def i_create(d):
    panel_box(d, 10, 20, 70, 96)
    d.line([96, 30, 96, 90], fill=GREEN, width=12)
    d.line([66, 60, 126, 60], fill=GREEN, width=12)


def i_order_dev(d):
    for i, y in enumerate((22, 64, 106)):
        circle_num(d, 22, y, 18, str(i + 1), LBLUE, BLUE)
        d.line([48, y, 118, y], fill=GRAY, width=8)


def i_order_panel(d):
    panel_box(d, 8, 10, 56, 108)
    for i, y in enumerate((30, 64, 98)):
        text(d, (96, y), str(i + 1), 34, DBLUE)


def i_order_space(d):
    d.rectangle([8, 8, 120, 120], outline=DBLUE, width=6)
    d.line([64, 8, 64, 120], fill=DBLUE, width=6)
    text(d, (36, 64), "1", 44, BLUE)
    text(d, (92, 64), "2", 44, BLUE)


def i_props(d):
    panel_box(d, 8, 10, 76, 108)
    d.ellipse([70, 66, 122, 118], fill=BLUE)
    text(d, (96, 92), "i", 40, WHITE)


def i_identify(d):
    tag(d, 8, 40, 116, 52)
    text(d, (78, 66), "ID", 34, BLUE)


def i_balance(d):
    d.line([64, 20, 64, 116], fill=DBLUE, width=8)
    d.line([20, 36, 108, 36], fill=DBLUE, width=8)
    for x in (24, 104):
        d.chord([x - 20, 50, x + 20, 90], 0, 180, fill=YELLOW, outline=ORANGE, width=4)
        d.line([x, 36, x - 18, 70], fill=GRAY, width=3)
        d.line([x, 36, x + 18, 70], fill=GRAY, width=3)
    d.line([40, 116, 88, 116], fill=DBLUE, width=10)


def i_returns(d):
    i_switch_small = (14, 20, 60, 108)
    d.rounded_rectangle(i_switch_small, 10, fill=WHITE, outline=DBLUE, width=6)
    text(d, (37, 64), "a", 40, BLUE)
    d.ellipse([70, 40, 120, 90], fill=YELLOW, outline=ORANGE, width=5)
    text(d, (95, 65), "a", 34, BLACK)


def i_path(d):
    pipe(d, [(8, 100), (60, 100), (60, 28), (120, 28)], (230, 0, 126), 18)


def i_reports(d):
    table(d, 8, 12, 112, 104, 5, 3, GREEN)


def i_reportdim(d):
    table(d, 8, 12, 90, 104, 5, 3, BLUE)
    bolt(d, 100, 92, 48)


def i_schedule(d):
    table(d, 8, 12, 112, 104, 6, 4, LBLUE)


def i_memorial(d):
    document(d, 20, 8, 88, 112)
    text(d, (64, 100), "W", 22, BLUE)


def i_diagram(d):
    d.line([20, 16, 20, 112], fill=DBLUE, width=8)
    for y in (32, 64, 96):
        d.line([20, y, 76, y], fill=DBLUE, width=6)
        d.rectangle([76, y - 10, 96, y + 10], fill=ORANGE)
        d.line([96, y, 118, y], fill=DBLUE, width=6)


def i_sheets(d):
    d.rectangle([14, 14, 100, 114], fill=WHITE, outline=DBLUE, width=6)
    d.rectangle([24, 24, 90, 80], outline=GRAY, width=4)
    d.rectangle([60, 88, 92, 106], fill=LBLUE)
    d.line([108, 8, 108, 48], fill=GREEN, width=10)
    d.line([88, 28, 128, 28], fill=GREEN, width=10)


def i_copy(d):
    d.ellipse([10, 30, 70, 90], fill=LBLUE, outline=BLUE, width=6)
    d.ellipse([56, 40, 116, 100], fill=YELLOW, outline=ORANGE, width=6)


def i_about(d):
    d.rounded_rectangle([12, 12, 116, 116], 14, fill=WHITE, outline=DBLUE, width=8)
    text(d, (64, 66), "?", 70, BLUE)


def i_bug(d):
    d.ellipse([34, 30, 94, 110], fill=RED, outline=(140, 20, 20), width=5)
    d.ellipse([46, 10, 82, 40], fill=(140, 20, 20))
    for y in (52, 72, 92):
        d.line([14, y, 34, y], fill=BLACK, width=5)
        d.line([94, y, 114, y], fill=BLACK, width=5)


def i_inspect(d):
    table(d, 8, 10, 90, 90, 4, 2, LBLUE)
    magnifier(d, 80, 76, 20)


ICONS = {
    "settings": i_settings, "tags": i_tags, "tagdim": i_tagdim, "taglevel": i_taglevel,
    "links": i_links, "levels": i_levels, "importloads": i_importloads,
    "lamp": i_lamp, "outlet": i_outlet, "switch": i_switch, "cota": i_cota,
    "conduit": i_conduit, "tray": i_tray, "detour": i_detour, "supports": i_supports,
    "central": i_central, "adjusttags": i_adjusttags, "visibility": i_visibility, "tagsview": i_tagsview,
    "route": i_route, "legend": i_legend, "legendview": i_legendview, "legendreset": i_legendreset,
    "legendauto": i_legendauto, "sizeinfra": i_sizeinfra, "center": i_center, "centerpanel": i_centerpanel,
    "dimension": i_dimension, "dimproject": i_dimproject, "grouping": i_grouping, "update": i_update,
    "create": i_create, "orderdev": i_order_dev, "orderpanel": i_order_panel, "orderspace": i_order_space,
    "props": i_props, "identify": i_identify, "balance": i_balance, "returns": i_returns, "path": i_path,
    "reports": i_reports, "reportdim": i_reportdim, "schedule": i_schedule, "memorial": i_memorial,
    "diagram": i_diagram, "sheets": i_sheets, "copy": i_copy, "about": i_about, "bug": i_bug,
    "inspect": i_inspect,
}


def main():
    os.makedirs(OUT, exist_ok=True)
    for name, fn in ICONS.items():
        img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        fn(ImageDraw.Draw(img))
        for size in (32, 16):
            img.resize((size, size), Image.LANCZOS).save(os.path.join(OUT, f"{name}{size}.png"))
    print(f"{len(ICONS)} ícones gerados em {os.path.abspath(OUT)}")


if __name__ == "__main__":
    main()
