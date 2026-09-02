using System.Diagnostics;

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

        var stdoutTask = startInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        var stderrTask = startInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync()
            : null;

        try
        {
            var output = await AwaitRedirectedOutputAsync(
                process,
                stdoutTask,
                timeoutMs,
                cancellationToken);
            return output is not null && process.HasExited
                ? (process.ExitCode, output)
                : (-1, string.Empty);
        }
        finally
        {
            ObserveQuietly(stdoutTask);
            if (stderrTask is not null)
            {
                if (!stderrTask.IsCompleted)
                    DisposeQuietly(process.StandardError);
                ObserveQuietly(stderrTask);
            }
        }
    }

    internal static async Task<string?> AwaitRedirectedOutputAsync(
        Process process,
        Task<string> readTask,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(readTask);
        if (timeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var deadline = new MonotonicDeadline(TimeSpan.FromMilliseconds(timeoutMs));
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        try
        {
            await WaitWithinDeadlineAsync(exitTask, deadline, cancellationToken);
            await WaitWithinDeadlineAsync(readTask, deadline, cancellationToken);
            return await readTask;
        }
        catch (TimeoutException)
        {
            StopAndAbandon(process, readTask);
            return null;
        }
        catch (OperationCanceledException)
        {
            StopAndAbandon(process, readTask);
            throw;
        }
        finally
        {
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

        if (!readTask.IsCompleted)
            DisposeQuietly(process.StandardOutput);
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
