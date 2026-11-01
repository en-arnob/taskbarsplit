// TaskbarSplit - give every window its own taskbar button (no labels, no Explorer injection).
//
// How it works: the Windows taskbar groups buttons by AppUserModelID (AUMID).
// This tool watches for new top-level windows and stamps a unique per-window
// AUMID on each extra window of an app via SHGetPropertyStoreForWindow.
// The first window of each app keeps its real AUMID so it still sits on the
// pinned icon. Everything is restored when the tool exits or is paused.
//
// Build: build.cmd (uses the C# compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarSplit
{
    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; }
    }

    // Oversized on purpose so it covers PROPVARIANT on both x86 and x64.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(8)] public short boolVal;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    static class Native
    {
        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);

        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public MOUSEINPUT mi; }

        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr hmod, uint tid);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const uint GA_ROOT = 2;
        public const uint GW_OWNER = 4;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int DWMWA_CLOAKED = 14;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint WM_APP = 0x8000;
    }

    // ------------------------------------------------- window property access

    static class WindowProps
    {
        static readonly Guid AppUserModelFmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        public static readonly PropertyKey Id = new PropertyKey(AppUserModelFmt, 5);
        public static readonly PropertyKey PreventPinning = new PropertyKey(AppUserModelFmt, 9);

        const ushort VT_EMPTY = 0, VT_BOOL = 11, VT_LPWSTR = 31;
        static Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        static IPropertyStore Open(IntPtr hwnd)
        {
            IPropertyStore store;
            if (Native.SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out store) != 0) return null;
            return store;
        }

        static void Close(IPropertyStore store)
        {
            if (store != null) Marshal.ReleaseComObject(store);
        }

        public static string GetString(IntPtr hwnd, PropertyKey key)
        {
            IPropertyStore store = Open(hwnd);
            if (store == null) return null;
            try
            {
                PropVariant pv;
                if (store.GetValue(ref key, out pv) != 0) return null;
                string s = (pv.vt == VT_LPWSTR && pv.ptr != IntPtr.Zero) ? Marshal.PtrToStringUni(pv.ptr) : null;
                Native.PropVariantClear(ref pv);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
            finally { Close(store); }
        }

        // value == null removes the property.
        public static bool SetString(IntPtr hwnd, PropertyKey key, string value)
        {
            PropVariant pv = new PropVariant();
            if (value == null) pv.vt = VT_EMPTY;
            else { pv.vt = VT_LPWSTR; pv.ptr = Marshal.StringToCoTaskMemUni(value); }
            try { return Set(hwnd, key, ref pv); }
            finally { Native.PropVariantClear(ref pv); }
        }

        // value == null removes the property.
        public static bool SetBool(IntPtr hwnd, PropertyKey key, bool? value)
        {
            PropVariant pv = new PropVariant();
            if (value.HasValue) { pv.vt = VT_BOOL; pv.boolVal = (short)(value.Value ? -1 : 0); }
            else pv.vt = VT_EMPTY;
            return Set(hwnd, key, ref pv);
        }

        static bool Set(IntPtr hwnd, PropertyKey key, ref PropVariant pv)
        {
            IPropertyStore store = Open(hwnd);
            if (store == null) return false;
            try
            {
                if (store.SetValue(ref key, ref pv) != 0) return false;
                store.Commit();
                return true;
            }
            catch { return false; }
            finally { Close(store); }
        }
    }

    // Set TASKBARSPLIT_DEBUG=1 to write a log to %TEMP%\TaskbarSplit.log.
    static class Log
    {
        static readonly string path = Environment.GetEnvironmentVariable("TASKBARSPLIT_DEBUG") == "1"
            ? Path.Combine(Path.GetTempPath(), "TaskbarSplit.log") : null;
        static readonly object gate = new object();

        public static void Write(string msg)
        {
            if (path == null) return;
            lock (gate)
            {
                try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine); } catch { }
            }
        }
    }

    // ------------------------------------------- double-click to launch pinned

    // Reads the taskbar through UI Automation on a background thread and keeps a
    // snapshot of where the pinned-but-not-running buttons are, so the mouse hook
    // only has to do a rectangle lookup.
    class TaskbarButtons : IDisposable
    {
        // Matches "Visual Studio Code - 3 running windows" in any language: running buttons carry a window count.
        static readonly System.Text.RegularExpressions.Regex RunningPattern =
            new System.Text.RegularExpressions.Regex(@"\s-\s\d+\s");

        volatile Rectangle[] idlePinned = new Rectangle[0];
        volatile Rectangle[] taskbars = new Rectangle[0];
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool stopping;

        public TaskbarButtons()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            wake.Set();
        }

        public void RequestRefresh() { wake.Set(); }

        public bool OverTaskbar(Point p)
        {
            foreach (Rectangle r in taskbars) if (r.Contains(p)) return true;
            return false;
        }

        public bool HitIdlePinned(Point p)
        {
            foreach (Rectangle r in idlePinned) if (r.Contains(p)) return true;
            return false;
        }

        void Loop()
        {
            while (true)
            {
                wake.WaitOne();
                if (stopping) return;
                try { Refresh(); } catch (Exception e) { Log.Write("refresh failed: " + e); }
            }
        }

        void Refresh()
        {
            List<IntPtr> trays = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                StringBuilder cls = new StringBuilder(64);
                Native.GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") trays.Add(h);
                return true;
            }, IntPtr.Zero);

            List<Rectangle> bars = new List<Rectangle>();
            List<Rectangle> idle = new List<Rectangle>();
            System.Windows.Automation.Condition cond = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer");
            System.Windows.Automation.CacheRequest cache = new System.Windows.Automation.CacheRequest();
            cache.Add(System.Windows.Automation.AutomationElement.NameProperty);
            cache.Add(System.Windows.Automation.AutomationElement.BoundingRectangleProperty);
            cache.AutomationElementMode = System.Windows.Automation.AutomationElementMode.None;

            foreach (IntPtr tray in trays)
            {
                Native.RECT wr;
                if (Native.GetWindowRect(tray, out wr)) bars.Add(Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom));

                // The root must be a live element, so fetch it before the cache request is active.
                System.Windows.Automation.AutomationElement root = System.Windows.Automation.AutomationElement.FromHandle(tray);
                System.Windows.Automation.AutomationElementCollection found;
                using (cache.Activate())
                    found = root.FindAll(System.Windows.Automation.TreeScope.Descendants, cond);
                foreach (System.Windows.Automation.AutomationElement b in found)
                {
                    string name = b.Cached.Name ?? "";
                    if (RunningPattern.IsMatch(name)) continue;
                    System.Windows.Rect r = b.Cached.BoundingRectangle;
                    if (r.IsEmpty || r.Width <= 0) continue;
                    idle.Add(new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
                }
            }
            taskbars = bars.ToArray();
            idlePinned = idle.ToArray();
            Log.Write("refresh: " + trays.Count + " taskbars, idle pinned = " + string.Join(" ", Array.ConvertAll(idlePinned, r => r.ToString())));
        }

        public void Dispose()
        {
            stopping = true;
            wake.Set();
        }
    }

    // Low-level mouse hook: a single left click on a pinned app that is not running
    // is swallowed; a second click within the double-click time goes through and launches it.
    class DoubleClickGuard : NativeWindow, IDisposable
    {
        const int WH_MOUSE_LL = 14;
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        const uint LLMHF_INJECTED = 0x1;
        const uint WM_INJECT_DOWN = Native.WM_APP + 2;
        static readonly IntPtr OwnMarker = (IntPtr)0x54425350; // tags input we inject ourselves

        enum State { Idle, Swallowing }

        readonly TaskbarButtons buttons = new TaskbarButtons();
        readonly Native.LowLevelMouseProc proc;
        IntPtr hook;
        State state;
        Point downPoint;
        bool haveFirstClick;
        uint firstClickTime;
        Point firstClickPoint;
        int lastRefreshTick;
        readonly int dragX = Native.GetSystemMetrics(68), dragY = Native.GetSystemMetrics(69);       // SM_CXDRAG/CYDRAG
        readonly int dblX = Native.GetSystemMetrics(36), dblY = Native.GetSystemMetrics(37);         // SM_CXDOUBLECLK/CYDOUBLECLK

        public DoubleClickGuard()
        {
            CreateHandle(new CreateParams());
            proc = HookProc;
            hook = Native.SetWindowsHookEx(WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            Log.Write("mouse hook = " + hook + (hook == IntPtr.Zero ? " error " + Marshal.GetLastWin32Error() : ""));
        }

        IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                Native.MSLLHOOKSTRUCT s = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                bool ours = (s.flags & LLMHF_INJECTED) != 0 && s.extraInfo == OwnMarker;
                if (!ours && Decide(wParam.ToInt32(), s)) return (IntPtr)1;
            }
            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // Returns true to swallow the event.
        bool Decide(int msg, Native.MSLLHOOKSTRUCT s)
        {
            Point p = new Point(s.x, s.y);
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    if (state == State.Swallowing)
                    {
                        // The user is dragging the icon: give the taskbar its button-down after all.
                        if (Math.Abs(p.X - downPoint.X) > dragX || Math.Abs(p.Y - downPoint.Y) > dragY)
                        {
                            state = State.Idle;
                            haveFirstClick = false;
                            Native.PostMessage(Handle, WM_INJECT_DOWN, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    else if (buttons.OverTaskbar(p) && unchecked(Environment.TickCount - lastRefreshTick) > 300)
                    {
                        lastRefreshTick = Environment.TickCount;
                        buttons.RequestRefresh();
                    }
                    return false;

                case WM_LBUTTONDOWN:
                    Log.Write("down at " + p + " flags=" + s.flags + " hitIdle=" + buttons.HitIdlePinned(p) + " taskbar=" + IsTaskbarAt(p) + " firstClick=" + haveFirstClick);
                    if (state == State.Idle && buttons.HitIdlePinned(p) && IsTaskbarAt(p) && !ModifierDown())
                    {
                        if (haveFirstClick &&
                            unchecked(s.time - firstClickTime) <= Native.GetDoubleClickTime() &&
                            Math.Abs(p.X - firstClickPoint.X) <= dblX && Math.Abs(p.Y - firstClickPoint.Y) <= dblY)
                        {
                            haveFirstClick = false;   // second click: let it launch
                            return false;
                        }
                        state = State.Swallowing;
                        downPoint = p;
                        return true;
                    }
                    haveFirstClick = false;
                    return false;

                case WM_LBUTTONUP:
                    if (state == State.Swallowing)
                    {
                        state = State.Idle;
                        haveFirstClick = true;
                        firstClickTime = s.time;
                        firstClickPoint = downPoint;
                        buttons.RequestRefresh();
                        return true;
                    }
                    return false;
            }
            return false;
        }

        static bool IsTaskbarAt(Point p)
        {
            IntPtr h = Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { x = p.X, y = p.Y }), Native.GA_ROOT);
            StringBuilder cls = new StringBuilder(64);
            Native.GetClassName(h, cls, cls.Capacity);
            string c = cls.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }

        static bool ModifierDown()
        {
            return (Native.GetAsyncKeyState(0x10) & 0x8000) != 0     // Shift
                || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0     // Ctrl
                || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;    // Alt
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)WM_INJECT_DOWN)
            {
                Native.INPUT[] input = new Native.INPUT[1];
                input[0].type = 0; // INPUT_MOUSE
                input[0].mi.dwFlags = 0x0002; // MOUSEEVENTF_LEFTDOWN
                input[0].mi.dwExtraInfo = OwnMarker;
                Native.SendInput(1, input, Marshal.SizeOf(typeof(Native.INPUT)));
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            buttons.Dispose();
            DestroyHandle();
        }
    }

    // ----------------------------------------------------------------- config

    class Config
    {
        public bool IncludeMode;                 // false = split all apps except Exclude
        public HashSet<string> Include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool KeepFirstOnPin = true;
        public bool DoubleClickToLaunch;

        public const string DefaultText =
@"; TaskbarSplit settings. Use the tray icon's ""Reload settings"" after editing.
;
; Mode=all      split every app's windows, except apps listed in Exclude
; Mode=include  split only the apps listed in Include
Mode=all

; Comma-separated exe names, for example: Code.exe, chrome.exe
Include=
Exclude=ApplicationFrameHost.exe

; true  = the first window of an app stays on its pinned taskbar icon,
;         every additional window gets its own icon next to it
; false = every window gets its own icon, the pinned icon stays a launcher
KeepFirstOnPin=true

; true = a pinned app that is not running needs a double-click to open.
;        Running apps still switch with a single click.
DoubleClickToLaunch=false
";

        static bool ParseBool(string val, bool fallback)
        {
            if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1") return true;
            if (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val == "0") return false;
            return fallback;
        }

        // Rewrites "key=..." in the ini file, or appends it if missing.
        public static void SetValue(string path, string key, string value)
        {
            List<string> lines = new List<string>();
            try { if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path)); } catch { }
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                int eq = t.IndexOf('=');
                if (t.StartsWith(";") || t.StartsWith("#") || eq < 0) continue;
                if (t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    found = true;
                }
            }
            if (!found) { lines.Add(""); lines.Add(key + "=" + value); }
            try { File.WriteAllLines(path, lines.ToArray()); } catch { }
        }

        public bool Applies(string exeName)
        {
            if (Exclude.Contains(exeName)) return false;
            return !IncludeMode || Include.Contains(exeName);
        }

        public static Config Load(string path)
        {
            Config c = new Config();
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, DefaultText.Replace("\r\n", "\n").Replace("\n", "\r\n")); } catch { }
            }
            string[] lines;
            try { lines = File.Exists(path) ? File.ReadAllLines(path) : DefaultText.Split('\n'); }
            catch { lines = DefaultText.Split('\n'); }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "mode": c.IncludeMode = val.Equals("include", StringComparison.OrdinalIgnoreCase); break;
                    case "include": AddList(c.Include, val); break;
                    case "exclude": AddList(c.Exclude, val); break;
                    case "keepfirstonpin": c.KeepFirstOnPin = ParseBool(val, true); break;
                    case "doubleclicktolaunch": c.DoubleClickToLaunch = ParseBool(val, false); break;
                }
            }
            return c;
        }

        static void AddList(HashSet<string> set, string val)
        {
            foreach (string part in val.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length > 0) set.Add(s);
            }
        }
    }

    // --------------------------------------------------------------- splitter

    class Splitter
    {
        public const string Prefix = "TaskbarSplit.";

        class Tracked
        {
            public string Group;
            public string OriginalId;   // window-level AUMID before we touched it (null = none)
            public bool Split;          // true if we stamped our own AUMID on it
        }

        readonly Dictionary<IntPtr, Tracked> tracked = new Dictionary<IntPtr, Tracked>();
        readonly Dictionary<string, IntPtr> primary = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        public Config Config;

        public int SplitCount
        {
            get { int n = 0; foreach (Tracked t in tracked.Values) if (t.Split) n++; return n; }
        }

        public void Forget(IntPtr hwnd)
        {
            tracked.Remove(hwnd);
        }

        public void ScanAll()
        {
            Prune();
            List<IntPtr> list = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l) { list.Add(h); return true; }, IntPtr.Zero);
            foreach (IntPtr h in list) Handle(h);
        }

        public void Handle(IntPtr hwnd)
        {
            if (tracked.ContainsKey(hwnd)) return;
            if (!IsTaskbarWindow(hwnd)) return;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == ownPid) return;
            string exe = GetExePath(pid);
            if (exe == null) return;
            if (!Config.Applies(Path.GetFileName(exe))) return;

            string original = WindowProps.GetString(hwnd, WindowProps.Id);
            if (original != null && original.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // Leftover from a previous run that did not exit cleanly.
                WindowProps.SetString(hwnd, WindowProps.Id, null);
                WindowProps.SetBool(hwnd, WindowProps.PreventPinning, null);
                original = null;
            }

            Tracked t = new Tracked();
            t.OriginalId = original;
            t.Group = original != null ? "id:" + original : "exe:" + exe;

            IntPtr first;
            bool needPrimary = Config.KeepFirstOnPin &&
                (!primary.TryGetValue(t.Group, out first) || !tracked.ContainsKey(first) || !Native.IsWindow(first));

            if (needPrimary)
            {
                primary[t.Group] = hwnd;
            }
            else
            {
                // PreventPinning first so a split button never offers to pin a throwaway ID.
                WindowProps.SetBool(hwnd, WindowProps.PreventPinning, true);
                t.Split = WindowProps.SetString(hwnd, WindowProps.Id, Prefix + hwnd.ToInt64().ToString("X"));
                if (!t.Split) WindowProps.SetBool(hwnd, WindowProps.PreventPinning, null);
            }
            tracked[hwnd] = t;
        }

        public void RestoreAll()
        {
            foreach (KeyValuePair<IntPtr, Tracked> kv in tracked)
            {
                if (!kv.Value.Split || !Native.IsWindow(kv.Key)) continue;
                WindowProps.SetString(kv.Key, WindowProps.Id, kv.Value.OriginalId);
                WindowProps.SetBool(kv.Key, WindowProps.PreventPinning, null);
            }
            tracked.Clear();
            primary.Clear();
        }

        void Prune()
        {
            List<IntPtr> dead = new List<IntPtr>();
            foreach (IntPtr h in tracked.Keys) if (!Native.IsWindow(h)) dead.Add(h);
            foreach (IntPtr h in dead) tracked.Remove(h);
        }

        static readonly HashSet<string> SkipClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "ApplicationFrameWindow", "Windows.UI.Core.CoreWindow"
        };

        static bool IsTaskbarWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            if (Native.GetAncestor(h, Native.GA_ROOT) != h) return false;

            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_APPWINDOW) == 0)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
                if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
            }

            int cloaked;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;

            StringBuilder cls = new StringBuilder(256);
            Native.GetClassName(h, cls, cls.Capacity);
            return !SkipClasses.Contains(cls.ToString());
        }

        static string GetExePath(uint pid)
        {
            IntPtr hp = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return Native.QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { Native.CloseHandle(hp); }
        }
    }

    // ------------------------------------------------------------------- app

    class App : NativeWindow, IDisposable
    {
        const uint WM_PROCESS_PENDING = Native.WM_APP + 1;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "TaskbarSplit";

        readonly string configPath;
        readonly Splitter splitter = new Splitter();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer rescanTimer = new System.Windows.Forms.Timer();
        readonly Native.WinEventProc hookProc;   // kept alive for the native hook
        readonly List<IntPtr> hooks = new List<IntPtr>();
        readonly Queue<IntPtr> pending = new Queue<IntPtr>();
        readonly uint taskbarCreatedMsg;
        bool postScheduled;
        bool enabled = true;
        DoubleClickGuard guard;   // only created when the option is on, so UI Automation is never loaded otherwise
        ToolStripMenuItem enabledItem, startupItem, doubleClickItem;

        public App()
        {
            configPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "TaskbarSplit.ini");
            splitter.Config = Config.Load(configPath);
            taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
            CreateParams cp = new CreateParams();
            cp.Caption = Program.WindowTitle;   // lets "TaskbarSplit.exe --exit" find us
            CreateHandle(cp);

            hookProc = OnWinEvent;
            uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, hookProc, 0, 0, flags));
            hooks.Add(Native.SetWinEventHook(Native.EVENT_OBJECT_UNCLOAKED, Native.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, hookProc, 0, 0, flags));

            // Safety net for windows that only become taskbar-eligible some time after being shown.
            rescanTimer.Interval = 3000;
            rescanTimer.Tick += delegate { if (enabled) splitter.ScanAll(); };
            rescanTimer.Start();

            BuildTray();
            splitter.ScanAll();
            ApplyDoubleClick();
            TrimMemory();
        }

        void ApplyDoubleClick()
        {
            bool want = splitter.Config.DoubleClickToLaunch;
            if (want && guard == null) guard = new DoubleClickGuard();
            else if (!want && guard != null) { guard.Dispose(); guard = null; }
            if (doubleClickItem != null) doubleClickItem.Checked = want;
        }

        void ToggleDoubleClick()
        {
            splitter.Config.DoubleClickToLaunch = !splitter.Config.DoubleClickToLaunch;
            if (!File.Exists(configPath)) Config.Load(configPath);
            Config.SetValue(configPath, "DoubleClickToLaunch", splitter.Config.DoubleClickToLaunch ? "true" : "false");
            ApplyDoubleClick();
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            if (evt == Native.EVENT_OBJECT_DESTROY) { splitter.Forget(hwnd); return; }
            if (!enabled) return;
            pending.Enqueue(hwnd);
            if (!postScheduled)
            {
                postScheduled = true;
                Native.PostMessage(Handle, WM_PROCESS_PENDING, IntPtr.Zero, IntPtr.Zero);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)WM_PROCESS_PENDING)
            {
                postScheduled = false;
                while (pending.Count > 0)
                {
                    IntPtr h = pending.Dequeue();
                    if (enabled) splitter.Handle(h);
                }
                return;
            }
            if (m.Msg == 0x0010) // WM_CLOSE, sent by "TaskbarSplit.exe --exit": exit cleanly and restore
            {
                Application.Exit();
                return;
            }
            if (taskbarCreatedMsg != 0 && m.Msg == (int)taskbarCreatedMsg)
            {
                // Explorer restarted: re-stamp everything.
                if (enabled) { splitter.RestoreAll(); splitter.ScanAll(); }
            }
            base.WndProc(ref m);
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem("TaskbarSplit");
            title.Enabled = false;
            menu.Items.Add(title);
            menu.Items.Add(new ToolStripSeparator());

            enabledItem = new ToolStripMenuItem("Split windows", null, delegate { SetEnabled(!enabled); });
            enabledItem.Checked = true;
            menu.Items.Add(enabledItem);

            doubleClickItem = new ToolStripMenuItem("Double-click to open pinned apps", null, delegate { ToggleDoubleClick(); });
            doubleClickItem.Checked = splitter.Config.DoubleClickToLaunch;
            menu.Items.Add(doubleClickItem);

            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            startupItem.Checked = IsStartupEnabled();
            menu.Items.Add(startupItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Edit settings", null, delegate
            {
                if (!File.Exists(configPath)) Config.Load(configPath);
                try { Process.Start("notepad.exe", "\"" + configPath + "\""); } catch { }
            }));
            menu.Items.Add(new ToolStripMenuItem("Reload settings", null, delegate { Reload(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Application.Exit(); }));

            tray.ContextMenuStrip = menu;
            tray.Icon = MakeIcon();
            tray.Text = "TaskbarSplit";
            tray.DoubleClick += delegate { SetEnabled(!enabled); };
            tray.Visible = true;
        }

        void SetEnabled(bool on)
        {
            enabled = on;
            enabledItem.Checked = on;
            tray.Text = on ? "TaskbarSplit" : "TaskbarSplit (paused)";
            if (on) splitter.ScanAll(); else splitter.RestoreAll();
        }

        void Reload()
        {
            splitter.RestoreAll();
            splitter.Config = Config.Load(configPath);
            if (enabled) splitter.ScanAll();
            ApplyDoubleClick();
        }

        bool IsStartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        void ToggleStartup()
        {
            bool on = IsStartupEnabled();
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.DeleteValue(RunValue, false);
                else k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            }
            startupItem.Checked = IsStartupEnabled();
        }

        static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush a = new SolidBrush(Color.FromArgb(0x3B, 0x8E, 0xEA)))
                using (Brush b = new SolidBrush(Color.FromArgb(0x9C, 0xC9, 0xFF)))
                {
                    g.FillRectangle(a, 2, 6, 12, 20);
                    g.FillRectangle(b, 18, 6, 12, 20);
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        public void Dispose()
        {
            rescanTimer.Stop();
            if (guard != null) { guard.Dispose(); guard = null; }
            foreach (IntPtr h in hooks) if (h != IntPtr.Zero) Native.UnhookWinEvent(h);
            hooks.Clear();
            splitter.RestoreAll();
            tray.Visible = false;
            tray.Dispose();
            DestroyHandle();
        }
    }

    static class Program
    {
        public const string WindowTitle = "TaskbarSplit.MessageWindow";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);

        [STAThread]
        static void Main(string[] args)
        {
            bool exitRequested = false;
            foreach (string a in args)
                if (a.Equals("--exit", StringComparison.OrdinalIgnoreCase) || a.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                    exitRequested = true;

            if (exitRequested)
            {
                IntPtr running = FindWindow(null, WindowTitle);
                if (running != IntPtr.Zero) Native.PostMessage(running, 0x0010, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            // Physical-pixel coordinates everywhere, so mouse-hook points match taskbar button rectangles.
            try { Native.SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { }

            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\TaskbarSplit.SingleInstance", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (App app = new App())
                {
                    Application.Run();
                }
            }
        }
    }
}
