package com.pavel.arbuildings;

import org.tensorflow.lite.DataType;
import org.tensorflow.lite.Interpreter;
import org.tensorflow.lite.Tensor;

import com.google.ai.edge.litert.Accelerator;
import com.google.ai.edge.litert.CompiledModel;
import com.google.ai.edge.litert.TensorBuffer;

import java.io.File;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.List;
import java.util.Locale;

/**
 * TFLite wrapper for a single-input image segmenter.
 *
 * Four backends:
 *   cpu    — XNNPACK only
 *   gpu    — XNNPACK (LiteRT 2.1 Interpreter is CPU-only; no NnApiDelegate in this AAR)
 *   gpudec — LiteRT CompiledModel(GPU) for DIS-ISNet
 *   npu    — same as gpu; ENN NNAPI is gone with classic tensorflow-lite
 */
public final class NpuSegmenter {
    private static final boolean nativeLoaded;
    static {
        boolean ok = false;
        try {
            System.loadLibrary("LiteRt");
            System.loadLibrary("npu_gl");
            ok = true;
        } catch (UnsatisfiedLinkError e) {
            android.util.Log.w("NpuGl", "npu_gl not loaded: " + e.getMessage());
        }
        nativeLoaded = ok;
    }

    private native boolean nativeGlEglReady();
    private native boolean nativeGlCapture();
    private native boolean nativeGlLoaded();
    private native String nativeGlError();
    private native float nativeGlRunMs();
    private native void nativeGlSetTextures(int rgb, int matte, int size);
    private native boolean nativeGlLoad(String path, String libDir);
    private native boolean nativeGlRun();
    private native void nativeGlClose();

    public static boolean nativeGlAvailable() { return nativeLoaded; }

    private Interpreter interpreter;
    private CompiledModel compiled;
    private List<TensorBuffer> compiledInBufs;
    private List<TensorBuffer> compiledOutBufs;
    private float[] compiledIn;
    private String compiledPath;
    private String lastError = "";
    private String ep = "none";
    private float lastMs = -1f;
    private float fillMs = -1f;
    private float runMs = -1f;
    private float decodeMs = -1f;

    private int inH, inW, inC;
    private int outH, outW, outC;
    private boolean nchw;
    // Whether the LOGITS are channel-major. PIDNet emits [1,19,128,128]: all of class 0's
    // map, then all of class 1's. Decoding that pixel-major reads 19 neighbouring pixels of
    // one class as 19 classes of one pixel, and the argmax of that is a plausible-looking
    // segmentation of nothing.
    private boolean outNchw;
    private DataType inType = DataType.UINT8;
    private DataType outType = DataType.FLOAT32;
    private ByteBuffer inBuf;
    private ByteBuffer outBuf;

    // Inference runs on this worker, never on Unity's render thread. Measured on the A35:
    // synchronous inference made frame time equal inference time, 33 ms -> 90+ ms, which
    // read as "the accelerator is slow" when it was really "the caller is blocked".
    private final Object lock = new Object();
    private Thread worker;
    private volatile boolean running;
    private byte[] pending;
    private boolean hasPending;
    private boolean hasGlPending;
    private boolean glFrameDone;
    private boolean glPath;
    private byte[] readyLabels;
    private boolean inFlight;

    // DeepLab's float32 graphs are trained on (v - 127.5) / 127.5, i.e. [-1,1].
    // Feeding [0,1] does not fail, it just returns background everywhere, which is
    // indistinguishable from "the object is not in the label set". Settable so the
    // two can be told apart over RemoteControl instead of by rebuilding.
    private float inMean = 127.5f;
    private float inScale = 127.5f;

    public String lastError() { return lastError; }
    public String ep() { return ep; }
    public float lastInferenceMs() { return lastMs; }
    public float fillMs() { return fillMs; }
    public float runMs() { return runMs; }
    public float decodeMs() { return decodeMs; }
    public int inputWidth() { return inW; }
    public int inputHeight() { return inH; }
    public int outputWidth() { return outW; }
    public int outputHeight() { return outH; }
    public int outputChannels() { return outC; }
    public boolean ready() { return interpreter != null || compiled != null || compiledPath != null || canny || bench; }

    public boolean glPathReady() { return glPath && nativeLoaded; }

    // No TFLite. Same worker, same RGB in / byte-map out as a matte. 480 is the largest
    // square the A35 camera CPU image (640x480) can feed without upscaling.
    private static final int CANNY_SIZE = 480;
    private boolean canny;
    private int[] cGray, cBlur, cMag, cNms, cStack;

    // Latency probe: 1024² like DIS, but the "network" is luma. Isolates blit / copy /
    // rotate / paint from LiteRT fill+readFloat.
    private static final int BENCH_SIZE = 1024;
    private boolean bench;

    public void setNormalization(float mean, float scale) {
        inMean = mean;
        inScale = scale == 0f ? 1f : scale;
    }

