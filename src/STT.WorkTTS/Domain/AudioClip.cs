namespace STT.WorkTTS.Domain;

public sealed record AudioClip(
    byte[] Pcm16,
    int SampleRate,
    DateTime CapturedAt,
    string? SavedPath = null)
{
    public double DurationSeconds => Pcm16.Length / (double)(SampleRate * 2);
}
