using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Zashboard.App.Controls;

namespace Zashboard.App.Logic.Tests;

[TestClass]
public sealed class CollectionSynchronizerTests
{
    [TestMethod]
    public void StableKeysUpdateExistingObjectsWithoutCollectionEvents()
    {
        Row[] original = CreateRows(8);
        BulkObservableCollection<Row> destination = CreateCollection(original);
        List<NotifyCollectionChangedAction> actions = CaptureActions(destination);
        Row[] source = CreateRows(8, "updated");

        Reconcile(destination, source);

        Assert.IsEmpty(actions);
        for (int index = 0; index < original.Length; index++)
        {
            Assert.AreSame(original[index], destination[index]);
            Assert.AreEqual("updated", destination[index].Value);
            Assert.AreEqual(1, destination[index].UpdateCount);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    [DataRow(7)]
    public void RemovingOneRowDoesNotMoveSurvivors(int removedIndex)
    {
        Row[] original = CreateRows(8);
        BulkObservableCollection<Row> destination = CreateCollection(original);
        List<NotifyCollectionChangedAction> actions = CaptureActions(destination);
        Row[] source = CreateRows(8, "updated")
            .Where(row => row.Key != removedIndex)
            .ToArray();

        Reconcile(destination, source);

        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Remove }, actions);
        CollectionAssert.AreEqual(original.Where(row => row.Key != removedIndex).ToArray(), destination);
        Assert.IsTrue(destination.All(row => row.Value == "updated" && row.UpdateCount == 1));
    }

