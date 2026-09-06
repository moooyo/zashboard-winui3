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
    private const int MaximumIncrementalChanges = 64;

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
        if (destination.Count == source.Count &&
            KeysMatchInOrder(destination, source, keySelector, comparer))
        {
            for (int index = 0; index < source.Count; index++)
            {
                updateExisting(destination[index], source[index]);
            }

            return;
        }

        // Link occurrences by index so duplicate keys retain distinct existing objects.
        Dictionary<TKey, int> available = new(destination.Count, comparer);
        int[] nextOccurrence = new int[destination.Count];
        for (int index = destination.Count - 1; index >= 0; index--)
        {
            TKey key = keySelector(destination[index]);
            nextOccurrence[index] = available.TryGetValue(key, out int next) ? next : -1;
            available[key] = index;
        }

        T[] reconciled = new T[source.Count];
        int[] sourceIndices = new int[source.Count];
        bool[] retained = new bool[destination.Count];
        int retainedCount = 0;
        for (int index = 0; index < source.Count; index++)
        {
            T desired = source[index];
            TKey key = keySelector(desired);
            if (available.TryGetValue(key, out int existingIndex))
            {
                if (nextOccurrence[existingIndex] < 0)
                {
                    available.Remove(key);
                }
                else
                {
                    available[key] = nextOccurrence[existingIndex];
                }

                reconciled[index] = destination[existingIndex];
                sourceIndices[index] = existingIndex;
                retained[existingIndex] = true;
                retainedCount++;
            }
            else
            {
                reconciled[index] = desired;
                sourceIndices[index] = ~index;
            }
        }

        List<CollectionChange>? changes = PlanChanges(sourceIndices, retained, retainedCount);
        for (int index = 0; index < reconciled.Length; index++)
        {
            if (sourceIndices[index] >= 0)
            {
                updateExisting(reconciled[index], source[index]);
            }
        }

        if (changes is null)
        {
            CollectionBatch.Replace(destination, reconciled);
            return;
        }

        foreach (CollectionChange change in changes)
        {
            switch (change.Action)
            {
                case NotifyCollectionChangedAction.Remove:
                    destination.RemoveAt(change.Index);
                    break;
                case NotifyCollectionChangedAction.Add:
                    destination.Insert(change.Index, reconciled[change.Index]);
                    break;
                case NotifyCollectionChangedAction.Move:
                    destination.Move(change.OldIndex, change.Index);
                    break;
            }
        }
    }

    private static bool KeysMatchInOrder<T, TKey>(
        ObservableCollection<T> destination,
        IReadOnlyList<T> source,
        Func<T, TKey> keySelector,
        EqualityComparer<TKey> comparer)
    {
        for (int index = 0; index < source.Count; index++)
        {
            if (!comparer.Equals(keySelector(destination[index]), keySelector(source[index])))
            {
                return false;
            }
        }

        return true;
    }

    private static List<CollectionChange>? PlanChanges(
        int[] sourceIndices,
        bool[] retained,
        int retainedCount)
    {
        int structuralChanges = retained.Length - retainedCount + sourceIndices.Length - retainedCount;
        if (structuralChanges > MaximumIncrementalChanges)
        {
            return null;
        }

        // Bound both notifications and backing-array work before changing the live collection.
        // A large reorder or deletion uses one bulk reset, while small edits remain incremental.
        long remainingWork = Math.Max(256L, ((long)retained.Length + sourceIndices.Length) * 4);
        List<CollectionChange> changes = [];
        int count = retained.Length;
        for (int index = retained.Length - 1; index >= 0; index--)
        {
            if (retained[index])
            {
                continue;
            }

            remainingWork -= count - index - 1;
            if (remainingWork < 0)
            {
                return null;
            }

            changes.Add(new(NotifyCollectionChangedAction.Remove, index));
            count--;
        }

        List<int> working = new(Math.Max(retainedCount, sourceIndices.Length));
        for (int index = 0; index < retained.Length; index++)
        {
            if (retained[index])
            {
                working.Add(index);
            }
        }

        for (int index = 0; index < sourceIndices.Length; index++)
        {
            int desired = sourceIndices[index];
            if (index < working.Count && working[index] == desired)
            {
                continue;
            }

            if (changes.Count >= MaximumIncrementalChanges)
            {
                return null;
            }

            if (desired < 0)
            {
                remainingWork -= working.Count - index;
                if (remainingWork < 0)
                {
                    return null;
                }

                changes.Add(new(NotifyCollectionChangedAction.Add, index));
                working.Insert(index, desired);
                continue;
            }

            int existingIndex = index + 1;
            while (working[existingIndex] != desired)
            {
                if (--remainingWork < 0)
                {
                    return null;
                }

                existingIndex++;
            }

            remainingWork -= (long)working.Count * 2 - existingIndex - index - 2;
            if (remainingWork < 0)
            {
                return null;
            }

            changes.Add(new(NotifyCollectionChangedAction.Move, index, existingIndex));
            working.RemoveAt(existingIndex);
            working.Insert(index, desired);
        }

        return changes;
    }

    private readonly record struct CollectionChange(
        NotifyCollectionChangedAction Action,
        int Index,
        int OldIndex = -1);
}
