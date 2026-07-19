#if DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;

namespace Underpaint.Internal;

internal readonly record struct TransparentDrawArguments(
    string DrawType,
    uint ElementCount,
    uint InstanceCount,
    uint StartIndex,
    int BaseVertex,
    uint StartVertex,
    uint StartInstance
);

internal sealed unsafe partial class D3D11GBufferBackend
{
    private const int TransparentCaptureConstantBufferSlots = 8;
    private const int TransparentCaptureShaderResourceSlots = 16;
    private const int TransparentCaptureVertexBufferSlots = 4;
    private const int TransparentCaptureMaxHashedBufferBytes = 16 * 1024;
    private TransparentCaptureSession? transparentCapture;
    private TransparentDrawCapture? completedTransparentCapture;
    private long transparentCaptureSequence;
    private bool transparentStageCActive;
    private NativeGeometryDrawCaptureSession? nativeGeometryDrawCapture;
    private NativeGeometryDrawCapture? completedNativeGeometryDrawCapture;
    private long nativeGeometryDrawSequence;

    private sealed class NativeGeometryDrawCaptureSession(
        nint targetVertexBuffer,
        nint targetIndexBuffer
    )
    {
        public nint TargetVertexBuffer { get; } = targetVertexBuffer;
        public nint TargetIndexBuffer { get; } = targetIndexBuffer;
        public List<NativeGeometryDrawMatch> Draws { get; } = [];
    }

    private sealed class TransparentCaptureSession(
        int maxDraws,
        int maxStageAFrames,
        CaptureModule[] modules
    )
    {
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public int MaxDraws { get; } = maxDraws;
        public int MaxStageAFrames { get; } = maxStageAFrames;
        public CaptureModule[] Modules { get; } = modules;
        public List<TransparentNativeDrawSnapshot> Draws { get; } = new(maxDraws);
        public HashSet<long> StageAPasses { get; } = [];
        public int StageADraws;
        public int StageCDraws;
    }

    private readonly record struct CaptureModule(nint Start, nint End, string Name);

