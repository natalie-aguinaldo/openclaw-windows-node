using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Presentation;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    [Fact]
    public async Task UnsupportedHardware_DisablesEveryOptionAndCommand()
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
        Assert.True(viewModel.AreOptionsEnabled);

        await ActivateAndWaitForAvailabilityAsync(viewModel);

        Assert.True(viewModel.IsAvailabilityKnown);
        Assert.False(viewModel.IsLocalAiAvailable);
        Assert.False(viewModel.AreOptionsEnabled);
        Assert.Contains("NVIDIA GPU", viewModel.LocalAiUnavailableReason);
        Assert.False(viewModel.CanStart);
        Assert.False(viewModel.CanStop);
        Assert.False(viewModel.CanRestart);
        Assert.False(viewModel.CanOpenLogs);
        Assert.False(viewModel.CanRetrySetup);
        Assert.False(viewModel.CanRepairConnection);
        Assert.False(viewModel.CanOpenChat);
        Assert.False(viewModel.OpenLogs());
        Assert.False(viewModel.RetrySetup());
        Assert.False(viewModel.RepairConnection());
        Assert.False(viewModel.OpenChat());
        Assert.Equal(0, commands.OpenLocalAiLogsCount);
        Assert.Equal(0, commands.ShowOnboardingCount);
        Assert.Equal(0, commands.ReconnectCount);
        Assert.Equal(0, commands.ShowChatCount);
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
        Assert.True(viewModel.AreOptionsEnabled);
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

    private static async Task ActivateAndWaitForAvailabilityAsync(LocalAiPageViewModel viewModel)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, _) =>
        {
            if (viewModel.IsAvailabilityKnown)
                completion.TrySetResult();
        };

        viewModel.Activate(null);
        if (!viewModel.IsAvailabilityKnown)
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

    private static LocalAiRuntimeSnapshot CreateInstalledSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string modelId = LocalModelCatalog.Models[0].Id;
        return new LocalAiRuntimeSnapshot(
            LocalAiRuntimeState.Healthy,
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

    private sealed class FakeLocalAiRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
