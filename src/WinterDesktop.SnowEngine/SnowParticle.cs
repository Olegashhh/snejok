using WinterDesktop.Core.Models;

namespace WinterDesktop.SnowEngine;

/// <summary>
/// Одна снежинка. Структура, а не класс: тысячи частиц обновляются каждый кадр,
/// поэтому важны компактность и отсутствие аллокаций (см. ARCHITECTURE §4).
/// </summary>
public struct SnowParticle
{
    public float X;
    public float Y;
    public float PrevX;
    public float PrevY;
    public float Vx;
    public float Vy;

    /// <summary>Базовый радиус в физических пикселях для 96 DPI.</summary>
    public float Radius;

    /// <summary>Множитель прозрачности 0.3..1.</summary>
    public float Alpha;

    /// <summary>Фаза колебаний — делает снежинки несинхронными.</summary>
    public float Phase;

    public float SwayFrequency;
    public float SwayAmplitude;

    /// <summary>Остаток жизни для «брызг» при взаимодействии; &lt;= 0 — обычная снежинка.</summary>
    public float Life;

    public SnowShape Shape;

    /// <summary>true — снежинка летит перед окнами (не оседает на верхних кромках).</summary>
    public bool Foreground;

    public readonly float InterpolatedX(float t) => PrevX + (X - PrevX) * t;
    public readonly float InterpolatedY(float t) => PrevY + (Y - PrevY) * t;
}
