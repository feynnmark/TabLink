using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabLink {
    sealed partial class HostForm {
        void HandleVideo(TcpClient client, NetworkStream stream, StreamProfile requested) {
            if (!mirror && !EnsureMonitor(requested)) throw new IOException("Prepara la pantalla virtual antes de conectar.");
            string display = mirror ? Screen.PrimaryScreen.DeviceName : Native.VirtualDisplay();
            Rectangle bounds = Native.DisplayBounds(display);
            if (bounds.Width <= 0) throw new IOException("La pantalla no está activa.");
            int monitor = Native.CaptureMonitorIndex(display);
            int refresh = Native.RefreshRate(display);
            int fps = Math.Min(requested.Rate, refresh > 0 ? refresh : requested.Rate);
            double fit=Math.Min(1,Math.Min((double)requested.Width/bounds.Width,(double)requested.Height/bounds.Height));
            int videoWidth=((int)(bounds.Width*fit))&~1, videoHeight=((int)(bounds.Height*fit))&~1;
            string ffmpeg = Path.Combine(Program.Root, "video", "ffmpeg.exe");
            // Give the capture API headroom so jitter around the monitor's refresh
            // boundary cannot cause it to reject every second compositor update.
            string source = "gfxcapture=monitor_idx=" + monitor + ":max_framerate=" + (mirror?fps:Math.Max(120,fps*2)) + ":output_fmt="+requested.Bits+"bit:display_border=0:width="+videoWidth+":height="+videoHeight+":resize_mode=scale_aspect";
            if(!capturePrimed) {
                bool ready=false;
                for(int attempt=0;attempt<3 && running;attempt++) {
                    try {
                        Program.Run(ffmpeg,"-hide_banner -nostdin -loglevel error -f lavfi -i \""+source+"\" -frames:v 1 -c:v wrapped_avframe -f null -",5000);
                        ready=true;break;
                    }catch(IOException error){Program.Log("CAPTURE warmup "+(attempt+1)+" "+error.Message);Thread.Sleep(250);}
                }
                if(!ready)throw new IOException("Windows aun no permite capturar el monitor virtual.");
                capturePrimed=true;
            }
            string arguments = "-hide_banner -nostdin -loglevel warning -nostats -stats_period 5 -progress pipe:2 -thread_queue_size 1 -f lavfi -i \"" + source + "\" " +
                "-c:v hevc_nvenc -profile:v "+(requested.Bits==10?"main10 -highbitdepth 1":"main")+" -preset p1 -tune ull -rc vbr -cq 16 -b:v 160M -maxrate 180M -bufsize 2M -g 1 -refs 1 " +
                "-bf 0 -zerolatency 1 -delay 0 -rc-lookahead 0 -aud 1 -color_primaries bt709 -color_trc iec61966-2-1 -fps_mode passthrough -flush_packets 1 -avioflags direct -f hevc pipe:1";
            ProcessStartInfo info = new ProcessStartInfo(ffmpeg, arguments);
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using (Process encoder = new Process()) {
                encoder.StartInfo = info;
                int warnings = 0;
                encoder.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("fps=")) Program.Log("NVENC " + e.Data);
                    else if (!e.Data.Contains("=") && Interlocked.Increment(ref warnings) <= 20) Program.Log("NVENC " + e.Data);
                };
                encoder.Start(); encoder.BeginErrorReadLine();
                try {encoder.PriorityClass=ProcessPriorityClass.AboveNormal;}catch(System.ComponentModel.Win32Exception){}
                var lifetime = new VideoLifetime(encoder, client);
                Stopwatch clock=Stopwatch.StartNew();
                long lastAck=-1,ackTotalUs=0,ackCount=0;
                var ackSignal=new AutoResetEvent(false);
                var mailbox=new CaptureMailbox();
                Thread reader = new Thread(delegate() {
                    try {
                        while (running && lifetime.Running) {
                            string command = ReadLine(stream, 1024);
                            if (command == null || command == "BYE") break;
                            if(command.StartsWith("ACK ")) {
                                long stamp;
                                if(long.TryParse(command.Substring(4),NumberStyles.Integer,CultureInfo.InvariantCulture,out stamp) && stamp>=0 && stamp<=(long)(clock.Elapsed.TotalMilliseconds*1000.0) && stamp>Interlocked.Read(ref lastAck)) {
                                    Interlocked.Exchange(ref lastAck,stamp);
                                    Interlocked.Add(ref ackTotalUs,Math.Max(0,(long)(clock.Elapsed.TotalMilliseconds*1000)-stamp));
                                    Interlocked.Increment(ref ackCount);ackSignal.Set();
                                }
                            } else if (command.StartsWith("TOUCH ")) Native.InjectTouchFrame(command, Native.DisplayBounds(display));
                        }
                    } catch (Exception error) { if (running && lifetime.Running) Program.Log("INPUT " + error.Message); }
                    finally { lifetime.Stop();ackSignal.Set(); }
                });
                reader.IsBackground = true; reader.Name = "TabLink touch";
                reader.Priority=ThreadPriority.AboveNormal;
                client.ReceiveTimeout = 0;
                WriteInt(stream, 0x544c5635); WriteInt(stream, videoWidth); WriteInt(stream, videoHeight); WriteInt(stream, fps); WriteInt(stream,requested.Bits);
                reader.Start();
                Thread producer=new Thread(delegate(){
                    try {
                        foreach(ArraySegment<byte> accessUnit in AccessUnits(encoder.StandardOutput.BaseStream)) {
                            if(!running || !lifetime.Running)break;
                            mailbox.Publish(accessUnit,(long)(clock.Elapsed.TotalMilliseconds*1000.0));
                        }
                    }catch(Exception error){if(running && lifetime.Running)Program.Log("CAPTURE "+error.Message);}
                    finally{mailbox.Complete();}
                });
                producer.IsBackground=true;producer.Name="TabLink latest capture";producer.Start();
                Program.Log("VIDEO start " + display + " " + bounds + " source=" + refresh + "Hz target=" + fps + "fps WGC HEVC "+requested.Bits+"bit ALL-INTRA NVENC");
                Status("Pantalla por USB · " + fps + " Hz", videoWidth + " × " + videoHeight + " · "+requested.Bits+" bits · táctil de Windows\nAjusta la posición en Configuración de pantallas.");
                long frames = 0, bytes = 0; Stopwatch time = Stopwatch.StartNew();
                byte[] packet = new byte[4 * 1024 * 1024 + 12];
                Queue<long> inFlight=new Queue<long>();
                try {
                    while(running && lifetime.Running) {
                        if (!running || !lifetime.Running) break;
                        Rectangle latest = Native.DisplayBounds(display);
                        if (latest.Size != bounds.Size) break;
                        long acknowledged=Interlocked.Read(ref lastAck);
                        while(inFlight.Count>0 && inFlight.Peek()<=acknowledged)inFlight.Dequeue();
                        if(inFlight.Count>=4){ackSignal.WaitOne(20);continue;}
                        int length;long pts;
                        if(!mailbox.CopyLatest(packet,12,out length,out pts)) {if(mailbox.Ended)break;continue;}
                        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length)), 0, packet, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(pts)), 0, packet, 4, 8);
                        inFlight.Enqueue(pts);
                        stream.Write(packet, 0, 12+length);
                        frames++; bytes += length;
                        if (frames == 1) Program.Log("VIDEO first access unit " + length);
                        if (frames % 450 == 0) Program.Log(string.Format(CultureInfo.InvariantCulture, "VIDEO sent {0} frames {1:F1}fps {2:F1}Mbit/s render_confirm={3:F1}ms pending={4}", frames, frames / time.Elapsed.TotalSeconds, bytes * 8 / time.Elapsed.TotalSeconds / 1000000,Interlocked.Read(ref ackTotalUs)/1000.0/Math.Max(1,Interlocked.Read(ref ackCount)),inFlight.Count));
                    }
                } finally {
                    lifetime.Stop(); reader.Join(2000);producer.Join(2000);ackSignal.Dispose(); Native.ReleaseTouches();
                    Program.Log("VIDEO end " + frames + " frames");
                }
            }
        }
        sealed class CaptureMailbox {
            readonly object gate=new object();
            byte[] bytes=new byte[512*1024];int length;long stamp;bool available,ended;
            public bool Ended{get{lock(gate)return ended && !available;}}
            public void Publish(ArraySegment<byte> picture,long timestamp) {
                lock(gate){
                    if(bytes.Length<picture.Count)bytes=new byte[picture.Count];
                    Buffer.BlockCopy(picture.Array,picture.Offset,bytes,0,picture.Count);
                    length=picture.Count;stamp=timestamp;available=true;Monitor.PulseAll(gate);
                }
            }
            public bool CopyLatest(byte[] destination,int offset,out int count,out long timestamp) {
                lock(gate){
                    if(!available && !ended)Monitor.Wait(gate,50);
                    count=0;timestamp=0;if(!available)return false;
                    Buffer.BlockCopy(bytes,0,destination,offset,length);count=length;timestamp=stamp;available=false;return true;
                }
            }
            public void Complete(){lock(gate){ended=true;Monitor.PulseAll(gate);}}
        }
        sealed class VideoLifetime {
            readonly Process process; readonly TcpClient client;
            int stopped;
            public bool Running { get { return Volatile.Read(ref stopped) == 0; } }
            public VideoLifetime(Process p, TcpClient c) { process = p; client = c; }
            public void Stop() {
                if (Interlocked.Exchange(ref stopped, 1) != 0) return;
                try { client.Close(); } catch { }
                try { if (!process.HasExited) process.Kill(); } catch { }
            }
        }
        internal static IEnumerable<ArraySegment<byte>> AccessUnits(Stream input) {
            byte[] buffer = new byte[4 * 1024 * 1024];
            int used = 0, scan = 0; bool foundAud = false;
            for (;;) {
                int count = input.Read(buffer, used, Math.Min(64 * 1024, buffer.Length - used));
                if (count == 0) { if (used > 0) yield return new ArraySegment<byte>(buffer,0,used); yield break; }
                used += count;
                while (scan + 5 <= used) {
                    int prefix = 0;
                    if (buffer[scan] == 0 && buffer[scan + 1] == 0) {
                        if (buffer[scan + 2] == 1) prefix = 3;
                        else if (buffer[scan + 2] == 0 && buffer[scan + 3] == 1) prefix = 4;
                    }
                    if (prefix > 0) {
                        if (((buffer[scan + prefix] >> 1) & 63) == 35) {
                            if (foundAud && scan > 0) {
                                yield return new ArraySegment<byte>(buffer,0,scan);
                                Buffer.BlockCopy(buffer, scan, buffer, 0, used - scan);
                                used -= scan; scan = 0; foundAud = false;
                                continue;
                            }
                            foundAud = true;
                        }
                        scan += prefix + 1;
                    } else scan++;
                }
                if (used >= buffer.Length - 8) throw new IOException("El fotograma HEVC supera el límite de 4 MB.");
            }
        }
    }
    static partial class Native {
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        public static void EnablePerMonitorDpi() { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware(); }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point,uint flags);
        public static ulong MonitorHandle(Rectangle bounds) { return unchecked((ulong)MonitorFromPoint(new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2),2).ToInt64()); }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct MonitorDetails {public int size;public RectangleNative monitor,work;public uint flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string device;}
        delegate bool MonitorCallback(IntPtr monitor,IntPtr dc,IntPtr rect,IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorCallback callback,IntPtr data);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorDetails details);
        public static int CaptureMonitorIndex(string name) {
            int index=0,result=-1;
            MonitorCallback callback=delegate(IntPtr monitor,IntPtr dc,IntPtr rect,IntPtr data){
                MonitorDetails details=new MonitorDetails();details.size=Marshal.SizeOf(details);
                if(GetMonitorInfo(monitor,ref details) && details.device==name){result=index;return false;}
                index++;return true;
            };
            EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,callback,IntPtr.Zero);
            if(result<0)throw new IOException("El monitor virtual aun no esta listo para captura");
            return result;
        }
        static string ModernAdapter() {
            for (uint i = 0; i < 64; i++) {
                DisplayDevice d = new DisplayDevice(); d.cb = Marshal.SizeOf(d);
                if (!EnumDisplayDevices(null, i, ref d, 0)) break;
                if (d.DeviceString == "Virtual Display Driver") return d.DeviceName;
            }
            return null;
        }
        public static bool IsModernVdd(string name) { return name != null && name == ModernAdapter(); }
        public static bool HasModernVdd {get{return ModernAdapter()!=null;}}
        static DevMode SelectMode(string name,StreamProfile requested) {
            DevMode best=new DevMode();double bestScore=double.PositiveInfinity;
            for(int index=0;index<10000;index++) {
                DevMode candidate=new DevMode();candidate.dmSize=(ushort)Marshal.SizeOf(candidate);
                if(!EnumDisplaySettings(name,index,ref candidate))break;
                double score=requested.Score((int)candidate.dmPelsWidth,(int)candidate.dmPelsHeight,(int)candidate.dmDisplayFrequency);
                if(score<bestScore){best=candidate;bestScore=score;}
            }
            if(double.IsPositiveInfinity(bestScore))throw new IOException("No hay un modo de pantalla compatible. Actualiza la configuración del controlador virtual.");
            return best;
        }
        public static void SwitchMonitor(bool attach,StreamProfile requested=null) {
            string name=ModernAdapter();
            if(name==null)throw new IOException("Instala el controlador de pantalla virtual.");
            DevMode previous=new DevMode();previous.dmSize=(ushort)Marshal.SizeOf(previous);
            EnumDisplaySettings(name,-2,ref previous);
            string positionFile=Path.Combine(Program.Data,"monitor-position.txt");
            DevMode mode=attach?SelectMode(name,requested):previous;
            mode.dmSize=(ushort)Marshal.SizeOf(mode);mode.dmBitsPerPel=32;
            if(attach) {
                Rectangle primary=DisplayBounds(Screen.PrimaryScreen.DeviceName);
                mode.dmPositionX=primary.Right-(int)mode.dmPelsWidth;mode.dmPositionY=primary.Bottom;
                // Place just the new screen below existing screens, without overlap.
                bool overlaps;
                do {
                    overlaps=false;Rectangle proposed=new Rectangle(mode.dmPositionX,mode.dmPositionY,(int)mode.dmPelsWidth,(int)mode.dmPelsHeight);
                    foreach(Screen screen in Screen.AllScreens)if(screen.DeviceName!=name && proposed.IntersectsWith(DisplayBounds(screen.DeviceName))){mode.dmPositionY=DisplayBounds(screen.DeviceName).Bottom;overlaps=true;}
                }while(overlaps);
                if(File.Exists(positionFile)) {
                    string[] values=File.ReadAllText(positionFile).Split(',');int x,y;
                    if(values.Length==2 && int.TryParse(values[0],out x) && int.TryParse(values[1],out y)){mode.dmPositionX=x;mode.dmPositionY=y;}
                }
            }else {
                Rectangle current=DisplayBounds(name);
                if(current.Width>0)File.WriteAllText(positionFile,current.X+","+current.Y);
                mode.dmPelsWidth=0;mode.dmPelsHeight=0;
            }
            mode.dmFields=0x20|0x80000|0x100000|0x40000|0x400000;
            int result=ChangeDisplaySettingsEx(name,ref mode,IntPtr.Zero,1,IntPtr.Zero);
            if(result!=0 && attach){SetModernPathActive(name,true);result=ChangeDisplaySettingsEx(name,ref mode,IntPtr.Zero,1,IntPtr.Zero);}
            if(result!=0)throw new IOException("No se pudo cambiar la pantalla virtual: "+result);
            Thread.Sleep(200);
        }
        public static void PrepareModernVdd(string name,StreamProfile requested) {
            DevMode mode=SelectMode(name,requested);Rectangle current=DisplayBounds(name);
            if(current.Width!=mode.dmPelsWidth || current.Height!=mode.dmPelsHeight || RefreshRate(name)!=mode.dmDisplayFrequency)SwitchMonitor(true,requested);
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="ChangeDisplaySettingsExW")] static extern int CommitDisplaySettings(string device,IntPtr mode,IntPtr window,uint flags,IntPtr param);
        static void Place(string name,int x,int y) {
            DevMode mode=new DevMode();mode.dmSize=(ushort)Marshal.SizeOf(mode);
            if(!EnumDisplaySettings(name,-1,ref mode))return;
            mode.dmPositionX=x;mode.dmPositionY=y;mode.dmFields=0x20;
            int result=ChangeDisplaySettingsEx(name,ref mode,IntPtr.Zero,0x10000001,IntPtr.Zero);
            Program.Log("LAYOUT "+name+" "+x+","+y+" result="+result);
        }
        public static int RefreshRate(string name) {
            DevMode mode = new DevMode(); mode.dmSize = (ushort)Marshal.SizeOf(mode);
            return EnumDisplaySettings(name, -1, ref mode) ? (int)mode.dmDisplayFrequency : 0;
        }
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint low; public int high; }
        [StructLayout(LayoutKind.Sequential)] struct PathSource { public Luid adapter; public uint id, mode, status; }
        [StructLayout(LayoutKind.Sequential)] struct Rational { public uint numerator, denominator; }
        [StructLayout(LayoutKind.Sequential)] struct PathTarget { public Luid adapter; public uint id, mode, technology, rotation, scaling; public Rational refresh; public uint scanline; public int available; public uint status; }
        [StructLayout(LayoutKind.Sequential)] struct DisplayPath { public PathSource source; public PathTarget target; public uint flags; }
        [StructLayout(LayoutKind.Explicit,Size=64)] struct DisplayMode { [FieldOffset(0)] public uint type; [FieldOffset(4)] public uint id; [FieldOffset(8)] public Luid adapter; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SourceName { public uint type,size; public Luid adapter; public uint id; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string name; }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags,ref uint paths,[Out] DisplayPath[] pathArray,ref uint modes,[Out] DisplayMode[] modeArray,IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName name);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths,[In] DisplayPath[] pathArray,uint modes,[In] DisplayMode[] modeArray,uint flags);
        static void SetModernPathActive(string name, bool active) {
            Dictionary<string,Rectangle> preserved=new Dictionary<string,Rectangle>();
            foreach(Screen screen in Screen.AllScreens) if(screen.DeviceName!=name) preserved[screen.DeviceName]=DisplayBounds(screen.DeviceName);
            uint count, modeCount;
            int error = GetDisplayConfigBufferSizes(1, out count, out modeCount);
            if (error != 0) throw new System.ComponentModel.Win32Exception(error);
            DisplayPath[] paths = new DisplayPath[count]; DisplayMode[] modes = new DisplayMode[modeCount];
            error = QueryDisplayConfig(1,ref count,paths,ref modeCount,modes,IntPtr.Zero);
            if(error!=0) throw new System.ComponentModel.Win32Exception(error);
            List<DisplayPath> selected = new List<DisplayPath>(); bool added = false;
            for(int i=0;i<count;i++) {
                DisplayPath path=paths[i];
                SourceName source=new SourceName();source.type=1;source.size=(uint)Marshal.SizeOf(source);source.adapter=path.source.adapter;source.id=path.source.id;
                DisplayConfigGetDeviceInfo(ref source);
                bool ours=source.name==name;
                if (ours && !added && active && path.target.available!=0) {
                    path.flags|=1; path.source.mode=0xffffffff;path.target.mode=0xffffffff;
                    selected.Add(path);added=true;
                } else if(!ours && (path.flags&1)!=0) selected.Add(path);
            }
            if(active && !added) throw new IOException("No se encontro la ruta del monitor virtual");
            error=SetDisplayConfig((uint)selected.Count,selected.ToArray(),modeCount,modes,0x20|0x80|0x200|0x400);
            Program.Log("MONITOR targeted path activation="+error);
            if(error!=0) throw new System.ComponentModel.Win32Exception(error);
            Thread.Sleep(250);
            foreach(var placement in preserved) Place(placement.Key,placement.Value.Left,placement.Value.Top);
            error=CommitDisplaySettings(null,IntPtr.Zero,IntPtr.Zero,0,IntPtr.Zero);
            if(error!=0)throw new System.ComponentModel.Win32Exception(error);
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct OutputDescription {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string name;
            public RectangleNative rect; public int attached; public uint rotation; public IntPtr monitor;
        }
        [StructLayout(LayoutKind.Sequential)] struct RectangleNative { public int left, top, right, bottom; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdaptersDelegate(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumOutputsDelegate(IntPtr self, uint index, out IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetOutputDescriptionDelegate(IntPtr self, out OutputDescription desc);
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid guid, out IntPtr factory);
        static T Method<T>(IntPtr instance, int slot) where T: class {
            IntPtr table = Marshal.ReadIntPtr(instance);
            return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof(T)) as T;
        }
        public static int DdaOutputIndex(string name) {
            Guid guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            IntPtr factory;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref guid, out factory));
            try {
                IntPtr adapter;
                Marshal.ThrowExceptionForHR(Method<EnumAdaptersDelegate>(factory, 12)(factory, 0, out adapter));
                try {
                    for (uint i = 0; i < 32; i++) {
                        IntPtr output;
                        if (Method<EnumOutputsDelegate>(adapter, 7)(adapter, i, out output) < 0) break;
                        try {
                            OutputDescription description;
                            Marshal.ThrowExceptionForHR(Method<GetOutputDescriptionDelegate>(output, 7)(output, out description));
                            if (description.name == name) return (int)i;
                        } finally { Marshal.Release(output); }
                    }
                } finally { Marshal.Release(adapter); }
            } finally { Marshal.Release(factory); }
            throw new IOException("DXGI no encontró la salida " + name);
        }

        [StructLayout(LayoutKind.Sequential)] struct PointerInfo {
            public uint type, id, frame, flags; public IntPtr source, target;
            public Point pixel, himetric, pixelRaw, himetricRaw;
            public uint time, history; public int input; public uint keys; public ulong performance; public uint buttonChange;
        }
        [StructLayout(LayoutKind.Sequential)] struct TouchInfo {
            public PointerInfo pointer; public uint flags, mask;
            public RectangleNative contact, contactRaw; public uint orientation, pressure;
        }
        [DllImport("user32.dll",SetLastError=true)] static extern bool InitializeTouchInjection(uint contacts,uint mode);
        [DllImport("user32.dll",SetLastError=true)] static extern bool InjectTouchInput(uint count,[In] TouchInfo[] contacts);
        static readonly Dictionary<uint, TouchInfo> ActiveTouches = new Dictionary<uint, TouchInfo>();
        static bool touchInitialized;
        public static void InjectTouchFrame(string command, Rectangle bounds) {
            lock (ActiveTouches) {
                if (!touchInitialized) {
                    if (!InitializeTouchInjection(10, 3)) throw new System.ComponentModel.Win32Exception();
                    touchInitialized = true;
                }
                string[] parts = command.Split(' ');
                int action = int.Parse(parts[1], CultureInfo.InvariantCulture), actionIndex = int.Parse(parts[2], CultureInfo.InvariantCulture), count = int.Parse(parts[3], CultureInfo.InvariantCulture);
                if (count == 0) { ReleaseTouches(); return; }
                if (count < 1 || count > 10 || parts.Length != count + 4) throw new IOException("Trama táctil no válida");
                TouchInfo[] contacts = new TouchInfo[count];
                for (int i = 0; i < count; i++) {
                    string[] point = parts[i + 4].Split(':');
                    uint id = uint.Parse(point[0], CultureInfo.InvariantCulture) + 1;
                    double x = double.Parse(point[1], CultureInfo.InvariantCulture), y = double.Parse(point[2], CultureInfo.InvariantCulture);
                    if (id > 32 || double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) throw new IOException("Coordenadas táctiles no válidas");
                    TouchInfo contact = new TouchInfo();
                    contact.pointer.type = 2; contact.pointer.id = id;
                    contact.pointer.pixel = new Point(bounds.Left + (int)Math.Round(Math.Max(0,Math.Min(1,x)) * (bounds.Width - 1)), bounds.Top + (int)Math.Round(Math.Max(0,Math.Min(1,y)) * (bounds.Height - 1)));
                    bool up = action == 3 || ((action == 1 || action == 6) && i == actionIndex);
                    bool down = (action == 0 || action == 5) && i == actionIndex;
                    contact.pointer.flags = up ? 0x40000u : (down ? 0x10000u : 0x20000u) | 2u | 4u;
                    if (action == 3) contact.pointer.flags |= 0x8000;
                    contact.mask = 7; contact.orientation = 90; contact.pressure = 512;
                    contact.contact = new RectangleNative { left=contact.pointer.pixel.X-2, top=contact.pointer.pixel.Y-2, right=contact.pointer.pixel.X+2, bottom=contact.pointer.pixel.Y+2 };
                    contacts[i] = contact;
                    if (up) ActiveTouches.Remove(id); else ActiveTouches[id] = contact;
                }
                if (!InjectTouchInput((uint)count, contacts)) {
                    int error = Marshal.GetLastWin32Error(); ActiveTouches.Clear();
                    Program.Log("TOUCH injection failed " + error);
                }
            }
        }
        public static void ReleaseTouches() {
            lock (ActiveTouches) {
                if (ActiveTouches.Count == 0) return;
                TouchInfo[] contacts = new TouchInfo[ActiveTouches.Count]; int index=0;
                foreach (TouchInfo value in ActiveTouches.Values) { TouchInfo up = value; up.pointer.flags = 0x40000; contacts[index++] = up; }
                InjectTouchInput((uint)contacts.Length,contacts); ActiveTouches.Clear();
            }
        }
    }
}
