using System;
using System.Collections.Generic;
using System.Linq;

namespace RaceControl
{
    // AI driver telemetry, mapped live 2026-11-13 (Indy500_2020 and Coronado_2026). Car block = 0xACCA30 + slot * 8724:
    //   +6390 gear (byte, minus 1)       +6394 brake slot (always 0 for the AI)   +6398 throttle pedal (0..1)
    //   +6402 rpm                        +6318 front wheel steer (rad, + = left: the AI's steering, ~0.9 x the kinematic
    //   steer in a steady corner; +6526/+6530 are not the steer, +6526 reads pi/2 on a parked car)
    //   +6622/+6626 position x/y (m)     +6634/+6638 velocity x/y (m/s)
    // NR2003 keeps no brake value for the AI (nothing in the car record or block follows their braking), so the brake is
    // worked out from the car: how hard it slows with its pedal off, less what coasting alone would take off (rolling +
    // aero drag), against 35 mph/s (~1.6 g, the hardest stops seen) = 100 %. Timed on the game clock, so time
    // acceleration and pausing don't change it.
    public struct TeleSample
    {
        public float D;          // metres from the line (timed laps), or game seconds (the recent strip)
        public int Ms;           // ms into the lap
        public float Mph, Thr, Brk, Steer, Rpm;   // Steer: front wheel degrees, + = left
        public float X, Y;       // world position (m): laps are lined up by where on track, not by their own distance
        public sbyte Gear;
    }

    // one lap of one car: samples by distance from the line
    public class TeleLap
    {
        public int Car, No;                     // No: the car's timed lap count in this session (1 = its first)
        public int Start;                       // game ms the lap started (QualTiming's LapStart)
        public int LapMs;                       // 0 = still running
        internal int Match = -1;                // Align: where on the reference the last sample matched
        public readonly List<TeleSample> S = new List<TeleSample>();
        public float Length { get { return S.Count > 0 ? S[S.Count - 1].D : 0; } }
        public bool Done { get { return LapMs > 0; } }

        // the sample index at or just before distance d (binary search; S is in distance order)
        public int IndexAt(float d)
        {
            int lo = 0, hi = S.Count - 1;
            if (hi < 0 || d <= S[0].D) return 0;
            if (d >= S[hi].D) return hi;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (S[mid].D <= d) lo = mid; else hi = mid; }
            return lo;
        }

