// GPU-to-GPU LiteRT: wrap Unity GLES buffers, run CompiledModel on a worker
// that shares Unity's EGL context. Compile never happens on the render thread
// (that is the S24 hang from GpuDelegate).
#include "litert_min.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES3/gl31.h>
#include <android/log.h>
#include <jni.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>

#ifndef EGL_NO_SYNC_KHR
#define EGL_NO_SYNC_KHR ((EGLSyncKHR)0)
#endif
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "NpuGl", __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "NpuGl", __VA_ARGS__)

#define EXPORT __attribute__((visibility("default")))

namespace {

constexpr GLenum kSsbo = 0x90D2; // GL_SHADER_STORAGE_BUFFER
constexpr int kEventCapture = 1;
constexpr int kEventPack = 2;
constexpr int kEventUnpack = 3;

std::mutex g_mu;
char g_err[512] = "not init";
std::atomic<bool> g_egl_ready{false};
std::atomic<bool> g_loaded{false};
std::atomic<bool> g_bound{false};
std::atomic<float> g_run_ms{-1.f};

EGLDisplay g_dpy = EGL_NO_DISPLAY;
EGLContext g_unity = EGL_NO_CONTEXT;
EGLContext g_share = EGL_NO_CONTEXT;
EGLSurface g_pbuffer = EGL_NO_SURFACE;
EGLConfig g_cfg = nullptr;

EGLSyncKHR g_blit_sync = EGL_NO_SYNC_KHR;
EGLSyncKHR g_out_sync = EGL_NO_SYNC_KHR;

GLuint g_in_ssbo = 0;
GLuint g_out_ssbo = 0;
GLuint g_pack_prog = 0;
GLuint g_unpack_prog = 0;
std::atomic<GLuint> g_rgb_tex{0};
std::atomic<GLuint> g_matte_tex{0};
std::atomic<int> g_size{1024};

LiteRtEnvironment g_env = nullptr;
LiteRtModel g_model = nullptr;
LiteRtOptions g_opts = nullptr;
LiteRtCompiledModel g_compiled = nullptr;
LiteRtTensorBuffer g_in_tb = nullptr;
LiteRtTensorBuffer g_out_tb = nullptr;
std::string g_lib_dir;
std::string g_model_path;

PFNEGLCREATESYNCKHRPROC pCreateSync = nullptr;
PFNEGLDESTROYSYNCKHRPROC pDestroySync = nullptr;
PFNEGLCLIENTWAITSYNCKHRPROC pClientWait = nullptr;
PFNEGLWAITSYNCKHRPROC pWaitSync = nullptr;

void SetErr(const char* s) {
  std::snprintf(g_err, sizeof(g_err), "%s", s ? s : "");
  LOGE("%s", g_err);
}

void SetErrf(const char* fmt, LiteRtStatus st) {
  std::snprintf(g_err, sizeof(g_err), "%s status=%d", fmt, (int)st);
  LOGE("%s", g_err);
}

const char* kPackSrc = R"(#version 310 es
layout(local_size_x = 8, local_size_y = 8) in;
uniform sampler2D uRgb;
uniform int uSize;
layout(std430, binding = 0) buffer InBuf { float data[]; };
void main() {
  ivec2 p = ivec2(gl_GlobalInvocationID.xy);
  if (p.x >= uSize || p.y >= uSize) return;
  vec3 c = texelFetch(uRgb, p, 0).rgb;
  int i = p.y * uSize + p.x;
  int plane = uSize * uSize;
  data[i] = c.r;
  data[plane + i] = c.g;
  data[2 * plane + i] = c.b;
}
)";

