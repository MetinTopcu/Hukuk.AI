namespace Hukuk.AI.Retrieval;

// Cache TTL'ine rastgele sapma: aynı anda yazılan kayıtların aynı anda düşmesini (stampede) önler.
public static class Jitter
{
    public static TimeSpan Apply(TimeSpan ttl, double ratio = 0.1) =>
        ttl * (1 + (Random.Shared.NextDouble() * 2 - 1) * ratio);
}
