using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;
using Spayly.Sim;

namespace Spayly.Game.Render;

/// <summary>
/// Schwarzschild lensing as a post-process (Bruneton 2020's table method, see godot/CREDITS.md): a CompositorEffect at
/// <c>PostTransparent</c> that re-samples the HDR colour buffer along each pixel's deflected ray, blacks out captured rays
/// and ray-traces the accretion disc through the same tables.
///
/// Why a CompositorEffect and not a quad with <c>hint_screen_texture</c> (both exist in the dump): the effect runs on the
/// HDR buffer before tonemapping and glow, so the disc blooms and the auto-exposure applies to it like every other
/// emitter; it can read the depth buffer, so a planet in front of the hole stays unbent and a source's depth scales its
/// bend; and it runs after the transparent pass, so the additive point stars, coronae and atmosphere rims are lensed with
/// everything else. A spatial quad's screen texture is copied before the transparent pass; a CanvasLayer quad sees the
/// tonemapped LDR frame and no depth.
///
/// Threading: views publish per-frame hole parameters on the main thread (<see cref="Publish"/>); the render callback
/// copies them under a lock. With the default thread model both are the main thread, the lock costs nothing. The render
/// side allocates nothing per frame: uniforms, arrays and byte buffers are made once and reused.
/// </summary>
public partial class LensingPass : CompositorEffect
{
    /// <summary>Holes lensed at once; a fifth is simply not drawn (the shader's array size).</summary>
    public const int MaxHoles = 4;
    /// <summary>Lensing table resolution, texels per side (512 × 512 RGBA32F, 4 MB each).</summary>
    public const int TableSize = 512;
    const int HoleFloats = 20;
    const int ParamFloats = MaxHoles * HoleFloats + 24;
    static readonly StringName Context = "spayly_lensing", CopyName = "color_copy";
    const string SkyPath = "res://textures/milky_way.jpg";

    /// <summary>One hole's parameters for a frame. Vectors are in the render frame with the camera at the origin.</summary>
    public struct Hole
    {
        /// <summary>Unit direction from the camera to the hole.</summary>
        public Vector3 Dir;
        /// <summary>Unsqueezed camera distance, render units.</summary>
        public double TrueDistance;
        /// <summary>Rendered Schwarzschild radius (true or diorama-scaled), render units.</summary>
        public double RsUnits;
        /// <summary>Squeeze factor of the hole's placement (1 when near).</summary>
        public double Squeeze;
        /// <summary>Disc radii, r_s.</summary>
        public double DiscInner, DiscOuter;
        /// <summary>Disc pole (prograde) and first in-plane axis.</summary>
        public Vector3 DiscNormal, DiscX;
        /// <summary>Brightness at the temperature peak, before exposure compensation; 0 without a disc.</summary>
        public double DiscBrightness;
        /// <summary>Display temperature at the profile peak, K.</summary>
        public double DiscPeakK;
    }

    readonly object gate = new();
    readonly Hole[] pending = new Hole[MaxHoles];
    readonly bool[] used = new bool[MaxHoles];
    double pendingExposure = 1, pendingTime, pendingSqueeze = double.PositiveInfinity;
    bool pendingDisc = true, pendingDoppler = true;

    // Render side: a sorted snapshot of the pending holes.
    readonly Hole[] frame = new Hole[MaxHoles];
    int frameCount;
    double frameExposure = 1, frameTime, frameSqueeze = double.PositiveInfinity;
    bool frameDisc, frameDoppler;

    readonly Task<LensingTables> tables;
    readonly float[] lut;
    readonly Texture2D? skyTexture;

    RenderingDevice? rd;
    bool ready, failed;
    Rid copyShader, copyPipe, lensShader, lensPipe, linearClamp, nearestClamp, skySampler, escTex, capTex, lutTex, fallbackTex, ubo;
    readonly RDUniform[] lensU = new RDUniform[8];
    readonly RDUniform[] copyU = new RDUniform[2];
    Godot.Collections.Array<RDUniform>? lensSet, copySet;
    Rid lastColor, lastDepth, lastCopy, lastSky;
    readonly float[] paramFloats = new float[ParamFloats];
    bool formatChecked;

