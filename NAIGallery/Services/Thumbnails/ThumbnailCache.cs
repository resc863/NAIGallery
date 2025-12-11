using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace NAIGallery.Services.Thumbnails;

/// <summary>
/// 디코딩된 썸네일 픽셀 데이터
/// </summary>
internal sealed class PixelData : IDisposable
{
    public byte[] Pixels { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public int ByteCount { get; }
    
    private int _disposed;

    public PixelData(byte[] pixels, int width, int height, int byteCount)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        ByteCount = byteCount;
    }

    public bool IsValid => Volatile.Read(ref _disposed) == 0 && Pixels != null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            var px = Pixels;
            Pixels = null!;
            if (px != null)
            {
                try { ArrayPool<byte>.Shared.Return(px); } catch { }
            }
        }
    }
}

/// <summary>
/// 간단한 LRU 썸네일 캐시
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _currentBytes;
    private long _capacity;
    private bool _disposed;

    private sealed class CacheEntry
    {
        public required string Key;
        public required PixelData Data;
    }

    public ThumbnailCache(long capacityBytes)
    {
        _capacity = Math.Max(1024 * 1024, capacityBytes); // 최소 1MB
    }

    public long Capacity
    {
        get { lock (_lock) return _capacity; }
        set
        {
            lock (_lock)
            {
                _capacity = Math.Max(1024 * 1024, value);
                Evict();
            }
        }
    }

    public bool TryGet(string key, out PixelData? data)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // LRU 업데이트: 맨 앞으로 이동
                _lru.Remove(node);
                _lru.AddFirst(node);
                data = node.Value.Data;
                return data.IsValid;
            }
        }
        data = null;
        return false;
    }

    public void Add(string key, PixelData data)
    {
        lock (_lock)
        {
            // 이미 존재하면 제거
            if (_map.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.Data.ByteCount;
                _lru.Remove(existing);
                _map.Remove(key);
                existing.Value.Data.Dispose();
            }

            // 새 항목 추가
            var entry = new CacheEntry { Key = key, Data = data };
            var node = _lru.AddFirst(entry);
            _map[key] = node;
            _currentBytes += data.ByteCount;

            Evict();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var node in _lru)
            {
                node.Data.Dispose();
            }
            _map.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private void Evict()
    {
        while (_currentBytes > _capacity && _lru.Last != null)
        {
            var victim = _lru.Last;
            _lru.RemoveLast();
            _map.Remove(victim.Value.Key);
            _currentBytes -= victim.Value.Data.ByteCount;
            victim.Value.Data.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }
}
