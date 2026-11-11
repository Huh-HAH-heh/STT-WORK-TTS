using NAudio.Wave;

namespace STT.WorkTTS.Audio;

public static class AudioPlayer
{
    public static async Task PlayWavAsync(string path, CancellationToken cancellationToken = default)
    {
        using var reader = new AudioFileReader(path);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        while (output.PlaybackState == PlaybackState.Playing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(40, cancellationToken);
        }
    }
}
