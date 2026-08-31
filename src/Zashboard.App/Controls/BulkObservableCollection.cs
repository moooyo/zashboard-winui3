using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Zashboard.App.Controls;

public sealed partial class BulkObservableCollection<T> : ObservableCollection<T>
{
    public event EventHandler? Resetting;

    public event EventHandler? ResetCompleted;

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        T[] snapshot = items as T[] ?? items.ToArray();

        Resetting?.Invoke(this, EventArgs.Empty);
        try
        {
            Items.Clear();
            foreach (T item in snapshot)
            {
                Items.Add(item);
            }

            RaiseReset();
        }
        finally
        {
            ResetCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        T[] snapshot = items as T[] ?? items.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        Resetting?.Invoke(this, EventArgs.Empty);
        try
        {
            foreach (T item in snapshot)
            {
                Items.Add(item);
            }

            RaiseReset();
        }
        finally
        {
            ResetCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RemoveFirst(int count)
    {
        int removeCount = Math.Clamp(count, 0, Items.Count);
        if (removeCount == 0)
        {
            return;
        }

        ReplaceAll(Items.Skip(removeCount).ToArray());
    }

    public void ApplyDelta(int removeFromStart, IEnumerable<T> addedItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(removeFromStart);
        ArgumentNullException.ThrowIfNull(addedItems);
        T[] added = addedItems as T[] ?? addedItems.ToArray();
        int removeCount = Math.Min(removeFromStart, Items.Count);
        if (removeCount == 0 && added.Length == 0)
        {
            return;
        }

        Resetting?.Invoke(this, EventArgs.Empty);
        try
        {
            if (removeCount > 0)
            {
                T[] retained = Items.Skip(removeCount).ToArray();
                Items.Clear();
                foreach (T item in retained)
                {
                    Items.Add(item);
                }
            }

            foreach (T item in added)
            {
                Items.Add(item);
            }

            RaiseReset();
        }
        finally
        {
            ResetCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

internal static class CollectionBatch
{
    public static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        if (destination is BulkObservableCollection<T> bulk)
        {
            bulk.ReplaceAll(source);
            return;
        }

        destination.Clear();
        foreach (T item in source)
        {
            destination.Add(item);
        }
    }
}

internal static class CollectionSynchronizer
{
    public static void ReconcileByKey<T, TKey>(
        ObservableCollection<T> destination,
        IReadOnlyList<T> source,
        Func<T, TKey> keySelector,
        Action<T, T> updateExisting)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(updateExisting);

        EqualityComparer<TKey> comparer = EqualityComparer<TKey>.Default;
        for (int index = 0; index < source.Count; index++)
        {
            T desired = source[index];
            TKey desiredKey = keySelector(desired);
            if (index < destination.Count &&
                comparer.Equals(keySelector(destination[index]), desiredKey))
            {
                updateExisting(destination[index], desired);
                continue;
            }

            int existingIndex = -1;
            for (int candidate = index + 1; candidate < destination.Count; candidate++)
            {
                if (comparer.Equals(keySelector(destination[candidate]), desiredKey))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                destination.Move(existingIndex, index);
                updateExisting(destination[index], desired);
            }
            else
            {
                destination.Insert(index, desired);
            }
        }

        while (destination.Count > source.Count)
        {
            destination.RemoveAt(destination.Count - 1);
        }
    }
}
