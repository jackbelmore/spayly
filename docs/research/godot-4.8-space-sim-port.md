> **UNTRUSTED research (copied 2026-09-24).** Claims here are not facts until verified; verified claims live in `docs/godot/decisions.md`.

# **Technical Architecture & Feasibility Report: "spayly" Godot 4.8 Migration**

## **Executive Summary: Architectural Readiness Matrix**

The migration of the browser-based space-simulation game "spayly" to the Godot 4.8 rendering and physics pipeline represents a profound architectural shift. Evaluating this transition against the project's target parameters—N-body gravitational mechanics, astronomically scaled spatial rendering (![][image1] m to ![][image2] km), high-density particle simulations via GPU compute, and a purely automated AI-driven (Claude Code) development workflow—reveals a complex landscape. The state of Godot 4.8, as of September 2026, presents highly advanced rendering capabilities juxtaposed against critical bottlenecks in API synchronization and memory management.  
Below is the high-level readiness assessment for the six primary domains of the project architecture.

| Architectural Domain | Readiness Status | Strategic Justification |
| :---- | :---- | :---- |
| **State of Godot 4.8** | **CAUTION** | Godot 4.8 dev 6 (released September 15, 2026\) is highly capable but remains a pre-release snapshot. While it hotfixes critical engine crashes rapidly, regressions in subsystems (such as OpenXR) demonstrate the inherent volatility of relying on development branches for long-term AI-driven engineering. |
| **Texture Streaming** | **GO** | Introduced in 4.8 dev 5, mip-level texture streaming mitigates the severe VRAM constraints inherent to 16K equirectangular planet maps. The system allows dynamic memory budgeting, provided custom shaders are explicitly authored to utilize the new streamed sampler uniforms. |
| **Astronomical Scale** | **BLOCKER** | Official Godot C\# (.NET) assemblies are not published with double-precision (precision=double) enabled. Maintaining custom engine and C\# assembly builds solely for an AI agent introduces severe pipeline friction, necessitating a decoupled physics architecture. |
| **GPU Compute Particles** | **BLOCKER** | A confirmed bug in the RenderingDevice pipeline (Issue \#105113) prevents direct GPU-to-screen rendering via MultiMesh without a CPU-side read/write roundtrip. This obliterates the PCIe bandwidth and performance benefits of compute shaders for millions of particles. |
| **C\# / .NET in 4.8** | **CAUTION** | Godot 4 provides excellent modern .NET support, but passing large arrays to the Vulkan RenderingDevice incurs unavoidable byte\[\] allocations and memory copies. This creates significant garbage collection (GC) stutter risks for per-frame updates. |
| **AI Agent Tooling** | **GO** | Headless CLI execution, xvfb-run for visual test captures, and code-driven scene generation (bypassing raw .tscn text editing) provide a robust, automatable framework for an autonomous coding agent operating without visual editor access. |

## **1\. State of Godot (as of September 2026\)**

### **Engine Release Trajectory and Stability**

As of mid-September 2026, the Godot engine ecosystem is bifurcated between the stable 4.7 maintenance branch (currently 4.7.2) and the aggressive feature integration of the 4.8 pre-release cycle1. The most current snapshot is Godot 4.8 dev 6, released on September 15, 20262. The remarkably tight release cadence—arriving mere days after dev 5—signals that the 4.8 development cycle is closely approaching its feature freeze, shifting priority toward stabilization rather than broad feature implementation2.  
Dev 6 functions primarily as a critical stabilization hotfix. It directly addresses severe macOS Metal rendering startup crashes (GH-123439) and Google Play Store upload failures (GH-123474) that plagued the preceding dev 5 build4. For an AI coding agent operating over a multi-month development timeline, relying on these development snapshots introduces a measurable degree of structural volatility. While core APIs in the 4.x lineage rarely undergo fundamental paradigm shifts this late in the major version cycle, method signatures, import settings, and resource definitions do experience deprecation or renaming. Furthermore, regressions are standard in dev snapshots; for instance, OpenXR Spatial Container integration remains entirely broken in dev 6 due to an ongoing regression introduced by multi-layer projection camera implementations in GH-1164245. An AI agent must be programmed to handle these transient regressions gracefully without assuming hallucinated code is the root cause of an engine-level failure.

### **Feature Evolution: 4.5 through 4.8**

The progression of the Godot engine from version 4.5 to 4.8 has systematically targeted high-end 3D rendering fidelity and large-scale world management. These advancements directly benefit the technical requirements of a complex space simulation project.  
The 4.5 and 4.6 releases stabilized the core Forward+ rendering pipeline, significantly improved VoxelGI performance, and introduced foundational architectural shifts for embedding Godot as a library via the LibGodot initiative6. Following this, Godot 4.7 introduced a revamped Asset Store, native HDR output support, and dedicated rectangular light source nodes8.  
The current 4.8 cycle represents a massive leap in rendering fidelity, memory management, and quality-of-life tools for technical artists. Rendering improvements have received particular focus, with the introduction of multi-bounce ambient occlusion (approximating light bounces between surfaces for deeper scene naturalism) and directional lightmap specular lighting8. Furthermore, 4.8 dev 6 integrated screen-space contact shadows (SSCS) for directional lights, a feature implemented by Rudolph Bester (GH-118045) using algorithms pioneered by Bend Studio. This feature utilizes a localized R8 shadow texture blended with existing shadow maps to produce high-fidelity micro-details on geometry without compromising performance4. For "spayly," this feature is highly relevant for rendering intricate details on asteroid surfaces and planetary terrain topologies.

| Claim/Feature | Godot Version | Source | Date |
| :---- | :---- | :---- | :---- |
| 4.8 dev 6 release and Metal/Android hotfixes | 4.8 dev 6 | Official Godot Blog, Pixelwolf2 | Sept 15, 2026 |
| OpenXR Spatial Container regression (GH-116424) | 4.8 dev 6 | Official Godot Blog5 | Sept 15, 2026 |
| Screen-space contact shadows (SSCS) | 4.8 dev 6 | Official Godot Blog, Reddit4 | Sept 15, 2026 |
| 4.8 features (Multi-bounce AO, Lightmap specular) | 4.8 dev 4 | 80.lv Godot 4.8 coverage8 | Aug 27, 2026 |

## **2\. Texture Streaming**

### **Internal Mechanics and Memory Relief**

A historic and heavily criticized limitation of the Godot 4.x rendering pipeline was the requirement that all textures within a loaded scene fit entirely within the GPU's VRAM10. This limitation rendered large open worlds, and specifically high-resolution planetary textures, computationally hazardous. Godot 4.8 dev 5 introduces mip-level texture streaming (PR \#113429 by Trevor Davenport), resolving this long-standing bottleneck11.  
Internally, the system does not stream textures from the disk to the GPU on a purely per-asset basis; rather, it dynamically loads and unloads specific mipmap levels of a texture based on the camera's relative position and distance12. When rendering a massive astronomical body with a 16384x8192 equirectangular map, holding the entire asset in memory when the planet occupies only a few pixels on the screen is a catastrophic waste of VRAM. With streaming, the engine evaluates the screen-space footprint of the material and loads only the lowest-resolution mipmaps required to fulfill the rendering equation14. As the floating-origin camera approaches the body, higher-resolution mip levels are streamed into VRAM asynchronously via the engine's resource loader threads.  
This architecture directly dictates asset import times and on-disk footprint. A 16K texture is exceptionally large; uncompressed, a 16384x8192 texture with four channels occupies roughly 536 MB of disk space. Generating the full mip chain during the initial project import phase adds approximately 33% more data. While the engine utilizes Block Compression (BPTC or S3TC) to reduce this footprint by a factor of 4 to 6 (bringing a 16K map down to roughly 130 MB on disk), importing 39 planetary bodies and 23 moons at this scale will incur severe initial import times11. However, the runtime VRAM cost remains strictly budgeted to the currently visible mip levels, making the project viable on a 12 GB RTX 3080 Ti.

### **Enabling the System and API Controls**

Texture streaming is architected as a two-tiered, opt-in system. This design prevents the aggressive, global texture pop-in associated with naive streaming implementations, allowing developers to maintain full resolution on hero assets while background props degrade gracefully13.  
To utilize the system, streaming must first be enabled globally within the project settings, a change that requires a full editor restart11. Once the global setting is active, individual textures must be imported utilizing a new import type: Texture2D Streamed (which correlates to the StreamedTexture2D class API)11. The system exposes minimum and maximum resolution overrides per texture, allowing the AI agent to explicitly define VRAM budgets per asset11. Furthermore, the TextureStreaming singleton exposes runtime configurations, allowing game code to manipulate overall streaming memory budgets on the fly based on the player's hardware11.  
A critical parallel fix shipped in 4.8 dev 5 (GH-104289 by Kasper Arnklit Frandsen) ensures that streamed textures preserve alpha test coverage at lower mip levels12. Without this fix, alpha-scissor materials—frequently used for rendering distant asteroid fields or foliage cards—would dissolve into noise when downscaled, as averaging the alpha channel destroys the strict binary threshold required by the scissor test13.

### **Shader Integration and Compatibility**

For the custom GLSL or Godot shading language materials required for planetary atmospheres and black hole lensing, texture streaming requires explicit architectural compliance. Standard sampler2D uniforms defined in custom shaders will not automatically hook into the texture streaming system. The shader language requires the explicit use of the sampler2DStreamed uniform type19. This ensures the rendering pipeline receives the necessary feedback regarding screen-space usage and UV derivatives to trigger the correct mip-level loads from disk.

| Claim/Feature | Godot Version | Source | Date |
| :---- | :---- | :---- | :---- |
| Mip-level texture streaming (PR \#113429) | 4.8 dev 5 | Godot Blog, GameFromScratch11 | Sept 10/11, 2026 |
| Opt-in system via Texture2D Streamed | 4.8 dev 5 | 3DXDev, Godot Blog11 | Sept 10, 2026 |
| Alpha test coverage preservation (GH-104289) | 4.8 dev 5 | Godot Blog, 3DXDev12 | Sept 10, 2026 |
| Shader integration requires sampler2DStreamed | 4.x Proposals | Godot GitHub Proposals \#317719 | Aug 2024 |

## **3\. Rendering at Astronomical Scale**

### **The Double-Precision (.NET) Blocker**

Simulating a solar system where a camera can observe a spacecraft docking ring at a 1-meter scale and subsequently track a planetary orbit spanning ![][image2] kilometers demands extreme numerical stability. Standard 32-bit floating-point numbers (float) suffer from catastrophic precision loss (jitter) at these magnitudes, causing physics to break down and geometry to tear visually. Godot natively supports 64-bit precision if compiled from source using the \--precision=double flag, which promotes the internal real\_t typedef from a 32-bit float to a 64-bit double across the entire engine20.  
However, relying on this feature poses a severe infrastructural blocker for a C\#/.NET project managed by an autonomous AI agent. The official Godot C\# assemblies published to NuGet are compiled exclusively for single precision22. If the Godot engine binary is built with precision=double and module\_mono\_enabled=yes, the C\# wrapper assemblies do not automatically adapt; they continue to treat native types like Vector3 as composites of 32-bit floats. This mismatch results in immediate compilation errors (e.g., Operator '\*' cannot be applied to operands of type 'Vector3' and 'double') unless the GodotSharp API assemblies are entirely recompiled locally to match the engine's precision flag22. Forcing an AI agent to execute complex, multi-step local compilation of the Godot .NET bindings via CLI severely complicates the CI/CD pipeline and introduces massive points of failure.

### **The Floating Origin Architecture**

Given the severe friction of managing custom double-precision C\# builds, the optimal architectural path for "spayly" is to entirely decouple the physics simulation from the engine's built-in spatial types.  
The AI agent must build a pure C\# N-Body Simulation class library. This library will execute all astronomical integrations utilizing standard System.Double math and custom 64-bit vector structures, entirely isolated from the Godot API. Godot's single-precision 3D renderer will be utilized strictly as a visual presentation layer. To achieve this, the architecture must implement a "Floating Origin" pattern. In this paradigm, the active camera remains anchored near the world origin (0,0,0). The C\# backend computes the 64-bit positions of all celestial bodies relative to the camera's true 64-bit position in the universe. These relative coordinates are then safely downcast to 32-bit floats and applied to the Godot Node3D transforms for rendering.  
Furthermore, Godot 4 natively utilizes a Reverse-Z depth buffer mapping. Traditional depth buffers lose precision logarithmically as distance increases, leading to Z-fighting on distant overlapping geometry. Reverse-Z flips this mapping, allocating floating-point precision much more efficiently across the frustum and pushing the far-plane limits significantly further without artifacting, which is highly advantageous for rendering distant planetary bodies.

### **Atmospheric Scattering Implementations**

For realistic planet rendering, Eric Bruneton’s seminal "Precomputed Atmospheric Scattering" (EGSR 2008\) remains the industry gold standard25. The algorithm models highly accurate multiple-scattering physical interactions (Rayleigh and Mie scattering)25. However, the mathematical complexity of Bruneton's model maps poorly to simple, real-time pixel shaders; performing the numerical integration of the airmass loop per-pixel is computationally devastating26.  
The optimal implementation relies on precomputing these scattering integrals into 3D Lookup Tables (LUTs)25. Several open-source implementations have successfully ported Bruneton's model to GLSL and adapted it for Godot's ecosystem (such as the "Zarevo" landscape experiments by Bananaft), proving that the precomputed LUT approach is highly compatible with Godot's custom shader nodes and environment maps27.

| Claim/Feature | Godot Version | Source | Date |
| :---- | :---- | :---- | :---- |
| Double precision requires custom GodotSharp rebuild | 4.x (.NET) | GitHub Godot Proposals \#862822 | Dec 2023 |
| Vector3/double compilation errors on mismatch | 4.2.1+ | GitHub Issue \#9018423 | Apr 2024 |
| Bruneton scattering port to Godot / GLSL | 4.x | Urho3D Discourse / Godot Reddit27 | N/A |

## **4\. GPU Compute for Particles (The Core Technical Risk)**

The most perilous architectural risk for "spayly" involves simulating and rendering tens of thousands to millions of collision-enabled debris particles (utilizing Smoothed Particle Hydrodynamics or SPH) via Godot's RenderingDevice API from C\#.

### **RenderingDevice and Buffer Management**

Godot 4 exposes low-level Vulkan abstractions directly through the RenderingDevice API, allowing developers to execute highly parallel compute shaders without modifying the engine source29. An instance of this device is retrieved either via RenderingServer.GetRenderingDevice() (accessing the primary graphics queue) or RenderingServer.CreateLocalRenderingDevice() (spinning up a dedicated context for asynchronous, off-thread compute tasks)29.  
Data is passed to these compute shaders via Storage Buffers. In GLSL, these buffers must adhere strictly to std430 memory layout rules. This introduces significant structural friction when marshaling C\# structures to GPU memory32. The std430 specification enforces rigorous alignment padding; for example, an array of vec3 elements within a struct is padded to a 16-byte alignment per element, functioning identically in memory to a vec432. If the AI agent does not explicitly pad the C\# byte arrays or \[StructLayout\] declarations to match this 16-byte alignment, the resulting memory upload will corrupt the simulation data instantly34.

### **The MultiMesh Synchronization Blocker (Issue \#105113)**

To render compute shader output directly to the screen without inducing massive CPU overhead, the standard paradigm utilizes a MultiMeshInstance3D. The developer extracts the underlying GPU buffer via RenderingServer.multimesh\_get\_buffer\_rd\_rid(RID) and binds that RID directly as a writeable Storage Buffer in the compute shader31.  
However, a verified and critical bug in recent Godot 4.x branches (Issue \#105113, duplicated by \#105100) prevents this from functioning autonomously35. Even when the compute shader successfully executes and writes valid transform data to the Vulkan buffer, the Godot RenderingServer fails to recognize the updated state35. The instances will not render unless the game code explicitly executes multimesh\_get\_buffer() followed by multimesh\_set\_buffer() on the CPU side every frame35.  
For a simulation of 1,000,000 particles, where each instance requires a 48-byte Transform3D and a 16-byte Color, the data payload is 64 MB. Forcing a GPU-to-CPU-to-GPU memory transfer of 64 MB every frame at 60 FPS consumes nearly 8 GB/s of PCIe bandwidth purely in synchronization overhead. This catastrophic bottleneck completely negates the performance bandwidth benefits of utilizing GPU compute, rendering this specific pipeline a **BLOCKER**35.  
To bypass this limitation, the AI agent must abandon MultiMeshInstance3D for the dense SPH fluid simulation. Instead, it must utilize raw drawing commands via RenderingDevice.draw\_list\_draw() or draw\_list\_draw\_indirect(), manually binding the compute output buffers to a custom render pipeline to execute instanced rendering directly on the GPU29.

### **Memory Leaks and Async Readback**

Unmanaged resources interacting with the RenderingDevice are a primary source of memory leaks in Godot C\#. Godot's Resource IDs (RIDs) generated by the rendering device (e.g., via storage\_buffer\_create) are low-level Vulkan handles. They are not tracked by the standard C\# Garbage Collector or Godot's internal RefCounted system37. They must be explicitly and manually freed using RenderingDevice.FreeRid() during the node's \_ExitTree or C\# Dispose phase to prevent GPU memory from hemorrhaging39.  
For reading minimal, highly aggregated data back to the CPU (e.g., querying the total momentum of a colliding asteroid field to apply a physics impulse to a player ship), Godot provides the buffer\_get\_data\_async method. This method accepts a C\# Callable (mapped via Callable.From(Action)) that executes asynchronously once the Vulkan memory barrier confirms the compute queue has finished, avoiding immediate main-thread CPU stalls29.

C\#  
// Minimal C\# RenderingDevice Async Readback Example  
using Godot;

public partial class ComputeManager : Node   
{  
    private RenderingDevice \_rd;  
    private Rid \_bufferRid;

    public override void \_Ready()   
    {  
        \_rd \= RenderingServer.GetRenderingDevice();  
        // Assume buffer is created and compute dispatched...  
          
        // Request async readback without stalling the CPU  
        \_rd.BufferGetDataAsync(\_bufferRid, Callable.From\<byte\[\]\>(OnComputeDataReady));  
    }

    private void OnComputeDataReady(byte\[\] data)   
    {  
        // Process minimal physics readback  
        GD.Print(\$"Async readback complete. Bytes read: {data.Length}");  
    }  
}

### **SPH and Advanced Algorithms**

For the complex SPH planet-on-planet collision algorithms, literature and community benchmarks confirm that a compute-based approach is viable, provided the spatial data structures are handled entirely on the GPU40. Achieving ![][image3] neighbor searches for SPH requires implementing spatial hashing and prefix sums (such as the Blelloch scan) via compute shaders41. Modern Gaussian splatting implementations in Godot (e.g., godot-gaussian-splatting) have successfully utilized radix sorts running entirely on the GPU43. Implementing these complex multi-pass algorithms requires strict manual management of compute pipeline barriers via compute\_list\_add\_barrier to prevent race conditions between the binning, sorting, and density evaluation passes29.  
For the simpler, non-fluid debris tier, relying on Godot's built-in GPUParticles3D is highly recommended. While they lack the fluid density solvers required for SPH, they support custom process shaders and natively handle collisions against engine-generated Signed Distance Fields (SDF) and heightfields. Utilizing GPUParticles3D bypasses the multimesh\_get\_buffer bottleneck entirely, providing a stable, performant fallback for standard visual effects.

| Claim/Feature | Godot Version | Source | Date |
| :---- | :---- | :---- | :---- |
| multimesh\_get\_buffer\_rd\_rid requires CPU sync | 4.4.1+ (Issue \#105113) | GitHub Godot Issues35 | Apr 2025 |
| std430 alignment pads vec3 arrays to 16 bytes | 4.x Vulkan | Godot Forums / Reddit32 | June 2024 |
| RIDs cause memory leaks if not explicitly freed | 4.x | GitHub Issue \#86926 / Docs37 | 2024 |
| buffer\_get\_data\_async accepts C\# Callables | 4.x | Godot Rust/C\# API Docs29 | N/A |
| GPU spatial hashing & prefix sum for SPH | General GPU | ResearchGate / arXiv41 | 2025/2026 |

## **5\. C\# / .NET in 4.8**

### **SDK Requirements and API Transitions**

Godot 4 relies inherently on the modern .NET ecosystem. While Godot 4.0 initially targeted .NET 621, development in late 2026 for a desktop Linux/Steam release must baseline on the .NET 8 SDK (or later Long-Term Support versions). This is necessary to utilize modern runtime performance optimizations and advanced Garbage Collector configurations required for heavy physics workloads on Linux.

### **The Interop Penalty and GC Stutter**

The primary friction point between C\# and the Godot RenderingDevice is memory marshaling. To update a buffer, the API RenderingDevice.BufferUpdate() requires a Godot PackedByteArray or a standard C\# byte\[\]29.  
Currently, the high-level Godot API does not directly expose zero-copy Vulkan mapped memory securely to C\# Span\<T\> or unmanaged pointers without dipping into custom GDExtension modules. Passing large arrays of simulation data from C\# to the GPU every frame involves creating or resizing a byte\[\], copying the structured physics data into it, and passing it to the engine, which executes an internal C++ memcpy before discarding the reference46.  
For a dense simulation, generating multi-megabyte byte\[\] objects every physics tick will rapidly saturate the Gen 0 Garbage Collector in .NET, resulting in aggressive GC spikes that manifest as severe micro-stutter during gameplay. To mitigate this, the C\# logic must aggressively pool arrays utilizing ArrayPool\<byte\>.Shared or pin memory utilizing GCHandle. However, the most effective mitigation strategy aligns with the architectural necessity discovered in Section 4: execute all continuous physics calculations directly on the GPU, interacting with the CPU only via asynchronous, lightweight readbacks, thereby eliminating massive per-frame CPU-to-GPU data transfers entirely.

| Claim/Feature | Godot Version | Source | Date |
| :---- | :---- | :---- | :---- |
| .NET 6 baseline scaling to modern SDKs | 4.x | Godot Blog (C\# in 4.0)21 | 2023 |
| buffer\_update uses byte\[\] incurring memory copies | 4.x | Reddit / Godot Docs29 | N/A |

## **6\. Tooling for an AI Agent that Cannot See the Editor**

For an AI coding agent (Claude Code) to autonomously engineer this simulation without visual editor access, the development pipeline must be aggressively script-driven, headless, and verifiable via automated CI methodologies.

### **Headless CLI and Visual Feedback**

Godot 4 inherently supports headless execution via the \--headless flag, which suppresses window generation during export or script execution47. However, testing visual outputs—such as verifying shader configurations or texture streaming LOD transitions—requires the engine to actually render frames.  
On a Fedora Linux environment, the agent can circumvent the headless limitation by utilizing xvfb-run (X virtual framebuffer) combined with Godot's \--write-movie argument49. This configuration tricks the engine into rendering fully composited frames into a headless virtual display, which are then piped into a video container (e.g., out.avi). The agent can subsequently analyze these frames (if equipped with vision capabilities) or output them as artifacts for human review. *CLI Invocation Example:* xvfb-run \-a godot \--path . \--fixed-fps 60 \--write-movie out.avi49.

### **Testing Frameworks: xUnit vs. GdUnit4**

Running standard dotnet test with pure xUnit inside a Godot project folder frequently fails. This occurs because C\# tests interacting with Godot API types cannot load the engine's internal unmanaged C++ bindings without the engine process hosting them51.  
The standard community solution is GdUnit4Net, which provides a custom test adapter designed to execute C\# tests safely within the Godot runtime51. The AI agent can execute these tests via the CLI using standard .NET commands prefixed with the engine binary: GODOT\_BIN=\$(which godot) dotnet test51.  
However, for the decoupled N-body C\# class library proposed in Section 3, standard xUnit is perfectly viable and highly recommended. As long as this specific physics library contains absolutely zero Godot namespace references, it can be tested rapidly using plain dotnet test without the overhead of booting the engine context.

### **Scene Generation: The Builder Pattern**

Allowing an AI agent to hand-edit .tscn files (Godot's text-based scene serialization format) is highly destructive. .tscn files rely on strictly managed uid:// string references and highly specific node hierarchy paths. If a generative AI hallucinates a UID or misaligns a bracket, the scene will fail to load, corrupting the project state50.  
Instead, the agent must be instructed to utilize a "Scene Builder Pattern"50. The AI writes a pure C\# or GDScript script that instantiates nodes programmatically (e.g., new Node3D()), assigns properties, and attaches resources dynamically. This builder script is run once in headless mode, utilizing PackedScene.Pack() to generate the .tscn safely on disk through the engine's internal serializers50. The generated .tscn and .import files are then committed to version control, while the temporary .godot/ directory is strictly ignored.

| Claim/Feature | Godot Version | Source | Date |
| :---- | :---- | :---- | :---- |
| xvfb-run and \--write-movie for headless capture | 4.x | SkillingMain, Tistory49 | N/A |
| Godogen scene builder script methodology | 4.x | Tistory50 | N/A |
| GdUnit4 test adapter CLI requirements | 4.x | GdUnit4 GitHub Issues51 | N/A |

## **7\. Decisions This Implies**

The research and architectural constraints outlined above necessitate several strict directives for the AI coding agent's workflow:

> 1. **Abandon Native Double-Precision Godot Builds:** The AI agent must not attempt to build a custom precision=double engine and GodotSharp assembly, as managing this via automated CLI scripts introduces fatal CI friction. The physics engine must be built as an isolated, pure C\# class library utilizing System.Double, which communicates coordinate transforms down to a single-precision Godot visual layer relying on a Floating Origin architecture.  
> 2. **Bypass MultiMesh for Compute Debris:** Due to Issue \#105113, the agent cannot bind a compute shader directly to a MultiMeshInstance3D without incurring a crippling CPU synchronization stall. The AI must be directed to write custom RenderingDevice.draw\_list\_draw() commands to render the Vulkan buffers directly to the screen, or fallback to GPUParticles3D for non-fluid debris.  
> 3. **Strict Buffer Alignment Directives:** The AI agent prompt must explicitly mandate that all C\# structures sent to the GPU are padded to match GLSL std430 alignment (e.g., all vec3 arrays must be padded to 16 bytes per element in C\#).  
> 4. **Adopt the Scene Builder Pattern:** Claude Code must never write or edit .tscn files directly. It must write C\# "Builder" scripts that programmatically construct the scene tree and serialize it via ResourceSaver in headless mode.  
> 5. **Array Pooling is Mandatory:** To circumvent C\# GC stutter when updating RenderingDevice buffers, the AI must implement ArrayPool\<byte\> for all necessary CPU-to-GPU data transfers, while striving to keep the SPH simulation loop entirely on the GPU.

## **8\. Unknowns to Test and Resolve**

Before committing to the final implementation, the AI agent must prototype and verify the following unknowns:

* **Custom Shader Streaming Integration:** While the documentation dictates using sampler2DStreamed in custom shaders for Texture Streaming, it is unknown how this interacts specifically with advanced multi-pass atmospheric scattering shaders (like Eric Bruneton's model) that sample heavily from precomputed LUTs. The agent must verify if streaming induces visual artifacting during the scattering integration pass.  
* **Vulkan DrawList Rendering Overhead:** Bypassing MultiMeshInstance3D and using raw RenderingDevice.draw\_list\_draw() avoids the CPU-sync bug, but the exact overhead, boilerplate complexity, and stability of maintaining the correct Vulkan pipeline state, vertex arrays, and view matrices manually from C\# requires immediate prototyping.  
* **Linux / NVIDIA Compute Limits:** The maximum storage buffer allocation sizes on the target RTX 3080 Ti running Fedora Linux must be queried via the RenderingDevice.limit\_get API to ensure the requested tens of millions of SPH particles do not exceed driver-level VRAM allocation boundaries per buffer.

#### **Works cited**

> 1. Releases · godotengine/godot-builds \- GitHub, [https://github.com/godotengine/godot-builds/releases](https://github.com/godotengine/godot-builds/releases)  
> 2. Godot 4.8 dev 6 lands with fixes as feature freeze approaches, [https://pixelwolf.net/news/story/godot-4-8-dev-6-snapshot](https://pixelwolf.net/news/story/godot-4-8-dev-6-snapshot)  
> 3. Download Godot 4.8 (dev6), [https://godotengine.org/download/archive/4.8-dev6/](https://godotengine.org/download/archive/4.8-dev6/)  
> 4. Dev snapshot: Godot 4.8 dev 6 – Godot Engine \- daily.dev, [https://daily.dev/posts/dev-snapshot-godot-4-8-dev-6-godot-engine-didpln506](https://daily.dev/posts/dev-snapshot-godot-4-8-dev-6-godot-engine-didpln506)  
> 5. Dev snapshot: Godot 4.8 dev 6, [https://godotengine.org/article/dev-snapshot-godot-4-8-dev-6/](https://godotengine.org/article/dev-snapshot-godot-4-8-dev-6/)  
> 6. Priorities \- Godot Engine, [https://godotengine.org/priorities/](https://godotengine.org/priorities/)  
> 7. VoxelGI is fixed in 4.5 beta2 and no one's talking about this \>:( : r/godot, [https://www.reddit.com/r/godot/comments/1lspos9/voxelgi\_is\_fixed\_in\_45\_beta2\_and\_no\_ones\_talking/](https://www.reddit.com/r/godot/comments/1lspos9/voxelgi_is_fixed_in_45_beta2_and_no_ones_talking/)  
> 8. Godot 4.8 Development Is In Full Swing \- 80 Level, [https://80.lv/articles/godot-4-8-development-is-in-full-swing](https://80.lv/articles/godot-4-8-development-is-in-full-swing)  
> 9. Dev snapshot: Godot 4.8 dev 6 \- Reddit, [https://www.reddit.com/r/godot/comments/1whb4yv/dev\_snapshot\_godot\_48\_dev\_6/](https://www.reddit.com/r/godot/comments/1whb4yv/dev_snapshot_godot_48_dev_6/)  
> 10. Godot 4.8: Texture Streaming Finally Arrives\! \- YouTube, [https://www.youtube.com/watch?v=wClWgdIr6Vo](https://www.youtube.com/watch?v=wClWgdIr6Vo)  
> 11. Godot 4.8 Dev5 Released \- GameFromScratch.com, [https://gamefromscratch.com/godot-4-8-dev5-released/](https://gamefromscratch.com/godot-4-8-dev5-released/)  
> 12. Dev snapshot: Godot 4.8 dev 5, [https://godotengine.org/article/dev-snapshot-godot-4-8-dev-5/](https://godotengine.org/article/dev-snapshot-godot-4-8-dev-5/)  
> 13. Godot 4.8 dev 5: Mip-Level Texture Streaming Explained \- 3DxDEV, [https://3dxdev.com/godot-4-8-dev-5-adds-mip-level-texture-streaming-before-feature-freeze/](https://3dxdev.com/godot-4-8-dev-5-adds-mip-level-texture-streaming-before-feature-freeze/)  
> 14. Texture streaming is finally going to get merged. : r/godot \- Reddit, [https://www.reddit.com/r/godot/comments/1vzp4aw/texture\_streaming\_is\_finally\_going\_to\_get\_merged/](https://www.reddit.com/r/godot/comments/1vzp4aw/texture_streaming_is_finally_going_to_get_merged/)  
> 15. StreamedTexture2D — Godot Engine (4.7) documentation in English, [https://docs.godotengine.org/en/4.7/classes/class\_streamedtexture2d.html](https://docs.godotengine.org/en/4.7/classes/class_streamedtexture2d.html)  
> 16. Compiling with .NET — Godot Engine (4.4) documentation in English, [https://docs.godotengine.org/en/4.4/contributing/development/compiling/compiling\_with\_dotnet.html](https://docs.godotengine.org/en/4.4/contributing/development/compiling/compiling_with_dotnet.html)  
> 17. ResourceImporterLayeredTexture \- Godot Docs, [https://docs.godotengine.org/en/latest/classes/class\_resourceimporterlayeredtexture.html](https://docs.godotengine.org/en/latest/classes/class_resourceimporterlayeredtexture.html)  
> 18. GDScriptTextDocument \- Godot Docs, [https://docs.godotengine.org/en/latest/classes/class\_gdscripttextdocument.html](https://docs.godotengine.org/en/latest/classes/class_gdscripttextdocument.html)  
> 19. Add support for texture streaming · Issue \#3177 · godotengine/godot, [https://github.com/godotengine/godot-proposals/issues/3177](https://github.com/godotengine/godot-proposals/issues/3177)  
> 20. PKGBUILD \- aur.git \- AUR Package Repositories \- Arch Linux, [https://aur.archlinux.org/cgit/aur.git/tree/PKGBUILD?h=godot-double](https://aur.archlinux.org/cgit/aur.git/tree/PKGBUILD?h=godot-double)  
> 21. What's new in C\# for Godot 4.0, [https://godotengine.org/article/whats-new-in-csharp-for-godot-4-0/](https://godotengine.org/article/whats-new-in-csharp-for-godot-4-0/)  
> 22. Expose a real\_t type definition in C\# · Issue \#8628 \- GitHub, [https://github.com/godotengine/godot-proposals/issues/8628](https://github.com/godotengine/godot-proposals/issues/8628)  
> 23. Double Precision with CSharp? \#90184 \- godotengine/godot \- GitHub, [https://github.com/godotengine/godot/issues/90184](https://github.com/godotengine/godot/issues/90184)  
> 24. Does Vector3 become typed as double in C\# with precision=double, [https://forum.godotengine.org/t/does-vector3-become-typed-as-double-in-c-with-precision-double-build/54442](https://forum.godotengine.org/t/does-vector3-become-typed-as-double-in-c-with-precision-double-build/54442)  
> 25. Precomputed Atmospheric Scattering: a New Implementation, [https://ebruneton.github.io/precomputed\_atmospheric\_scattering/](https://ebruneton.github.io/precomputed_atmospheric_scattering/)  
> 26. Followup to Atmospheric Scattering – Part 1: Overview, [http://www.thetenthplanet.de/archives/4519](http://www.thetenthplanet.de/archives/4519)  
> 27. Precomputed Atmospheric Scattering \- Urho3D, [https://discourse-urho3d.github.io/t/precomputed-atmospheric-scattering/2897/](https://discourse-urho3d.github.io/t/precomputed-atmospheric-scattering/2897/)  
> 28. Precomputed atmospheric scattering : r/godot \- Reddit, [https://www.reddit.com/r/godot/comments/o3s6ol/precomputed\_atmospheric\_scattering/](https://www.reddit.com/r/godot/comments/o3s6ol/precomputed_atmospheric_scattering/)  
> 29. RenderingDevice — Godot Engine (stable) documentation in English, [https://docs.godotengine.org/en/stable/classes/class\_renderingdevice.html](https://docs.godotengine.org/en/stable/classes/class_renderingdevice.html)  
> 30. godot-docs/classes/class\_renderingdevice.rst at master \- GitHub, [https://github.com/godotengine/godot-docs/blob/master/classes/class\_renderingdevice.rst](https://github.com/godotengine/godot-docs/blob/master/classes/class_renderingdevice.rst)  
> 31. The Godot Barn \~ Articles / A glance at Compute Shaders, [https://thegodotbarn.com/contributions/article/219/a-glance-at-compute-shaders](https://thegodotbarn.com/contributions/article/219/a-glance-at-compute-shaders)  
> 32. Help with compute shaders. Using structs in a buffer that contain, [https://godotforums.org/d/40463-help-with-compute-shaders-using-structs-in-a-buffer-that-contain-arrays](https://godotforums.org/d/40463-help-with-compute-shaders-using-structs-in-a-buffer-that-contain-arrays)  
> 33. \[Help\] Compute shaders: std430 and alignment : r/computergraphics, [https://www.reddit.com/r/computergraphics/comments/1ok6i3q/help\_compute\_shaders\_std430\_and\_alignment/](https://www.reddit.com/r/computergraphics/comments/1ok6i3q/help_compute_shaders_std430_and_alignment/)  
> 34. \[Help\] Compute shaders: std430 and alignment : r/godot \- Reddit, [https://www.reddit.com/r/godot/comments/1ok6lol/help\_compute\_shaders\_std430\_and\_alignment/](https://www.reddit.com/r/godot/comments/1ok6lol/help_compute_shaders_std430_and_alignment/)  
> 35. \`multimesh\_get\_buffer\_rd\_rid()\` GPU writes require ... \- GitHub, [https://github.com/godotengine/godot/issues/105113](https://github.com/godotengine/godot/issues/105113)  
> 36. RenderingServer — Godot Engine (stable) documentation in English, [https://docs.godotengine.org/en/stable/classes/class\_renderingserver.html](https://docs.godotengine.org/en/stable/classes/class_renderingserver.html)  
> 37. RenderingDevice — Godot Engine (4.4) documentation in English, [https://docs.godotengine.org/en/4.4/classes/class\_renderingdevice.html](https://docs.godotengine.org/en/4.4/classes/class_renderingdevice.html)  
> 38. RenderingDevice in godot::classes \- Rust, [https://godot-rust.github.io/docs/gdext/master/godot/classes/struct.RenderingDevice.html](https://godot-rust.github.io/docs/gdext/master/godot/classes/struct.RenderingDevice.html)  
> 39. C\# .Dispose() should free the object from memory \#86926 \- GitHub, [https://github.com/godotengine/godot/issues/86926](https://github.com/godotengine/godot/issues/86926)  
> 40. A Real-Time Bidirectional Haptic Interaction Framework for Coupled, [https://arxiv.org/html/2511.15908v1](https://arxiv.org/html/2511.15908v1)  
> 41. (PDF) Moving Towards Large-Scale Particle Based Fluid Simulation, [https://www.researchgate.net/publication/395296151\_Moving\_Towards\_Large-Scale\_Particle\_Based\_Fluid\_Simulation\_in\_Unity\_3D](https://www.researchgate.net/publication/395296151_Moving_Towards_Large-Scale_Particle_Based_Fluid_Simulation_in_Unity_3D)  
> 42. 3D SPH Fluid Simulation \- Godot Asset Library, [https://godotengine.org/asset-library/asset/5116](https://godotengine.org/asset-library/asset/5116)  
> 43. ReconWorldLab/godot-gaussian-splatting: \[GDGS\] A real ... \- GitHub, [https://github.com/ReconWorldLab/godot-gaussian-splatting](https://github.com/ReconWorldLab/godot-gaussian-splatting)  
> 44. haztro/godot-gaussian-splatting \- GitHub, [https://github.com/haztro/godot-gaussian-splatting](https://github.com/haztro/godot-gaussian-splatting)  
> 45. Enabling AI-Controlled additive manufacturing with real-time, [https://www.tandfonline.com/doi/full/10.1080/17452759.2025.2610146](https://www.tandfonline.com/doi/full/10.1080/17452759.2025.2610146)  
> 46. Godot language binding system explained by one of the lead, [https://www.reddit.com/r/godot/comments/16oigg1/godot\_language\_binding\_system\_explained\_by\_one\_of/](https://www.reddit.com/r/godot/comments/16oigg1/godot_language_binding_system_explained_by_one_of/)  
> 47. Command line tutorial \- Godot Docs, [https://docs.godotengine.org/en/stable/tutorials/editor/command\_line\_tutorial.html](https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html)  
> 48. Command line tutorial — Godot Engine latest documentation \- Huihoo, [https://docs.huihoo.com/godotengine/godot-docs/godot/reference/command\_line\_tutorial.html](https://docs.huihoo.com/godotengine/godot-docs/godot/reference/command_line_tutorial.html)  
> 49. Setup guide · SkillingMain, [https://skillingmain.com/skills/godot-visual-debugger/setup](https://skillingmain.com/skills/godot-visual-debugger/setup)  
> 50. Godogen: 프롬프트 한줄로 게임 전부를 만들다 \- 오늘도 공부 \- 티스토리, [https://javaexpert.tistory.com/1643](https://javaexpert.tistory.com/1643)  
> 51. GD-166: gdUnit4 in a setup with multiple test projects causes, [https://github.com/godot-gdunit-labs/gdUnit4Net/issues/166](https://github.com/godot-gdunit-labs/gdUnit4Net/issues/166)  
> 52. Unit testing for C\# in Godot 4 \- Reddit, [https://www.reddit.com/r/godot/comments/16ipfhc/unit\_testing\_for\_c\_in\_godot\_4/](https://www.reddit.com/r/godot/comments/16ipfhc/unit_testing_for_c_in_godot_4/)

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAoAAAAaCAYAAACO5M0mAAAAXElEQVR4XmNgGLqAHV0AGXACcT8Q/wfiLDQ5OLgGxIeBOJCBgEJkMFQUZqMLYgMghTnogtgASGEuuiA2AFKYhy6IDYAUFqALogNeBojCWnQJGFjOAFGADY8CKgIAEYUaR+wC4MEAAAAASUVORK5CYII=>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEcAAAAZCAYAAABjNDOYAAABv0lEQVR4Xu2XQStEURTHj+yEhbJga0NhIVlRPgFb2ZM0pKwtfAA2PoGVna9gZSWJUhQ1pSzYKRSJc3rzuPOfc+be+3pjFnN/9a+5v/Peu3fOzNzbECU6jl7OGucbC8wTZ4BzwpmGWkcwz+mixuZcc3qcMdY7CnzzvrGXVc46yn9mmJov/I2zz1mi7Lrl+vIv+AzfWOWQ80XZxZJ2NGeQ80B/a7AW/snZAyfXyv0IPkMba/eZtKs5LlZz+kn34i5QUuO1vrGX2OYsolBwN8EQrOYckO6fSffofGMvsc0Z4rygdHhFEYDVnFvSvZxCmkd3zJmrvZbT7N2pBRHbHGGMUwWnHaWhWM35IN2fU73P78+z69TyzVx7jpcizREmKPtkcwpNXiO2OWek+9KRSSooA5kh+yseg9Uc2XQ1f0m6Lx2ZZANlIN2U3V8FH4vVnCPS/R3pvnRkkk2UAbh7zCjn3qnFYjVnknQvTn5yLUcm2kIZAC5a9qAbcKFYzRHEzypO/mi2lD7KJtrBggfrjUxxrlAG0Kw5K1RfK3zyhJL/lrX4OEUBLHDGURrg3NYaRig7vh8521BLJBKJRCKRSHj4AUTMkzgxMUMyAAAAAElFTkSuQmCC>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAC0AAAAaCAYAAAAjZdWPAAACA0lEQVR4Xu2XMUhcQRCGBxSNEgkYixAlhaBNggqKTRpJGi0sFQtBSGehYJpEW0HEwkLRSqsELFIkkCKCYm1jJdpIiI2tRMTCQnR+Z/dYf/a9u3c+BeE+GN7dN3Ozb/ft7XEiFZ4uxyxKZEHjLcvHYE+jmWUGzjXqWJZKu8a1i0WNdff6d1hEfNX4xbIMME5msFr44AtOiPlTlo6yBouwrzHNMg0MfMYyYEys5h35z2KD5UG1ZFiAQyle7Bt+Iw/XT+4+oF8PS6ZPrHCHPPNcrO6EfNJkX4rlcDKAKo2fYk9z1hdFQP4HS+ZKrHktJ4gRsbrvgetwLob3uK5qbFPuS/A+ZFOSexZAQdEisRVG3WDg/EQYHH2T7nXS0/lDzrMs8Z4F0LzUm47VTUQcaHLXTrE8tkYI3CdyHkw21rNAjVhB2qkB5sXqhskPOZ8E9jDnRyMuZEbS87fEVjAEv1LI/+OE0iXpn0WOz/ZL50FjmHCsSXrPW47EimJfRH9iHHDC8UzSB0AOW4id3xo4BBh8YfEjV5S/Ys1eBW7FOR6UQU0DS6Vb4hPybk7jfZhwID/FMokWjSWN/2KPqPduOhE8btwA80Fjl6UyLrbCA5xwxCaaO22S30DYNsUOhdzAyr1hWQaYfCvLh+K1xgXLjHzU2GD50OCfxxbLEqkXOwwqVKjwFLgBT8V/HBSS7d8AAAAASUVORK5CYII=>