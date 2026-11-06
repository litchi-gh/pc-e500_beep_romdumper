"""Generate the SVG figures of the documents, in Japanese (name.svg) and English (name_en.svg).

  py build_figures.py

Plots use data.json, measured from real recordings by extract_data.py. No other dependencies.
"""
import json
import math
from pathlib import Path
from xml.sax.saxutils import escape

HERE = Path(__file__).resolve().parent
DATA = json.loads((HERE / 'data.json').read_text(encoding='utf-8'))

FONT = 'system-ui, -apple-system, &quot;Segoe UI&quot;, &quot;Yu Gothic UI&quot;, &quot;Hiragino Sans&quot;, &quot;Noto Sans CJK JP&quot;, sans-serif'
MONO = 'ui-monospace, Consolas, Menlo, monospace'
SURFACE, INK, SECONDARY, MUTED, GRID, AXIS = '#fcfcfb', '#0b0b0b', '#52514e', '#898781', '#e1e0d9', '#c3c2b7'
BLUE, ORANGE, AQUA = '#2a78d6', '#eb6834', '#1baf7a'
BLUE_WASH, ORANGE_WASH, GRAY_WASH = '#e4effc', '#fde9e0', '#f0efec'
GOOD, WARNING, CRITICAL, NONE = '#0ca30c', '#fab219', '#d03b3b', '#dddcd5'

lang = 'ja'


def L(japanese, english):
    return japanese if lang == 'ja' else english


