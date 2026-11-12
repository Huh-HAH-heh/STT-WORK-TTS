namespace STT.WorkTTS.Processing;

public sealed class IntentCommand
{
    public string Action { get; init; } = "unknown";
    public IntentArguments Arguments { get; init; } = new();
    public string? Clarification { get; init; }
}

public sealed class IntentArguments
{
    public int? DurationSeconds { get; init; }
    public string? Label { get; init; }
    public string? TimerId { get; init; }
    public string? AppId { get; init; }
    public string? Url { get; init; }
    public int? VolumePercent { get; init; }
    public int? VolumeDelta { get; init; }
}
