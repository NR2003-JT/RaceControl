"""Caution report: one block per caution from race_control.log (read only).

    python caution_report.py                 the last 10 cautions
    python caution_report.py --all           every caution in the log
    python caution_report.py --last 3        the last 3 cautions
    python caution_report.py --follow        keep running: print each caution as soon as it goes green
    python caution_report.py path\\to\\race_control.log

Times are real (wall clock) times, so with time acceleration a caution looks shorter than it was in game time;
the laps are the game's. A caution runs from the first line with flag 0-6 after green running to the first
flag 7 line ("green").
"""
import os
import re
import sys
import time

LINE = re.compile(r'^(\d\d):(\d\d):(\d\d) (?:lap (-?\d+) flag (\d): )?(.*)$')


def secs(h, m, s):
    return int(h) * 3600 + int(m) * 60 + int(s)


def clock(t):
    t %= 86400
    return '%02d:%02d:%02d' % (t // 3600, t // 60 % 60, t % 60)


def dur(a, b):
    d = (b - a) % 86400
    return '%d:%02d' % (d // 60, d % 60)


class Caution:
    def __init__(self, t, lap, track):
        self.start, self.lap, self.track = t, lap, track
        self.green = self.green_lap = None
        self.ended = None
        self.lines = []

    def add(self, t, lap, flag, text):
        self.lines.append((t, lap, flag, text))

    def find(self, *words):
        return [x for x in self.lines if all(w in x[3] for w in words)]

    def first(self, *words):
        f = self.find(*words)
        return f[0] if f else None

    def report(self):
        out = []
        laps = (self.green_lap - self.lap) if self.green_lap is not None else None
        head = 'CAUTION lap %s  %s  %s' % (self.lap, clock(self.start), self.track or '')
        if self.green is not None:
            head += '  ->  green lap %s %s  (%s laps, %s real)' % (self.green_lap, clock(self.green), laps, dur(self.start, self.green))
        else:
            head += '  ->  ' + (self.ended or 'still out')
        out.append(head)
        why = self.first('caution thrown') or self.first('caution near the stage end') or self.first('stage ') or None
        if why:
            out.append('  cause       %s' % why[3][:110])
        accel = self.first('time acceleration is on')
        if accel:
            out.append('  note        time acceleration was on during this caution')

        def at(x):
            return '+%s (lap %s)' % (dur(self.start, x[0]), x[1])

        pc_out, pc_on = self.first('pace car sent out'), self.first('pace car on track')
        if pc_on:
            held = ' - slow to reach its caution state (RaceControl logs this as "held up by traffic")' if self.first('held up by traffic') else ''
            out.append('  pace car    out %s%s' % (at(pc_on), held))
        elif pc_out:
            out.append('  pace car    sent %s' % at(pc_out))
        po = self.first('pits open to all cars') or self.first('pit road gate opened') or self.first('pit road opened')
        out.append('  pits open   %s' % (at(po) if po else 'not seen'))
        fp = [x for x in self.find('free pass') if x[3].startswith('free pass')]
        if fp:
            out.append('  free pass   %s' % fp[-1][3].replace('free pass: ', '')[:100])
        sent = sorted({m.group(1) for x in self.find('sent around it now') for m in [re.match(r'#(\S+)', x[3])] if m})
        waved = sorted({m.group(1) for x in self.find('wave around: #', 'stayed out') for m in [re.search(r'#(\S+)', x[3])] if m})
        went = sorted({m.group(1) for x in self.find('wave around: #', 'goes around the pace car now') for m in [re.search(r'#(\S+)', x[3])] if m})
        gave_up = sorted({m.group(1) for x in self.find('before its wave around: not waved') for m in [re.match(r'#(\S+)', x[3])] if m})
        dropped = sorted({m.group(1) for x in self.find('dropped back behind the leader') for m in [re.match(r'#(\S+)', x[3])] if m})
        passed = sorted({m.group(1) for x in self.find('passed the pace car') if not x[3].endswith('until it has passed the pace car') for m in [re.search(r'#(\S+)', x[3])] if m})
        if sent:
            out.append('  sent around %d lead lap car(s): %s' % (len(sent), ' '.join('#' + s for s in sent)))
        if waved:
            out.append('  wave around %d eligible: %s; %d sent%s' % (len(waved), ' '.join('#' + s for s in waved), len(went),
                       ('; pitted instead: ' + ' '.join('#' + s for s in gave_up)) if gave_up else ''))
        if passed or dropped:
            out.append('  got around  %d passed the pace car, %d dropped back instead%s' % (
                len(passed), len(dropped), (': ' + ' '.join('#' + s for s in dropped)) if dropped else ''))
        lu = self.first('lineup after the pit stops')
        if lu:
            m = re.search(r'(\d+) cars given a new spot', lu[3])
            out.append('  lineup      %s%s' % (at(lu), (' - %s cars moved' % m.group(1)) if m else ''))
        late = [x for x in self.find('is on pit road: to the end of the line') if lu and x[0] >= lu[0]]
        if late:
            seen = []
            for x in late:
                m2 = re.match(r'#(\S+)', x[3])
                if m2 and m2.group(0) not in seen: seen.append(m2.group(0))
            out.append('  late pit    %s' % ' '.join(seen))
        held = self.first('one to go held')
        rel = self.first('one to go released') or self.first('one to go no longer held')
        if held:
            out.append('  1 to go     held for the pit stops%s' % ((' - ' + rel[3][:80] + ' ' + at(rel)) if rel else ''))
        otg = self.first('one to go:')
        if otg:
            out.append('  one to go   %s' % at(otg))
        for key, label in (('line check 10 s after one to go', 'at 1 to go'), ('line check after double file', 'double file'),
                           ('line check just before the green', 'at green')):
            x = self.first(key)
            if x:
                m = re.search(r'(\d+) out of place', x[3])
                lapped = re.search(r'(\d+) lapped car\(s\) still ahead', x[3])
                out.append('  line        %-11s %s out of place%s' % (label, m.group(1) if m else '?',
                                                                       (', %s lapped ahead of lead lap' % lapped.group(1)) if lapped and lapped.group(1) != '0' else ''))
        credited = [x for x in self.find('lap credited')]
        if credited:
            out.append('  at green    %d lap(s) credited by Race Control (not gained on track)' % len(credited))
        odd = []
        for words, label in ((('untangled',), 'untangled'), (('phantom penalty',), 'phantom penalty stops called off'),
                             (('penalty cleared',), 'penalties cleared'), (('the game re-ordered',), 'game re-ordered the line'),
                             (('memory error',), 'memory errors')):
            n = len(self.find(*words))
            if n:
                odd.append('%s %d' % (label, n))
        if odd:
            out.append('  also        ' + ', '.join(odd))
        return '\n'.join(out)


def parse(lines):
    cautions, cur, track, under = [], None, None, False
    for raw in lines:
        m = LINE.match(raw.rstrip('\r\n'))
        if not m:
            if raw.startswith('RaceControl build'):
                if under: cur.ended = 'no green: Race Control restarted'
                cur, under = None, False
            continue
        h, mi, s, lap, flag, text = m.groups()
        t = secs(h, mi, s)
        if text.startswith('track: ') or text.startswith('NR2003 closed') or text.startswith('NR2003 CRASHED'):
            if under: cur.ended = 'no green: ' + ('session changed' if text.startswith('track') else text[:60])
            under = False
            if text.startswith('track: '): track = text[7:]
            continue
        if flag is None:
            continue
        flag, lap = int(flag), int(lap)
        if flag != 7 and not under and lap > 0:
            cur = Caution(t, lap, track)
            cautions.append(cur)
            under = True
        if under:
            if flag == 7:
                cur.green, cur.green_lap = t, lap
                cur.add(t, lap, flag, text)
                under = False
            else:
                cur.add(t, lap, flag, text)
        elif cur is not None and flag == 7 and t - (cur.green or t) < 10 and text.startswith('green:'):
            cur.add(t, lap, flag, text)              # the lap settles right after the green
    real = ('pace car held', 'pace car sent', 'one to go:', 'caution thrown', 'pits closed until', 'pit road gate closed')
    cautions = [c for c in cautions if any(w in x[3] for x in c.lines for w in real)]
    return cautions, track


def main():
    args = sys.argv[1:]
    path = next((a for a in args if not a.startswith('--') and not a.isdigit()),
                os.path.join(os.path.dirname(os.path.abspath(__file__)), 'app', 'race_control.log'))
    if '--follow' in args:
        seen = None
        while True:
            with open(path, encoding='utf-8', errors='replace') as f:
                cs, _ = parse(f)
            done = [c for c in cs if c.green is not None]
            if seen is None:
                seen = len(done)
                print('watching %s for cautions (Ctrl+C to stop)' % path, flush=True)
            for c in done[seen:]:
                print(c.report() + '\n', flush=True)
            seen = len(done)
            time.sleep(5)
    with open(path, encoding='utf-8', errors='replace') as f:
        lines = f.readlines()
    cs, _ = parse(lines)
    if '--all' not in args:
        n = int(args[args.index('--last') + 1]) if '--last' in args else 10
        cs = cs[-n:]
    if not cs:
        print('no cautions found')
        return
    for c in cs:
        print(c.report() + '\n')
    done = [c for c in cs if c.green is not None]
    if done and [c for c in done if 0 <= c.green_lap - c.lap < 40]:
        laps = [c.green_lap - c.lap for c in done if 0 <= c.green_lap - c.lap < 40]
        print('%d caution(s): %s laps each on average (%d-%d)' % (len(done), round(sum(laps) / len(laps), 1), min(laps), max(laps)))


if __name__ == '__main__':
    main()