    public String normalization() {
        return String.format(Locale.US, "(v-%.1f)/%.1f", inMean, inScale);
    }

    // What a single output channel MEANS. Matting models (MODNet, RVM, RMBG) emit an alpha
    // already in 0..1; monocular depth models (MiDaS, Depth Anything) emit relative INVERSE
    // depth on an arbitrary scale that is invisible unless normalised per frame. Both arrive
    // as [1,H,W,1] FLOAT32 and cannot be told apart from the shape, so "auto" decides from
    // the observed range and reports which way it went.
    private String kind = "auto";
    private float lastMin, lastMax;
    private String lastScalarMode = "n/a";

    public void setOutputKind(String k) {
        kind = (k == null || k.isEmpty()) ? "auto" : k.trim().toLowerCase(Locale.US);
    }

    public String outputKind() {
        return kind + (scalarOutput() ? " -> " + lastScalarMode : " -> labels");
    }

    /** "0.0182 .. 8.4310" — the line that says whether a scalar is an alpha or a depth. */
    public String scalarRange() {
        if (!scalarOutput()) return "n/a (label map)";
        return String.format(Locale.US, "%.4f .. %.4f", lastMin, lastMax);
    }

    /**
     * A single FLOAT32 channel is a continuous value, not a class id. More channels, or an
     * integer type, means an argmax has already been applied and it stays on the label path.
     */
    public boolean scalarOutput() {
        if (canny || bench) return true;
        if ("labels".equals(kind)) return false;
        return outC <= 1 && outType == DataType.FLOAT32;
    }

    /**
     * Canny edges as a 0/255 matte. No .tflite, so it is always available — the HUD cycle
     * can land here without a file push.
     */
    public boolean loadCanny() {
        close();
        inW = inH = outW = outH = CANNY_SIZE;
        inC = 3;
        outC = 1;
        nchw = false;
        outNchw = false;
        inType = DataType.UINT8;
        outType = DataType.FLOAT32;
        canny = true;
        kind = "alpha";
        lastScalarMode = "alpha (canny)";
        lastMin = 0f;
        lastMax = 1f;
        int n = CANNY_SIZE * CANNY_SIZE;
        cGray = new int[n];
        cBlur = new int[n];
        cMag = new int[n];
        cNms = new int[n];
        cStack = new int[n];
        ep = "canny";
        lastError = "";
        return true;
    }

    /**
     * One-layer stand-in at DIS resolution. Rec.601 luma written as a 0..255 matte.
     * No interpreter, so fill and decode are zero and run is a 1024² loop.
     */
    public boolean loadBench() {
        close();
        inW = inH = outW = outH = BENCH_SIZE;
        inC = 3;
        outC = 1;
        nchw = false;
        outNchw = false;
        inType = DataType.UINT8;
        outType = DataType.FLOAT32;
        bench = true;
        kind = "alpha";
        lastScalarMode = "alpha (bench luma)";
        lastMin = 0f;
        lastMax = 1f;
        ep = "bench luma 1024";
        lastError = "";
        return true;
    }

    public boolean loadBytes(byte[] model, String backend) {
        close();
        if (model == null || model.length == 0) {
            lastError = "empty model bytes";
            ep = "REJECT";
            return false;
        }
        try {
            ByteBuffer modelBuf = ByteBuffer.allocateDirect(model.length);
            modelBuf.order(ByteOrder.nativeOrder());
            modelBuf.put(model).rewind();
            interpreter = new Interpreter(modelBuf, optionsFor(backend));
            describe();
            lastError = "";
            return true;
        } catch (Throwable e) {
            lastError = e.getClass().getSimpleName() + ": " + e.getMessage();
            ep = "REJECT";
            close();
            return false;
        }
    }

    /**
     * mmap the file instead of copying it into a direct buffer. A 176 MB IS-Net otherwise
     * exists twice in RAM before a single inference, which on this phone is how switching
     * to it became a SIGSEGV in libtensorflowlite_jni rather than a Java exception.
     */
    public boolean loadFile(String path, String backend) {
        close();
        if (path == null || path.isEmpty()) {
            lastError = "empty model path";
            ep = "REJECT";
            return false;
        }
        File file = new File(path);
        if (!file.isFile()) {
            lastError = "missing " + path;
            ep = "REJECT";
            return false;
        }
        if (useCompiledGpu(backend, path)) {
            compiledPath = path;
            describeDisLiteRt();
            ep = "litert-gpu";
            lastError = "";
            return true;
        }
        try {
            interpreter = new Interpreter(file, optionsFor(backend));
            describe();
            lastError = "";
            return true;
        } catch (Throwable e) {
            lastError = e.getClass().getSimpleName() + ": " + e.getMessage();
            ep = "REJECT";
            close();
            return false;
        }
    }

