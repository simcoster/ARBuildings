// GPU-to-GPU LiteRT: wrap Unity GLES buffers, run CompiledModel on a worker
// that shares Unity's EGL context. Compile never happens on the render thread
// (that is the S24 hang from GpuDelegate).
#include "litert_min.h"

#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <GLES3/gl31.h>
#include <android/log.h>
#include <dlfcn.h>
#include <jni.h>
#include <unistd.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#ifndef EGL_NO_SYNC_KHR
#define EGL_NO_SYNC_KHR ((EGLSyncKHR)0)
#endif
#ifndef EGL_NO_CONFIG_KHR
#define EGL_NO_CONFIG_KHR ((EGLConfig)0)
#endif
#ifndef EGL_OPENGL_ES3_BIT
#define EGL_OPENGL_ES3_BIT 0x00000040
#endif
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "NpuGl", __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, "NpuGl", __VA_ARGS__)
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
std::atomic<bool> g_capturing{false};
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
std::atomic<int> g_w{1024};
std::atomic<int> g_h{1024};
int g_ssbo_w = 0;
int g_ssbo_h = 0;

LiteRtEnvironment g_env = nullptr;
LiteRtModel g_model = nullptr;
LiteRtOptions g_opts = nullptr;
LiteRtCompiledModel g_compiled = nullptr;
LiteRtTensorBuffer g_in_tb = nullptr;
LiteRtTensorBuffer g_out_tb = nullptr;
std::string g_lib_dir;
std::string g_model_path;

// LiteRtDestroyCompiledModel SIGBUS in LiteRtDeleteMlDriftClDelegate after any
// GL-CL Run on this phone (pc=0x10001), even idle, worker, EGL current. Park
// the graph instead: stop running it, keep the OpenCL object until process exit.
struct GpuGraph {
  std::string path;
  std::string lib_dir;
  LiteRtEnvironment env = nullptr;
  LiteRtModel model = nullptr;
  LiteRtOptions opts = nullptr;
  LiteRtCompiledModel compiled = nullptr;
  LiteRtTensorBuffer in_tb = nullptr;
  LiteRtTensorBuffer out_tb = nullptr;
  GLuint in_ssbo = 0;
  GLuint out_ssbo = 0;
  int w = 0;
  int h = 0;
};
std::vector<GpuGraph> g_graphs;
int g_cur = -1;

PFNEGLCREATESYNCKHRPROC pCreateSync = nullptr;
PFNEGLDESTROYSYNCKHRPROC pDestroySync = nullptr;
PFNEGLCLIENTWAITSYNCKHRPROC pClientWait = nullptr;
PFNEGLWAITSYNCKHRPROC pWaitSync = nullptr;

void SetErr(const char* s) {
  std::snprintf(g_err, sizeof(g_err), "%s", s ? s : "");
  if (g_err[0]) LOGE("%s", g_err);
}

void SetErrf(const char* fmt, LiteRtStatus st) {
  std::snprintf(g_err, sizeof(g_err), "%s status=%d", fmt, (int)st);
  LOGE("%s", g_err);
}

using FnCreateRuntimeOptions = LiteRtStatus (*)(LiteRtOpaqueOptions*);
using FnFindRuntimeOptions = LiteRtStatus (*)(LiteRtOpaqueOptions, LiteRtRuntimeOptions*);
using FnSetReporterMode = LiteRtStatus (*)(LiteRtRuntimeOptions, LiteRtErrorReporterMode);
using FnAddOpaque = LiteRtStatus (*)(LiteRtOptions, LiteRtOpaqueOptions);
using FnGetErrorMessages = LiteRtStatus (*)(LiteRtCompiledModel, char**);

template <typename Fn>
Fn Dl(const char* name) {
  return reinterpret_cast<Fn>(dlsym(RTLD_DEFAULT, name));
}

