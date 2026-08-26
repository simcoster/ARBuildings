package com.pavel.arbuildings;

import org.tensorflow.lite.DataType;
import org.tensorflow.lite.Interpreter;
import org.tensorflow.lite.Tensor;
import org.tensorflow.lite.nnapi.NnApiDelegate;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * NNAPI / ENN wrapper for a single-input image segmenter (or classifier).
 *
 * npuOnly=true sets useNnapiCpu(false) and accelerator "enn". If ENN rejects the
 * graph the Interpreter constructor throws and we report REJECT — the same
 * CPU_DISABLED trap documented in tools/encoder_bench. Never silently fall back.
 */
public final class NpuSegmenter {
    private Interpreter interpreter;
    private NnApiDelegate nnapi;
    private String lastError = "";
    private String ep = "none";
    private float lastMs = -1f;

    private int inH, inW, inC;
    private int outH, outW, outC;
    private boolean nchw;
    private DataType inType = DataType.UINT8;
    private DataType outType = DataType.FLOAT32;
    private ByteBuffer inBuf;
    private ByteBuffer outBuf;

    public String lastError() { return lastError; }
    public String ep() { return ep; }
    public float lastInferenceMs() { return lastMs; }
    public int inputWidth() { return inW; }
    public int inputHeight() { return inH; }
    public int outputWidth() { return outW; }
    public int outputHeight() { return outH; }
    public int outputChannels() { return outC; }
    public boolean ready() { return interpreter != null; }

    public boolean loadBytes(byte[] model, boolean npuOnly) {
        close();
        if (model == null || model.length == 0) {
            lastError = "empty model bytes";
            ep = "REJECT";
            return false;
        }
        try {
            NnApiDelegate.Options opt = new NnApiDelegate.Options();
            opt.setAcceleratorName("enn");
            opt.setUseNnapiCpu(!npuOnly);
            nnapi = new NnApiDelegate(opt);

            Interpreter.Options iopt = new Interpreter.Options();
            iopt.addDelegate(nnapi);
            iopt.setNumThreads(1);

            ByteBuffer modelBuf = ByteBuffer.allocateDirect(model.length);
            modelBuf.order(ByteOrder.nativeOrder());
            modelBuf.put(model).rewind();

            interpreter = new Interpreter(modelBuf, iopt);
            describe();
            ep = npuOnly ? "enn" : "nnapi-hybrid";
            lastError = "";
            return true;
        } catch (Exception e) {
            lastError = e.getClass().getSimpleName() + ": " + e.getMessage();
            ep = "REJECT";
            close();
            return false;
        }
    }

    private void describe() {
        Tensor in = interpreter.getInputTensor(0);
        Tensor out = interpreter.getOutputTensor(0);
        inType = in.dataType();
        outType = out.dataType();
        int[] ish = in.shape();
        int[] osh = out.shape();
        // [1,H,W,C] NHWC or [1,C,H,W] NCHW
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
            outH = osh[1];
            outW = osh[2];
            outC = osh[3];
        } else if (osh.length == 4) {
            outC = osh[1];
            outH = osh[2];
            outW = osh[3];
        } else if (osh.length == 3) {
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
     * rgb is packed RGB24, length inW*inH*3, row-major.
     * labelsOut is outW*outH class ids (argmax already applied).
     */
    public boolean infer(byte[] rgb, int[] labelsOut) {
        if (interpreter == null) {
            lastError = "not loaded";
            return false;
        }
        if (rgb == null || rgb.length < inW * inH * 3) {
            lastError = "rgb too small";
            return false;
        }
        if (labelsOut == null || labelsOut.length < outW * outH) {
            lastError = "labels too small";
            return false;
        }

        fillInput(rgb);
        long t0 = System.nanoTime();
        try {
            interpreter.run(inBuf, outBuf);
        } catch (Exception e) {
            lastError = "run: " + e.getMessage();
            return false;
        }
        lastMs = (System.nanoTime() - t0) / 1e6f;
        decodeLabels(labelsOut);
        return true;
    }

    private void fillInput(byte[] rgb) {
        inBuf.rewind();
        if (inType == DataType.UINT8 || inType == DataType.INT8) {
            if (!nchw) {
                inBuf.put(rgb, 0, inW * inH * 3);
            } else {
                int n = inW * inH;
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < n; i++)
                        inBuf.put(rgb[i * 3 + c]);
            }
        } else {
            // float32, 0..1
            if (!nchw) {
                for (int i = 0; i < rgb.length; i++)
                    inBuf.putFloat((rgb[i] & 0xff) / 255f);
            } else {
                int n = inW * inH;
                for (int c = 0; c < 3; c++)
                    for (int i = 0; i < n; i++)
                        inBuf.putFloat((rgb[i * 3 + c] & 0xff) / 255f);
            }
        }
        inBuf.rewind();
    }

    private void decodeLabels(int[] labelsOut) {
        outBuf.rewind();
        int n = outW * outH;
        if (outC <= 1) {
            for (int i = 0; i < n; i++) {
                if (outType == DataType.FLOAT32) {
                    float v = outBuf.getFloat();
                    labelsOut[i] = v > 0.5f ? 15 : 0; // person vs stuff
                } else if (outType == DataType.INT32) {
                    labelsOut[i] = outBuf.getInt();
                } else {
                    labelsOut[i] = outBuf.get() & 0xff;
                }
            }
            return;
        }

        if (outType == DataType.FLOAT32) {
            float[] row = new float[outC];
            for (int i = 0; i < n; i++) {
                int best = 0;
                float bestV = -Float.MAX_VALUE;
                for (int c = 0; c < outC; c++) {
                    row[c] = outBuf.getFloat();
                    if (row[c] > bestV) { bestV = row[c]; best = c; }
                }
                labelsOut[i] = best;
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
            labelsOut[i] = best;
        }
    }

    public void close() {
        if (interpreter != null) {
            interpreter.close();
            interpreter = null;
        }
        if (nnapi != null) {
            nnapi.close();
            nnapi = null;
        }
        inBuf = null;
        outBuf = null;
    }
}