    public LensingPass()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
        // Built off the main thread (~0.2 s); the pass draws nothing until they are uploaded.
        tables = Task.Run(() => BlackHole.BuildLensingTables(TableSize, TableSize));
        lut = AccretionDisc.BuildColorLut();
        if (ResourceLoader.Exists(SkyPath)) skyTexture = ResourceLoader.Load<Texture2D>(SkyPath);
        Enabled = false;
    }

    /// <summary>Claim a slot for a hole; -1 when <see cref="MaxHoles"/> are already lensed.</summary>
    public int Register()
    {
        lock (gate)
        {
            for (var i = 0; i < MaxHoles; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                pending[i] = default;
                return i;
            }
        }
        return -1;
    }

    public void Unregister(int slot)
    {
        if (slot < 0 || slot >= MaxHoles) return;
        lock (gate) used[slot] = false;
    }

    /// <summary>Whether any hole holds a slot.</summary>
    public bool AnyRegistered
    {
        get
        {
            lock (gate)
            {
                foreach (var u in used) if (u) return true;
                return false;
            }
        }
    }

    /// <summary>This frame's parameters of the hole in <paramref name="slot"/>, and the frame-wide ones every hole shares.</summary>
    public void Publish(int slot, in Hole hole, double exposureComp, double realTime, double squeezeStart, bool disc, bool doppler)
    {
        if (slot < 0 || slot >= MaxHoles) return;
        lock (gate)
        {
            pending[slot] = hole;
            pendingExposure = exposureComp;
            pendingTime = realTime;
            pendingSqueeze = squeezeStart;
            pendingDisc = disc;
            pendingDoppler = doppler;
        }
    }

    /// <summary>Copy the published holes, nearest first (the disc of a nearer hole covers a farther one's).</summary>
    void Snapshot()
    {
        lock (gate)
        {
            frameCount = 0;
            for (var i = 0; i < MaxHoles; i++)
            {
                if (!used[i] || pending[i].RsUnits <= 0) continue;
                var h = pending[i];
                var k = frameCount++;
                while (k > 0 && frame[k - 1].TrueDistance > h.TrueDistance)
                {
                    frame[k] = frame[k - 1];
                    k--;
                }
                frame[k] = h;
            }
            frameExposure = pendingExposure;
            frameTime = pendingTime;
            frameSqueeze = pendingSqueeze;
            frameDisc = pendingDisc;
            frameDoppler = pendingDoppler;
        }
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (effectCallbackType != (int)EffectCallbackTypeEnum.PostTransparent) return;
        Snapshot();
        if (frameCount == 0 || !EnsureReady()) return;
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD rb) return;
        var size = rb.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0) return;
        var scene = renderData.GetRenderSceneData();
        var device = rd!;
        var color = rb.GetColorLayer(0);
        if (color != lastColor)
        {
            // Once per resize: the kernel's images are declared rgba16f, Forward+'s HDR buffer format.
            var fmt = device.TextureGetFormat(color);
            if (!formatChecked) GD.Print($"[lensing] colour buffer {fmt.Format} {size.X}x{size.Y}, views={rb.GetViewCount()}");
            formatChecked = true;
            if (fmt.Format != RenderingDevice.DataFormat.R16G16B16A16Sfloat)
            {
                GD.PushError($"[lensing] the kernel writes rgba16f but the colour buffer is {fmt.Format}; lensing disabled");
                failed = true;
                return;
            }
        }
        if (!rb.HasTexture(Context, CopyName))
        {
            const uint usage = (uint)(RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit);
            rb.CreateTexture(Context, CopyName, RenderingDevice.DataFormat.R16G16B16A16Sfloat, usage, RenderingDevice.TextureSamples.Samples1, size, 1, 1, true, false);
        }
        var depth = rb.GetDepthLayer(0);
        var copy = rb.GetTexture(Context, CopyName);
        var sky = skyTexture != null ? RenderingServer.TextureGetRdTexture(skyTexture.GetRid(), true) : fallbackTex;
        if (!sky.IsValid) sky = fallbackTex;
        BindTargets(color, depth, copy, sky);

        var cam = scene.GetCamTransform().Basis;
        var proj = scene.GetCamProjection();
        FillParams(cam);
        device.BufferUpdate(ubo, 0, (uint)(ParamFloats * 4), MemoryMarshal.AsBytes(paramFloats.AsSpan()));

        var p00 = Math.Abs(proj.X.X);
        var p11 = Math.Abs(proj.Y.Y);
        Span<byte> pcCopy = stackalloc byte[16];
        var fc = MemoryMarshal.Cast<byte, float>(pcCopy);
        fc[0] = size.X;
        fc[1] = size.Y;
        Span<byte> pcLens = stackalloc byte[48];
        var fl = MemoryMarshal.Cast<byte, float>(pcLens);
        fl[0] = size.X;
        fl[1] = size.Y;
        fl[2] = p00;
        fl[3] = p11;
        // The render-side projection is Vulkan reverse-Z (depth 1 at the near plane, 0 at the far one), for which
        // Projection.GetZNear/GetZFar, written for the OpenGL form, return garbage (both gave -0.576, which black-holed
        // every pixel into the "surface in front" early-out). From depth = (A z + B)/(-z): near = B/(A+1), far = B/A.
        var depthA = (double)proj.Z.Z;
        var depthB = (double)proj.W.Z;
        fl[4] = (float)(depthB / (depthA + 1));
        fl[5] = (float)(depthB / depthA);
        fl[6] = (float)Math.Min(frameSqueeze, 1e30);
        fl[7] = 0.5f * size.Y * p11;
        fl[8] = (float)frameExposure;
        fl[9] = (float)(frameTime % 3600);
        fl[10] = frameCount;
        fl[11] = (frameDisc ? 1 : 0) | (frameDoppler ? 2 : 0) | (skyTexture != null && sky != fallbackTex ? 4 : 0);

        var gx = (uint)((size.X + 7) / 8);
        var gy = (uint)((size.Y + 7) / 8);
        var copyUniforms = UniformSetCacheRD.GetCache(copyShader, 0, copySet!);
        var lensUniforms = UniformSetCacheRD.GetCache(lensShader, 0, lensSet!);
        // Two lists, so the render graph orders the copy's storage write before the lens kernel samples it.
        var list = device.ComputeListBegin();
        device.ComputeListBindComputePipeline(list, copyPipe);
        device.ComputeListBindUniformSet(list, copyUniforms, 0);
        device.ComputeListSetPushConstant(list, pcCopy, 16);
        device.ComputeListDispatch(list, gx, gy, 1);
        device.ComputeListEnd();
        list = device.ComputeListBegin();
        device.ComputeListBindComputePipeline(list, lensPipe);
        device.ComputeListBindUniformSet(list, lensUniforms, 0);
        device.ComputeListSetPushConstant(list, pcLens, 48);
        device.ComputeListDispatch(list, gx, gy, 1);
        device.ComputeListEnd();
    }

    /// <summary>The std140 block of the shader: four holes (5 vec4 each), the camera basis, the Milky Way basis.</summary>
    void FillParams(Basis cam)
    {
        var p = paramFloats;
        Array.Clear(p);
        for (var i = 0; i < frameCount; i++)
        {
            ref readonly var h = ref frame[i];
            var o = i * HoleFloats;
            Put(p, o, h.Dir, h.TrueDistance / h.RsUnits);
            p[o + 4] = (float)h.RsUnits;
            p[o + 5] = (float)(h.TrueDistance * h.Squeeze);
            p[o + 6] = (float)h.DiscInner;
            p[o + 7] = (float)h.DiscOuter;
            Put(p, o + 8, h.DiscNormal, h.DiscBrightness);
            Put(p, o + 12, h.DiscX, h.DiscPeakK);
            p[o + 16] = (float)h.TrueDistance;
            p[o + 17] = (float)h.Squeeze;
        }
        var b = MaxHoles * HoleFloats;
        Put(p, b, cam.X, 0);
        Put(p, b + 4, cam.Y, 0);
        Put(p, b + 8, cam.Z, 0);
        Put(p, b + 12, GalX, 0);
        Put(p, b + 16, GalY, 0);
        Put(p, b + 20, GalZ, 0);
    }

    static void Put(float[] p, int o, Vector3 v, double w)
    {
        p[o] = v.X;
        p[o + 1] = v.Y;
        p[o + 2] = v.Z;
        p[o + 3] = (float)w;
    }

    // Sky.cs's galactic basis in the render frame (pole and centre from their ecliptic coordinates), repeated here because
    // Sky keeps it inside its material.
    static readonly Vector3 GalY = new Vector3(-0.8676f, 0.4971f, 0.0003f).Normalized();
    static readonly Vector3 GalX = (new Vector3(-0.0556f, -0.0958f, 0.9937f).Normalized() - GalY * new Vector3(-0.0556f, -0.0958f, 0.9937f).Normalized().Dot(GalY)).Normalized();
    static readonly Vector3 GalZ = GalX.Cross(GalY);

    /// <summary>Point the uniforms at this frame's buffers; they change only when the viewport is resized.</summary>
    void BindTargets(Rid color, Rid depth, Rid copy, Rid sky)
    {
        if (color == lastColor && depth == lastDepth && copy == lastCopy && sky == lastSky) return;
        lastColor = color;
        lastDepth = depth;
        lastCopy = copy;
        lastSky = sky;
        Ids(copyU[0], color);
        Ids(copyU[1], copy);
        Ids(lensU[0], color);
        Ids(lensU[1], linearClamp, copy);
        Ids(lensU[2], nearestClamp, depth);
        Ids(lensU[5], skySampler, sky);
    }

    static void Ids(RDUniform u, Rid a)
    {
        u.ClearIds();
        u.AddId(a);
    }

    static void Ids(RDUniform u, Rid sampler, Rid texture)
    {
        u.ClearIds();
        u.AddId(sampler);
        u.AddId(texture);
    }

    /// <summary>Compile, allocate and upload once, on the render thread, as soon as the tables are built.</summary>
    bool EnsureReady()
    {
        if (ready) return true;
        if (failed || !tables.IsCompleted) return false;
        try
        {
            var device = rd = RenderingServer.GetRenderingDevice();
            if (device == null) throw new InvalidOperationException("no RenderingDevice (compatibility renderer?)");
            (copyShader, copyPipe) = Compile(device, "lensing_copy", LensingShader.Copy);
            (lensShader, lensPipe) = Compile(device, "lensing", LensingShader.Lens);
            linearClamp = device.SamplerCreate(new RDSamplerState
            {
                MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear,
                RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            });
            nearestClamp = device.SamplerCreate(new RDSamplerState
            {
                MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest,
                RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            });
            skySampler = device.SamplerCreate(new RDSamplerState
            {
                MinFilter = RenderingDevice.SamplerFilter.Linear, MagFilter = RenderingDevice.SamplerFilter.Linear,
                MipFilter = RenderingDevice.SamplerFilter.Linear,
                RepeatU = RenderingDevice.SamplerRepeatMode.Repeat, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            });
            var t = tables.Result;
            escTex = FloatTexture(device, t.Width, t.Height, t.Escape);
            capTex = FloatTexture(device, t.Width, t.Height, t.Capture);
            lutTex = FloatTexture(device, lut.Length / 4, 1, lut);
            fallbackTex = FloatTexture(device, 1, 1, [0, 0, 0, 1]);
            ubo = device.UniformBufferCreate(ParamFloats * 4, new byte[ParamFloats * 4]);
            if (!ubo.IsValid) throw new InvalidOperationException("UniformBufferCreate failed");

            copyU[0] = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
            copyU[1] = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
            lensU[0] = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
            for (var b = 1; b <= 6; b++) lensU[b] = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = b };
            lensU[7] = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 7 };
            Ids(lensU[3], nearestClamp, escTex);
            Ids(lensU[4], nearestClamp, capTex);
            Ids(lensU[6], linearClamp, lutTex);
            Ids(lensU[7], ubo);
            copySet = new Godot.Collections.Array<RDUniform>(copyU);
            lensSet = new Godot.Collections.Array<RDUniform>(lensU);
            GD.Print($"[lensing] ready: tables {t.Width}x{t.Height}, sky map {(skyTexture != null ? "milky_way.jpg" : "none (procedural band)")}");
            ready = true;
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError($"[lensing] setup failed, lensing disabled: {ex.Message}");
            failed = true;
            return false;
        }
    }

    static (Rid Shader, Rid Pipeline) Compile(RenderingDevice device, string name, string src)
    {
        var spirv = device.ShaderCompileSpirVFromSource(new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src });
        var err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) throw new InvalidOperationException($"GLSL compile error in {name}:\n{err}");
        var shader = device.ShaderCreateFromSpirV(spirv, name);
        if (!shader.IsValid) throw new InvalidOperationException($"ShaderCreateFromSpirV({name}) failed");
        var pipe = device.ComputePipelineCreate(shader);
        if (!pipe.IsValid) throw new InvalidOperationException($"ComputePipelineCreate({name}) failed");
        return (shader, pipe);
    }

    static Rid FloatTexture(RenderingDevice device, int w, int h, float[] rgba)
    {
        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)w,
            Height = (uint)h,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        var tex = device.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> { MemoryMarshal.AsBytes(rgba.AsSpan()).ToArray() });
        if (!tex.IsValid) throw new InvalidOperationException($"TextureCreate({w}x{h} rgba32f) failed");
        return tex;
    }

    /// <summary>Free every RID this pass created (the colour copy belongs to the render buffers, the sky map to its texture).</summary>
    public void Release()
    {
        if (!ready || rd == null) return;
        ready = false;
        failed = true;
        var device = rd;
        void Free()
        {
            // Pipelines go with their shaders, but freeing them first keeps the dependency order explicit.
            foreach (var r in new[] { copyPipe, lensPipe, copyShader, lensShader, linearClamp, nearestClamp, skySampler, escTex, capTex, lutTex, fallbackTex, ubo })
                if (r.IsValid) device.FreeRid(r);
        }
        if (RenderingServer.IsOnRenderThread()) Free();
        else RenderingServer.CallOnRenderThread(Callable.From(Free));
    }

    public override void _Notification(int what)
    {
        if (what == NotificationPredelete) Release();
    }
}
