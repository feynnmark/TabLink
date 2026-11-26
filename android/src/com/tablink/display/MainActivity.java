package com.tablink.display;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.*;
import android.os.*;
import android.util.Log;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.net.*;
import java.util.Locale;

/** Native USB display receiver. All transport is localhost through adb reverse. */
public final class MainActivity extends Activity {
    private static final int BG = Color.rgb(16, 23, 25);
    private static final int FG = Color.rgb(239, 246, 244);
    private static final int MUTED = Color.rgb(176, 195, 191);
    private static final int ACCENT = Color.rgb(94, 234, 212);
    private final Object writeLock = new Object();
    private volatile Socket socket;
    private volatile BufferedWriter writer;
    private volatile int generation;
    private volatile boolean active;
    private boolean touchEnabled = true;
    private volatile boolean connected;
    private String token = "";
    private DesktopSurface desktop;
    private LinearLayout welcome;
    private TextView status;
    private TextView stats;
    private Button menu;
    private int frameCount;
    private long fpsSince;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        WindowManager.LayoutParams windowParams = getWindow().getAttributes();
        float preferred=60f;
        Display.Mode currentMode=getWindowManager().getDefaultDisplay().getMode();
        for(Display.Mode available:getWindowManager().getDefaultDisplay().getSupportedModes())if(available.getPhysicalWidth()==currentMode.getPhysicalWidth() && available.getPhysicalHeight()==currentMode.getPhysicalHeight())preferred=Math.min(120f,Math.max(preferred,available.getRefreshRate()));
        windowParams.preferredRefreshRate = preferred;
        for(Display.Mode mode:getWindowManager().getDefaultDisplay().getSupportedModes()) {
            if(mode.getPhysicalWidth()==currentMode.getPhysicalWidth() && mode.getPhysicalHeight()==currentMode.getPhysicalHeight() && Math.abs(mode.getRefreshRate()-preferred)<0.5f){windowParams.preferredDisplayModeId=mode.getModeId();break;}
        }
        getWindow().setAttributes(windowParams);
        getWindow().getDecorView().setSystemUiVisibility(5894);
        token = getPreferences(0).getString("token", "");
        acceptIntent(getIntent());
        FrameLayout root = new FrameLayout(this);
        if(Build.VERSION.SDK_INT>=35)root.setRequestedFrameRate(getWindowManager().getDefaultDisplay().getRefreshRate());
        root.setBackgroundColor(BG);
        desktop = new DesktopSurface();
        root.addView(desktop, new FrameLayout.LayoutParams(-1, -1));
        root.addOnLayoutChangeListener((view,l,t,r,b,oldL,oldT,oldR,oldB) -> {
            if(r-l!=oldR-oldL || b-t!=oldB-oldT)desktop.fitVideo();
        });

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        welcome = new LinearLayout(this);
        welcome.setOrientation(LinearLayout.VERTICAL);
        welcome.setGravity(Gravity.CENTER_VERTICAL);
        welcome.setPadding(dp(48), dp(32), dp(48), dp(32));
        welcome.setBackgroundColor(BG);
        scroll.addView(welcome, new ScrollView.LayoutParams(-1, -1));
        root.addView(scroll, new FrameLayout.LayoutParams(-1, -1));
        TextView brand = text("TABLINK  /  PANTALLA POR USB", 14, ACCENT);
        brand.setLetterSpacing(0.10f);
        welcome.addView(brand);
        TextView title = text("Más espacio para trabajar.", 32, FG);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(-1, -2);
        titleParams.topMargin = dp(16);
        welcome.addView(title, titleParams);
        status = text("Conecta el cable USB y abre TabLink en Windows.", 18, MUTED);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        LinearLayout.LayoutParams statusParams = new LinearLayout.LayoutParams(-1, -2);
        statusParams.topMargin = dp(16);
        welcome.addView(status, statusParams);
        TextView steps = text("1. Deja activada la depuración USB.\n2. Autoriza este PC cuando Android lo solicite.\n3. TabLink conecta automáticamente al enchufar el cable.", 16, FG);
        steps.setLineSpacing(dp(8), 1.1f);
        LinearLayout.LayoutParams stepsParams = new LinearLayout.LayoutParams(-1, -2);
        stepsParams.topMargin = dp(28);
        welcome.addView(steps, stepsParams);
        Button retry = new Button(this);
        retry.setText("Volver a conectar");
        retry.setTextColor(BG);
        retry.setBackgroundTintList(android.content.res.ColorStateList.valueOf(ACCENT));
        retry.setMinHeight(dp(52));
        retry.setOnClickListener(v -> restart());
        LinearLayout.LayoutParams retryParams = new LinearLayout.LayoutParams(dp(240), -2);
        retryParams.topMargin = dp(24);
        welcome.addView(retry, retryParams);
        stats = text("", 13, FG);
        stats.setPadding(dp(12), dp(8), dp(12), dp(8));
        stats.setBackgroundColor(0xcc101719);
        stats.setVisibility(View.GONE);
        FrameLayout.LayoutParams statsParams = new FrameLayout.LayoutParams(-2, -2, Gravity.TOP | Gravity.LEFT);
        statsParams.setMargins(dp(12), dp(12), dp(12), dp(12));
        root.addView(stats, statsParams);
        menu = new Button(this);
        menu.setText("Menú");
        menu.setTextSize(13);
        menu.setTextColor(FG);
        menu.setBackgroundTintList(android.content.res.ColorStateList.valueOf(0xcc101719));
        menu.setContentDescription("Controles de la pantalla USB");
        menu.setOnClickListener(v -> showMenu());
        FrameLayout.LayoutParams menuParams = new FrameLayout.LayoutParams(dp(88), dp(52), Gravity.TOP | Gravity.RIGHT);
        menuParams.setMargins(dp(12), dp(8), dp(12), dp(8));
        root.addView(menu, menuParams);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                Insets safe = insets.getInsets(WindowInsets.Type.displayCutout());
                view.setPadding(safe.left, safe.top, safe.right, safe.bottom);
            }
            return insets;
        });
        setContentView(root);
        // The scroll container is hidden as soon as the first real frame arrives.
        welcome.setTag(scroll);
    }

    private TextView text(String value, float size, int color) {
        TextView view = new TextView(this);
        view.setText(value); view.setTextSize(size); view.setTextColor(color);
        return view;
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private void acceptIntent(Intent intent) {
        String supplied = intent.getStringExtra("token");
        if (supplied != null && supplied.matches("[a-f0-9]{64}")) {
            token = supplied;
            getPreferences(0).edit().putString("token", token).apply();
        }
    }
    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent); setIntent(intent); acceptIntent(intent);
        if (active) restart();
    }
    @Override protected void onStart() { super.onStart(); active = true; restart(); }
    @Override protected void onStop() { active = false; stopConnection(); super.onStop(); }
    @Override public void onWindowFocusChanged(boolean focus) {
        super.onWindowFocusChanged(focus);
        if (focus) getWindow().getDecorView().setSystemUiVisibility(5894);
    }
    private void stopConnection() {
        generation++;
        Socket old = socket; socket = null; writer = null;
        if (old != null) try { old.close(); } catch (IOException ignored) { }
    }
    private void restart() {
        stopConnection();
        if (!active) return;
        int current = generation;
        new Thread(() -> receive(current), "tablink-usb").start();
    }
    private void send(String command) throws IOException {
        synchronized (writeLock) {
            BufferedWriter output = writer;
            if (output == null) throw new IOException("USB desconectado");
            output.write(command); output.write('\n'); output.flush();
        }
    }
    private void disconnected(int current, String message) {
        runOnUiThread(() -> {
            if (current != generation) return;
            connected = false;
            ((View) welcome.getTag()).setVisibility(View.VISIBLE);
            stats.setVisibility(View.GONE);
            status.setText(message);
        });
    }
    private void receive(int current) {
        DeviceProfile profile=null;
        while (active && current == generation) {
            Socket connection = new Socket();
            try {
                if (token.isEmpty()) {
                    disconnected(current, "Abre TabLink en Windows para vincular esta tablet por USB.");
                    Thread.sleep(1000); continue;
                }
                if(profile==null)profile=DeviceProfile.detect(getWindowManager().getDefaultDisplay());
                connection.connect(new InetSocketAddress("127.0.0.1", 27839), 2000);
                connection.setTcpNoDelay(true); connection.setSoTimeout(0);
                if (!active || current != generation) break;
                socket = connection;
                writer = new BufferedWriter(new OutputStreamWriter(connection.getOutputStream(), "US-ASCII"));
                DataInputStream input = new DataInputStream(new BufferedInputStream(connection.getInputStream(), 128 * 1024));
                send("TABLINK/5 " + token+" "+profile.width+" "+profile.height+" "+profile.rate+" "+profile.bits);
                int magic = input.readInt();
                if (magic != 0x544c5635) throw new IOException("Actualiza el compañero de Windows para usar vídeo de alta frecuencia");
                final int videoWidth = input.readInt(), videoHeight = input.readInt(), videoFps = input.readInt(), videoBits=input.readInt();
                if (videoWidth < 1 || videoHeight < 1 || videoWidth > 4096 || videoHeight > 4096 || videoFps < 1 || videoFps > 120 || (videoBits!=8 && videoBits!=10)) throw new IOException("Formato de vídeo no válido");
                while (!desktop.ready && active && current == generation) Thread.sleep(50);
                if (!active || current != generation) break;
                desktop.setVideoSize(videoWidth, videoHeight, videoFps);
                VideoDecoder.play(input, desktop.getHolder().getSurface(), videoWidth, videoHeight, videoFps, videoBits,
                    () -> active && current == generation,
                    () -> { try { connection.close(); } catch (IOException ignored) { } },
                    stamp -> inputHandler.post(() -> {if(current!=generation)return;try{send("ACK "+stamp);}catch(IOException ignored){}}),
                    (fps, decoder) -> runOnUiThread(() -> {
                        if (current != generation) return;
                        if (!connected) {
                            connected = true; ((View) welcome.getTag()).setVisibility(View.GONE);
                            Log.i("TabLink", "FIRST_VIDEO_FRAME " + videoWidth + "x" + videoHeight + " " + decoder);
                        }
                        if (fps > 0) {
                            String measured = String.format(Locale.US, "USB · %d × %d · %.0f fps · %d bits · panel %.0f Hz", videoWidth, videoHeight, fps, videoBits, getWindowManager().getDefaultDisplay().getRefreshRate());
                            stats.setText(measured); Log.i("TabLink", "RENDERED " + measured);
                        }
                    }));
            } catch (Exception failure) {
                if (current == generation && active) {
                    Log.i("TabLink", "Reconnect: " + failure.getClass().getSimpleName() + " " + failure.getMessage());
                    if (failure instanceof android.media.MediaCodec.CodecException) Log.e("TabLink", "Codec configuration failed", failure);
                    disconnected(current, profile==null ? failure.getMessage() : "Esperando a Windows por USB…\nComprueba que TabLink siga abierto en el PC.");
                    try { Thread.sleep(1000); } catch (InterruptedException ignored) { }
                }
            } finally {
                try { connection.close(); } catch (IOException ignored) { }
                if (socket == connection) { socket = null; writer = null; }
            }
        }

    }
    private void showMenu() {
        String[] options = {
            touchEnabled ? "Desactivar táctil de Windows" : "Activar táctil de Windows",
            stats.getVisibility() == View.VISIBLE ? "Ocultar estadísticas" : "Mostrar estadísticas",
            "Volver a conectar", "Cerrar pantalla"
        };
        new AlertDialog.Builder(this).setTitle("TabLink · USB").setItems(options, (dialog, choice) -> {
            if (choice == 0) {
                touchEnabled = !touchEnabled;
                inputHandler.post(() -> { try { send("TOUCH 3 0 0"); } catch (IOException ignored) { } });
                Toast.makeText(this, touchEnabled ? "Control táctil activado" : "Control táctil desactivado", Toast.LENGTH_SHORT).show();
            } else if (choice == 1) stats.setVisibility(stats.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE);
            else if (choice == 2) restart();
            else finish();
        }).setNegativeButton("Volver", null).show();
    }
    private final class DesktopSurface extends SurfaceView implements SurfaceHolder.Callback {
        volatile boolean ready;
        private volatile RectF destination = new RectF();
        private int videoWidth,videoHeight;
        DesktopSurface() {
            super(MainActivity.this);
            if(Build.VERSION.SDK_INT>=35)setRequestedFrameRate(getWindowManager().getDefaultDisplay().getRefreshRate());
            getHolder().addCallback(this);
            setContentDescription("Pantalla de Windows por USB. Activa el control táctil desde Menú para hacer clic y arrastrar.");
        }
        public void surfaceCreated(SurfaceHolder holder) {
            ready = true;
            if (Build.VERSION.SDK_INT >= 30) holder.getSurface().setFrameRate(getWindowManager().getDefaultDisplay().getRefreshRate(), Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
        }
        public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { ready = true; }
        public void surfaceDestroyed(SurfaceHolder holder) { ready = false; }
        void setVideoSize(int width, int height, int fps) {
            runOnUiThread(() -> {videoWidth=width;videoHeight=height;fitVideo();});
            if (Build.VERSION.SDK_INT >= 30) getHolder().getSurface().setFrameRate(fps, Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
        }
        void fitVideo() {
            if(videoWidth<=0 || !(getParent() instanceof View))return;
            View parent=(View)getParent();
            float scale=Math.min((float)parent.getWidth()/videoWidth,(float)parent.getHeight()/videoHeight);
            int w=Math.max(1,Math.round(videoWidth*scale)),h=Math.max(1,Math.round(videoHeight*scale));
            FrameLayout.LayoutParams params=(FrameLayout.LayoutParams)getLayoutParams();
            if(params.width!=w || params.height!=h){params.width=w;params.height=h;params.gravity=Gravity.CENTER;setLayoutParams(params);}
            destination=new RectF(0,0,w,h);
        }
        @Override public boolean onTouchEvent(MotionEvent event) {
            if (!touchEnabled || !connected) return true;
            RectF rect = destination;
            if (rect.width() <= 0 || rect.height() <= 0) return true;
            if (event.getActionMasked() == MotionEvent.ACTION_UP) performClick();
            int count = Math.min(10, event.getPointerCount());
            StringBuilder command = new StringBuilder("TOUCH ").append(event.getActionMasked()).append(' ').append(event.getActionIndex()).append(' ').append(count);
            for (int i=0;i<count;i++) {
                float x = Math.max(0, Math.min(1, (event.getX(i) - rect.left) / rect.width()));
                float y = Math.max(0, Math.min(1, (event.getY(i) - rect.top) / rect.height()));
                command.append(' ').append(event.getPointerId(i)).append(':').append(String.format(Locale.US,"%.6f:%.6f",x,y));
            }
            final String packet = command.toString();
            inputHandler.post(() -> { try { send(packet); } catch (IOException ignored) { } });
            return true;
        }
        @Override public boolean performClick() { super.performClick(); return true; }
    }
    private final HandlerThread inputThread = makeInputThread();
    private final Handler inputHandler = new Handler(inputThread.getLooper());
    private static HandlerThread makeInputThread() {
        HandlerThread thread = new HandlerThread("tablink-touch",android.os.Process.THREAD_PRIORITY_DISPLAY); thread.start(); return thread;
    }
    @Override protected void onDestroy() { stopConnection(); inputThread.quitSafely(); super.onDestroy(); }
}
