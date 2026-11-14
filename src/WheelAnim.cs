using System;

namespace RaceControl
{
    // The in-car steering wheel / driver's arms animation, turned by the car's real steering.
    //
    // NR2003.exe 0x565CC0 (the cockpit model's per-frame update, called from the render car's vtable 0x587AE0) works out
    // the wheel angle as
    //     wheel = -(ratio() 0x554FB0 x the render state's yaw rate [state+0xBC]), clamped to +-2.79 rad (160 deg)
    // and plays the cockpit 3do's "timeValue" animation at frame (wheel deg + 160.355) x 10. It never looks at the
    // steering: the replay / network car state has none, so it guesses from how fast the car turns. ratio() is the
    // player's setup steering ratio (setup 0xC055C0 slot 0x30) cut towards 7 below 40 mph of the PLAYER's speed (the
    // "low speed steering ratio override", on by default) - so with nobody driving every AI wheel uses 7, and the
    // amount shown depends on speed (yaw rate = steer x speed / wheelbase), not on the steering.
    //
    // The patch: the call at 0x565EF8 goes to a small cave instead. For a car in the render array (0xD95740 +
    // slot x 1004, 48 of them; index = car slot, checked live 2026-11-13) other than slot 0 (the player) it returns
    //     setup steering ratio [0xC05680] x the car's front wheel steer (car block +6318, rad, + = left)
    // and skips the yaw rate multiply; anything else gets the game's own sum. The game's sign flip and +-160 deg clamp
    // follow unchanged. Car block +6318 is the AI's steering (0x51F440 eases it towards its target, rate +6322);
    // in a steady corner it reads ~0.9 x the kinematic steer (wheelbase x yaw rate / speed), live 2026-11-13.
    // Unpatching points the call back at 0x554FB0; the cave is left in place (a frame may be inside it).
    public static class WheelAnim
    {
        const long Site = 0x565EF8;            // E8 rel32 (call 0x554FB0), then D8 8F BC 00 00 00 (fmul [edi+0xBC]), D9 E0
        const long RatioFn = 0x554FB0;
        static readonly byte[] After = { 0xD8, 0x8F, 0xBC, 0x00, 0x00, 0x00, 0xD9, 0xE0 };

        static long cave;                       // per game process
        static int pid = -1;

        // true = the game now uses the real steering (or the original, when on is false); null = not this NR2003.exe
        public static bool? Set(ProcessMemory m, bool on)
        {
            if (m.Process.Id != pid) { pid = m.Process.Id; cave = 0; }
            byte[] cur = m.Bytes(Site, 13);
            if (cur[0] != 0xE8) return null;
            for (int i = 0; i < After.Length; i++) if (cur[5 + i] != After[i]) return null;
            long target = Site + 5 + BitConverter.ToInt32(cur, 1);
            if (!on)
            {
                if (target != RatioFn) m.WriteCode(Site + 1, BitConverter.GetBytes((int)(RatioFn - (Site + 5))));
                return false;
            }
            if (target != RatioFn && target == cave) return true;               // already ours
            if (cave == 0)
            {
                long c = m.Alloc(128);
                if (c == 0) return null;
                byte[] code = Cave(c);
                m.WriteCode(c, code);
                cave = c;
            }
            m.WriteCode(Site + 1, BitConverter.GetBytes((int)(cave - (Site + 5))));
            return true;
        }

        static byte[] Cave(long c)
        {
            var b = new System.Collections.Generic.List<byte>();
            Action<byte[]> e = x => b.AddRange(x);
            Func<long, byte[]> d = v => BitConverter.GetBytes((int)v);
            const int Fallback = 0x38;
            e(new byte[] { 0x8B, 0xC3 });                                      // 00 mov eax, ebx        (the render car)
            e(new byte[] { 0x2D }); e(d(0xD95740));                            // 02 sub eax, 0xD95740
            e(new byte[] { 0x72, Fallback - 0x09 });                           // 07 jb fallback
            e(new byte[] { 0x3D }); e(d(48 * 1004));                           // 09 cmp eax, 48 x 1004
            e(new byte[] { 0x73, Fallback - 0x10 });                           // 0E jae fallback
            e(new byte[] { 0x33, 0xD2 });                                      // 10 xor edx, edx
            e(new byte[] { 0xB9 }); e(d(1004));                                // 12 mov ecx, 1004
            e(new byte[] { 0xF7, 0xF1 });                                      // 17 div ecx            (eax = slot)
            e(new byte[] { 0x85, 0xD2 });                                      // 19 test edx, edx
            e(new byte[] { 0x75, Fallback - 0x1D });                           // 1B jnz fallback
            e(new byte[] { 0x85, 0xC0 });                                      // 1D test eax, eax
            e(new byte[] { 0x74, Fallback - 0x21 });                           // 1F jz fallback        (slot 0, the player)
            e(new byte[] { 0x69, 0xC0 }); e(d(Addr.Ocd));                      // 21 imul eax, eax, 8724
            e(new byte[] { 0xD9, 0x80 }); e(d(0xACCA30 + 6318));               // 27 fld [eax + block +6318]  front steer, rad
            e(new byte[] { 0xD8, 0x0D }); e(d(0xC05680));                      // 2D fmul [setup steering ratio]
            e(new byte[] { 0x83, 0x04, 0x24, 0x06 });                          // 33 add [esp], 6       (skip the fmul)
            e(new byte[] { 0xC3 });                                            // 37 ret
            if (b.Count != Fallback) throw new InvalidOperationException("wheel cave layout");
            e(new byte[] { 0xE8 }); e(d(RatioFn - (c + Fallback + 5)));        // 38 call 0x554FB0      (the game's own sum)
            e(new byte[] { 0xD8, 0x8F, 0xBC, 0x00, 0x00, 0x00 });              // 3D fmul [edi+0xBC]
            e(new byte[] { 0x83, 0x04, 0x24, 0x06 });                          // 43 add [esp], 6
            e(new byte[] { 0xC3 });                                            // 47 ret
            return b.ToArray();
        }
    }
}
