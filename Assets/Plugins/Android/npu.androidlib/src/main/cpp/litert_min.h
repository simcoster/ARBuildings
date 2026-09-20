// Minimal LiteRT 2.1.0 C ABI used by npu_gl. Layouts match the public headers.
#pragma once

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
  kLiteRtStatusOk = 0,
  kLiteRtStatusErrorInvalidArgument = 1,
  kLiteRtStatusErrorMemoryAllocationFailure = 2,
  kLiteRtStatusErrorRuntimeFailure = 3,
  kLiteRtStatusErrorMissingInputTensor = 4,
  kLiteRtStatusErrorUnsupported = 5,
  kLiteRtStatusErrorNotFound = 6,
  kLiteRtStatusErrorTimeoutExpired = 7,
  kLiteRtStatusErrorWrongVersion = 8,
  kLiteRtStatusErrorUnknown = 9,
} LiteRtStatus;

typedef enum {
  kLiteRtHwAcceleratorNone = 0,
  kLiteRtHwAcceleratorCpu = 1 << 0,
  kLiteRtHwAcceleratorGpu = 1 << 1,
  kLiteRtHwAcceleratorNpu = 1 << 2,
} LiteRtHwAccelerators;
typedef int LiteRtHwAcceleratorSet;
typedef size_t LiteRtParamIndex;

typedef struct LiteRtEnvironmentT* LiteRtEnvironment;
typedef struct LiteRtModelT* LiteRtModel;
typedef struct LiteRtCompiledModelT* LiteRtCompiledModel;
typedef struct LiteRtTensorBufferT* LiteRtTensorBuffer;
typedef struct LiteRtEventT* LiteRtEvent;
typedef struct LiteRtOptionsT* LiteRtOptions;

typedef enum {
  kLiteRtAnyTypeNone = 0,
  kLiteRtAnyTypeBool = 1,
  kLiteRtAnyTypeInt = 2,
  kLiteRtAnyTypeReal = 3,
  kLiteRtAnyTypeString = 8,
  kLiteRtAnyTypeVoidPtr = 9,
} LiteRtAnyType;

typedef struct {
  LiteRtAnyType type;
  union {
    bool bool_value;
    int64_t int_value;
    double real_value;
    const char* str_value;
    const void* ptr_value;
  };
} LiteRtAny;

typedef enum {
  kLiteRtEnvOptionTagCompilerPluginLibraryDir = 0,
  kLiteRtEnvOptionTagDispatchLibraryDir = 1,
  kLiteRtEnvOptionTagOpenClDeviceId = 2,
  kLiteRtEnvOptionTagOpenClPlatformId = 3,
  kLiteRtEnvOptionTagOpenClContext = 4,
  kLiteRtEnvOptionTagOpenClCommandQueue = 5,
  kLiteRtEnvOptionTagEglDisplay = 6,
  kLiteRtEnvOptionTagEglContext = 7,
} LiteRtEnvOptionTag;

typedef struct {
  LiteRtEnvOptionTag tag;
  LiteRtAny value;
} LiteRtEnvOption;

#define LITERT_TENSOR_MAX_RANK 8

typedef struct {
  unsigned int rank : 7;
  bool has_strides : 1;
  int32_t dimensions[LITERT_TENSOR_MAX_RANK];
  uint32_t strides[LITERT_TENSOR_MAX_RANK];
} LiteRtLayout;

typedef enum {
  kLiteRtElementTypeFloat32 = 1,
} LiteRtElementType;

typedef struct {
  LiteRtElementType element_type;
  LiteRtLayout layout;
} LiteRtRankedTensorType;

typedef uint32_t LiteRtGLenum;
typedef uint32_t LiteRtGLuint;
typedef void (*LiteRtGlBufferDeallocator)(void*);

LiteRtStatus LiteRtCreateEnvironment(int num_options, const LiteRtEnvOption* options,
                                     LiteRtEnvironment* environment);
void LiteRtDestroyEnvironment(LiteRtEnvironment environment);

LiteRtStatus LiteRtCreateOptions(LiteRtOptions* options);
void LiteRtDestroyOptions(LiteRtOptions options);
LiteRtStatus LiteRtSetOptionsHardwareAccelerators(LiteRtOptions options,
                                                 LiteRtHwAcceleratorSet hardware_accelerators);

LiteRtStatus LiteRtCreateModelFromFile(const char* filename, LiteRtModel* model);
void LiteRtDestroyModel(LiteRtModel model);

LiteRtStatus LiteRtCreateCompiledModel(LiteRtEnvironment environment, LiteRtModel model,
                                       LiteRtOptions compilation_options,
                                       LiteRtCompiledModel* compiled_model);
void LiteRtDestroyCompiledModel(LiteRtCompiledModel compiled_model);

LiteRtStatus LiteRtRunCompiledModel(LiteRtCompiledModel compiled_model,
                                    LiteRtParamIndex signature_index,
                                    size_t num_input_buffers, LiteRtTensorBuffer* input_buffers,
                                    size_t num_output_buffers, LiteRtTensorBuffer* output_buffers);

LiteRtStatus LiteRtCreateTensorBufferFromGlBuffer(
    LiteRtEnvironment env, const LiteRtRankedTensorType* tensor_type, LiteRtGLenum target,
    LiteRtGLuint id, size_t size_bytes, size_t offset, LiteRtGlBufferDeallocator deallocator,
    LiteRtTensorBuffer* buffer);

void LiteRtDestroyTensorBuffer(LiteRtTensorBuffer buffer);

LiteRtStatus LiteRtCreateEventFromEglSyncFence(LiteRtEnvironment env, void* egl_sync,
                                               LiteRtEvent* event);
LiteRtStatus LiteRtSetTensorBufferEvent(LiteRtTensorBuffer tensor_buffer, LiteRtEvent event);
LiteRtStatus LiteRtClearTensorBufferEvent(LiteRtTensorBuffer tensor_buffer);
void LiteRtDestroyEvent(LiteRtEvent event);

#ifdef __cplusplus
}
#endif
