using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Pins how narrow the exception to Topmost is. Everything that gets past this can cover the
/// screenshot the user is in the middle of taking, so widening the list is a decision, not a tidy-up.
/// </summary>
public class SystemRecoveryYieldTests
{
    [Fact]
    public void TaskManagerIsLetPast_WhateverCaseWindowsReportsItIn()
    {
        Assert.True(SystemRecoveryYield.IsRecoveryProcess("Taskmgr"));
        Assert.True(SystemRecoveryYield.IsRecoveryProcess("taskmgr"));
        Assert.True(SystemRecoveryYield.IsRecoveryProcess("TASKMGR"));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("chrome")]
    [InlineData("OverTranslate")]
    [InlineData("Taskmgr.exe")] // Windows reports process names without the extension
    [InlineData("")]
    public void EverythingElseStaysUnderTheCaptureLayer(string processName) =>
        Assert.False(SystemRecoveryYield.IsRecoveryProcess(processName));
}
