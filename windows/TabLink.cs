using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TabLink {
    static class Program {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabLink");
        static readonly object LogLock = new object();
        [STAThread] static void Main(string[] args) {
            Directory.CreateDirectory(Data);
            Native.EnablePerMonitorDpi();
            if (args.Contains("--stop")) { try { using (var signal = EventWaitHandle.OpenExisting("Local\\TabLink.StopHost")) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { } return; }
            if (args.Contains("--diagnose")) { File.WriteAllText(Path.Combine(Data, "displays.txt"), Native.Describe()); return; }
            if (args.Contains("--test-window")) { Application.EnableVisualStyles(); Application.Run(new ProofForm()); return; }
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\TabLink.NativeHost", out created)) {
                if (!created) {
                    if(!args.Contains("--tray"))try{using(var show=EventWaitHandle.OpenExisting("Local\\TabLink.ShowHost"))show.Set();}catch(WaitHandleCannotBeOpenedException){}
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new HostForm(args.Contains("--tray")));
            }
        }
        public static void Log(string value) {
            lock (LogLock) {
                string path = Path.Combine(Data, "tablink.log");
                value = Regex.Replace(value, @"\b[a-fA-F0-9]{64}\b", "[redacted]");
                try {
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Copy(path, path + ".previous", true);
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.WriteAllText(path, "");
                    File.AppendAllText(path, DateTime.Now.ToString("s") + " " + value + Environment.NewLine);
                } catch (IOException) { }
            }
        }
        public static string Run(string file, string arguments, int timeout) {
            ProcessStartInfo start = new ProcessStartInfo(file, arguments);
            start.UseShellExecute = false; start.CreateNoWindow = true;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            start.WorkingDirectory = Root;
            using (Process process = Process.Start(start)) {
                // Drain both pipes concurrently so a verbose adb operation cannot deadlock.
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout)) { try { process.Kill(); } catch { } throw new IOException("Tiempo de espera agotado: " + Path.GetFileName(file)); }
                string result = output.Result + error.Result;
                if (process.ExitCode != 0) throw new IOException(Path.GetFileName(file) + ": " + result.Trim());
                return result.Trim();
            }
        }
    }

    sealed partial class HostForm : Form {
        readonly Label state = new Label();
        readonly Label detail = new Label();
        readonly ComboBox mode = new ComboBox();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly object displayLock = new object();
        readonly string adb = Path.Combine(Program.Root, "adb", "adb.exe");

        readonly string token;
        readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        readonly EventWaitHandle stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\TabLink.StopHost");
        readonly EventWaitHandle showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\TabLink.ShowHost");
        TcpListener listener;
        volatile bool running = true;
        volatile bool mirror;
        volatile bool reconnect;
        volatile string serial = "";
        volatile string transport = "";
        int sessions;
        bool ownsMonitor;
        bool capturePrimed;
        bool exitRequested;
        int closing;

        public HostForm(bool startHidden) {
            Text = "TabLink · Pantalla por USB";
            ClientSize = new Size(590, 410); MinimumSize = new Size(570, 440);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(240, 253, 250); ForeColor = Color.FromArgb(19, 78, 74);
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = SystemIcons.Application;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.Controls.Add(new Label { Text = "TabLink", Font = new Font("Segoe UI", 26, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            state.Text = "Buscando tu tablet…"; state.Dock = DockStyle.Fill; state.Font = new Font("Segoe UI Semibold", 16);
            layout.Controls.Add(state, 0, 1);
            detail.Text = "Conecta tu dispositivo Android por USB y autoriza este PC en la tablet."; detail.Dock = DockStyle.Fill;
            layout.Controls.Add(detail, 0, 2);
            mode.DropDownStyle = ComboBoxStyle.DropDownList; mode.Dock = DockStyle.Fill;
            mode.Items.Add("Extender escritorio · pantalla independiente"); mode.Items.Add("Duplicar pantalla principal");
            mode.SelectedIndex = 0;
            mode.SelectedIndexChanged += delegate { mirror = mode.SelectedIndex == 1; CloseClients(); reconnect = true; };
            layout.Controls.Add(mode, 0, 3);
            var connect = new Button { Text = "Conectar tablet", Dock = DockStyle.Fill };
            connect.Click += delegate { reconnect = true; };
            layout.Controls.Add(connect, 0, 4);
            var prepare = new Button { Text = "Preparar pantalla virtual (una vez)", Dock = DockStyle.Fill };
            prepare.Click += delegate { PrepareDriver(); };
            layout.Controls.Add(prepare, 0, 5);
            var startup = new CheckBox { Text = "Abrir al iniciar sesión en Windows", Dock = DockStyle.Fill };
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) startup.Checked = key != null && key.GetValue("TabLink") != null;
            startup.CheckedChanged += delegate {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                    if (startup.Checked) key.SetValue("TabLink", "\"" + Application.ExecutablePath + "\" --tray");
                    else key.DeleteValue("TabLink", false);
                }
            };
            layout.Controls.Add(startup, 0, 6);
            layout.Controls.Add(new Label { Text = "USB local · sin cuenta · sin conexión a Internet", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9) }, 0, 7);
            Controls.Add(layout);
            var context = new ContextMenuStrip();
            context.Items.Add("Abrir TabLink", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            context.Items.Add("Conectar tablet", null, delegate { reconnect = true; });
            context.Items.Add("Configuración de pantallas", null, delegate { Process.Start("ms-settings:display"); });
            context.Items.Add("Cerrar TabLink", null, delegate { exitRequested = true; Close(); });
            tray.Icon = SystemIcons.Application; tray.Text = "TabLink · USB"; tray.ContextMenuStrip = context; tray.Visible = true;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!exitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
                Shutdown();
            };
            string tokenFile = Path.Combine(Program.Data, "pairing.key");
            if (!File.Exists(tokenFile)) {
                byte[] bytes = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                File.WriteAllText(tokenFile, BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant());
            }
            token = File.ReadAllText(tokenFile).Trim();
            // Start from the message loop even when Windows launches us hidden at login.
            IntPtr initializedHandle = Handle;
            BeginInvoke((Action)delegate {
                if (startHidden) Hide();
                ThreadPool.QueueUserWorkItem(delegate { PollUsb(); });
                ThreadPool.QueueUserWorkItem(delegate { Serve(); });
                ThreadPool.QueueUserWorkItem(delegate { stopSignal.WaitOne(); if (running) BeginInvoke((Action)delegate { exitRequested = true; Close(); }); });
                ThreadPool.QueueUserWorkItem(delegate { while(running){showSignal.WaitOne();if(running)BeginInvoke((Action)delegate {Show();WindowState=FormWindowState.Normal;Activate();});} });
            });
            Program.Log("START v0.6.1 " + Native.Describe().Replace(Environment.NewLine, " | "));
        }
        void Status(string title, string description) {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { state.Text = title; detail.Text = description; tray.Text = ("TabLink · " + title).Substring(0, Math.Min(63, ("TabLink · " + title).Length)); }); } catch (InvalidOperationException) { }
        }
        void PrepareDriver() {
            try {
                var info = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + Path.Combine(Program.Root, "Prepare-Driver.ps1") + "\"");
                info.Verb = "runas"; info.UseShellExecute = true; info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
                Status("Preparando pantalla virtual", "Acepta el aviso de administrador de Windows. Después pulsa Conectar tablet.");
            } catch (Exception error) { Status("Preparación pendiente", error.Message); }
        }
        void PollUsb() {
            while (running) {
                try {
                    string list = Program.Run(adb, "devices -l", 6000);
                    string found = "", unauthorized = "", foundTransport="";
                    foreach (string line in list.Split('\n')) {
                        Match match = Regex.Match(line.Trim(), @"^([A-Za-z0-9._-]+)\s+(device|unauthorized)\b(.*)$");
                        if (!match.Success || match.Groups[1].Value.StartsWith("emulator-")) continue;
                        if (match.Groups[2].Value == "unauthorized") { unauthorized = match.Groups[1].Value; continue; }
                        // TCP, mDNS and emulator transports are intentionally excluded.
                        if (match.Groups[1].Value.Contains(".") || match.Groups[1].Value.Contains(":")) continue;
                        {
                            found = match.Groups[1].Value;
                            foundTransport=Regex.Match(match.Groups[3].Value,@"transport_id:(\d+)").Groups[1].Value;
                            // Keep the first authorized USB device. Only one receiver is supported.
                            break;
                        }
                    }
                    if (found.Length == 0) {
                        if (serial.Length != 0) { Program.Log("USB disconnected"); CloseClients(); serial = ""; transport=""; }
                        if(Volatile.Read(ref sessions)==0 && File.Exists(Path.Combine(Program.Data,"owns-driver-v2.flag"))) {
                            string active=Native.VirtualDisplay();
                            if(active!=null && Native.IsModernVdd(active)){ownsMonitor=true;ReleaseMonitor();}
                        }
                        Status(unauthorized.Length > 0 ? "Autoriza la conexión USB" : "Esperando tu tablet", unauthorized.Length > 0 ? "Desbloquea la tablet y acepta Permitir depuración USB, con Permitir siempre." : "Conecta el cable USB. TabLink se abrirá automáticamente en la tablet.");
                    } else if (found != serial || foundTransport!=transport || reconnect) {
                        reconnect = false; serial = found; transport=foundTransport;
                        string installed = Program.Run(adb, "-s " + serial + " shell pm list packages com.tablink.display", 5000);
                        bool updateRequired=!installed.Contains("package:");
                        if(!updateRequired) {
                            string packageInfo=Program.Run(adb,"-s "+serial+" shell dumpsys package com.tablink.display",5000);
                            Match version=Regex.Match(packageInfo,@"versionCode=(\d+)");
                            updateRequired=!version.Success || int.Parse(version.Groups[1].Value)<7;
                        }
                        if (updateRequired) {
                            Status("Instalando app Android", "La app nativa se está copiando a la tablet por USB.");
                            Program.Run(adb, "-s " + serial + " install -r \"" + Path.Combine(Program.Root, "TabLink.apk") + "\"", 60000);
                        }
                        Program.Run(adb, "-s " + serial + " reverse tcp:27839 tcp:27839", 5000);
                        Program.Run(adb, "-s " + serial + " shell am start -n com.tablink.display/.MainActivity --es token " + token, 10000);
                        Program.Log("USB paired");
                        if (Volatile.Read(ref sessions) == 0) Status("Tablet conectada por USB", "Abriendo la pantalla en la tablet…");
                    }
                } catch (Exception error) { Program.Log("USB " + error.Message); serial = ""; Status("Comprueba el cable USB", error.Message); }
                for (int i = 0; i < 20 && running; i++) Thread.Sleep(100);
            }
        }
        void Serve() {
            try {
                listener = new TcpListener(IPAddress.Loopback, 27839); listener.Start(4);
                while (running) {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (clients) clients.Add(client);
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
            } catch (Exception error) { if (running) { Program.Log("SERVER " + error.Message); Status("No se pudo abrir TabLink", error.Message); } }
        }
        bool EnsureMonitor(StreamProfile requested) {
            lock (displayLock) {
                string existing = Native.VirtualDisplay();
                if (!File.Exists(Path.Combine(Program.Data,"owns-driver-v2.flag"))) return false;
                if (existing != null) {
                    ownsMonitor = true; Native.PrepareModernVdd(existing,requested); return true;
                }
                if (!Native.HasModernVdd) return false;
                try {
                    Program.Log("Creating virtual monitor");
                    Native.SwitchMonitor(true,requested);
                    capturePrimed=false;
                    ownsMonitor = true;
                    for (int i = 0; i < 40 && running; i++) {
                        string name = Native.VirtualDisplay();
                        if (name != null) {
                            ownsMonitor = true;
                            Native.PrepareModernVdd(name,requested);
                            Program.Log("MONITOR ready " + Native.Describe().Replace(Environment.NewLine, " | "));
                            Thread.Sleep(500);
                            return true;
                        }
                        Thread.Sleep(150);
                    }
                } catch (Exception error) { Program.Log("MONITOR " + error.Message); }
                return false;
            }
        }
        void ReleaseMonitor() {
            lock (displayLock) {
                if(running && Volatile.Read(ref sessions)>0)return;
                if (!ownsMonitor) return;
                try { Native.SwitchMonitor(false); Program.Log("MONITOR released"); } catch (Exception error) { Program.Log(error.Message); }
                ownsMonitor = false;
            }
        }
        void HandleClient(TcpClient client) {
            bool authenticated=false;
            try {
                client.NoDelay=true;client.ReceiveTimeout=8000;client.SendTimeout=5000;
                NetworkStream stream=client.GetStream();
                StreamProfile requested=StreamProfile.Parse(ReadLine(stream,160),token);
                if(requested==null){Program.Log("Rejected unauthenticated or unsupported connection");return;}
                if(Interlocked.CompareExchange(ref sessions,1,0)!=0)return;
                authenticated=true;HandleVideo(client,stream,requested);
            }catch(Exception error){if(running){Program.Log("SESSION "+error.Message);Status("No se pudo iniciar el vídeo",error.Message);}}
            finally {
                lock(clients)clients.Remove(client);client.Close();
                if(authenticated){
                    Interlocked.Exchange(ref sessions,0);
                    Program.Log("SESSION end");
                    ThreadPool.QueueUserWorkItem(delegate{Thread.Sleep(2500);if(Volatile.Read(ref sessions)==0)ReleaseMonitor();});
                }
            }
        }
        static string ReadLine(Stream stream, int max) {
            StringBuilder value = new StringBuilder();
            for (int i = 0; i <= max; i++) {
                int b = stream.ReadByte(); if (b == -1) return null;
                if (b == 10) return value.ToString();
                if (b < 32 || b > 126) throw new IOException("Invalid protocol character");
                value.Append((char)b);
            }
            throw new IOException("Protocol line too long");
        }
        static void WriteInt(Stream stream, int number) { byte[] bytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(number)); stream.Write(bytes, 0, 4); }
        void CloseClients() { lock (clients) foreach (TcpClient client in clients.ToArray()) try { client.Close(); } catch { } }
        void Shutdown() {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            running = false; CloseClients(); if (listener != null) listener.Stop();
            ReleaseMonitor(); tray.Visible = false; tray.Dispose();
            Program.Log("STOP");
        }
    }

    static partial class Native {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DisplayDevice {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice display, uint flags);
        public static string VirtualDisplay() {
            for (uint i = 0; i < 64; i++) {
                DisplayDevice d = new DisplayDevice(); d.cb = Marshal.SizeOf(d);
                if (!EnumDisplayDevices(null, i, ref d, 0)) break;
                if ((d.StateFlags & 1) != 0 && d.DeviceString == "Virtual Display Driver") return d.DeviceName;
            }
            return null;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DevMode {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DevMode settings);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DevMode mode, IntPtr hwnd, uint flags, IntPtr param);
        public static Rectangle DisplayBounds(string name) {
            DevMode mode = new DevMode(); mode.dmSize = (ushort)Marshal.SizeOf(mode);
            if (!EnumDisplaySettings(name, -1, ref mode)) return Rectangle.Empty;
            return new Rectangle(mode.dmPositionX, mode.dmPositionY, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
        }
        public static string Describe() {
            StringBuilder result = new StringBuilder();
            foreach (Screen screen in Screen.AllScreens) result.AppendLine(screen.DeviceName + " " + screen.Bounds + " primary=" + screen.Primary);
            for (uint i = 0; i < 64; i++) {
                DisplayDevice d = new DisplayDevice(); d.cb = Marshal.SizeOf(d);
                if (!EnumDisplayDevices(null, i, ref d, 0)) break;
                result.AppendLine(d.DeviceName + " " + d.DeviceString + " flags=" + d.StateFlags);
            }
            return result.ToString();
        }
        [StructLayout(LayoutKind.Sequential)] struct CursorInfo { public int cbSize, flags; public IntPtr cursor; public Point position; }
        [StructLayout(LayoutKind.Sequential)] struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool icon; public uint xHotspot, yHotspot; public IntPtr mask, color; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        public static void DrawCursor(Graphics graphics, int x, int y) {
            CursorInfo cursor = new CursorInfo(); cursor.cbSize = Marshal.SizeOf(cursor);
            if (!GetCursorInfo(ref cursor) || cursor.flags != 1) return;
            IconInfo icon;
            if (!GetIconInfo(cursor.cursor, out icon)) return;
            IntPtr dc = graphics.GetHdc();
            try { DrawIconEx(dc, x - (int)icon.xHotspot, y - (int)icon.yHotspot, cursor.cursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { graphics.ReleaseHdc(dc); if (icon.mask != IntPtr.Zero) DeleteObject(icon.mask); if (icon.color != IntPtr.Zero) DeleteObject(icon.color); }
        }
    }
    sealed class ProofForm : Form {
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
        int clicks;
        int animationFrame;
        System.Threading.Timer animation;
        readonly Label number = new Label();
        public ProofForm() {
            Text = "TabLink · Prueba de segunda pantalla";
            BackColor = Color.FromArgb(16, 23, 25); ForeColor = Color.FromArgb(239, 246, 244);
            Font = new Font("Segoe UI", 20); ClientSize = new Size(1000, 650);
            string name = Native.VirtualDisplay();
            Screen target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == name) ?? Screen.PrimaryScreen;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(target.Bounds.X + 40, target.Bounds.Y + 40);
            var title = new Label { Text = "Tu tablet es otra pantalla.", AutoSize = true, Location = new Point(48, 50), Font = new Font("Segoe UI", 32, FontStyle.Bold) };
            Controls.Add(title);
            Controls.Add(new Label { Text = "Esta ventana está en el escritorio extendido.\nPuedes arrastrar aquí tus aplicaciones de Windows.", AutoSize = true, Location = new Point(52, 140) });
            var button = new Button { Text = "Probar clic táctil", Location = new Point(52, 270), Size = new Size(360, 90), BackColor = Color.FromArgb(94, 234, 212), ForeColor = Color.FromArgb(16, 23, 25), FlatStyle = FlatStyle.Flat };
            button.Click += delegate { clicks++; number.Text = "Clics recibidos: " + clicks; Program.Log("PROOF_TOUCH " + clicks); };
            Controls.Add(button);
            number.Text = "Clics recibidos: 0"; number.AutoSize = true; number.Location = new Point(52, 390); Controls.Add(number);
            Controls.Add(new Label { Text = "USB LOCAL  /  TABLINK", AutoSize = true, Location = new Point(52, 540), ForeColor = Color.FromArgb(94, 234, 212), Font = new Font("Segoe UI Semibold", 14) });
            var motion=new Panel {Location=new Point(52,575),Size=new Size(880,45)};
            motion.Paint+=delegate(object sender,PaintEventArgs e) {
                e.Graphics.Clear(Color.FromArgb(24,24,24));
                int x=(animationFrame*9)%800;
                e.Graphics.FillRectangle(Brushes.White,x,0,18,45);
                using(Font counterFont=new Font("Segoe UI",12)) e.Graphics.DrawString(Native.RefreshRate(target.DeviceName)+" Hz · "+DateTime.Now.ToString("HH:mm:ss.fff"),counterFont,Brushes.White,400,10);
            };
            Controls.Add(motion);
            timeBeginPeriod(1);
            Shown+=delegate {
                animation=new System.Threading.Timer(delegate {
                    if(IsDisposed || !IsHandleCreated)return;
                    try {BeginInvoke((Action)delegate{animationFrame++;motion.Invalidate();});}catch(InvalidOperationException){}
                },null,0,8);
            };
            FormClosed+=delegate {if(animation!=null)animation.Dispose();timeEndPeriod(1);};
        }
    }
}