    private static boolean useCompiledGpu(String backend, String path) {
        String b = backend == null ? "" : backend.toLowerCase(Locale.US);
        if (!("gpudec".equals(b) || "litert".equals(b))) return false;
        String n = path == null ? "" : new File(path).getName().toLowerCase(Locale.US);
        return n.contains("isnet") || n.contains("dis");
    }

    /** DIS-ISNet LiteRT I/O is NCHW float32. Baked file is 1024²; `setIoSize` can
     *  ask LiteRT to resize after compile (FCN). */
    private void describeDisLiteRt() {
        nchw = true;
        inC = 3;
        int side = 1024;
        String n = compiledPath == null ? "" : new File(compiledPath).getName();
        java.util.regex.Matcher m = java.util.regex.Pattern.compile("_(\\d+)").matcher(n);
        if (m.find()) {
            try {
                int parsed = Integer.parseInt(m.group(1));
                if (parsed >= 64 && parsed <= 2048) side = parsed;
            } catch (NumberFormatException ignored) { }
        }
        inH = inW = side;
        outNchw = true;
        outC = 1;
        outH = outW = side;
        inType = DataType.FLOAT32;
        outType = DataType.FLOAT32;
        compiledIn = new float[inC * inH * inW];
        kind = "alpha";
    }

    public void setIoSize(int side) {
        if (side < 64 || side > 2048) return;
        inH = inW = outH = outW = side;
        if (inC < 1) inC = 3;
        compiledIn = new float[inC * inH * inW];
    }

    private Interpreter.Options optionsFor(String backend) {
        String b = backend == null ? "npu" : backend.toLowerCase(Locale.US);
        Interpreter.Options iopt = new Interpreter.Options();
        iopt.setNumThreads(b.equals("cpuref") ? 2 : 4);

        if ("cpu".equals(b)) {
            iopt.setUseXNNPACK(true);
            ep = "xnnpack";
        } else if ("cpuref".equals(b)) {
            // XNNPACK refuses some ops outright — DeepLab 513's RESIZE_BILINEAR among
            // them — and refusing to load is not the same as being slow. The built-in
            // kernels take those graphs, so this is how a model XNNPACK rejects still
            // gets measured. Also the only way a 1024² graph has survived on this phone:
            // XNNPACK native-crashes instead of throwing.
            iopt.setUseXNNPACK(false);
            ep = "cpu-builtin";
        } else if ("gpu".equals(b) || "gpudec".equals(b) || "npu".equals(b)) {
            // DIS uses CompiledModel GPU in loadFile, not this Interpreter path.
            // LiteRT 2.1 Interpreter is CPU-only and does not ship NnApiDelegate.
            iopt.setUseXNNPACK(true);
            ep = "xnnpack (no nnapi in LiteRT)";
        } else {
            iopt.setUseXNNPACK(true);
            ep = "xnnpack";
        }
        return iopt;
    }

    private void describe() {
        Tensor in = interpreter.getInputTensor(0);
        Tensor out = interpreter.getOutputTensor(0);
        inType = in.dataType();
        outType = out.dataType();
        int[] ish = in.shape();
        int[] osh = out.shape();
        if (ish.length == 4 && ish[1] <= 4 && ish[2] > 4 && ish[3] > 4) {
            nchw = true;
            inC = ish[1];
            inH = ish[2];
            inW = ish[3];
        } else if (ish.length == 4) {
            nchw = false;
            inH = ish[1];
            inW = ish[2];
            inC = ish[3];
        } else {
            throw new IllegalStateException("unexpected input shape");
        }

        if (osh.length == 4 && osh[1] > 4 && osh[3] <= 64) {
            outNchw = false;
            outH = osh[1];
            outW = osh[2];
            outC = osh[3];
        } else if (osh.length == 4) {
            outNchw = true;
            outC = osh[1];
            outH = osh[2];
            outW = osh[3];
        } else if (osh.length == 3) {
            outNchw = false;
            outH = osh[1];
            outW = osh[2];
            outC = 1;
        } else if (osh.length == 2) {
            outH = 1;
            outW = 1;
            outC = osh[1];
        } else {
            throw new IllegalStateException("unexpected output shape");
        }

        inBuf = ByteBuffer.allocateDirect(in.numBytes()).order(ByteOrder.nativeOrder());
        outBuf = ByteBuffer.allocateDirect(out.numBytes()).order(ByteOrder.nativeOrder());
    }

    /**
     * rgb is packed RGB24, length at least inW*inH*3, row-major. Returns outW*outH
     * class ids (argmax already applied), or null on failure with lastError set.
     *
     * The label map is RETURNED rather than written into a caller-supplied array:
     * Unity's JNI copies a managed array INTO a fresh Java array and never copies it
     * back, so an out-parameter silently stays whatever the caller allocated — which
     * read as "the model labels nothing" for as long as this was wired that way.
     *
     * Synchronous. Prefer {@link #submit} — on the render thread this costs a stall the
     * length of the inference, which on this phone made frame time equal inference time.
     */
    public byte[] inferLabels(byte[] rgb) {
        if (interpreter == null && compiled == null && compiledPath == null && !canny && !bench) {
            lastError = "not loaded";
            return null;
        }
        if (rgb == null || rgb.length < inW * inH * 3) {
            lastError = "rgb too small: " + (rgb == null ? 0 : rgb.length)
                    + " < " + (inW * inH * 3);
            return null;
        }
        return runOnce(rgb);
    }

