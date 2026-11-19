namespace WinterDesktop.SnowEngine;

/// <summary>
/// Детерминированный генератор случайных чисел (xorshift32). Нужен для воспроизводимых
/// тестов физики: без него «поведение снега» нельзя проверить автоматически.
/// </summary>
public sealed class SnowRandom
{
    private uint _state;

    public SnowRandom(int seed = 12345)
    {
        _state = seed == 0 ? 0x9E3779B9u : unchecked((uint)seed);
    }

    public uint NextUInt()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>0..1 включительно-исключительно.</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    public float NextRange(float min, float max) => min + (max - min) * NextFloat();

    public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

    public bool Chance(float probability) => probability > 0f && NextFloat() < probability;
}
