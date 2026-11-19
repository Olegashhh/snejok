using WinterDesktop.Core.Models;

namespace WinterDesktop.Core.Performance;

/// <summary>
/// Бюджет производительности на текущий кадр. Вычисляется <c>PerformanceManager</c> и
/// потребляется движком снега и рендерером (§3.10).
/// </summary>
public readonly record struct PerformanceBudget(
    int TargetFps,
    int MaxParticles,
    float QualityScale,
    bool EffectsEnabled)
{
    public static PerformanceBudget CreateDefault() => new(60, 2600, 1f, true);

    public static PerformanceBudget ForProfile(PerformanceProfile profile) => profile switch
    {
        PerformanceProfile.Eco => new PerformanceBudget(30, 900, 0.6f, true),
        PerformanceProfile.High => new PerformanceBudget(60, 7000, 1.35f, true),
        _ => new PerformanceBudget(60, 2600, 1f, true),
    };

    public PerformanceBudget WithFpsLimit(FrameRateLimit limit, int monitorHz)
    {
        int fps = limit switch
        {
            FrameRateLimit.Fps30 => 30,
            FrameRateLimit.Fps60 => 60,
            FrameRateLimit.Unlimited => Math.Max(monitorHz, 60),
            _ => Math.Clamp(monitorHz, 30, 60),
        };
        return this with { TargetFps = Math.Max(10, fps) };
    }

    public PerformanceBudget WithParticles(int maxParticles) =>
        maxParticles > 0 ? this with { MaxParticles = maxParticles } : this;

    /// <summary>Понижение качества при просадке кадров. <paramref name="step"/> 0..3.</summary>
    public PerformanceBudget Degrade(int step)
    {
        int clamped = Math.Clamp(step, 0, 3);
        return this with
        {
            MaxParticles = Math.Max(300, (int)(MaxParticles * (1.0 - 0.22 * clamped))),
            QualityScale = MathF.Max(0.5f, QualityScale * (1f - 0.12f * clamped)),
        };
    }
}