        // interpolated values at distance d (false past the end of a lap still running)
        public bool At(float d, out TeleSample v)
        {
            v = default(TeleSample);
            if (S.Count == 0 || d < 0 || (!Done && d > Length)) return false;
            int i = IndexAt(d);
            if (i >= S.Count - 1) { v = S[S.Count - 1]; return true; }
            var a = S[i]; var b = S[i + 1];
            float f = b.D > a.D ? Math.Max(0, Math.Min(1, (d - a.D) / (b.D - a.D))) : 0;
            v.D = d; v.Ms = (int)Math.Round(a.Ms + f * (b.Ms - a.Ms));
            v.Mph = a.Mph + f * (b.Mph - a.Mph); v.Thr = a.Thr + f * (b.Thr - a.Thr); v.Brk = a.Brk + f * (b.Brk - a.Brk);
            v.Steer = a.Steer + f * (b.Steer - a.Steer); v.Rpm = a.Rpm + f * (b.Rpm - a.Rpm); v.X = a.X + f * (b.X - a.X); v.Y = a.Y + f * (b.Y - a.Y); v.Gear = f < 0.5f ? a.Gear : b.Gear;
            return true;
        }
    }

    public class TeleCar
    {
        public int Car;
        public readonly List<TeleLap> Laps = new List<TeleLap>();   // finished timed laps, oldest first
        public TeleLap Live;                                         // the timed lap running now (null = none)
        public TeleSample Now; public bool HaveNow;                  // the latest sample
        public readonly Queue<TeleSample> Recent = new Queue<TeleSample>();   // the last ~12 game seconds (D = game s)
        public int Count;                                            // timed laps started this session
        internal DateTime SeenLapAt = DateTime.MinValue;
        internal float PX, PY; internal bool HavePos; internal double Path;
        internal readonly Queue<KeyValuePair<double, float>> Speeds = new Queue<KeyValuePair<double, float>>();
        internal float BrkShown;
        internal float ThrS, SteerS; internal double SmoothT = -1;   // the smoothed throttle / steering and when
        public TeleLap Best { get { TeleLap b = null; foreach (var l in Laps) if (b == null || l.LapMs < b.LapMs) b = l; return b; } }
    }

    public class TeleRecorder
    {
        const long Block0 = 0xACCA30; const int Stride = Addr.Ocd, From = 6318, Len = 324;
        // steering wheel turns per front wheel turn, for the wheel icon / graph: the setup's steering ratio (setup 0xC055C0
        // slot 0x30, the one the in-car wheel uses, WheelAnim.cs) when the game has one, else 14
        public static float SteerRatio = 14f;
        const long SetupSteerRatio = 0xC05680;
        public const double RecentSeconds = 12;
        // the AI flick their throttle and steering tick to tick: the slightest smoothing (game-time constants) takes the
        // jitter out and keeps every real lift, stab and correction (about one tick of lag)
        public const double ThrottleTau = 0.06, SteerTau = 0.045;
        public static float Smooth(float state, float raw, double dt, double tau) { return (float)(state + (raw - state) * (1 - Math.Exp(-dt / tau))); }
        readonly Dictionary<int, TeleCar> cars = new Dictionary<int, TeleCar>();
        public IEnumerable<TeleCar> Cars { get { return cars.Values; } }
        public TeleCar Of(int c) { TeleCar t; return cars.TryGetValue(c, out t) ? t : null; }
        public int Version;                       // bumps when a lap finishes or starts (lists need a refresh)
        public double Now;                        // game seconds of the last sample
        double lastSample = -1;

        public void Reset() { cars.Clear(); lastSample = -1; Version++; }
        internal void Add(TeleCar t) { cars[t.Car] = t; Version++; }      // the offline render (test\TeleLive.cs)

        // the cars to follow and the timing that knows where their timed laps start and end. Call it often (it samples
        // about 30 times a game second at most)
        public void Sample(IGameMemory m, QualTiming timing, IEnumerable<int> slots)
        {
            double now = Addr.GameSeconds(m);
            if (lastSample >= 0 && now < lastSample - 1) Reset();        // the clock went back: a new session
            // paused, or sampled a moment ago. The game clock moves in 1/36 s ticks: every tick is a sample
            if (lastSample >= 0 && now - lastSample < 0.025) return;
            lastSample = now; Now = now;
            try { float sr = m.F32(SetupSteerRatio); if (sr >= 4 && sr <= 40) SteerRatio = sr; } catch (GameMemoryException) { }
            var pm = m as ProcessMemory;
            foreach (int c in slots)
            {
                TeleCar t;
                if (!cars.TryGetValue(c, out t)) cars[c] = t = new TeleCar { Car = c };
                long b = Block0 + c * (long)Stride;
                float thr, steer, rpm, x, y, vx, vy; int gear;
                if (pm != null)
                {
                    byte[] r = pm.Bytes(b + From, Len);
                    gear = r[6390 - From] - 1; thr = BitConverter.ToSingle(r, 6398 - From); rpm = BitConverter.ToSingle(r, 6402 - From);
                    steer = BitConverter.ToSingle(r, 6318 - From) * 57.29578f;
                    x = BitConverter.ToSingle(r, 6622 - From); y = BitConverter.ToSingle(r, 6626 - From);
                    vx = BitConverter.ToSingle(r, 6634 - From); vy = BitConverter.ToSingle(r, 6638 - From);
                }
                else
                {
                    gear = m.U8(b + 6390) - 1; thr = m.F32(b + 6398); rpm = m.F32(b + 6402); steer = m.F32(b + 6318) * 57.29578f;
                    x = m.F32(b + 6622); y = m.F32(b + 6626); vx = m.F32(b + 6634); vy = m.F32(b + 6638);
                }
                float mph = (float)(Math.Sqrt(vx * vx + vy * vy) * 2.2369363);
                if (!(mph >= 0 && mph < 400)) mph = 0;
                thr = float.IsNaN(thr) ? 0 : Math.Max(0, Math.Min(1, thr));
                if (!(Math.Abs(steer) < 90)) steer = 0;
                if (!(rpm >= 0 && rpm < 30000)) rpm = 0;
                double sdt = now - t.SmoothT;
                if (t.SmoothT < 0 || sdt > 0.5 || sdt < 0) { t.ThrS = thr; t.SteerS = steer; }   // first sample, or after a gap
                else { t.ThrS = Smooth(t.ThrS, thr, sdt, ThrottleTau); t.SteerS = Smooth(t.SteerS, steer, sdt, SteerTau); }
                t.SmoothT = now;
                // the brake test uses the raw pedal (the AI never brake and throttle at once)
                var s = new TeleSample { X = x, Y = y, Mph = mph, Thr = t.ThrS, Brk = Brake(t, now, mph, thr), Steer = t.SteerS, Rpm = rpm, Gear = (sbyte)(gear >= -1 && gear <= 9 ? gear : 0) };
                t.Now = s; t.HaveNow = true;
                var rs = s; rs.D = (float)now;
                t.Recent.Enqueue(rs);
                while (t.Recent.Count > 0 && now - t.Recent.Peek().D > RecentSeconds) t.Recent.Dequeue();

                float ds = t.HavePos ? (float)Math.Sqrt((x - t.PX) * (x - t.PX) + (y - t.PY) * (y - t.PY)) : 0f;
                if (!(ds >= 0 && ds < 200)) ds = 0;                         // a jump (reset to the pits): no distance
                t.PX = x; t.PY = y; t.HavePos = true;
                Laps(t, timing.Of(c), now, ds, s);
            }
        }

        // follow QualTiming's timed laps: a lap it closes is finished here with its official time; a new LapStart starts one
        void Laps(TeleCar t, QualTiming.CarT tt, double now, float ds, TeleSample s)
        {
            if (tt == null) { t.Live = null; return; }
            if (t.SeenLapAt != tt.LastLapAt)
            {
                t.SeenLapAt = tt.LastLapAt;
                var l = t.Live;
                // (a lap joined part way round - recording started after its line crossing - is never kept)
                if (l != null && tt.LastLapMs > 0 && l.S.Count > 20 && tt.LastLapAt != DateTime.MinValue && l.S[0].Ms <= 2000)
                {
                    // QualTiming sees the line ~10 times a second: samples past the line belong to the next lap
                    l.LapMs = tt.LastLapMs;
                    int cut = l.S.FindIndex(q => q.Ms > l.LapMs);
                    var carry = new List<TeleSample>();
                    if (cut > 0)
                    {
                        float dLine = LineDistance(l);
                        for (int i = cut; i < l.S.Count; i++) { var q = l.S[i]; q.Ms -= l.LapMs; q.D -= dLine; if (q.D >= 0) carry.Add(q); }
                        l.S.RemoveRange(cut, l.S.Count - cut);
                    }
                    if (l.S.Count > 1)
                    {
                        // close the lap at the line
                        var end = l.S[l.S.Count - 1];
                        float dEnd = LineDistance(l);
                        if (end.D < dEnd) { end.D = dEnd; end.Ms = l.LapMs; l.S.Add(end); }
                        t.Laps.Add(l); Version++;
                        // ~180 KB a lap: keep a car's last 12 (and always its best)
                        while (t.Laps.Count > 12) { var best = t.Best; t.Laps.Remove(t.Laps.First(x => x != best)); }
                    }
                    t.Live = null;
                    if (tt.LapStart >= 0 && tt.LapStart == l.Start + l.LapMs)
                    {
                        t.Live = NewLap(t, tt.LapStart);
                        t.Live.S.AddRange(carry);
                        t.Path = carry.Count > 0 ? carry[carry.Count - 1].D : 0;
                    }
                }
                else t.Live = null;
            }
            if (tt.LapStart < 0) { if (t.Live != null) { t.Live = null; Version++; } return; }
            if (t.Live == null || t.Live.Start != tt.LapStart) { t.Live = NewLap(t, tt.LapStart); t.Path = 0; }
            int ms = (int)Math.Round(now * 1000) - tt.LapStart;
            if (ms < 0) return;
            t.Path += ds;
            var q2 = s; q2.D = (float)t.Path; q2.Ms = ms;
            var L = t.Live.S;
            if (L.Count == 0 || q2.D > L[L.Count - 1].D + 0.05f) L.Add(q2);
        }

        // where the line falls in a lap that ran past it: the distance at its lap time
        static float LineDistance(TeleLap l)
        {
            var S = l.S;
            for (int i = 1; i < S.Count; i++)
                if (S[i].Ms >= l.LapMs)
                {
                    float f = S[i].Ms > S[i - 1].Ms ? (float)(l.LapMs - S[i - 1].Ms) / (S[i].Ms - S[i - 1].Ms) : 1;
                    return S[i - 1].D + f * (S[i].D - S[i - 1].D);
                }
            return l.Length;
        }

        TeleLap NewLap(TeleCar t, int start) { t.Count++; Version++; return new TeleLap { Car = t.Car, No = t.Count, Start = start }; }

        // the brake, from the car's slowing with its pedal off (see the top of the file)
        static float Brake(TeleCar t, double now, float mph, float thr)
        {
            var q = t.Speeds;
            if (q.Count > 0 && now < q.Last().Key) q.Clear();
            if (q.Count == 0 || now > q.Last().Key) q.Enqueue(new KeyValuePair<double, float>(now, mph));
            while (q.Count > 2 && now - q.Peek().Key > 0.6) q.Dequeue();
            if (thr > 0.05f) return t.BrkShown = 0;                     // the AI never brakes and throttles at once
            var from = q.Peek(); var last = q.Last();
            foreach (var p in q) { if (now - p.Key >= 0.25) from = p; else break; }
            double dt = last.Key - from.Key;
            if (dt < 0.1) return t.BrkShown;                              // not enough yet: hold
            double decel = (from.Value - last.Value) / dt;                // mph per second
            double v = (from.Value + last.Value) / 2, coast = 1.5 + 6 * (v / 200) * (v / 200);
            float b = (float)Math.Max(0, Math.Min(1, (decel - coast) / 35));
            if (mph < 2) b = 0;
            return t.BrkShown = t.BrkShown * 0.4f + b * 0.6f;
        }

        // Line a lap up with a reference lap by position: every sample gets the distance of the nearest point of the
        // reference lap's path (searched forward from the last match, like QualTiming's live gap), so the traces share
        // the reference's distance scale and the gap compares the same spot on track whatever line each car took.
        // Returns a copy with D in reference metres (kept in order); `from` re-uses an earlier result for a lap still
        // running (only its new samples are matched).
        public static TeleLap Align(TeleLap lap, TeleLap reference, TeleLap from = null)
        {
            if (lap == null || reference == null || lap == reference || reference.S.Count < 2) return lap;
            var R = reference.S; int n = R.Count;
            var o = from != null && from.S.Count <= lap.S.Count ? from : new TeleLap { Car = lap.Car, No = lap.No, Start = lap.Start };
            o.LapMs = lap.LapMs;
            int match = o.S.Count == 0 ? -1 : o.Match;
            float scale = lap.Length > 0 && reference.Length > 0 && lap.Done ? reference.Length / lap.Length : 1;
            for (int i = o.S.Count; i < lap.S.Count; i++)
            {
                var q = lap.S[i];
                int lo, hi;
                if (match < 0) { lo = 0; hi = Math.Max(1, n / 10); } else { lo = Math.Max(0, match - 8); hi = Math.Min(n - 1, match + 150); }
                int best = -1; double bd = double.MaxValue;
                for (int j = lo; j <= hi; j++) { double dx = R[j].X - q.X, dy = R[j].Y - q.Y, d2 = dx * dx + dy * dy; if (d2 < bd) { bd = d2; best = j; } }
                float d;
                if (best < 0 || bd > 45 * 45) d = q.D * scale;        // off the reference's line (pit lane, a spin): its own distance
                else
                {
                    match = best;
                    // between reference points: project onto the segment ahead (or behind)
                    int j0 = best, j1 = best; double f = 0;
                    if (best + 1 < n) { double fa = Proj(R[best], R[best + 1], q); if (fa >= 0) { j1 = best + 1; f = Math.Min(1, fa); } }
                    if (j0 == j1 && best > 0) { j0 = best - 1; f = Math.Max(0, Math.Min(1, Proj(R[best - 1], R[best], q))); }
                    d = (float)(R[j0].D + f * (R[j1].D - R[j0].D));
                }
                if (o.S.Count > 0) d = Math.Max(d, o.S[o.S.Count - 1].D);   // keep it in order (the search uses it)
                if (i == 0 && d > reference.Length * 0.5f) d = 0;            // just past the line, matched to the end of the reference
                q.D = d; o.S.Add(q);
            }
            o.Match = match;
            return o;
        }

        static double Proj(TeleSample a, TeleSample b, TeleSample p)
        {
            double sx = b.X - a.X, sy = b.Y - a.Y, l2 = sx * sx + sy * sy;
            return l2 < 1e-6 ? 0 : ((p.X - a.X) * sx + (p.Y - a.Y) * sy) / l2;
        }

        // time gained / lost: ms of `aligned` (a lap lined up with Align) minus the reference's at distance d (reference
        // metres); false where either has no data
        public static bool Delta(TeleLap aligned, TeleLap reference, float d, out int ms)
        {
            ms = 0;
            if (aligned == null || reference == null) return false;
            TeleSample a, r;
            if (!aligned.At(d, out a) || !reference.At(d, out r)) return false;
            ms = a.Ms - r.Ms;
            return true;
        }
    }
}
