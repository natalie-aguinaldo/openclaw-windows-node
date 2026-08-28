using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Presentation;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    [Fact]
    public async Task UnsupportedHardware_KeepsExistingRuntimeManagementAvailable()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Idle,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsSetupAvailable);

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.IsSetupAvailable);
        Assert.Contains("NVIDIA GPU", viewModel.LocalAiUnavailableReason);
        Assert.False(viewModel.CanStart);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.False(viewModel.CanRetrySetup);
        Assert.True(viewModel.CanRepairConnection);
        Assert.False(viewModel.CanOpenChat);
        Assert.True(await viewModel.StopAsync());
        Assert.True(await viewModel.RestartAsync());
        Assert.True(viewModel.OpenLogs());
        Assert.False(viewModel.RetrySetup());
        Assert.True(viewModel.RepairConnection());
        Assert.False(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(0, commands.ShowOnboardingCount);
        Assert.Equal(1, commands.ReconnectCount);
        Assert.Equal(0, commands.ShowChatCount);
        Assert.Equal(1, runtime.StopCount);
        Assert.Equal(1, runtime.RestartCount);
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsChatAvailableForHealthyConnectedRuntime()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Connected,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public async Task UnsupportedHardware_KeepsInstalledStoppedRuntimeStartAvailable()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot(LocalAiRuntimeState.Stopped));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanStart);
        Assert.True(await viewModel.StartAsync());
        Assert.Equal(1, runtime.StartCount);
    }

    [Fact]
    public async Task UnsupportedHardware_BlocksFreshSetupRetry()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(HostHardwareInfo.Unknown));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.False(viewModel.IsSetupAvailable);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CanRetrySetup);
    }

    [Fact]
    public async Task ProbeFailure_UsesUnknownStateUntilRecheckSucceeds()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new SequencedHardwareProbe(
                () => throw new InvalidOperationException("probe failed"),
                CreateQualifiedHardware));

        Assert.False(viewModel.RecheckAvailability());

        await ActivateAndWaitForAvailabilityResultAsync(viewModel);

        Assert.False(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.HasAvailabilityProbeError);
        Assert.True(viewModel.ShowAvailabilityInfoBar);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.True(viewModel.CanRetrySetup);
        Assert.True(viewModel.CanRecheckAvailability);
        Assert.Contains("could not read", viewModel.LocalAiUnavailableReason, StringComparison.OrdinalIgnoreCase);

        Assert.True(viewModel.RecheckAvailability());
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.False(viewModel.ShowAvailabilityInfoBar);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.False(viewModel.CanRecheckAvailability);
        Assert.Null(viewModel.LocalAiUnavailableReason);
    }

    [Fact]
    public async Task ProbeFailure_PublishesChangeWhenRecheckBecomesAvailable()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            new SequencedHardwareProbe(
                () => throw new InvalidOperationException("probe failed")));

        var observed = new List<(bool HasError, bool CanRecheck)>();
        var recheckAvailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, _) =>
        {
            var snapshot = (viewModel.HasAvailabilityProbeError, viewModel.CanRecheckAvailability);
            observed.Add(snapshot);
            if (snapshot is (true, true))
                recheckAvailable.TrySetResult();
        };

        viewModel.Activate(null);
        await recheckAvailable.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(observed, state => state is (true, false));
        Assert.Contains(observed, state => state is (true, true));
    }

    [Fact]
    public async Task QualifiedHardware_EnablesApplicableOptionsAndRoutesActions()
    {
        var runtime = new FakeLocalAiRuntime(CreateInstalledSnapshot());
        var runtimeHost = new FakePermissionsPageRuntimeHost
        {
            ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
            {
                OperatorState = RoleConnectionState.Connected,
            },
        };
        using var gatewaySource = new PermissionsPageRuntimeSource(runtimeHost);
        var commands = new FakeAppCommands();
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            commands,
            new RecordingUiDispatcher(),
            new FixedHardwareProbe(CreateQualifiedHardware()));

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.True(viewModel.IsSetupAvailable);
        Assert.Null(viewModel.LocalAiUnavailableReason);
        Assert.True(viewModel.CanStop);
        Assert.True(viewModel.CanRestart);
        Assert.True(viewModel.CanOpenLogs);
        Assert.True(viewModel.CanOpenChat);
        Assert.True(viewModel.OpenLogs());
        Assert.True(viewModel.OpenChat());
        Assert.Equal(1, commands.OpenLocalAiLogsCount);
        Assert.Equal(1, commands.ShowChatCount);
    }

    [Fact]
    public async Task StaleAvailabilityProbe_DoesNotOverwriteNewerResult()
    {
        var runtime = new FakeLocalAiRuntime(LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"),
            DateTimeOffset.UtcNow));
        using var gatewaySource = new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost());
        var probe = new BlockingFirstHardwareProbe(CreateQualifiedHardware);
        using var viewModel = new LocalAiPageViewModel(
            runtime,
            gatewaySource,
            new FakeAppCommands(),
            new RecordingUiDispatcher(),
            probe);

        viewModel.Activate(null);
        await probe.FirstProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.Deactivate();
        viewModel.Activate(null);
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown && viewModel.IsLocalAiAvailable);

        probe.ReleaseFirstProbe();
        await Task.Delay(100);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.True(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.HasAvailabilityProbeError);
        Assert.Null(viewModel.LocalAiUnavailableReason);
    }

    private static async Task ActivateAndWaitForAvailabilityAsync(LocalAiPageViewModel viewModel)
    {
        await ActivateAndWaitForAvailabilityResultAsync(viewModel);
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown);
    }

    private static async Task ActivateAndWaitForAvailabilityResultAsync(LocalAiPageViewModel viewModel)
    {
        viewModel.Activate(null);
        await WaitForAsync(viewModel, () => viewModel.IsAvailabilityKnown || viewModel.HasAvailabilityProbeError);
    }

    private static async Task WaitForAsync(LocalAiPageViewModel viewModel, Func<bool> condition)
    {
        if (condition())
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (!condition())
                return;
            viewModel.PropertyChanged -= handler;
            completion.TrySetResult();
        };
        viewModel.PropertyChanged += handler;
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static HostHardwareInfo CreateQualifiedHardware() =>
        new(
            Architecture.X64,
            TotalPhysicalMemoryBytes: 256_000_000_000,
            AvailablePhysicalMemoryBytes: 128_000_000_000,
            Gpus:
            [
                new GpuInfo(
                    GpuVendor.Nvidia,
                    "NVIDIA Test GPU",
                    GpuVisibleMemoryBytes: 128_000_000_000,
                    FreeGpuVisibleMemoryBytes: 128_000_000_000,
                    DriverVersion: "620.0",
                    CudaMajorVersion: 13,
                    StableId: "GPU-test"),
            ],
            VulkanAvailable: false);

    private static LocalAiRuntimeSnapshot CreateInstalledSnapshot(
        LocalAiRuntimeState state = LocalAiRuntimeState.Healthy)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string modelId = LocalModelCatalog.Models[0].Id;
        return new LocalAiRuntimeSnapshot(
            state,
            LocalAiOwnership.CompanionManaged,
            new Uri("http://127.0.0.1:18080"),
            "test",
            modelId,
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Verified,
                now,
                new string('0', 64),
                sizeBytes: 1),
            ProcessId: 1234,
            ProcessStartedAtUtc: now,
            Detail: null,
            UpdatedAtUtc: now);
    }

    private sealed class FixedHardwareProbe(HostHardwareInfo hardware) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => hardware;
    }

    private sealed class SequencedHardwareProbe(params Func<HostHardwareInfo>[] attempts) : IHostHardwareProbe
    {
        private readonly Queue<Func<HostHardwareInfo>> _attempts = new(attempts);

        public HostHardwareInfo Probe()
        {
            if (_attempts.Count == 0)
                throw new InvalidOperationException("No probe attempts configured.");
            return _attempts.Dequeue().Invoke();
        }
    }

    private sealed class BlockingFirstHardwareProbe(Func<HostHardwareInfo> secondAttempt) : IHostHardwareProbe
    {
        private readonly TaskCompletionSource _firstProbeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstProbe =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public TaskCompletionSource FirstProbeStarted => _firstProbeStarted;

        public HostHardwareInfo Probe()
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                _firstProbeStarted.TrySetResult();
                _releaseFirstProbe.Task.GetAwaiter().GetResult();
                return HostHardwareInfo.Unknown;
            }

            return secondAttempt();
        }

        public void ReleaseFirstProbe() => _releaseFirstProbe.TrySetResult();
    }

    private sealed class FakeLocalAiRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int RestartCount { get; private set; }
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default)
        {
            RestartCount++;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
