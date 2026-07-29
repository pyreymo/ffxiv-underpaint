#if DEBUG
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FfxivVector4 = FFXIVClientStructs.FFXIV.Common.Math.Vector4;

namespace Underpaint.Internal;

internal sealed unsafe class AvfxPerformanceProbe : IDisposable
{
    private const string PoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string RunCallSignature = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string RemoveSignature =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private const int WarmupFrames = 180;
    private const int SampleFrames = 600;
    private const int CooldownFrames = 180;

    private readonly object sync = new();
    private readonly Process process = Process.GetCurrentProcess();
    private readonly IPluginLog log;
    private readonly StaticVfxRunDelegate run;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private readonly double[] frameSamples = new double[SampleFrames];
    private readonly double[] cpuSamples = new double[SampleFrames];
    private nint[] hosts = [];
    private ProbePhase phase;
    private int warmupFrameCount;
    private int sampleFrameCount;
    private int cooldownFrameCount;
    private double warmupFrameMax;
    private double warmupCpuMax;
    private double previousCpuMilliseconds;
    private double createMilliseconds;
    private double removeMilliseconds;
    private int benchmarkHostCount;
    private long privateBytesBefore;
    private long privateBytesActive;
    private long workingSetBefore;
    private long workingSetActive;
    private string status = "Ready.";
    private bool disposed;

    internal AvfxPerformanceProbe(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.log = log;
        run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(sigScanner.ScanText(RunCallSignature));
        removeHook = gameInteropProvider.HookFromSignature<StaticVfxRemoveDelegate>(RemoveSignature, RemoveDetour);
    }

    internal string Status
    {
        get
        {
            lock (sync)
                return status;
        }
    }

    internal void Start(string resourcePath, Vector3 sortingCenter, int hostCount, float spacing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (hostCount is not (0 or 8 or 32 or 128 or 512))
            throw new ArgumentOutOfRangeException(nameof(hostCount), "Host count must be 0, 8, 32, 128, or 512.");
        if (!IsFinite(sortingCenter) || !float.IsFinite(spacing) || spacing < 0)
            throw new ArgumentOutOfRangeException(nameof(sortingCenter), "Sorting center and spacing must be finite.");

        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (phase is not ProbePhase.Idle and not ProbePhase.Complete)
                throw new InvalidOperationException("Stop the active AVFX performance test before starting another run.");
            if (hosts.Length != 0)
                throw new InvalidOperationException("Stop the completed AVFX performance test before starting another run.");

            ResetMeasurements();
            benchmarkHostCount = hostCount;
            SnapshotMemory(out privateBytesBefore, out workingSetBefore);
            hosts = new nint[hostCount];
            removeHook.Enable();
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var side = hostCount == 0 ? 0 : (int)Math.Ceiling(Math.Sqrt(hostCount));
            var center = (side - 1) * 0.5f;
            for (var index = 0; index < hostCount; index++)
            {
                var vfx = VfxObject.Create(resourcePath, PoolName);
                if (vfx == null)
                    throw new InvalidOperationException($"VfxObject.Create returned null for host {index}.");

                lock (sync)
                    hosts[index] = (nint)vfx;
                run(vfx, 0f, uint.MaxValue);
                var row = index / side;
                var column = index % side;
                SetHost(
                    vfx,
                    sortingCenter + new Vector3((column - center) * spacing, 0f, (row - center) * spacing)
                );
            }

            stopwatch.Stop();
            lock (sync)
            {
                createMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                previousCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                phase = ProbePhase.Warmup;
                status = $"Warmup: hosts={hostCount}, frame=0/{WarmupFrames}, create={createMilliseconds:F2} ms.";
            }
            log.Information(
                "[Underpaint] AVFX performance started. Hosts={HostCount} SortingCenter={SortingCenter} Spacing={Spacing} CreateMs={CreateMs:F2}.",
                hostCount,
                sortingCenter,
                spacing,
                createMilliseconds
            );
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Update(TimeSpan frameDelta)
    {
        var frameMilliseconds = frameDelta.TotalMilliseconds;
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds < 0)
            return;

        bool finishSamples = false;
        lock (sync)
        {
            if (disposed || phase is ProbePhase.Idle or ProbePhase.Complete)
                return;

            var currentCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
            var cpuMilliseconds = Math.Max(0, currentCpuMilliseconds - previousCpuMilliseconds);
            previousCpuMilliseconds = currentCpuMilliseconds;

            switch (phase)
            {
                case ProbePhase.Warmup:
                    warmupFrameMax = Math.Max(warmupFrameMax, frameMilliseconds);
                    warmupCpuMax = Math.Max(warmupCpuMax, cpuMilliseconds);
                    warmupFrameCount++;
                    if (warmupFrameCount >= WarmupFrames)
                    {
                        SnapshotMemory(out privateBytesActive, out workingSetActive);
                        phase = ProbePhase.Sampling;
                        previousCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
                    }
                    status = $"Warmup: hosts={hosts.Length}, frame={warmupFrameCount}/{WarmupFrames}, create={createMilliseconds:F2} ms.";
                    break;
                case ProbePhase.Sampling:
                    frameSamples[sampleFrameCount] = frameMilliseconds;
                    cpuSamples[sampleFrameCount] = cpuMilliseconds;
                    sampleFrameCount++;
                    status = $"Sampling: hosts={hosts.Length}, frame={sampleFrameCount}/{SampleFrames}.";
                    finishSamples = sampleFrameCount >= SampleFrames;
                    break;
                case ProbePhase.Cooldown:
                    cooldownFrameCount++;
                    if (cooldownFrameCount >= CooldownFrames)
                        Complete();
                    else
                        status = $"Cooldown: frame={cooldownFrameCount}/{CooldownFrames}, remove={removeMilliseconds:F2} ms.";
                    break;
            }
        }

        if (finishSamples)
            FinishSamples();
    }

