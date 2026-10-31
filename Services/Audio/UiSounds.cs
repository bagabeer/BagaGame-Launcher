using System.IO;
using System.Media;

namespace BagaGame.Services.Audio;

/// <summary>
/// Short interface sounds for Big Picture. The WAVs are synthesized at first
/// use and cached in the data folder, so the portable exe stays a single file
/// without binary assets and the user can delete or replace them.
/// </summary>
public static class UiSounds
{
    private const int SampleRate = 44100;

    private static readonly object Gate = new();
    private static SoundPlayer? _enter;
    private static SoundPlayer? _exit;
    private static SoundPlayer? _move;

    public static void PlayEnter(bool enabled) => Play(ref _enter, "enter-v3.wav", BuildEnter, enabled);

    public static void PlayExit(bool enabled) => Play(ref _exit, "exit-v3.wav", BuildExit, enabled);

    /// <summary>A quiet tick while moving between cards.</summary>
    public static void PlayMove(bool enabled) => Play(ref _move, "move-v3.wav", BuildMove, enabled);

    private static void Play(ref SoundPlayer? player, string fileName, Func<double[]> build, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (player is null)
                {
                    var path = EnsureFile(fileName, build);
                    if (path is null)
                    {
                        return;
                    }

                    player = new SoundPlayer(path);
                    player.Load();
                }

                player.Play();
            }
        }
        catch
        {
            // Audio is decoration: a missing device or a locked file must never
            // break the transition.
        }
    }

    private static string? EnsureFile(string fileName, Func<double[]> build)
    {
        try
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "sounds");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 44)
            {
                return path;
            }

            var samples = build();
            WriteWave(path, samples);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteWave(string path, double[] samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);

        var dataSize = samples.Length * 2;

        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + dataSize);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)1);              // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);        // byte rate
        writer.Write((short)2);              // block align
        writer.Write((short)16);             // bits per sample
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(dataSize);

        foreach (var sample in samples)
        {
            var clamped = Math.Max(-1, Math.Min(1, sample));
            writer.Write((short)(clamped * short.MaxValue));
        }
    }

    /// <summary>Two soft button presses, like a controller waking the console.</summary>
    private static double[] BuildEnter()
    {
        var samples = new double[(int)(SampleRate * 0.42)];

        AddClick(samples, 0.00, 640, 0.55);
        AddClick(samples, 0.095, 760, 0.42);

        return Normalize(samples, 0.26);
    }

    /// <summary>A single lower press: the mode closes.</summary>
    private static double[] BuildExit()
    {
        var samples = new double[(int)(SampleRate * 0.30)];

        AddClick(samples, 0.00, 420, 0.5);

        return Normalize(samples, 0.22);
    }

    /// <summary>A very quiet tick for carousel movement.</summary>
    private static double[] BuildMove()
    {
        var samples = new double[(int)(SampleRate * 0.12)];

        AddClick(samples, 0.00, 330, 0.35);

        return Normalize(samples, 0.11);
    }

    /// <summary>
    /// A damped thock: low body, a touch of second harmonic and a short filtered
    /// transient. Soft attack keeps it from sounding like a click track.
    /// </summary>
    private static void AddClick(double[] samples, double at, double frequency, double amplitude)
    {
        var start = (int)(at * SampleRate);
        var length = Math.Min(samples.Length - start, SampleRate / 3);
        var noise = new Random((int)frequency ^ 0x5F3A11);
        double filtered = 0;

        for (var i = 0; i < length; i++)
        {
            var t = (double)i / SampleRate;
            var attack = Math.Min(1, t / 0.006);
            var decay = Math.Exp(-t * 42);

            var body = Math.Sin(2 * Math.PI * frequency * t) * 0.72 +
                       Math.Sin(2 * Math.PI * frequency * 2.02 * t) * 0.28;

            // Muted transient instead of a bright digital click.
            filtered += 0.22 * ((noise.NextDouble() * 2 - 1) - filtered);
            var transient = filtered * Math.Exp(-t * 230) * 0.5;

            samples[start + i] += (body * decay + transient) * attack * amplitude;
        }
    }

    private static double[] Normalize(double[] samples, double peak)
    {
        var max = 0.0;
        foreach (var sample in samples)
        {
            max = Math.Max(max, Math.Abs(sample));
        }

        if (max <= 0.0001)
        {
            return samples;
        }

        var gain = peak / max;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }

        return samples;
    }
}
