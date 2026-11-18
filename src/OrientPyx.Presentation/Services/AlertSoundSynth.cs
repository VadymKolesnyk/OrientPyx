using System.Buffers.Binary;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Builds the finish alert sounds as 16-bit mono PCM WAV files in memory — no audio assets in the repo. The two
/// sounds differ in rhythm and timbre, not just pitch, so a judge who is not looking at the screen tells them apart
/// by ear (and over the noise at the finish):
/// <list type="bullet">
/// <item><see cref="FinishAlert.CollectChip"/> — a calm rising two-note chime («дзінь-дон»), bell-like decay: take the
/// chip from the runner, no need to come to the computer.</item>
/// <item><see cref="FinishAlert.UnknownChip"/> — three short sharp beeps on one note: come to the computer, a chip
/// waits for its holder.</item>
/// </list>
/// </summary>
public static class AlertSoundSynth
{
    /// <summary>Bumped whenever a sound changes, so a WAV cached on disk by an older build is not reused.</summary>
    public const int Version = 1;

    public const int SampleRate = 44_100;

    private const double Peak = 0.8;

    /// <summary>The sound as a complete WAV file (RIFF header + PCM data).</summary>
    public static byte[] Wav(FinishAlert sound) => ToWav(Samples(sound));

    /// <summary>How long a WAV built by <see cref="Wav"/> plays.</summary>
    public static TimeSpan Duration(byte[] wav)
        => TimeSpan.FromSeconds((double)Math.Max(0, wav.Length - 44) / 2 / SampleRate);

    private static double[] Samples(FinishAlert sound) => sound switch
    {
        FinishAlert.UnknownChip => Beeps(frequency: 1320, count: 3, beepSeconds: 0.11, gapSeconds: 0.07),
        _ => Chime(),
    };

    // Two bell notes a fourth apart (G5 → C6), the second starting while the first still rings.
    private static double[] Chime()
    {
        var buffer = new double[(int)(SampleRate * 0.9)];
        AddBell(buffer, frequency: 784, startSeconds: 0, lengthSeconds: 0.45);
        AddBell(buffer, frequency: 1047, startSeconds: 0.24, lengthSeconds: 0.66);
        return Normalize(buffer);
    }

    // A bell: the fundamental plus two soft overtones, a 5 ms attack and an exponential decay.
    private static void AddBell(double[] buffer, double frequency, double startSeconds, double lengthSeconds)
    {
        var start = (int)(startSeconds * SampleRate);
        var length = Math.Min((int)(lengthSeconds * SampleRate), buffer.Length - start);
        for (var i = 0; i < length; i++)
        {
            var t = (double)i / SampleRate;
            var envelope = Math.Min(1, t / 0.005) * Math.Exp(-t / 0.16) * FadeOut(i, length);
            var phase = 2 * Math.PI * frequency * t;
            buffer[start + i] += envelope * (Math.Sin(phase) + 0.35 * Math.Sin(2 * phase) + 0.12 * Math.Sin(3 * phase));
        }
    }

    // Flat beeps with a hint of odd harmonics (a softened square wave) — sharper than the chime.
    private static double[] Beeps(double frequency, int count, double beepSeconds, double gapSeconds)
    {
        var beep = (int)(beepSeconds * SampleRate);
        var gap = (int)(gapSeconds * SampleRate);
        var buffer = new double[count * beep + (count - 1) * gap];
        var ramp = (int)(0.005 * SampleRate);
        for (var n = 0; n < count; n++)
        {
            var start = n * (beep + gap);
            for (var i = 0; i < beep; i++)
            {
                var t = (double)i / SampleRate;
                var envelope = Math.Min(1, Math.Min(i, beep - 1 - i) / (double)ramp);
                var phase = 2 * Math.PI * frequency * t;
                buffer[start + i] = envelope * (Math.Sin(phase) + Math.Sin(3 * phase) / 4 + Math.Sin(5 * phase) / 10);
            }
        }

        return Normalize(buffer);
    }

    // The last 10 ms of a note fade to zero, so a note cut by the buffer's end does not click.
    private static double FadeOut(int i, int length)
    {
        var tail = (int)(0.01 * SampleRate);
        var left = length - 1 - i;
        return left >= tail ? 1 : (double)left / tail;
    }

    private static double[] Normalize(double[] buffer)
    {
        var max = buffer.Max(Math.Abs);
        if (max > 0)
        {
            for (var i = 0; i < buffer.Length; i++)
                buffer[i] = buffer[i] / max * Peak;
        }

        return buffer;
    }

    private static byte[] ToWav(double[] samples)
    {
        const int headerSize = 44;
        const short channels = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channels * bitsPerSample / 8;
        var dataSize = samples.Length * blockAlign;
        var wav = new byte[headerSize + dataSize];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], headerSize - 8 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16); // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);

        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(samples[i], -1, 1) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(span[(headerSize + i * blockAlign)..], value);
        }

        return wav;
    }
}
