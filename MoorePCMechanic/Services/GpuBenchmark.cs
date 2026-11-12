// Moore PC Mechanic - graphics card benchmark (Direct3D 11 compute shaders)
// 1) Compute: millions of GPU threads each run a long chain of multiply-add math. Counting the
//    math operations per second gives TFLOPS (trillions of floating-point operations per second).
// 2) Video memory: copies a 512 MB buffer to another one on the card, many times.
// Uses the card with the most video memory (your graphics card, not the processor's built-in graphics).

using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MoorePCMechanic.Services;

public sealed class GpuResult
{
    public string Adapter { get; init; } = "";
    public double? Tflops { get; init; }
    public double? MemGBs { get; init; }
    public string? Error { get; init; }
}

public static class GpuBenchmark
{
    private const int ThreadsPerGroup = 256;
    private const int Groups = 8192;            // 2,097,152 GPU threads per run of the shader
    private const int LoopCount = 512;
    private const double FlopsPerThread = LoopCount * 8 * 4 * 2.0; // 8 float4 multiply-adds per loop, 4 lanes, 2 operations each
    private const double FlopsPerDispatch = FlopsPerThread * ThreadsPerGroup * Groups;
    private const int OutElements = 65536;
    private const int CopyBytes = 512 * 1024 * 1024;

    private const string Shader = @"
RWStructuredBuffer<float4> Out : register(u0);
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float4 a = float4(id.x * 1e-7, 1.0001, 0.9999, 1.0);
    float4 b = a * 0.5 + 0.1;
    float4 c = a * 0.25 + 0.2;
    float4 d = a * 0.125 + 0.3;
    float4 m = float4(0.9999, 1.0001, 0.99995, 1.00005);
    float4 k = float4(0.0001, -0.0001, 0.00005, -0.00005);
    [loop]
    for (int i = 0; i < 512; i++)
    {
        a = a * m + k; b = b * m + k; c = c * m + k; d = d * m + k;
        a = a * m - k; b = b * m - k; c = c * m - k; d = d * m - k;
    }
    Out[id.x & 65535] = a + b + c + d;
}";

    public static GpuResult Run(CancellationToken ct)
    {
        string adapterName = "";
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? best = null;
            ulong bestMem = 0;
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1 a).Success; i++)
            {
                var desc = a.Description1;
                bool software = (desc.Flags & AdapterFlags.Software) != 0;
                ulong mem = (ulong)desc.DedicatedVideoMemory;
                if (!software && (best == null || mem > bestMem))
                {
                    best?.Dispose();
                    best = a;
                    bestMem = mem;
                    adapterName = desc.Description;
                }
                else a.Dispose();
            }
            if (best == null) return new GpuResult { Error = "No graphics card found." };

            using var adapter = best;
            var levels = new[] { FeatureLevel.Level_11_0 };
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None, levels,
                                    out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
            using (device)
            using (ctx)
            {
                var bytecode = Compiler.Compile(Shader, "main", "bench.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
                using var cs = device.CreateComputeShader(bytecode.Span);
                using var outBuf = device.CreateBuffer(new BufferDescription(OutElements * 16, BindFlags.UnorderedAccess,
                    ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
                using var uav = device.CreateUnorderedAccessView(outBuf);
                using var done = device.CreateQuery(QueryType.Event);

                ctx.CSSetShader(cs);
                ctx.CSSetUnorderedAccessView(0, uav);

                void Wait()
                {
                    ctx.End(done);
                    ctx.Flush();
                    var limit = Stopwatch.StartNew();
                    while (!ctx.IsDataAvailable(done))
                    {
                        if (limit.Elapsed.TotalSeconds > 20) throw new TimeoutException("The graphics card stopped responding to the test.");
                        Thread.Yield();
                    }
                }

                // ---- compute
                for (int i = 0; i < 3; i++) ctx.Dispatch(Groups, 1, 1); // warm up: let the card reach full clock
                Wait();
                double bestTflops = 0;
                for (int round = 0; round < 3; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    int dispatches = 0;
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed.TotalSeconds < 0.6)
                    {
                        for (int i = 0; i < 10; i++) ctx.Dispatch(Groups, 1, 1);
                        dispatches += 10;
                        Wait();
                    }
                    bestTflops = Math.Max(bestTflops, dispatches * FlopsPerDispatch / sw.Elapsed.TotalSeconds / 1e12);
                }
                ctx.CSSetUnorderedAccessView(0, null);
                ctx.CSSetShader(null);

                // ---- video memory bandwidth
                double bestGBs = 0;
                using (var a = device.CreateBuffer(new BufferDescription(CopyBytes, BindFlags.UnorderedAccess)))
                using (var b = device.CreateBuffer(new BufferDescription(CopyBytes, BindFlags.UnorderedAccess)))
                {
                    ctx.CopyResource(b, a);
                    Wait();
                    for (int round = 0; round < 3; round++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var sw = Stopwatch.StartNew();
                        const int copies = 20;
                        for (int i = 0; i < copies; i++)
                        {
                            if ((i & 1) == 0) ctx.CopyResource(b, a); else ctx.CopyResource(a, b);
                        }
                        Wait();
                        bestGBs = Math.Max(bestGBs, 2.0 * CopyBytes * copies / sw.Elapsed.TotalSeconds / 1e9); // read + write
                    }
                }

                return new GpuResult { Adapter = adapterName, Tflops = bestTflops, MemGBs = bestGBs };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GpuResult { Adapter = adapterName, Error = ex.Message };
        }
    }
}
