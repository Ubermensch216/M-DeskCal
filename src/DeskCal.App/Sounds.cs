using System.IO;
using System.Media;

namespace DeskCal.App;

/// <summary>Short synthesized feedback sounds (no bundled audio assets).</summary>
internal static class Sounds
{
    private static byte[]? _complete, _reminder;

    /// <summary>A soft two-partial "ding" that decays over ~0.3 s.</summary>
    public static void PlayComplete()
    {
        _complete ??= Synthesize(freq1: 988, freq2: 1480, seconds: 0.30);
        Play(_complete);
    }

    /// <summary>A rising three-note chime (about 1 s) for reminders.</summary>
    public static void PlayReminder()
    {
        _reminder ??= Chime([(784.0, 0.00), (1046.5, 0.20), (1318.5, 0.40)], seconds: 1.1);
        Play(_reminder);
    }

    private static void Play(byte[] wav)
    {
        try
        {
            // SoundPlayer.Play() runs on a background thread; the stream must stay readable until then.
            new SoundPlayer(new MemoryStream(wav)).Play();
        }
        catch { /* no audio device: silently skip */ }
    }

    internal static byte[] Synthesize(double freq1, double freq2, double seconds, int rate = 44100)
    {
        int samples = (int)(rate * seconds);
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            double attack = Math.Min(1.0, t / 0.004);              // 4 ms fade-in avoids a click
            double env = attack * Math.Exp(-t * 11);
            double v = 0.65 * Math.Sin(2 * Math.PI * freq1 * t) + 0.35 * Math.Sin(2 * Math.PI * freq2 * t);
            pcm[i] = (short)Math.Round(v * env * 0.45 * short.MaxValue);
        }
        return Wav(pcm, rate);
    }

    /// <summary>Overlapping decaying notes, each starting at its offset in seconds.</summary>
    internal static byte[] Chime((double Freq, double Start)[] notes, double seconds, int rate = 44100)
    {
        int samples = (int)(rate * seconds);
        var mix = new double[samples];
        foreach (var (freq, start) in notes)
            for (int i = (int)(start * rate); i < samples; i++)
            {
                double t = (double)i / rate - start;
                double env = Math.Min(1.0, t / 0.005) * Math.Exp(-t * 4.5);
                mix[i] += (Math.Sin(2 * Math.PI * freq * t) + 0.3 * Math.Sin(2 * Math.PI * freq * 2 * t)) * env;
            }
        var pcm = new short[samples];
        double peak = Math.Max(1e-9, mix.Max(Math.Abs));
        for (int i = 0; i < samples; i++) pcm[i] = (short)Math.Round(mix[i] / peak * 0.5 * short.MaxValue);
        return Wav(pcm, rate);
    }

    private static byte[] Wav(short[] pcm, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
