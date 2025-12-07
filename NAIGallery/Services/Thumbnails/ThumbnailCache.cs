using System;
using System.Buffers;
using System.Collections.Generic;

namespace NAIGallery.Services.Thumbnails;

/// <summary>
/// Represents cached pixel data for a thumbnail.
/// </summary>
internal sealed class PixelEntry
{
    public required byte[] Pixels;
    public required int W;
    public required int H;
    public required int Rented;
}

/// <summary>
/// LRU cache for decoded thumbnail pixel data.
/// Thread-safe via internal locking.
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    private sealed class LruNode
    {
        public required string Key;
        public required PixelEntry Entry;
        public LruNode? Prev;
        public LruNode? Next;
    }

    private readonly Dictionary<string, LruNode> _cacheMap = new(StringComparer.Ordinal);
    private LruNode? _head;
    private LruNode? _tail;
    private long _currentBytes;
    private long _byteCapacity;
    private readonly object _cacheLock = new();
    private bool _disposed;

    public ThumbnailCache(long capacityBytes)
    {
        _byteCapacity = Math.Max(1, capacityBytes);
    }

    public long Capacity
    {
        get
        {
            lock (_cacheLock)
                return _byteCapacity;
        }
        set
        {
            lock (_cacheLock)
            {
                _byteCapacity = Math.Max(1, value);
                Prune_NoLock();
            }
        }
    }

    public long CurrentBytes
    {
        get
        {
            lock (_cacheLock)
                return _currentBytes;
        }
    }

    public bool TryGet(string key, out PixelEntry? entry)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                MoveToHead_NoLock(node);
                entry = node.Entry;
                return true;
            }
        }
        entry = null;
        return false;
    }

    public void Add(string key, PixelEntry entry)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Entry.Rented;
                try { ArrayPool<byte>.Shared.Return(existing.Entry.Pixels); } catch { }
                existing.Entry = entry;
                _currentBytes += entry.Rented;
                MoveToHead_NoLock(existing);
                Prune_NoLock();
                return;
            }

            var node = new LruNode { Key = key, Entry = entry };
            _cacheMap[key] = node;
            _currentBytes += entry.Rented;
            InsertHead_NoLock(node);
            Prune_NoLock();
        }
    }

    public void Touch(string key)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                MoveToHead_NoLock(node);
            }
        }
    }

    public void Clear()
    {
        lock (_cacheLock)
        {
            foreach (var kv in _cacheMap)
            {
                try { ArrayPool<byte>.Shared.Return(kv.Value.Entry.Pixels); } catch { }
            }
            _cacheMap.Clear();
            _head = _tail = null;
            _currentBytes = 0;
        }
    }

    private void InsertHead_NoLock(LruNode node)
    {
        node.Prev = null;
        node.Next = _head;
        if (_head != null) _head.Prev = node;
        _head = node;
        _tail ??= node;
    }

    private void MoveToHead_NoLock(LruNode node)
    {
        if (node == _head) return;
        if (node.Prev != null) node.Prev.Next = node.Next;
        if (node.Next != null) node.Next.Prev = node.Prev;
        if (node == _tail) _tail = node.Prev;
        node.Prev = null;
        node.Next = _head;
        if (_head != null) _head.Prev = node;
        _head = node;
        _tail ??= node;
    }

    private void Prune_NoLock()
    {
        while (_currentBytes > _byteCapacity && _tail != null)
        {
            var evict = _tail;
            var prev = evict.Prev;
            if (prev != null) prev.Next = null;
            _tail = prev;
            if (_tail == null) _head = null;
            _cacheMap.Remove(evict.Key);
            _currentBytes -= evict.Entry.Rented;
            try { ArrayPool<byte>.Shared.Return(evict.Entry.Pixels); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
