namespace PhSpectre.Rendering;

// Temporary hook for stage timings while investigating slow Recipe rendering on phones.
public static class RenderPerfLog
{
    public static Action<string, long>? OnStage { get; set; }
}
