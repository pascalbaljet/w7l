using System;
using System.IO;
using System.Runtime.InteropServices;

namespace KidLauncher
{
    // Soft xylophone notes on a major pentatonic scale, so any run of hovers sounds like a tune.
    // The notes are synthesized once into unmanaged memory: PlaySound reads an asynchronous
    // in-memory sound while it plays, so the garbage collector must not move it.
    internal static class Chimes
    {
        private const int SampleRate = 22050;
        private static readonly int[] Scale = { 0, 2, 4, 7, 9 };
        private static readonly IntPtr[] Notes = new IntPtr[10];

        // Note 0 is C5; each step climbs the pentatonic scale, up to A6.
        public static void Play(int note)
        {
            note = ((note % Notes.Length) + Notes.Length) % Notes.Length;
            if (Notes[note] == IntPtr.Zero)
            {
                var wave = Wave(note);
                var memory = Marshal.AllocHGlobal(wave.Length);
                Marshal.Copy(wave, 0, memory, wave.Length);
                Notes[note] = memory;
            }
            // Without an audio device PlaySound returns false; a missing chime never matters.
            PlaySound(Notes[note], IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT | SND_NOWAIT);
        }

        public static byte[] Wave(int note)
        {
            double frequency = 523.25 * Math.Pow(2, (Scale[note % 5] + 12 * (note / 5)) / 12.0);
            int samples = SampleRate * 55 / 100;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(36 + samples * 2);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(SampleRate); writer.Write(SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(samples * 2);
                for (int i = 0; i < samples; i++)
                {
                    double t = (double)i / SampleRate;
                    // A quick swell avoids a click; the bright octave fades first, like a struck wooden bar.
                    double attack = Math.Min(1, t / 0.004), tail = Math.Min(1, (samples - i) / (SampleRate * 0.03));
                    double tone = Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t / 0.16)
                        + 0.35 * Math.Sin(2 * Math.PI * frequency * 2 * t) * Math.Exp(-t / 0.05)
                        + 0.12 * Math.Sin(2 * Math.PI * frequency * 3 * t) * Math.Exp(-t / 0.025);
                    writer.Write((short)(tone * attack * tail * 0.22 * short.MaxValue));
                }
                return stream.ToArray();
            }
        }

        private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004, SND_NOWAIT = 0x2000;
        [DllImport("winmm.dll", SetLastError = false)] private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
    }
}
