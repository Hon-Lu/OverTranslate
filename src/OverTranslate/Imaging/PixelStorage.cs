using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OverTranslate.Imaging;

/// <summary>
/// The pixels behind an <see cref="ImageBuffer"/> and every view cut from it, shared by reference
/// count and given back when the last of them is disposed.
/// </summary>
/// <remarks>
/// <para>The repair runs every 100 ms on a live overlay and asks for some forty images each time,
/// from a few hundred bytes to the whole frame, and the same sizes come back every cycle. Both
/// size classes are therefore reused rather than allocated.</para>
///
/// <para>Small blocks are pinned managed arrays, so a view can hand out a raw pointer for as long
/// as it lives. Large blocks — a frame, a mask, a distance field — are native memory, outside the
/// collector entirely. Taking those fresh from the system on every cycle was measured, and it is
/// what the library this replaced did: a 2557×1437 frame keeps some 150 MB of them alive at once,
/// and touching that much newly committed memory ten times a second cost the repair 30 ms of its
/// 160. So they are kept too — but only while they are being asked for. A block nobody has taken
/// for <see cref="IdleRelease"/> goes back to the system, so the memory a live overlay needs is not
/// memory the process keeps once the overlay has closed.</para>
/// </remarks>
internal sealed unsafe class PixelStorage
{
    /// <summary>Smallest bucket: 64 bytes. Bucket <c>k</c> holds arrays of <c>64 &lt;&lt; k</c> bytes.</summary>
    private const int SmallestShift = 6;

    /// <summary>Arrays up to 1 MB are pooled managed arrays; the pool holds these 15 sizes.</summary>
    private const int Buckets = 15;

    private const long LargestManaged = 1L << (SmallestShift + Buckets - 1);

    /// <summary>How many arrays of one size the managed pool keeps.</summary>
    private const int PerBucket = 16;

    /// <summary>Large blocks are kept by size, rounded up to this.</summary>
    private const long LargeGranule = 1L << 20;

    /// <summary>The most native memory kept unused at any moment, whatever the frame size.</summary>
    private const long LargeRetainLimit = 512L << 20;

    /// <summary>How long a kept native block may go unasked for before it is freed.</summary>
    private static readonly TimeSpan IdleRelease = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentBag<byte[]>[] Pool = CreatePool();
    private static readonly int[] PoolCounts = new int[Buckets];
    private static readonly ConcurrentDictionary<long, ConcurrentBag<nint>> Large = new();
    private static long _largeRetained;
    private static long _largeUses;
    private static System.Threading.Timer? _trim;

    private readonly byte[]? _array;
    private readonly long _largeSize;
    private nint _native;
    private int _references = 1;

    private PixelStorage(byte[] array)
    {
        _array = array;
        Pointer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
    }

    private PixelStorage(nint native, long size)
    {
        _native = native;
        _largeSize = size;
        Pointer = (byte*)native;
    }

    /// <summary>A block that was never disposed is still given back, late.</summary>
    ~PixelStorage()
    {
        if (_native != 0) NativeMemory.Free((void*)_native);
    }

    /// <summary>Where the first byte is. Valid until the last reference is released.</summary>
    public byte* Pointer { get; }

    /// <summary>At least <paramref name="bytes"/> bytes whose content is whatever was there before.</summary>
    public static PixelStorage Rent(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (bytes > LargestManaged) return RentLarge(bytes);
        int bucket = Bucket(bytes);
        PixelStorage storage;
        if (Pool[bucket].TryTake(out var array))
        {
            Interlocked.Decrement(ref PoolCounts[bucket]);
            storage = new PixelStorage(array);
        }
        else storage = new PixelStorage(GC.AllocateUninitializedArray<byte>(1 << (bucket + SmallestShift), pinned: true));
        GC.SuppressFinalize(storage);
        return storage;
    }

    public void AddReference() => Interlocked.Increment(ref _references);

    public void Release()
    {
        int left = Interlocked.Decrement(ref _references);
        if (left > 0) return;
        if (left < 0) throw new ObjectDisposedException(nameof(PixelStorage));
        if (_array is null)
        {
            ReturnLarge(_native, _largeSize);
            _native = 0;
            GC.SuppressFinalize(this);
            return;
        }
        int bucket = Bucket(_array.Length);
        if (Interlocked.Increment(ref PoolCounts[bucket]) > PerBucket)
        {
            Interlocked.Decrement(ref PoolCounts[bucket]);
            return;
        }
        Pool[bucket].Add(_array);
    }

    private static PixelStorage RentLarge(long bytes)
    {
        long size = (bytes + LargeGranule - 1) / LargeGranule * LargeGranule;
        Interlocked.Increment(ref _largeUses);
        if (Large.TryGetValue(size, out var bag) && bag.TryTake(out nint kept))
        {
            Interlocked.Add(ref _largeRetained, -size);
            return new PixelStorage(kept, size);
        }
        EnsureTrimming();
        return new PixelStorage((nint)NativeMemory.Alloc((nuint)size), size);
    }

    private static void ReturnLarge(nint block, long size)
    {
        Interlocked.Increment(ref _largeUses);
        if (Interlocked.Add(ref _largeRetained, size) > LargeRetainLimit)
        {
            Interlocked.Add(ref _largeRetained, -size);
            NativeMemory.Free((void*)block);
            return;
        }
        Large.GetOrAdd(size, _ => []).Add(block);
    }

    private static void EnsureTrimming()
    {
        if (Volatile.Read(ref _trim) is not null) return;
        var timer = new System.Threading.Timer(Trim, null, Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _trim, timer, null) is null) timer.Change(IdleRelease, IdleRelease);
        else timer.Dispose();
    }

    private static long _usesAtLastTrim;

    /// <summary>Frees every kept native block if none was taken or returned since the last tick.</summary>
    private static void Trim(object? state)
    {
        long uses = Interlocked.Read(ref _largeUses);
        if (uses != Interlocked.Exchange(ref _usesAtLastTrim, uses)) return;
        foreach (var (size, bag) in Large)
            while (bag.TryTake(out nint block))
            {
                Interlocked.Add(ref _largeRetained, -size);
                NativeMemory.Free((void*)block);
            }
    }

    private static int Bucket(long bytes)
    {
        if (bytes <= 1L << SmallestShift) return 0;
        return BitOperations.Log2((ulong)(bytes - 1)) + 1 - SmallestShift;
    }

    private static ConcurrentBag<byte[]>[] CreatePool()
    {
        var pool = new ConcurrentBag<byte[]>[Buckets];
        for (int i = 0; i < pool.Length; i++) pool[i] = [];
        return pool;
    }
}

/// <summary>A working array for one routine, from the same place images get their pixels.</summary>
internal readonly unsafe struct Scratch<T> : IDisposable where T : unmanaged
{
    private readonly PixelStorage _storage;

    public Scratch(long count)
    {
        _storage = PixelStorage.Rent(Math.Max(1, count * sizeof(T)));
        Pointer = (T*)_storage.Pointer;
    }

    public T* Pointer { get; }

    public void Dispose() => _storage.Release();
}