    private byte[] runOnce(byte[] rgb) {
        try {
            if (canny) {
                byte[] out = new byte[outW * outH];
                long tRun = System.nanoTime();
                runCanny(rgb, out);
                runMs = (System.nanoTime() - tRun) / 1e6f;
                fillMs = 0f;
                decodeMs = 0f;
                lastMs = runMs;
                lastError = "";
                return out;
            }

            if (bench) {
                byte[] out = new byte[outW * outH];
                long tRun = System.nanoTime();
                runBenchLuma(rgb, out);
                runMs = (System.nanoTime() - tRun) / 1e6f;
                fillMs = 0f;
                decodeMs = 0f;
                lastMs = runMs;
                lastError = "";
                return out;
            }

            if (compiledPath != null || compiled != null) {
                return runCompiled(rgb);
            }

            long tFill = System.nanoTime();
            fillInput(rgb);
            fillMs = (System.nanoTime() - tFill) / 1e6f;

            // Rewind the OUTPUT too. decodeLabels leaves position at the limit, so
            // without this every run after the first sees remaining() == 0 and throws
            // an exception whose message is null.
            outBuf.rewind();
            long tRun = System.nanoTime();
            interpreter.run(inBuf, outBuf);
            runMs = (System.nanoTime() - tRun) / 1e6f;
            lastMs = fillMs + runMs; // kept as "inference" until decode is added below

            byte[] out = new byte[outW * outH];
            long tDec = System.nanoTime();
            if (scalarOutput()) decodeScalar(out);
            else decodeLabels(out);
            decodeMs = (System.nanoTime() - tDec) / 1e6f;
            lastMs = fillMs + runMs + decodeMs;
            lastError = "";
            return out;
        } catch (Throwable e) {
            lastError = "run " + e.getClass().getSimpleName() + ": " + e.getMessage();
            return null;
        }
    }

    /**
     * Hands a frame to the worker thread and returns immediately; false means a job is
     * already in flight and this frame should be dropped. Poll {@link #pollLabels()}.
     */
    public boolean submit(byte[] rgb) {
        if (interpreter == null && compiled == null && compiledPath == null && !canny && !bench) {
            lastError = "not loaded";
            return false;
        }
        int need = inW * inH * 3;
        if (rgb == null || rgb.length < need) {
            lastError = "rgb too small: " + (rgb == null ? 0 : rgb.length) + " < " + need;
            return false;
        }

        synchronized (lock) {
            if (inFlight) return false;
            if (pending == null || pending.length != need) pending = new byte[need];
            System.arraycopy(rgb, 0, pending, 0, need);
            hasPending = true;
            inFlight = true;
            startWorker();
            lock.notifyAll();
        }
        return true;
    }

    public void setGlTextures(int rgbTex, int matteTex, int size) {
        if (!nativeLoaded) return;
        nativeGlSetTextures(rgbTex, matteTex, size);
    }

    public boolean eglReady() {
        return nativeLoaded && nativeGlEglReady();
    }

    /** Call on a thread that has Unity's GLES context current (plugin event or end-of-frame). */
    public boolean captureEglNow() {
        if (!nativeLoaded) return false;
        if (nativeGlCapture()) {
            lastError = "";
            return true;
        }
        lastError = nativeGlError();
        return false;
    }

    /**
     * Zero-copy GPU path for DIS: worker waits the Unity blit fence, CompiledModel::Run
     * on GL SSBOs, then an output fence. No writeFloat / readFloat.
     */
    public boolean enableGlPath() {
        if (!nativeLoaded) return false;
        if (canny || bench || compiledPath == null) return false;
        glPath = true;
        nativeGlSetTextures(0, 0, inW);
        return true;
    }

    public boolean submitGl() {
        if (!glPath || !nativeLoaded) {
            lastError = "gl path off";
            return false;
        }
        if (!nativeGlEglReady()) {
            lastError = "EGL not captured yet";
            return false;
        }
        synchronized (lock) {
            if (inFlight) return false;
            hasGlPending = true;
            inFlight = true;
            startWorker();
            lock.notifyAll();
        }
        return true;
    }

    /** True once the worker finished a GL run (output fence is posted). Clears on read. */
    public boolean pollGl() {
        synchronized (lock) {
            if (!glFrameDone) return false;
            glFrameDone = false;
            return true;
        }
    }

