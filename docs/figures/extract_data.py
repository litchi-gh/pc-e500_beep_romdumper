"""Measure the data behind the plots in the documents from real recordings and write data.json.

  py extract_data.py SCRATCH ROMS

SCRATCH holds the audio as mov/NNNN.npz, the decoder's dumps as mov/NNNN.dbgtxt (learned timing and
responses), and mov/3178.trace (BASIC tone readings). ROMS is the folder with the reference ROMs.
The recordings are not in the repository; data.json is, so build_figures.py runs without them.
"""
import json
import re
import sys
from pathlib import Path

import numpy as np
from scipy import signal

SCRATCH, ROMS = Path(sys.argv[1]), Path(sys.argv[2])
RATE = 48000
HEAD = 3


def load(name):
    z = np.load(SCRATCH / 'mov' / f'{name}.npz')
    return signal.sosfilt(signal.butter(4, 800, 'hp', fs=RATE, output='sos'), z['audio'].astype(np.float64).mean(0) / 32768)


def dump(name):
    timing, up, down, lines = None, None, None, []
    for line in open(SCRATCH / 'mov' / f'{name}.dbgtxt', encoding='utf-8'):
        if line.startswith('# run') and timing is None:
            f = line.split()
            timing = dict(T=float(f[5]), M=float(f[7]), G=float(f[9]), E=float(f[11]))
        elif line.startswith('# up') and up is None:
            up = np.array(line.split()[2:], float) / 32768
        elif line.startswith('# dn') and down is None:
            down = np.array(line.split()[2:], float) / 32768
        elif not line.startswith('#'):
            f = line.split()
            lines.append(dict(t=float(f[2]), length=float(f[4]), address=f[6], ok=f[8] == '1', ff=f[10] == '1'))
    return timing, up, down, lines


def after(t, index):
    k = 12 if index == 0 else 7 if index == 1 else 48 if index == 2 else 36 if index < 258 else 42 if index == 258 else 22
    return t['G'] / 36 * k


def edges_of(data, start, t):
    edges, level, cell = [], False, start
    for index, value in enumerate(data):
        for n in range(8):
            bit = value >> n & 1
            level = not level
            edges.append((cell, level))
            if bit:
                level = not level
                edges.append((cell + t['M'], level))
            cell += t['T'] + bit * t['E']
        cell += after(t, index)
    return edges


def render(edges, up, down, size):
    y = np.zeros(size)
    for at, rising in edges:
        h = up if rising else down
        i = int(np.floor(at))
        f = at - i
        for shift, weight in ((0, 1 - f), (1, f)):
            a = max(0, i + shift)
            b = min(size, i + shift + len(h))
            if b > a:
                y[a:b] += weight * h[a - (i + shift):b - (i + shift)]
    return y


def envelope(x, center, span):
    """The detector's envelope and threshold around a sample, as in Segmenter.Find."""
    half, step = int(0.0004 * RATE), RATE // 12000
    wide = int(0.2 * RATE)
    part = x[center - span - wide:center + span + wide]
    power = np.convolve(part ** 2, np.ones(2 * half) / (2 * half), 'same')
    env = np.sqrt(power)
    top = signal.convolve(np.array(ndimage_max(env, int(0.1 * RATE))), np.ones(int(0.05 * RATE)) / int(0.05 * RATE), 'same')
    keep = slice(wide, wide + 2 * span, step)
    return env[keep], 0.12 * top[keep]


def ndimage_max(values, width):
    from scipy.ndimage import maximum_filter1d
    return maximum_filter1d(values, width)


def rounded(values, digits=2):
    return [round(float(v), digits) for v in values]


data = {}

# ---- The ring: how the learned responses differ between models
t80, up80, dn80, lines80 = dump('3180')
t83, up83, dn83, lines83 = dump('3183')
t84, up84, dn84, lines84 = dump('3184')
data['response'] = {
    'e500': rounded(up80 / np.abs(up80).max(), 3),
    'u6000': rounded(up83 / np.abs(up83).max(), 3),
}

# ---- A boundary between blocks: silent on the PC-E500, filled on the PC-U6000
x80, x84 = load('3180'), load('3184')
def boundary(x, t):
    """Center of the quietest 2 ms within 40 ms of a time."""
    c = int(t * RATE)
    part = x[c - 2000:c + 2000] ** 2
    energy = np.convolve(part, np.ones(96), 'same')
    return c - 2000 + int(np.argmin(energy[100:-100])) + 100
span = int(0.03 * RATE)
gaps = {}
for key, x, t in (('e500', x80, lines80[20]['t']), ('u6000', x84, 16.12)):
    c = boundary(x, t)
    env, th = envelope(x, c, span)
    floor = 1e-5
    gaps[key] = {'env': rounded(20 * np.log10(np.maximum(env, floor) / env.max()), 1), 'threshold': rounded(20 * np.log10(np.maximum(th, floor) / env.max()), 1)}
