"""beepfast.asm をアセンブルし、実機で打ち込む BASIC（../BASIC/BEEPFAST.TXT）と、
CE-140F で LOAD する ../BASIC/BEEPFAST.BAS と ../BASIC/BEEPDUMP.BAS を作る。BEEPFAST.BAS には行番号のない 1 行目（領域の確保）は入らない。

  py build.py

README のプログラムのリストと、40 文字で折り返したリストの画像（../BASIC/*.png、Pillow が必要）も作り直す。
DATA は 1 行 PER_LINE バイトの 16 進と、その和。PC 側が学習に使う機械語の先頭も BeepDump.Core/FastCode.cs に書き出す。
打ち間違えた行は RUN したときに NG と表示される。
1 行目は行番号のない命令で、機械語の領域を確保する（一度だけ実行すればよい）。
"""
import html
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent


def tool(variable, name):
    """環境変数か PATH から道具を探す。"""
    path = os.environ.get(variable) or shutil.which(name)
    if not path:
        raise SystemExit(f'{name} が見つかりません。PATH に置くか、環境変数 {variable} で指定してください。')
    return path


ASM = tool('E500ASM', 'e500asm')
CONV = tool('CE140CONV', 'ce140conv')
BASIC = HERE.parent / 'BASIC'
PER_LINE = 16
FIRST_DATA = 100
COLUMNS = 40           # ポケコンの画面の桁数
DELAY = 130            # 半ビットの待ち。実機で約 2400 bps
ALLOCATE = 'POKE &HBFE03,&H1A,&HFD,&HB,0,2,0:CALL &HFFFD8'

PROGRAM = '''\
10 INPUT A,E:E=E+1:P=&HBFA00:POKE &HBFBE0,0,A/256 AND 255,A/65536,0,E/256 AND 255,E/65536 AND 15,{delay},0
20 READ D$,C:IF D$="" GOTO 50
30 S=0:FOR I=1 TO LEN D$ STEP 2:V=VAL ("&H"+MID$ (D$,I,2)):POKE P,V:P=P+1:S=S+V:NEXT
40 IF S=C GOTO 20
45 PRINT "NG ";D$:END
50 CALL &HBFA00
'''


def assemble():
    with tempfile.TemporaryDirectory() as folder:
        out = Path(folder) / 'beepfast.bin'
        subprocess.run([str(ASM), str(HERE / 'beepfast.asm'), '-o', str(out)], check=True, stdout=subprocess.DEVNULL)
        return out.read_bytes()


def listing(template, code):
    lines = [ALLOCATE] + template.format(delay=DELAY).splitlines()
    number = FIRST_DATA
    for i in range(0, len(code), PER_LINE):
        chunk = code[i:i + PER_LINE]
        lines.append(f'{number} DATA "{chunk.hex().upper()}",{sum(chunk)}')
        number += 10
    lines.append(f'{number} DATA "",0')
    return '\n'.join(lines) + '\n'


def csharp(text):
    return '"' + text.strip().replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\r\\n') + '"'


def listing_html(path, skip):
    """README に載せるリスト。折り返さず、少し大きく表示する。"""
    lines = path.read_text(encoding='ascii').splitlines()[skip:]
    body = html.escape('\n'.join(lines), quote=False)
    return f'<pre style="font-size:1.25em; white-space:pre-wrap"><code>{body}</code></pre>'


def update_readme(path):
    """README の <!-- list:ファイル skip=n --> の所へ、プログラムのリストを入れ直す。"""
    if not path.exists():
        return
    text = path.read_text(encoding='utf-8')
    text = re.sub(r'(<!-- list:(\S+) skip=(\d+) -->\n).*?(<!-- /list -->)',
                  lambda m: f'{m[1]}\n{listing_html(BASIC / m[2], int(m[3]))}\n\n{m[4]}', text, flags=re.S)
    path.write_text(text, encoding='utf-8', newline='\n')


def listing_image(source, target, title):
    """ポケコンの画面に合わせて 40 文字で折り返したリストの画像を作る。行番号ごとに背景の濃さを変える。"""
    size, margin = 30, 24
    font = ImageFont.truetype('consola.ttf', size)
    small = ImageFont.truetype('meiryo.ttc', 18)
    cell = font.getlength('0')
    pitch = int(size * 1.3)
    rows = []
    for number, line in enumerate(source.read_text(encoding='ascii').splitlines()):
        rows += [(number, line[i:i + COLUMNS]) for i in range(0, len(line), COLUMNS)]
    width = int(cell * COLUMNS) + 2 * margin
    notes = [*title, '色の変わり目が行の終わり。折り返しで ENTER を押さない', 'A color change ends a line. Do not press ENTER at a wrap.']
    head = 14 + 28 * len(notes) + 8
    image = Image.new('RGB', (width, head + pitch * len(rows) + margin), 'white')
    draw = ImageDraw.Draw(image)
    for i, note in enumerate(notes):
        draw.text((margin, 14 + 28 * i), note, font=small, fill='black' if i == 0 else '#555555')
    for row, (number, text) in enumerate(rows):
        y = head + row * pitch
        draw.rectangle((0, y, width, y + pitch - 1), fill='#e8eef7' if number % 2 else '#ffffff')
        draw.text((margin, y + 2), text, font=font, fill='black')
    image.save(target)


def main():
    code = assemble()
    text = listing(PROGRAM, code)
    (BASIC / 'BEEPFAST.TXT').write_text(text, encoding='ascii', newline='\r\n')
    print(f'BEEPFAST.TXT: {len(text.splitlines())} lines, {len(text) - len(text.splitlines())} chars')
    print(f'machine code {len(code)} bytes')
    update_readme(HERE.parent / 'README.md')
    update_readme(HERE.parent / 'README_en.md')
    first = '1 行目は行番号なし。最初に 1 回だけ実行 / Line 1 has no number: run it once first'
    for name, title in (('BEEPFAST', ['機械語版 / Machine-code version', first]), ('BEEPDUMP', ['BASIC 版 / BASIC version'])):
        listing_image(BASIC / f'{name}.TXT', BASIC / f'{name}.png', title)
    with tempfile.TemporaryDirectory() as folder:
        numbered = Path(folder) / 'BEEPFAST.TXT'
        numbered.write_text(text.split('\n', 1)[1], encoding='ascii', newline='\r\n')
        subprocess.run([str(CONV), 'text2bas', str(numbered), str(BASIC / 'BEEPFAST.BAS')], check=True)
    subprocess.run([str(CONV), 'text2bas', str(BASIC / 'BEEPDUMP.TXT'), str(BASIC / 'BEEPDUMP.BAS')], check=True)
    learn = ', '.join(f'0x{b:02X}' for b in code[:128])
    (HERE.parent / 'BeepDump/src/BeepDump.Core/FastCode.cs').write_text(
        'namespace BeepDump.Core;\n\n'
        '/// <summary>機械語の先頭 128 バイト（学習用の並び）と、打ち込む BASIC。fast/build.py が beepfast.asm から作る。</summary>\n'
        'public static class FastCode\n{\n'
        f'    public static readonly byte[] Learn = [{learn}];\n\n'
        f'    public const string MachineListing = {csharp(text)};\n\n'
        f'    public const string BasicListing = {csharp((BASIC / "BEEPDUMP.TXT").read_text(encoding="ascii"))};\n}}\n', encoding='utf-8', newline='\n')


if __name__ == '__main__':
    main()