    private String nativeLibDir() {
        try {
            Class<?> up = Class.forName("com.unity3d.player.UnityPlayer");
            Object act = up.getField("currentActivity").get(null);
            Object info = act.getClass().getMethod("getApplicationInfo").invoke(act);
            Object dir = info.getClass().getField("nativeLibraryDir").get(info);
            return dir == null ? "" : dir.toString();
        } catch (Throwable e) {
            return "";
        }
    }

    private boolean runGl() {
        try {
            if (!nativeGlLoaded()) {
                long tLoad = System.nanoTime();
                if (!nativeGlLoad(compiledPath, nativeLibDir())) {
                    lastError = nativeGlError();
                    // Waiting for Unity's render thread to capture EGL is not a reject.
                    if (lastError == null || !lastError.contains("EGL not captured"))
                        ep = "REJECT gl";
                    return false;
                }
                float compileMs = (System.nanoTime() - tLoad) / 1e6f;
                ep = String.format(Locale.US, "litert-gl compile %.0fms", compileMs);
            }
            fillMs = 0f;
            decodeMs = 0f;
            if (!nativeGlRun()) {
                lastError = nativeGlError();
                return false;
            }
            runMs = nativeGlRunMs();
            lastMs = runMs;
            lastError = "";
            lastMin = 0f;
            lastMax = 1f;
            lastScalarMode = "alpha (gl)";
            return true;
        } catch (Throwable e) {
            lastError = "gl run " + e.getClass().getSimpleName() + ": " + e.getMessage();
            return false;
        }
    }

    /** The newest finished label map, or null if none is waiting. Clears on read. */
    public byte[] pollLabels() {
        synchronized (lock) {
            byte[] r = readyLabels;
            readyLabels = null;
            return r;
        }
    }

    public boolean busy() {
        synchronized (lock) {
            return inFlight;
        }
    }

    private void startWorker() {
        if (worker != null && worker.isAlive()) return;
        running = true;
        worker = new Thread(this::loop, "NpuSegmenter");
        worker.setDaemon(true);
        worker.start();
    }

    private void loop() {
        while (true) {
            byte[] job;
            boolean gl;
            synchronized (lock) {
                while (running && !hasPending && !hasGlPending) {
                    try {
                        lock.wait(200);
                    } catch (InterruptedException e) {
                        return;
                    }
                }
                if (!running) return;
                gl = hasGlPending;
                hasGlPending = false;
                job = gl ? null : pending;
                if (!gl) hasPending = false;
            }

            byte[] result = null;
            boolean glOk = false;
            if (gl) glOk = runGl();
            else result = runOnce(job);

            synchronized (lock) {
                readyLabels = result;
                glFrameDone = gl && glOk;
                inFlight = false;
            }
        }
    }