data['gap'] = gaps

# ---- Start candidates: periodic $00 gives peaks every two bits (an all-FF block of the extension ROM)
def candidates_for(line, up, down, full_up, full_down):
    start = int(line['t'] * RATE)
    length = len(up)
    before, later = int(0.003 * RATE), int(0.007 * RATE)
    longest = 8 * (t84['T'] + t84['E']) + t84['G'] * 48 / 36
    window = int(length + later + 3 * longest)
    origin = start - length
    y = x84[origin:origin + window]
    offsets, explained = [], []
    for k in range((before + later) * 2 + 1):
        d = -before + k * 0.5
        m = render(edges_of([0, 0, 0xA5], length + d, t84), up, down, window)
        xy, mm = float(m @ y), float(m @ m)
        g = xy / max(mm, 1e-30)
        offsets.append(d)
        explained.append(xy * g if g > 0 else 0)
    # 本当の開始は、正解のデータ（全部 FFh）で長い範囲を合わせて求める
    whole = [0, 0, 0xA5] + [0xFF] * 21
    full = len(full_up)
    size = int(24 * longest) + 2 * full
    y2 = x84[start - full:start - full + size]
    best, truth = -1, 0
    for k in range((before + later) * 2 + 1):
        d = -before + k * 0.5
        m = render(edges_of(whole, full + d, t84), full_up, full_down, size)
        xy, mm = float(m @ y2), float(m @ m)
        score = xy * xy / max(mm, 1e-30) if xy > 0 else 0
        if score > best:
            best, truth = score, d
    return np.array(offsets), np.array(explained), truth

short = int(0.006 * RATE)
found = None
for line in [l for l in lines84 if l['ff']][:60]:
    offsets, explained, truth = candidates_for(line, up84[:short], dn84[:short], up84, dn84)
    chosen = offsets[int(np.argmax(explained))]
    if abs(chosen - truth) > 20:
        found = (line, offsets, explained, truth, chosen)
        break
line, offsets, explained, truth, chosen = found
data['candidates'] = {
    'offset': rounded(offsets[::2] / RATE * 1000, 3),
    'explained': rounded(explained[::2] / explained.max(), 3),
    'true': round(truth / RATE * 1000, 3),
    'chosen': round(chosen / RATE * 1000, 3),
    'two_bits_ms': round(2 * t84['T'] / RATE * 1000, 3),
    'address': line['address'],
}

# ---- The waveform model against a real block of the PC-E500
rom = np.fromfile(ROMS / 'PC-E500(3.2).ROM', np.uint8)
x83 = x80
t83, up83, dn83 = t80, up80, dn80
line = next(l for l in lines80 if l['ok'] and not l['ff'] and l['address'] >= 'D0000' and l['length'] < 1)
address = int(line['address'], 16)
block_address = line['address']
block = rom[-0x40000 + address - 0xC0000:][:256]
whole = [0, 0, 0xA5] + [int(v) for v in block[:13]]
length = len(up83)
start = int(line['t'] * RATE)
origin = start - length
size = int(len(whole) * (8 * (t83['T'] + t83['E']) + t83['G'] * 48 / 36)) + 2 * length
y = x83[origin:origin + size]
best, chosen, gain = -1, 0, 1
for d in np.arange(-150, 400, 0.5):
    m = render(edges_of(whole, length + d, t83), up83, dn83, size)
    xy, mm = float(m @ y), float(m @ m)
    score = xy * xy / max(mm, 1e-30) if xy > 0 else 0
    if score > best:
        best, chosen, gain = score, d, xy / mm
edges = edges_of(whole, length + chosen, t83)
model = gain * render(edges, up83, dn83, size)
first_data = edges[next(i for i, e in enumerate(edges) if i > 0 and e[0] > length + chosen + 3 * 8 * t83['T'] + after(t83, 0) + after(t83, 1) + after(t83, 2) - 1)][0]
a = int(first_data)
b = a + 330
best, local = -1, 0
for d in np.arange(-4, 4, 0.25):
    m = render(edges_of(whole, length + chosen + d, t83), up83, dn83, size)
    xy, mm = float(m[a:b] @ y[a:b]), float(m[a:b] @ m[a:b])
    score = xy * xy / max(mm, 1e-30) if xy > 0 else 0
    if score > best:
        best, local, gain = score, d, xy / mm
