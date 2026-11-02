# CUDA 13 driver and GPU minimums for sherpa-onnx's CUDA 13 build

Research note for YAAT-1602 (STT settings in the YAAT desktop client), 2026-11-01.

## Answer

1. **Driver.** CUDA 13.0 GA needs Linux driver >= 580.65.06; NVIDIA's table lists no Windows figure ("N/A"), because the CUDA 13.0 toolkit no longer bundles a Windows driver. Under minor-version compatibility, every CUDA 13.x application runs on a driver >= 580 (the R580 branch) on both OSes, with no upper bound. A practical check is "driver major version >= 580", or equivalently `cuDriverGetVersion` >= 13000.
2. **GPU.** CUDA 13.0 removed Maxwell, Pascal and Volta: compute capability below 7.5 (Turing) is unsupported. Require compute capability >= 7.5.
3. **sherpa-onnx build.** The current release (v2.0.1) ships `sherpa-onnx-v2.0.1-cuda-13.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2` (480,541,871 bytes) and `...-linux-x64-gpu.tar.bz2` (254,696,132 bytes): onnxruntime 1.28.2 built against CUDA 13.0 and cuDNN 9.x. The Windows archive bundles only the onnxruntime DLLs (`onnxruntime.dll`, `onnxruntime_providers_cuda.dll`, `_shared`, `_tensorrt`) and the sherpa-onnx C/C++ API DLLs; it contains no cuDNN, cuBLAS or cudart. The CUDA 13 runtime libraries and cuDNN 9 must come from the system (or be shipped by YAAT separately).
4. **Detection without the toolkit.** Both libraries ship with the driver. Load `nvcuda.dll` (Windows, System32) / `libcuda.so.1` (Linux), call `cuInit(0)`, `cuDriverGetVersion` (returns `1000*major + 10*minor` of the newest CUDA the driver supports; >= 13000 means CUDA 13 capable), and `cuDeviceGetAttribute` with `CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR`/`_MINOR` per device. Alternatively NVML (`nvml.dll` in `\Windows\System32` for DCH drivers or `%ProgramW6432%\NVIDIA Corporation\NVSMI\` for standard installs, `libnvidia-ml.so` on Linux): `nvmlSystemGetDriverVersion` gives the driver string (e.g. "580.88"), `nvmlDeviceGetCudaComputeCapability` the capability. A missing library means no NVIDIA driver.

## Findings

### 1. Driver minimums (NVIDIA CUDA Toolkit release notes)

Current release notes, https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html :

> Table 2 CUDA Toolkit and Corresponding Driver Branch ... CUDA 13.4 R615 CUDA 13.3 R610 CUDA 13.2 R595 CUDA 13.1 R590 CUDA 13.0 R580

> Note Existing CUDA 13.x applications run on drivers >=580 under CUDA minor version compatibility. CUDA 13.4 new features and newly enabled platforms require an R615 or later driver that supports them.

> Table 3 CUDA Toolkit and Minimum Required Driver Version for CUDA Minor Version Compatibility CTK Version Driver Range for Minor Version Compatibility Min Max 13.x >= 580 N/A 12.x >= 525 < 580 11.x >= 450 < 525

CUDA 13.0.0 release notes, https://docs.nvidia.com/cuda/archive/13.0.0/cuda-toolkit-release-notes/index.html :

> Table 3 CUDA Toolkit and Corresponding Driver Versions CUDA Toolkit Toolkit Driver Version Linux x86_64 Driver Version Windows x86_64 Driver Version CUDA 13.0 GA >=580.65.06 N/A

> ** Starting with CUDA 13.0, the Windows display driver is no longer bundled with the CUDA Toolkit package. Users must download and install the appropriate NVIDIA driver separately from the official driver download page.

> NVIDIA Linux Driver 580.65.06 x86_64, arm64-sbsa Linux

No exact Windows build number for CUDA 13.0 appears in the primary source; the R580 branch (>= 580) is the documented minimum.

### 2. Compute capability (same release notes)

https://docs.nvidia.com/cuda/cuda-toolkit-release-notes/index.html , cuFFT deprecations:

> Removed support for Maxwell, Pascal, and Volta GPUs, corresponding to compute capabilities earlier than Turing.

cuSPARSE deprecations:

> Dropped support for pre-Turing architectures (Maxwell, Volta, and Pascal).

Turing is compute capability 7.5, so the minimum is 7.5 (Volta 7.0/7.2 and older are out).

### 3. onnxruntime and sherpa-onnx

onnxruntime CUDA EP requirements, https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html :

> ONNX Runtime built with CUDA 13.0 require CUDA 13.0 or newer. ONNX Runtime built with cuDNN 8.x is not compatible with cuDNN 9.x, and vice versa.

> CUDA 13.x ONNX Runtime CUDA cuDNN Notes 1.30.x 13.0 9.x ... 1.28.x 13.0 9.x Available in PyPI and NuGet. Compatible with PyTorch for CUDA 13.x. 1.27.x 13.0 9.x

> To reduce the need for manual installations of CUDA and cuDNN, and ensure seamless integration between ONNX Runtime and PyTorch, the onnxruntime-gpu Python package offers API to load CUDA and cuDNN dynamic link libraries (DLLs) appropriately.

(That preload API is Python-only; a native consumer must have CUDA and cuDNN on the loader path.)

sherpa-onnx release assets (`gh api repos/k2-fsa/sherpa-onnx/releases/tags/v2.0.1`), https://github.com/k2-fsa/sherpa-onnx/releases/tag/v2.0.1 :

- `sherpa-onnx-v2.0.1-cuda-13.x-cudnn-9.x-onnxruntime1.28.2-linux-x64-gpu.tar.bz2` (254696132 bytes)
- `sherpa-onnx-v2.0.1-cuda-13.x-cudnn-9.x-onnxruntime1.28.2-win-x64-cuda.tar.bz2` (480541871 bytes)

Earlier releases also shipped `cuda-13.x-cudnn-9.x-onnxruntime1.27.1` variants, and CUDA 12 builds sit beside each (`cuda-12.x-cudnn-9.x-onnxruntime1.28.2-...`).

Build matrix, https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/master/.github/scripts/libs/windows_x64_cuda.py :

```python
return f"https://github.com/microsoft/onnxruntime/releases/download/v{self.onnxruntime_version}/{self.onnxruntime_dir_name}.zip"
...
onnxruntime_configs = [
    OnnxruntimeConfig("1.28.2", "12"),
    OnnxruntimeConfig("1.28.2", "13"),
]
```

The workflow, https://raw.githubusercontent.com/k2-fsa/sherpa-onnx/master/.github/workflows/windows-x64-cuda.yaml , packages only the install tree (`cp -a build/install/bin $dst/`, `lib`, `include`); it copies no CUDA or cuDNN libraries.

DLLs in the downloaded Windows CUDA 13 archive (listed with `tar tjvf`): `bin/` and `lib/` each hold `onnxruntime.dll`, `onnxruntime_providers_cuda.dll` (267,632,952 bytes), `onnxruntime_providers_shared.dll`, `onnxruntime_providers_tensorrt.dll`; `lib/` also `sherpa-onnx-c-api.dll`, `sherpa-onnx-cxx-api.dll`. No `cudnn*`, `cublas*`, `cudart*`, `cufft*` or `curand*` file is present: cuDNN 9 and the CUDA 13 runtime are not bundled. The Linux archive was not unpacked; it is built the same way.

### 4. Detecting driver and capability without the toolkit

CUDA Driver API, version management, https://docs.nvidia.com/cuda/cuda-driver-api/group__CUDA__VERSION.html :

> CUresult cuDriverGetVersion ( int * driverVersion ) Returns the latest CUDA version supported by driver. Returns in *driverVersion the version of CUDA supported by the driver. The version is returned as (1000 * major + 10 * minor). For example, CUDA 9.2 would be represented by 9020.

Device management, https://docs.nvidia.com/cuda/cuda-driver-api/group__CUDA__DEVICE.html :

> CUresult cuDeviceGetAttribute (int *pi, CUdevice_attribute attrib, CUdevice dev) Returns information about the device.

Types, https://docs.nvidia.com/cuda/cuda-driver-api/group__CUDA__TYPES.html :

> CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MAJOR Major compute capability version number.

(and `CU_DEVICE_ATTRIBUTE_COMPUTE_CAPABILITY_MINOR`, the minor number; enum values 75 and 76 in `cuda.h`, not re-verified on the page.) `cuDriverGetVersion` works before `cuInit`; device queries need `cuInit(0)` and `cuDeviceGetCount`/`cuDeviceGet` first.

NVML library locations, https://docs.nvidia.com/deploy/nvml-api/nvml-api-reference.html :

> The NVML library can be found at the following locations on Windows: Standard driver install: %ProgramW6432%\"NVIDIA Corporation"\NVSMI\ DCH driver install: \Windows\System32 Note that these libraries will not be added to the path on Windows. ... To dynamically load NVML, call LoadLibrary with this path. On Linux, the NVML library is named " libnvidia-ml.so " and can be found on the standard library path.

NVML system queries, https://docs.nvidia.com/deploy/nvml-api/group__nvmlSystemQueries.html :

> nvmlReturn_t nvmlSystemGetDriverVersion ( char* version, unsigned int length )

> nvmlReturn_t nvmlSystemGetCudaDriverVersion_v2 ( int* cudaDriverVersion ) ... NVML_ERROR_LIBRARY_NOT_FOUND if libcuda.so.1 or libcuda.dll is not found ... The returned CUDA driver version by calling cuDriverGetVersion()

NVML device queries, https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceQueries.html :

> nvmlReturn_t nvmlDeviceGetCudaComputeCapability ( nvmlDevice_t device, int* major, int* minor )

NVML needs `nvmlInit_v2` first and `nvmlShutdown` after. The NVML page also says: "NVML also has limited support for newer non-data-center GPUs (for example, NVIDIA GeForce or NVIDIA RTX GPUs)"; the driver API path (`nvcuda.dll` / `libcuda.so.1`) is the more direct check for consumer cards.