void AttachBufferReporter(LiteRtOptions opts) {
  auto create = Dl<FnCreateRuntimeOptions>("LiteRtCreateRuntimeOptions");
  auto find = Dl<FnFindRuntimeOptions>("LiteRtFindRuntimeOptions");
  auto set_mode = Dl<FnSetReporterMode>("LiteRtSetRuntimeOptionsErrorReporterMode");
  auto add = Dl<FnAddOpaque>("LiteRtAddOpaqueOptions");
  if (!create || !find || !set_mode || !add) {
    LOGW("LiteRT 2.1 has no runtime error-reporter C API (dlsym missed) — using logcat");
    return;
  }
  LiteRtOpaqueOptions opaque = nullptr;
  if (create(&opaque) != kLiteRtStatusOk || !opaque) {
    LOGW("LiteRtCreateRuntimeOptions failed");
    return;
  }
  LiteRtRuntimeOptions rt = nullptr;
  if (find(opaque, &rt) != kLiteRtStatusOk || !rt) {
    LOGW("LiteRtFindRuntimeOptions failed");
    return;
  }
  if (set_mode(rt, kLiteRtErrorReporterModeBuffer) != kLiteRtStatusOk) {
    LOGW("LiteRtSetRuntimeOptionsErrorReporterMode failed");
    return;
  }
  if (add(opts, opaque) != kLiteRtStatusOk) {
    LOGW("LiteRtAddOpaqueOptions failed");
    return;
  }
  LOGI("LiteRT buffer error reporter attached");
}

void DumpLiteRtErrors(LiteRtCompiledModel model) {
  if (!model) {
    LOGW("DumpLiteRtErrors: no compiled model (CreateCompiledModel never returned one)");
    return;
  }
  auto get = Dl<FnGetErrorMessages>("LiteRtCompiledModelGetErrorMessages");
  if (!get) {
    LOGW("LiteRtCompiledModelGetErrorMessages not exported — use unfiltered logcat");
    return;
  }
  char* msg = nullptr;
  if (get(model, &msg) != kLiteRtStatusOk || !msg) {
    LOGW("GetErrorMessages empty or failed");
    return;
  }
  LOGE("LiteRT error buffer: %s", msg);
  free(msg);
}

void LogGpuState(const char* where) {
  EGLDisplay cur_dpy = eglGetCurrentDisplay();
  EGLContext cur_ctx = eglGetCurrentContext();
  EGLint egl_err = eglGetError();
  GLenum gl_err = GL_NO_ERROR;
  GLboolean in_ok = GL_FALSE, out_ok = GL_FALSE;
  GLint in_sz = -1, out_sz = -1;
  if (cur_ctx != EGL_NO_CONTEXT && cur_dpy != EGL_NO_DISPLAY) {
    gl_err = glGetError();
    in_ok = glIsBuffer(g_in_ssbo);
    out_ok = glIsBuffer(g_out_ssbo);
    if (in_ok) {
      glBindBuffer(kSsbo, g_in_ssbo);
      glGetBufferParameteriv(kSsbo, 0x8764 /* GL_BUFFER_SIZE */, &in_sz);
    }
    if (out_ok) {
      glBindBuffer(kSsbo, g_out_ssbo);
      glGetBufferParameteriv(kSsbo, 0x8764, &out_sz);
    }
    glBindBuffer(kSsbo, 0);
  }
  LOGI("%s tid=%d passed_dpy=%p share=%p unity=%p cur_dpy=%p cur_ctx=%p same_share=%d "
       "eglErr=0x%x glErr=0x%x ssbo %u/%u isBuffer=%d/%d bytes=%d/%d",
       where, (int)gettid(), (void*)g_dpy, (void*)g_share, (void*)g_unity, (void*)cur_dpy,
       (void*)cur_ctx, (int)(cur_ctx == g_share), (int)egl_err, (int)gl_err, g_in_ssbo,
       g_out_ssbo, (int)in_ok, (int)out_ok, (int)in_sz, (int)out_sz);
}

const char* kPackSrc = R"(#version 310 es
precision highp float;
precision highp int;
precision highp sampler2D;
layout(local_size_x = 8, local_size_y = 8) in;
uniform sampler2D uRgb;
uniform int uWidth;
uniform int uHeight;
layout(std430, binding = 0) buffer InBuf { highp float data[]; };
void main() {
  ivec2 p = ivec2(gl_GlobalInvocationID.xy);
  if (p.x >= uWidth || p.y >= uHeight) return;
  vec3 c = texelFetch(uRgb, p, 0).rgb;
  int i = p.y * uWidth + p.x;
  int plane = uWidth * uHeight;
  data[i] = c.r;
  data[plane + i] = c.g;
  data[2 * plane + i] = c.b;
}
)";

