namespace Forecasting.App;

public static class CollectionHelpers
{
    public static List<T> DeduplicateByKeyKeepLast<T, TKey>(
        IReadOnlyList<T> rows,
        Func<T, TKey> keySelector,
        out int droppedRows,
        IComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        if (rows.Count == 0)
        {
            droppedRows = 0;
            return [];
        }

        var deduplicated = rows
            .GroupBy(keySelector)
            .Select(group => group.Last())
            .OrderBy(keySelector, comparer ?? Comparer<TKey>.Default)
            .ToList();

        droppedRows = rows.Count - deduplicated.Count;
        return deduplicated;
    }
}
