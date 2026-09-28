using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using UnityEngine;

public static partial class Gemini {

    private const int FrameBytes = 640;
    private const int RawLogInterval = 500;

    private static readonly object micGate = new object();
    private static byte[] micAccum = new byte[FrameBytes * 4];
    private static int micAccumLen;

    private static int rawFrames;
    private static int rawMin = int.MaxValue;
    private static int rawMax;

    private static void ResetMicAccumulator() {
        lock (micGate) micAccumLen = 0;
    }

    private static void sendTick(byte[] data) {
        if (data == null || data.Length == 0) return;

        rawFrames++;
        if (data.Length < rawMin) rawMin = data.Length;
        if (data.Length > rawMax) rawMax = data.Length;
        if (rawFrames % RawLogInterval == 0) {
            Debug.Log($"[Gemini] mic in: {rawFrames} frames, raw {rawMin}-{rawMax} B (~{rawMin / 32f:0.#}-{rawMax / 32f:0.#} ms); sending fixed {FrameBytes} B (~{FrameBytes / 32f:0.#} ms)");
            rawMin = int.MaxValue;
            rawMax = 0;
        }

        lock (micGate) {
            int needed = micAccumLen + data.Length;
            if (needed > micAccum.Length)
                Array.Resize(ref micAccum, Math.Max(micAccum.Length * 2, needed));
            Buffer.BlockCopy(data, 0, micAccum, micAccumLen, data.Length);
            micAccumLen = needed;

            int off = 0;
            while (micAccumLen - off >= FrameBytes) {
                var frame = new byte[FrameBytes];
                Buffer.BlockCopy(micAccum, off, frame, 0, FrameBytes);
                off += FrameBytes;
                sendQueue.Enqueue(frame);
                while (sendQueue.Count > MaxQueuedFrames && sendQueue.TryDequeue(out _)) { }
                sendSignal?.Release();
            }

            if (off == 0) return;
            micAccumLen -= off;
            if (micAccumLen > 0) Buffer.BlockCopy(micAccum, off, micAccum, 0, micAccumLen);
        }
    }

    public static async Task<bool> InjectPromptAsync(string text, CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = liveSession;
        if (s == null || _status != GeminiStatus.Live || !setupCompleted) {
            Debug.LogWarning("[Gemini][inject] no live session; prompt dropped.");
            return false;
        }

        injecting = true;
        try {
            ResetMicAccumulator();
            while (sendQueue.TryDequeue(out _)) { }

            NoteProtocol($"-> clientContent {text.Length}B (turnComplete=true)");
            await s.SendClientContentAsync(new LiveSendClientContentParameters {
                Turns = new List<Content> {
                    new Content {
                        Role = "user",
                        Parts = new List<Part> { new Part { Text = text } }
                    }
                },
                TurnComplete = true
            }).ConfigureAwait(false);

            turnPending = true;
            return true;
        }
        catch (Exception e) {
            Debug.LogWarning($"[Gemini][inject] failed: {e.Message}");
            return false;
        }
        finally {
            injecting = false;
        }
    }

    public static async Task InjectClipAsync(byte[] pcm16kMono, CancellationToken token = default) {
        if (pcm16kMono == null || pcm16kMono.Length == 0) return;
        injecting = true;
        try {
            ResetMicAccumulator();
            while (sendQueue.TryDequeue(out _)) { }
            for (int off = 0; off < pcm16kMono.Length; off += FrameBytes) {
                if (token.IsCancellationRequested) break;
                int len = Math.Min(FrameBytes, pcm16kMono.Length - off);
                var frame = new byte[len];
                Array.Copy(pcm16kMono, off, frame, 0, len);
                sendQueue.Enqueue(frame);
                while (sendQueue.Count > MaxQueuedFrames && sendQueue.TryDequeue(out _)) { }
                sendSignal?.Release();
                try { await Task.Delay(20, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            while (sendQueue.Count > 0 && !token.IsCancellationRequested) {
                try { await Task.Delay(10, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            var s = liveSession;
            if (s != null) {
                try {
                    NoteProtocol("audioStreamEnd");
                    await s.SendRealtimeInputAsync(new LiveSendRealtimeInputParameters {
                        AudioStreamEnd = true
                    }).ConfigureAwait(false);
                    turnPending = true;
                }
                catch (Exception e) {
                    Debug.LogWarning($"[Gemini] audioStreamEnd failed: {e.Message}");
                }
            }
        }
        finally {
            injecting = false;
        }
    }

    private static async Task SendPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try { await sendSignal.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (s != liveSession) return;
            while (sendQueue.TryDequeue(out byte[] data)) {
                if (s != liveSession) return;
                if (!setupCompleted || _status != GeminiStatus.Live) continue;
                try {
                    await s.SendRealtimeInputAsync(new LiveSendRealtimeInputParameters {
                        Audio = new Blob {
                            MimeType = "audio/pcm;rate=16000",
                            Data = data
                        }
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) {
                    Debug.LogError($"[Gemini] send failed: {e.Message}");
                    return;
                }
            }
        }
    }

    private static bool IdleForPush() =>
        keepAlive && setupCompleted && _status == GeminiStatus.Live && !Busy;

    private static async Task ActionPushPump(AsyncSession s, CancellationToken token) {
        while (!token.IsCancellationRequested) {
            if (!StateChannel.HasPending || !keepAlive) {
                try { await actionSignal.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            if (s != liveSession) return;
            if (!keepAlive) continue;

            int waited = 0;
            while (!IdleForPush() && waited < 30000) {
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (s != liveSession) return;
                waited += 100;
            }
            if (!IdleForPush()) continue;

            string text = null;
            try {
                await MainThread.Run(() => {
                    if (StateChannel.TryTakeBatch(out string batch)) text = batch;
                }).ConfigureAwait(false);
            }
            catch (Exception e) {
                Debug.LogWarning($"[Gemini] push could not gather: {e.Message}");
                continue;
            }
            if (string.IsNullOrEmpty(text)) continue;

            try {
                NoteProtocol($"-> clientContent {text.Length}B (turnComplete=false)");
                Debug.Log($"[Gemini][push] {text.Replace('\n', ' ')}");
                await s.SendClientContentAsync(new LiveSendClientContentParameters {
                    Turns = new List<Content> {
                        new Content {
                            Role = "user",
                            Parts = new List<Part> { new Part { Text = text } }
                        }
                    },
                    TurnComplete = false
                }).ConfigureAwait(false);
            }
            catch (Exception e) {
                Debug.LogWarning($"[Gemini][push] failed: {e.Message}");
            }
        }
    }
}
