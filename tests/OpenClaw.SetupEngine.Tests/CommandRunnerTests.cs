using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public class CommandRunnerTests
{
    private static readonly string s_largeStdin = new('x', 8 * 1024 * 1024);

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysTimeout()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromMilliseconds(250),
            stdinInput: s_largeStdin);

        Assert.True(result.TimedOut);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_LargeStdinWriteObeysCallerCancellation()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30),
            stdinInput: s_largeStdin,
            ct: cts.Token));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_ProcessTimeoutRemainsBounded()
    {
        var runner = CreateRunner();
        var (executable, arguments) = SleepingCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromMilliseconds(250));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_StreamStdinPreservesBinaryBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openclaw-stdin-{Guid.NewGuid():N}.bin");
        var bytes = new byte[] { 0, 1, 2, 0x7F, 0x80, 0xFF };
        var (executable, arguments) = CopyStdinCommand(path);
        await using var input = new MemoryStream(bytes);

        try
        {
            var result = await CreateRunner().RunAsync(
                executable,
                arguments,
                TimeSpan.FromSeconds(15),
                stdinStream: input);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsMultipleStdinSources()
    {
        var (executable, arguments) = SleepingCommand();
        await using var input = new MemoryStream([1]);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(1),
            stdinInput: "text",
            stdinStream: input));
    }

    [Fact]
    public async Task RunAsync_ReturnsWhenDescendantKeepsOutputPipesOpen()
    {
        var runner = CreateRunner();
        var (executable, arguments) = ExitsLeavingPipeHolderCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30),
            allowInheritedPipeHandleEscape: true);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_DrainsHighVolumeStdoutAndStderrThroughTrailingMarkers()
    {
        const int lineCount = 8_000;
        var (executable, arguments) = HighVolumeOutputCommand(lineCount);

        var result = await CreateRunner().RunAsync(
            executable,
            arguments,
            TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(lineCount, CountLinesStartingWith(result.Stdout, "stdout-"));
        Assert.Equal(lineCount, CountLinesStartingWith(result.Stderr, "stderr-"));
        Assert.EndsWith($"STDOUT_MARKER{Environment.NewLine}", result.Stdout, StringComparison.Ordinal);
        Assert.EndsWith($"STDERR_MARKER{Environment.NewLine}", result.Stderr, StringComparison.Ordinal);
    }

    private static CommandRunner CreateRunner()
        => new(new SetupLogger(filePath: null, LogLevel.Trace));

    private static (string Executable, string[] Arguments) ExitsLeavingPipeHolderCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "start /b ping 127.0.0.1 -n 20"])
            : ("/bin/sh", ["-c", "sleep 20 & exit 0"]);

    private static (string Executable, string[] Arguments) SleepingCommand()
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c", "ping 127.0.0.1 -n 30 >nul"])
            : ("/bin/sh", ["-c", "sleep 30"]);

    private static (string Executable, string[] Arguments) HighVolumeOutputCommand(int lineCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            var shellScript =
                $"i=0; while [ $i -lt {lineCount} ]; do echo stdout-$i; echo stderr-$i >&2; i=$((i+1)); done; " +
                "echo STDOUT_MARKER; echo STDERR_MARKER >&2";
            return ("/bin/sh", ["-c", shellScript]);
        }

        var powerShellScript =
            $"for ($i = 0; $i -lt {lineCount}; $i++) {{ " +
            "[Console]::Out.WriteLine(\"stdout-$i\"); " +
            "[Console]::Error.WriteLine(\"stderr-$i\") }; " +
            "[Console]::Out.WriteLine('STDOUT_MARKER'); " +
            "[Console]::Error.WriteLine('STDERR_MARKER')";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", powerShellScript]);
    }

    private static int CountLinesStartingWith(string value, string prefix)
        => value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith(prefix, StringComparison.Ordinal));

    private static (string Executable, string[] Arguments) CopyStdinCommand(string path)
    {
        if (!OperatingSystem.IsWindows())
            return ("/bin/sh", ["-c", $"cat > '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'"]);

        var escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        var script =
            $"$inputStream = [Console]::OpenStandardInput(); $output = [IO.File]::Create('{escapedPath}'); " +
            "try { $inputStream.CopyTo($output) } finally { $output.Dispose() }";
        return ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
    }
}