class Figure:
    def __init__(self, name, width, height, title):
        self.name, self.width, self.height, self.title = name, width, height, title
        self.parts = [f'<rect width="{width}" height="{height}" rx="8" fill="{SURFACE}"/>']

    def raw(self, text):
        self.parts.append(text)

    def rect(self, x, y, w, h, fill, r=0, stroke=None, opacity=None):
        extra = f' stroke="{stroke}"' if stroke else ''
        extra += f' fill-opacity="{opacity}"' if opacity is not None else ''
        self.raw(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{r}" fill="{fill}"{extra}/>')

    def text(self, x, y, value, size=13, fill=INK, anchor='start', weight=None, mono=False):
        family = f' font-family="{MONO}"' if mono else ''
        bold = f' font-weight="{weight}"' if weight else ''
        self.raw(f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{fill}" text-anchor="{anchor}"{family}{bold}>{escape(str(value))}</text>')

    def line(self, x1, y1, x2, y2, stroke=AXIS, width=1):
        self.raw(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{width}"/>')

    def arrow(self, x1, y1, x2, y2, stroke=SECONDARY):
        self.raw(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="1.5" marker-end="url(#arrow)"/>')

    def path(self, d, stroke=SECONDARY, width=1.5, fill='none', arrow=False):
        end = ' marker-end="url(#arrow)"' if arrow else ''
        self.raw(f'<path d="{d}" fill="{fill}" stroke="{stroke}" stroke-width="{width}" stroke-linejoin="round" stroke-linecap="round"{end}/>')

    def box(self, x, y, w, h, lines, fill=GRAY_WASH, size=13, stroke=None, ink=INK):
        self.rect(x, y, w, h, fill, r=6, stroke=stroke)
        lines = [lines] if isinstance(lines, str) else lines
        pitch = size + 5
        top = y + h / 2 - pitch * (len(lines) - 1) / 2 + size * 0.36
        for i, value in enumerate(lines):
            self.text(x + w / 2, top + i * pitch, value, size=size if i == 0 else size - 1, anchor='middle', fill=ink if i == 0 else SECONDARY if ink == INK else ink)

    def save(self):
        head = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {self.width} {self.height}" width="{self.width}" height="{self.height}" '
                f'font-family="{FONT}" role="img"><title>{escape(self.title)}</title>'
                f'<defs><marker id="arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto-start-reverse">'
                f'<path d="M0 0L8 4L0 8z" fill="{SECONDARY}"/></marker></defs>')
        name = f'{self.name}.svg' if lang == 'ja' else f'{self.name}_en.svg'
        (HERE / name).write_text(head + ''.join(self.parts) + '</svg>\n', encoding='utf-8', newline='\n')


class Plot:
    """One plot area: hairline grid, solid axes, one y scale."""

    def __init__(self, figure, x, y, w, h, xr, yr, xticks, yticks, xlabel=None, ylabel=None, xfmt=str, yfmt=str):
        self.f, self.x, self.y, self.w, self.h, self.xr, self.yr = figure, x, y, w, h, xr, yr
        for v in yticks:
            figure.line(x, self.Y(v), x + w, self.Y(v), GRID)
            figure.text(x - 7, self.Y(v) + 4, yfmt(v), size=12, fill=MUTED, anchor='end')
        figure.line(x, y + h, x + w, y + h, AXIS)
        for v in xticks:
            figure.line(self.X(v), y + h, self.X(v), y + h + 4, AXIS)
            figure.text(self.X(v), y + h + 18, xfmt(v), size=12, fill=MUTED, anchor='middle')
        if xlabel:
            figure.text(x + w / 2, y + h + 36, xlabel, size=12, fill=SECONDARY, anchor='middle')
        if ylabel:
            figure.text(x, y - 9, ylabel, size=12, fill=SECONDARY)

    def X(self, v):
        return self.x + (v - self.xr[0]) / (self.xr[1] - self.xr[0]) * self.w

    def Y(self, v):
        return self.y + self.h - (v - self.yr[0]) / (self.yr[1] - self.yr[0]) * self.h

    def series(self, xs, ys, color, width=2):
        pts = [(x, y) for x, y in zip(xs, ys) if self.xr[0] <= x <= self.xr[1]]
        d = 'M' + 'L'.join(f'{self.X(x):.1f} {self.Y(max(self.yr[0], min(self.yr[1], y))):.1f}' for x, y in pts)
        self.f.path(d, stroke=color, width=width)

    def dot(self, x, y, color):
        self.f.raw(f'<circle cx="{self.X(x):.1f}" cy="{self.Y(y):.1f}" r="5" fill="{color}" stroke="{SURFACE}" stroke-width="2"/>')


def legend(f, x, y, items):
    for label, color in items:
        f.rect(x, y - 3, 16, 3, color, r=1)
        f.text(x + 22, y + 2, label, size=12, fill=SECONDARY)
        x += 30 + len(label) * (13 if lang == 'ja' else 7)


# ---------------------------------------------------------------- README

def pocket_computer(f, cx, y, lines):
    """ポケコン。本体は濃い色にして、白い背景でも見えるようにする。"""
    x = cx - 66
    f.rect(x, y, 132, 92, '#3b3a37', r=6)
    f.rect(x + 9, y + 9, 114, 32, '#c9d6c3', r=2)
    for i, line in enumerate(lines):
        f.text(x + 14, y + 22 + i * 13, line, size=11, mono=True)
    for row in range(3):
        for col in range(8):
            f.rect(x + 10 + col * 14, y + 49 + row * 13, 11, 9, '#a9a79f', r=1)


def listing_sheet(f, cx, y):
    """打ち込むリストを書いた紙。"""
    x, w, h, fold = cx - 48, 96, 108, 16
    f.path(f'M{x} {y + 3} a3 3 0 0 1 3 -3 H{x + w - fold} L{x + w} {y + fold} V{y + h - 3} a3 3 0 0 1 -3 3 H{x + 3} a3 3 0 0 1 -3 -3 Z', stroke='#3b3a37', width=2, fill='#ffffff')
    f.path(f'M{x + w - fold} {y} V{y + fold} H{x + w}', stroke='#3b3a37', width=2)
    for i, line in enumerate(('10 INPUT A,E', '20 READ D$,C', '30 S=0:FOR I', '40 IF S=C G', '100 DATA "2F', '110 DATA "5A')):
        f.text(x + 8, y + 28 + i * 14, line, size=10, mono=True)


def overview():
    f = Figure('overview', 860, 222, L('全体の流れ', 'Overview'))
    y = 26
    centers = (86, 250, 424, 588, 770)
    captions = [
        (L('1. 命令を打ち込む', '1. Type it in'), L('CSAVE M なら 1 行', 'one line with CSAVE M')),
        (L('2. ROM を鳴らす', '2. Play the ROM'), L('ブザーの音で流れる', 'on the buzzer')),
        (L('3. スマホで録音', '3. Record with a phone'), L('録画でもよい', 'video is fine too')),
        (L('4. BeepDump で読む', '4. Read with BeepDump'), L('音をデータに戻す', 'sound back to data')),
        (L('5. ROM ファイル', '5. ROM file'), L('エミュレータなどで使う', 'for an emulator and so on')),
    ]
    # 1. 打ち込み
    listing_sheet(f, centers[0], y)
    f.arrow(centers[0] + 54, y + 54, centers[1] - 72, y + 54)
    # 2. RUN
    pocket_computer(f, centers[1], y + 8, ['RUN', '>'])
    for r in (9, 16, 23):
        f.path(f'M{centers[1] + 70 + r * 0.5:.0f} {y + 54 - r} A{r} {r} 0 0 1 {centers[1] + 70 + r * 0.5:.0f} {y + 54 + r}', stroke=BLUE, width=2)
    f.arrow(centers[1] + 100, y + 54, centers[2] - 38, y + 54)
    # 3. スマホ
    px = centers[2] - 30
    f.rect(px, y, 60, 108, '#1f1f1d', r=9)
    f.rect(px + 5, y + 8, 50, 88, BLUE_WASH, r=3)
    for i, height in enumerate((10, 22, 34, 18, 28, 12, 24, 8)):
        f.rect(px + 9 + i * 5.3, y + 44 - height / 2, 3.2, height, BLUE, r=1)
    f.raw(f'<circle cx="{centers[2]}" cy="{y + 76}" r="10" fill="{CRITICAL}" stroke="#ffffff" stroke-width="2"/>')
    f.arrow(centers[2] + 36, y + 54, centers[3] - 70, y + 54)
    f.text((centers[2] + 36 + centers[3] - 70) / 2, y + 44, 'MOV', size=12, fill=SECONDARY, anchor='middle', mono=True)
    # 4. PC
    px = centers[3] - 62
    f.rect(px, y + 4, 124, 82, '#3b3a37', r=5)
    f.rect(px + 7, y + 11, 110, 68, SURFACE, r=2)
    colors = {(5, 1): WARNING, (2, 3): WARNING, (7, 2): CRITICAL}
    for row in range(5):
        for col in range(9):
            f.rect(px + 12 + col * 11.2, y + 16 + row * 12, 9.2, 10, colors.get((col, row), GOOD), r=1)
    f.rect(centers[3] - 8, y + 86, 16, 12, '#3b3a37')
    f.rect(centers[3] - 28, y + 98, 56, 6, '#3b3a37', r=2)
    f.arrow(centers[3] + 66, y + 54, centers[4] - 66, y + 54)
    # 5. ROM
    f.rect(centers[4] - 56, y + 24, 112, 60, INK, r=4)
    f.text(centers[4], y + 60, 'ROM', size=17, fill=SURFACE, anchor='middle', weight=600)
    for i in range(7):
        f.rect(centers[4] - 50 + i * 15, y + 17, 8, 7, MUTED)
        f.rect(centers[4] - 50 + i * 15, y + 84, 8, 7, MUTED)
    for center, (title, sub) in zip(centers, captions):
        f.text(center, y + 134, title, anchor='middle', weight=600)
        f.text(center, y + 154, sub, size=12, fill=SECONDARY, anchor='middle')
    f.save()


def recording_steps():
    f = Figure('recording_steps', 860, 158, L('録音を始めるタイミング', 'When to start recording'))
    y = 30
    steps = [
        (14, 96, ['RUN'], GRAY_WASH),
        (120, 130, [L('1 つ目の ?', '1st ?'), '&HC0000'], GRAY_WASH),
        (260, 130, [L('2 つ目の ?', '2nd ?'), '&HFFFFF'], GRAY_WASH),
        (400, 110, [L('録音開始', 'Start recording')], '#f7d6d6'),
        (520, 80, ['ENTER'], GRAY_WASH),
        (610, 130, [L('音が鳴る', 'Sound plays'), L('キーに触らない', 'Do not touch keys')], BLUE_WASH),
        (750, 96, [L('> が出たら', 'At >,'), L('録音停止', 'stop recording')], '#f7d6d6'),
    ]
    for i, (x, w, lines, fill) in enumerate(steps):
        f.box(x, y, w, 58, lines, fill=fill)
        if i:
            px, pw = steps[i - 1][0], steps[i - 1][1]
            f.arrow(px + pw + 1, y + 29, x - 1, y + 29)
    f.line(14, y + 86, 846, y + 86, AXIS)
    f.path(f'M455 {y + 58} L455 {y + 76}', stroke=CRITICAL, arrow=True)
    f.text(455, y + 106, L('2 つ目の ENTER を押す前に録音を始める', 'Start recording before the second ENTER'), anchor='middle', weight=600)
    f.save()


def time_compare():
    f = Figure('time_compare', 860, 152, L('本体 ROM 256KB にかかる時間', 'Time for the 256KB main ROM'))
    f.text(16, 24, L('本体 ROM 256KB を 1 回録音する時間（分）', 'Time to record the 256KB main ROM once (minutes)'), weight=600)
    left, full = 150, 620
    rows = [(L('機械語版', 'Machine code'), 15.3, L('15.3 分（実測）', '15.3 min (measured)')),
            ('CSAVE M', 22.2, L('約 22 分（実測。確かめるには 2 回録音）', 'About 22 min (measured; record twice to confirm)')),
            (L('BASIC 版', 'BASIC'), 114, L('約 1.9 時間（途中までの実測からの推定）', 'About 1.9 hours (estimated from a partial run)'))]
    for i, (name, value, label) in enumerate(rows):
        y = 44 + i * 34
        f.text(left - 10, y + 16, name, size=13, anchor='end', fill=SECONDARY)
        f.path(f'M{left} {y} H{left + value / 114 * full * 0.62 - 4:.1f} a4 4 0 0 1 4 4 V{y + 18} a4 4 0 0 1 -4 4 H{left} Z', stroke='none', fill=BLUE, width=0)
        f.text(left + value / 114 * full * 0.62 + 8, y + 16, label, size=13)
    f.save()


def retake():
    f = Figure('retake', 860, 196, L('録り直しで補い合う', 'Retakes fill each other\'s gaps'))
    n, cell = 44, 13
    left = 190
    failed = {5, 6, 17, 30, 31, 32, 41}
    repaired = {11, 24, 37}
    rows = [
        (L('1 本目', '1st recording'), lambda i: CRITICAL if i in failed else WARNING if i in repaired else GOOD),
        (L('2 本目（赤の所を録音）', '2nd (only the rest)'), lambda i: GOOD if i in failed else NONE),
        (L('合わせた結果', 'Combined'), lambda i: WARNING if i in repaired else GOOD),
    ]
    for r, (name, color) in enumerate(rows):
        y = 22 + r * 40
        f.text(left - 12, y + 19, name, anchor='end', size=13, fill=SECONDARY)
        for i in range(n):
            f.rect(left + i * (cell + 1), y, cell, 26, color(i), r=2)
    x = left
    for label, color in STATES():
        f.rect(x, 152, 13, 13, color, r=2)
        f.text(x + 19, 163, label, size=12, fill=SECONDARY)
        x += 40 + len(label) * (13 if lang == 'ja' else 7)
    f.text(left, 186, L('色だけでなく、足りない範囲の番地も画面に表示します。', 'The window also lists the missing ranges by address.'), size=12, fill=SECONDARY)
    f.save()


def STATES():
    return ((L('読めた', 'Read'), GOOD), (L('直して読めた', 'Repaired'), WARNING), (L('読めなかった', 'Not read'), CRITICAL), (L('録音していない', 'Not recorded'), NONE))


def result_grid():
    states = DATA['states']
    counts = {c: states.count(c) for c in 'GRF-'}
    f = Figure('result_grid', 860, 214, L('実際の読み取り結果', 'A real result'))
    f.text(16, 24, L('PC-U6000 の本体 ROM（1 マスが 256 バイト、解析パターンは「標準」だけ）', 'PC-U6000 main ROM (one cell is 256 bytes, standard analysis pattern only)'), weight=600)
    colors = {'G': GOOD, 'R': WARNING, 'F': CRITICAL, '-': NONE}
    columns, cell = 64, 9
    x0, y0 = 16, 38
    for i, c in enumerate(states):
        f.rect(x0 + (i % columns) * (cell + 1), y0 + (i // columns) * (cell + 1), cell, cell, colors[c], r=1)
    lx = x0 + columns * (cell + 1) + 22
    rows = [(L('読めた', 'Read'), GOOD, counts['G']), (L('直して読めた', 'Repaired'), WARNING, counts['R']), (L('読めなかった', 'Not read'), CRITICAL, counts['F'])]
    for r, (label, color, count) in enumerate(rows):
        y = y0 + 8 + r * 34
        f.rect(lx, y, 14, 14, color, r=2)
        f.text(lx + 22, y + 12, label, size=13)
        f.text(844, y + 12, count, size=13, anchor='end', weight=600)
    f.text(lx, y0 + 124, L('赤はほかのパターンで', 'Other patterns read'), size=12, fill=SECONDARY)
    f.text(lx, y0 + 142, L('読めて、この録音は', 'the red cells, so this'), size=12, fill=SECONDARY)
    f.text(lx, y0 + 160, L('全部そろいました。', 'recording read in full.'), size=12, fill=SECONDARY)
    f.save()


def repair():
    f = Figure('repair', 860, 356, L('ビットの修復', 'Repairing bits'))
    x0, cell = 150, 40
    wrong = 5
    first = [1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 1, 0, 0]
    fixed = [1, 0, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0]
    confidence = [.8, .9, .7, .85, .6, .08, .3, .75, .9, .55, .8, .22, .7, .85]
    def bits(y, values, marks, title):
        f.text(16, y + 22, title, size=13, fill=SECONDARY)
        for i, v in enumerate(values):
            fill = GRAY_WASH
            if i in marks:
                fill = marks[i]
            f.box(x0 + i * cell, y, cell - 3, 32, str(v), fill=fill)
    # 1 回目
    f.text(16, 24, L('1. 1 回目の読み: 検査が通らない', '1. First reading: the check fails'), weight=600)
    bits(36, first, {5: '#f7d6d6', 6: '#f7d6d6'}, L('読んだビット', 'Bits read'))
    f.box(x0 + 14 * cell + 6, 36, 104, 32, L('検査 ×', 'Check: fail'), fill='#f7d6d6', size=12)
    # 自信
    f.text(16, 96, L('2. 自信の低いビットを探す', '2. Find the bits with low confidence'), weight=600)
    y = 108
    h = 44
    f.text(16, y + 28, L('0 と 1 の差', 'Gap, 0 vs 1'), size=13, fill=SECONDARY)
    f.line(x0, y + h, x0 + 14 * cell - 3, y + h, AXIS)
    for i, c in enumerate(confidence):
        bar = c * h
        f.path(f'M{x0 + i * cell + 9} {y + h} V{y + h - bar + 3:.1f} a3 3 0 0 1 3 -3 h13 a3 3 0 0 1 3 3 V{y + h} Z', stroke='none', fill=ORANGE if i == wrong else BLUE, width=0)
    f.text(x0 + wrong * cell + 18, y - 2, L('いちばん低い', 'Lowest'), size=12, anchor='middle')
    # 反転
    f.text(16, 182, L('3. そのビットを反転して、そこから先を読み直す', '3. Flip that bit and read on from there'), weight=600)
    bits(194, fixed, {5: ORANGE_WASH, 6: '#d7f0d7'}, L('読み直し', 'Read again'))
    f.box(x0 + 14 * cell + 6, 194, 104, 32, L('検査 ○', 'Check: pass'), fill='#d7f0d7', size=12)
    f.text(x0 + wrong * cell + 30, 244, L('反転', 'flipped'), size=12, anchor='end', fill=SECONDARY)
    f.text(x0 + 6 * cell + 4, 244, L('連れて直る', 'fixed too'), size=12, fill=SECONDARY)
    # 結論
    f.text(16, 276, L('4. 検査が通り、番地が前後と合えば採用', '4. Accept if the check passes and the address fits'), weight=600)
    f.rect(16, 290, 16, 16, WARNING, r=2)
    f.text(40, 303, L('画面では黄色のマス（読めたものとして ROM に入る）', 'A yellow cell in the window (it goes into the ROM as read)'), size=13)
    f.text(16, 336, L('説明用の例。だめなら次に低いビットへ進み、上位 48 個を 1 つずつ、上位 10 個を 2 つ組で試す。全部だめなら赤のまま。', 'An illustration. If it fails, the next lowest is tried: the 48 lowest one at a time, then pairs from the 10 lowest. If all fail, the cell stays red.'), size=12, fill=SECONDARY)
    f.save()


def rom_map():
    f = Figure('rom_map', 860, 238, L('ROM の番地と保存するファイル', 'ROM addresses and the saved file'))
    x, w = 40, 780
    def at(a):
        return x + a / 0x100000 * w
    f.text(x, 24, L('ポケコンのメモリの番地', 'Pocket computer memory addresses'), weight=600)
    y = 36
    f.rect(x, y, w, 30, GRAY_WASH, r=3)
    f.box(at(0x20000), y, at(0x40000) - at(0x20000), 30, L('拡張 ROM', 'Extension ROM'), fill=ORANGE_WASH, stroke=ORANGE, size=12)
    f.box(at(0xC0000), y, at(0x100000) - at(0xC0000), 30, L('本体 ROM', 'Main ROM'), fill=BLUE_WASH, stroke=BLUE, size=12)
    for a, label, anchor in ((0x20000, '20000', 'middle'), (0x3FFFF, '3FFFF', 'middle'), (0xC0000, 'C0000', 'middle'), (0xFFFFF, 'FFFFF', 'end')):
        f.text(min(at(a), x + w), y + 48, label, size=12, fill=SECONDARY, anchor=anchor, mono=True)
    f.text(at(0x70000), y + 20, L('RAM など', 'RAM and others'), size=12, fill=MUTED, anchor='middle')
    f.text(x, 118, L('保存する ROM ファイル', 'Saved ROM file'), weight=600)
    rows = [(L('拡張 ROM のない機種', 'No extension ROM'), [(0x40000, BLUE_WASH, BLUE, L('本体 256KB', 'Main 256KB'))]),
            (L('PC-E650 / U6000', 'PC-E650 / U6000'), [(0x20000, ORANGE_WASH, ORANGE, L('拡張 128KB', 'Extension 128KB')), (0x40000, BLUE_WASH, BLUE, L('本体 256KB', 'Main 256KB'))])]
    for r, (name, parts) in enumerate(rows):
        yy = 132 + r * 44
        f.text(x, yy + 22, name, size=13, fill=SECONDARY)
        px = x + 150
        for size, fill, stroke, label in parts:
            pw = size / 0x60000 * 600
            f.box(px, yy, pw - 2, 32, label, fill=fill, stroke=stroke, size=12)
            px += pw
    f.save()


# ---------------------------------------------------------------- TECHNICAL

def fast_stream():
    f = Figure('fast_stream', 860, 232, L('機械語版の音の並び', 'Machine-code sound layout'))
    y = 34
    f.text(16, 20, L('録音全体', 'A whole recording'), weight=600)
    x = 16
    f.box(x, y, 110, 40, [L('学習用の並び', 'Learning'), L('約 0.44 秒', 'about 0.44 s')], fill=ORANGE_WASH, size=12)
    x += 110
    for i, label in enumerate((L('ブロック', 'Block'), L('ブロック', 'Block'), L('ブロック', 'Block'))):
        x += 12
        f.box(x, y, 170, 40, [label, L('約 0.89 秒', 'about 0.89 s')] if i == 0 else [label], fill=BLUE_WASH, size=12)
        zoom = x
        x += 170
    f.text(x + 16, y + 25, '…', size=18, fill=SECONDARY)
    f.line(126, y + 52, 138, y + 52, SECONDARY)
    f.text(132, y + 68, L('無音 約 10 ms', 'Silence, about 10 ms'), size=12, fill=SECONDARY, anchor='start')
    # ズーム
    bx = 138
    yz = 136
    f.path(f'M{bx} {y + 40} L16 {yz - 2}', stroke=AXIS, width=1)
    f.path(f'M{bx + 170} {y + 40} L844 {yz - 2}', stroke=AXIS, width=1)
    f.text(16, yz - 8, L('1 ブロック（265 バイト）', 'One block (265 bytes)'), weight=600)
    parts = [(44, '$00', L('同期', 'sync'), GRAY_WASH), (44, '$00', '', GRAY_WASH), (44, '$A5', '', GRAY_WASH),
             (470, L('データ 256 バイト', '256 data bytes'), L('下位ビットから', 'LSB first'), BLUE_WASH),
             (110, 'S1', L('番地 + データの和（3 バイト）', 'address + sum (3 bytes)')[:0], ORANGE_WASH), (110, 'S2', '', ORANGE_WASH)]
    x = 16
    for w, top, sub, fill in parts:
        f.box(x, yz, w - 2, 44, [top, sub] if sub else [top], fill=fill, size=12)
        x += w
    f.text(16, yz + 66, L('先頭 3 バイトは毎回同じ。位置合わせに使う', 'The first 3 bytes never change and are used for alignment'), size=12, fill=SECONDARY)
    f.text(844, yz + 66, L('S1 = 番地 + データの和　S2 = S1 の累積和', 'S1 = address + data sum, S2 = running sum of S1'), size=12, fill=SECONDARY, anchor='end')
    f.save()


def biphase():
    f = Figure('biphase', 860, 236, L('バイフェーズの符号と時間', 'Biphase code and timing'))
    bits = [1, 0, 1, 1, 0]
    x0, y_hi, y_lo = 60, 66, 116
    t, m, e = 124, 58, 10
    level = False
    path = [f'M{x0 - 30} {y_lo}']
    x = x0
    cells = []
    ticks = []
    def toggle(at):
        nonlocal level
        y_from = y_hi if level else y_lo
        level = not level
        y_to = y_hi if level else y_lo
        path.append(f'L{at} {y_from}L{at} {y_to}')
        ticks.append(at)
    for bit in bits:
        cells.append((x, bit))
        toggle(x)
        if bit:
            toggle(x + m)
        x += t + bit * e
    # バイトの切れ目
    gap = 34
    path.append(f'L{x + gap} {y_hi if level else y_lo}')
    toggle(x + gap)
    path.append(f'L{x + gap + 40} {y_hi if level else y_lo}')
    for (cx, bit), nx in zip(cells, [c[0] for c in cells[1:]] + [x]):
        f.rect(cx, 30, nx - cx - 2, 104, GRAY_WASH if bit == 0 else BLUE_WASH, r=3)
        f.text((cx + nx) / 2, 50, bit, size=15, anchor='middle', weight=600, mono=True)
    f.path(''.join(path), stroke=INK, width=2)
    f.text(x + gap / 2, 150, 'G', size=13, anchor='middle', weight=600)
    f.text(x + gap / 2, 165, L('バイトの切れ目', 'byte gap'), size=12, anchor='middle', fill=SECONDARY)
    # 寸法
    c0 = cells[0][0]
    f.arrow(c0, 150, c0 + m, 150)
    f.text(c0 + m / 2, 168, 'M', size=13, anchor='middle', weight=600)
    c1 = cells[1][0]
    f.arrow(c1, 150, c1 + t, 150)
    f.text(c1 + t / 2, 168, L('T（0 のビット）', 'T (0 bit)'), size=13, anchor='middle', weight=600)
    c2 = cells[2][0]
    f.arrow(c2, 150, c2 + t + e, 150)
    f.text(c2 + (t + e) / 2, 168, L('T + E（1 のビット）', 'T + E (1 bit)'), size=13, anchor='middle', weight=600)
    f.text(16, 196, L('各ビットの頭で必ず切り替わり、1 のビットは中央でも切り替わる。', 'The output always toggles at the head of a bit, and in the middle if the bit is 1.'), size=13, fill=SECONDARY)
    f.text(16, 216, L('T は 310 サイクル、E は 4 サイクル、G は 36 サイクル（待ち 130、実機で約 2400 bps）', 'T is 310 cycles, E is 4, G is 36 (delay 130, about 2400 bps on real hardware)'), size=13, fill=SECONDARY)
    f.save()


def basic_frame():
    f = Figure('basic_frame', 860, 244, L('BASIC 版の音の並び', 'BASIC sound layout'))
    y = 34
    f.text(16, 22, L('1 ブロック（259 音）', 'One block (259 tones)'), weight=600)
    parts = [(120, [L('番地 ÷ 5000', 'Address ÷ 5000')], ORANGE_WASH), (500, [L('データ 256 バイト（256 音）', '256 data bytes (256 tones)')], BLUE_WASH),
             (110, [L('S の下位', 'Low of S')], ORANGE_WASH), (110, ['S ÷ 257'], ORANGE_WASH)]
    x = 16
    for w, lines, fill in parts:
        f.box(x, y, w - 2, 40, lines, fill=fill, size=12)
        x += w
    # 1 音
    f.text(16, 106, L('1 音', 'One tone'), weight=600)
    yy, x0 = 150, 60
    period = 34
    d = [f'M{x0 - 40} {yy}']
    x = x0
    for i in range(8):
        d.append(f'L{x} {yy}L{x} {yy - 24}L{x + period / 2} {yy - 24}L{x + period / 2} {yy + 24}L{x + period} {yy + 24}L{x + period} {yy}')
        x += period
    d.append(f'L{x + 90} {yy}')
    f.path(''.join(d), stroke=INK, width=2)
    f.arrow(x0, yy + 40, x0 + period, yy + 40)
    f.text(x0 + period / 2, yy + 58, L('周期', 'period'), size=12, anchor='middle', fill=SECONDARY)
    f.line(x0 + period, yy + 34, x0 + period, yy + 46, AXIS)
    f.text(x0 + 120, yy + 45, L('周期 = 倍率 × (90 + 4 × 値) ÷ 256000 秒', 'period = scale × (90 + 4 × value) ÷ 256000 s'), size=13)
    f.text(x0 + 120, yy + 66, L('8 周期のあと、BASIC の処理の間は無音', '8 periods, then silence while BASIC runs'), size=13, fill=SECONDARY)
    xn = x + 90
    d2 = f'M{xn} {yy}L{xn} {yy - 24}L{xn + 22} {yy - 24}L{xn + 22} {yy + 24}L{xn + 44} {yy + 24}L{xn + 44} {yy}L{xn + 66} {yy}'
    f.path(d2, stroke=INK, width=2)
    f.text(xn + 70, yy - 12, L('次の音', 'next'), size=12, fill=SECONDARY)
    f.line(x0, yy - 34, x0, yy - 42, AXIS)
    f.line(xn, yy - 34, xn, yy - 42, AXIS)
    f.path(f'M{x0} {yy - 38} L{xn - 1} {yy - 38}', stroke=SECONDARY, arrow=True)
    f.text((x0 + xn) / 2, yy - 44, L('間隔 = 8 周期 + 処理時間', 'interval = 8 periods + processing time'), size=12, anchor='middle', fill=SECONDARY)
    f.save()


def tape_steps():
    f = Figure('tape_steps', 860, 158, L('CSAVE M の録音の流れ', 'Recording with CSAVE M'))
    y = 30
    steps = [
        (14, 104, ['BASIC', L('RUN モード', 'RUN mode')], GRAY_WASH),
        (134, 216, [L('命令を打つ', 'Type the command'), 'CSAVE M "A",&HC0000,&HFFFFF'], GRAY_WASH),
        (366, 96, [L('録音開始', 'Start recording')], '#f7d6d6'),
        (478, 72, ['ENTER'], GRAY_WASH),
        (566, 146, [L('音が鳴る（約 22 分）', 'Sound (about 22 min)'), L('キーに触らない', 'Do not touch keys')], BLUE_WASH),
        (728, 118, [L('> が出たら', 'At >,'), L('録音停止', 'stop recording')], '#f7d6d6'),
    ]
    for i, (x, w, lines, fill) in enumerate(steps):
        f.box(x, y, w, 58, lines, fill=fill, size=12)
        if i:
            px, pw = steps[i - 1][0], steps[i - 1][1]
            f.arrow(px + pw + 1, y + 29, x - 1, y + 29)
    f.line(14, y + 86, 846, y + 86, AXIS)
    f.path(f'M414 {y + 58} L414 {y + 76}', stroke=CRITICAL, arrow=True)
    f.text(430, y + 106, L('ENTER を押す前に録音を始める。読み違いを見つけるため、同じことをもう 1 回録音する', 'Start recording before ENTER. Then record it all once more, so that misreads can be found'), anchor='middle', weight=600)
    f.save()


def tape_stream():
    f = Figure('tape_stream', 860, 196, L('CSAVE M の音の並び', 'CSAVE M sound layout'))
    def section(y, title, parts):
        f.text(16, y - 8, title, weight=600)
        x = 16
        for w, lines, fill in parts:
            f.box(x, y, w - 2, 46, lines, fill=fill, size=12)
            x += w
    section(32, L('ヘッダの区間', 'Header section'), [
        (230, [L('搬送音', 'Carrier'), L('10000 周期、約 3.3 秒', '10000 cycles, about 3.3 s')], GRAY_WASH),
        (82, [L('長', 'Long'), L('40 周期', '40 cycles')], GRAY_WASH), (82, [L('短', 'Short'), L('40 周期', '40 cycles')], GRAY_WASH), (70, [L('長', 'Long'), L('1 周期', '1 cycle')], GRAY_WASH),
        (364, [L('ヘッダ 50 バイト', '50 header bytes'), L('開始番地、長さ、名前', 'start address, length, name')], ORANGE_WASH)])
    section(112, L('本体の区間', 'Data section'), [
        (230, [L('搬送音', 'Carrier'), L('11000 周期、約 3.7 秒', '11000 cycles, about 3.7 s')], GRAY_WASH),
        (82, [L('長', 'Long'), L('20 周期', '20 cycles')], GRAY_WASH), (82, [L('短', 'Short'), L('20 周期', '20 cycles')], GRAY_WASH), (70, [L('長', 'Long'), L('1 周期', '1 cycle')], GRAY_WASH),
        (244, [L('データ', 'Data'), L('開始番地から長さの分', 'from the start address')], BLUE_WASH),
        (120, [L('検査値 2 バイト', '2-byte check'), L('1 のビットの数', 'count of 1 bits')], ORANGE_WASH)])
    f.text(16, 182, L('データの中に番地やブロックごとの検査値はなく、最後の検査値はデータ全体で 1 つだけ', 'The data carries no addresses and no per-block checks; the only check is one value at the very end'), size=12, fill=SECONDARY)
    f.save()


def tape_frame():
    f = Figure('tape_frame', 860, 232, L('CSAVE M の 1 バイト', 'One CSAVE M byte'))
    long_w, short_w, gap_w = 62, 25, 8
    x0, y_hi, y_lo = 40, 64, 104
    labels = [('S', True)] + [(str(b), b == 1) for b in (1, 0, 1, 0, 0, 1, 0, 1)]
    x = x0
    d = [f'M{x0 - 24} {y_lo}']
    for label, is_long in labels:
        w = long_w if is_long else short_w
        f.rect(x, 30, w - 1, 86, ORANGE_WASH if label == 'S' else BLUE_WASH if is_long else GRAY_WASH, r=2)
        f.text(x + w / 2, 47, label, size=13, anchor='middle', weight=600, mono=True)
        d.append(f'L{x} {y_lo}L{x} {y_hi}L{x + w / 2} {y_hi}L{x + w / 2} {y_lo}L{x + w} {y_lo}')
        x += w
    d.append(f'L{x + gap_w} {y_lo}L{x + gap_w} {y_hi}L{x + gap_w + long_w / 2} {y_hi}')
    f.path(''.join(d), stroke=INK, width=2)
    f.text(x + gap_w + long_w / 2 + 8, y_hi + 4, L('次のバイト', 'next byte'), size=12, fill=SECONDARY)
    f.text(x0, 136, L('S = 開始ビット（必ず長い周期）。続いてデータを上位ビットから 1 周期ずつ送る。この例は A5h', 'S = start bit (always long). The data follows, MSB first, one cycle per bit. This example is A5h'), size=13)
    rows = [(L('データの 1', 'Data 1'), L('長い周期（約 1217 Hz、0.82 ms）', 'long cycle (about 1217 Hz, 0.82 ms)')),
            (L('データの 0', 'Data 0'), L('短い周期（約 3041 Hz、0.33 ms）', 'short cycle (about 3041 Hz, 0.33 ms)')),
            (L('バイトの長さ', 'Byte length'), L('FFh は 7.45 ms、00h は 3.51 ms。時間は中身の 1 の数で決まる', 'FFh takes 7.45 ms and 00h takes 3.51 ms: the time depends on the number of 1 bits'))]
    for i, (name, value) in enumerate(rows):
        f.text(40, 164 + i * 22, name, size=13, weight=600)
        f.text(140, 164 + i * 22, value, size=13, fill=SECONDARY)
    f.save()


def tape_retake():
    f = Figure('tape_retake', 860, 214, L('CSAVE M を 2 回録音した実際の結果', 'A real result of recording CSAVE M twice'))
    f.text(16, 24, L('PC-E650 の本体 ROM（C0000-FFFFF、1024 ブロック）', 'PC-E650 main ROM (C0000-FFFFF, 1024 blocks)'), weight=600)
    left, width = 130, 700
    red = [0xD9200, 0xDC000, 0xEE100, 0xF3A00, 0xF7A00, 0xF9700, 0xFB700]
    def at(a):
        return left + (a - 0xC0000) / 0x40000 * width
    rows = [(L('1 回目', '1st take'), WARNING, CRITICAL), (L('2 回目', '2nd take'), WARNING, None), (L('合わせた結果', 'Combined'), GOOD, WARNING)]
    for r, (name, base, mark) in enumerate(rows):
        y = 40 + r * 38
        f.text(left - 12, y + 18, name, anchor='end', size=13, fill=SECONDARY)
        f.rect(left, y, width, 26, base, r=2)
        if mark:
            for a in red:
                f.rect(at(a), y, 3, 26, mark)
    y = 40 + 3 * 38 - 4
    for a in range(0xC0000, 0x100000, 0x10000):
        f.line(at(a), y, at(a), y + 4, AXIS)
        f.text(at(a), y + 18, f'{a:05X}', size=12, fill=MUTED, anchor='middle', mono=True)
    x = left
    for label, color in ((L('2 回の録音が一致', 'Two takes agree'), GOOD), (L('読めたが 1 回だけで未確認', 'Read once, not confirmed'), WARNING), (L('読めなかった', 'Not read'), CRITICAL)):
        f.rect(x, 184, 13, 13, color, r=2)
        f.text(x + 19, 195, label, size=12, fill=SECONDARY)
        x += 40 + len(label) * (13 if lang == 'ja' else 7)
    f.save()


def tape_pipeline():
    f = Figure('tape_pipeline', 860, 300, L('CSAVE M の読み取りの流れ', 'How CSAVE M is read'))
    steps = [
        (L('搬送音を探す', 'Find the carriers'), L('10000 周期と 11000 周期の組', 'a pair of 10000 and 11000 cycles')),
        (L('ヘッダの前半で学ぶ', 'Learn from the header start'), L('01h、名前、0Dh は形が決まっている', '01h, name, 0Dh are predictable')),
        (L('本体を 3000 バイト読む', 'Read 3000 data bytes'), L('応答とビットの長さを学び直す', 'relearn the response and bit lengths')),
        (L('ヘッダを読む', 'Read the header'), L('位置を変えて読み、多数決で決める', 'read at many offsets and vote')),
    ]
    x = 16
    for i, (a, b) in enumerate(steps):
        f.box(x, 22, 192, 62, [a, b], size=12)
        if i < 3:
            f.arrow(x + 192, 53, x + 214, 53)
        x += 214
    f.path('M766 84 L766 108 L112 108 L112 124', arrow=True)
    f.text(430, 102, L('ヘッダが読めなければ、位置をずらして本体からやり直す（最大 9 通り）', 'If the header cannot be read, shift the position and redo the data (up to 9 tries)'), size=12, fill=SECONDARY, anchor='middle')
    f.rect(16, 124, 828, 84, BLUE_WASH, r=8)
    f.text(30, 144, L('本体を最後まで読む', 'Read all the data'), weight=600)
    x = 30
    for i, (a, b) in enumerate((
            (L('1 バイトずつ', 'Byte by byte'), L('256 通りの波形と比べる', 'compare 256 waveforms')),
            (L('FFh の並び', 'FFh runs'), L('時間から数を数える', 'count them by time')),
            (L('見失ったら', 'If lost'), L('戻って別の道も試す', 'back up, try other paths')),
            (L('仕上げ', 'Polish'), L('ビットの入れ替わりを直す', 'fix swapped bits')))):
        f.box(x, 152, 188, 46, [a, b], fill=SURFACE, size=12)
        if i < 3:
            f.arrow(x + 188, 175, x + 202, 175)
        x += 202
    f.arrow(430, 208, 430, 228)
    f.box(16, 228, 268, 56, [L('物音の大きかったバイトを除く', 'Drop bytes under loud noise'), L('含むブロックは読めなかったことに（赤）', 'their blocks count as not read (red)')], size=12)
    f.arrow(284, 256, 300, 256)
    f.box(300, 228, 260, 56, [L('ヘッダの番地から順に番地を付ける', 'Number from the header address'), L('読めたブロックはすべて黄', 'every block read is yellow')], size=12)
    f.arrow(560, 256, 576, 256)
    f.box(576, 228, 268, 56, [L('ほかの録音と比べる', 'Compare with other recordings'), L('一致すれば緑、違えば赤', 'green if they agree, red if not')], size=12)
    f.save()


# ---------------------------------------------------------------- ANALYSIS

def pipeline():
    f = Figure('pipeline', 860, 410, L('解析の全体の流れ', 'Overview of the analysis'))
    cx = 150
    f.box(cx - 110, 16, 220, 40, L('録音ファイル', 'Recording'), fill=SURFACE, stroke=AXIS)
    f.arrow(cx, 56, cx, 80)
    f.box(cx - 110, 80, 220, 40, L('1. 音声の読み込み', '1. Load the audio'))
    f.arrow(cx, 120, cx, 150)
    # CSAVE M はパターンを通らない
    f.arrow(cx + 110, 100, 318, 100)
    f.box(318, 80, 200, 40, [L('4. 長い搬送音があれば', '4. A long carrier means'), 'CSAVE M'], fill=ORANGE_WASH, size=12)
    f.arrow(518, 100, 566, 100)
    f.box(566, 80, 240, 40, [L('9. CSAVE M の読み取り', '9. CSAVE M reading'), L('1 バイトずつ、波形を比べる', 'waveforms, byte by byte')], fill=ORANGE_WASH, size=12)
    f.path('M806 100 L850 100 L850 330 L460 330 L460 344', arrow=True)
    # パターンの枠
    f.rect(20, 150, 820, 166, BLUE_WASH, r=8)
    f.text(34, 171, L('解析パターンごと（「標準」から順に）', 'For each analysis pattern (from "standard")'), weight=600)
    y = 184
    x = 34
    for lines in ([L('2. 前処理', '2. Preprocess'), L('チャンネル、高域通過', 'channel, high-pass')], [L('3. 音の区切り', '3. Find sound'), L('音量の包絡', 'level envelope')], [L('4. 版の判別', '4. Which version'), L('長い音があるか', 'any long sound?')]):
        f.box(x, y + 20, 160, 56, lines, fill=SURFACE)
        f.arrow(x + 160, y + 48, x + 180, y + 48)
        x += 180
    f.box(x, y + 2, 230, 44, [L('5. 機械語版', '5. Machine code'), L('波形モデルで 1 ビットずつ', 'waveform model, bit by bit')], fill=SURFACE)
    f.box(x, y + 54, 230, 44, [L('6. BASIC 版', '6. BASIC'), L('音の高さを 1 音ずつ', 'pitch, tone by tone')], fill=SURFACE)
    f.path(f'M{x - 20} {y + 48} L{x - 10} {y + 48} L{x - 10} {y + 24} L{x - 1} {y + 24}', arrow=True)
    f.path(f'M{x - 10} {y + 48} L{x - 10} {y + 76} L{x - 1} {y + 76}', arrow=True)
    f.text(430, 306, L('全部読めたら終わり。読めていなければ次のパターンで、残りを読む', 'Stop once everything is read; otherwise the next pattern reads the rest'), size=12, fill=SECONDARY, anchor='middle')
    f.arrow(cx, 316, cx, 344)
    f.box(cx - 110, 344, 220, 48, [L('7. パターンの統合', '7. Combine patterns'), L('食い違いは読めなかったことに', 'disagreements count as not read')])
    f.arrow(cx + 110, 368, 340, 368)
    f.box(340, 344, 240, 48, [L('8. 録音の統合', '8. Combine recordings'), L('補い合う、食い違いは保留', 'fill gaps, hold disagreements')])
    f.arrow(580, 368, 620, 368)
    f.box(620, 344, 120, 48, L('ROM ファイル', 'ROM file'), fill=INK, ink=SURFACE)
    f.save()


def gap_envelope():
    f = Figure('gap_envelope', 860, 322, L('ブロックの間の音量（実機の録音）', 'Level between blocks (real recordings)'))
    panels = [('e500', 'PC-E500', L('基準より下が 8.2 ms → ここで切れる', '8.2 ms below the threshold: blocks separate')),
              ('u6000', 'PC-U6000', L('基準より下が 3.2 ms（3.5 ms 未満）→ つながる', '3.2 ms below (under 3.5 ms): blocks join'))]
    for i, (key, name, note) in enumerate(panels):
        x = 56 + i * 410
        env = DATA['gap'][key]['env']
        th = DATA['gap'][key]['threshold']
        ts = [-30 + k / 12 for k in range(len(env))]
        f.text(x, 22, name, weight=600)
        p = Plot(f, x, 44, 370, 180, (-30, 30), (-50, 0), (-30, -15, 0, 15, 30), (-50, -25, 0), xlabel=L('ブロックの切れ目からの時間（ms）', 'Time from the block boundary (ms)'), ylabel='dB', yfmt=lambda v: str(v))
        # 基準より下の区間を薄く塗る
        run = None
        for k, (e, t) in enumerate(zip(env + [0], th + [0])):
            if e < t and run is None:
                run = k
            elif e >= t and run is not None:
                f.rect(p.X(ts[run]), 44, p.X(ts[k - 1]) - p.X(ts[run]), 180, ORANGE_WASH)
                run = None
        p.series(ts, th, ORANGE, 2)
        p.series(ts, env, BLUE, 1.5)
        f.text(x, 284, note, size=13)
    legend(f, 56, 308, [(L('音量の包絡', 'Level envelope'), BLUE), (L('鳴っているかの基準', 'Sound-on threshold'), ORANGE)])
    f.save()


def response():
    f = Figure('response', 860, 296, L('切り替え 1 回の波形（応答）', 'The waveform of one toggle (the response)'))
    panels = [('e500', L('PC-E500（6 ms まで学習）', 'PC-E500 (learned to 6 ms)')), ('u6000', L('PC-U6000（20 ms まで学習）', 'PC-U6000 (learned to 20 ms)'))]
    for i, (key, name) in enumerate(panels):
        x = 50 + i * 410
        v = DATA['response'][key]
        ts = [k / 48 for k in range(len(v))]
        f.text(x, 22, name, weight=600)
        p = Plot(f, x, 54, 370, 170, (0, 20), (-1, 1), (0, 5, 10, 15, 20), (-1, 0, 1), xlabel=L('切り替えからの時間（ms）', 'Time from the toggle (ms)'), ylabel=L('最大を 1 とした大きさ', 'Relative to the peak'))
        f.rect(p.X(0), 54, p.X(0.41) - p.X(0), 170, BLUE_WASH)
        p.series(ts, v, BLUE, 1.5)
    f.text(16, 284, L('実機の録音から求めた波形。薄い帯が 1 ビットの長さで、響きは後のビットへ重なる。', 'Learned from real recordings. The band is one bit, so the ring reaches far into the following bits.'), size=13, fill=SECONDARY)
    f.save()


def model():
    d = DATA['model']
    f = Figure('model', 860, 300, L('波形のモデルと実際の録音', 'The waveform model and a real recording'))
    n = len(d['recorded'])
    ts = list(range(n))
    bits_y = 26
    x0, w = 78, 762
    X = lambda s: x0 + s / (n - 1) * w
    cell = d['cell_ms'] * 48
    f.text(16, bits_y + 13, L('ビット', 'Bits'), size=12, fill=SECONDARY)
    starts = []
    prev = None
    edges = d['edges']
    # ビットの頭の位置は切り替えの列から求める
    index, at = 0, edges[0][0]
    for k, bit in enumerate(d['bits']):
        if index >= len(edges):
            break
        head = edges[index][0]
        next_index = index + (2 if bit else 1)
        next_head = edges[next_index][0] if next_index < len(edges) else None
        if next_head is None or next_head > n:
            break
        f.rect(X(head), bits_y, X(next_head) - X(head) - 2, 20, BLUE_WASH if bit else GRAY_WASH, r=2)
        f.text((X(head) + X(next_head)) / 2, bits_y + 15, bit, size=12, anchor='middle', mono=True)
        index = next_index
    f.text(16, bits_y + 44, L('切り替え', 'Toggles'), size=12, fill=SECONDARY)
    for at, rising in edges:
        if 0 <= at < n:
            f.line(X(at), bits_y + 32, X(at), bits_y + 50, INK if rising else MUTED, 2 if rising else 1)
    p = Plot(f, x0, 92, w, 150, (0, n - 1), (-1, 1), [i * 48 for i in range(0, 7)], (-1, 0, 1), xlabel=L('時間（ms）', 'Time (ms)'), xfmt=lambda v: str(v // 48))
    p.series(ts, d['recorded'], BLUE, 2)
    p.series(ts, d['model'], ORANGE, 1.5)
    legend(f, x0, 288, [(L('録音', 'Recording'), BLUE), (L('モデル', 'Model'), ORANGE)])
    f.text(840, 292, L(f'PC-E500 の実機の録音。モデルで音の {d["explained"] * 100:.0f}% を説明できる', f'Real PC-E500 recording. The model explains {d["explained"] * 100:.0f}% of the sound'), size=12, fill=SECONDARY, anchor='end')
    f.save()


def split():
    f = Figure('split', 860, 150, L('つながったブロックの切り方', 'Cutting joined blocks'))
    y, h = 46, 34
    f.text(16, 22, L('響きで無音が埋まり、3 ブロックが 1 つの塊に見える', 'A ring fills the silences, so 3 blocks look like one burst'), weight=600)
    f.rect(40, y, 780, h, BLUE_WASH, r=3)
    bw = 260
    for i in (1, 2):
        cut = 40 + i * bw
        f.rect(cut - 30, y - 6, 60, h + 12, ORANGE_WASH, r=3)
        f.line(cut, y - 8, cut, y + h + 8, CRITICAL, 2)
    f.text(40 + bw / 2, y + 22, L('ブロック', 'Block'), anchor='middle')
    f.text(40 + bw * 1.5, y + 22, L('ブロック', 'Block'), anchor='middle')
    f.text(40 + bw * 2.5, y + 22, L('ブロック', 'Block'), anchor='middle')
    f.rect(40, 110, 16, 13, ORANGE_WASH, r=2)
    f.text(62, 121, L('ビットの時間から分かるブロックの長さの範囲', 'Block length range, known from the bit timing'), size=12, fill=SECONDARY)
    f.line(460, 108, 460, 124, CRITICAL, 2)
    f.text(470, 121, L('範囲の中で音がいちばん小さい所で切る', 'Cut at the quietest point in that range'), size=12, fill=SECONDARY)
    f.save()


def candidates():
    d = DATA['candidates']
    f = Figure('candidates', 860, 312, L('開始位置の候補', 'Candidate start positions'))
    p = Plot(f, 56, 34, 780, 196, (min(d['offset']), max(d['offset'])), (0, 1.05), (-3, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7), (0, 0.5, 1), xlabel=L('開始位置（ms、音の検出位置から）', 'Start position (ms, from where sound was detected)'), ylabel=L('先頭 3 バイトで説明できる音（最大を 1）', 'Sound explained by the first 3 bytes (peak = 1)'))
    p.series(d['offset'], d['explained'], BLUE, 2)
    lookup = dict(zip(d['offset'], d['explained']))
    def nearest(x):
        return lookup[min(lookup, key=lambda k: abs(k - x))]
    true_peak = max((k for k in lookup if abs(k - d['true']) < 0.2), key=lambda k: lookup[k])
    p.dot(d['chosen'], nearest(d['chosen']), ORANGE)
    p.dot(true_peak, lookup[true_peak], GOOD)
    f.text(p.X(d['chosen']) + 10, p.Y(nearest(d['chosen'])) + 4, L('いちばん合う位置（誤り）', 'Best fit (wrong)'), size=13)
    f.text(p.X(true_peak) + 10, p.Y(lookup[true_peak]) - 2, L('本当の開始', 'True start'), size=13)
    f.text(16, 298, L(f'全部 FFh のブロック（{d["address"]}）を 6 ms の応答で計算。山は約 {d["two_bits_ms"]:.1f} ms（ビット 2 つ分）ごとに現れるので、上位 5 つを残して検査で決める。', f'An all-FFh block ({d["address"]}) with the 6 ms response. Peaks recur about every {d["two_bits_ms"]:.1f} ms (two bits), so the top 5 are kept and the check decides.'), size=13, fill=SECONDARY)
    f.save()


def decision():
    f = Figure('decision', 860, 252, L('1 ビットずつ決める', 'Deciding bit by bit'))
    y = 28
    x0, cell = 150, 42
    f.text(16, y + 20, L('ビット', 'Bits'), size=13, fill=SECONDARY)
    decided = [1, 0, 1, 1, 0, 0]
    for i, b in enumerate(decided):
        f.box(x0 + i * cell, y, cell - 3, 30, str(b), fill=GRAY_WASH)
    xn = x0 + len(decided) * cell
    for i, label in enumerate(('?', '?', '?')):
        f.box(xn + i * cell, y, cell - 3, 30, label, fill=BLUE_WASH if i else ORANGE_WASH)
    for i in range(3):
        f.box(xn + (3 + i) * cell, y, cell - 3, 30, '', fill=SURFACE, stroke=GRID)
    f.text(x0 + 3 * cell, y + 50, L('決めたビット', 'Decided'), size=12, fill=SECONDARY, anchor='middle')
    f.text(xn + 1.5 * cell, y + 50, L('先読みの 3 ビット', '3-bit lookahead'), size=12, fill=SECONDARY, anchor='middle')
    steps = [
        ('1', L('決めたビットの応答（響きも含む）を、録音から差し引く', 'Subtract the responses of decided bits, ring included, from the recording')),
        ('2', L('次の 3 ビットの 8 通りの波形を計算し、残りの音といちばん近いものを探す', 'Compute the waveform of all 8 combinations of the next 3 bits, and find the closest')),
        ('3', L('先頭の 1 ビット（橙）だけを確定する。0 と 1 の差が小さければ「自信がない」と記録する', 'Decide only the first bit (orange). A small difference between 0 and 1 is noted as low confidence')),
        ('4', L('1 ビット進む。ときどき時間のずれと音量の変化を補正する', 'Move on one bit. Correct timing drift and level changes now and then')),
    ]
    for i, (n, label) in enumerate(steps):
        yy = y + 76 + i * 34
        f.raw(f'<circle cx="28" cy="{yy}" r="11" fill="{BLUE}"/>')
        f.text(28, yy + 4, n, size=12, fill='#ffffff', anchor='middle', weight=600)
        f.text(48, yy + 5, label, size=13)
    f.save()


def refit_loop():
    f = Figure('refit_loop', 860, 310, L('学習と読み直しの流れ', 'Learning and reading again'))
    f.box(20, 24, 180, 56, [L('学習', 'Learn'), L('学習用の並びから 6 ms', '6 ms from the sequence')], fill=ORANGE_WASH)
    f.arrow(200, 52, 240, 52)
    f.box(240, 24, 180, 56, [L('全ブロックを読む', 'Read every block'), L('候補とずれを試す', 'trying candidates, shifts')])
    f.arrow(420, 52, 460, 52)
    f.box(460, 24, 160, 56, [L('検査', 'Check'), L('番地のつながりも', 'and address continuity')], fill=BLUE_WASH)
    f.arrow(620, 52, 680, 52)
    f.text(650, 44, L('通る', 'pass'), size=12, fill=SECONDARY, anchor='middle')
    f.box(680, 24, 160, 56, L('読めた（緑）', 'Read (green)'), fill='#d7f0d7')
    f.arrow(540, 80, 540, 130)
    f.text(548, 110, L('通らない', 'fail'), size=12, fill=SECONDARY)
    f.box(450, 130, 180, 56, [L('学び直し', 'Learn again'), L('読めた内容で 20 ms', '20 ms from blocks read')], fill=ORANGE_WASH)
    f.path('M450 158 L330 158 L330 81', arrow=True)
    f.text(338, 150, L('読み直す（最大 3 回）', 'Read again (max 3)'), size=12, fill=SECONDARY)
    f.arrow(540, 186, 540, 226)
    f.text(548, 210, L('増えなくなったら', 'when it gains no more'), size=12, fill=SECONDARY)
    f.box(430, 226, 220, 62, [L('修復', 'Repair'), L('自信のないビットを反転', 'flip low-confidence bits'), L('検査と番地の一致を確かめる', 'check and address must match')], fill=BLUE_WASH)
    f.path('M650 240 L760 240 L760 81', arrow=True)
    f.text(668, 232, L('通る（黄）', 'pass (yellow)'), size=12, fill=SECONDARY)
    f.arrow(430, 257, 250, 257)
    f.text(340, 249, L('通らない', 'fail'), size=12, fill=SECONDARY, anchor='middle')
    f.box(40, 228, 210, 58, [L('読めなかった（赤）', 'Not read (red)'), L('次のパターンか録り直しへ', 'next pattern, or a retake')], fill='#f7d6d6')
    f.save()


def address_check():
    f = Figure('address_check', 860, 256, L('番地の検査', 'Checking the address'))
    f.text(16, 24, L('20 bit の番地のうち、検査で確かめられる部分', 'Which bits of the 20-bit address the check confirms'), weight=600)
    x, y, bit = 40, 36, 39
    groups = [(8, L('上位 8 bit（S2 は見ない）', 'Upper 8 bits (S2 does not see them)'), ORANGE_WASH, ORANGE), (4, L('4 bit', '4 bits'), '#d7f0d7', GOOD), (8, L('下位 8 bit（必ず 0）', 'Low 8 bits (always 0)'), '#d7f0d7', GOOD)]
    for count, label, fill, stroke in groups:
        f.box(x, y, count * bit - 2, 34, label, fill=fill, stroke=stroke, size=12)
        x += count * bit
    f.text(40, 92, L('S2 は番地の 256 倍を足す形なので、番地の上位 8 bit が変わっても S2 は変わらない。', 'S2 adds 256 times the address, so the upper 8 bits can change without changing S2.'), size=13, fill=SECONDARY)
    f.text(16, 130, L('そこで前後のブロックとのつながりを確かめる', 'So continuity with nearby blocks is checked'), weight=600)
    seq = [('21F00', GOOD), ('22000', GOOD), ('62100', CRITICAL), ('22200', GOOD), ('22300', GOOD)]
    x = 40
    for i, (label, color) in enumerate(seq):
        wrong = color == CRITICAL
        f.box(x, 144, 120, 40, label, fill='#f7d6d6' if wrong else '#d7f0d7', stroke=color)
        if i < 4:
            f.arrow(x + 121, 164, x + 155, 164)
        x += 158
    f.text(40 + 2 * 158 + 60, 208, L('データは正しいが番地が違う → 捨てる', 'Right data, wrong address: discarded'), size=13, anchor='middle')
    f.text(40 + 2 * 158 + 60, 228, L('（読み直しか修復で 22100 として読める）', '(reading again or repair gets it as 22100)'), size=12, fill=SECONDARY, anchor='middle')
    f.save()


def stage_gain():
    f = Figure('stage_gain', 860, 232, L('各段階で読めたブロック', 'Blocks read at each stage'))
    labels = [L('学習と 1 ビットずつの読み取りだけ', 'Learning and reading bit by bit only'), L('+ ブロックの切り出し、候補、学び直し', '+ separating blocks, candidates, relearning'), L('+ ビットの修復', '+ bit repair'), L('+ ほかの解析パターン', '+ other analysis patterns')]
    panels = [(L('本体 ROM（全 1024 ブロック）', 'Main ROM (1024 blocks)'), [710, 1002, 1017, 1024], 1024), (L('拡張 ROM（全 512 ブロック）', 'Extension ROM (512 blocks)'), [371, 507, 507, 512], 512)]
    for k, (name, values, total) in enumerate(panels):
        x = (290 if lang == 'en' else 260) + k * 290
        f.text(x, 24, name, weight=600)
        f.line(x, 36, x, 36 + 4 * 40 + 2, AXIS)
        for i, v in enumerate(values):
            y = 42 + i * 40
            if k == 0:
                f.text(x - 12, y + 16, labels[i], size=12, anchor='end', fill=SECONDARY)
            w = v / total * 190
            f.path(f'M{x} {y} H{x + w - 4:.1f} a4 4 0 0 1 4 4 V{y + 18} a4 4 0 0 1 -4 4 H{x} Z', stroke='none', fill=BLUE, width=0)
            f.text(x + w + 7, y + 16, v, size=13)
    f.text(16, 222, L('PC-U6000 の録音。誤ったデータはどの段階でも 0。', 'PC-U6000 recordings. No wrong data at any stage.'), size=12, fill=SECONDARY)
    f.save()


def comb():
    d = DATA['comb']
    f = Figure('comb', 860, 322, L('倍音から周期を読む', 'Reading the period from the harmonics'))
    f.text(50, 22, L(f'音のスペクトル（長さ {d["duration_ms"]} ms の短い音）', f'Spectrum of a short tone ({d["duration_ms"]} ms)'), weight=600)
    p = Plot(f, 50, 58, 370, 186, (0, 12000), (-40, 0), (0, 4000, 8000, 12000), (-40, -20, 0), xlabel=L('周波数（Hz）', 'Frequency (Hz)'), ylabel='dB')
    f0 = d['f0']
    k = 1
    while k * f0 < 12000:
        f.line(p.X(k * f0), 58, p.X(k * f0), 244, ORANGE, 1)
        k += 1
    p.series(d['freq'], d['db'], BLUE, 1.5)
    legend(f, 50, 306, [(L('スペクトル', 'Spectrum'), BLUE), (L('読んだ周期の倍音の位置', 'Harmonics of the period read'), ORANGE)])
    f.text(470, 22, L('周期の候補ごとの合い方', 'Fit for each candidate period'), weight=600)
    q = Plot(f, 470, 58, 370, 186, (60, 520), (min(d['score']), 1.05), (100, 200, 300, 400, 500), (0, 1), xlabel=L('周期（1/256000 秒）', 'Period (1/256000 s)'), ylabel=None)
    w0, w1 = d['window']
    f.rect(q.X(w0), 58, q.X(w1) - q.X(w0), 186, BLUE_WASH)
    q.series(d['period'], d['score'], BLUE, 2)
    look = dict(zip(d['period'], d['score']))
    near = lambda u: look[min(look, key=lambda k: abs(k - u))]
    q.dot(d['read'], near(d['read']), GOOD)
    q.dot(d['top'], near(d['top']), ORANGE)
    f.line(q.X(d['read']), q.Y(near(d['read'])) - 6, q.X(d['read']), 49, AXIS)
    f.text(q.X(d['read']), 45, L('帯の中の山（読み）', 'Peak in the band (read)'), size=12, anchor='middle')
    f.line(q.X(d['top']), q.Y(near(d['top'])) - 6, q.X(d['top']), 49, AXIS)
    f.text(q.X(d['top']), 45, L('全体の最大（読み違い）', 'Overall top (misread)'), size=12)
    f.text(470, 306, L('薄い帯が音の長さから許される範囲', 'The band is the range the duration allows'), size=12, fill=SECONDARY)
    f.save()


def scale_search():
    d = DATA['scale']
    f = Figure('scale_search', 860, 260, L('倍率を求める', 'Finding the scale'))
    p = Plot(f, 56, 34, 780, 160, (min(d['scale']), max(d['scale'])), (min(0, min(d['fit'])), 1.05), (0.98, 0.99, 1.0, 1.01, 1.02, 1.03), (0, 0.5, 1), xlabel=L('倍率の候補', 'Candidate scale'), ylabel=L('周期が格子に乗る度合い（最大を 1）', 'How well periods sit on the lattice (peak = 1)'), xfmt=lambda v: f'{v:.2f}')
    p.series(d['scale'], d['fit'], BLUE, 1.5)
    look = dict(zip(d['scale'], d['fit']))
    y = look[min(look, key=lambda k: abs(k - d['best']))]
    p.dot(d['best'], y, ORANGE)
    f.text(p.X(d['best']) + 10, p.Y(y) + 4, L(f'いちばん合う倍率 {d["best"]:.4f}', f'Best scale {d["best"]:.4f}'), size=13)
    f.text(16, 250, L('PC-E500 の BASIC 版の録音、周期 180 以上の 1500 音。ほかにも山があるので、検査の通り方で確かめる。', 'BASIC recording on a PC-E500, 1500 tones with a period of at least 180. There are other peaks, so the check confirms the choice.'), size=13, fill=SECONDARY)
    f.save()


def install_choice():
    f = Figure('install_choice', 860, 262, L('OS ごとのインストールの流れ', 'Installation by OS'))
    rows = [
        ('Windows', [([L('Releases', 'Releases'), L('ダウンロード', 'download')], GRAY_WASH), ([L('.NET、ffmpeg', '.NET, ffmpeg'), L('どちらも不要', 'neither needed')], SURFACE), (['BeepDump.exe', L('ダブルクリックで起動', 'double-click to start')], BLUE_WASH)]),
        ('Linux', [([L('.NET 10 SDK', '.NET 10 SDK'), L('ビルドに使う', 'for building')], GRAY_WASH), ([L('ビルド', 'Build'), 'dotnet publish'], GRAY_WASH), (['ffmpeg', L('MOV などを読むとき', 'to read MOV and so on')], ORANGE_WASH), (['beepdump', L('コマンドライン版', 'command line')], BLUE_WASH)]),
        ('macOS', [([L('.NET 10 SDK', '.NET 10 SDK'), L('ビルドに使う', 'for building')], GRAY_WASH), ([L('ビルド', 'Build'), 'dotnet publish'], GRAY_WASH), (['ffmpeg', L('MOV などを読むとき', 'to read MOV and so on')], ORANGE_WASH), (['beepdump', L('コマンドライン版', 'command line')], BLUE_WASH)]),
    ]
    for r, (name, steps) in enumerate(rows):
        y = 18 + r * 76
        f.text(16, y + 33, name, size=14, weight=600)
        x = 110
        for i, (lines, fill) in enumerate(steps):
            f.box(x, y, 160, 56, lines, fill=fill, stroke=GRID if fill == SURFACE else None)
            if i < len(steps) - 1:
                f.arrow(x + 161, y + 28, x + 183, y + 28)
            x += 185
    f.text(16, 252, L('.NET SDK が要るのはビルドのときだけ。できたアプリを動かすのに .NET は要らない。', 'The .NET SDK is only for building. Running the result needs no .NET.'), size=12, fill=SECONDARY)
    f.save()


# 実機の録音（PC-E500、機械語版、99 ブロック）を、別の機材で録ったように加工して読んだ結果。(標準だけ, 複数パターン)
DEVICES = [
    ('加工なし', 'Unprocessed', 99, 99),
    ('音量 1/33', 'Level 1/33', 99, 99),
    ('8 kHz より上を削る', 'Cut above 8 kHz', 99, 99),
    ('4 kHz より上を削る', 'Cut above 4 kHz', 43, 50),
    ('サンプリング 44.1 kHz', 'Sampled at 44.1 kHz', 99, 99),
    ('サンプリング 22 kHz', 'Sampled at 22 kHz', 99, 99),
    ('サンプリング 16 kHz', 'Sampled at 16 kHz', 64, 99),
    ('自動音量調整（周期 3 秒で 4 倍）', 'Automatic gain (4x over 3 s)', 99, 99),
    ('雑音（SN 比 20 dB）', 'Noise (SNR 20 dB)', 99, 99),
    ('雑音（SN 比 10 dB）', 'Noise (SNR 10 dB)', 0, 2),
    ('左チャンネルが無音', 'Left channel silent', 99, 99),
    ('左チャンネルに大きな雑音', 'Loud noise on left', 0, 99),
    ('左右が逆相', 'Left and right out of phase', 0, 99),
    ('電源ハム', 'Mains hum', 0, 99),
    ('音割れ', 'Clipping', 0, 99),
    ('録音機材の時計のずれ 0.5%', 'Recorder clock off by 0.5%', 99, 99),
    ('強い部屋の響き', 'Strong room reverberation', 0, 0),
    ('ノイズゲート', 'Noise gate', 99, 99),
]


# 同じく BASIC 版の録音（4 分、約 35 ブロック）。強い部屋の響きは時間内に読めなかった
DEVICES_BASIC = [
    ('加工なし', 'Unprocessed', 28, 32),
    ('音量 1/33', 'Level 1/33', 30, 32),
    ('8 kHz より上を削る', 'Cut above 8 kHz', 27, 32),
    ('4 kHz より上を削る', 'Cut above 4 kHz', 2, 4),
    ('サンプリング 44.1 kHz', 'Sampled at 44.1 kHz', 30, 32),
    ('サンプリング 22 kHz', 'Sampled at 22 kHz', 28, 31),
    ('サンプリング 16 kHz', 'Sampled at 16 kHz', 23, 32),
    ('自動音量調整（周期 3 秒で 4 倍）', 'Automatic gain (4x over 3 s)', 28, 32),
    ('雑音（SN 比 20 dB）', 'Noise (SNR 20 dB)', 29, 32),
    ('左チャンネルが無音', 'Left channel silent', 30, 30),
    ('左チャンネルに大きな雑音', 'Loud noise on left', 0, 30),
    ('左右が逆相', 'Left and right out of phase', 0, 29),
    ('電源ハム', 'Mains hum', 0, 16),
    ('音割れ', 'Clipping', 3, 14),
    ('録音機材の時計のずれ 0.5%', 'Recorder clock off by 0.5%', 29, 32),
    ('強い部屋の響き', 'Strong room reverberation', 0, 0),
    ('ノイズゲート', 'Noise gate', 27, 32),
]


def devices():
    device_chart('devices', DEVICES, 99, (0, 25, 50, 75, 99),
                 L('読めたブロック（全 99、機械語版。実機の録音を加工した試験）', 'Blocks read (of 99, machine code, real recording processed for the test)'),
                 L('誤ったデータはどの条件でも 0。青だけ見えるのは、2 つが同じ値で重なっている所。', 'No wrong data in any condition. Where only blue shows, the two values are the same.'))


def devices_basic():
    device_chart('devices_basic', DEVICES_BASIC, 35, (0, 10, 20, 30, 35),
                 L('読めたブロック（約 35、BASIC 版。実機の録音を加工した試験）', 'Blocks read (of about 35, BASIC, real recording processed for the test)'),
                 L('誤ったデータが通った条件がある（本文を参照）。青だけ見えるのは、2 つが同じ値で重なっている所。', 'Wrong data passed in some conditions (see the text). Where only blue shows, the two values are the same.'))


def device_chart(name, rows, full, ticks, title, note):
    pitch = 22
    f = Figure(name, 860, 76 + pitch * len(rows) + 70, L('録音機材の違いへの強さ', 'Tolerance of recording devices'))
    x0, w = 290, 500
    top = 44
    h = pitch * len(rows)
    f.text(16, 24, title, weight=600)
    X = lambda v: x0 + v / full * w
    for v in ticks:
        f.line(X(v), top, X(v), top + h, GRID)
        f.text(X(v), top + h + 18, v, size=12, fill=MUTED, anchor='middle')
    f.line(x0, top + h, x0 + w, top + h, AXIS)
    for i, (ja, en, single, multi) in enumerate(rows):
        y = top + pitch * i + pitch / 2
        f.text(x0 - 12, y + 4, L(ja, en), size=12, anchor='end', fill=SECONDARY)
        if multi != single:
            f.line(X(single), y, X(multi), y, AXIS, 2)
        f.raw(f'<circle cx="{X(single):.1f}" cy="{y:.1f}" r="5" fill="{ORANGE}" stroke="{SURFACE}" stroke-width="2"/>')
        f.raw(f'<circle cx="{X(multi):.1f}" cy="{y:.1f}" r="5" fill="{BLUE}" stroke="{SURFACE}" stroke-width="2"/>')
        f.text(x0 + w + 14, y + 4, multi, size=12, fill=INK)
    y = top + h + 42
    f.raw(f'<circle cx="{x0 + 6}" cy="{y}" r="5" fill="{ORANGE}"/>')
    f.text(x0 + 16, y + 4, L('標準のパターンだけ', 'Standard pattern only'), size=12, fill=SECONDARY)
    f.raw(f'<circle cx="{x0 + 186}" cy="{y}" r="5" fill="{BLUE}"/>')
    f.text(x0 + 196, y + 4, L('複数パターン（右端の数字）', 'Multiple patterns (number at right)'), size=12, fill=SECONDARY)
    f.text(16, y + 26, note, size=12, fill=SECONDARY)
    f.save()


FIGURES = [devices, devices_basic, install_choice, result_grid, repair, overview, recording_steps, tape_steps, time_compare, retake, tape_retake, rom_map, fast_stream, biphase, basic_frame, tape_stream, tape_frame, tape_pipeline, pipeline, gap_envelope, response, model, split, candidates, decision, refit_loop, address_check, stage_gain, comb, scale_search]

if __name__ == '__main__':
    for lang in ('ja', 'en'):
        for build in FIGURES:
            build()
    print(f'{len(FIGURES) * 2} figures written to {HERE}')