const char* kUnpackSrc = R"(#version 310 es
layout(local_size_x = 8, local_size_y = 8) in;
uniform int uSize;
layout(std430, binding = 0) buffer OutBuf { float data[]; };
layout(r32f, binding = 1) uniform writeonly image2D uMatte;
void main() {
  ivec2 p = ivec2(gl_GlobalInvocationID.xy);
  if (p.x >= uSize || p.y >= uSize) return;
  int i = p.y * uSize + p.x;
  imageStore(uMatte, p, vec4(data[i], 0.0, 0.0, 1.0));
}
)";

GLuint CompileCompute(const char* src, const char* name) {
  GLuint sh = glCreateShader(GL_COMPUTE_SHADER);
  glShaderSource(sh, 1, &src, nullptr);
  glCompileShader(sh);
  GLint ok = 0;
  glGetShaderiv(sh, GL_COMPILE_STATUS, &ok);
  if (!ok) {
    char log[512];
    glGetShaderInfoLog(sh, sizeof(log), nullptr, log);
    std::snprintf(g_err, sizeof(g_err), "compile %s: %s", name, log);
    LOGE("%s", g_err);
    glDeleteShader(sh);
    return 0;
  }
  GLuint prog = glCreateProgram();
  glAttachShader(prog, sh);
  glLinkProgram(prog);
  glDeleteShader(sh);
  glGetProgramiv(prog, GL_LINK_STATUS, &ok);
  if (!ok) {
    char log[512];
    glGetProgramInfoLog(prog, sizeof(log), nullptr, log);
    std::snprintf(g_err, sizeof(g_err), "link %s: %s", name, log);
    LOGE("%s", g_err);
    glDeleteProgram(prog);
    return 0;
  }
  return prog;
}

void DestroySync(EGLSyncKHR* s) {
  if (*s && *s != EGL_NO_SYNC_KHR && pDestroySync && g_dpy != EGL_NO_DISPLAY)
    pDestroySync(g_dpy, *s);
  *s = EGL_NO_SYNC_KHR;
}

bool LoadEglKhr() {
  pCreateSync = (PFNEGLCREATESYNCKHRPROC)eglGetProcAddress("eglCreateSyncKHR");
  pDestroySync = (PFNEGLDESTROYSYNCKHRPROC)eglGetProcAddress("eglDestroySyncKHR");
  pClientWait = (PFNEGLCLIENTWAITSYNCKHRPROC)eglGetProcAddress("eglClientWaitSyncKHR");
  pWaitSync = (PFNEGLWAITSYNCKHRPROC)eglGetProcAddress("eglWaitSyncKHR");
  if (!pCreateSync || !pDestroySync || !pClientWait) {
    SetErr("EGL_KHR_fence_sync missing");
    return false;
  }
  return true;
}

