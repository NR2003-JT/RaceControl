using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RaceControl
{
    public class GameMemoryException : Exception
    {
        public GameMemoryException(string msg) : base(msg) { }
    }

    // Typed access to NR2003's memory. Small integers and bytes are unsigned, as Cheat Engine reads them.
    public interface IGameMemory
    {
        byte U8(long a);
        int U16(long a);
        int I32(long a);
        uint U32(long a);
        float F32(long a);
        double F64(long a);
        string Str(long a, int max);   // ASCII, up to the first zero byte
        void W8(long a, byte v);
        void W16(long a, int v);
        void W32(long a, int v);
    }

    public class ProcessMemory : IGameMemory, IDisposable
    {
        const uint PROCESS_CREATE_THREAD = 0x0002, PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020,
            PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr done);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr done);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, IntPtr id);
        [DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);

        readonly IntPtr handle;
        readonly byte[] buf = new byte[8];
        public readonly Process Process;

        ProcessMemory(Process p, IntPtr h) { Process = p; handle = h; }

        // Null when NR2003 is not running; throws when it is but cannot be opened (not elevated).
        public static ProcessMemory Attach()
        {
            Process[] found = Process.GetProcessesByName("NR2003");
            if (found.Length == 0) return null;
            Process p = found[0];
            IntPtr h = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero)
                throw new GameMemoryException("cannot open NR2003.exe (error " + Marshal.GetLastWin32Error() + "); run Race Control as administrator");
            return new ProcessMemory(p, h);
        }

        public bool Alive
        {
            get { try { return !Process.HasExited; } catch { return false; } }
        }

        byte[] Read(long a, int n)
        {
            IntPtr done;
            if (!ReadProcessMemory(handle, new IntPtr(a), buf, n, out done) || done.ToInt64() != n)
                throw new GameMemoryException(string.Format("read failed at 0x{0:X}", a));
            return buf;
        }

        void Write(long a, byte[] data)
        {
            IntPtr done;
            if (!WriteProcessMemory(handle, new IntPtr(a), data, data.Length, out done))
                throw new GameMemoryException(string.Format("write failed at 0x{0:X}", a));
        }

        // n bytes in one read (a copy): for the telemetry, a car's driver inputs, position and velocity together
        public byte[] Bytes(long a, int n)
        {
            var r = new byte[n]; IntPtr done;
            if (!ReadProcessMemory(handle, new IntPtr(a), r, n, out done) || done.ToInt64() != n)
                throw new GameMemoryException(string.Format("read failed at 0x{0:X}", a));
            return r;
        }
        public byte U8(long a) { return Read(a, 1)[0]; }
        public int U16(long a) { return BitConverter.ToUInt16(Read(a, 2), 0); }
        public int I32(long a) { return BitConverter.ToInt32(Read(a, 4), 0); }
        public uint U32(long a) { return BitConverter.ToUInt32(Read(a, 4), 0); }
        public float F32(long a) { return BitConverter.ToSingle(Read(a, 4), 0); }
        public double F64(long a) { return BitConverter.ToDouble(Read(a, 8), 0); }
        public string Str(long a, int max)
        {
            var b = new byte[max];
            IntPtr done;
            if (!ReadProcessMemory(handle, new IntPtr(a), b, max, out done)) throw new GameMemoryException(string.Format("read failed at 0x{0:X}", a));
            int n = Array.IndexOf(b, (byte)0);
            return System.Text.Encoding.ASCII.GetString(b, 0, n < 0 ? max : n);
        }
        public void W8(long a, byte v) { Write(a, new byte[] { v }); }
        public void W16(long a, int v) { Write(a, BitConverter.GetBytes((ushort)v)); }
        public void W32(long a, int v) { Write(a, BitConverter.GetBytes(v)); }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, UIntPtr size, uint protect, out uint old);
        [DllImport("kernel32.dll")]
        static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, UIntPtr size);

        // n bytes of executable memory in the game (for a code cave); 0 when it could not be had
        public long Alloc(int n)
        {
            IntPtr p = VirtualAllocEx(handle, IntPtr.Zero, new UIntPtr((uint)n), 0x3000, 0x40);   // commit+reserve, RWX
            return p.ToInt64();
        }

        // writes over the game's code (made writable for the write, then put back) and flushes it
        public void WriteCode(long a, byte[] data)
        {
            uint old;
            bool prot = VirtualProtectEx(handle, new IntPtr(a), new UIntPtr((uint)data.Length), 0x40, out old);
            try { Write(a, data); }
            finally
            {
                if (prot) VirtualProtectEx(handle, new IntPtr(a), new UIntPtr((uint)data.Length), old, out old);
                FlushInstructionCache(handle, new IntPtr(a), new UIntPtr((uint)data.Length));
            }
        }

        IntPtr stub;

        // Runs one of NR2003's own functions (no arguments, cdecl) on a thread in the game: a small stub
        // "mov eax, fn; call eax; ret 4" in memory allocated in the game. False when it could not be run.
        public bool CallGame(long fn)
        {
            if (stub == IntPtr.Zero) stub = VirtualAllocEx(handle, IntPtr.Zero, new UIntPtr(64), 0x3000, 0x40); // commit+reserve, RWX
            if (stub == IntPtr.Zero) return false;
            byte[] code = { 0xB8, 0, 0, 0, 0, 0xFF, 0xD0, 0xC2, 0x04, 0x00 };
            BitConverter.GetBytes((int)fn).CopyTo(code, 1);
            IntPtr done;
            if (!WriteProcessMemory(handle, stub, code, code.Length, out done)) return false;
            IntPtr t = CreateRemoteThread(handle, IntPtr.Zero, UIntPtr.Zero, stub, IntPtr.Zero, 0, IntPtr.Zero);
            if (t == IntPtr.Zero) return false;
            WaitForSingleObject(t, 2000);
            CloseHandle(t);
            return true;
        }

        public void Dispose() { CloseHandle(handle); }
    }
}