    internal void Stop()
    {
        nint[] activeHosts;
        lock (sync)
        {
            if (hosts.Length == 0)
            {
                phase = ProbePhase.Idle;
                removeHook.Disable();
                return;
            }

            activeHosts = hosts;
            hosts = [];
            phase = ProbePhase.Idle;
        }

        var stopwatch = Stopwatch.StartNew();
        removeHook.Disable();
        foreach (var address in activeHosts)
        {
            if (address != 0)
                removeHook.Original((VfxObject*)address);
        }
        stopwatch.Stop();

        lock (sync)
        {
            removeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            status = $"Stopped: hosts={activeHosts.Length}, remove={removeMilliseconds:F2} ms.";
        }
        log.Information(
            "[Underpaint] AVFX performance stopped. Hosts={HostCount} RemoveMs={RemoveMs:F2}.",
            benchmarkHostCount,
            removeMilliseconds
        );
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
        }

        Stop();
        lock (sync)
            disposed = true;
        removeHook.Dispose();
        process.Dispose();
    }

    private void FinishSamples()
    {
        nint[] activeHosts;
        lock (sync)
        {
            if (phase != ProbePhase.Sampling)
                return;
            activeHosts = hosts;
            hosts = [];
            phase = ProbePhase.Cooldown;
        }

        var stopwatch = Stopwatch.StartNew();
        removeHook.Disable();
        foreach (var address in activeHosts)
        {
            if (address != 0)
                removeHook.Original((VfxObject*)address);
        }
        stopwatch.Stop();

        lock (sync)
        {
            removeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            status = $"Cooldown: frame=0/{CooldownFrames}, remove={removeMilliseconds:F2} ms.";
        }
    }

    private void Complete()
    {
        SnapshotMemory(out var privateBytesAfter, out var workingSetAfter);
        var frame = Summarize(frameSamples);
        var cpu = Summarize(cpuSamples);
        status =
            $"Complete: hosts={benchmarkHostCount}, create={createMilliseconds:F2} ms, remove={removeMilliseconds:F2} ms, "
            + $"loadMax={warmupFrameMax:F2} ms/{warmupCpuMax:F2} CPU-ms.\n"
            + $"Frame ms: avg={frame.Average:F2}, p95={frame.P95:F2}, p99={frame.P99:F2}, max={frame.Max:F2}.\n"
            + $"Process CPU ms/frame: avg={cpu.Average:F2}, p95={cpu.P95:F2}, p99={cpu.P99:F2}, max={cpu.Max:F2}.\n"
            + $"Private bytes: active={FormatBytes(privateBytesActive - privateBytesBefore)}, "
            + $"after={FormatBytes(privateBytesAfter - privateBytesBefore)}; "
            + $"working set: active={FormatBytes(workingSetActive - workingSetBefore)}, "
            + $"after={FormatBytes(workingSetAfter - workingSetBefore)}.";
        phase = ProbePhase.Complete;
        log.Information("[Underpaint] AVFX performance result. {Result}", status.Replace('\n', ' '));
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            for (var index = 0; index < hosts.Length; index++)
            {
                if (hosts[index] != (nint)vfx)
                    continue;
                hosts[index] = 0;
                break;
            }
        }
        return removeHook.Original(vfx);
    }

    private void ResetMeasurements()
    {
        Array.Clear(frameSamples);
        Array.Clear(cpuSamples);
        warmupFrameCount = 0;
        sampleFrameCount = 0;
        cooldownFrameCount = 0;
        warmupFrameMax = 0;
        warmupCpuMax = 0;
        createMilliseconds = 0;
        removeMilliseconds = 0;
        privateBytesBefore = 0;
        privateBytesActive = 0;
        workingSetBefore = 0;
        workingSetActive = 0;
    }

    private void SnapshotMemory(out long privateBytes, out long workingSet)
    {
        process.Refresh();
        privateBytes = process.PrivateMemorySize64;
        workingSet = process.WorkingSet64;
    }

    private static SampleSummary Summarize(double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return new SampleSummary(
            samples.Average(),
            sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
            sorted[(int)Math.Ceiling(sorted.Length * 0.99) - 1],
            sorted[^1]
        );
    }

    private static string FormatBytes(long bytes) => $"{bytes / (1024d * 1024d):+0.00;-0.00;0.00} MiB";

    private static void SetHost(VfxObject* vfx, Vector3 position)
    {
        vfx->Position = new FfxivVector3
        {
            X = position.X,
            Y = position.Y,
            Z = position.Z,
        };
        vfx->Rotation = FfxivQuaternion.Identity;
        vfx->Color = new FfxivVector4
        {
            X = 1f,
            Y = 1f,
            Z = 1f,
            W = 0.5f,
        };
        vfx->UpdateTransforms(true);
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);

    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);

    private enum ProbePhase
    {
        Idle,
        Warmup,
        Sampling,
        Cooldown,
        Complete,
    }

    private readonly record struct SampleSummary(double Average, double P95, double P99, double Max);
}
#endif