bool CaptureEgl() {
  if (g_egl_ready.load()) return true;
  if (!LoadEglKhr()) return false;

  g_dpy = eglGetCurrentDisplay();
  g_unity = eglGetCurrentContext();
  if (g_dpy == EGL_NO_DISPLAY || g_unity == EGL_NO_CONTEXT) {
    SetErr("no current EGL display/context");
    return false;
  }

  EGLint cfg_id = 0;
  if (!eglQueryContext(g_dpy, g_unity, EGL_CONFIG_ID, &cfg_id)) {
    SetErr("eglQueryContext CONFIG_ID failed");
    return false;
  }
  EGLint attrs[] = {EGL_CONFIG_ID, cfg_id, EGL_NONE};
  EGLint n = 0;
  if (!eglChooseConfig(g_dpy, attrs, &g_cfg, 1, &n) || n < 1) {
    SetErr("eglChooseConfig failed");
    return false;
  }

  EGLint ctx_attr[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
  g_share = eglCreateContext(g_dpy, g_cfg, g_unity, ctx_attr);
  if (g_share == EGL_NO_CONTEXT) {
    SetErr("eglCreateContext share failed");
    return false;
  }
  EGLint pb[] = {EGL_WIDTH, 8, EGL_HEIGHT, 8, EGL_NONE};
  g_pbuffer = eglCreatePbufferSurface(g_dpy, g_cfg, pb);
  if (g_pbuffer == EGL_NO_SURFACE) {
    SetErr("eglCreatePbufferSurface failed");
    return false;
  }

  g_pack_prog = CompileCompute(kPackSrc, "pack");
  g_unpack_prog = CompileCompute(kUnpackSrc, "unpack");
  if (!g_pack_prog || !g_unpack_prog) return false;

  auto make_ssbo = [](GLsizeiptr bytes) {
    GLuint b = 0;
    glGenBuffers(1, &b);
    glBindBuffer(kSsbo, b);
    glBufferData(kSsbo, bytes, nullptr, GL_DYNAMIC_COPY);
    glBindBuffer(kSsbo, 0);
    return b;
  };
  const int sz = g_size.load();
  g_in_ssbo = make_ssbo((GLsizeiptr)3 * sz * sz * sizeof(float));
  g_out_ssbo = make_ssbo((GLsizeiptr)sz * sz * sizeof(float));
  GLenum err = glGetError();
  if (err != GL_NO_ERROR) {
    std::snprintf(g_err, sizeof(g_err), "ssbo glGetError 0x%x", err);
    LOGE("%s", g_err);
    return false;
  }

  g_egl_ready.store(true);
  SetErr("");
  LOGI("EGL captured, SSBOs %u/%u size=%d", g_in_ssbo, g_out_ssbo, g_size.load());
  return true;
}

void PackNchw() {
  GLuint rgb = g_rgb_tex.load();
  int sz = g_size.load();
  if (!g_pack_prog || !rgb || !g_in_ssbo) return;
  glUseProgram(g_pack_prog);
  glActiveTexture(GL_TEXTURE0);
  glBindTexture(GL_TEXTURE_2D, rgb);
  glUniform1i(glGetUniformLocation(g_pack_prog, "uRgb"), 0);
  glUniform1i(glGetUniformLocation(g_pack_prog, "uSize"), sz);
  glBindBufferBase(kSsbo, 0, g_in_ssbo);
  GLuint groups = (GLuint)((sz + 7) / 8);
  glDispatchCompute(groups, groups, 1);
  glMemoryBarrier(GL_SHADER_STORAGE_BARRIER_BIT);
  DestroySync(&g_blit_sync);
  g_blit_sync = pCreateSync(g_dpy, EGL_SYNC_FENCE_KHR, nullptr);
  glFlush();
}

void UnpackMatte() {
  GLuint matte = g_matte_tex.load();
  int sz = g_size.load();
  if (!g_unpack_prog || !matte || !g_out_ssbo) return;
  if (g_out_sync && g_out_sync != EGL_NO_SYNC_KHR) {
    if (pWaitSync)
      pWaitSync(g_dpy, g_out_sync, 0);
    else
      pClientWait(g_dpy, g_out_sync, EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, EGL_FOREVER_KHR);
  }
  glUseProgram(g_unpack_prog);
  glUniform1i(glGetUniformLocation(g_unpack_prog, "uSize"), sz);
  glBindBufferBase(kSsbo, 0, g_out_ssbo);
  glBindImageTexture(1, matte, 0, GL_FALSE, 0, GL_WRITE_ONLY, GL_R32F);
  GLuint groups = (GLuint)((sz + 7) / 8);
  glDispatchCompute(groups, groups, 1);
  glMemoryBarrier(GL_SHADER_IMAGE_ACCESS_BARRIER_BIT | GL_TEXTURE_FETCH_BARRIER_BIT);
}

bool WorkerMakeCurrent() {
  if (!g_egl_ready.load()) {
    SetErr("EGL not captured yet");
    return false;
  }
  if (!eglMakeCurrent(g_dpy, g_pbuffer, g_pbuffer, g_share)) {
    std::snprintf(g_err, sizeof(g_err), "worker eglMakeCurrent 0x%x", eglGetError());
    LOGE("%s", g_err);
    return false;
  }
  return true;
}

LiteRtRankedTensorType MakeType(int c, int h, int w) {
  LiteRtRankedTensorType t{};
  t.element_type = kLiteRtElementTypeFloat32;
  t.layout.rank = 4;
  t.layout.has_strides = false;
  t.layout.dimensions[0] = 1;
  t.layout.dimensions[1] = c;
  t.layout.dimensions[2] = h;
  t.layout.dimensions[3] = w;
  return t;
}

bool LoadModel(const char* path, const char* lib_dir) {
  if (g_loaded.load()) return true;
  if (!WorkerMakeCurrent()) return false;

  g_model_path = path ? path : "";
  g_lib_dir = lib_dir ? lib_dir : "";

  LiteRtEnvOption opts[3];
  int nopt = 0;
  if (!g_lib_dir.empty()) {
    opts[nopt].tag = kLiteRtEnvOptionTagDispatchLibraryDir;
    opts[nopt].value.type = kLiteRtAnyTypeString;
    opts[nopt].value.str_value = g_lib_dir.c_str();
    nopt++;
  }
  opts[nopt].tag = kLiteRtEnvOptionTagEglDisplay;
  opts[nopt].value.type = kLiteRtAnyTypeVoidPtr;
  opts[nopt].value.ptr_value = g_dpy;
  nopt++;
  opts[nopt].tag = kLiteRtEnvOptionTagEglContext;
  opts[nopt].value.type = kLiteRtAnyTypeVoidPtr;
  opts[nopt].value.ptr_value = g_share;
  nopt++;

  LiteRtStatus st = LiteRtCreateEnvironment(nopt, opts, &g_env);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtCreateEnvironment", st);
    return false;
  }
  st = LiteRtCreateModelFromFile(g_model_path.c_str(), &g_model);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtCreateModelFromFile", st);
    return false;
  }
  st = LiteRtCreateOptions(&g_opts);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtCreateOptions", st);
    return false;
  }
  st = LiteRtSetOptionsHardwareAccelerators(g_opts, kLiteRtHwAcceleratorGpu);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtSetOptionsHardwareAccelerators", st);
    return false;
  }
  st = LiteRtCreateCompiledModel(g_env, g_model, g_opts, &g_compiled);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtCreateCompiledModel GPU", st);
    return false;
  }

  const int sz = g_size.load();
  auto in_ty = MakeType(3, sz, sz);
  auto out_ty = MakeType(1, sz, sz);
  size_t in_bytes = (size_t)3 * sz * sz * sizeof(float);
  size_t out_bytes = (size_t)sz * sz * sizeof(float);
  st = LiteRtCreateTensorBufferFromGlBuffer(g_env, &in_ty, kSsbo, g_in_ssbo, in_bytes, 0,
                                            nullptr, &g_in_tb);
  if (st != kLiteRtStatusOk) {
    SetErrf("CreateFromGlBuffer input", st);
    return false;
  }
  st = LiteRtCreateTensorBufferFromGlBuffer(g_env, &out_ty, kSsbo, g_out_ssbo, out_bytes, 0,
                                            nullptr, &g_out_tb);
  if (st != kLiteRtStatusOk) {
    SetErrf("CreateFromGlBuffer output", st);
    return false;
  }

  g_loaded.store(true);
  g_bound.store(true);
  SetErr("");
  LOGI("CompiledModel GPU bound to GL SSBOs %u/%u", g_in_ssbo, g_out_ssbo);
  return true;
}

