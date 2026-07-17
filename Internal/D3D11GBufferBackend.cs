using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Shader;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;
using D3D11Device = SharpDX.Direct3D11.Device;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;
using RawRectangle = SharpDX.Mathematics.Interop.RawRectangle;

namespace Underpaint.Internal;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct GBufferVertex
{
    public readonly NumericsVector3 Position;
    public readonly NumericsVector3 Normal;
    public readonly NumericsVector4 Color;
    public readonly NumericsVector2 TexCoord;

    public GBufferVertex(
        NumericsVector3 position,
        NumericsVector3 normal,
        NumericsVector4 color,
        NumericsVector2 texCoord
    )
    {
        Position = position;
        Normal = normal;
        Color = color;
        TexCoord = texCoord;
    }
}

internal sealed class GBufferDrawCommand : IDisposable
{
    public GBufferVertex[] Vertices { get; }
    public ShaderResourceView? Texture { get; }

    public GBufferDrawCommand(GBufferVertex[] vertices, ShaderResourceView? texture = null)
    {
        Vertices = vertices;
        Texture = texture;
    }

    public void Dispose()
    {
        Texture?.Dispose();
    }
}

internal sealed class GBufferFrame
{
    private int referenceCount = 1;

    public GBufferDrawCommand[] Commands { get; }
    public GBufferMaterial Material { get; }
    public SemitransparentLighting Lighting { get; }
    public long CycleId { get; }

    public GBufferFrame(
        GBufferDrawCommand[] commands,
        GBufferMaterial material,
        SemitransparentLighting lighting,
        long cycleId
    )
    {
        Commands = commands;
        Material = material;
        Lighting = lighting;
        CycleId = cycleId;
    }

    public GBufferFrame Retain()
    {
        Interlocked.Increment(ref referenceCount);
        return this;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref referenceCount) != 0)
        {
            return;
        }

        foreach (var command in Commands)
        {
            command.Dispose();
        }
    }
}

internal sealed unsafe class D3D11GBufferBackend : IDisposable
{
    internal const int MaxVerticesPerCommand = 128 * 6;

    private readonly object stateLock = new();
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private readonly nint immediateContextPointer;
    private readonly D3D11Device device;
    private readonly DeviceContext immediateContext;
    private readonly DeviceContext deferredContext;
    private readonly UserDefinedAnnotation? annotation;
    private readonly D3D11Buffer vertexBuffer;
    private readonly D3D11Buffer constantsBuffer;
    private readonly InputLayout inputLayout;
    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly PixelShader semitransparentPixelShader;
    private readonly PixelShader semitransparentCompositePixelShader;
    private readonly Texture2D whiteTexture;
    private readonly ShaderResourceView whiteTextureView;
    private readonly SamplerState sampler;
    private readonly RasterizerState rasterizerState;
    private readonly BlendState blendState;
    private readonly BlendState semitransparentCompositeBlendState;
    private readonly DepthStencilState depthStencilState;
    private readonly DepthStencilState semitransparentDepthTestWriteStencilState;
    private readonly DepthStencilState semitransparentCompositeDepthState;
    private readonly Hook<OMSetRenderTargetsDelegate> omSetRenderTargetsHook;
    private readonly Hook<OMSetRenderTargetsAndUnorderedAccessViewsDelegate> omSetRenderTargetsAndUavsHook;
    private readonly Hook<SetShaderResourcesDelegate> psSetShaderResourcesHook;
    private readonly Hook<DrawIndexedDelegate> drawIndexedHook;
    private readonly Hook<DrawDelegate> drawHook;
    private readonly Hook<DrawIndexedInstancedDelegate> drawIndexedInstancedHook;
    private readonly Hook<DrawInstancedDelegate> drawInstancedHook;
    private bool candidateActive;
    private GBufferTarget candidateTarget;
    private nint[] candidateRenderTargets = [];
    private nint candidateDepthStencil;
    private uint candidateWidth;
    private uint candidateHeight;
    private ViewportF? candidateViewport;
    private bool detouring;
    private bool disposed;
    private uint ditherPhase;
    private bool semitransparentDrawLogged;
    private bool semitransparentCompositeArmed;
    private bool semitransparentCompositeInjected;
    private bool semitransparentCompositeDrawLogged;
    private Matrix semitransparentInjectedViewProjection;
    private bool hasSemitransparentInjectedViewProjection;
    private GBufferFrame? opaqueFrame;
    private GBufferFrame? semitransparentFrame;
    private GBufferFrame? activeSemitransparentCycle;
    private long nextCycleId;
    private NumericsVector2 opaqueJitterPixels;
    private bool forceOpaqueAlpha;
    private bool opaqueSnapshotRequested;
    private NativeDrawSnapshot? opaqueSnapshot;
    private long snapshotSequence;

    [StructLayout(LayoutKind.Sequential)]
    private struct WorldConstants
    {
        public Matrix ViewProjection;
        public Vector4 G0;
        public Vector4 G1;
        public Vector4 G2;
        public Vector4 G3;
        public Vector4 G4;
        public Vector4 DitherParameters;
        public Vector4 SemitransparentLighting;
        public Vector4 DebugParameters;
    }

    private sealed class PendingTargets : IDisposable
    {
        public GBufferTarget Target { get; }
        public RenderTargetView[] RenderTargets { get; }
        public DepthStencilView DepthStencil { get; }
        public uint Width { get; }
        public uint Height { get; }
        public ViewportF Viewport { get; }

        public PendingTargets(
            GBufferTarget target,
            nint[] renderTargets,
            nint depthStencil,
            uint width,
            uint height,
            ViewportF? viewport
        )
        {
            Target = target;
            var retainedTargets = new List<RenderTargetView>(5);
            DepthStencilView? retainedDepth = null;
            try
            {
                for (var index = 0; index < renderTargets.Length; index++)
                {
                    Marshal.AddRef(renderTargets[index]);
                    retainedTargets.Add(new RenderTargetView(renderTargets[index]));
                }

                Marshal.AddRef(depthStencil);
                retainedDepth = new DepthStencilView(depthStencil);
            }
            catch
            {
                retainedDepth?.Dispose();
                foreach (var retainedTarget in retainedTargets)
                {
                    retainedTarget.Dispose();
                }
                throw;
            }

            RenderTargets = [.. retainedTargets];
            DepthStencil = retainedDepth;
            Width = width;
            Height = height;
            Viewport = viewport ?? new ViewportF(0, 0, width, height, 0, 1);
        }

