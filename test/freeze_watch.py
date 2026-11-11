# Read-only: waits for the caution (flag leaves 7), then records every running car's speed and the gap to the car
# ahead on track for 20 s, and prints a summary: hardest braking, closest approaches (pile-up risk), and the
# physical order at the yellow vs the scoring order. Nothing is written to the game.
import ctypes, struct, subprocess, time, math, sys, csv, os

k = ctypes.windll.kernel32
out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq NR2003.exe', '/FO', 'CSV', '/NH'], capture_output=True, text=True).stdout
pid = int(out.split('","')[1])
h = k.OpenProcess(0x0010 | 0x0400, False, pid)
def rd(a, n):
    b = ctypes.create_string_buffer(n); r = ctypes.c_size_t()
    k.ReadProcessMemory(h, ctypes.c_void_p(a), b, n, ctypes.byref(r)); return b.raw
u8 = lambda a: rd(a, 1)[0]
u16 = lambda a: struct.unpack('<H', rd(a, 2))[0]
i32 = lambda a: struct.unpack('<i', rd(a, 4))[0]
f32 = lambda a: struct.unpack('<f', rd(a, 4))[0]
f64 = lambda a: struct.unpack('<d', rd(a, 8))[0]
OCD = 8724; AOFF = 2 * OCD
DLONG = 0xAD26C2 - AOFF; LAPS = 0xAD13A8 - AOFF; CARNUM = 0xAD207A - AOFF
FLAG = 0x76F184; TRACKLEN = 0xDACF0C; SIM = 0xBEBD30
ALIVE = 0x7A76DA - 2; SPAWNED = 0x76B35E - 2; PITROAD = 0x76BB0E - 2
B0 = 0xACCA30; VEL = 6634; TICKS = 0xBEBDF5
STANDINGS = 0x797996
PACE = 1  # the pace car's slot is excluded below by number 0 / not spawned as a racer

def gsec():
    t = i32(TICKS); return t / 36.0

def snap():
    L = f64(TRACKLEN)
    cars = []
    for c in range(48):
        if u8(ALIVE + c) != 1 or u8(SPAWNED + c) != 1: continue
        num = u16(CARNUM + c * OCD)
        if num <= 0: continue
        d = f32(DLONG + c * OCD); laps = u16(LAPS + c * OCD)
        vx, vy = f32(B0 + c * OCD + VEL), f32(B0 + c * OCD + VEL + 4)
        cars.append(dict(c=c, num=num, d=d, laps=laps, prog=laps + d / L if L > 0 else laps, v=math.hypot(vx, vy),
                         pit=u8(PITROAD + c) == 1, scored=i32(STANDINGS + c * 16)))
    return L, cars

if u8(FLAG) != 7:
    print('waiting for the green first (flag now %d)' % u8(FLAG), flush=True)
    while u8(FLAG) != 7: time.sleep(0.2)
    gl = i32(0x76F180)
    print('green on lap %d: armed for the next caution' % gl, flush=True)
else:
    print('armed: waiting for the caution (flag now %d)' % u8(FLAG), flush=True)
while u8(FLAG) == 7:
    time.sleep(0.05)
t0 = gsec(); w0 = time.time()
L, first = snap()
print('caution at lap %d (game %.1f s): recording 20 s' % (i32(0x76F180), t0), flush=True)
rows = []
prev = {}
maxdec = {}; mingap = {}
while time.time() - w0 < 20:
    L, cars = snap()
    t = gsec() - t0
    track = sorted([x for x in cars if not x['pit']], key=lambda x: -x['prog'])
    for i, x in enumerate(track):
        gap = (track[i - 1]['prog'] - x['prog']) * L if i > 0 else float('nan')
        rel = (x['v'] - track[i - 1]['v']) if i > 0 else 0.0     # + = closing on the car ahead
        rows.append((round(t, 2), x['num'], round(x['d'], 1), x['laps'], round(x['v'], 1), round(gap, 1), round(rel, 1)))
        p = prev.get(x['num'])
        if p and t > p[0]:
            dec = (p[1] - x['v']) / (t - p[0])
            if dec > maxdec.get(x['num'], (0,))[0]: maxdec[x['num']] = (dec, t, x['v'])
        prev[x['num']] = (t, x['v'])
        if i > 0 and gap == gap and gap < mingap.get(x['num'], (1e9,))[0]:
            mingap[x['num']] = (gap, t, rel, track[i - 1]['num'])
    time.sleep(0.1)

path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'freeze_%d.csv' % int(w0))
with open(path, 'w', newline='') as f:
    wr = csv.writer(f); wr.writerow(['t', 'num', 'dlong', 'laps', 'speed', 'gap_ahead_m', 'closing_ms']); wr.writerows(rows)

phys = [x['num'] for x in sorted([x for x in first if not x['pit']], key=lambda x: -x['prog'])]
scor = [x['num'] for x in sorted(first, key=lambda x: (-x['scored'], -x['prog']))]
print('SUMMARY (%d samples, csv %s)' % (len(rows), path))
print('  hardest braking (m/s^2, at s, speed then):', ', '.join('#%d %.0f@%.1fs' % (n, v[0], v[1]) for n, v in sorted(maxdec.items(), key=lambda kv: -kv[1][0])[:8]))
close = sorted(mingap.items(), key=lambda kv: kv[1][0])[:10]
print('  closest to the car ahead (m, at s, closing m/s):', ', '.join('#%d %.1fm@%.1fs %+.1f behind #%d' % (n, g[0], g[1], g[2], g[3]) for n, g in close))
print('  under 4 m (contact likely):', ' '.join('#%d' % n for n, g in mingap.items() if g[0] < 4) or 'none')
print('  on-track order at the yellow:', ' '.join('#%d' % n for n in phys[:20]))
print('  scoring order at the yellow: ', ' '.join('#%d' % n for n in scor[:20]))
diff = [n for i, n in enumerate(phys[:20]) if i < len(scor) and scor[i] != n]
print('  differ in the top 20:', ' '.join('#%d' % n for n in diff) or 'none')
