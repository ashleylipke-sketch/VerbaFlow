using NAudio.Wave;

namespace VerbaFlow.Widget;

/// <summary>
/// The Windows default microphone at 16 kHz, 16-bit mono (Windows converts from the microphone's own format). Sound goes
/// straight to the listener and is not kept here. The level (0 to 1) lets the panel say when nothing is heard.
/// </summary>
internal sealed class Microphone : IDisposable
{
    private readonly WaveInEvent _wave;
    private readonly Action<ReadOnlyMemory<byte>> _sound;

    public event Action<float>? Level;

    public Microphone(Action<ReadOnlyMemory<byte>> sound)
    {
        _sound = sound;
        // Device -1 is Windows' default recording device ("wave mapper").
        _wave = new WaveInEvent { DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100, NumberOfBuffers = 4 };
        _wave.DataAvailable += (_, e) =>
        {
            _sound(new ReadOnlyMemory<byte>(e.Buffer, 0, e.BytesRecorded));
            var peak = 0;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
            Level?.Invoke(peak / 32768f);
        };
    }

    /// <summary>Starts recording. Throws a plain sentence when there is no microphone or Windows does not allow it.</summary>
    public void Start()
    {
        try { _wave.StartRecording(); }
        catch (Exception)
        {
            throw new WidgetException("The microphone could not be started. Check that one is connected, and that Windows lets desktop apps use it " +
                "(Start › Settings › Privacy & security › Microphone).");
        }
    }

    public void Dispose()
    {
        try { _wave.StopRecording(); } catch (Exception) { /* already stopped */ }
        _wave.Dispose();
    }
}