    [TestMethod]
    public void SmallInsertDeleteAndReorderRemainIncremental()
    {
        Row[] original = CreateRows(4);
        BulkObservableCollection<Row> destination = CreateCollection(original);
        List<NotifyCollectionChangedAction> actions = CaptureActions(destination);
        Row inserted = new(8, "new");

        Reconcile(destination, [new(2, "updated"), inserted, new(0, "updated"), new(3, "updated")]);

        int[] expectedKeys = [2, 8, 0, 3];
        CollectionAssert.AreEqual(expectedKeys, destination.Select(row => row.Key).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            NotifyCollectionChangedAction.Remove,
            NotifyCollectionChangedAction.Move,
            NotifyCollectionChangedAction.Add,
        }, actions);
        Assert.AreSame(original[2], destination[0]);
        Assert.AreSame(inserted, destination[1]);
        Assert.AreSame(original[0], destination[2]);
        Assert.AreSame(original[3], destination[3]);
        Assert.AreEqual(0, inserted.UpdateCount);
    }

    [TestMethod]
    public void LargeReorderUsesOneResetAndPreservesSelectedObjectIdentity()
    {
        Row[] original = CreateRows(10_000);
        BulkObservableCollection<Row> destination = CreateCollection(original);
        Row selected = original[317];
        List<string> events = [];
        destination.Resetting += (_, _) =>
        {
            Assert.AreSame(selected, destination[317]);
            events.Add("resetting");
        };
        destination.CollectionChanged += (_, args) => events.Add(args.Action.ToString());
        destination.ResetCompleted += (_, _) =>
        {
            Assert.AreSame(selected, destination[^318]);
            events.Add("completed");
        };
        int keyReads = 0;

        CollectionSynchronizer.ReconcileByKey(
            destination,
            CreateRows(10_000, "updated").Reverse().ToArray(),
            row =>
            {
                keyReads++;
                return row.Key;
            },
            static (existing, updated) => existing.UpdateFrom(updated));

        string[] expectedEvents = ["resetting", "Reset", "completed"];
        CollectionAssert.AreEqual(expectedEvents, events);
        CollectionAssert.AreEqual(original.Reverse().ToArray(), destination);
        Assert.IsTrue(destination.All(row => row.Value == "updated" && row.UpdateCount == 1));
        Assert.IsLessThanOrEqualTo(2 * original.Length + 2, keyReads);
    }

    [TestMethod]
    public void LargeRemovalUsesOneResetAndKeepsSurvivors()
    {
        Row[] original = CreateRows(10_000);
        BulkObservableCollection<Row> destination = CreateCollection(original);
        List<NotifyCollectionChangedAction> actions = CaptureActions(destination);
        Row[] source = CreateRows(10_000, "updated").Where(row => row.Key % 2 == 0).ToArray();

        Reconcile(destination, source);

        CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, actions);
        CollectionAssert.AreEqual(original.Where(row => row.Key % 2 == 0).ToArray(), destination);
        Assert.IsTrue(destination.All(row => row.Value == "updated" && row.UpdateCount == 1));
    }

    [TestMethod]
    public void DuplicateKeysRetainDistinctOccurrencesWithoutAliasing()
    {
        Row first = new(1, "first");
        Row second = new(1, "second");
        Row last = new(2, "last");
        BulkObservableCollection<Row> destination = CreateCollection([first, second, last]);
        Row added = new(1, "added");

        Reconcile(destination, [new(2, "moved"), new(1, "first updated"), new(1, "second updated"), added]);

        CollectionAssert.AreEqual(new[] { last, first, second, added }, destination);
        Assert.AreEqual("first updated", first.Value);
        Assert.AreEqual("second updated", second.Value);
        Assert.AreEqual(0, added.UpdateCount);

        Reconcile(destination, [new(1, "remaining"), new(2, "remaining")]);

        CollectionAssert.AreEqual(new[] { first, last }, destination);
        Assert.AreEqual("remaining", first.Value);
    }

    [TestMethod]
    public void EmptyAndPlainCollectionTransitionsPreserveTheRequestedContents()
    {
        BulkObservableCollection<Row> empty = [];
        List<NotifyCollectionChangedAction> actions = CaptureActions(empty);
        Reconcile(empty, []);
        Assert.IsEmpty(actions);

        Row[] source = CreateRows(100);
        Reconcile(empty, source);
        CollectionAssert.AreEqual(source, empty);
        Reconcile(empty, []);
        Assert.IsEmpty(empty);
        CollectionAssert.AreEqual(new[]
        {
            NotifyCollectionChangedAction.Reset,
            NotifyCollectionChangedAction.Reset,
        }, actions);

        ObservableCollection<Row> plain = new(source);
        Reconcile(plain, CreateRows(100, "updated").Reverse().ToArray());
        CollectionAssert.AreEqual(source.Reverse().ToArray(), plain);
        Assert.IsTrue(plain.All(row => row.Value == "updated"));
    }

    [TestMethod]
    [DataRow(20)]
    [DataRow(250)]
    public void RepeatedMixedChangesKeepTheRequestedOrderAndSurvivingInstances(int keyCount)
    {
        Random random = new(7919);
        BulkObservableCollection<Row> destination = CreateCollection(CreateRows(keyCount * 4 / 5));
        for (int iteration = 0; iteration < 100; iteration++)
        {
            Dictionary<int, Row> previous = destination.ToDictionary(row => row.Key);
            Row[] source = Enumerable.Range(0, keyCount)
                .Where(_ => random.Next(4) != 0)
                .OrderBy(_ => random.Next())
                .Select(key => new Row(key, iteration.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToArray();

            Reconcile(destination, source);

            CollectionAssert.AreEqual(source.Select(row => row.Key).ToArray(), destination.Select(row => row.Key).ToArray());
            for (int index = 0; index < source.Length; index++)
            {
                Row expected = previous.GetValueOrDefault(source[index].Key, source[index]);
                Assert.AreSame(expected, destination[index]);
                Assert.AreEqual(source[index].Value, destination[index].Value);
            }
        }
    }

    private static Row[] CreateRows(int count, string value = "original") =>
        Enumerable.Range(0, count).Select(key => new Row(key, value)).ToArray();

    private static BulkObservableCollection<Row> CreateCollection(IEnumerable<Row> rows)
    {
        BulkObservableCollection<Row> collection = [];
        collection.ReplaceAll(rows);
        return collection;
    }

    private static List<NotifyCollectionChangedAction> CaptureActions(ObservableCollection<Row> collection)
    {
        List<NotifyCollectionChangedAction> actions = [];
        collection.CollectionChanged += (_, args) => actions.Add(args.Action);
        return actions;
    }

    private static void Reconcile(ObservableCollection<Row> destination, IReadOnlyList<Row> source) =>
        CollectionSynchronizer.ReconcileByKey(
            destination,
            source,
            static row => row.Key,
            static (existing, updated) => existing.UpdateFrom(updated));

    private sealed class Row(int key, string value)
    {
        public int Key { get; } = key;

        public string Value { get; private set; } = value;

        public int UpdateCount { get; private set; }

        public void UpdateFrom(Row source)
        {
            Value = source.Value;
            UpdateCount++;
        }
    }
}