bool Run() {
  if (!g_loaded.load()) {
    SetErr("native model not loaded");
    return false;
  }
  if (!WorkerMakeCurrent()) return false;

  if (g_blit_sync && g_blit_sync != EGL_NO_SYNC_KHR) {
    LiteRtEvent ev = nullptr;
    LiteRtStatus est = LiteRtCreateEventFromEglSyncFence(g_env, g_blit_sync, &ev);
    if (est == kLiteRtStatusOk && ev)
      LiteRtSetTensorBufferEvent(g_in_tb, ev);
    else if (pWaitSync)
      pWaitSync(g_dpy, g_blit_sync, 0);
    else
      pClientWait(g_dpy, g_blit_sync, 0, EGL_FOREVER_KHR);
  }

  auto t0 = std::chrono::steady_clock::now();
  LiteRtStatus st = LiteRtRunCompiledModel(g_compiled, 0, 1, &g_in_tb, 1, &g_out_tb);
  auto t1 = std::chrono::steady_clock::now();
  g_run_ms.store(std::chrono::duration<float, std::milli>(t1 - t0).count());
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtRunCompiledModel", st);
    return false;
  }

  DestroySync(&g_out_sync);
  g_out_sync = pCreateSync(g_dpy, EGL_SYNC_FENCE_KHR, nullptr);
  glFlush();
  SetErr("");
  return true;
}

