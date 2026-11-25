using System.Buffers.Binary;

namespace QuickRelay.Voice.Audio;

internal sealed record WaveClip(ushort Channels, uint SampleRate, ushort BitsPerSample, byte[] Samples)
{
    internal ushort BlockAlign => (ushort)(Channels * (BitsPerSample / 8));
    internal uint BytesPerSecond => SampleRate * BlockAlign;

    internal static WaveClip Parse(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw InvalidWave();

        var length = BinaryPrimitives.ReadUInt32LittleEndian(wav[4..]);
        if (length < 4 || length > wav.Length - 8)
            throw InvalidWave();

        var end = (int)length + 8;
        ReadOnlySpan<byte> format = [];
        ReadOnlySpan<byte> samples = [];
        var offset = 12;
        while (offset <= end - 8)
        {
            var id = wav.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..]);
            var dataStart = offset + 8;
            if (chunkSize > end - dataStart)
                throw InvalidWave();
            var chunk = wav.Slice(dataStart, (int)chunkSize);
            if (id.SequenceEqual("fmt "u8) && format.IsEmpty)
                format = chunk;
            if (id.SequenceEqual("data"u8) && samples.IsEmpty)
                samples = chunk;
            offset = dataStart + (int)chunkSize + ((int)chunkSize & 1);
        }
        if (offset != end || format.Length < 16 || samples.IsEmpty)
            throw InvalidWave();

        var codec = BinaryPrimitives.ReadUInt16LittleEndian(format);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        var bytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
        var align = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        if (codec != 1 || channels is < 1 or > 2 || sampleRate is < 8000 or > 192000
            || bits is not (8 or 16 or 24 or 32) || align != channels * (bits / 8)
            || bytesPerSecond != sampleRate * align || samples.Length % align != 0)
            throw InvalidWave();
        return new WaveClip(channels, sampleRate, bits, samples.ToArray());
    }

    internal static WaveClip CreateSignal(AudioSignal signal, double volume)
    {
        const int sampleRate = 24000;
        // Distinct nonverbal patterns with a short envelope to avoid clicks at each edge.
        (int Start, int Length, int Primary, int Overtone)[] pulses = signal == AudioSignal.Alert
            ? [(0, 90, 690, 1100), (116, 90, 830, 1270), (232, 110, 980, 1490)]
            : [(0, 95, 410, 620), (125, 105, 290, 470)];
        var duration = pulses[^1].Start + pulses[^1].Length;
        var samples = new byte[sampleRate * duration / 1000 * sizeof(short)];
        foreach (var pulse in pulses)
        {
            var start = sampleRate * pulse.Start / 1000;
            var length = sampleRate * pulse.Length / 1000;
            for (var index = 0; index < length; index++)
            {
                var phase = 2 * Math.PI * index / sampleRate;
                var envelope = Math.Min(1, Math.Min(index, length - 1 - index) / (sampleRate * 0.009));
                var value = (0.78 * Math.Sin(phase * pulse.Primary) + 0.22 * Math.Sin(phase * pulse.Overtone))
                    * envelope * volume * short.MaxValue * 0.35;
                BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan((start + index) * sizeof(short)), (short)value);
            }
        }
        return new WaveClip(1, sampleRate, 16, samples);
    }

    private static SpeechEngineException InvalidWave() =>
        new("AivisSpeechから再生できるPCM WAVが返りませんでした。");
}
