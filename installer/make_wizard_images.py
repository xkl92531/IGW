# 由 assets/igw.ico 生成安装向导用的图片：
#   wizard-small-*.png  页眉右上角的小图标(透明底)，55x55 为 100% 缩放
#   wizard-large-*.bmp  完成页左侧竖图，164x314 为 100% 缩放
#   wizard-back-*.png   整个窗口的背景：白到浅绿的渐变，右下角是设备连线图案和 IGW 水印
# 多个缩放比例都生成，Inno 按屏幕 DPI 挑最合适的一张。改了图标后运行：python installer/make_wizard_images.py
import random
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

here = Path(__file__).resolve().parent
ico = Image.open(here.parent / 'assets' / 'igw.ico')
ico.size = max(ico.info['sizes'])
logo = ico.convert('RGBA')
green = (50, 205, 50)
scales = [100, 150, 200]
# 背景图区域在 100% / 150% / 200% 缩放下的大小(见 Inno 帮助 WizardBackImageFile)
back_sizes = [(596, 432), (994, 720), (1272, 922)]


def font(size):
    for name in ('msyhbd.ttc', 'msyh.ttc', 'simhei.ttf'):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def paste_center(canvas, image, cx, cy):
    canvas.paste(image, (cx - image.width // 2, cy - image.height // 2), image)


def lerp(a, b, k):
    return tuple(round(x + (y - x) * k) for x, y in zip(a, b))


def background(w, h):
    top, bottom = (255, 255, 255), (222, 242, 226)
    img = Image.new('RGB', (w, h))
    draw = ImageDraw.Draw(img)
    # 左上 → 右下的斜向渐变：按对角线方向画细线
    for i in range(w + h):
        k = min(1.0, i / (w + h) * 1.25)
        draw.line([(i, 0), (i - h, h)], fill=lerp(top, bottom, k), width=2)
    layer = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    s = w / 1000
    # 右下角 IGW 水印：圆环 + 字，和深色款一样看得出轮廓
    size = round(h * 0.55)
    mark = logo.resize((size, size), Image.LANCZOS)
    mark.putalpha(mark.getchannel('A').point(lambda v: v * 0.16))
    layer.alpha_composite(mark, (round(w - size * 0.72), round(h - size * 0.78)))
    ring = round(size * 0.035)
    x0, y0 = round(w - size * 0.72) - ring * 2, round(h - size * 0.78) - ring * 2
    d.ellipse([x0, y0, x0 + size + ring * 4, y0 + size + ring * 4], outline=(40, 170, 70, 60), width=ring)
    # 设备连线：一片节点，每个连到最近的两个
    rnd = random.Random(7)
    nodes = [(w * (0.55 + 0.45 * rnd.random()), h * (0.45 + 0.55 * rnd.random())) for _ in range(26)]
    for a in nodes:
        for b in sorted(nodes, key=lambda p: (a[0] - p[0]) ** 2 + (a[1] - p[1]) ** 2)[1:3]:
            d.line([a, b], fill=(40, 160, 70, 120), width=max(1, round(1.6 * s)))
    for x, y in nodes:
        r = 4 * s
        d.ellipse([x - r, y - r, x + r, y + r], fill=(40, 170, 70, 200))
    return Image.alpha_composite(img.convert('RGBA'), layer).convert('RGB')


for scale in scales:
    # 小图：透明底图标，四周留一点边
    side = round(55 * scale / 100)
    small = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    icon = logo.resize((round(side * 0.9),) * 2, Image.LANCZOS)
    small.alpha_composite(icon, ((side - icon.width) // 2, (side - icon.height) // 2))
    small.save(here / f'wizard-small-{scale}.png')

    # 竖图：上浅下深的绿色渐变，白色圆底图标 + 产品名
    w, h = round(164 * scale / 100), round(314 * scale / 100)
    large = Image.new('RGB', (w, h))
    draw = ImageDraw.Draw(large)
    for y in range(h):
        k = y / h
        draw.line([(0, y), (w, y)], fill=tuple(round(c * (1 - 0.35 * k)) for c in green))
    d = round(w * 0.62)
    ring = Image.new('RGBA', (d + 8, d + 8), (0, 0, 0, 0))
    ImageDraw.Draw(ring).ellipse([0, 0, d + 7, d + 7], fill='white')
    paste_center(large, ring, w // 2, round(h * 0.3))
    paste_center(large, logo.resize((d, d), Image.LANCZOS), w // 2, round(h * 0.3))
    for text, size, y in (('IGW', round(w * 0.16), 0.55), ('工业网关', round(w * 0.12), 0.64)):
        f = font(size)
        box = draw.textbbox((0, 0), text, font=f)
        draw.text(((w - (box[2] - box[0])) // 2 - box[0], round(h * y)), text, font=f, fill='white')
    large.save(here / f'wizard-large-{scale}.bmp')

for w, h in back_sizes:
    background(w, h).save(here / f'wizard-back-{w}.png')

print('ok')
