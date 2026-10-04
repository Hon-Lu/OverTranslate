using System.IO;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

public sealed class AuthenticodeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ot-authenticode-" + Guid.NewGuid().ToString("N"));

    public AuthenticodeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // The DirectML.dll the build restores, which is the file the manifest pins.
    private static string? Restored()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var path = Path.Combine(packages, "microsoft.ai.directml", "1.15.4", "bin", "x64-win", "DirectML.dll");
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void DirectMl_IsSignedByMicrosoft()
    {
        if (Restored() is not { } dll) return;

        Assert.Null(Authenticode.Problem(dll, "Microsoft Corporation"));
        Assert.Contains("not Someone Else", Authenticode.Problem(dll, "Someone Else"));
    }

    [Fact]
    public void ADirectMlWithOneByteChanged_IsNotTrusted()
    {
        if (Restored() is not { } dll) return;
        var bytes = File.ReadAllBytes(dll);
        bytes[4096] ^= 0xFF;
        var tampered = Path.Combine(_folder, "DirectML.dll");
        File.WriteAllBytes(tampered, bytes);

        Assert.NotNull(Authenticode.Problem(tampered, "Microsoft Corporation"));
    }

    [Fact]
    public void AnUnsignedFile_IsNotTrusted()
    {
        var unsigned = Path.Combine(_folder, "DirectML.dll");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 1, 2, 3]);

        Assert.NotNull(Authenticode.Problem(unsigned, "Microsoft Corporation"));
        Assert.NotNull(Authenticode.Problem(Path.Combine(_folder, "missing.dll"), "Microsoft Corporation"));
    }
}
