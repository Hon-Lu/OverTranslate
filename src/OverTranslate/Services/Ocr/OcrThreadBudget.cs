namespace OverTranslate.Services.Ocr;

/// <summary>
/// How much of the machine recognition may use, split into threads inside one inference and how many
/// inferences may run at once.
/// </summary>
/// <remarks>
/// The two are decided together because their product is the whole of it. That product stays at half
/// the machine, where it has always been; what this chooses is how a large machine spends it.
///
/// <code>
///   cores   threads x slots = peak     before        realtime blocks that queue
///     64        4 x 4 = 16             2 x 4 =  8    none
///     16        4 x 3 = 12             2 x 4 =  8    none
///     12        3 x 3 =  9             2 x 3 =  6    none
///      8        2 x 2 =  4             2 x 2 =  4    the third
///      4        2 x 1 =  2             2 x 1 =  2    the second and third
///      2        2 x 1 =  2             2 x 1 =  2    the second and third
/// </code>
///
/// Nothing at eight cores or below moves. That matters more than the gain does: the measurements
/// behind this were taken on a sixteen-core machine, where four threads is a quarter of it, and they
/// do not transfer to a four-core machine where four threads is all of it — and this application's
/// premise is a game running behind it. A small machine keeps the shipped numbers and lets its
/// blocks queue, which is a graceful thing to do: a refused poll is skipped and retried a poll later,
/// so the block updates less often rather than failing.
///
/// Four threads is the ceiling because the model stops scaling there rather than because of the
/// machine: measured on sixteen cores, six threads (370ms) was slower than four (356ms). Above
/// sixteen cores the peak is allowed to grow to 16, which is still a quarter of a 64-core machine.
///
/// A pure function of the core count so the table above can be a test rather than a comment. The
/// rule it replaces was <c>Clamp(ProcessorCount, 1, 2)</c>, which reads as though it scales with the
/// machine and does not — everything from two cores to sixty-four came out at two.
/// </remarks>
internal static class OcrThreadBudget
{
    /// <summary>Threads the model stops making use of, measured rather than assumed.</summary>
    public const int MaxThreads = 4;

    /// <summary>Below this, one inference at a time and two threads is the whole budget.</summary>
    public const int MinThreads = 2;

    /// <summary>
    /// Blocks a realtime session may watch at once, and therefore the most inferences it can ask for
    /// at the same instant — one loop per block. Mirrors RealtimePage's own limit.
    /// </summary>
    /// <remarks>
    /// Kept here as a number the budget has to answer to rather than read from the view: fewer slots
    /// than blocks means the feature is built to contend with itself, which is a thing to decide on
    /// purpose and not to discover.
    /// </remarks>
    public const int RealtimeBlocks = 3;

    /// <summary>
    /// Cores from which a machine is given enough slots that a full realtime session never queues:
    /// enough to run every block at the full thread count.
    /// </summary>
    public const int NeverContendFrom = MaxThreads * RealtimeBlocks;

    public static (int Threads, int Slots) For(int logicalProcessors)
    {
        var cores = Math.Max(1, logicalProcessors);

        var threads = Math.Clamp(cores / 4, MinThreads, MaxThreads);

        // Half the machine, spent at whatever the thread count above costs per inference. The floor
        // of one is what a machine too small to divide gets; the cap of four is the throughput
        // measurement the capture side was tuned against.
        var slots = Math.Clamp(cores / (2 * threads), 1, 4);

        // A machine with room to spare gets enough slots for a full session instead. Half the
        // machine turned out to be a budget for a peak that does not happen: measured on a 16-core
        // machine during a three-block session, actual use sat near 25% because recognition is
        // bursty — a block that has not changed does not run at all, and three blocks rarely fire
        // together. The same session had 58 polls turned away, 36 of them from the one busy block,
        // which is the cost of budgeting for a worst case instead of the real one.
        //
        // The trade is honest rather than free: three blocks all firing at once now takes three
        // times the thread count instead of contending, so the ceiling on these machines moves from
        // half to three quarters. Machines below the threshold keep the old numbers exactly, because
        // that headroom is what they do not have.
        if (cores >= NeverContendFrom)
            slots = Math.Max(slots, RealtimeBlocks);

        return (threads, slots);
    }
}
