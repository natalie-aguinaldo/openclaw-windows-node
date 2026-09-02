using System.Diagnostics;
using System.Text;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Wait for a short-lived setup probe, then drain leftover redirected
/// stdout. Synchronous ReadToEnd before WaitForExit never reaches the
/// timeout when the child hangs or a descendant holds the pipe open.
/// </summary>
internal static class BoundedProcessOutput
{
    internal const int DefaultTimeoutMs = 5_000;

    internal static async Task<(int ExitCode, string Output)> ReadAsync(
        ProcessStartInfo startInfo,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Process.Start(startInfo);
        if (process is null)
            return (-1, string.Empty);

        var stdout = startInfo.RedirectStandardOutput
            ? new RedirectedOutputCapture(process.StandardOutput)
            : null;
        var stdoutTask = stdout?.Completion ?? Task.CompletedTask;
        var stderrTask = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync()
            : null;

        try
        {
            var waitResult = await AwaitRedirectedOutputAsync(
                process,
                stdoutTask,
                timeoutMs,
                cancellationToken);
            return waitResult.ProcessExited && process.HasExited
                ? (process.ExitCode, stdout?.Output ?? string.Empty)
                : (-1, string.Empty);
        }
        finally
        {
            if (!stdoutTask.IsCompleted)
                DisposeQuietly(process.StandardOutput);
            ObserveQuietly(stdoutTask);
            if (stderrTask is not null)
            {
                if (!stderrTask.IsCompleted)
                    DisposeQuietly(process.StandardError);
                ObserveQuietly(stderrTask);
            }
        }
    }

    internal static async Task<RedirectedOutputWaitResult> AwaitRedirectedOutputAsync(
        Process process,
        Task readTask,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readTask);
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var deadline = new MonotonicDeadline(TimeSpan.FromMilliseconds(timeoutMs));
        using var exitSignalCancellation = new CancellationTokenSource();
        var exitTask = WaitForProcessExitOnlyAsync(process, exitSignalCancellation.Token);
        try
        {
            try
            {
                await WaitWithinDeadlineAsync(exitTask, deadline, cancellationToken);
            }
            catch (TimeoutException)
            {
                StopAndAbandon(process, readTask);
                return new RedirectedOutputWaitResult(ProcessExited: false, OutputDrained: false);
            }
            catch (OperationCanceledException)
            {
                StopAndAbandon(process, readTask);
                throw;
            }

            try
            {
                await WaitWithinDeadlineAsync(readTask, deadline, cancellationToken);
                return new RedirectedOutputWaitResult(ProcessExited: true, OutputDrained: true);
            }
            catch (TimeoutException)
            {
                AbandonRead(process, readTask);
                return new RedirectedOutputWaitResult(ProcessExited: true, OutputDrained: false);
            }
            catch (OperationCanceledException)
            {
                StopAndAbandon(process, readTask);
                throw;
            }
        }
        finally
        {
            exitSignalCancellation.Cancel();
            ObserveQuietly(exitTask);
            ObserveQuietly(readTask);
        }
    }

    private static async Task WaitWithinDeadlineAsync(
        Task task,
        MonotonicDeadline deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.IsCompleted)
        {
            await task;
            return;
        }

        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException();

        await task.WaitAsync(remaining, cancellationToken);
    }

    private static void StopAndAbandon(Process process, Task readTask)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            Trace.WriteLine($"BoundedProcessOutput.TryKillTree: {ex.GetType().Name}: {ex.Message}");
        }

        AbandonRead(process, readTask);
    }

    private static void AbandonRead(Process process, Task readTask)
    {
        if (!readTask.IsCompleted)
            DisposeQuietly(process.StandardOutput);
    }

    private static async Task WaitForProcessExitOnlyAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, EventArgs e) => exited.TrySetResult();

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        try
        {
            if (!process.HasExited)
                await exited.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try { disposable.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    internal readonly record struct RedirectedOutputWaitResult(
        bool ProcessExited,
        bool OutputDrained);

    private sealed class RedirectedOutputCapture
    {
        private readonly StreamReader _reader;
        private readonly Lock _lock = new();
        private readonly StringBuilder _output = new();

        internal RedirectedOutputCapture(StreamReader reader)
        {
            _reader = reader;
            Completion = CaptureAsync();
        }

        internal Task Completion { get; }

        internal string Output
        {
            get
            {
                lock (_lock)
                    return _output.ToString();
            }
        }

        private async Task CaptureAsync()
        {
            var buffer = new char[4_096];
            while (await _reader.ReadAsync(buffer) is var count && count > 0)
            {
                lock (_lock)
                    _output.Append(buffer, 0, count);
            }
        }
    }

    private readonly struct MonotonicDeadline
    {
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly TimeSpan _timeout;

        internal MonotonicDeadline(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        internal TimeSpan Remaining
        {
            get
            {
                var remaining = _timeout - Stopwatch.GetElapsedTime(_startedAt);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}
