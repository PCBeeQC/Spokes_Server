namespace Spokes_Server.Core.Helpers;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// A thread-safe hash set implementation backed by <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Provides safe concurrent read, write, and snapshot enumeration across multiple threads
/// without throwing <see cref="InvalidOperationException"/> when the set is mutated during iteration.
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public class ConcurrentHashSet<T> : ISet<T>, IReadOnlyCollection<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, byte> _dictionary;
    private readonly IEqualityComparer<T> _comparer;

    public ConcurrentHashSet()
        : this(EqualityComparer<T>.Default)
    {
    }

    public ConcurrentHashSet(IEqualityComparer<T> comparer)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _dictionary = new ConcurrentDictionary<T, byte>(_comparer);
    }

    public ConcurrentHashSet(IEnumerable<T> collection)
        : this(collection, EqualityComparer<T>.Default)
    {
    }

    public ConcurrentHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
        : this(comparer)
    {
        ArgumentNullException.ThrowIfNull(collection);
        foreach (var item in collection)
        {
            _dictionary.TryAdd(item, 0);
        }
    }

    public int Count => _dictionary.Count;

    public bool IsReadOnly => false;

    public bool Add(T item) => _dictionary.TryAdd(item, 0);

    void ICollection<T>.Add(T item) => Add(item);

    public bool TryAdd(T item) => Add(item);

    public bool Remove(T item) => _dictionary.TryRemove(item, out _);

    public bool TryRemove(T item) => Remove(item);

    public void Clear() => _dictionary.Clear();

    public bool Contains(T item) => _dictionary.ContainsKey(item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);

        var keys = _dictionary.Keys.ToArray();
        var countToCopy = Math.Min(keys.Length, array.Length - arrayIndex);
        if (countToCopy > 0)
        {
            Array.Copy(keys, 0, array, arrayIndex, countToCopy);
        }
    }

    public T[] ToArray() => _dictionary.Keys.ToArray();

    public IEnumerator<T> GetEnumerator() => _dictionary.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Atomically reconciles the current set to match <paramref name="items"/> without clearing first,
    /// preventing empty-set flicker for concurrent readers.
    /// </summary>
    public void ReplaceWith(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var targetSet = items as HashSet<T> ?? new HashSet<T>(items, _comparer);

        foreach (var key in _dictionary.Keys)
        {
            if (!targetSet.Contains(key))
            {
                _dictionary.TryRemove(key, out _);
            }
        }

        foreach (var item in targetSet)
        {
            _dictionary.TryAdd(item, 0);
        }
    }

    public void ExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other)
        {
            Remove(item);
        }
    }

    public void IntersectWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var otherSet = other as HashSet<T> ?? new HashSet<T>(other, _comparer);
        foreach (var key in _dictionary.Keys)
        {
            if (!otherSet.Contains(key))
            {
                Remove(key);
            }
        }
    }

    public bool IsProperSubsetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var otherSet = other as HashSet<T> ?? new HashSet<T>(other, _comparer);
        return Count < otherSet.Count && IsSubsetOf(otherSet);
    }

    public bool IsProperSupersetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var otherSet = other as HashSet<T> ?? new HashSet<T>(other, _comparer);
        return Count > otherSet.Count && IsSupersetOf(otherSet);
    }

    public bool IsSubsetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var otherSet = other as HashSet<T> ?? new HashSet<T>(other, _comparer);
        foreach (var item in this)
        {
            if (!otherSet.Contains(item))
            {
                return false;
            }
        }
        return true;
    }

    public bool IsSupersetOf(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other)
        {
            if (!Contains(item))
            {
                return false;
            }
        }
        return true;
    }

    public bool Overlaps(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other)
        {
            if (Contains(item))
            {
                return true;
            }
        }
        return false;
    }

    public bool SetEquals(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var otherSet = other as HashSet<T> ?? new HashSet<T>(other, _comparer);
        return Count == otherSet.Count && IsSubsetOf(otherSet);
    }

    public void SymmetricExceptWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other)
        {
            if (!Remove(item))
            {
                Add(item);
            }
        }
    }

    public void UnionWith(IEnumerable<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other)
        {
            Add(item);
        }
    }
}
