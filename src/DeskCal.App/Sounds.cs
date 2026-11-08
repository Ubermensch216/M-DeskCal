using System.IO;
using System.Media;

namespace DeskCal.App;

/// <summary>Short synthesized feedback sounds (no bundled audio assets).</summary>
internal static class Sounds
{
    private static byte[]? _complete;

    /// <summary>A soft two-partial "ding" that decays over ~0.3 s.</summary>
    public static void PlayComplete()
    {
        try
        {
            _complete ??= Synthesize(freq1: 988, freq2: 1480, seconds: 0.30);
            // SoundPlayer.Play() runs on a background thread; the stream must stay readable until then.
            var player = new SoundPlayer(new MemoryStream(_complete));
            player.Play();
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

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
