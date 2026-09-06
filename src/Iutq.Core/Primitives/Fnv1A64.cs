namespace Iutq.Core.Primitives;

internal static class Fnv1A64
{
    public static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public static ulong HashString(string name)
    {
        return Hash(System.Text.Encoding.UTF8.GetBytes(name));
    }
}
