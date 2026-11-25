using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuickRelay.Voice.Audio;

internal static class WinMmPlayer
{
    private const string WinMm = "winmm.dll";
    private const uint DefaultDevice = uint.MaxValue;
    private const int DoneFlag = 1;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    internal static IReadOnlyList<AudioDevice> ListDevices()
    {
        List<AudioDevice> devices = [];
        var count = waveOutGetNumDevs();
        for (var index = 0; index < count; index++)
        {
            if (waveOutGetDevCaps((nuint)index, out var caps, (uint)Marshal.SizeOf<WaveOutCaps>()) == 0)
                devices.Add(new AudioDevice(index, caps.Name));
        }
        return devices.AsReadOnly();
    }

    internal static async Task<bool> PlayAsync(
        WaveClip clip, int? deviceIndex, Func<CancellationToken, ValueTask<bool>> mayStart, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var format = new WaveFormat
        {
            Format = 1,
            Channels = clip.Channels,
            SamplesPerSecond = clip.SampleRate,
            BitsPerSample = clip.BitsPerSample,
            BlockAlign = clip.BlockAlign,
            AverageBytesPerSecond = clip.BytesPerSecond,
        };
        Check(waveOutOpen(out var device, deviceIndex is int index ? (uint)index : DefaultDevice,
            in format, 0, 0, 0), "出力機器を開く");

        nint data = 0;
        nint header = 0;
        var prepared = false;
        var queued = false;
        try
        {
            data = Marshal.AllocHGlobal(clip.Samples.Length);
            Marshal.Copy(clip.Samples, 0, data, clip.Samples.Length);
            header = Marshal.AllocHGlobal((int)HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)clip.Samples.Length }, header, false);
            Check(waveOutPrepareHeader(device, header, HeaderSize), "再生を準備する");
            prepared = true;

            if (!await mayStart(ct).ConfigureAwait(false))
                return false;
            ct.ThrowIfCancellationRequested();
            Check(waveOutWrite(device, header, HeaderSize), "再生を始める");
            queued = true;

            var maxDuration = TimeSpan.FromSeconds((double)clip.Samples.Length / clip.BytesPerSecond + 5);
            var elapsed = Stopwatch.StartNew();
            while (!IsDone(header))
            {
                if (elapsed.Elapsed > maxDuration)
                    throw new AudioPlaybackException("Windowsから音声の再生完了が通知されませんでした。");
                // Cancellation interrupts the wait so the finally block can reset the device immediately.
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return true;
        }
        finally
        {
            if (queued && !IsDone(header))
                _ = waveOutReset(device);
            // A driver that still owns its buffer must never see freed memory.
            var canReleaseBuffer = !prepared || waveOutUnprepareHeader(device, header, HeaderSize) == 0;
            _ = waveOutClose(device);
            if (canReleaseBuffer)
            {
                Marshal.FreeHGlobal(header);
                Marshal.FreeHGlobal(data);
            }
        }
    }

    private static bool IsDone(nint header) => (Marshal.ReadInt32(header, FlagsOffset) & DoneFlag) != 0;

    private static void Check(uint result, string operation)
    {
        if (result != 0)
            throw new AudioPlaybackException($"音声の処理に失敗しました ({operation}, Windows code {result})。");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        internal ushort Format;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtensionSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal nint Data;
        internal uint Length;
        internal uint BytesRecorded;
        internal nuint User;
        internal uint Flags;
        internal uint Loops;
        internal nint Next;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        internal ushort Manufacturer;
        internal ushort Product;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string Name;
        internal uint Formats;
        internal ushort Channels;
        internal ushort Reserved;
        internal uint Support;
    }

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutGetNumDevs();

    [DllImport(WinMm, EntryPoint = "waveOutGetDevCapsW", ExactSpelling = true)]
    private static extern uint waveOutGetDevCaps(nuint deviceIndex, out WaveOutCaps caps, uint size);

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutOpen(out nint device, uint deviceIndex, in WaveFormat format,
        nint callback, nuint instance, uint flags);

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutWrite(nint device, nint header, uint size);

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutReset(nint device);

    [DllImport(WinMm, ExactSpelling = true)]
    private static extern uint waveOutClose(nint device);
}
