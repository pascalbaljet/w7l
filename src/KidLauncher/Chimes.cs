using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace KidLauncher
{
    // Soft xylophone notes on a major pentatonic scale, so any run of hovers sounds like a tune.
    // Each note plays on its own waveOut voice, which Windows mixes, so a quick sweep lets every
    // note ring out instead of cutting off the one before it (PlaySound plays one sound at a time).
    internal static class Chimes
    {
        private const int SampleRate = 22050, HeaderBytes = 44, MaxVoices = 8;
        private static readonly int[] Scale = { 0, 2, 4, 7, 9 };
        private static readonly byte[][] Notes = new byte[10][];
        private static readonly List<Voice> Voices = new List<Voice>();
        private static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHeader));

        private sealed class Voice { public IntPtr Device, Header, Data; }

        // Voices still ringing. Called on the UI thread only.
        public static int Playing { get { Reap(); return Voices.Count; } }

        // Note 0 is C5; each step climbs the pentatonic scale, up to A6. Returns false without an audio device.
        public static bool Play(int note)
        {
            note = ((note % Notes.Length) + Notes.Length) % Notes.Length;
            Reap();
            if (Voices.Count >= MaxVoices) { Release(Voices[0]); Voices.RemoveAt(0); }
            var wave = Notes[note] ?? (Notes[note] = Wave(note));
            var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = SampleRate, AvgBytesPerSec = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16 };
            IntPtr device;
            if (waveOutOpen(out device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) return false;
            var voice = new Voice { Device = device, Data = Marshal.AllocHGlobal(wave.Length - HeaderBytes), Header = Marshal.AllocHGlobal(HeaderSize) };
            Marshal.Copy(wave, HeaderBytes, voice.Data, wave.Length - HeaderBytes);
            Marshal.StructureToPtr(new WaveHeader { Data = voice.Data, BufferLength = (uint)(wave.Length - HeaderBytes) }, voice.Header, false);
            if (waveOutPrepareHeader(device, voice.Header, HeaderSize) != 0 || waveOutWrite(device, voice.Header, HeaderSize) != 0)
            {
                Release(voice);
                return false;
            }
            Voices.Add(voice);
            return true;
        }

        private static void Reap()
        {
            for (int i = Voices.Count - 1; i >= 0; i--)
                if ((((WaveHeader)Marshal.PtrToStructure(Voices[i].Header, typeof(WaveHeader))).Flags & WhdrDone) != 0)
                {
                    Release(Voices[i]);
                    Voices.RemoveAt(i);
                }
        }

        private static void Release(Voice voice)
        {
            // Reset returns an unfinished buffer, so the header can be unprepared and the memory freed safely.
            waveOutReset(voice.Device);
            waveOutUnprepareHeader(voice.Device, voice.Header, HeaderSize);
            waveOutClose(voice.Device);
            Marshal.FreeHGlobal(voice.Header);
            Marshal.FreeHGlobal(voice.Data);
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

        private const uint WaveMapper = 0xFFFFFFFF, WhdrDone = 1;
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat { public ushort FormatTag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, Size; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader { public IntPtr Data; public uint BufferLength, BytesRecorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr device);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr device);
    }
}
