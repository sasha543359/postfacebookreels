# -*- coding: utf-8 -*-
"""
Рисует скриншоты do_08_nano_before.png и do_09_nano_after.png для шага 21
ИНСТРУКЦИЯ.pdf прямо из appsettings.json — начало блока первой Страницы, как
его показывает nano в веб-консоли DigitalOcean (подсветка JSON из nano,
палитра Tango, Courier New).

Зачем рисовать, а не снимать: снимки должны совпадать с шаблоном
символ в символ. Настоящий скриншот устаревает при каждой правке подсказок в
файле, и в PDF оставалась бы старая подсказка, которая противоречит новой.

Запуск (из корня репозитория), затем пересборка PDF:
    python docs/instruction/make_nano_shots.py
    python docs/instruction/make_pdf.py
"""
import os
import re

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SHOTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "shots")

# Размеры клетки сняты со старого настоящего скриншота консоли.
FONT = ImageFont.truetype("C:/Windows/Fonts/cour.ttf", 15.25)
CELL_W, CELL_H, TOP = 9.15, 17, 5
WIDTH = 743

BG = (0, 0, 0)
NAME = (114, 159, 207)      # brightblue: "ключ":
STRING = (173, 127, 168)    # brightmagenta: "строка"
PUNCT = (239, 41, 41)       # brightred: { } , :
BRACKET = (114, 159, 207)   # brightblue: [ ]
COMMENT = (6, 152, 154)     # cyan: // …
PLAIN = (211, 215, 207)
CURSOR = (240, 240, 240)


def colors(line):
    """Цвет каждого символа — в том же порядке правил, что в json.nanorc:
    более позднее правило перекрашивает раннее."""
    col = [PLAIN] * len(line)

    def paint(start, end, color):
        for i in range(start, end):
            col[i] = color

    for m in re.finditer(r'".+"', line):
        paint(m.start(), m.end(), STRING)
    for m in re.finditer(r'"[^"]+"\s*:', line):
        paint(m.start(), m.end(), NAME)
    for m in re.finditer(r"[\[\]]", line):
        paint(m.start(), m.end(), BRACKET)
    for m in re.finditer(r"[{},:]", line):
        paint(m.start(), m.end(), PUNCT)
    m = re.search(r"(^|\s+)(//|#).*$", line)
    if m:
        paint(m.start(), m.end(), COMMENT)
    return col


def render(lines, cursor, path):
    img = Image.new("RGB", (WIDTH, TOP * 2 + CELL_H * len(lines)), BG)
    draw = ImageDraw.Draw(img)
    for row, line in enumerate(lines):
        y = TOP + row * CELL_H
        for i, (ch, color) in enumerate(zip(line, colors(line))):
            x = round(i * CELL_W)
            if (row, i) == cursor:
                draw.rectangle((x, y, round((i + 1) * CELL_W) - 1, y + CELL_H - 1), fill=CURSOR)
                color = BG
            if ch != " ":
                draw.text((x, y + 1), ch, font=FONT, fill=color)
        if cursor[0] == row and cursor[1] >= len(line):
            x = round(cursor[1] * CELL_W)
            draw.rectangle((x, y, round((cursor[1] + 1) * CELL_W) - 1, y + CELL_H - 1), fill=CURSOR)
    img.save(path)
    print("сохранено:", os.path.relpath(path, ROOT), img.size)


def main():
    text = open(os.path.join(ROOT, "appsettings.json"), encoding="utf-8-sig").read().splitlines()
    first = next(i for i, l in enumerate(text) if '"FacebookPages"' in l)
    last = next(i for i in range(first, len(text)) if '"PageAccessToken"' in text[i])
    lines = text[first:last + 1]

    name_row = next(i for i, l in enumerate(lines) if '"PageName"' in l)
    render(lines, (name_row, 0), os.path.join(SHOTS, "do_08_nano_before.png"))

    # «После»: вместо заглушки — число, курсор стоит на закрывающей кавычке,
    # как после стирания заглушки и вставки ID.
    id_row = next(i for i, l in enumerate(lines) if '"PageId"' in l)
    after = list(lines)
    after[id_row] = after[id_row].replace("ВСТАВЬ_PAGE_ID", "1234567890123456")
    render(after, (id_row, after[id_row].rindex('"')), os.path.join(SHOTS, "do_09_nano_after.png"))


if __name__ == "__main__":
    main()