const char* kUnpackSrc = R"(#version 310 es
precision highp float;
precision highp int;
precision highp image2D;
layout(local_size_x = 8, local_size_y = 8) in;
uniform int uWidth;
uniform int uHeight;
layout(std430, binding = 0) buffer OutBuf { highp float data[]; };
layout(r32f, binding = 1) uniform highp writeonly image2D uMatte;
void main() {
  ivec2 p = ivec2(gl_GlobalInvocationID.xy);
  if (p.x >= uWidth || p.y >= uHeight) return;
  int i = p.y * uWidth + p.x;
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

bool HasEglExt(const char* name) {
  const char* e = eglQueryString(g_dpy, EGL_EXTENSIONS);
  return e && std::strstr(e, name);
}

bool FindConfigById(EGLint want, EGLConfig* out) {
  EGLint n = 0;
  if (!eglGetConfigs(g_dpy, nullptr, 0, &n) || n < 1) return false;
  if (n > 256) n = 256;
  EGLConfig cfgs[256];
  EGLint got = 0;
  if (!eglGetConfigs(g_dpy, cfgs, n, &got)) return false;
  for (EGLint i = 0; i < got; i++) {
    EGLint id = 0;
    if (eglGetConfigAttrib(g_dpy, cfgs[i], EGL_CONFIG_ID, &id) && id == want) {
      *out = cfgs[i];
      return true;
    }
  }
  return false;
}

bool FindEs3Config(EGLConfig* out) {
  EGLint a0[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
                 EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
  EGLint a1[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
                 EGL_SURFACE_TYPE, EGL_WINDOW_BIT, EGL_NONE};
  EGLint a2[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
                 EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
  EGLint* tries[] = {a0, a1, a2};
  for (EGLint* a : tries) {
    EGLint n = 0;
    if (eglChooseConfig(g_dpy, a, out, 1, &n) && n > 0) return true;
  }
  EGLint n = 0;
  return eglGetConfigs(g_dpy, out, 1, &n) && n > 0;
}

bool TryPbuffer(EGLConfig cfg) {
  if (!cfg) return false;
  EGLint st = 0;
  eglGetConfigAttrib(g_dpy, cfg, EGL_SURFACE_TYPE, &st);
  if ((st & EGL_PBUFFER_BIT) == 0) return false;
  EGLint pb[] = {EGL_WIDTH, 8, EGL_HEIGHT, 8, EGL_NONE};
  g_pbuffer = eglCreatePbufferSurface(g_dpy, cfg, pb);
  return g_pbuffer != EGL_NO_SURFACE;
}

void DeleteSsbos() {
  if (g_in_ssbo) {
    glDeleteBuffers(1, &g_in_ssbo);
    g_in_ssbo = 0;
  }
  if (g_out_ssbo) {
    glDeleteBuffers(1, &g_out_ssbo);
    g_out_ssbo = 0;
  }
  g_ssbo_w = 0;
  g_ssbo_h = 0;
}

void AbandonPartialEgl() {
  DeleteSsbos();
  if (g_pack_prog) { glDeleteProgram(g_pack_prog); g_pack_prog = 0; }
  if (g_unpack_prog) { glDeleteProgram(g_unpack_prog); g_unpack_prog = 0; }
  if (g_pbuffer != EGL_NO_SURFACE) {
    eglDestroySurface(g_dpy, g_pbuffer);
    g_pbuffer = EGL_NO_SURFACE;
  }
  if (g_share != EGL_NO_CONTEXT) {
    eglDestroyContext(g_dpy, g_share);
    g_share = EGL_NO_CONTEXT;
  }
}

bool EnsureSsbos() {
  int w = g_w.load();
  int h = g_h.load();
  if (w < 8 || h < 8) {
    SetErr("infer size unset");
    return false;
  }
  GLsizeiptr in_bytes = (GLsizeiptr)3 * w * h * sizeof(float);
  GLsizeiptr out_bytes = (GLsizeiptr)w * h * sizeof(float);
  // glBufferData on a buffer LiteRT already imported as a CL-GL object leaves a
  // stale OpenCL mem on Adreno — every later Run then dies with LITERT_CL
  // "failed to invoke". Delete and allocate a new name instead.
  if (g_ssbo_w != w || g_ssbo_h != h || !g_in_ssbo || !g_out_ssbo) {
    DeleteSsbos();
    glGenBuffers(1, &g_in_ssbo);
    glBindBuffer(kSsbo, g_in_ssbo);
    glBufferData(kSsbo, in_bytes, nullptr, GL_DYNAMIC_COPY);
    glGenBuffers(1, &g_out_ssbo);
    glBindBuffer(kSsbo, g_out_ssbo);
    glBufferData(kSsbo, out_bytes, nullptr, GL_DYNAMIC_COPY);
    glBindBuffer(kSsbo, 0);
    GLenum err = glGetError();
    if (err != GL_NO_ERROR) {
      std::snprintf(g_err, sizeof(g_err), "EnsureSsbos glGetError 0x%x %dx%d", err, w, h);
      LOGE("%s", g_err);
      DeleteSsbos();
      return false;
    }
    g_ssbo_w = w;
    g_ssbo_h = h;
    LOGI("SSBOs %u/%u bytes %ld/%ld %dx%d", g_in_ssbo, g_out_ssbo, (long)in_bytes,
         (long)out_bytes, w, h);
  }
  return true;
}

using FnResize = LiteRtStatus (*)(LiteRtCompiledModel, LiteRtParamIndex, LiteRtParamIndex,
                                  const int*, size_t);

int BakedSideFromPath(const char* path, int fallback) {
  if (!path || !*path) return fallback;
  const char* slash = std::strrchr(path, '/');
  const char* name = slash ? slash + 1 : path;
  int last = fallback;
  for (const char* p = name; *p; ++p) {
    if (*p == '_' && p[1] >= '0' && p[1] <= '9') {
      int v = std::atoi(p + 1);
      if (v >= 64 && v <= 2048) last = v;
    }
  }
  return last;
}

bool ResizeToSize() {
  int w = g_w.load();
  int h = g_h.load();
  if (w != h) {
    LOGI("keeping rectangular %dx%d input", w, h);
    return true;
  }
  int baked = BakedSideFromPath(g_model_path.c_str(), 1024);
  if (w == baked) {
    LOGI("keeping baked %d input", baked);
    return true;
  }
  auto strict = Dl<FnResize>("LiteRtCompiledModelResizeInputTensor");
  auto loose = Dl<FnResize>("LiteRtCompiledModelResizeInputTensorNonStrict");
  if (!strict && !loose) {
    SetErr("no ResizeInputTensor in libLiteRt; need a baked 512 tflite");
    return false;
  }
  int dims[] = {1, 3, w, w};
  LiteRtStatus st = kLiteRtStatusErrorUnsupported;
  const char* which = "none";
  if (strict) {
    st = strict(g_compiled, 0, 0, dims, 4);
    which = "strict";
  }
  if (st != kLiteRtStatusOk && loose) {
    st = loose(g_compiled, 0, 0, dims, 4);
    which = "nonstrict";
  }
  if (st != kLiteRtStatusOk) {
    std::snprintf(g_err, sizeof(g_err),
                  "ResizeInputTensor %s %d failed status=%d (DIS baked 1024; need re-export)",
                  which, w, (int)st);
    LOGE("%s", g_err);
    DumpLiteRtErrors(g_compiled);
    return false;
  }
  LOGI("resized DIS input to 1x3x%dx%d via %s", w, w, which);
  return true;
}

bool CaptureEgl() {
  if (g_egl_ready.load()) return true;
  bool expected = false;
  if (!g_capturing.compare_exchange_strong(expected, true)) return false;
  struct Unlock {
    ~Unlock() { g_capturing.store(false); }
  } unlock;

  if (!LoadEglKhr()) {
    LOGW("CaptureEgl: %s", g_err);
    return false;
  }

  g_dpy = eglGetCurrentDisplay();
  g_unity = eglGetCurrentContext();
  LOGI("CaptureEgl dpy=%p ctx=%p", (void*)g_dpy, (void*)g_unity);
  if (g_dpy == EGL_NO_DISPLAY || g_unity == EGL_NO_CONTEXT) {
    if (!std::strstr(g_err, "compile") && !std::strstr(g_err, "link")) {
      SetErr("no current EGL display/context");
      LOGW("%s", g_err);
    }
    return false;
  }

  EGLint cfg_id = 0;
  bool have_id = eglQueryContext(g_dpy, g_unity, EGL_CONFIG_ID, &cfg_id) && cfg_id > 0;
  LOGI("CaptureEgl cfg_id=%d query_ok=%d", cfg_id, (int)have_id);

  g_cfg = nullptr;
  if (have_id && !FindConfigById(cfg_id, &g_cfg))
    LOGW("no EGLConfig with CONFIG_ID %d (eglChooseConfig is skipped; walking eglGetConfigs)",
         cfg_id);
  if (!g_cfg && !FindEs3Config(&g_cfg)) {
    std::snprintf(g_err, sizeof(g_err), "no ES3 EGLConfig 0x%x", eglGetError());
    LOGE("%s", g_err);
    return false;
  }

  EGLint ctx_attr[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
  g_share = EGL_NO_CONTEXT;
  if (HasEglExt("EGL_KHR_no_config_context"))
    g_share = eglCreateContext(g_dpy, EGL_NO_CONFIG_KHR, g_unity, ctx_attr);
  if (g_share == EGL_NO_CONTEXT)
    g_share = eglCreateContext(g_dpy, g_cfg, g_unity, ctx_attr);
  if (g_share == EGL_NO_CONTEXT) {
    std::snprintf(g_err, sizeof(g_err), "eglCreateContext share failed 0x%x", eglGetError());
    LOGE("%s", g_err);
    return false;
  }

  if (!TryPbuffer(g_cfg)) {
    EGLConfig pb_cfg = nullptr;
    EGLint pb_attrs[] = {EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
                         EGL_SURFACE_TYPE, EGL_PBUFFER_BIT, EGL_NONE};
    EGLint n = 0;
    if (eglChooseConfig(g_dpy, pb_attrs, &pb_cfg, 1, &n) && n > 0)
      TryPbuffer(pb_cfg);
  }
  if (g_pbuffer == EGL_NO_SURFACE && !HasEglExt("EGL_KHR_surfaceless_context")) {
    std::snprintf(g_err, sizeof(g_err), "no pbuffer and no surfaceless 0x%x", eglGetError());
    LOGE("%s", g_err);
    AbandonPartialEgl();
    return false;
  }
  LOGI("share ctx=%p pbuffer=%p surfaceless=%d", (void*)g_share, (void*)g_pbuffer,
       (int)(g_pbuffer == EGL_NO_SURFACE));

  g_pack_prog = CompileCompute(kPackSrc, "pack");
  g_unpack_prog = CompileCompute(kUnpackSrc, "unpack");
  if (!g_pack_prog || !g_unpack_prog) {
    AbandonPartialEgl();
    return false;
  }

  if (!EnsureSsbos()) {
    AbandonPartialEgl();
    return false;
  }

  g_egl_ready.store(true);
  SetErr("");
  LOGI("EGL captured, SSBOs %u/%u %dx%d", g_in_ssbo, g_out_ssbo, g_w.load(), g_h.load());
  return true;
}

void PackNchw() {
  GLuint rgb = g_rgb_tex.load();
  int w = g_w.load();
  int h = g_h.load();
  if (!g_pack_prog || !rgb || !g_in_ssbo) return;
  if (w != g_ssbo_w || h != g_ssbo_h) return;
  glUseProgram(g_pack_prog);
  glActiveTexture(GL_TEXTURE0);
  glBindTexture(GL_TEXTURE_2D, rgb);
  glUniform1i(glGetUniformLocation(g_pack_prog, "uRgb"), 0);
  glUniform1i(glGetUniformLocation(g_pack_prog, "uWidth"), w);
  glUniform1i(glGetUniformLocation(g_pack_prog, "uHeight"), h);
  glBindBufferBase(kSsbo, 0, g_in_ssbo);
  GLuint gx = (GLuint)((w + 7) / 8);
  GLuint gy = (GLuint)((h + 7) / 8);
  glDispatchCompute(gx, gy, 1);
  glMemoryBarrier(GL_SHADER_STORAGE_BARRIER_BIT);
  DestroySync(&g_blit_sync);
  g_blit_sync = pCreateSync(g_dpy, EGL_SYNC_FENCE_KHR, nullptr);
  glFlush();
}

void UnpackMatte() {
  GLuint matte = g_matte_tex.load();
  int w = g_w.load();
  int h = g_h.load();
  if (!g_unpack_prog || !matte || !g_out_ssbo) return;
  if (w != g_ssbo_w || h != g_ssbo_h) return;
  if (g_out_sync && g_out_sync != EGL_NO_SYNC_KHR) {
    if (pWaitSync)
      pWaitSync(g_dpy, g_out_sync, 0);
    else
      pClientWait(g_dpy, g_out_sync, EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, EGL_FOREVER_KHR);
  }
  glUseProgram(g_unpack_prog);
  glUniform1i(glGetUniformLocation(g_unpack_prog, "uWidth"), w);
  glUniform1i(glGetUniformLocation(g_unpack_prog, "uHeight"), h);
  glBindBufferBase(kSsbo, 0, g_out_ssbo);
  glBindImageTexture(1, matte, 0, GL_FALSE, 0, GL_WRITE_ONLY, GL_R32F);
  GLuint gx = (GLuint)((w + 7) / 8);
  GLuint gy = (GLuint)((h + 7) / 8);
  glDispatchCompute(gx, gy, 1);
  glMemoryBarrier(GL_SHADER_IMAGE_ACCESS_BARRIER_BIT | GL_TEXTURE_FETCH_BARRIER_BIT);
}

bool WorkerMakeCurrent(bool silent = false) {
  if (!g_egl_ready.load()) {
    if (!silent) SetErr("EGL not captured yet");
    return false;
  }
  EGLSurface surf = g_pbuffer != EGL_NO_SURFACE ? g_pbuffer : EGL_NO_SURFACE;
  if (!eglMakeCurrent(g_dpy, surf, surf, g_share)) {
    if (!silent) {
      std::snprintf(g_err, sizeof(g_err), "worker eglMakeCurrent 0x%x", eglGetError());
      LOGE("%s", g_err);
    }
    return false;
  }
  return true;
}

// The share context may be current on only one thread. If the worker exits
// still bound, the next NpuSegmenter thread gets EGL_BAD_ACCESS (0x3002) and
// every model after the first one fails. Must run on the worker, before join.
void WorkerReleaseCurrent() {
  if (g_dpy == EGL_NO_DISPLAY) return;
  EGLContext cur = eglGetCurrentContext();
  if (cur == EGL_NO_CONTEXT) {
    eglReleaseThread();
    return;
  }
  if (!eglMakeCurrent(g_dpy, EGL_NO_SURFACE, EGL_NO_SURFACE, EGL_NO_CONTEXT))
    LOGI("worker unbind eglMakeCurrent 0x%x (cur=%p share=%p)", eglGetError(),
         (void*)cur, (void*)g_share);
  else
    LOGI("worker released EGL context");
  eglReleaseThread();
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

void DrainGpu() {
  if (g_blit_sync && g_blit_sync != EGL_NO_SYNC_KHR) {
    if (pClientWait)
      pClientWait(g_dpy, g_blit_sync, EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, EGL_FOREVER_KHR);
    DestroySync(&g_blit_sync);
  }
  if (g_out_sync && g_out_sync != EGL_NO_SYNC_KHR) {
    if (pClientWait)
      pClientWait(g_dpy, g_out_sync, EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, EGL_FOREVER_KHR);
    DestroySync(&g_out_sync);
  }
  glFinish();
}

void ApplyGraph(const GpuGraph& g) {
  g_model_path = g.path;
  g_lib_dir = g.lib_dir;
  g_env = g.env;
  g_model = g.model;
  g_opts = g.opts;
  g_compiled = g.compiled;
  g_in_tb = g.in_tb;
  g_out_tb = g.out_tb;
  g_in_ssbo = g.in_ssbo;
  g_out_ssbo = g.out_ssbo;
  g_ssbo_w = g.w;
  g_ssbo_h = g.h;
  if (g.w > 8) g_w.store(g.w);
  if (g.h > 8) g_h.store(g.h);
}

void DetachGlobals() {
  g_env = nullptr;
  g_model = nullptr;
  g_opts = nullptr;
  g_compiled = nullptr;
  g_in_tb = nullptr;
  g_out_tb = nullptr;
  g_in_ssbo = 0;
  g_out_ssbo = 0;
  g_ssbo_w = 0;
  g_ssbo_h = 0;
  g_cur = -1;
  g_loaded.store(false);
  g_bound.store(false);
}

void ParkCurrent() {
  if (g_egl_ready.load() && WorkerMakeCurrent(true)) DrainGpu();
  if (g_compiled && g_in_tb) {
    if (g_cur >= 0 && g_cur < (int)g_graphs.size()) {
      GpuGraph& g = g_graphs[g_cur];
      g.path = g_model_path;
      g.lib_dir = g_lib_dir;
      g.env = g_env;
      g.model = g_model;
      g.opts = g_opts;
      g.compiled = g_compiled;
      g.in_tb = g_in_tb;
      g.out_tb = g_out_tb;
      g.in_ssbo = g_in_ssbo;
      g.out_ssbo = g_out_ssbo;
      g.w = g_ssbo_w;
      g.h = g_ssbo_h;
    } else {
      GpuGraph g;
      g.path = g_model_path;
      g.lib_dir = g_lib_dir;
      g.env = g_env;
      g.model = g_model;
      g.opts = g_opts;
      g.compiled = g_compiled;
      g.in_tb = g_in_tb;
      g.out_tb = g_out_tb;
      g.in_ssbo = g_in_ssbo;
      g.out_ssbo = g_out_ssbo;
      g.w = g_ssbo_w;
      g.h = g_ssbo_h;
      g_graphs.push_back(g);
      g_cur = (int)g_graphs.size() - 1;
    }
    LOGI("idle, parked %s ssbo %u/%u (%zu resident — not destroyed)",
         g_model_path.c_str(), g_in_ssbo, g_out_ssbo, g_graphs.size());
  }
  DetachGlobals();
}

int FindGraph(const char* path) {
  if (!path || !*path) return -1;
  for (int i = 0; i < (int)g_graphs.size(); ++i)
    if (g_graphs[i].path == path) return i;
  return -1;
}

void CloseLiteRtUnlocked() {
  // Stop using the graph. Do not call LiteRtDestroyCompiledModel.
  ParkCurrent();
}

bool CreateLiteRtEnv(bool with_egl) {
  LiteRtEnvOption opts[4];
  int nopt = 0;
  if (!g_lib_dir.empty()) {
    opts[nopt].tag = kLiteRtEnvOptionTagDispatchLibraryDir;
    opts[nopt].value.type = kLiteRtAnyTypeString;
    opts[nopt].value.str_value = g_lib_dir.c_str();
    nopt++;
  }
  if (with_egl) {
    // LiteRT 2.1 gpu_environment.cc only copies EGL handles when type is Int
    // (int_value = pointer bits). VoidPtr is ignored, so the previous path
    // never actually handed Unity's context to CreateCLGLContext.
    opts[nopt].tag = kLiteRtEnvOptionTagEglDisplay;
    opts[nopt].value.type = kLiteRtAnyTypeInt;
    opts[nopt].value.int_value = static_cast<int64_t>(reinterpret_cast<intptr_t>(g_dpy));
    nopt++;
    opts[nopt].tag = kLiteRtEnvOptionTagEglContext;
    opts[nopt].value.type = kLiteRtAnyTypeInt;
    opts[nopt].value.int_value = static_cast<int64_t>(reinterpret_cast<intptr_t>(g_share));
    nopt++;
    LOGI("EGL env opts as Int dpy=%p share=%p", (void*)g_dpy, (void*)g_share);
  }
  LiteRtStatus st = LiteRtCreateEnvironment(nopt, opts, &g_env);
  if (st != kLiteRtStatusOk) {
    SetErrf(with_egl ? "LiteRtCreateEnvironment egl" : "LiteRtCreateEnvironment", st);
    return false;
  }
  return true;
}

bool CompileGpu() {
  LiteRtStatus st = LiteRtCreateModelFromFile(g_model_path.c_str(), &g_model);
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
  AttachBufferReporter(g_opts);
  LogGpuState("before CreateCompiledModel");
  st = LiteRtCreateCompiledModel(g_env, g_model, g_opts, &g_compiled);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtCreateCompiledModel GPU", st);
    DumpLiteRtErrors(g_compiled);
    LogGpuState("after CreateCompiledModel fail");
    return false;
  }
  return true;
}

bool LoadModel(const char* path, const char* lib_dir) {
  const char* next = path ? path : "";
  if (lib_dir && *lib_dir) g_lib_dir = lib_dir;

  if (g_loaded.load() && g_compiled && g_model_path == next) {
    LOGI("LoadModel already current %s", next);
    return true;
  }
  int hit = FindGraph(next);
  if (hit >= 0) {
    ParkCurrent();
    g_cur = hit;
    ApplyGraph(g_graphs[hit]);
    g_loaded.store(true);
    g_bound.store(true);
    LOGI("LoadModel cache hit %s ssbo %u/%u %dx%d", next, g_in_ssbo, g_out_ssbo,
         g_ssbo_w, g_ssbo_h);
    SetErr("");
    return true;
  }

  if (g_compiled || g_env) ParkCurrent();
  g_model_path = next;
  if (lib_dir) g_lib_dir = lib_dir;

  // LiteRT OpenCL delegate: eglGetCurrentContext() must equal the env's EGL
  // context (delegate_opencl.cc:632 "EGL context or display does not match").
  // That check runs on THIS worker, not Unity's render thread — the S24 hang
  // was compiling on the render thread, not sharing a pbuffer context here.
  if (!WorkerMakeCurrent()) {
    CloseLiteRtUnlocked();
    return false;
  }
  LogGpuState("LoadModel after MakeCurrent (share must be current)");
  if (!CreateLiteRtEnv(true) || !CompileGpu()) {
    LOGW("GPU compile with EGL env failed (%s)", g_err);
    CloseLiteRtUnlocked();
    return false;
  }
  LOGI("CompiledModel GPU ok egl_env=1 %dx%d", g_w.load(), g_h.load());

  if (!WorkerMakeCurrent()) {
    CloseLiteRtUnlocked();
    return false;
  }
  if (!ResizeToSize()) {
    CloseLiteRtUnlocked();
    return false;
  }
  if (!EnsureSsbos()) {
    CloseLiteRtUnlocked();
    return false;
  }

  const int w = g_w.load();
  const int h = g_h.load();
  auto in_ty = MakeType(3, h, w);
  auto out_ty = MakeType(1, h, w);
  size_t in_bytes = (size_t)3 * w * h * sizeof(float);
  size_t out_bytes = (size_t)w * h * sizeof(float);
  LiteRtStatus st = LiteRtCreateTensorBufferFromGlBuffer(
      g_env, &in_ty, kSsbo, g_in_ssbo, in_bytes, 0, nullptr, &g_in_tb);
  if (st != kLiteRtStatusOk) {
    SetErrf("CreateFromGlBuffer input", st);
    CloseLiteRtUnlocked();
    return false;
  }
  st = LiteRtCreateTensorBufferFromGlBuffer(g_env, &out_ty, kSsbo, g_out_ssbo, out_bytes, 0,
                                            nullptr, &g_out_tb);
  if (st != kLiteRtStatusOk) {
    SetErrf("CreateFromGlBuffer output", st);
    CloseLiteRtUnlocked();
    return false;
  }

  g_loaded.store(true);
  g_bound.store(true);
  if (FindGraph(g_model_path.c_str()) < 0) {
    GpuGraph g;
    g.path = g_model_path;
    g.lib_dir = g_lib_dir;
    g.env = g_env;
    g.model = g_model;
    g.opts = g_opts;
    g.compiled = g_compiled;
    g.in_tb = g_in_tb;
    g.out_tb = g_out_tb;
    g.in_ssbo = g_in_ssbo;
    g.out_ssbo = g_out_ssbo;
    g.w = g_ssbo_w;
    g.h = g_ssbo_h;
    g_graphs.push_back(g);
    g_cur = (int)g_graphs.size() - 1;
  }
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
    // LiteRT OpenCL: "Attaching EGLSyncFence event to TensorBuffer is not
    // needed. GL-CL event synchronization is handled internally." Doing it
    // anyway returned status=3 / "Node 247 (LITERT_CL) failed to invoke."
    if (pWaitSync)
      pWaitSync(g_dpy, g_blit_sync, 0);
    else
      pClientWait(g_dpy, g_blit_sync, EGL_SYNC_FLUSH_COMMANDS_BIT_KHR, EGL_FOREVER_KHR);
    DestroySync(&g_blit_sync);
  }

  LogGpuState("before Run");
  auto t0 = std::chrono::steady_clock::now();
  LiteRtStatus st = LiteRtRunCompiledModel(g_compiled, 0, 1, &g_in_tb, 1, &g_out_tb);
  auto t1 = std::chrono::steady_clock::now();
  float ms = std::chrono::duration<float, std::milli>(t1 - t0).count();
  g_run_ms.store(ms);
  if (st != kLiteRtStatusOk) {
    SetErrf("LiteRtRunCompiledModel", st);
    DumpLiteRtErrors(g_compiled);
    LogGpuState("after Run fail");
    return false;
  }

  DestroySync(&g_out_sync);
  g_out_sync = pCreateSync(g_dpy, EGL_SYNC_FENCE_KHR, nullptr);
  glFlush();
  SetErr("");
  LOGI("Run ok %.1f ms %dx%d", ms, g_ssbo_w, g_ssbo_h);
  return true;
}

void CloseNative() {
  std::lock_guard<std::mutex> lock(g_mu);
  CloseLiteRtUnlocked();
  WorkerReleaseCurrent();
}

} // namespace

extern "C" {

EXPORT void npu_gl_on_render_event(int event_id) {
  // Never take g_mu here: the worker holds it across CompiledModel.create
  // (seconds) and that is the S24 render-thread hang.
  if (event_id == kEventCapture) {
    LOGI("capture event ctx=%p ready=%d", (void*)eglGetCurrentContext(),
         (int)g_egl_ready.load());
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
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlCapture(JNIEnv*, jobject) {
  return CaptureEgl() ? JNI_TRUE : JNI_FALSE;
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
                                                           jint width, jint height) {
  if (rgb != 0) g_rgb_tex.store((GLuint)rgb);
  if (matte != 0) g_matte_tex.store((GLuint)matte);
  if (width > 8) g_w.store(width);
  if (height > 8) g_h.store(height);
  else if (width > 8) g_h.store(width);
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
  std::lock_guard<std::mutex> lock(g_mu);
  return Run() ? JNI_TRUE : JNI_FALSE;
}

JNIEXPORT void JNICALL
Java_com_pavel_arbuildings_NpuSegmenter_nativeGlClose(JNIEnv*, jobject) {
  CloseNative();
}

} // extern "C"
