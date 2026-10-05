using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexQuotaBar
{
    static class Json
    {
        public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static Dictionary<string, object> Decode(string value) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(value); }
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
        public static object Get(Dictionary<string, object> obj, string key) { object v; return obj != null && obj.TryGetValue(key, out v) ? v : null; }
        public static double? Number(object value) { double d; return value != null && Double.TryParse(Convert.ToString(value), out d) && !Double.IsNaN(d) && !Double.IsInfinity(d) ? (double?)d : null; }
    }

    sealed class QuotaWindow
    {
        public double? Remaining;
        public double? Minutes;
        public double? Reset;
        public static QuotaWindow Parse(object value)
        {
            var obj = Json.Obj(value);
            if (obj == null) return null;
            var used = Json.Number(Json.Get(obj, "usedPercent"));
            return new QuotaWindow { Remaining = used.HasValue ? Math.Max(0, Math.Min(100, 100 - used.Value)) : (double?)null,
                Minutes = Json.Number(Json.Get(obj, "windowDurationMins")), Reset = Json.Number(Json.Get(obj, "resetsAt")) };
        }
        public string Label
        {
            get {
                if (!Minutes.HasValue) return "额度";
                if (Minutes.Value == 10080) return "周额度";
                if (Minutes.Value % 1440 == 0) return (Minutes.Value / 1440).ToString("0.#") + "天额度";
                if (Minutes.Value % 60 == 0) return (Minutes.Value / 60).ToString("0.#") + "小时";
                return Minutes.Value.ToString("0.#") + "分钟";
            }
        }
        public string Text { get { return Label + "剩余 " + (Remaining.HasValue ? Remaining.Value.ToString("0.#") + "%" : "未知"); } }
        public string ResetText
        {
            get {
                if (!Reset.HasValue || Reset.Value <= 0) return null;
                try {
                    var utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Reset.Value);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")).ToString("MM/dd HH:mm");
                } catch { return null; }
            }
        }
        public string Countdown(DateTime utcNow)
        {
            if (!Reset.HasValue || Reset.Value <= 0) return null;
            try {
                var left = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(Reset.Value) - utcNow;
                if (left <= TimeSpan.Zero) return "等待额度重置";
                if (left.TotalDays >= 1) return ((int)left.TotalDays) + "天" + left.Hours + "小时后重置";
                if (left.TotalHours >= 1) return ((int)left.TotalHours) + "小时" + left.Minutes + "分钟后重置";
                return Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)) + "分钟后重置";
            } catch { return null; }
        }
    }

    sealed class QuotaSnapshot
    {
        public Dictionary<string, Dictionary<string, object>> Buckets = new Dictionary<string, Dictionary<string, object>>();
        public DateTime Updated;
        public static QuotaSnapshot Parse(Dictionary<string, object> result)
        {
            var s = new QuotaSnapshot { Updated = DateTime.Now };
            var map = Json.Obj(Json.Get(result, "rateLimitsByLimitId"));
            if (map != null) foreach (var pair in map) { var bucket = Json.Obj(pair.Value); if (bucket != null) s.Buckets[pair.Key] = bucket; }
            if (s.Buckets.Count == 0) {
                var single = Json.Obj(Json.Get(result, "rateLimits"));
                if (single != null) s.Buckets[Convert.ToString(Json.Get(single, "limitId")) ?? "codex"] = single;
            }
            return s;
        }
        public string Choose(string preferred)
        {
            if (!String.IsNullOrEmpty(preferred) && Buckets.ContainsKey(preferred)) return preferred;
            if (Buckets.ContainsKey("codex")) return "codex";
            return Buckets.Keys.FirstOrDefault();
        }
        public List<QuotaWindow> Windows(string id)
        {
            var list = new List<QuotaWindow>();
            if (id == null || !Buckets.ContainsKey(id)) return list;
            foreach (var key in new[] { "primary", "secondary" }) {
                var w = QuotaWindow.Parse(Json.Get(Buckets[id], key)); if (w != null) list.Add(w);
            }
            return list;
        }
        public string Line(string id)
        {
            var windows = Windows(id);
            if (windows.Count == 0) return "此账户暂未返回额度数据";
            var text = String.Join("   ·   ", windows.Select(w => w.Text));
            if (windows.Count == 1 && windows[0].ResetText != null) text += "   ·   " + windows[0].ResetText + " 重置";
            return text;
        }
    }

    sealed class AppServer : IDisposable
    {
        Process process;
        readonly object gate = new object();
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        int sequence;
        bool disposed;
        public async Task Connect(string executable)
        {
            var info = new ProcessStartInfo(executable, "app-server") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Program.Root };
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Exited += delegate { FailPending(); };
            if (!process.Start()) throw new IOException("无法启动 Codex 额度接口");
            process.StandardInput.AutoFlush = true;
            Task.Run(async delegate {
                try { string line; while ((line = await process.StandardOutput.ReadLineAsync()) != null) {
                    Dictionary<string, object> msg; try { msg = Json.Decode(line); } catch { continue; }
                    var id = Json.Number(Json.Get(msg, "id")); if (!id.HasValue) continue;
                    TaskCompletionSource<Dictionary<string, object>> waiter = null;
                    lock (gate) { if (pending.TryGetValue((int)id.Value, out waiter)) pending.Remove((int)id.Value); }
                    if (waiter == null) continue;
                    if (Json.Get(msg, "error") != null) waiter.TrySetException(new IOException("Codex 额度接口请求失败"));
                    else waiter.TrySetResult(Json.Obj(Json.Get(msg, "result")) ?? new Dictionary<string, object>());
                }} catch { } finally { FailPending(); }
            });
            // Drain diagnostics without saving potentially sensitive server messages.
            Task.Run(async delegate { try { while (await process.StandardError.ReadLineAsync() != null) { } } catch { } });
            await Request("initialize", new { clientInfo = new { name = "codex_quota_bar", title = "Codex Quota Bar", version = "1.0.0" } });
            lock (gate) process.StandardInput.WriteLine(Json.Encode(new { method = "initialized", @params = new { } }));
        }
        async Task<Dictionary<string, object>> Request(string method, object parameters)
        {
            var waiter = new TaskCompletionSource<Dictionary<string, object>>(); int id;
            lock (gate) {
                if (disposed || process == null || process.HasExited) throw new IOException("额度连接已断开");
                id = ++sequence; pending[id] = waiter;
                try { process.StandardInput.WriteLine(Json.Encode(new { id = id, method = method, @params = parameters })); }
                catch { pending.Remove(id); throw; }
            }
            if (await Task.WhenAny(waiter.Task, Task.Delay(20000)) != waiter.Task) {
                lock (gate) pending.Remove(id);
                throw new TimeoutException("额度请求超时");
            }
            return await waiter.Task;
        }
        public async Task<QuotaSnapshot> Read() { return QuotaSnapshot.Parse(await Request("account/rateLimits/read", null)); }
        void FailPending()
        {
            lock (gate) { foreach (var p in pending.Values) p.TrySetException(new IOException("额度连接已断开")); pending.Clear(); }
        }
        public void Dispose()
        {
            disposed = true; FailPending();
            if (process == null) return;
            try { process.StandardInput.Close(); if (!process.WaitForExit(700)) process.Kill(); } catch { }
            process.Dispose();
        }
    }

    sealed class ComposerAnchor
    {
        public Rectangle Editor;
        public Rectangle Slot;
        public Rectangle Owner;
        public int Dpi;
        public string Mode;
        public List<object> Controls = new List<object>();
        public bool Valid { get { return !Editor.IsEmpty && Slot.Width > 0 && Slot.Height > 0; } }
        public Rectangle SlotAt(Rectangle owner) { return new Rectangle(owner.Left+Slot.Left-Owner.Left,owner.Top+Slot.Top-Owner.Top,Slot.Width,Slot.Height); }
        public bool Matches(Rectangle owner, int dpi) { return Owner.Size==owner.Size && Dpi==dpi; }
    }

    sealed class AnchorReader : IDisposable
    {
        Process worker;
        public string LastResult="not-started";
        public async Task<ComposerAnchor> Read(IntPtr window)
        {
            try {
                if(worker==null || worker.HasExited) {
                    worker=Process.Start(new ProcessStartInfo(Path.Combine(Program.Root,"CodexQuotaBar.exe"),"--anchor-worker "+Process.GetCurrentProcess().Id) {
                        UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,
                        StandardOutputEncoding=Encoding.UTF8,WorkingDirectory=Program.Root });
                    worker.StandardInput.AutoFlush=true;
                }
                worker.StandardInput.WriteLine(window.ToInt64());
                var read=worker.StandardOutput.ReadLineAsync();
                if(await Task.WhenAny(read,Task.Delay(5000))!=read) { LastResult="timeout"; Dispose(); return null; }
                var line=await read; if(line==null) { LastResult="worker-exited"; Dispose(); return null; }
                var result=new JavaScriptSerializer().Deserialize<ComposerAnchor>(line);
                LastResult=result.Valid ? "located" : "no-editor-or-toolbar"; return result;
            } catch(Exception e) { LastResult=e.GetType().Name; Dispose(); return null; }
        }
        public void Dispose()
        {
            var previous=worker; worker=null;
            if(previous==null) return;
            try { if(!previous.HasExited) previous.Kill(); } catch { }
            Task.Run(delegate { previous.Dispose(); });
        }
    }

    static class QuotaPlacement
    {
        public static Rectangle Fit(Rectangle slot, Size size, double scale, double offsetX, double offsetY)
        {
            if(size.Width>slot.Width || size.Height>slot.Height || size.Width<=0 || size.Height<=0) return Rectangle.Empty;
            int x = slot.Left + (int)Math.Round(offsetX*scale);
            int y = slot.Top + (slot.Height-size.Height)/2 + (int)Math.Round(offsetY*scale);
            return new Rectangle(Math.Max(slot.Left,Math.Min(x,slot.Right-size.Width)),Math.Max(slot.Top,Math.Min(y,slot.Bottom-size.Height)),size.Width,size.Height);
        }
    }

    static class Native
    {
        public delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLong64(IntPtr w, int n, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong32(IntPtr w, int n, int value);
        public static void Owner(IntPtr window, IntPtr owner) { if (IntPtr.Size == 8) SetWindowLong64(window, -8, owner); else SetWindowLong32(window, -8, owner.ToInt32()); }
        public static int Dpi(IntPtr window) { try { return (int)GetDpiForWindow(window); } catch { return 96; } }
        public static Color? SurfaceColor(IntPtr window, Rectangle bar, Rectangle owner)
        {
            // Sample a few neighboring background pixels only, without capturing or storing a screenshot.
            var pixels = new List<Color>();
            var dc = GetDC(IntPtr.Zero); if (dc == IntPtr.Zero) return null;
            try {
                var anchors = new[] { new Point(bar.Left - 8, bar.Top + bar.Height / 2),
                    new Point(bar.Right + 8, bar.Top + bar.Height / 2), new Point(bar.Left + bar.Width / 2, bar.Bottom + 4) };
                foreach (var anchor in anchors) foreach (int dx in new[] { -2, 2 }) foreach (int dy in new[] { 0 }) {
                    var point = new Point(anchor.X + dx, anchor.Y + dy);
                    if (!owner.Contains(point) || GetAncestor(WindowFromPoint(point), 2) != window) continue;
                    var rgb = GetPixel(dc, point.X, point.Y); if (rgb == 0xFFFFFFFF) continue;
                    pixels.Add(Color.FromArgb((int)(rgb & 255), (int)((rgb >> 8) & 255), (int)((rgb >> 16) & 255)));
                }
            } finally { ReleaseDC(IntPtr.Zero, dc); }
            if (pixels.Count < 4) return null;
            return pixels.OrderByDescending(c => pixels.Count(p => Math.Abs(c.R-p.R) + Math.Abs(c.G-p.G) + Math.Abs(c.B-p.B) < 12)).First();
        }
        public static bool IsCodex(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindowVisible(window)) return false;
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (!name.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return false;
            uint pid; GetWindowThreadProcessId(window, out pid);
            try { using (var p = Process.GetProcessById((int)pid)) {
                if (String.Equals(p.ProcessName, "Codex", StringComparison.OrdinalIgnoreCase)) return true;
                // Current Store builds retain the Codex package name but run ChatGPT.exe.
                if (String.Equals(p.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase)) {
                    var path = p.MainModule.FileName;
                    return path.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return false;
            }} catch { return false; }
        }
        public static List<IntPtr> Windows()
        {
            var windows = new List<IntPtr>(); EnumWindows(delegate(IntPtr w, IntPtr x) { if (IsCodex(w)) windows.Add(w); return true; }, IntPtr.Zero); return windows;
        }
        public static Rectangle Composer(IntPtr window)
        {
            return ReadComposer(window).Editor;
        }
        public static ComposerAnchor ReadComposer(IntPtr window)
        {
            // Read control geometry and toolbar button labels, never editable values or chat text.
            var result = new ComposerAnchor();
            try {
                Rect bounds; if (!GetWindowRect(window, out bounds)) return result;
                var r = bounds.ToRectangle();
                result.Owner = r; result.Dpi = Dpi(window);
                var root = AutomationElement.FromHandle(window);
                var cache = new CacheRequest();
                cache.Add(AutomationElement.BoundingRectangleProperty); cache.Add(AutomationElement.IsOffscreenProperty);
                using(cache.Activate()) {
                var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                Rectangle best = Rectangle.Empty;
                AutomationElement bestElement=null;
                foreach (AutomationElement edit in edits) {
                    if (edit.Cached.IsOffscreen) continue;
                    var b = edit.Cached.BoundingRectangle;
                    if (b.IsEmpty) continue;
                    var candidate = Rectangle.FromLTRB((int)b.Left, (int)b.Top, (int)b.Right, (int)b.Bottom);
                    if (candidate.Width < 180 || candidate.Height < 22 || candidate.Height > r.Height * 0.4 || candidate.Top < r.Top + r.Height * 0.5 || candidate.Bottom > r.Bottom - 12) continue;
                    if (candidate.Top > best.Top) { best = candidate; bestElement=edit; }
                }
                result.Editor = best;
                if (best.IsEmpty) return result;
                // Prefer the composer's containing group instead of querying every chat action button.
                var scope=root; var ancestor=bestElement;
                for(int depth=0;depth<6;depth++) {
                    ancestor=TreeWalker.ControlViewWalker.GetParent(ancestor);
                    if(ancestor==null) break;
                    var b=ancestor.Current.BoundingRectangle;
                    if(b.Height>r.Height*.5) break;
                    if(b.Left<=best.Left && b.Right>=best.Right && b.Bottom>=best.Bottom+20*result.Dpi/96.0) { scope=ancestor; break; }
                }
                var buttons = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                var candidates = new List<Tuple<string,Rectangle>>();
                foreach(AutomationElement button in buttons) {
                    try {
                        if (button.Cached.IsOffscreen) continue;
                        var b = button.Cached.BoundingRectangle;
                        if (b.IsEmpty) continue;
                        var rect = Rectangle.FromLTRB((int)b.Left,(int)b.Top,(int)b.Right,(int)b.Bottom);
                        if (rect.Width<=0 || rect.Height<=0 || rect.Left < best.Left-30 || rect.Right>best.Right+30 || rect.Top<best.Bottom-8 || rect.Bottom>r.Bottom || rect.Height>100) continue;
                        string name = button.Current.Name ?? "";
                        candidates.Add(Tuple.Create(name,rect));
                        result.Controls.Add(new { name = name, left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom });
                    } catch { }
                }
                var permissions = candidates.FirstOrDefault(p => p.Item1.Contains("帮我批准") || p.Item1.Contains("权限") || p.Item1.IndexOf("approval",StringComparison.OrdinalIgnoreCase)>=0 || p.Item1.IndexOf("permission",StringComparison.OrdinalIgnoreCase)>=0);
                if(permissions != null) {
                    var p = permissions.Item2;
                    var right = candidates.Where(c => c.Item2.Left>p.Right+5 && Math.Abs((c.Item2.Top+c.Item2.Bottom)-(p.Top+p.Bottom))<Math.Max(c.Item2.Height,p.Height)).OrderBy(c=>c.Item2.Left).FirstOrDefault();
                    int gap = (int)(8*result.Dpi/96.0);
                    result.Slot = Rectangle.FromLTRB(p.Right+gap,p.Top,right == null ? best.Right-gap : right.Item2.Left-gap,p.Bottom);
                    result.Mode = "permission-toolbar";
                }
                else if(candidates.Count>1) {
                    // Alternate language/label: use the largest actual gap in the bottom button row.
                    var bottom = candidates.OrderByDescending(c=>c.Item2.Bottom).First().Item2;
                    var row = candidates.Where(c=>Math.Abs((c.Item2.Top+c.Item2.Bottom)-(bottom.Top+bottom.Bottom))<bottom.Height).OrderBy(c=>c.Item2.Left).ToList();
                    int gap = (int)(8*result.Dpi/96.0);
                    for(int i=1;i<row.Count;i++) {
                        var left=row[i-1].Item2; var right=row[i].Item2;
                        int width=right.Left-left.Right-2*gap;
                        if(width>result.Slot.Width) result.Slot=Rectangle.FromLTRB(left.Right+gap,Math.Max(left.Top,right.Top),right.Left-gap,Math.Min(left.Bottom,right.Bottom));
                    }
                    result.Mode="toolbar-gap";
                }
                return result;
                }
            } catch { return result; }
        }
    }

    sealed class Settings
    {
        public bool CustomPosition;
        public string Bucket = "codex";
        public string Theme = "auto";
        public int PositionVersion = 2;
        public double AnchorOffsetX;
        public double AnchorOffsetY;
        public static Settings Load()
        {
            try {
                var obj = Json.Decode(File.ReadAllText(Path.Combine(Program.Root, "settings.json"), Encoding.UTF8));
                return new Settings { CustomPosition = Json.Number(Json.Get(obj,"PositionVersion")) == 2 && Object.Equals(Json.Get(obj, "CustomPosition"), true), Bucket = Convert.ToString(Json.Get(obj, "Bucket")) ?? "codex",
                    AnchorOffsetX = Json.Number(Json.Get(obj,"AnchorOffsetX")) ?? 0, AnchorOffsetY = Json.Number(Json.Get(obj,"AnchorOffsetY")) ?? 0,
                    Theme = String.IsNullOrEmpty(Convert.ToString(Json.Get(obj, "Theme"))) ? "auto" : Convert.ToString(Json.Get(obj, "Theme")) };
            } catch { return new Settings(); }
        }
        public void Save() { try { File.WriteAllText(Path.Combine(Program.Root, "settings.json"), Json.Encode(this), Encoding.UTF8); } catch { } }
    }

    static class BarDesign
    {
        public static Color Mix(Color a, Color b, double weight) { return Color.FromArgb((int)(a.R*(1-weight)+b.R*weight), (int)(a.G*(1-weight)+b.G*weight), (int)(a.B*(1-weight)+b.B*weight)); }
        public static bool IsLight(Color color) { return color.R * .2126 + color.G * .7152 + color.B * .0722 > 145; }
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath(); float d = radius * 2;
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90); path.AddArc(rect.Right-d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right-d, rect.Bottom-d, d, d, 0, 90); path.AddArc(rect.Left, rect.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        static string Label(QuotaWindow w) { return (w.Label == "周额度" ? "周" : w.Label.Replace("额度", "")) + "剩余"; }
        static string Value(QuotaWindow w) { return w.Remaining.HasValue ? w.Remaining.Value.ToString("0.#") + "%" : "未知"; }
        static void TimeIcon(Graphics g, float x, float centerY, float s, Color color, bool calendar)
        {
            if(g==null) return;
            using(var pen=new Pen(color,1.25f*s)) {
                pen.StartCap=pen.EndCap=LineCap.Round; pen.LineJoin=LineJoin.Round;
                if(!calendar) {
                    g.DrawEllipse(pen,x,centerY-6*s,12*s,12*s);
                    g.DrawLines(pen,new[] { new PointF(x+6*s,centerY-3.5f*s),new PointF(x+6*s,centerY),new PointF(x+8.5f*s,centerY+1.5f*s) });
                } else {
                    using(var box=Round(new RectangleF(x,centerY-5*s,12*s,11*s),1.4f*s)) g.DrawPath(pen,box);
                    g.DrawLine(pen,x,centerY-1.5f*s,x+12*s,centerY-1.5f*s);
                    g.DrawLine(pen,x+3*s,centerY-7*s,x+3*s,centerY-3.5f*s);
                    g.DrawLine(pen,x+9*s,centerY-7*s,x+9*s,centerY-3.5f*s);
                }
            }
        }
        public static int Layout(Graphics graphics, Size size, double scale, Color background, bool hover, List<QuotaWindow> windows, string status, bool stale, int detail = 2)
        {
            float s = (float)scale;
            bool light = IsLight(background);
            var ink = light ? Color.FromArgb(35, 38, 42) : Color.FromArgb(224, 225, 228);
            var muted = Mix(background, ink, .62); var timeInk = Mix(background, ink, .88);
            var surface = hover ? Mix(background, ink, .045) : background;
            var warning = light ? Color.FromArgb(166, 112, 34) : Color.FromArgb(218, 174, 98);
            var rules = Mix(background, ink, .20);
            using (var measureBitmap = new Bitmap(1,1))
            using (var measure = Graphics.FromImage(measureBitmap))
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (var regular = new Font("Microsoft YaHei UI", 12*s, FontStyle.Regular, GraphicsUnit.Pixel)) {
                // One font instance for every run: identical numeral height, hinting and origin.
                var strong=regular; var small=regular;
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                measure.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                Func<string,Font,float> textWidth = (value,font) => measure.MeasureString(value,font,PointF.Empty,format).Width;
                Func<Font,float> ascent = font => font.Size*font.FontFamily.GetCellAscent(font.Style)/font.FontFamily.GetEmHeight(font.Style);
                float descent = regular.Size*regular.FontFamily.GetCellDescent(regular.Style)/regular.FontFamily.GetEmHeight(regular.Style);
                float baseline = size.Height/2f+(ascent(regular)-descent)/2f;
                if (graphics != null) {
                    graphics.Clear(background); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    if (hover) using (var shape = Round(new RectangleF(.5f, .5f, size.Width-1, size.Height-1), 7*s)) {
                        using (var fill = new SolidBrush(surface)) graphics.FillPath(fill, shape);
                        using (var border = new Pen(Mix(background, ink, .085))) graphics.DrawPath(border, shape);
                    }
                }
                float x = 8*s;
                float textTop=(float)Math.Round(baseline-ascent(regular));
                // Shared pixel-snapped origin keeps all numerals on the same visible baseline.
                Action<string, Font, Color> text = delegate(string value, Font font, Color color) {
                    float width = textWidth(value,font);
                    if (graphics != null) {
                        var saved=graphics.Save();
                        graphics.SetClip(new RectangleF(x,0,Math.Max(0,size.Width-18*s-x),size.Height),CombineMode.Intersect);
                        using(var brush=new SolidBrush(color)) graphics.DrawString(value,regular,brush,new PointF(x,textTop),format);
                        graphics.Restore(saved);
                    }
                    x += width;
                };
                Action divider = delegate {
                    x += 8*s;
                    if (graphics != null) using (var pen = new Pen(rules)) graphics.DrawLine(pen, x, size.Height/2f-5*s, x, size.Height/2f+5*s);
                    x += 8*s;
                };
                if (windows.Count == 0) text(status, regular, muted);
                for (int i=0; i<windows.Count; i++) {
                    var w = windows[i]; if (i > 0) divider();
                    var accent = stale || (w.Remaining.HasValue && w.Remaining.Value <= 10) ? warning : Mix(background, ink, .70);
                    if (graphics != null) {
                        var circle = new RectangleF(x, size.Height/2f-5*s, 10*s, 10*s);
                        using (var pen = new Pen(Mix(background, ink, .13), 1.6f*s)) graphics.DrawEllipse(pen, circle);
                        if (w.Remaining.HasValue && w.Remaining.Value > 0) using (var pen = new Pen(accent, 1.6f*s)) {
                            pen.StartCap = pen.EndCap = LineCap.Round;
                            graphics.DrawArc(pen, circle, -90, (float)(w.Remaining.Value*3.6));
                        }
                    }
                    x += 16*s; text(Label(w), regular, muted); x += 4*s;
                    float valueStart=x;
                    text(Value(w), w.Remaining.HasValue ? strong : regular, stale ? muted : ink);
                    x=Math.Max(x,valueStart+textWidth("100%",strong));
                }
                if (stale && detail>0) { divider(); text("待更新", small, warning); }
                else if (!stale && detail>0 && windows.Count == 1 && windows[0].ResetText != null) {
                    divider();
                    TimeIcon(graphics,x,size.Height/2f,s,timeInk,false); x+=18*s;
                    string countdown=windows[0].Countdown(DateTime.UtcNow).Replace("后重置","");
                    float countdownStart=x;
                    text(countdown, small, timeInk);
                    string countdownTemplate=countdown.Contains("天") ? "00天00小时" : (countdown.Contains("小时") ? "00小时00分钟" : "00分钟");
                    x=Math.Max(x,countdownStart+textWidth(countdownTemplate,small));
                    float resetWidth = textWidth(windows[0].ResetText, small);
                    if (detail>1 && (graphics == null || x + 52*s + resetWidth <= size.Width)) {
                        divider();
                        TimeIcon(graphics,x,size.Height/2f,s,timeInk,true); x+=18*s;
                        text(windows[0].ResetText, small, timeInk);
                    }
                }
                x += 18*s;
                if (graphics != null && hover) using (var brush = new SolidBrush(muted)) {
                    for(int i=0; i<3; i++) graphics.FillEllipse(brush, size.Width - 16*s + i*3*s, size.Height/2f-s, 1.6f*s, 1.6f*s);
                }
                return (int)Math.Ceiling(x);
            }
        }
    }

    static class AutoStart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "CodexQuotaBar";
        static string UserId { get { return System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } }
        public static string TaskName { get { return Name+"-"+UserId; } }
        public static string Command { get { return "\""+Path.Combine(Program.Root,"CodexQuotaBar.exe")+"\" --background"; } }
        static dynamic Scheduler()
        {
            dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect(); return service;
        }
        public static bool Enabled {
            get { try {
                dynamic task=Scheduler().GetFolder(@"\").GetTask(TaskName);
                dynamic action=task.Definition.Actions.Item(1);
                return task.Enabled && String.Equals((string)action.Path,Path.Combine(Program.Root,"CodexQuotaBar.exe"),StringComparison.OrdinalIgnoreCase);
            } catch { return false; } }
        }
        public static void Set(bool enabled)
        {
            dynamic service=Scheduler(); dynamic folder=service.GetFolder(@"\");
            if(enabled) {
                dynamic task=service.NewTask(0);
                task.RegistrationInfo.Description="Codex 额度条：当前用户登录后等待 Codex，自动显示剩余额度。";
                task.Principal.UserId=UserId; task.Principal.LogonType=3; task.Principal.RunLevel=0;
                dynamic trigger=task.Triggers.Create(9);
                trigger.UserId=UserId; trigger.Delay="PT20S"; trigger.Enabled=true;
                dynamic action=task.Actions.Create(0);
                action.Path=Path.Combine(Program.Root,"CodexQuotaBar.exe");
                action.Arguments="--background"; action.WorkingDirectory=Program.Root;
                task.Settings.Enabled=true; task.Settings.StartWhenAvailable=true;
                task.Settings.DisallowStartIfOnBatteries=false; task.Settings.StopIfGoingOnBatteries=false;
                task.Settings.ExecutionTimeLimit="PT0S"; task.Settings.MultipleInstances=2;
                task.Settings.RestartInterval="PT1M"; task.Settings.RestartCount=3;
                folder.RegisterTaskDefinition(TaskName,task,6,UserId,null,3,null);
                if(!Enabled) throw new IOException("自动启动任务验证失败");
            } else {
                try { folder.GetTask(TaskName); folder.DeleteTask(TaskName,0); }
                catch(COMException e) { if(e.ErrorCode!=unchecked((int)0x80070002)) throw; }
            }
            // Migrate only our own old Run entry, after successful task registration.
            using(var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key)) {
                key.DeleteValue(Name,false);
            }
            Program.LogLifecycle(enabled ? "autostart-enabled" : "autostart-disabled");
        }
    }

    sealed class Bar : Form
    {
        readonly Settings settings = Settings.Load();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem buckets = new ToolStripMenuItem("选择额度类型");
        readonly ToolTip tooltip = new ToolTip { AutoPopDelay = 20000 };
        readonly System.Windows.Forms.Timer tracker = new System.Windows.Forms.Timer { Interval = 250 };
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 60000 };
        AppServer server;
        readonly AnchorReader anchorReader=new AnchorReader();
        QuotaSnapshot snapshot;
        IntPtr target;
        ComposerAnchor composerAnchor;
        Rectangle lastOwner;
        int lastDpi;
        DateTime anchorValidated = DateTime.MinValue;
        int detailLevel = 2;
        Rectangle currentSlot;
        DateTime anchorChecked = DateTime.MinValue;
        DateTime lastWindowSearch = DateTime.MinValue;
        bool anchorBusy, refreshing, paused, dragging, closing;
        string status = "正在读取 Codex 额度…";
        string error;
        Point dragStart, formStart;
        double scale = 1;
        bool light;
        bool hovered;
        Color surface;
        DateTime surfaceChecked = DateTime.MinValue;
        DateTime stateWritten = DateTime.MinValue;
        long countdownMinute = -1;
        string lastRenderKey;
        public Bar()
        {
            Text = "Codex 额度条"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true; Size = new Size(340, 26);
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Pixel);
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) light = Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1; } catch { }
            surface = light ? Color.FromArgb(249,249,249) : Color.FromArgb(33,33,33);
            settings.Save();
            var refreshItem = new ToolStripMenuItem("立即刷新", null, async delegate { await RefreshQuota(); });
            var pause = new ToolStripMenuItem("暂时隐藏") { CheckOnClick = true };
            pause.CheckedChanged += delegate { paused = pause.Checked; Track(); };
            menu.Items.Add(refreshItem); menu.Items.Add(buckets); menu.Items.Add(pause);
            var appearance = new ToolStripMenuItem("外观");
            foreach (var choice in new[] { new[] {"auto", "融入 Codex 背景"}, new[] {"dark", "深色"}, new[] {"light", "浅色"} }) {
                var key = choice[0]; var item = new ToolStripMenuItem(choice[1]) { Checked = settings.Theme == key };
                item.Click += delegate {
                    settings.Theme = key; settings.Save(); surfaceChecked = DateTime.MinValue;
                    foreach (ToolStripMenuItem entry in appearance.DropDownItems) entry.Checked = entry == item;
                    Track(); Invalidate();
                }; appearance.DropDownItems.Add(item);
            }
            menu.Items.Add(appearance);
            var autoStart = new ToolStripMenuItem("随 Windows 登录启动（自动跟随 Codex）") { Checked = AutoStart.Enabled };
            autoStart.Click += delegate {
                try { AutoStart.Set(!AutoStart.Enabled); autoStart.Checked=AutoStart.Enabled; }
                catch(Exception) { tray.ShowBalloonTip(5000,"Codex 额度条","自动启动设置失败，请稍后重试。",ToolTipIcon.Warning); }
            };
            menu.Opening += delegate { autoStart.Checked=AutoStart.Enabled; };
            menu.Items.Add(autoStart);
            menu.Items.Add("恢复默认位置", null, delegate { settings.CustomPosition = false; settings.AnchorOffsetX = settings.AnchorOffsetY = 0; settings.Save(); Track(); });
            menu.Items.Add("打开说明", null, delegate { Process.Start(new ProcessStartInfo(Path.Combine(Program.Root, "使用说明.md")) { UseShellExecute = true }); });
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("退出额度条", null, delegate { Close(); });
            ContextMenuStrip = menu;
            tray.Icon = SystemIcons.Information; tray.Text = "Codex 额度条：正在连接"; tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.DoubleClick += async delegate { paused = false; pause.Checked = false; await RefreshQuota(); Track(); };
            tracker.Tick += delegate { Track(); };
            refresh.Tick += async delegate { if(Native.IsCodex(target)) await RefreshQuota(); };
            MouseEnter += delegate { hovered = true; Invalidate(); };
            MouseLeave += delegate { hovered = false; Invalidate(); };
            MouseDown += delegate(object sender, MouseEventArgs e) {
                if (e.Button != MouseButtons.Left) return;
                if (e.X > Width - 18*scale) { menu.Show(this, new Point(Width - (int)(18*scale), Height)); return; }
                dragging = true; Capture = true; dragStart = Cursor.Position; formStart = Location;
            };
            MouseMove += delegate { if (dragging) Location = new Point(formStart.X + Cursor.Position.X - dragStart.X, formStart.Y + Cursor.Position.Y - dragStart.Y); };
            MouseUp += delegate {
                if (!dragging) return; dragging = false; Capture = false;
                if (Math.Abs(Cursor.Position.X-dragStart.X) + Math.Abs(Cursor.Position.Y-dragStart.Y) < 4) return;
                if(!currentSlot.IsEmpty) {
                    settings.AnchorOffsetX = (Left-currentSlot.Left)/scale;
                    settings.AnchorOffsetY = (Top-currentSlot.Top-(currentSlot.Height-Height)/2.0)/scale;
                    settings.CustomPosition = true; settings.Save(); Track();
                }
            };
            Shown += delegate { Hide(); tracker.Start(); refresh.Start(); Program.LogLifecycle("watcher-ready"); Track(); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; } }
        async Task RefreshQuota()
        {
            if (refreshing || closing) return; refreshing = true;
            try {
                if (server == null) {
                    var path = Program.FindCodex(); if (path == null) throw new IOException("未找到 Codex，请先安装或启动 Codex");
                    server = new AppServer(); await server.Connect(path);
                }
                snapshot = await server.Read(); error = null;
                settings.Bucket = snapshot.Choose(settings.Bucket);
                status = snapshot.Line(settings.Bucket); UpdateBuckets();
            } catch (Exception e) {
                error = e is TimeoutException ? "连接超时" : "额度连接失败，请确认 Codex 已登录";
                if (snapshot == null) status = error + "（右键重试）";
                if (server != null) { server.Dispose(); server = null; }
            } finally { refreshing = false; if (!closing) { UpdateText(); Invalidate(); Track(); } }
        }
        void UpdateBuckets()
        {
            foreach (ToolStripItem item in buckets.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
            buckets.DropDownItems.Clear();
            foreach (var key in snapshot.Buckets.Keys) {
                var id = key; var name = Convert.ToString(Json.Get(snapshot.Buckets[key], "limitName"));
                var item = new ToolStripMenuItem(String.IsNullOrEmpty(name) ? key : name) { Checked = id == settings.Bucket };
                item.Click += delegate { settings.Bucket = id; settings.Save(); status = snapshot.Line(id); UpdateBuckets(); UpdateText(); Invalidate(); };
                buckets.DropDownItems.Add(item);
            }
        }
        bool Stale { get { return snapshot != null && (error != null || DateTime.Now - snapshot.Updated > TimeSpan.FromMinutes(3)); } }
        string Display { get { return status + (Stale ? "  ·  数据待更新" : ""); } }
        void UpdateText()
        {
            var detail = Display;
            if (snapshot != null) {
                detail += "\n" + (settings.Bucket ?? "Codex") + " · 上次读取 " + snapshot.Updated.ToString("HH:mm:ss");
                foreach (var w in snapshot.Windows(settings.Bucket)) if (w.ResetText != null) detail += "\n" + w.Label + "：" + w.Countdown(DateTime.UtcNow) + " · " + w.ResetText + "（北京时间）";
            }
            if (error != null) detail += "\n" + error;
            tooltip.SetToolTip(this, detail + "\n左键拖动调整位置；右键设置；每分钟刷新");
            var text = "Codex · " + Display; tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        void Track()
        {
            if (Program.StopSignal != null && Program.StopSignal.WaitOne(0)) { Close(); return; }
            long minute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            if (countdownMinute != minute) { countdownMinute = minute; UpdateText(); Invalidate(); }
            if (Program.DebugState && DateTime.Now - stateWritten > TimeSpan.FromSeconds(1)) {
                stateWritten = DateTime.Now;
                try { File.WriteAllText(Path.Combine(Program.Root, "runtime-state.json"), Json.Encode(new { running = true,
                    pid = Process.GetCurrentProcess().Id, handle = Handle.ToInt64(), target = target.ToInt64(), foreground = Native.GetForegroundWindow().ToInt64(), targetFound = target != IntPtr.Zero, visible = Visible,
                    paused = paused, dragging = dragging, anchorBusy = anchorBusy, anchorResult = anchorReader.LastResult, anchorChecked = anchorChecked.ToString("o"), anchorValidated = anchorValidated.ToString("o"),
                    inputLocated = composerAnchor != null && composerAnchor.Valid, detailLevel = detailLevel,
                    slot = new { x = currentSlot.Left, y = currentSlot.Top, width = currentSlot.Width, height = currentSlot.Height },
                    bounds = new { x = Left, y = Top, width = Width, height = Height },
                    display = Display, theme = settings.Theme, background = ColorTranslator.ToHtml(surface), updatedAt = stateWritten.ToString("s") }), Encoding.UTF8); } catch { }
                var renderKey = Display + ":" + Size + ":" + surface + ":" + countdownMinute + ":" + Visible + ":" + detailLevel;
                if (snapshot != null && renderKey != lastRenderKey) {
                    // Render our own component even while the user is in another app; never steal focus.
                    var previewScale = Visible ? scale : 1.75;
                    var previewBackground = Visible ? surface : Color.FromArgb(43,43,43);
                    var previewWindows = snapshot.Windows(settings.Bucket);
                    int previewWidth = BarDesign.Layout(null, Size.Empty, previewScale, previewBackground, false, previewWindows, status, Stale,detailLevel);
                    var previewSize = new Size(previewWidth, (int)(26*previewScale));
                    try { using (var bitmap = new Bitmap(previewSize.Width, previewSize.Height)) {
                        using(var graphics = Graphics.FromImage(bitmap)) BarDesign.Layout(graphics, previewSize, previewScale, previewBackground, false, previewWindows, status, Stale,detailLevel);
                        bitmap.Save(Path.Combine(Program.Root, "额度条预览.png"), System.Drawing.Imaging.ImageFormat.Png);
                        lastRenderKey = renderKey;
                    }} catch { }
                }
            }
            if (closing || dragging) return;
            var foreground = Native.GetForegroundWindow();
            if (Native.IsCodex(foreground) && target != foreground) SetTarget(foreground);
            if (!Native.IsCodex(target) && DateTime.Now - lastWindowSearch > TimeSpan.FromSeconds(2)) {
                lastWindowSearch = DateTime.Now; SetTarget(Native.Windows().FirstOrDefault());
            }
            if(target==IntPtr.Zero && !refreshing && server!=null) { ReleaseServer(); }
            if (target == IntPtr.Zero || paused || Native.IsIconic(target) || (foreground != target && foreground != Handle && !menu.Visible)) { if (Visible) Hide(); return; }
            Native.Rect nativeRect; if (!Native.GetWindowRect(target, out nativeRect)) { Hide(); return; }
            var rect = nativeRect.ToRectangle(); if (rect.Width < 250 || rect.Height < 200) { Hide(); return; }
            var dpi = Native.Dpi(target);
            if(lastOwner.Size!=rect.Size || lastDpi!=dpi) {
                composerAnchor=null; currentSlot=Rectangle.Empty; anchorChecked=DateTime.MinValue;
                if(Visible) Hide();
            }
            lastOwner=rect; lastDpi=dpi;
            if(!anchorBusy && DateTime.Now-anchorChecked>TimeSpan.FromMilliseconds(600)) ReadAnchor(target);
            if(composerAnchor==null || !composerAnchor.Valid || !composerAnchor.Matches(rect,dpi) || DateTime.Now-anchorValidated>TimeSpan.FromSeconds(6)) { if(Visible) Hide(); return; }
            currentSlot=Rectangle.Intersect(composerAnchor.SlotAt(rect),rect);
            currentSlot=Rectangle.Intersect(currentSlot,Screen.FromHandle(target).WorkingArea);
            if(currentSlot.IsEmpty) { Hide(); return; }
            // Toolbar geometry also responds to the app's zoom, unlike monitor DPI alone.
            scale = Math.Max(0.75,Math.Min(3,composerAnchor.Slot.Height/28.0));
            int height = (int)(26 * scale);
            using (var font = new Font("Microsoft YaHei UI", (float)(12 * scale), FontStyle.Regular, GraphicsUnit.Pixel)) {
                if (Math.Abs(Font.Size - font.Size) > 0.1) { var old = Font; Font = (Font)font.Clone(); old.Dispose(); }
            }
            var quotaWindows = snapshot == null ? new List<QuotaWindow>() : snapshot.Windows(settings.Bucket);
            int width=0, previousDetail=detailLevel;
            for(detailLevel=2;detailLevel>=0;detailLevel--) {
                width=BarDesign.Layout(null,Size.Empty,scale,surface,false,quotaWindows,status,Stale,detailLevel);
                if(width<=currentSlot.Width) break;
            }
            if(detailLevel<0) { Hide(); return; }
            var placement=QuotaPlacement.Fit(currentSlot,new Size(width,height),scale,settings.AnchorOffsetX,settings.AnchorOffsetY);
            if(placement.IsEmpty) { Hide(); return; }
            int x=placement.Left,y=placement.Top;
            if(previousDetail!=detailLevel) Invalidate();
            if (Bounds != new Rectangle(x, y, width, height)) {
                Bounds = new Rectangle(x, y, width, height);
                using (var shape = BarDesign.Round(new RectangleF(0,0,width,height), (float)(7*scale))) {
                    var previous = Region; Region = new Region(shape); if (previous != null) previous.Dispose();
                }
            }
            if (DateTime.Now - surfaceChecked > TimeSpan.FromSeconds(2)) {
                surfaceChecked = DateTime.Now;
                Color next = surface;
                if (settings.Theme == "dark") next = Color.FromArgb(33,33,33);
                else if (settings.Theme == "light") next = Color.FromArgb(249,249,249);
                else if (foreground == target && !menu.Visible) next = Native.SurfaceColor(target, Bounds, rect) ?? surface;
                if (next != surface) { surface = next; Invalidate(); }
            }
            // An independent watcher has no owner to keep it above Codex. HWND_TOP can remain
            // behind the foreground app; promote only while our foreground guard allows showing.
            if (!Visible) Show(); Native.SetWindowPos(Handle, new IntPtr(-1), x, y, width, height, 0x0010);
        }
        void SetTarget(IntPtr window)
        {
            bool changed = target != window;
            target = window; composerAnchor = null; currentSlot = Rectangle.Empty; anchorChecked = DateTime.MinValue;
            lastOwner=Rectangle.Empty; lastDpi=0;
            // Keep the watcher alive when Codex closes; an owned native window can be destroyed with its owner.
            Native.Owner(Handle, IntPtr.Zero);
            if(changed && window!=IntPtr.Zero) { var pendingRefresh=RefreshQuota(); }
        }
        async void ReadAnchor(IntPtr window)
        {
            anchorBusy = true; anchorChecked = DateTime.Now;
            try {
                // The accessibility provider can stall; never block the UI or queue more probes.
                var found = await anchorReader.Read(window);
                if (closing || target != window) return;
                Native.Rect wr;
                if (found!=null && Native.GetWindowRect(window, out wr) && found.Matches(wr.ToRectangle(),Native.Dpi(window))) {
                    if(composerAnchor==null || composerAnchor.Slot!=found.Slot) surfaceChecked=DateTime.MinValue;
                    composerAnchor=found; anchorValidated=DateTime.Now;
                }
            } finally { anchorBusy = false; }
            if(!closing) Track();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            BarDesign.Layout(e.Graphics, Size, scale, surface, hovered, snapshot == null ? new List<QuotaWindow>() : snapshot.Windows(settings.Bucket), status, Stale,Math.Max(0,detailLevel));
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Program.LogLifecycle("watcher-stopped");
            closing = true; tracker.Stop(); refresh.Stop(); tray.Visible = false;
            anchorReader.Dispose(); ReleaseServer(); tracker.Dispose(); refresh.Dispose(); tooltip.Dispose(); tray.Dispose(); menu.Dispose();
            if (Program.DebugState) try { File.WriteAllText(Path.Combine(Program.Root, "runtime-state.json"), Json.Encode(new { running = false }), Encoding.UTF8); } catch { }
            base.OnFormClosed(e);
        }
        void ReleaseServer()
        {
            var previous=server; server=null;
            // Pipe teardown can wait for a pending read; never block the window tracker or exit.
            if(previous!=null) Task.Run(delegate { previous.Dispose(); });
        }
    }

    static class Program
    {
        public static string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static EventWaitHandle StopSignal;
        public static bool DebugState;
        public static string FindCodex()
        {
            var config = Path.Combine(Root, "codex-path.txt");
            if (File.Exists(config)) { var path = File.ReadAllText(config).Trim(); if (File.Exists(path)) return path; }
            var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\OpenAI\Codex\bin\codex.exe");
            if (File.Exists(standard)) return standard;
            // A Store-installed Codex bundles its CLI next to Electron resources.
            // Resolve through the running application, without a machine-specific path.
            foreach (var name in new[] { "ChatGPT", "Codex" }) foreach (var running in Process.GetProcessesByName(name)) {
                using (running) try {
                    var app = running.MainModule.FileName;
                    if (name == "ChatGPT" && app.IndexOf("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var bundled = Path.Combine(Path.GetDirectoryName(app), @"resources\codex.exe");
                    if (File.Exists(bundled)) return bundled;
                } catch { }
            }
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
                try { var candidate = Path.Combine(folder.Trim('"'), "codex.exe"); if (File.Exists(candidate)) return candidate; } catch { }
            }
            return null;
        }
        static void Report(string path, object value) { File.WriteAllText(path, Json.Encode(value), Encoding.UTF8); }
        public static void LogLifecycle(string state)
        {
            try {
                var path=Path.Combine(Root,"startup.log");
                var previous=File.Exists(path) ? File.ReadAllLines(path) : new string[0];
                var lines=previous.Skip(Math.Max(0,previous.Length-99)).ToList();
                lines.Add(DateTimeOffset.Now.ToString("o")+" pid="+Process.GetCurrentProcess().Id+" "+state);
                File.WriteAllLines(path,lines,Encoding.UTF8);
            } catch { }
        }
        [STAThread] static int Main(string[] args)
        {
            try { return Run(args); }
            catch(Exception e) { LogLifecycle("fatal "+e.GetType().Name+" code="+e.HResult); return 1; }
        }
        static int Run(string[] args)
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if(args.Length==2 && args[0]=="--anchor-worker") {
                int parentId=Int32.Parse(args[1]);
                Task.Run(delegate { try { using(var parent=Process.GetProcessById(parentId)) parent.WaitForExit(); } catch { } Environment.Exit(0); });
                using(var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8))
                using(var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) {
                    output.AutoFlush=true;
                    string request;
                    while((request=input.ReadLine())!=null) {
                        long handle; if(!Int64.TryParse(request,out handle)) continue;
                        var anchor=Task.Run(() => Native.ReadComposer(new IntPtr(handle))).GetAwaiter().GetResult();
                        anchor.Controls.Clear();
                        output.WriteLine(Json.Encode(anchor));
                    }
                }
                return 0;
            }
            if(args.Contains("--enable-autostart") || args.Contains("--disable-autostart")) {
                try { AutoStart.Set(args.Contains("--enable-autostart")); }
                catch(Exception e) { LogLifecycle("autostart-failed "+e.GetType().Name+" code="+e.HResult); if(!args.Contains("--configure-only")) MessageBox.Show("无法设置自动启动，请查看 startup.log。","Codex 额度条"); return 1; }
                if(args.Contains("--disable-autostart") || args.Contains("--configure-only")) return 0;
            }
            if (args.Contains("--quit")) {
                try { using (var signal = EventWaitHandle.OpenExisting("Local\\CodexQuotaBar-v1-stop")) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                return 0;
            }
            if (args.Contains("--self-test")) { try { SelfTest.Run(); Report(Path.Combine(Root, "test-result.json"), new { passed = true }); return 0; }
                catch (Exception e) { Report(Path.Combine(Root, "test-result.json"), new { passed = false, error = e.Message }); return 1; } }
            if (args.Contains("--diagnose")) return Diagnose().GetAwaiter().GetResult();
            if (args.Contains("--layout-probe")) {
                var windows = Native.Windows(); var window = windows.FirstOrDefault();
                var probe = Task.Run(() => Native.ReadComposer(window));
                if(!probe.Wait(10000)) { Report(Path.Combine(Root,"layout-probe.json"),new { error="timeout" }); return 1; }
                Report(Path.Combine(Root,"layout-probe.json"),probe.Result); return 0;
            }
            bool created; using (var mutex = new Mutex(true, "Local\\CodexQuotaBar-v1", out created)) {
                if (!created) { LogLifecycle("already-running"); return 0; }
                LogLifecycle(args.Contains("--background") ? "background-start" : "manual-start");
                DebugState = args.Contains("--debug-state") || File.Exists(Path.Combine(Root,"enable-debug.flag"));
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (StopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\CodexQuotaBar-v1-stop")) {
                    StopSignal.Reset(); Application.Run(new Bar());
                }
            }
            return 0;
        }
        static async Task<int> Diagnose()
        {
            var windows = Native.Windows(); var composer = Rectangle.Empty;
            if (windows.Count > 0) {
                var anchor = Task.Run(() => Native.Composer(windows[0]));
                if (await Task.WhenAny(anchor, Task.Delay(8000)) == anchor) composer = await anchor;
            }
            try { using (var server = new AppServer()) {
                var executable = FindCodex(); if (executable == null) throw new IOException("未找到 codex.exe");
                await server.Connect(executable); var snapshot = await server.Read(); var id = snapshot.Choose("codex");
                Native.Rect bounds = new Native.Rect(); if (windows.Count > 0) Native.GetWindowRect(windows[0], out bounds);
                Report(Path.Combine(Root, "diagnostics.json"), new { connected = true, windowCount = windows.Count, composerFound = !composer.IsEmpty,
                    composer = new { x = composer.X, y = composer.Y, width = composer.Width, height = composer.Height },
                    window = new { left = bounds.Left, top = bounds.Top, right = bounds.Right, bottom = bounds.Bottom },
                    bucket = id, display = snapshot.Line(id), updatedAt = snapshot.Updated.ToString("s") }); return 0;
            }} catch (Exception e) { Report(Path.Combine(Root, "diagnostics.json"), new { connected = false, windowCount = windows.Count, error = e.GetType().Name }); return 1; }
        }
    }

    static class SelfTest
    {
        static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
        public static void Run()
        {
            var snapshot = QuotaSnapshot.Parse(Json.Decode(@"{""rateLimitsByLimitId"":{""codex_other"":{""primary"":{""usedPercent"":10,""windowDurationMins"":60}},""codex"":{""primary"":{""usedPercent"":21,""windowDurationMins"":10080,""resetsAt"":1791613008},""secondary"":null}}}"));
            Check(snapshot.Choose(null) == "codex", "应优先选择 codex 额度");
            Check(snapshot.Line("codex").StartsWith("周额度剩余 79%"), "已用额度应正确换算为剩余额度");
            Check(snapshot.Windows("codex").Count == 1 && !snapshot.Line("codex").Contains("5小时"), "未返回的窗口不得虚构");
            Check(snapshot.Choose("codex_other") == "codex_other", "保留用户选择的额度类型");
            Check(QuotaWindow.Parse(Json.Decode(@"{""usedPercent"":null}")).Remaining == null, "缺失额度应显示未知");
            Check(QuotaWindow.Parse(Json.Decode(@"{""usedPercent"":120}")).Remaining == 0, "额度不能小于零");
            Check(QuotaWindow.Parse(Json.Decode(@"{""usedPercent"":-20}")).Remaining == 100, "额度不能大于100");
            var dual = QuotaSnapshot.Parse(Json.Decode(@"{""rateLimits"":{""limitId"":""codex"",""primary"":{""usedPercent"":35,""windowDurationMins"":300},""secondary"":{""usedPercent"":60,""windowDurationMins"":10080}}}"));
            Check(dual.Line("codex") == "5小时剩余 65%   ·   周额度剩余 40%", "支持双窗口及旧版格式");
            var empty = QuotaSnapshot.Parse(Json.Decode(@"{""rateLimits"":null}"));
            Check(empty.Choose(null) == null && empty.Line(null).Contains("暂未返回"), "无数据不能显示满额");
            var resetWindow = new QuotaWindow { Reset = 1791613008 };
            var resetUtc = new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(resetWindow.Reset.Value);
            Check(resetWindow.Countdown(resetUtc.AddDays(-2).AddHours(-3)) == "2天3小时后重置", "倒计时显示天和小时");
            Check(resetWindow.Countdown(resetUtc.AddHours(-3).AddMinutes(-12)) == "3小时12分钟后重置", "不足一天显示小时和分钟");
            Check(resetWindow.Countdown(resetUtc.AddSeconds(-20)) == "1分钟后重置", "不足一分钟不显示零分钟");
            Check(resetWindow.Countdown(resetUtc) == "等待额度重置", "已到重置时间不能虚构满额或负数倒计时");
            Check(new QuotaWindow().Countdown(resetUtc) == null, "缺失重置时间不推算倒计时");
            var owner = new Rectangle(2508,48,2544,1620);
            var anchor = new ComposerAnchor { Owner=owner, Dpi=168, Editor=new Rectangle(3160,1476,1247,77), Slot=new Rectangle(3375,1560,698,49) };
            var original=QuotaPlacement.Fit(anchor.Slot,new Size(600,45),1.75,0,0);
            Check(original.Left==3375 && original.Top==1562 && anchor.Slot.Contains(original), "贴合权限与模型按钮之间的工具栏空白");
            var movedOwner=new Rectangle(owner.X-700,owner.Y+50,owner.Width,owner.Height);
            var moved=QuotaPlacement.Fit(anchor.SlotAt(movedOwner),new Size(600,45),1.75,0,0);
            Check(moved.Left==original.Left-700 && moved.Top==original.Top+50, "移动窗口时保持输入框相对位置");
            Check(anchor.Matches(movedOwner,168), "仅移动位置时缓存仍有效");
            Check(!anchor.Matches(new Rectangle(owner.X,owner.Y,2100,1280),168), "窗口缩小时拒绝旧坐标");
            Check(!anchor.Matches(owner,144), "切换屏幕缩放时拒绝旧坐标");
            var narrowSlot=new Rectangle(880,1110,360,42);
            var clamped=QuotaPlacement.Fit(narrowSlot,new Size(250,39),1.5,-500,800);
            Check(narrowSlot.Contains(clamped), "拖动偏移不能越过工具栏边界");
            Check(QuotaPlacement.Fit(narrowSlot,new Size(600,39),1.5,0,0).IsEmpty, "过宽时不能覆盖相邻按钮");
            int fullWidth=BarDesign.Layout(null,Size.Empty,1.75,Color.FromArgb(43,43,43),false,snapshot.Windows("codex"),"",false,2);
            int shortWidth=BarDesign.Layout(null,Size.Empty,1.75,Color.FromArgb(43,43,43),false,snapshot.Windows("codex"),"",false,0);
            Check(fullWidth>narrowSlot.Width && shortWidth<narrowSlot.Width, "窄窗口可降为仅显示额度的紧凑模式");
        }
    }
}
