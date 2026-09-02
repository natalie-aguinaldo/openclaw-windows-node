using System.Diagnostics;

namespace OpenClaw.SetupEngine.Tests;

public sealed class BoundedProcessOutputTests
{
    [Fact]
    public async Task AwaitRedirectedOutputAsync_ReturnsNullWhenStdoutNeverCloses()
    {
        using var process = StartExitingProcess();
        Assert.NotNull(process);

        var never = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var stopwatch = Stopwatch.StartNew();

        var output = await BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            never,
            timeoutMs: 400);

        Assert.Null(output);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_UsesOneDeadlineForExitKillAndDrain()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var readTask = process.StandardOutput.ReadToEndAsync();
        var stopwatch = Stopwatch.StartNew();
        var output = await BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            readTask,
            timeoutMs: 400);

        Assert.Null(output);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(900));
        Assert.True(
            SpinWait.SpinUntil(() => process.HasExited, TimeSpan.FromSeconds(2)),
            "The timed-out Tailscale probe process was not killed.");
        var readException = await Record.ExceptionAsync(
            () => readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(
            readException is null or ObjectDisposedException,
            $"Abandoned stdout read failed unexpectedly: {readException}");
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_PreservesOutputCompletedNearDeadline()
    {
        using var process = StartExitingProcess();
        Assert.True(process.WaitForExit(3_000));
        var outputSource = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            outputSource.Task,
            timeoutMs: 500);

        await Task.Delay(350);
        outputSource.SetResult("{\"BackendState\":\"Running\"}");

        Assert.Equal("{\"BackendState\":\"Running\"}", await helper);
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_CancellationKillsProcessAndObservesRead()
    {
        var (fileName, arguments) = LongRunningCommand();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);

        var readTask = process.StandardOutput.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedProcessOutput.AwaitRedirectedOutputAsync(
                process,
                readTask,
                timeoutMs: 5_000,
                cancellation.Token));

        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromMilliseconds(800));
        Assert.True(
            SpinWait.SpinUntil(() => process.HasExited, TimeSpan.FromSeconds(2)),
            "The cancelled Tailscale probe process was not killed.");
        _ = await Record.ExceptionAsync(() => readTask);
    }

    [Fact]
    public async Task AwaitRedirectedOutputAsync_BoundsInheritedHandleDrainOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var process = Process.Start(InheritedHandleCommand());
        Assert.NotNull(process);
        var readTask = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(2_000), "The parent probe process did not exit.");
        Assert.False(readTask.IsCompleted, "The descendant did not retain the redirected output handle.");
        var stopwatch = Stopwatch.StartNew();

        var output = await BoundedProcessOutput.AwaitRedirectedOutputAsync(
            process,
            readTask,
            timeoutMs: 300);

        Assert.Null(output);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800));
        _ = await Record.ExceptionAsync(() => readTask.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task ReadAsync_CapturesStdoutFromExitingProcess()
    {
        var (fileName, arguments) = EchoCommand("status-ok");
        var result = await BoundedProcessOutput.ReadAsync(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, timeoutMs: 5_000);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("status-ok", result.Output);
    }

    [Fact]
    public async Task ReadAsync_HungWindowsTailscaleProbeHonorsFiveSecondDeadline()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (fileName, arguments) = LongRunningCommand();
        var stopwatch = Stopwatch.StartNew();

        var result = await BoundedProcessOutput.ReadAsync(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromSeconds(4.5),
            TimeSpan.FromMilliseconds(5_750));
    }

    private static ProcessStartInfo InheritedHandleCommand()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$null = Start-Process powershell.exe " +
            "-ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 2' " +
            "-NoNewWindow -PassThru; Write-Output inherited-output");
        return startInfo;
    }

    private static Process StartExitingProcess() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/d /c exit 0" : "-c \"exit 0\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static (string FileName, string Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/d /c ping 127.0.0.1 -n 20")
            : ("/bin/sh", "-c \"sleep 20\"");

    private static (string FileName, string Arguments) EchoCommand(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /c echo {text}")
            : ("/bin/sh", $"-c \"printf '%s\\n' '{text}'\"");
}