    private void fillInput(byte[] rgb) {
        inBuf.rewind();
        // Always inW*inH*3, never rgb.length: XRCpuImage.GetConvertedDataSize can hand
        // back a larger buffer than the tensor, and the extra bytes overflow inBuf.
        int px = inW * inH;
        if (inType == DataType.UINT8 || inType == DataType.INT8) {
            if (!nchw) {
                inBuf.put(rgb, 0, px * 3);
            } else {
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < px; i++)
                        inBuf.put(rgb[i * 3 + c]);
            }
        } else {
            if (!nchw) {
                for (int i = 0; i < px * 3; i++)
                    inBuf.putFloat(((rgb[i] & 0xff) - inMean) / inScale);
            } else {
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < px; i++)
                        inBuf.putFloat(((rgb[i * 3 + c] & 0xff) - inMean) / inScale);
            }
        }
        inBuf.rewind();
    }

    /**
     * LiteRT CompiledModel GPU — the HuggingFace DIS card path, not TfLiteGpuDelegateV2.
     * First call compiles (can be seconds); later calls are run(). Must happen on this
     * worker, not Unity's thread: GLES and LiteRT-CL fighting on the render thread is
     * what wedged the S24 when the HUD's seg button ran Interpreter+GpuDelegate there.
     */
    private byte[] runCompiled(byte[] rgb) throws Exception {
        if (compiled == null) {
            long tLoad = System.nanoTime();
            CompiledModel.Options opts = new CompiledModel.Options(Accelerator.GPU);
            compiled = CompiledModel.create(compiledPath, opts);
            compiledInBufs = compiled.createInputBuffers();
            compiledOutBufs = compiled.createOutputBuffers();
            float compileMs = (System.nanoTime() - tLoad) / 1e6f;
            ep = String.format(Locale.US, "litert-gpu compile %.0fms", compileMs);
            lastError = "";
            runMs = compileMs;
        }

        long tFill = System.nanoTime();
        int px = inW * inH;
        int i = 0;
        for (int c = 0; c < 3; c++)
            for (int p = 0; p < px; p++)
                compiledIn[i++] = ((rgb[p * 3 + c] & 0xff) - inMean) / inScale;
        compiledInBufs.get(0).writeFloat(compiledIn);
        fillMs = (System.nanoTime() - tFill) / 1e6f;

        long tRun = System.nanoTime();
        compiled.run(compiledInBufs, compiledOutBufs);
        runMs = (System.nanoTime() - tRun) / 1e6f;

        long tDec = System.nanoTime();
        float[] src = compiledOutBufs.get(0).readFloat();
        byte[] out = new byte[outW * outH];
        decodeScalarFloats(src, out);
        decodeMs = (System.nanoTime() - tDec) / 1e6f;
        lastMs = fillMs + runMs + decodeMs;
        lastError = "";
        return out;
    }

    private void decodeScalarFloats(float[] src, byte[] out) {
        int n = outW * outH;
        int use = Math.min(n, src.length);
        boolean forceAlpha = "alpha".equals(kind);
        if (forceAlpha) {
            float min = Float.MAX_VALUE;
            float max = -Float.MAX_VALUE;
            for (int i = 0; i < n; i++) {
                float v = i < use ? src[i] : 0f;
                if (v < min) min = v;
                if (v > max) max = v;
                int b = (int) (v * 255f + 0.5f);
                out[i] = (byte) (b < 0 ? 0 : (b > 255 ? 255 : b));
            }
            lastMin = min;
            lastMax = max;
            lastScalarMode = "alpha (absolute)";
            return;
        }
        float min = Float.MAX_VALUE;
        float max = -Float.MAX_VALUE;
        for (int i = 0; i < use; i++) {
            float v = src[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        lastMin = min;
        lastMax = max;
        boolean absolute = "auto".equals(kind) && min >= -0.05f && max <= 1.05f;
        lastScalarMode = absolute ? "alpha (absolute)" : "depth (stretched)";
        float span = max - min;
        boolean flat = span <= 1e-9f;
        for (int i = 0; i < n; i++) {
            float v = i < use ? src[i] : 0f;
            float t = absolute ? v : (flat ? 0f : (v - min) / span);
            int b = (int) (t * 255f + 0.5f);
            out[i] = (byte) (b < 0 ? 0 : (b > 255 ? 255 : b));
        }
    }

    /**
     * A continuous single channel, quantised to 0..255 so it travels through the same byte
     * array the label maps use. Two passes: the range has to be known before anything can
     * be mapped into it.
     *
     * A depth model's output is scaleless — MiDaS returns relative inverse depth whose max
     * varies frame to frame — so it MUST be stretched or the whole map lands in the bottom
     * few codes and reads as blank. A matte must NOT be stretched: an empty frame whose true
     * range is 0.00..0.02 would be amplified into a confident-looking mask made of noise.
     */
    private void decodeScalar(byte[] out) {
        int n = outW * outH;
        outBuf.rewind();
        float min = Float.MAX_VALUE;
        float max = -Float.MAX_VALUE;
        for (int i = 0; i < n; i++) {
            float v = outBuf.getFloat();
            if (v < min) min = v;
            if (v > max) max = v;
        }
        lastMin = min;
        lastMax = max;

        boolean absolute = "alpha".equals(kind)
                || ("auto".equals(kind) && min >= -0.05f && max <= 1.05f);
        lastScalarMode = absolute ? "alpha (absolute)" : "depth (stretched)";

        float span = max - min;
        boolean flat = span <= 1e-9f;
        outBuf.rewind();
        for (int i = 0; i < n; i++) {
            float v = outBuf.getFloat();
            float t = absolute ? v : (flat ? 0f : (v - min) / span);
            int b = (int) (t * 255f + 0.5f);
            out[i] = (byte) (b < 0 ? 0 : (b > 255 ? 255 : b));
        }
    }

    /** e.g. "FLOAT32 [1,257,257,21] 5.5 MB" — the line that identifies a decode mismatch. */
    public String outputSpec() {
        if (canny) return "CANNY " + outW + "x" + outH + " 0/255 edges";
        if (bench) return "BENCH luma " + outW + "x" + outH + " (1-layer stand-in, no tflite)";
        if (compiled != null || compiledPath != null)
            return "FLOAT32 " + outW + "x" + outH + "x" + outC + " NCHW litert-gpu";
        if (outBuf == null) return "n/a";
        int n = Math.max(1, outW * outH);
        return outType + " " + outW + "x" + outH + "x" + outC
                + (outNchw ? " NCHW" : " NHWC")
                + " " + outBuf.capacity() + " bytes, " + (outBuf.capacity() / n) + " per pixel";
    }

    private void decodeLabels(byte[] labelsOut) {
        outBuf.rewind();
        int n = outW * outH;
        // One channel means the argmax is already done and this is a label map. The
        // integer WIDTH has to match: DeepLab's exported ArgMax is INT64, and reading it
        // a byte at a time yields a label followed by seven zeros, which decodes as
        // "everything is background" — indistinguishable from a model that found nothing.
        if (outC <= 1) {
            int stride = outBuf.remaining() / Math.max(1, n);
            for (int i = 0; i < n; i++) {
                if (outType == DataType.FLOAT32) {
                    // Only reachable with kind forced to "labels"; otherwise a float channel
                    // goes to decodeScalar. Treat it as a foreground probability.
                    labelsOut[i] = (byte) (outBuf.getFloat() > 0.5f ? 15 : 0);
                } else if (outType == DataType.INT64 || stride == 8) {
                    labelsOut[i] = (byte) outBuf.getLong();
                } else if (outType == DataType.INT32 || stride == 4) {
                    labelsOut[i] = (byte) outBuf.getInt();
                } else {
                    labelsOut[i] = outBuf.get();
                }
            }
            return;
        }

        if (outType == DataType.FLOAT32) {
            if (outNchw) {
                // Absolute indexing, because the values for one pixel are outW*outH floats
                // apart rather than adjacent.
                for (int i = 0; i < n; i++) {
                    int best = 0;
                    float bestV = -Float.MAX_VALUE;
                    for (int c = 0; c < outC; c++) {
                        float v = outBuf.getFloat((c * n + i) * 4);
                        if (v > bestV) { bestV = v; best = c; }
                    }
                    labelsOut[i] = (byte) best;
                }
                return;
            }
            for (int i = 0; i < n; i++) {
                int best = 0;
                float bestV = -Float.MAX_VALUE;
                for (int c = 0; c < outC; c++) {
                    float v = outBuf.getFloat();
                    if (v > bestV) { bestV = v; best = c; }
                }
                labelsOut[i] = (byte) best;
            }
            return;
        }

        for (int i = 0; i < n; i++) {
            int best = 0;
            int bestV = Integer.MIN_VALUE;
            for (int c = 0; c < outC; c++) {
                int v = (outType == DataType.INT32) ? outBuf.getInt() : (outBuf.get() & 0xff);
                if (v > bestV) { bestV = v; best = c; }
            }
            labelsOut[i] = (byte) best;
        }
    }

    /** Rec.601 luma. The 1x1 conv stand-in for latency: same 1024² I/O as DIS, no weights. */
    private void runBenchLuma(byte[] rgb, byte[] out) {
        int n = outW * outH;
        float min = Float.MAX_VALUE, max = -Float.MAX_VALUE;
        for (int i = 0, p = 0; i < n; i++, p += 3) {
            int y = ((rgb[p] & 0xff) * 77 + (rgb[p + 1] & 0xff) * 150
                    + (rgb[p + 2] & 0xff) * 29) >> 8;
            out[i] = (byte) y;
            float v = y * (1f / 255f);
            if (v < min) min = v;
            if (v > max) max = v;
        }
        lastMin = min;
        lastMax = max;
        lastScalarMode = "alpha (bench luma)";
    }

    /**
     * Gaussian 5-tap, Sobel, non-max suppression, hysteresis 80/160 — same numbers as
     * the desk webcam. Magnitude is the unscaled L1 Sobel (OpenCV's default). The first
     * version packed it into 8 bits and shifted off 3, which put every real camera edge
     * below the floor: 18 ms of work, 0 painted pixels.
     */
    private void runCanny(byte[] rgb, byte[] out) {
        int w = inW, h = inH, n = w * h;
        for (int i = 0, p = 0; i < n; i++, p += 3) {
            cGray[i] = ((rgb[p] & 0xff) * 77 + (rgb[p + 1] & 0xff) * 150
                    + (rgb[p + 2] & 0xff) * 29) >> 8;
        }

        // Separable [1 4 6 4 1] / 16, clamp to edge.
        for (int y = 0; y < h; y++) {
            int row = y * w;
            for (int x = 0; x < w; x++) {
                int x0 = x < 2 ? 0 : x - 2, x1 = x < 1 ? 0 : x - 1;
                int x3 = x + 1 >= w ? w - 1 : x + 1, x4 = x + 2 >= w ? w - 1 : x + 2;
                cBlur[row + x] = (cGray[row + x0] + (cGray[row + x1] << 2) + cGray[row + x] * 6
                        + (cGray[row + x3] << 2) + cGray[row + x4] + 8) >> 4;
            }
        }
        for (int x = 0; x < w; x++) {
            for (int y = 0; y < h; y++) {
                int y0 = y < 2 ? 0 : y - 2, y1 = y < 1 ? 0 : y - 1;
                int y3 = y + 1 >= h ? h - 1 : y + 1, y4 = y + 2 >= h ? h - 1 : y + 2;
                int s = cBlur[y0 * w + x] + (cBlur[y1 * w + x] << 2) + cBlur[y * w + x] * 6
                        + (cBlur[y3 * w + x] << 2) + cBlur[y4 * w + x];
                cGray[y * w + x] = (s + 8) >> 4;
            }
        }

        for (int y = 1; y < h - 1; y++) {
            for (int x = 1; x < w - 1; x++) {
                int i = y * w + x;
                int gx = -cGray[i - w - 1] + cGray[i - w + 1]
                        - (cGray[i - 1] << 1) + (cGray[i + 1] << 1)
                        - cGray[i + w - 1] + cGray[i + w + 1];
                int gy = -cGray[i - w - 1] - (cGray[i - w] << 1) - cGray[i - w + 1]
                        + cGray[i + w - 1] + (cGray[i + w] << 1) + cGray[i + w + 1];
                // Full L1, not >> 3: OpenCV's 80/160 thresholds are on this scale.
                // Max is ~8*255, so it fits next to a 2-bit direction in the high bits.
                int mag = Math.abs(gx) + Math.abs(gy);
                int agx = Math.abs(gx), agy = Math.abs(gy);
                int dir; // 0=E/W, 1=NE/SW, 2=N/S, 3=NW/SE
                if (agx > (agy + agy + agy) / 2) dir = 0;
                else if (agy > (agx + agx + agx) / 2) dir = 2;
                else dir = (gx ^ gy) >= 0 ? 1 : 3;
                cMag[i] = mag | (dir << 16);
            }
        }

        final int hi = 160, lo = 80;
        int sp = 0;
        for (int i = 0; i < n; i++) cNms[i] = 0;
        for (int y = 1; y < h - 1; y++) {
            for (int x = 1; x < w - 1; x++) {
                int i = y * w + x;
                int mag = cMag[i] & 0xffff;
                int dir = cMag[i] >> 16;
                int a, b;
                if (dir == 0) { a = cMag[i - 1] & 0xffff; b = cMag[i + 1] & 0xffff; }
                else if (dir == 1) { a = cMag[i - w + 1] & 0xffff; b = cMag[i + w - 1] & 0xffff; }
                else if (dir == 2) { a = cMag[i - w] & 0xffff; b = cMag[i + w] & 0xffff; }
                else { a = cMag[i - w - 1] & 0xffff; b = cMag[i + w + 1] & 0xffff; }
                if (mag >= a && mag >= b && mag >= lo) {
                    if (mag >= hi) {
                        cNms[i] = 2;
                        cStack[sp++] = i;
                    } else {
                        cNms[i] = 1;
                    }
                }
            }
        }
        while (sp > 0) {
            int i = cStack[--sp];
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++) {
                int ny = y + dy;
                if (ny <= 0 || ny >= h - 1) continue;
                for (int dx = -1; dx <= 1; dx++) {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx;
                    if (nx <= 0 || nx >= w - 1) continue;
                    int j = ny * w + nx;
                    if (cNms[j] == 1) {
                        cNms[j] = 2;
                        cStack[sp++] = j;
                    }
                }
            }
        }
        // 3x3 dilate so a 1-wide ridge survives bilinear sampling of the 480 mask on
        // a 1080p feed. The desk overlay paints native camera pixels; this one does not.
        int edges = 0;
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                boolean hit = false;
                for (int dy = -1; dy <= 1 && !hit; dy++) {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++) {
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        if (cNms[ny * w + nx] == 2) { hit = true; break; }
                    }
                }
                if (hit) { out[y * w + x] = (byte) 255; edges++; }
                else out[y * w + x] = 0;
            }
        }
        lastMin = 0f;
        lastMax = edges > 0 ? 1f : 0f;
        lastScalarMode = "alpha (canny) " + edges + " px";
    }

    public void close() {
        // Stop the worker BEFORE the interpreter goes away, or it runs inference against
        // a closed native handle and takes the process with it.
        Thread w;
        synchronized (lock) {
            running = false;
            hasPending = false;
            hasGlPending = false;
            glFrameDone = false;
            glPath = false;
            inFlight = false;
            readyLabels = null;
            w = worker;
            worker = null;
            lock.notifyAll();
        }
        if (w != null) {
            try {
                // A 1024² graph can sit in interpreter.run for many seconds. Closing the
                // native handle under it is a SIGSEGV, not a Java exception — that is the
                // crash that killed the process on the HUD's model-3 tap.
                w.join(30_000);
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            }
        }

        if (interpreter != null) {
            interpreter.close();
            interpreter = null;
        }
        if (compiled != null) {
            try { compiled.close(); } catch (Throwable ignored) { }
            compiled = null;
        }
        if (compiledInBufs != null) {
            for (TensorBuffer b : compiledInBufs)
                try { b.close(); } catch (Throwable ignored) { }
            compiledInBufs = null;
        }
        if (compiledOutBufs != null) {
            for (TensorBuffer b : compiledOutBufs)
                try { b.close(); } catch (Throwable ignored) { }
            compiledOutBufs = null;
        }
        compiledIn = null;
        compiledPath = null;
        if (nativeLoaded) {
            try { nativeGlClose(); } catch (Throwable ignored) { }
        }
        inBuf = null;
        outBuf = null;
        pending = null;
        canny = false;
        bench = false;
        cGray = cBlur = cMag = cNms = cStack = null;
    }
}
