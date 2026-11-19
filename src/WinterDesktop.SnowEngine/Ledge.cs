using WinterDesktop.Core.Geometry;
using WinterDesktop.Core.Models;

namespace WinterDesktop.SnowEngine;

/// <summary>
/// Кромка, на которой копится снег (§3.2). Снег описывается heightfield-ом: массив толщин
/// с постоянным шагом по X. Это дешёвая модель, которая даёт естественные сугробы после
/// сглаживания и ограничена по памяти (не более <see cref="MaxSamples"/> сэмплов).
/// </summary>
public sealed class Ledge
{
    public const int MinSamples = 6;
    public const int MaxSamples = 512;

    private float[] _visibleSpans = Array.Empty<float>();
    private readonly float _profilePhase1;
    private readonly float _profilePhase2;
    private readonly float _profilePhase3;

    internal Ledge(long key, IntPtr owner, LedgeSide side, bool isDesktopLedge)
    {
        Key = key;
        Owner = owner;
        Side = side;
        IsDesktopLedge = isDesktopLedge;

        // Фазы медленного профиля высоты: сугробы на разных кромках выглядят по-разному.
        ulong hash = unchecked((ulong)key * 0x9E3779B97F4A7C15UL) ^ 0x5DEECE66DUL;
        _profilePhase1 = (hash & 0xFFFF) * (MathF.Tau / 65536f);
        _profilePhase2 = ((hash >> 16) & 0xFFFF) * (MathF.Tau / 65536f);
        _profilePhase3 = ((hash >> 32) & 0xFFFF) * (MathF.Tau / 65536f);
    }

    /// <summary>Устойчивый ключ: (hwnd, сторона) или отрицательный индекс монитора.</summary>
    public long Key { get; }

    public IntPtr Owner { get; }

    public LedgeSide Side { get; }

    /// <summary>Кромка рабочего стола (низ рабочей области монитора) — не привязана к окну.</summary>
    public bool IsDesktopLedge { get; }

    /// <summary>Текущая (сглаженная) позиция кромки в физических пикселях.</summary>
    public float Left { get; internal set; }

    public float Right { get; internal set; }

    /// <summary>Высота линии кромки: снег растёт вверх от неё.</summary>
    public float EdgeY { get; internal set; }

    public float TargetLeft { get; internal set; }

    public float TargetRight { get; internal set; }

    public float TargetEdgeY { get; internal set; }

    /// <summary>Масштаб DPI монитора, на котором находится кромка.</summary>
    public float Scale { get; internal set; } = 1f;

    public float SampleStep { get; internal set; } = 5f;

    public float MaxHeight { get; internal set; }

    /// <summary>
    /// Локальный предел высоты вдоль кромки: три медленные волны (≈400–3100 px), чтобы
    /// насыпанный снег не превращался в идеально ровную плиту. Профиль никогда не
    /// превышает <see cref="MaxHeight"/>, поэтому общий лимит толщины действует.
    /// </summary>
    internal float CapAt(int index)
    {
        if (MaxHeight <= 0f) return 0f;

        float wave = (0.35f * MathF.Sin((index * 0.010f) + _profilePhase1))
                   + (0.40f * MathF.Sin((index * 0.028f) + _profilePhase2))
                   + (0.25f * MathF.Sin((index * 0.075f) + _profilePhase3));
        return MaxHeight * (0.80f + (0.20f * wave));
    }

    /// <summary>
    /// Индивидуальный коэффициент высоты сугроба (0.85…1.0): соседние окна и кромки
    /// накапливают разное количество снега, поэтому сугробы отличаются по размеру,
    /// но не превышают заданный пользователем предел толщины.
    /// </summary>
    public float HeightVariation { get; internal set; } = 1f;

    public int ZIndex { get; internal set; }

    /// <summary>Толщина снега по сэмплам (пиксели).</summary>
    internal float[] Heights = Array.Empty<float>();

    /// <summary>Целевая (накопленная) толщина; высота подтягивается к ней плавно.</summary>
    internal float[] Targets = Array.Empty<float>();

    public ReadOnlySpan<float> HeightSamples => Heights;

    /// <summary>Пары (left, right) интервалов, видимых с учётом z-order окон.</summary>
    public ReadOnlySpan<float> VisibleSpans => _visibleSpans;

    public float Width => MathF.Max(0f, Right - Left);

    public int SampleCount => Heights.Length;

    public void SetVisibleSpans(float[] spans) => _visibleSpans = spans;

    internal void SetTargetGeometry(RectF bounds, float edgeY)
    {
        TargetLeft = bounds.Left;
        TargetRight = bounds.Right;
        TargetEdgeY = edgeY;
    }

    internal void SnapToTarget()
    {
        Left = TargetLeft;
        Right = TargetRight;
        EdgeY = TargetEdgeY;
    }

    internal void EnsureSamples(int count)
    {
        count = Math.Clamp(count, MinSamples, MaxSamples);
        if (Heights.Length == count) return;

        var heights = new float[count];
        var targets = new float[count];
        if (Heights.Length > 0)
        {
            // Пересэмплирование пропорционально: снег сохраняет форму при изменении ширины окна.
            for (int i = 0; i < count; i++)
            {
                int src = (int)((long)i * Heights.Length / count);
                src = Math.Clamp(src, 0, Heights.Length - 1);
                heights[i] = Heights[src];
                targets[i] = Targets[src];
            }
        }

        Heights = heights;
        Targets = targets;
    }

    /// <summary>Индекс сэмпла для X; -1 если X вне кромки.</summary>
    public int IndexAt(float x)
    {
        if (x < Left || x > Right || Heights.Length == 0) return -1;
        int index = (int)((x - Left) / SampleStep);
        return Math.Clamp(index, 0, Heights.Length - 1);
    }

    /// <summary>Толщина снега в точке X (линейная интерполяция между сэмплами).</summary>
    public float HeightAt(float x)
    {
        if (Heights.Length == 0 || x < Left || x > Right) return 0f;
        float relative = (x - Left) / SampleStep;
        int i0 = Math.Clamp((int)relative, 0, Heights.Length - 1);
        int i1 = Math.Min(i0 + 1, Heights.Length - 1);
        float t = Math.Clamp(relative - i0, 0f, 1f);
        return Heights[i0] + (Heights[i1] - Heights[i0]) * t;
    }

    public bool IsVisibleAt(float x)
    {
        var spans = _visibleSpans;
        for (int i = 0; i + 1 < spans.Length; i += 2)
        {
            if (x >= spans[i] && x < spans[i + 1]) return true;
        }
        return false;
    }

    public float SumHeight()
    {
        float sum = 0f;
        for (int i = 0; i < Heights.Length; i++) sum += Heights[i];
        return sum;
    }
}
