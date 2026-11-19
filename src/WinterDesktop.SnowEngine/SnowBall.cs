namespace WinterDesktop.SnowEngine;

/// <summary>
/// Летящий снежок. Снежок рождается из сугроба, некоторое время висит в руке
/// (пока зажата левая кнопка), затем летит с гравитацией и ветром и разбивается
/// о кромку или стекло окна. Координаты — физические пиксели рабочего стола.
/// </summary>
public sealed class SnowBall
{
    public float X;
    public float Y;
    public float PrevX;
    public float PrevY;
    public float Vx;
    public float Vy;

    /// <summary>Радиус в физических пикселях (уже с учётом DPI монитора).</summary>
    public float Radius = 15f;

    /// <summary>Масштаб монитора, на котором слеплен снежок.</summary>
    public float Scale = 1f;

    /// <summary>Время жизни: защита от «вечно летящих» снежков.</summary>
    public float Age;

    /// <summary>Визуальное вращение (не влияет на физику).</summary>
    public float Spin;

    /// <summary>true — снежок ещё в руке и следует за курсором.</summary>
    public bool Held;
}
