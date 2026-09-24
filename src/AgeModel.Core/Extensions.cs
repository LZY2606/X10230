namespace AgeModel.Core;

internal static class CollectionExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key)
        where TValue : new()
    {
        if (!dict.TryGetValue(key, out var value))
        {
            value = new TValue();
            dict[key] = value;
        }
        return value;
    }
}
