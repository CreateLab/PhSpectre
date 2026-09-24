namespace PhSpectre.Rendering;

// Temporary diagnostic hook for the "why is Recipe rendering slow on mobile" investigation —
// lets a renderer report fine-grained stage timings without changing its public return shape.
// A subscriber (MainViewModel, routing into AppLogger and the on-screen perf overlay) can
// listen; left null everywhere else (Desktop, CLI, tests), where this is simply never called.
// Remove once the investigation is resolved.
public static class RenderPerfLog
{
    public static Action<string, long>? OnStage { get; set; }
}
