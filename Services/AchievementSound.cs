using System;
using System.IO;
using System.Media;

namespace BagaGame.Services;

/// <summary>
/// Short two-note chime for achievement toasts. The wave is generated in
/// memory, so the portable executable does not need an external audio file
/// and the sound cannot be lost when the launcher is copied.
/// </summary>
public static class AchievementSound
{
    private static readonly object Gate = new();
    private static SoundPlayer? _player;

    public static void Play()
    {
        try
        {
            lock (Gate)
            {
                _player ??= CreatePlayer();
                _player.Play();
            }
        }
        catch
        {
            // No audio device, or notifications are muted: keep the toast.
        }
    }

    private static SoundPlayer CreatePlayer()
    {
        var player = new SoundPlayer(new MemoryStream(CreateWave()));
        player.Load();
        return player;
    }

    /// <summary>
    /// A soft A5 -> E6 chime, about 0.45 seconds, mono 16-bit PCM.
    /// </summary>
    private static byte[] CreateWave()
    {
        const int sampleRate = 44100;
        const double duration = 0.46;
        var sampleCount = (int)(sampleRate * duration);
        var data = new byte[sampleCount * sizeof(short)];

        for (var index = 0; index < sampleCount; index++)
        {
            var time = (double)index / sampleRate;

            var first = Tone(time, 0.0, 880.0, 0.18, 0.010, 0.16);
            var second = Tone(time, 0.13, 1318.5, 0.33, 0.010, 0.20);
            var value = (first * 0.72 + second * 0.62) * 0.36;

            if (value > 0.95)
            {
                value = 0.95;
            }
            else if (value < -0.95)
            {
                value = -0.95;
            }

            var sample = (short)(value * short.MaxValue);
            data[index * 2] = (byte)(sample & 0xFF);
            data[index * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        using var memory = new MemoryStream(44 + data.Length);
        using var writer = new BinaryWriter(memory);

        writer.Write(new[] { 'R', 'I', 'F', 'F' });
        writer.Write(36 + data.Length);
        writer.Write(new[] { 'W', 'A', 'V', 'E' });
        writer.Write(new[] { 'f', 'm', 't', ' ' });
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(new[] { 'd', 'a', 't', 'a' });
        writer.Write(data.Length);
        writer.Write(data);

        return memory.ToArray();
    }

    private static double Tone(
        double time,
        double start,
        double frequency,
        double duration,
        double attack,
        double decay)
    {
        var local = time - start;
        if (local < 0 || local > duration)
        {
            return 0;
        }

        var envelope = Math.Min(1.0, local / attack) * Math.Exp(-local / decay);

        // A quiet octave harmonic gives the ding a little body without making
        // it sound like a system beep.
        return (Math.Sin(2 * Math.PI * frequency * local) +
                0.22 * Math.Sin(4 * Math.PI * frequency * local)) * envelope;
    }
}
