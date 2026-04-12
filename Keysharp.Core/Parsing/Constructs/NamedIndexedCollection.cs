using System.Collections;

namespace Keysharp.Parsing
{
	internal sealed class NamedIndexedCollection<TItem> : IEnumerable<TItem>
	{
		private sealed class Entry
		{
			internal TItem Item;
			internal string Name;

			internal Entry(TItem item, string name)
			{
				Item = item;
				Name = name;
			}
		}

		private readonly Func<TItem, string> nameSelector;
		private readonly List<Entry> items = new();
		private readonly Dictionary<string, List<Entry>> exactIndex = new(StringComparer.Ordinal);
		private readonly Dictionary<string, List<Entry>> ignoreCaseIndex = new(StringComparer.OrdinalIgnoreCase);

		internal NamedIndexedCollection(Func<TItem, string> nameSelector)
		{
			this.nameSelector = nameSelector ?? throw new ArgumentNullException(nameof(nameSelector));
		}

		internal int Count => items.Count;

		internal TItem this[int index]
		{
			get => items[index].Item;
			set => ReplaceAt(index, value);
		}

		internal void Add(TItem item) => Insert(items.Count, item);

		internal void AddRange(params TItem[] range) => AddRange((IEnumerable<TItem>)range);

		internal void AddRange(IEnumerable<TItem> range)
		{
			ArgumentNullException.ThrowIfNull(range);

			foreach (var item in range)
				Add(item);
		}

		internal void Insert(int index, TItem item)
		{
			var entry = CreateEntry(item);
			items.Insert(index, entry);
			IndexEntry(entry);
		}

		internal void InsertRange(int index, IEnumerable<TItem> range)
		{
			ArgumentNullException.ThrowIfNull(range);

			foreach (var item in range)
				Insert(index++, item);
		}

		internal bool Remove(TItem item)
		{
			var comparer = EqualityComparer<TItem>.Default;

			for (var i = 0; i < items.Count; i++)
			{
				if (comparer.Equals(items[i].Item, item))
				{
					RemoveAt(i);
					return true;
				}
			}

			return false;
		}

		internal void RemoveAt(int index)
		{
			var entry = items[index];
			UnindexEntry(entry);
			items.RemoveAt(index);
		}

		internal bool RemoveFirst(string name, bool caseSensitive = true, Predicate<TItem> predicate = null)
		{
			if (!TryFindEntry(name, caseSensitive, predicate, out var entry))
				return false;

			UnindexEntry(entry);
			items.Remove(entry);
			return true;
		}

		internal void Clear()
		{
			items.Clear();
			exactIndex.Clear();
			ignoreCaseIndex.Clear();
		}

		internal bool ContainsName(string name, bool caseSensitive = true, Predicate<TItem> predicate = null) =>
			TryFindEntry(name, caseSensitive, predicate, out _);

		internal bool TryGetValue(string name, out TItem item, bool caseSensitive = true, Predicate<TItem> predicate = null)
		{
			if (TryFindEntry(name, caseSensitive, predicate, out var entry))
			{
				item = entry.Item;
				return true;
			}

			item = default;
			return false;
		}

		internal bool TryGetName(string name, out string actualName, bool caseSensitive = true, Predicate<TItem> predicate = null)
		{
			if (TryFindEntry(name, caseSensitive, predicate, out var entry))
			{
				actualName = entry.Name;
				return true;
			}

			actualName = null;
			return false;
		}

		internal bool AddIfMissing(string name, TItem item, bool caseSensitive = true, Predicate<TItem> predicate = null)
		{
			if (ContainsName(name, caseSensitive, predicate))
				return false;

			Add(item);
			return true;
		}

		internal bool TryReplaceFirst(string name, TItem item, bool caseSensitive = true, Predicate<TItem> predicate = null)
		{
			if (!TryFindEntry(name, caseSensitive, predicate, out var entry))
				return false;

			ReplaceEntry(entry, item);
			return true;
		}

		internal void Upsert(string name, TItem item, bool caseSensitive = true, Predicate<TItem> predicate = null)
		{
			if (!TryReplaceFirst(name, item, caseSensitive, predicate))
				Add(item);
		}

		internal TItem[] ToArray()
		{
			var array = new TItem[items.Count];

			for (var i = 0; i < items.Count; i++)
				array[i] = items[i].Item;

			return array;
		}

		public IEnumerator<TItem> GetEnumerator()
		{
			foreach (var entry in items)
				yield return entry.Item;
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		private void ReplaceAt(int index, TItem item) => ReplaceEntry(items[index], item);

		private void ReplaceEntry(Entry entry, TItem item)
		{
			var updated = CreateEntry(item);
			UnindexEntry(entry);
			entry.Item = updated.Item;
			entry.Name = updated.Name;
			IndexEntry(entry);
		}

		private Entry CreateEntry(TItem item)
		{
			ArgumentNullException.ThrowIfNull(item);
			return new Entry(item, ExtractName(item));
		}

		private string ExtractName(TItem item)
		{
			var name = nameSelector(item);
			return string.IsNullOrWhiteSpace(name) ? null : name;
		}

		private void IndexEntry(Entry entry)
		{
			if (entry.Name == null)
				return;

			AddIndexEntry(exactIndex, entry.Name, entry);
			AddIndexEntry(ignoreCaseIndex, entry.Name, entry);
		}

		private void UnindexEntry(Entry entry)
		{
			if (entry.Name == null)
				return;

			RemoveIndexEntry(exactIndex, entry.Name, entry);
			RemoveIndexEntry(ignoreCaseIndex, entry.Name, entry);
		}

		private static void AddIndexEntry(Dictionary<string, List<Entry>> index, string name, Entry entry)
		{
			if (!index.TryGetValue(name, out var bucket))
			{
				bucket = new List<Entry>();
				index[name] = bucket;
			}

			bucket.Add(entry);
		}

		private static void RemoveIndexEntry(Dictionary<string, List<Entry>> index, string name, Entry entry)
		{
			if (!index.TryGetValue(name, out var bucket))
				return;

			bucket.Remove(entry);

			if (bucket.Count == 0)
				index.Remove(name);
		}

		private bool TryFindEntry(string name, bool caseSensitive, Predicate<TItem> predicate, out Entry entry)
		{
			entry = null;

			if (string.IsNullOrWhiteSpace(name))
				return false;

			var index = caseSensitive ? exactIndex : ignoreCaseIndex;
			if (!index.TryGetValue(name, out var bucket))
				return false;

			foreach (var candidate in bucket)
			{
				if (predicate == null || predicate(candidate.Item))
				{
					entry = candidate;
					return true;
				}
			}

			return false;
		}
	}
}