    public void BeginTransparentDrawCapture(int maxDraws, int maxStageAFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDraws, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDraws, 2048);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStageAFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxStageAFrames, 60);

        var modules = CaptureModules();
        lock (stateLock)
        {
            transparentCapture = new TransparentCaptureSession(maxDraws, maxStageAFrames, modules);
            completedTransparentCapture = null;
        }
    }

    public void BeginNativeGeometryDrawCapture(nint vertexBuffer, nint indexBuffer)
    {
        if (vertexBuffer == 0)
            throw new ArgumentNullException(nameof(vertexBuffer));
        if (indexBuffer == 0)
            throw new ArgumentNullException(nameof(indexBuffer));

        lock (stateLock)
        {
            nativeGeometryDrawCapture = new NativeGeometryDrawCaptureSession(
                vertexBuffer,
                indexBuffer
            );
            completedNativeGeometryDrawCapture = null;
        }
    }

    public void CompleteNativeGeometryDrawCapture(string reason)
    {
        lock (stateLock)
        {
            if (nativeGeometryDrawCapture is not { } capture)
                return;
            completedNativeGeometryDrawCapture = new NativeGeometryDrawCapture(
                capture.TargetVertexBuffer,
                capture.TargetIndexBuffer,
                reason,
                capture.Draws.ToArray()
            );
            nativeGeometryDrawCapture = null;
        }
    }

    public bool TryTakeNativeGeometryDrawCapture(out NativeGeometryDrawCapture capture)
    {
        lock (stateLock)
        {
            if (completedNativeGeometryDrawCapture == null)
            {
                capture = null!;
                return false;
            }

            capture = completedNativeGeometryDrawCapture;
            completedNativeGeometryDrawCapture = null;
            return true;
        }
    }

    public void CancelTransparentDrawCapture(string reason)
    {
        lock (stateLock)
        {
            CompleteTransparentCapture(reason.Length == 0 ? "cancelled" : reason);
        }
    }

    public TransparentDrawCaptureStatus GetTransparentDrawCaptureStatus()
    {
        lock (stateLock)
        {
            if (transparentCapture is { } active)
            {
                return new TransparentDrawCaptureStatus(
                    TransparentDrawCaptureState.Capturing,
                    active.Draws.Count,
                    active.StageAPasses.Count,
                    null
                );
            }

            if (completedTransparentCapture is { } completed)
            {
                return new TransparentDrawCaptureStatus(
                    TransparentDrawCaptureState.Completed,
                    completed.Draws.Count,
                    completed.CapturedStageAFrames,
                    completed.Reason
                );
            }

            return new TransparentDrawCaptureStatus(TransparentDrawCaptureState.Idle, 0, 0, null);
        }
    }

    public bool TryTakeTransparentDrawCapture(out TransparentDrawCapture capture)
    {
        lock (stateLock)
        {
            if (completedTransparentCapture == null)
            {
                capture = null!;
                return false;
            }

            capture = completedTransparentCapture;
            completedTransparentCapture = null;
            return true;
        }
    }

    private void TryCaptureTransparentDraw(nint context, TransparentDrawArguments arguments)
    {
        if (detouring || context != immediateContextPointer)
        {
            return;
        }

        lock (stateLock)
        {
            var capture = transparentCapture;
            if (capture == null)
            {
                return;
            }

            TransparentDrawStage? stage = null;
            if (activePass?.Kind == GBufferTarget.Semitransparent)
            {
                stage = TransparentDrawStage.StageA;
                capture.StageAPasses.Add(activePass.PassInstanceId);
                if (capture.StageAPasses.Count > capture.MaxStageAFrames)
                {
                    CompleteTransparentCapture("stage-a-frame-limit");
                    return;
                }
            }
            else if (transparentStageCActive)
            {
                stage = TransparentDrawStage.StageC;
            }

            if (stage == null)
            {
                return;
            }

            var perStageLimit = Math.Max(1, capture.MaxDraws / 2);
            if (
                (stage == TransparentDrawStage.StageA && capture.StageADraws >= perStageLimit)
                || (stage == TransparentDrawStage.StageC && capture.StageCDraws >= perStageLimit)
            )
            {
                return;
            }

            try
            {
                capture.Draws.Add(CaptureTransparentDraw(stage.Value, arguments, capture.Modules));
                if (stage == TransparentDrawStage.StageA)
                    capture.StageADraws++;
                else
                    capture.StageCDraws++;
                if (capture.StageADraws >= perStageLimit && capture.StageCDraws >= perStageLimit)
                {
                    CompleteTransparentCapture("per-stage-draw-limits");
                }
            }
            catch (Exception exception)
            {
                log.Warning(exception, "[Underpaint] Transparent draw capture failed.");
                CompleteTransparentCapture($"capture-error:{exception.GetType().Name}");
            }
        }
    }

    private void TryCaptureNativeGeometryDraw(nint context, TransparentDrawArguments arguments)
    {
        if (detouring || context != immediateContextPointer)
            return;

        lock (stateLock)
        {
            var capture = nativeGeometryDrawCapture;
            if (capture == null || capture.Draws.Count >= 32)
                return;

            var vertexBuffers = CaptureVertexBuffers();
            var indexBuffer = CaptureIndexBuffer();
            if (
                vertexBuffers.All(item => item.Buffer != capture.TargetVertexBuffer)
                || indexBuffer?.Buffer != capture.TargetIndexBuffer
            )
            {
                return;
            }

            nint vertexShader;
            nint pixelShader;
            nint inputLayout;
            using (var shader = immediateContext.VertexShader.Get())
                vertexShader = shader?.NativePointer ?? 0;
            using (var shader = immediateContext.PixelShader.Get())
                pixelShader = shader?.NativePointer ?? 0;
            var nativeInputLayout = immediateContext.InputAssembler.InputLayout;
            using (nativeInputLayout)
                inputLayout = nativeInputLayout?.NativePointer ?? 0;
            var vertexConstants = CaptureNativeConstantBuffers(immediateContext.VertexShader);
            var pixelConstants = CaptureNativeConstantBuffers(immediateContext.PixelShader);
            var shaderResources = CaptureShaderResources();

            var pass =
                activePass?.Kind.ToString()
                ?? (transparentStageCActive ? "SemitransparentStageC" : "Other");
            capture.Draws.Add(
                new NativeGeometryDrawMatch(
                    Interlocked.Increment(ref nativeGeometryDrawSequence),
                    Stopwatch.GetTimestamp(),
                    Environment.CurrentManagedThreadId,
                    pass,
                    arguments.DrawType,
                    arguments.ElementCount,
                    arguments.InstanceCount,
                    arguments.StartIndex,
                    arguments.BaseVertex,
                    arguments.StartVertex,
                    arguments.StartInstance,
                    vertexShader,
                    pixelShader,
                    inputLayout,
                    vertexBuffers
                        .Select(item => new NativeGeometryVertexBufferBinding(
                            item.Slot,
                            item.Buffer,
                            item.Stride,
                            item.Offset
                        ))
                        .ToArray(),
                    indexBuffer.Value.Buffer,
                    indexBuffer.Value.Format,
                    indexBuffer.Value.Offset,
                    vertexConstants,
                    pixelConstants,
                    shaderResources
                        .Select(item => new NativeGeometryShaderResourceBinding(
                            item.Slot,
                            item.View,
                            item.Resource
                        ))
                        .ToArray()
                )
            );
        }
    }

    private TransparentNativeDrawSnapshot CaptureTransparentDraw(
        TransparentDrawStage stage,
        TransparentDrawArguments arguments,
        CaptureModule[] modules
    )
    {
        nint vertexShader;
        nint pixelShader;
        nint inputLayout;
        using (var shader = immediateContext.VertexShader.Get())
            vertexShader = shader?.NativePointer ?? 0;
        using (var shader = immediateContext.PixelShader.Get())
            pixelShader = shader?.NativePointer ?? 0;
        var nativeInputLayout = immediateContext.InputAssembler.InputLayout;
        using (nativeInputLayout)
            inputLayout = nativeInputLayout?.NativePointer ?? 0;

        var vertexBuffers = CaptureVertexBuffers();
        var indexBuffer = CaptureIndexBuffer();
        var vertexConstants = CaptureConstantBuffers(immediateContext.VertexShader);
        var pixelConstants = CaptureConstantBuffers(immediateContext.PixelShader);
        var shaderResources = CaptureShaderResources();
        var nativeStack = CaptureNativeStack(modules);

        return new TransparentNativeDrawSnapshot(
            Interlocked.Increment(ref transparentCaptureSequence),
            Stopwatch.GetTimestamp(),
            stage,
            Environment.CurrentManagedThreadId,
            arguments.DrawType,
            arguments.ElementCount,
            arguments.InstanceCount,
            arguments.StartIndex,
            arguments.BaseVertex,
            arguments.StartVertex,
            arguments.StartInstance,
            vertexShader,
            pixelShader,
            inputLayout,
            vertexBuffers,
            indexBuffer,
            vertexConstants,
            pixelConstants,
            shaderResources,
            nativeStack
        );
    }

    private TransparentVertexBufferSnapshot[] CaptureVertexBuffers()
    {
        var buffers = new D3D11Buffer[TransparentCaptureVertexBufferSlots];
        var strides = new int[TransparentCaptureVertexBufferSlots];
        var offsets = new int[TransparentCaptureVertexBufferSlots];
        immediateContext.InputAssembler.GetVertexBuffers(
            0,
            buffers.Length,
            buffers,
            strides,
            offsets
        );
        try
        {
            return buffers
                .Select((buffer, slot) => (buffer, slot))
                .Where(item => item.buffer != null)
                .Select(item => new TransparentVertexBufferSnapshot(
                    item.slot,
                    item.buffer.NativePointer,
                    strides[item.slot],
                    offsets[item.slot]
                ))
                .ToArray();
        }
        finally
        {
            foreach (var buffer in buffers)
                buffer?.Dispose();
        }
    }

    private TransparentIndexBufferSnapshot? CaptureIndexBuffer()
    {
        immediateContext.InputAssembler.GetIndexBuffer(
            out var buffer,
            out Format format,
            out var offset
        );
        using (buffer)
        {
            return buffer == null
                ? null
                : new TransparentIndexBufferSnapshot(
                    buffer.NativePointer,
                    format.ToString(),
                    offset
                );
        }
    }

    private TransparentConstantBufferSnapshot[] CaptureConstantBuffers(CommonShaderStage stage)
    {
        var buffers = stage.GetConstantBuffers(0, TransparentCaptureConstantBufferSlots);
        try
        {
            var snapshots = new List<TransparentConstantBufferSnapshot>(buffers.Length);
            var hashesCaptured = 0;
            for (var slot = 0; slot < buffers.Length; slot++)
            {
                var buffer = buffers[slot];
                if (buffer == null)
                    continue;

                var byteWidth = buffer.Description.SizeInBytes;
                ulong? hash = null;
                if (hashesCaptured == 0 && byteWidth <= TransparentCaptureMaxHashedBufferBytes)
                {
                    hash = ComputeFnv1A64(ReadConstantBuffer(buffer));
                    hashesCaptured++;
                }
                snapshots.Add(
                    new TransparentConstantBufferSnapshot(
                        slot,
                        buffer.NativePointer,
                        byteWidth,
                        hash
                    )
                );
            }
            return snapshots.ToArray();
        }
        finally
        {
            foreach (var buffer in buffers)
                buffer?.Dispose();
        }
    }

    private NativeGeometryConstantBufferBinding[] CaptureNativeConstantBuffers(
        CommonShaderStage stage
    )
    {
        var buffers = stage.GetConstantBuffers(0, TransparentCaptureConstantBufferSlots);
        try
        {
            var snapshots = new List<NativeGeometryConstantBufferBinding>(buffers.Length);
            for (var slot = 0; slot < buffers.Length; slot++)
            {
                var buffer = buffers[slot];
                if (buffer == null)
                    continue;

                var byteWidth = buffer.Description.SizeInBytes;
                ulong? hash = null;
                ulong? firstHalfHash = null;
                ulong? secondHalfHash = null;
                if (byteWidth <= TransparentCaptureMaxHashedBufferBytes)
                {
                    var bytes = ReadConstantBuffer(buffer);
                    hash = ComputeFnv1A64(bytes);
                    if (bytes.Length == 128)
                    {
                        firstHalfHash = ComputeFnv1A64(bytes[..64]);
                        secondHalfHash = ComputeFnv1A64(bytes[64..]);
                    }
                }

                snapshots.Add(
                    new NativeGeometryConstantBufferBinding(
                        slot,
                        buffer.NativePointer,
                        byteWidth,
                        hash,
                        firstHalfHash,
                        secondHalfHash
                    )
                );
            }
            return snapshots.ToArray();
        }
        finally
        {
            foreach (var buffer in buffers)
                buffer?.Dispose();
        }
    }

    private TransparentShaderResourceSnapshot[] CaptureShaderResources()
    {
        var views = immediateContext.PixelShader.GetShaderResources(
            0,
            TransparentCaptureShaderResourceSlots
        );
        try
        {
            var snapshots = new List<TransparentShaderResourceSnapshot>(views.Length);
            for (var slot = 0; slot < views.Length; slot++)
            {
                var view = views[slot];
                if (view == null)
                    continue;
                using var resource = view.Resource;
                snapshots.Add(
                    new TransparentShaderResourceSnapshot(
                        slot,
                        view.NativePointer,
                        resource?.NativePointer ?? 0
                    )
                );
            }
            return snapshots.ToArray();
        }
        finally
        {
            foreach (var view in views)
                view?.Dispose();
        }
    }

    private void CompleteTransparentCapture(string reason)
    {
        if (transparentCapture == null)
            return;

        completedTransparentCapture = new TransparentDrawCapture(
            transparentCapture.StartedAt,
            DateTimeOffset.UtcNow,
            reason,
            transparentCapture.StageAPasses.Count,
            transparentCapture.Draws.ToArray()
        );
        transparentCapture = null;
    }

    private void NotifyOutputTargetsChangedForTransparentCapture() =>
        transparentStageCActive = false;

    private void NotifyTransparentLightBuffersBound() => transparentStageCActive = true;

    private static CaptureModule[] CaptureModules()
    {
        using var process = Process.GetCurrentProcess();
        return process
            .Modules.Cast<ProcessModule>()
            .Select(module => new CaptureModule(
                module.BaseAddress,
                module.BaseAddress + module.ModuleMemorySize,
                module.ModuleName
            ))
            .OrderBy(module => module.Start)
            .ToArray();
    }

    private static string[] CaptureNativeStack(CaptureModule[] modules)
    {
        var frames = stackalloc nint[24];
        // The draw detour enters managed code through a reverse-P/Invoke boundary. Skipping
        // frames here can consume the whole unwindable native chain before it is recorded.
        var count = RtlCaptureStackBackTrace(0, 24, frames, null);
        if (count == 0)
            return ["unavailable:rtl-capture-returned-zero"];

        var result = new string[count];
        for (var index = 0; index < count; index++)
        {
            var address = frames[index];
            var module = modules.FirstOrDefault(candidate =>
                address >= candidate.Start && address < candidate.End
            );
            result[index] =
                module.Name == null
                    ? $"0x{address:X}"
                    : $"{module.Name}+0x{address - module.Start:X}";
        }
        return result;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort RtlCaptureStackBackTrace(
        uint framesToSkip,
        uint framesToCapture,
        nint* backTrace,
        uint* backTraceHash
    );
}
#else
namespace Underpaint.Internal;

internal readonly record struct TransparentDrawArguments(
    string DrawType,
    uint ElementCount,
    uint InstanceCount,
    uint StartIndex,
    int BaseVertex,
    uint StartVertex,
    uint StartInstance
);

internal sealed unsafe partial class D3D11GBufferBackend
{
    public void BeginNativeGeometryDrawCapture(nint vertexBuffer, nint indexBuffer) { }

    public void CompleteNativeGeometryDrawCapture(string reason) { }

    public bool TryTakeNativeGeometryDrawCapture(out NativeGeometryDrawCapture capture)
    {
        capture = null!;
        return false;
    }

    private void TryCaptureNativeGeometryDraw(nint context, TransparentDrawArguments arguments) { }

    private static void TryCaptureTransparentDraw(
        nint context,
        TransparentDrawArguments arguments
    ) { }

    private static void NotifyOutputTargetsChangedForTransparentCapture() { }

    private static void NotifyTransparentLightBuffersBound() { }
}
#endif
