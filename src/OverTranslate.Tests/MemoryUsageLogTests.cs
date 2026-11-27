using System.Diagnostics;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The half-hourly memory line: its figures are the process's own, taking them costs next to
/// nothing and leaks nothing, and the timer writes, re-arms and stops as it should. The interval is
/// injected, so nothing here waits half an hour.
/// </summary>
public class MemoryUsageLogTests
{
    [Fact]
    public void Sample_reports_this_process_figures()
    {
        var s = MemoryUsageLog.Sample.Take();

        using var self = Process.GetCurrentProcess();
        // Same counter Process reads, a moment apart; allow for what the test host does in between.
        Assert.InRange(s.PrivateBytes, self.PrivateMemorySize64 - (64L << 20), self.PrivateMemorySize64 + (64L << 20));
        Assert.True(s.WorkingSetBytes > 0);
        Assert.True(s.GcHeapBytes > 0);
        Assert.True(s.LohBytes >= 0);
        Assert.True(s.Handles > 0);
        Assert.True(s.GdiObjects >= 0);
        Assert.True(s.UserObjects >= 0);
    }

    [Fact]
    public void Sampling_allocates_little_and_leaks_no_handles()
    {
        MemoryUsageLog.Sample.Take();
        int handlesBefore = MemoryUsageLog.Sample.Take().Handles;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) MemoryUsageLog.Sample.Take();
        long perSample = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;

        // A Process object per sample would open a handle each time; the pseudo-handle opens none.
        // Other tests run alongside, so allow a little drift rather than demanding an exact count.
        Assert.InRange(MemoryUsageLog.Sample.Take().Handles, 0, handlesBefore + 50);
        Assert.True(perSample < 4096, $"{perSample} bytes per sample");
    }

    [Fact]
    public void Ticks_write_a_numbers_only_line_and_stop_when_disposed()
    {
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var config = NLog.LogManager.Configuration ?? new NLog.Config.LoggingConfiguration();
        var rule = new NLog.Config.LoggingRule(typeof(MemoryUsageLog).FullName, NLog.LogLevel.Info, target);
        config.LoggingRules.Add(rule);
        NLog.LogManager.Configuration = config;
        try
        {
            var log = new MemoryUsageLog(TimeSpan.FromMilliseconds(50));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (target.Logs.Count < 3 && DateTime.UtcNow < deadline) Thread.Sleep(20);
            log.Dispose();

            Assert.True(target.Logs.Count >= 3, "re-armed after each tick");
            Assert.Matches(
                @"^Info\|Memory: private \d+MB, working set \d+MB, GC heap \d+MB, LOH \d+MB, handles \d+, GDI \d+, USER \d+$",
                target.Logs[0]);

            Thread.Sleep(200);
            int after = target.Logs.Count;
            Thread.Sleep(300);
            Assert.Equal(after, target.Logs.Count);
        }
        finally
        {
            config.LoggingRules.Remove(rule);
            NLog.LogManager.Configuration = config;
        }
    }
}