        public void Dispose()
        {
            DepthStencil.Dispose();
            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(
        nint context,
        uint numViews,
        nint* renderTargetViews,
        nint depthStencilView
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsAndUnorderedAccessViewsDelegate(
        nint context,
        uint numRenderTargetViews,
        nint* renderTargetViews,
        nint depthStencilView,
        uint unorderedAccessViewStartSlot,
        uint numUnorderedAccessViews,
        nint* unorderedAccessViews,
        uint* unorderedAccessViewInitialCounts
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetShaderResourcesDelegate(
        nint context,
        uint startSlot,
        uint numViews,
        nint* shaderResourceViews
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedDelegate(
        nint context,
        uint indexCount,
        uint startIndexLocation,
        int baseVertexLocation
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawDelegate(nint context, uint vertexCount, uint startVertexLocation);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedInstancedDelegate(
        nint context,
        uint indexCountPerInstance,
        uint instanceCount,
        uint startIndexLocation,
        int baseVertexLocation,
        uint startInstanceLocation
    );

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawInstancedDelegate(
        nint context,
        uint vertexCountPerInstance,
        uint instanceCount,
        uint startVertexLocation,
        uint startInstanceLocation
    );

    public D3D11GBufferBackend(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;

        var kernelDevice = Device.Instance();
        if (
            kernelDevice == null
            || kernelDevice->D3D11Forwarder == null
            || kernelDevice->D3D11DeviceContext == null
        )
        {
            throw new InvalidOperationException(
                "D3D11 device or immediate context is not available."
            );
        }

        immediateContextPointer = (nint)kernelDevice->D3D11DeviceContext;
        device = new D3D11Device((nint)kernelDevice->D3D11Forwarder);
        immediateContext = device.ImmediateContext;
        deferredContext = new DeviceContext(device);
        try
        {
            annotation = immediateContext.QueryInterface<UserDefinedAnnotation>();
        }
        catch (SharpDXException)
        {
            annotation = null;
        }

        const string shaderSource = """
            cbuffer WorldConstants : register(b0)
            {
                float4x4 ViewProjection;
                float4 MaterialG0;
                float4 MaterialG1;
                float4 MaterialG2;
                float4 MaterialG3;
                float4 MaterialG4;
                float4 DitherParameters;
                float4 SemitransparentLighting;
                float4 DebugParameters;
            };

            Texture2D<float4> AlbedoTexture : register(t0);
            Texture2D<float4> LightDiffuseTexture : register(t1);
            Texture2D<float4> LightSpecularTexture : register(t2);
            SamplerState AlbedoSampler : register(s0);

            static const float Bayer4x4[16] =
            {
                 0.0,  8.0,  2.0, 10.0,
                12.0,  4.0, 14.0,  6.0,
                 3.0, 11.0,  1.0,  9.0,
                15.0,  7.0, 13.0,  5.0
            };

            float DitherThreshold(float2 screenPosition)
            {
                uint phase = (uint)DitherParameters.x & 15;
                uint offsetIndex = (phase * 5) & 15;
                uint2 temporalOffset = uint2(offsetIndex & 3, offsetIndex >> 2);
                uint2 pixel = (uint2(screenPosition) + temporalOffset) & 3;
                return (Bayer4x4[pixel.y * 4 + pixel.x] + 0.5) / 16.0;
            }

            struct VertexInput
            {
                float3 Position : POSITION;
                float3 Normal : NORMAL;
                float4 Color : COLOR;
                float2 TexCoord : TEXCOORD0;
            };

            struct PixelInput
            {
                float4 Position : SV_POSITION;
                float3 WorldNormal : NORMAL;
                float4 Color : COLOR;
                float2 TexCoord : TEXCOORD0;
            };

            PixelInput VS(VertexInput input)
            {
                PixelInput output;
                output.Position = mul(float4(input.Position, 1.0), ViewProjection);
                float2 viewportSize = max(DebugParameters.zw, 1.0);
                float2 jitterNdc = 2.0 * DebugParameters.xy / viewportSize;
                output.Position.xy += jitterNdc * output.Position.w;
                output.WorldNormal = input.Normal;
                output.Color = input.Color;
                output.TexCoord = input.TexCoord;
                return output;
            }

            struct GBufferOutput
            {
                float4 Target0 : SV_Target0;
                float4 Target1 : SV_Target1;
                float4 Target2 : SV_Target2;
                float4 Target3 : SV_Target3;
                float4 Target4 : SV_Target4;
            };

            GBufferOutput PS(PixelInput input, bool isFrontFace : SV_IsFrontFace)
            {
                float4 texel = AlbedoTexture.Sample(AlbedoSampler, input.TexCoord);
                float opacity = saturate(texel.a * input.Color.a);
                bool semitransparent = DitherParameters.y > 0.5;
                if (!semitransparent && DitherParameters.z > 0.5)
                {
                    opacity = 1.0;
                }
                if (semitransparent)
                {
                    clip(opacity - 0.000001);
                }
                else
                {
                    clip(opacity - DitherThreshold(input.Position.xy));
                }

                GBufferOutput output;
                output.Target0 = MaterialG0;
                output.Target1 = MaterialG1;
                output.Target2 = MaterialG2;
                output.Target3 = MaterialG3;
                output.Target4 = MaterialG4;
                // The world-space cross-product winding is opposite D3D's projected
                // SV_IsFrontFace convention for the current FFXIV view-projection matrix.
                float3 normal = normalize(input.WorldNormal) * (isFrontFace ? -1.0 : 1.0);
                output.Target0.rgb = normal * 0.5 + 0.5;
                output.Target2.rgb = texel.rgb * input.Color.rgb;
                if (semitransparent)
                {
                    output.Target2.a = opacity;
                }
                return output;
            }

            struct SemitransparentGBufferOutput
            {
                float4 Target0 : SV_Target0;
                float4 Target1 : SV_Target1;
                float4 Target2 : SV_Target2;
                float4 Target3 : SV_Target3;
            };

            SemitransparentGBufferOutput PSSemitransparent(PixelInput input, bool isFrontFace : SV_IsFrontFace)
            {
                float4 texel = AlbedoTexture.Sample(AlbedoSampler, input.TexCoord);
                float opacity = saturate(texel.a * input.Color.a);
                clip(opacity - 0.000001);

                SemitransparentGBufferOutput output;
                output.Target0 = MaterialG0;
                output.Target1 = MaterialG1;
                output.Target2 = MaterialG2;
                output.Target3 = MaterialG4;
                float3 normal = normalize(input.WorldNormal) * (isFrontFace ? -1.0 : 1.0);
                output.Target0.rgb = normal * 0.5 + 0.5;
                output.Target2.rgb = texel.rgb * input.Color.rgb;
                output.Target0.a = opacity;
                output.Target2.a = opacity;
                output.Target3.a = opacity;
                return output;
            }

            float4 PSSemitransparentComposite(PixelInput input) : SV_Target0
            {
                float4 texel = AlbedoTexture.Sample(AlbedoSampler, input.TexCoord);
                float opacity = saturate(texel.a * input.Color.a);
                clip(opacity - 0.000001);
                int3 lightPixel = int3(input.Position.xy, 0);
                float3 diffuse = LightDiffuseTexture.Load(lightPixel).rgb;
                float3 specular = LightSpecularTexture.Load(lightPixel).rgb;
                float3 baseColor = texel.rgb * input.Color.rgb;
                float3 litColor = baseColor * (SemitransparentLighting.x + diffuse * SemitransparentLighting.y)
                    + specular * SemitransparentLighting.z;
                return float4(litColor, opacity);
            }
            """;

        using var compiledVertexShader = ShaderBytecode.Compile(shaderSource, "VS", "vs_5_0");
        using var compiledPixelShader = ShaderBytecode.Compile(shaderSource, "PS", "ps_5_0");
        using var compiledSemitransparentPixelShader = ShaderBytecode.Compile(
            shaderSource,
            "PSSemitransparent",
            "ps_5_0"
        );
        using var compiledSemitransparentCompositePixelShader = ShaderBytecode.Compile(
            shaderSource,
            "PSSemitransparentComposite",
            "ps_5_0"
        );
        vertexShader = new VertexShader(device, compiledVertexShader.Bytecode);
        pixelShader = new PixelShader(device, compiledPixelShader.Bytecode);
        semitransparentPixelShader = new PixelShader(
            device,
            compiledSemitransparentPixelShader.Bytecode
        );
        semitransparentCompositePixelShader = new PixelShader(
            device,
            compiledSemitransparentCompositePixelShader.Bytecode
        );
        inputLayout = new InputLayout(
            device,
            compiledVertexShader.Bytecode,
            [
                new InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, SharpDX.DXGI.Format.R32G32B32_Float, 12, 0),
                new InputElement("COLOR", 0, SharpDX.DXGI.Format.R32G32B32A32_Float, 24, 0),
                new InputElement("TEXCOORD", 0, SharpDX.DXGI.Format.R32G32_Float, 40, 0),
            ]
        );
        vertexBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<GBufferVertex>() * MaxVerticesPerCommand,
            ResourceUsage.Dynamic,
            BindFlags.VertexBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0
        );
        constantsBuffer = new D3D11Buffer(
            device,
            Utilities.SizeOf<WorldConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0
        );

        (whiteTexture, whiteTextureView) = CreateWhiteTexture();
        sampler = new SamplerState(
            device,
            new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MaximumAnisotropy = 1,
                MinimumLod = 0,
                MaximumLod = float.MaxValue,
                BorderColor = Color.Transparent,
            }
        );

        var rasterizerDescription = RasterizerStateDescription.Default();
        rasterizerDescription.CullMode = CullMode.None;
        rasterizerDescription.IsScissorEnabled = false;
        rasterizerState = new RasterizerState(device, rasterizerDescription);

        var blendDescription = BlendStateDescription.Default();
        blendDescription.IndependentBlendEnable = true;
        for (var index = 0; index < 5; index++)
        {
            blendDescription.RenderTarget[index].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        }
        blendState = new BlendState(device, blendDescription);

        var compositeBlendDescription = BlendStateDescription.Default();
        compositeBlendDescription.RenderTarget[0].IsBlendEnabled = true;
        compositeBlendDescription.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        compositeBlendDescription.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        compositeBlendDescription.RenderTarget[0].BlendOperation = BlendOperation.Add;
        compositeBlendDescription.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        compositeBlendDescription.RenderTarget[0].DestinationAlphaBlend =
            BlendOption.InverseSourceAlpha;
        compositeBlendDescription.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        compositeBlendDescription.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        semitransparentCompositeBlendState = new BlendState(device, compositeBlendDescription);

        var depthDescription = DepthStencilStateDescription.Default();
        depthDescription.IsDepthEnabled = true;
        depthDescription.DepthWriteMask = DepthWriteMask.All;
        depthDescription.DepthComparison = Comparison.Greater;
        depthDescription.IsStencilEnabled = true;
        depthDescription.StencilReadMask = 0xFF;
        depthDescription.StencilWriteMask = 0xFF;
        depthDescription.FrontFace.Comparison = Comparison.Always;
        depthDescription.FrontFace.FailOperation = StencilOperation.Keep;
        depthDescription.FrontFace.DepthFailOperation = StencilOperation.Keep;
        depthDescription.FrontFace.PassOperation = StencilOperation.Replace;
        depthDescription.BackFace = depthDescription.FrontFace;
        depthStencilState = new DepthStencilState(device, depthDescription);

        var semitransparentDepthDescription = DepthStencilStateDescription.Default();
        semitransparentDepthDescription.IsDepthEnabled = true;
        semitransparentDepthDescription.DepthWriteMask = DepthWriteMask.All;
        semitransparentDepthDescription.DepthComparison = Comparison.GreaterEqual;
        semitransparentDepthDescription.IsStencilEnabled = true;
        semitransparentDepthDescription.StencilReadMask = 0xFF;
        semitransparentDepthDescription.StencilWriteMask = 0xFF;
        semitransparentDepthDescription.FrontFace.Comparison = Comparison.Always;
        semitransparentDepthDescription.FrontFace.FailOperation = StencilOperation.Keep;
        semitransparentDepthDescription.FrontFace.DepthFailOperation = StencilOperation.Keep;
        semitransparentDepthDescription.FrontFace.PassOperation = StencilOperation.Replace;
        semitransparentDepthDescription.BackFace = semitransparentDepthDescription.FrontFace;
        semitransparentDepthTestWriteStencilState = new DepthStencilState(
            device,
            semitransparentDepthDescription
        );

        semitransparentDepthDescription.IsDepthEnabled = true;
        semitransparentDepthDescription.DepthWriteMask = DepthWriteMask.Zero;
        semitransparentDepthDescription.DepthComparison = Comparison.GreaterEqual;
        semitransparentDepthDescription.IsStencilEnabled = false;
        semitransparentCompositeDepthState = new DepthStencilState(
            device,
            semitransparentDepthDescription
        );

        var vtable = *(nint**)immediateContextPointer;
        omSetRenderTargetsHook = gameInteropProvider.HookFromAddress<OMSetRenderTargetsDelegate>(
            vtable[33],
            OMSetRenderTargetsDetour
        );
        omSetRenderTargetsAndUavsHook =
            gameInteropProvider.HookFromAddress<OMSetRenderTargetsAndUnorderedAccessViewsDelegate>(
                vtable[34],
                OMSetRenderTargetsAndUavsDetour
            );
        psSetShaderResourcesHook = gameInteropProvider.HookFromAddress<SetShaderResourcesDelegate>(
            vtable[8],
            PSSetShaderResourcesDetour
        );
        drawIndexedHook = gameInteropProvider.HookFromAddress<DrawIndexedDelegate>(
            vtable[12],
            DrawIndexedDetour
        );
        drawHook = gameInteropProvider.HookFromAddress<DrawDelegate>(vtable[13], DrawDetour);
        drawIndexedInstancedHook =
            gameInteropProvider.HookFromAddress<DrawIndexedInstancedDelegate>(
                vtable[20],
                DrawIndexedInstancedDetour
            );
        drawInstancedHook = gameInteropProvider.HookFromAddress<DrawInstancedDelegate>(
            vtable[21],
            DrawInstancedDetour
        );
        omSetRenderTargetsHook.Enable();
        omSetRenderTargetsAndUavsHook.Enable();
        psSetShaderResourcesHook.Enable();
        drawIndexedHook.Enable();
        drawHook.Enable();
        drawIndexedInstancedHook.Enable();
        drawInstancedHook.Enable();
    }

    public void Publish(
        GBufferTarget target,
        List<GBufferDrawCommand> commands,
        GBufferMaterial material,
        SemitransparentLighting lighting
    )
    {
        GBufferFrame? oldFrame;
        lock (stateLock)
        {
            var newFrame =
                commands.Count == 0
                    ? null
                    : new GBufferFrame(
                        [.. commands],
                        material,
                        lighting,
                        Interlocked.Increment(ref nextCycleId)
                    );
            if (target == GBufferTarget.Opaque)
            {
                oldFrame = opaqueFrame;
                opaqueFrame = newFrame;
            }
            else
            {
                oldFrame = semitransparentFrame;
                semitransparentFrame = newFrame;
            }
        }
        oldFrame?.Release();
    }

    public NumericsVector2 GetOpaqueJitterPixels()
    {
        lock (stateLock)
        {
            return opaqueJitterPixels;
        }
    }

    public void SetOpaqueJitterPixels(NumericsVector2 value)
    {
        lock (stateLock)
        {
            opaqueJitterPixels = new NumericsVector2(
                float.IsFinite(value.X) ? Math.Clamp(value.X, -8f, 8f) : 0f,
                float.IsFinite(value.Y) ? Math.Clamp(value.Y, -8f, 8f) : 0f
            );
        }
    }

    public bool GetForceOpaqueAlpha()
    {
        lock (stateLock)
        {
            return forceOpaqueAlpha;
        }
    }

    public void SetForceOpaqueAlpha(bool value)
    {
        lock (stateLock)
        {
            forceOpaqueAlpha = value;
        }
    }

    public void RequestOpaqueDrawSnapshot()
    {
        lock (stateLock)
        {
            opaqueSnapshotRequested = true;
        }
    }

    public bool TryTakeOpaqueDrawSnapshot(out NativeDrawSnapshot snapshot)
    {
        lock (stateLock)
        {
            if (opaqueSnapshot == null)
            {
                snapshot = null!;
                return false;
            }

            snapshot = opaqueSnapshot;
            opaqueSnapshot = null;
            return true;
        }
    }

    public void Clear(GBufferTarget target)
    {
        GBufferFrame? oldFrame;
        lock (stateLock)
        {
            if (target == GBufferTarget.Opaque)
            {
                oldFrame = opaqueFrame;
                opaqueFrame = null;
            }
            else
            {
                oldFrame = semitransparentFrame;
                semitransparentFrame = null;
            }
        }
        oldFrame?.Release();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        drawInstancedHook.Dispose();
        drawIndexedInstancedHook.Dispose();
        drawHook.Dispose();
        drawIndexedHook.Dispose();
        psSetShaderResourcesHook.Dispose();
        omSetRenderTargetsAndUavsHook.Dispose();
        omSetRenderTargetsHook.Dispose();

        lock (stateLock)
        {
            opaqueFrame?.Release();
            opaqueFrame = null;
            semitransparentFrame?.Release();
            semitransparentFrame = null;
            activeSemitransparentCycle?.Release();
            activeSemitransparentCycle = null;
            semitransparentDepthTestWriteStencilState.Dispose();
            semitransparentCompositeDepthState.Dispose();
            depthStencilState.Dispose();
            semitransparentCompositeBlendState.Dispose();
            blendState.Dispose();
            rasterizerState.Dispose();
            sampler.Dispose();
            whiteTextureView.Dispose();
            whiteTexture.Dispose();
            constantsBuffer.Dispose();
            vertexBuffer.Dispose();
            inputLayout.Dispose();
            semitransparentPixelShader.Dispose();
            semitransparentCompositePixelShader.Dispose();
            pixelShader.Dispose();
            vertexShader.Dispose();
            annotation?.Dispose();
            deferredContext.Dispose();
            immediateContext.Dispose();
        }
    }

    private void OMSetRenderTargetsDetour(
        nint context,
        uint numViews,
        nint* renderTargetViews,
        nint depthStencilView
    )
    {
        PendingTargets? pending = null;
        try
        {
            if (!detouring && context == immediateContextPointer)
            {
                lock (stateLock)
                {
                    pending = TrackTargetChange(numViews, renderTargetViews, depthStencilView);
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[Underpaint] Failed to track opaque G-buffer targets.");
        }

        try
        {
            omSetRenderTargetsHook.Original(context, numViews, renderTargetViews, depthStencilView);
        }
        catch
        {
            pending?.Dispose();
            throw;
        }

        IssueSafely(pending);
    }

    private void OMSetRenderTargetsAndUavsDetour(
        nint context,
        uint numRenderTargetViews,
        nint* renderTargetViews,
        nint depthStencilView,
        uint unorderedAccessViewStartSlot,
        uint numUnorderedAccessViews,
        nint* unorderedAccessViews,
        uint* unorderedAccessViewInitialCounts
    )
    {
        PendingTargets? pending = null;
        try
        {
            if (
                !detouring
                && context == immediateContextPointer
                && numRenderTargetViews != uint.MaxValue
            )
            {
                lock (stateLock)
                {
                    pending = TrackTargetChange(
                        numRenderTargetViews,
                        renderTargetViews,
                        depthStencilView
                    );
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning(
                exception,
                "[Underpaint] Failed to track opaque G-buffer targets with UAVs."
            );
        }

        try
        {
            omSetRenderTargetsAndUavsHook.Original(
                context,
                numRenderTargetViews,
                renderTargetViews,
                depthStencilView,
                unorderedAccessViewStartSlot,
                numUnorderedAccessViews,
                unorderedAccessViews,
                unorderedAccessViewInitialCounts
            );
        }
        catch
        {
            pending?.Dispose();
            throw;
        }

        IssueSafely(pending);
    }

    private void DrawIndexedDetour(
        nint context,
        uint indexCount,
        uint startIndexLocation,
        int baseVertexLocation
    )
    {
        TryCaptureCandidateViewport(context);
        TryCaptureOpaqueDrawSnapshot(context);
        TryIssueSemitransparentCompositeBeforeNativeDraw(context);
        drawIndexedHook.Original(context, indexCount, startIndexLocation, baseVertexLocation);
    }

    private void DrawDetour(nint context, uint vertexCount, uint startVertexLocation)
    {
        TryCaptureCandidateViewport(context);
        TryCaptureOpaqueDrawSnapshot(context);
        TryIssueSemitransparentCompositeBeforeNativeDraw(context);
        drawHook.Original(context, vertexCount, startVertexLocation);
    }

    private void DrawIndexedInstancedDetour(
        nint context,
        uint indexCountPerInstance,
        uint instanceCount,
        uint startIndexLocation,
        int baseVertexLocation,
        uint startInstanceLocation
    )
    {
        TryCaptureCandidateViewport(context);
        TryCaptureOpaqueDrawSnapshot(context);
        TryIssueSemitransparentCompositeBeforeNativeDraw(context);
        drawIndexedInstancedHook.Original(
            context,
            indexCountPerInstance,
            instanceCount,
            startIndexLocation,
            baseVertexLocation,
            startInstanceLocation
        );
    }

    private void DrawInstancedDetour(
        nint context,
        uint vertexCountPerInstance,
        uint instanceCount,
        uint startVertexLocation,
        uint startInstanceLocation
    )
    {
        TryCaptureCandidateViewport(context);
        TryCaptureOpaqueDrawSnapshot(context);
        TryIssueSemitransparentCompositeBeforeNativeDraw(context);
        drawInstancedHook.Original(
            context,
            vertexCountPerInstance,
            instanceCount,
            startVertexLocation,
            startInstanceLocation
        );
    }

    private void TryCaptureCandidateViewport(nint context)
    {
        if (detouring || context != immediateContextPointer)
        {
            return;
        }

        lock (stateLock)
        {
            if (!candidateActive || candidateViewport != null)
            {
                return;
            }

            var viewports = immediateContext.Rasterizer.GetViewports<ViewportF>();
            if (viewports.Length != 0)
            {
                candidateViewport = viewports[0];
            }
        }
    }

    private void TryCaptureOpaqueDrawSnapshot(nint context)
    {
        if (detouring || context != immediateContextPointer)
        {
            return;
        }

        lock (stateLock)
        {
            if (
                !opaqueSnapshotRequested
                || !candidateActive
                || candidateTarget != GBufferTarget.Opaque
            )
            {
                return;
            }

            opaqueSnapshotRequested = false;
            try
            {
                opaqueSnapshot = CaptureOpaqueDrawSnapshot();
                log.Information(
                    $"[Underpaint] Captured native opaque draw snapshot #{opaqueSnapshot.Sequence}."
                );
            }
            catch (Exception exception)
            {
                log.Warning(
                    exception,
                    "[Underpaint] Failed to capture native opaque draw snapshot."
                );
            }
        }
    }

    private NativeDrawSnapshot CaptureOpaqueDrawSnapshot()
    {
        var control = Control.Instance();
        var controlViewProjection =
            control != null
                ? ToNumerics(control->ViewProjectionMatrix)
                : NumericsMatrix4x4.Identity;
        NumericsMatrix4x4? sceneViewProjection = null;
        var activeCamera = control != null ? control->CameraManager.GetActiveCamera() : null;
        if (activeCamera != null && activeCamera->SceneCamera.RenderCamera != null)
        {
            sceneViewProjection =
                ToNumerics(activeCamera->SceneCamera.ViewMatrix)
                * ToNumerics(activeCamera->SceneCamera.RenderCamera->ProjectionMatrix);
        }

        var viewports = immediateContext
            .Rasterizer.GetViewports<ViewportF>()
            .Select(viewport => new NativeViewportSnapshot(
                viewport.X,
                viewport.Y,
                viewport.Width,
                viewport.Height,
                viewport.MinDepth,
                viewport.MaxDepth
            ))
            .ToArray();
        var scissors = immediateContext
            .Rasterizer.GetScissorRectangles<RawRectangle>()
            .Select(rectangle => new NativeScissorSnapshot(
                rectangle.Left,
                rectangle.Top,
                rectangle.Right,
                rectangle.Bottom
            ))
            .ToArray();

        NativeRasterizerSnapshot? rasterizerSnapshot = null;
        using (var nativeRasterizer = immediateContext.Rasterizer.State)
        {
            if (nativeRasterizer != null)
            {
                var description = nativeRasterizer.Description;
                rasterizerSnapshot = new NativeRasterizerSnapshot(
                    description.FillMode.ToString(),
                    description.CullMode.ToString(),
                    description.IsFrontCounterClockwise,
                    description.DepthBias,
                    description.DepthBiasClamp,
                    description.SlopeScaledDepthBias,
                    description.IsDepthClipEnabled,
                    description.IsScissorEnabled
                );
            }
        }

        NativeDepthStencilSnapshot? depthStencilSnapshot = null;
        using (
            var nativeDepthStencil = immediateContext.OutputMerger.GetDepthStencilState(
                out var stencilReference
            )
        )
        {
            if (nativeDepthStencil != null)
            {
                var description = nativeDepthStencil.Description;
                depthStencilSnapshot = new NativeDepthStencilSnapshot(
                    description.IsDepthEnabled,
                    description.DepthWriteMask.ToString(),
                    description.DepthComparison.ToString(),
                    description.IsStencilEnabled,
                    stencilReference
                );
            }
        }

        nint vertexShaderPointer;
        using (var nativeVertexShader = immediateContext.VertexShader.Get())
        {
            vertexShaderPointer = nativeVertexShader?.NativePointer ?? 0;
        }

        var constantBufferSnapshots = new List<NativeConstantBufferSnapshot>(14);
        NativeCameraParameterSnapshot? cameraParameterSnapshot = null;
        var constantBuffers = immediateContext.VertexShader.GetConstantBuffers(0, 14);
        try
        {
            for (var slot = 0; slot < constantBuffers.Length; slot++)
            {
                var constantBuffer = constantBuffers[slot];
                if (constantBuffer == null)
                {
                    continue;
                }

                var bytes = ReadConstantBuffer(constantBuffer);
                constantBufferSnapshots.Add(
                    new NativeConstantBufferSnapshot(
                        slot,
                        constantBuffer.NativePointer,
                        bytes.Length,
                        ComputeFnv1A64(bytes)
                    )
                );
                var cameraParameterCandidate = FindCameraParameter(
                    bytes,
                    slot,
                    controlViewProjection,
                    sceneViewProjection
                );
                if (
                    cameraParameterCandidate != null
                    && cameraParameterCandidate.MatchError
                        < (cameraParameterSnapshot?.MatchError ?? float.PositiveInfinity)
                )
                {
                    cameraParameterSnapshot = cameraParameterCandidate;
                }
            }
        }
        finally
        {
            foreach (var constantBuffer in constantBuffers)
            {
                constantBuffer?.Dispose();
            }
        }

        return new NativeDrawSnapshot(
            Interlocked.Increment(ref snapshotSequence),
            DateTimeOffset.UtcNow,
            viewports,
            scissors,
            rasterizerSnapshot,
            depthStencilSnapshot,
            vertexShaderPointer,
            constantBufferSnapshots,
            controlViewProjection,
            sceneViewProjection,
            cameraParameterSnapshot,
            DescribeRenderTargets(candidateRenderTargets),
            DescribeDepthTarget(candidateDepthStencil)
        );
    }

    private static NativeCameraParameterSnapshot? FindCameraParameter(
        byte[] bytes,
        int slot,
        NumericsMatrix4x4 controlViewProjection,
        NumericsMatrix4x4? sceneViewProjection
    )
    {
        NativeCameraParameterSnapshot? best = null;
        if (bytes.Length < sizeof(CameraParameter))
        {
            return null;
        }

        fixed (byte* data = bytes)
        {
            for (var offset = 0; offset <= bytes.Length - sizeof(CameraParameter); offset += 16)
            {
                var cameraParameter = (CameraParameter*)(data + offset);
                var rawViewProjection = ToNumerics(cameraParameter->ViewProjectionMatrix);
                var directError = MatrixError(rawViewProjection, controlViewProjection);
                if (sceneViewProjection is { } scene)
                {
                    directError = MathF.Min(directError, MatrixError(rawViewProjection, scene));
                }

                var transposedViewProjection = NumericsMatrix4x4.Transpose(rawViewProjection);
                var transposedError = MatrixError(transposedViewProjection, controlViewProjection);
                if (sceneViewProjection is { } transposedScene)
                {
                    transposedError = MathF.Min(
                        transposedError,
                        MatrixError(transposedViewProjection, transposedScene)
                    );
                }

                var transpose = transposedError < directError;
                var error = transpose ? transposedError : directError;
                if (!float.IsFinite(error) || error >= (best?.MatchError ?? 0.05f))
                {
                    continue;
                }

                var projection = ToNumerics(cameraParameter->ProjectionMatrix);
                var mainViewToProjection = ToNumerics(cameraParameter->MainViewToProjectionMatrix);
                best = new NativeCameraParameterSnapshot(
                    slot,
                    offset,
                    error,
                    transpose,
                    transpose ? transposedViewProjection : rawViewProjection,
                    transpose ? NumericsMatrix4x4.Transpose(projection) : projection,
                    transpose
                        ? NumericsMatrix4x4.Transpose(mainViewToProjection)
                        : mainViewToProjection
                );
            }
        }

        return best;
    }

    private static float MatrixError(NumericsMatrix4x4 actual, NumericsMatrix4x4 expected)
    {
        var total = 0f;
        var actualValues = (float*)&actual;
        var expectedValues = (float*)&expected;
        for (var index = 0; index < 16; index++)
        {
            if (!float.IsFinite(actualValues[index]) || !float.IsFinite(expectedValues[index]))
            {
                return float.PositiveInfinity;
            }

            total +=
                MathF.Abs(actualValues[index] - expectedValues[index])
                / (1f + MathF.Abs(expectedValues[index]));
        }

        return total / 16f;
    }

    private byte[] ReadConstantBuffer(D3D11Buffer source)
    {
        var description = source.Description;
        using var staging = new D3D11Buffer(
            device,
            new BufferDescription
            {
                SizeInBytes = description.SizeInBytes,
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None,
                StructureByteStride = 0,
            }
        );
        immediateContext.CopyResource(source, staging);
        immediateContext.MapSubresource(staging, MapMode.Read, MapFlags.None, out var stream);
        try
        {
            var bytes = new byte[description.SizeInBytes];
            stream.ReadExactly(bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            immediateContext.UnmapSubresource(staging, 0);
            stream.Dispose();
        }
    }

    private static ulong ComputeFnv1A64(byte[] bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static NumericsMatrix4x4 ToNumerics(
        FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 matrix
    ) => *(NumericsMatrix4x4*)&matrix;

    private static string[] DescribeRenderTargets(nint[] pointers)
    {
        var descriptions = new string[pointers.Length];
        for (var index = 0; index < pointers.Length; index++)
        {
            Marshal.AddRef(pointers[index]);
            using var view = new RenderTargetView(pointers[index]);
            using var texture = view.ResourceAs<Texture2D>();
            var description = texture.Description;
            descriptions[index] = $"{description.Width}x{description.Height} {description.Format}";
        }
        return descriptions;
    }

    private static string DescribeDepthTarget(nint pointer)
    {
        Marshal.AddRef(pointer);
        using var view = new DepthStencilView(pointer);
        using var texture = view.ResourceAs<Texture2D>();
        var description = texture.Description;
        return $"{description.Width}x{description.Height} {description.Format}";
    }

    private void TryIssueSemitransparentCompositeBeforeNativeDraw(nint context)
    {
        if (
            detouring
            || context != immediateContextPointer
            || !semitransparentCompositeArmed
            || semitransparentCompositeInjected
        )
        {
            return;
        }

        try
        {
            lock (stateLock)
            {
                if (activeSemitransparentCycle == null || semitransparentCompositeInjected)
                {
                    return;
                }

                var renderTargets = immediateContext.OutputMerger.GetRenderTargets(
                    2,
                    out var depthStencil
                );
                var shaderResources = immediateContext.PixelShader.GetShaderResources(0, 16);
                try
                {
                    if (
                        renderTargets.Length < 2
                        || renderTargets[0] == null
                        || renderTargets[1] != null
                        || depthStencil == null
                    )
                    {
                        return;
                    }

                    var (lightDiffuse, lightSpecular) = FindSemitransparentLightViews(
                        shaderResources
                    );
                    if (lightDiffuse == null || lightSpecular == null)
                    {
                        return;
                    }

                    using var targetTexture = renderTargets[0].ResourceAs<Texture2D>();
                    var description = targetTexture.Description;
                    IssueSemitransparentComposite(
                        renderTargets[0],
                        depthStencil,
                        (uint)description.Width,
                        (uint)description.Height,
                        activeSemitransparentCycle,
                        description.Format,
                        lightDiffuse,
                        lightSpecular
                    );
                    semitransparentCompositeInjected = true;
                    semitransparentCompositeArmed = false;
                    activeSemitransparentCycle.Release();
                    activeSemitransparentCycle = null;
                }
                finally
                {
                    depthStencil?.Dispose();
                    foreach (var renderTarget in renderTargets)
                    {
                        renderTarget?.Dispose();
                    }
                    foreach (var shaderResource in shaderResources)
                    {
                        shaderResource?.Dispose();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            semitransparentCompositeArmed = false;
            lock (stateLock)
            {
                activeSemitransparentCycle?.Release();
                activeSemitransparentCycle = null;
            }
            log.Warning(exception, "[Underpaint] Failed to issue semitransparent composite draw.");
        }
    }

    private void IssueSemitransparentComposite(
        RenderTargetView renderTarget,
        DepthStencilView depthStencil,
        uint width,
        uint height,
        GBufferFrame frame,
        SharpDX.DXGI.Format targetFormat,
        ShaderResourceView lightDiffuse,
        ShaderResourceView lightSpecular
    )
    {
        var constants = new WorldConstants
        {
            ViewProjection = semitransparentInjectedViewProjection,
            SemitransparentLighting = new Vector4(
                frame.Lighting.Ambient,
                frame.Lighting.Diffuse,
                frame.Lighting.Specular,
                0f
            ),
        };

        try
        {
            detouring = true;
            deferredContext.ClearState();
            deferredContext.OutputMerger.SetTargets(depthStencil, renderTarget);
            deferredContext.OutputMerger.SetBlendState(semitransparentCompositeBlendState);
            deferredContext.OutputMerger.SetDepthStencilState(
                semitransparentCompositeDepthState,
                0
            );
            deferredContext.Rasterizer.SetViewport(0, 0, width, height, 0, 1);
            deferredContext.Rasterizer.State = rasterizerState;
            deferredContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            deferredContext.InputAssembler.InputLayout = inputLayout;
            deferredContext.InputAssembler.SetVertexBuffers(
                0,
                new VertexBufferBinding(vertexBuffer, Utilities.SizeOf<GBufferVertex>(), 0)
            );
            deferredContext.HullShader.Set(null);
            deferredContext.DomainShader.Set(null);
            deferredContext.GeometryShader.Set(null);
            deferredContext.VertexShader.Set(vertexShader);
            deferredContext.VertexShader.SetConstantBuffer(0, constantsBuffer);
            deferredContext.PixelShader.Set(semitransparentCompositePixelShader);
            deferredContext.PixelShader.SetConstantBuffer(0, constantsBuffer);
            deferredContext.PixelShader.SetSampler(0, sampler);
            deferredContext.UpdateSubresource(ref constants, constantsBuffer);

            foreach (var command in frame.Commands)
            {
                if (command.Vertices.Length > MaxVerticesPerCommand)
                {
                    continue;
                }

                deferredContext.MapSubresource(
                    vertexBuffer,
                    MapMode.WriteDiscard,
                    MapFlags.None,
                    out var vertexStream
                );
                try
                {
                    fixed (GBufferVertex* vertices = command.Vertices)
                    {
                        vertexStream.Write(
                            (nint)vertices,
                            0,
                            Utilities.SizeOf<GBufferVertex>() * command.Vertices.Length
                        );
                    }
                }
                finally
                {
                    deferredContext.UnmapSubresource(vertexBuffer, 0);
                    vertexStream.Dispose();
                }

                deferredContext.PixelShader.SetShaderResource(
                    0,
                    command.Texture ?? whiteTextureView
                );
                deferredContext.PixelShader.SetShaderResource(1, lightDiffuse);
                deferredContext.PixelShader.SetShaderResource(2, lightSpecular);
                deferredContext.Draw(command.Vertices.Length, 0);
            }

            using var commandList = deferredContext.FinishCommandList(false);
            ExecuteAnnotated(commandList, "Underpaint/SemitransparentComposite");
            if (!semitransparentCompositeDrawLogged)
            {
                semitransparentCompositeDrawLogged = true;
                log.Information(
                    $"[Underpaint] Semitransparent composite enabled: target={targetFormat}, size={width}x{height}."
                );
            }
        }
        finally
        {
            detouring = false;
            deferredContext.ClearState();
        }
    }

    private static (
        ShaderResourceView? Diffuse,
        ShaderResourceView? Specular
    ) FindSemitransparentLightViews(ShaderResourceView[] views)
    {
        var manager = RenderTargetManager.Instance();
        if (manager == null)
        {
            return (null, null);
        }

        var diffuseTexture = GetSemitransparentLightTexture(manager, specular: false);
        var specularTexture = GetSemitransparentLightTexture(manager, specular: true);
        ShaderResourceView? diffuse = null;
        ShaderResourceView? specular = null;
        foreach (var view in views)
        {
            if (view == null)
            {
                continue;
            }

            var resource = GetViewResource(view.NativePointer);
            if (diffuseTexture != null && resource == (nint)diffuseTexture->D3D11Texture2D)
            {
                diffuse = view;
            }
            if (specularTexture != null && resource == (nint)specularTexture->D3D11Texture2D)
            {
                specular = view;
            }
        }

        return (diffuse, specular);
    }

    private void PSSetShaderResourcesDetour(
        nint context,
        uint startSlot,
        uint numViews,
        nint* shaderResourceViews
    )
    {
        try
        {
            if (
                !detouring
                && context == immediateContextPointer
                && activeSemitransparentCycle != null
                && hasSemitransparentInjectedViewProjection
                && !semitransparentCompositeInjected
                && shaderResourceViews != null
                && BindsBothSemitransparentLightBuffers(numViews, shaderResourceViews)
            )
            {
                semitransparentCompositeArmed = true;
            }
        }
        catch (Exception exception)
        {
            log.Warning(
                exception,
                "[Underpaint] Failed to identify the semitransparent composite pass."
            );
        }
        finally
        {
            psSetShaderResourcesHook.Original(context, startSlot, numViews, shaderResourceViews);
        }
    }

    private static bool BindsBothSemitransparentLightBuffers(uint numViews, nint* views)
    {
        var manager = RenderTargetManager.Instance();
        if (manager == null)
        {
            return false;
        }

        var diffuseTexture = GetSemitransparentLightTexture(manager, specular: false);
        var specularTexture = GetSemitransparentLightTexture(manager, specular: true);
        var hasDiffuse = false;
        var hasSpecular = false;
        for (var slot = 0; slot < numViews; slot++)
        {
            var resource = GetViewResource(views[slot]);
            hasDiffuse |=
                diffuseTexture != null && resource == (nint)diffuseTexture->D3D11Texture2D;
            hasSpecular |=
                specularTexture != null && resource == (nint)specularTexture->D3D11Texture2D;
        }

        return hasDiffuse && hasSpecular;
    }

    private PendingTargets? TrackTargetChange(
        uint numViews,
        nint* renderTargetViews,
        nint depthStencilView
    )
    {
        PendingTargets? pending = null;
        GBufferTarget? detectedTarget = null;
        (int MatchedCount, uint Width, uint Height) match = default;

        if (depthStencilView != 0 && numViews == 5)
        {
            match = MatchFullGBuffers(numViews, renderTargetViews, GBufferTarget.Opaque);
            if (match.MatchedCount == 5)
            {
                detectedTarget = GBufferTarget.Opaque;
            }
        }
        else if (depthStencilView != 0 && numViews == 4)
        {
            match = MatchFullGBuffers(numViews, renderTargetViews, GBufferTarget.Semitransparent);
            if (match.MatchedCount == 4)
            {
                detectedTarget = GBufferTarget.Semitransparent;
            }
        }

        if (candidateActive && detectedTarget != candidateTarget)
        {
            var frame =
                candidateTarget == GBufferTarget.Opaque ? opaqueFrame : semitransparentFrame;
            if (frame != null && candidateRenderTargets.Length != 0 && candidateDepthStencil != 0)
            {
                pending = new PendingTargets(
                    candidateTarget,
                    candidateRenderTargets,
                    candidateDepthStencil,
                    candidateWidth,
                    candidateHeight,
                    candidateViewport
                );
            }
            ResetCandidate();
        }

        if (detectedTarget == null)
        {
            return pending;
        }

        candidateActive = true;
        candidateTarget = detectedTarget.Value;
        candidateDepthStencil = depthStencilView;
        candidateWidth = match.Width;
        candidateHeight = match.Height;
        candidateViewport = null;
        var expectedTargetCount = candidateTarget == GBufferTarget.Opaque ? 5 : 4;
        candidateRenderTargets = new nint[expectedTargetCount];
        for (var index = 0; index < expectedTargetCount; index++)
        {
            candidateRenderTargets[index] = renderTargetViews[index];
        }

        return pending;
    }

    private void IssueSafely(PendingTargets? pending)
    {
        if (pending == null)
        {
            return;
        }

        try
        {
            lock (stateLock)
            {
                var frame =
                    pending.Target == GBufferTarget.Opaque ? opaqueFrame : semitransparentFrame;
                if (frame != null)
                {
                    if (pending.Target == GBufferTarget.Semitransparent)
                    {
                        activeSemitransparentCycle?.Release();
                        activeSemitransparentCycle = frame.Retain();
                    }
                    Issue(pending, frame);
                }
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Underpaint] Failed to draw opaque G-buffer commands.");
        }
        finally
        {
            pending.Dispose();
        }
    }

    private void Issue(PendingTargets pending, GBufferFrame frame)
    {
        var control = Control.Instance();
        if (control == null)
        {
            return;
        }

        var viewProjection = *(Matrix*)&control->ViewProjectionMatrix;
        viewProjection.Transpose();
        if (pending.Target == GBufferTarget.Semitransparent)
        {
            semitransparentInjectedViewProjection = viewProjection;
            hasSemitransparentInjectedViewProjection = true;
            semitransparentCompositeArmed = false;
            semitransparentCompositeInjected = false;
        }
        var currentDitherPhase = ditherPhase;
        if (pending.Target == GBufferTarget.Opaque)
        {
            ditherPhase = (ditherPhase + 1) & 15;
        }
        var debugJitter =
            pending.Target == GBufferTarget.Opaque ? opaqueJitterPixels : NumericsVector2.Zero;
        var constants = new WorldConstants
        {
            ViewProjection = viewProjection,
            G0 = ToSharpDx(frame.Material.G0),
            G1 = ToSharpDx(frame.Material.G1),
            G2 = ToSharpDx(frame.Material.G2),
            G3 = ToSharpDx(frame.Material.G3),
            G4 = ToSharpDx(frame.Material.G4),
            DitherParameters = new Vector4(
                currentDitherPhase,
                pending.Target == GBufferTarget.Semitransparent ? 1f : 0f,
                pending.Target == GBufferTarget.Opaque && forceOpaqueAlpha ? 1f : 0f,
                0f
            ),
            DebugParameters = new Vector4(
                debugJitter.X,
                debugJitter.Y,
                pending.Width,
                pending.Height
            ),
        };

        try
        {
            detouring = true;
            deferredContext.ClearState();
            deferredContext.OutputMerger.SetTargets(pending.DepthStencil, pending.RenderTargets);
            deferredContext.OutputMerger.SetBlendState(blendState);
            var selectedDepthStencilState =
                pending.Target == GBufferTarget.Semitransparent
                    ? semitransparentDepthTestWriteStencilState
                    : depthStencilState;
            var stencilReference =
                pending.Target == GBufferTarget.Semitransparent ? 0x10 : frame.Material.Stencil;
            deferredContext.OutputMerger.SetDepthStencilState(
                selectedDepthStencilState,
                stencilReference
            );
            deferredContext.Rasterizer.SetViewport(pending.Viewport);
            deferredContext.Rasterizer.State = rasterizerState;
            deferredContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            deferredContext.InputAssembler.InputLayout = inputLayout;
            deferredContext.InputAssembler.SetVertexBuffers(
                0,
                new VertexBufferBinding(vertexBuffer, Utilities.SizeOf<GBufferVertex>(), 0)
            );
            deferredContext.HullShader.Set(null);
            deferredContext.DomainShader.Set(null);
            deferredContext.GeometryShader.Set(null);
            deferredContext.VertexShader.Set(vertexShader);
            deferredContext.VertexShader.SetConstantBuffer(0, constantsBuffer);
            deferredContext.PixelShader.Set(
                pending.Target == GBufferTarget.Semitransparent
                    ? semitransparentPixelShader
                    : pixelShader
            );
            deferredContext.PixelShader.SetConstantBuffer(0, constantsBuffer);
            deferredContext.PixelShader.SetSampler(0, sampler);
            deferredContext.UpdateSubresource(ref constants, constantsBuffer);

            foreach (var command in frame.Commands)
            {
                if (command.Vertices.Length > MaxVerticesPerCommand)
                {
                    log.Warning(
                        $"[Underpaint] Opaque command has {command.Vertices.Length} vertices; maximum is {MaxVerticesPerCommand}."
                    );
                    continue;
                }

                deferredContext.MapSubresource(
                    vertexBuffer,
                    MapMode.WriteDiscard,
                    MapFlags.None,
                    out var vertexStream
                );
                try
                {
                    fixed (GBufferVertex* vertices = command.Vertices)
                    {
                        vertexStream.Write(
                            (nint)vertices,
                            0,
                            Utilities.SizeOf<GBufferVertex>() * command.Vertices.Length
                        );
                    }
                }
                finally
                {
                    deferredContext.UnmapSubresource(vertexBuffer, 0);
                    vertexStream.Dispose();
                }

                deferredContext.PixelShader.SetShaderResource(
                    0,
                    command.Texture ?? whiteTextureView
                );
                deferredContext.Draw(command.Vertices.Length, 0);
            }

            using var commandList = deferredContext.FinishCommandList(false);
            ExecuteAnnotated(
                commandList,
                pending.Target == GBufferTarget.Opaque
                    ? "Underpaint/Opaque"
                    : "Underpaint/SemitransparentGBuffer"
            );
            if (pending.Target == GBufferTarget.Semitransparent && !semitransparentDrawLogged)
            {
                semitransparentDrawLogged = true;
                log.Information(
                    $"[Underpaint] Semitransparent G-buffer backend enabled: commands={frame.Commands.Length}, vertices={frame.Commands.Sum(command => command.Vertices.Length)}."
                );
            }
        }
        finally
        {
            detouring = false;
            deferredContext.ClearState();
        }
    }

    private void ExecuteAnnotated(CommandList commandList, string name)
    {
        annotation?.BeginEvent(name);
        try
        {
            immediateContext.ExecuteCommandList(commandList, true);
        }
        finally
        {
            annotation?.EndEvent();
        }
    }

    private (Texture2D Texture, ShaderResourceView View) CreateWhiteTexture()
    {
        var pixel = uint.MaxValue;
        var description = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = SharpDX.DXGI.Format.R8G8B8A8_UNorm,
            SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        };
        var texture = new Texture2D(
            device,
            description,
            [new DataRectangle((nint)(&pixel), sizeof(uint))]
        );
        return (texture, new ShaderResourceView(device, texture));
    }

    private (int MatchedCount, uint Width, uint Height) MatchFullGBuffers(
        uint numViews,
        nint* renderTargetViews,
        GBufferTarget target
    )
    {
        var manager = RenderTargetManager.Instance();
        var expectedTargetCount = target == GBufferTarget.Opaque ? 5 : 4;
        if (manager == null || renderTargetViews == null || numViews < expectedTargetCount)
        {
            return default;
        }

        var matchedCount = 0;
        uint width = 0;
        uint height = 0;
        for (var slot = 0; slot < expectedTargetCount; slot++)
        {
            var index = target == GBufferTarget.Semitransparent && slot == 3 ? 4 : slot;
            var texture =
                target == GBufferTarget.Opaque
                    ? manager->GBuffers[index].Value
                    : manager->SemitransparentGBuffers[index].Value;
            if (texture == null || texture->MipRenderTargets == null)
            {
                continue;
            }

            var expectedView = (nint)
                texture->MipRenderTargets->D3D11RenderTargetViewOrDepthStencilView;
            if (
                expectedView == 0
                || (
                    renderTargetViews[slot] != expectedView
                    && GetViewResource(renderTargetViews[slot]) != (nint)texture->D3D11Texture2D
                )
            )
            {
                continue;
            }

            matchedCount++;
            if (width == 0)
            {
                width = texture->ActualWidth;
                height = texture->ActualHeight;
            }
        }

        return (matchedCount, width, height);
    }

    private static KernelTexture* GetSemitransparentLightTexture(
        RenderTargetManager* manager,
        bool specular
    ) => *(KernelTexture**)((byte*)manager + (specular ? 0xD8 : 0xD0));

    private static nint GetViewResource(nint renderTargetView)
    {
        if (renderTargetView == 0)
        {
            return 0;
        }

        nint resource = 0;
        try
        {
            var vtable = *(nint**)renderTargetView;
            var getResource = (delegate* unmanaged[Stdcall]<nint, nint*, void>)vtable[7];
            getResource(renderTargetView, &resource);
            return resource;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (resource != 0)
            {
                Marshal.Release(resource);
            }
        }
    }

    private void ResetCandidate()
    {
        candidateActive = false;
        candidateRenderTargets = [];
        candidateDepthStencil = 0;
        candidateWidth = 0;
        candidateHeight = 0;
        candidateViewport = null;
    }

    private static Vector4 ToSharpDx(NumericsVector4 value) =>
        new(value.X, value.Y, value.Z, value.W);
}