edges = edges_of(whole, length + chosen + local, t83)
model = gain * render(edges, up83, dn83, size)
scale = max(np.abs(y[a:b]).max(), np.abs(model[a:b]).max())
shown = []
level_edges = [e for e in edges if a - 1 <= e[0] < b]
data['model'] = {
    'recorded': rounded(y[a:b] / scale, 3),
    'model': rounded(model[a:b] / scale, 3),
    'edges': [[round(e[0] - a, 2), bool(e[1])] for e in level_edges],
    'bits': [int(whole[3 + i // 8]) >> (i % 8) & 1 for i in range(16)],
    'cell_ms': round(t83['T'] / RATE * 1000, 3),
    'explained': round(1 - float(((y[a:b] - model[a:b]) ** 2).sum() / (y[a:b] ** 2).sum()), 3),
    'address': block_address,
}

# ---- The comb on one short BASIC tone
x78 = load('3178')
head = x78[:RATE * 240]
power = np.convolve(head ** 2, np.ones(38) / 38, 'same')
env = np.sqrt(power)
level = 0.12 * np.percentile(env, 99)
on = env > level
edges_on = np.flatnonzero(np.diff(on.astype(int)))
tones = [(edges_on[i], edges_on[i + 1]) for i in range(0 if on[edges_on[0] + 1] else 1, len(edges_on) - 1, 2)]
pick = None
for s, e in tones[400:]:
    if 0.0052 * RATE < e - s < 0.0058 * RATE and env[s:e].max() > 3 * level:
        pick = (s, e)
        break
s, e = pick
size = 8192
while size < 16 * (e - s):
    size *= 2
spectrum = np.abs(np.fft.rfft(head[s:e], size)) ** 2
per_bin = size / RATE
def score(u):
    f0 = 256000 / u
    total = 0.0
    for k in range(1, int(16000 / f0) + 1):
        total += spectrum[min(len(spectrum) - 1, int(k * f0 * per_bin + 0.5))] - spectrum[min(len(spectrum) - 1, int((k - 0.5) * f0 * per_bin + 0.5))]
    return total
periods = np.arange(60, 520, 0.5)
scores = np.array([score(u) for u in periods])
top = float(periods[int(np.argmax(scores))])
duration = (e - s) * 1000 / RATE
low, high = max(0.15, duration - 3.5) * 256 / 8, (duration + 1) * 256 / 8
inside = (periods >= low) & (periods <= high)
read = float(periods[inside][int(np.argmax(scores[inside]))])
f0 = 256000 / read
freq = np.arange(0, 12000, 40.0)
bins = (freq * per_bin).astype(int)
peak = spectrum[:int(12000 * per_bin)].max()
data['comb'] = {
    'freq': rounded(freq, 0),
    'db': rounded(10 * np.log10(np.maximum(spectrum[bins], peak * 1e-4) / peak), 1),
    'period': rounded(periods[::2], 1),
    'score': rounded(scores[::2] / scores.max(), 3),
    'top': top,
    'read': read,
    'window': [round(low, 1), round(high, 1)],
    'f0': round(f0, 1),
    'value': int(round((read / 1.0065 - 90) / 4)),
    'duration_ms': round(float(duration), 1),
}

# ---- The lattice search for the scale, from the BASIC tone readings
readings = []
for line in open(SCRATCH / 'mov' / '3178.trace', encoding='utf-8'):
    for m in re.finditer(r' (\d+):([\d.]+):[\d.]+:', line):
        if float(m[2]) > 180:
            readings.append(float(m[2]))
    if len(readings) >= 1500:
        break
readings = np.array(readings[:1500])
scales = np.arange(0.98, 1.03, 2e-5)
fit = np.array([np.cos(np.pi / 2 * (readings / s - 90)).sum() for s in scales])
step = 5
data['scale'] = {
    'scale': rounded(scales[::step], 5),
    'fit': rounded([fit[i:i + step].max() / fit.max() for i in range(0, len(fit) - step + 1, step)], 3),
    'best': round(float(scales[int(np.argmax(fit))]), 5),
}

# ---- The state of each block of the PC-U6000 main ROM, read with the standard pattern (written by a test harness)
data['states'] = (SCRATCH / 'mov' / '3183.states').read_text(encoding='ascii').strip()

out = Path(__file__).with_name('data.json')
out.write_text(json.dumps(data, separators=(',', ':')), encoding='utf-8')
print(out, out.stat().st_size, 'bytes')
for key in ('candidates', 'comb', 'scale'):
    print(key, {k: v for k, v in data[key].items() if not isinstance(v, list)})
print('model', {k: v for k, v in data['model'].items() if k in ('explained', 'address', 'cell_ms', 'bits')})
print('gap under threshold ms', {k: round(sum(e < t for e, t in zip(v['env'], v['threshold'])) / 12, 1) for k, v in data['gap'].items()})
print('gap peaks', {k: (min(v['env']), min(v['threshold'])) for k, v in data['gap'].items()})