void CloseNative() {
  std::lock_guard<std::mutex> lock(g_mu);
  if (g_in_tb) {
    LiteRtDestroyTensorBuffer(g_in_tb);
    g_in_tb = nullptr;
  }
  if (g_out_tb) {
    LiteRtDestroyTensorBuffer(g_out_tb);
    g_out_tb = nullptr;
  }
  if (g_compiled) {
    LiteRtDestroyCompiledModel(g_compiled);
    g_compiled = nullptr;
  }
  if (g_opts) {
    LiteRtDestroyOptions(g_opts);
    g_opts = nullptr;
  }
  if (g_model) {
    LiteRtDestroyModel(g_model);
    g_model = nullptr;
  }
  if (g_env) {
    LiteRtDestroyEnvironment(g_env);
    g_env = nullptr;
  }
  g_loaded.store(false);
  g_bound.store(false);
}

} // namespace

extern "C" {

EXPORT void npu_gl_on_render_event(int event_id) {
  // Never take g_mu here: the worker holds it across CompiledModel.create
  // (seconds) and that is the S24 render-thread hang.
  if (event_id == kEventCapture) {
    CaptureEgl();
    return;
  }
  if (!g_egl_ready.load()) return;
  if (event_id == kEventPack) PackNchw();
  else if (event_id == kEventUnpack) UnpackMatte();
}

EXPORT void* npu_gl_event_fn() { return (void*)npu_gl_on_render_event; }

JNIEXPORT jboolean JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlEglReady(JNIEnv*, jobject) {
  return g_egl_ready.load() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlLoaded(JNIEnv*, jobject) {
  return g_loaded.load() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jstring JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlError(JNIEnv* env, jobject) {
  return env->NewStringUTF(g_err);
}

JNIEXPORT jfloat JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlRunMs(JNIEnv*, jobject) {
  return g_run_ms.load();
}

JNIEXPORT void JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlSetTextures(JNIEnv*, jobject, jint rgb, jint matte,
                                                           jint size) {
  g_rgb_tex.store((GLuint)rgb);
  g_matte_tex.store((GLuint)matte);
  if (size > 8) g_size.store(size);
}

JNIEXPORT jboolean JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlLoad(JNIEnv* env, jobject, jstring path,
                                                    jstring lib_dir) {
  const char* p = env->GetStringUTFChars(path, nullptr);
  const char* d = lib_dir ? env->GetStringUTFChars(lib_dir, nullptr) : nullptr;
  std::lock_guard<std::mutex> lock(g_mu);
  bool ok = LoadModel(p, d);
  env->ReleaseStringUTFChars(path, p);
  if (lib_dir && d) env->ReleaseStringUTFChars(lib_dir, d);
  return ok ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT jboolean JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlRun(JNIEnv*, jobject) {
  return Run() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlClose(JNIEnv*, jobject) {
  CloseNative();
}

} // extern "C"
