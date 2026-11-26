package com.tablink.display;

import android.media.*;
import android.os.*;
import android.util.Log;
import android.view.Surface;
import android.view.Choreographer;
import java.io.*;
import java.nio.ByteBuffer;
import java.util.concurrent.atomic.*;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;

final class VideoDecoder {
    interface Alive { boolean get(); }
    interface Rendered { void frame(double fps, String codec); }
    interface Acknowledge { void presented(long timestamp); }
    static void play(DataInputStream input, Surface surface, int width, int height, int fps, int bits,
                     Alive alive, Runnable abort, Acknowledge acknowledge, Rendered rendered) throws Exception {
        String selected=DeviceProfile.decoder(width,height,fps,bits);
        if(selected==null)throw new IOException("El decodificador no admite este formato de vídeo.");
        MediaCodec codec = MediaCodec.createByCodecName(selected);
        String decoderName = codec.getName();
        MediaFormat format = MediaFormat.createVideoFormat("video/hevc", width, height);
        format.setInteger(MediaFormat.KEY_PROFILE, bits==10?MediaCodecInfo.CodecProfileLevel.HEVCProfileMain10:MediaCodecInfo.CodecProfileLevel.HEVCProfileMain);
        format.setInteger(MediaFormat.KEY_FRAME_RATE, fps);
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4 * 1024 * 1024);
        format.setInteger(MediaFormat.KEY_PRIORITY, 0);
        format.setFloat(MediaFormat.KEY_OPERATING_RATE, fps);
        // Read colour aspects from the encoder's VUI, including its actual RGB-to-YUV matrix.
        if (Build.VERSION.SDK_INT >= 30) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
        AtomicBoolean running = new AtomicBoolean(true);
        ArrayBlockingQueue<Integer> pendingOutput=new ArrayBlockingQueue<>(2);
        AtomicInteger receivedCount=new AtomicInteger(),queuedCount=new AtomicInteger(),decodedCount=new AtomicInteger(),vsyncCount=new AtomicInteger(),compressedDrops=new AtomicInteger(),decodedDrops=new AtomicInteger();
        HandlerThread renderEvents = new HandlerThread("tablink-presented",android.os.Process.THREAD_PRIORITY_DISPLAY); renderEvents.start();
        Thread outputThread = null;
        Thread networkThread = null;
        ArrayBlockingQueue<EncodedFrame> latest = new ArrayBlockingQueue<>(2);
        AtomicReference<Exception> networkFailure = new AtomicReference<>();
        ArrayBlockingQueue<EncodedFrame> free = new ArrayBlockingQueue<>(5);
        for(int i=0;i<4;i++) free.add(new EncodedFrame());
        boolean started = false;
        try {
            codec.configure(format, surface, null, 0);
            codec.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT);
            AtomicLong startTime = new AtomicLong(0);
            AtomicInteger presented = new AtomicInteger(0);
            codec.setOnFrameRenderedListener((decoder, pts, nanoTime) -> {
                if (!running.get() || !alive.get()) return;
                acknowledge.presented(pts);
                long now = System.nanoTime();
                if (startTime.compareAndSet(0, now)) rendered.frame(0, decoderName);
                int count = presented.incrementAndGet();
                long elapsed = now - startTime.get();
                if (elapsed >= 2_000_000_000L) {
                    rendered.frame(count * 1_000_000_000.0 / elapsed, decoderName);
                    double seconds=elapsed/1_000_000_000.0;
                    Log.i("TabLink",String.format(java.util.Locale.US,"TIMING rx=%.1f queued=%.1f decoded=%.1f presented=%.1f vsync=%.1f dropped=%d/%d",receivedCount.getAndSet(0)/seconds,queuedCount.getAndSet(0)/seconds,decodedCount.getAndSet(0)/seconds,count/seconds,vsyncCount.getAndSet(0)/seconds,compressedDrops.getAndSet(0),decodedDrops.getAndSet(0)));
                    presented.set(0); startTime.set(now);
                }
            }, new Handler(renderEvents.getLooper()));
            codec.start(); started = true;
            new Handler(renderEvents.getLooper()).post(() -> {
                Choreographer clock=Choreographer.getInstance();
                clock.postFrameCallback(new Choreographer.FrameCallback(){
                    @Override public void doFrame(long frameTime) {
                        if(!running.get() || !alive.get())return;
                        vsyncCount.incrementAndGet();
                        Integer index=pendingOutput.poll();
                        if(index!=null) {
                            try {codec.releaseOutputBuffer(index,frameTime);}
                            catch(IllegalStateException failure){running.set(false);abort.run();return;}
                        }
                        clock.postFrameCallback(this);
                    }
                });
            });
            Log.i("TabLink", "DECODER " + decoderName + " " + width + "x" + height + " target=" + fps + " HEVC "+bits+"bit VUI low-latency");
            outputThread = new Thread(() -> {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_DISPLAY);
                MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
                try {
                    while (running.get() && alive.get()) {
                        int index = codec.dequeueOutputBuffer(info, 2000);
                        if (index >= 0) {
                            decodedCount.incrementAndGet();
                            if(!pendingOutput.offer(index)) {
                                Integer stale=pendingOutput.poll();
                                if(stale!=null){codec.releaseOutputBuffer(stale,false);decodedDrops.incrementAndGet();}
                                if(!pendingOutput.offer(index)){codec.releaseOutputBuffer(index,false);decodedDrops.incrementAndGet();}
                            }
                        }
                        else if (index == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) Log.i("TabLink", "VIDEO_FORMAT " + codec.getOutputFormat());
                    }
                } catch (Exception failure) {
                    if (running.get() && alive.get()) { Log.e("TabLink", "Decoder output failed", failure); running.set(false); abort.run(); }
                }
            }, "tablink-decoder-output");
            outputThread.start();
            networkThread=new Thread(() -> {
                android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_DISPLAY);
                try {
                    while(running.get() && alive.get()) {
                        int length=input.readInt();long pts=input.readLong();
                        if(length<1 || length>4*1024*1024)throw new IOException("Paquete HEVC no válido");
                        EncodedFrame packet=free.take();
                        if(packet.data.length<length)packet.data=new byte[length];
                        packet.length=length;packet.pts=pts;input.readFully(packet.data,0,length);
                        receivedCount.incrementAndGet();
                        // Two slots absorb a USB scheduling burst; overflow always drops old
                        // independent IDR pictures rather than building an unbounded queue.
                        if(!latest.offer(packet)) {
                            EncodedFrame stale=latest.poll();
                            if(stale!=null){free.offer(stale);compressedDrops.incrementAndGet();}
                            if(!latest.offer(packet)){free.offer(packet);compressedDrops.incrementAndGet();}
                        }
                    }
                }catch(Exception failure){if(running.get() && alive.get())networkFailure.set(failure);running.set(false);}
            },"tablink-latest-frame");
            networkThread.start();
            while (running.get() && alive.get()) {
                int index;
                do { index = codec.dequeueInputBuffer(2000); } while (index < 0 && running.get() && alive.get());
                if (index < 0 || !running.get() || !alive.get()) break;
                EncodedFrame packet;
                while((packet=latest.poll(20,TimeUnit.MILLISECONDS))==null && running.get() && alive.get()) { }
                if(packet==null)break;
                ByteBuffer buffer = codec.getInputBuffer(index);
                if (buffer == null || buffer.capacity() < packet.length) throw new IOException("Búfer de vídeo insuficiente");
                buffer.clear(); buffer.put(packet.data, 0, packet.length);
                codec.queueInputBuffer(index, 0, packet.length, packet.pts, 0);
                queuedCount.incrementAndGet();
                free.offer(packet);
            }
            if(networkFailure.get()!=null && alive.get())throw networkFailure.get();
        } finally {
            running.set(false);
            abort.run();
            if(networkThread!=null){networkThread.interrupt();networkThread.join(1500);}
            if (outputThread != null) outputThread.join(1500);
            if (started) try { codec.stop(); } catch (Exception ignored) { }
            codec.release(); renderEvents.quitSafely();
        }
    }
    static final class EncodedFrame {byte[] data=new byte[512*1024];int length;long pts;}
}
