using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardDebouncerApp
{
    // ==========================================
    // 1. Win32 API Definitions & Structs
    // ==========================================
    internal static class NativeMethods
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_INPUT = 0x00FF;
        public const int WM_INPUT_DEVICE_CHANGE = 0x00FE;

        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RIDEV_DEVNOTIFY = 0x00002000;
        public const uint RIM_TYPEKEYBOARD = 1;

        public const uint RIDI_DEVICENAME = 0x20000007;
        public const uint RID_INPUT = 0x10000003;

        public const uint RI_KEY_BREAK = 1;

        public const uint LLKHF_INJECTED = 0x10;
        public const uint LLKHF_LOWER_IL_INJECTED = 0x02;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList(IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        public static extern IntPtr GetThreadDesktop(int dwThreadId);

        [DllImport("kernel32.dll")]
        public static extern int GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, ref int lpnLengthNeeded);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct RAWINPUT
        {
            [FieldOffset(0)]
            public RAWINPUTHEADER header;
            [FieldOffset(24)] // 24 bytes header on 64-bit Windows
            public RAWKEYBOARD keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }
    }

    // ==========================================
    // 2. Data Models
    // ==========================================
    public class KeyboardDeviceItem
    {
        public string HardwareId { get; set; } // e.g. "VID_1A2C&PID_95F4" or "ALL"
        public string DisplayName { get; set; }
        public int EndpointCount { get; set; }
        public List<IntPtr> Handles { get; set; }

        public KeyboardDeviceItem()
        {
            Handles = new List<IntPtr>();
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    public class KeyRecord
    {
        public ushort Vk;
        public bool IsDown;
        public double LastHookDown;
        public double LastHookUp;
        public double InitialDownTime;
        public bool SuppressedDown;
        public bool SuppressedUp;
        public IntPtr LastDevice;
        public bool? IsTargetDevice;
        public int BounceCount;

        public KeyRecord(ushort vk)
        {
            Vk = vk;
        }
    }

    public class BlockedBounceEvent
    {
        public ushort Vk { get; set; }
        public string KeyName { get; set; }
        public string Reason { get; set; }
        public double IntervalMs { get; set; }
        public DateTime Timestamp { get; set; }
        public string TargetPattern { get; set; }
    }

    // ==========================================
    // 3. Message-Only Raw Input Window
    // ==========================================
    public class RawInputSinkWindow : NativeWindow
    {
        private readonly DebounceEngine engine;

        public RawInputSinkWindow(DebounceEngine engine)
        {
            this.engine = engine;
            CreateParams cp = new CreateParams();
            cp.Parent = (IntPtr)(-3); // HWND_MESSAGE
            this.CreateHandle(cp);

            // Register Raw Input Sink
            NativeMethods.RAWINPUTDEVICE[] rid = new NativeMethods.RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 1;
            rid[0].usUsage = 6;
            rid[0].dwFlags = NativeMethods.RIDEV_INPUTSINK | NativeMethods.RIDEV_DEVNOTIFY;
            rid[0].hwndTarget = this.Handle;

            NativeMethods.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE)));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_INPUT)
            {
                engine.ProcessRawInput(m.LParam);
            }
            else if (m.Msg == NativeMethods.WM_INPUT_DEVICE_CHANGE)
            {
                engine.RefreshDevices();
            }
            base.WndProc(ref m);
        }
    }

    // ==========================================
    // 4. Core Debounce Engine
    // ==========================================
    public class DebounceEngine
    {
        private IntPtr hookHandle = IntPtr.Zero;
        private NativeMethods.HookProc hookDelegate;
        private RawInputSinkWindow sinkWindow;

        public readonly object EngineLock = new object();
        public readonly Dictionary<ushort, KeyRecord> KeyRecords = new Dictionary<ushort, KeyRecord>();
        public readonly HashSet<IntPtr> TargetHandles = new HashSet<IntPtr>();
        public readonly Dictionary<IntPtr, string> AllKeyboards = new Dictionary<IntPtr, string>();

        public string TargetPattern { get; set; }
        public int DebounceThresholdMs { get; set; }
        public bool IsActive { get; set; }

        public long TotalKeystrokes { get; private set; }
        public long TotalBlocked { get; private set; }

        public event Action<BlockedBounceEvent> BounceBlocked;
        public event Action DevicesRefreshed;

        public string LogFilePath { get; set; }

        public DebounceEngine(string targetPattern, int debounceThresholdMs)
        {
            TargetPattern = targetPattern ?? "VID_1A2C&PID_95F4";
            DebounceThresholdMs = debounceThresholdMs;
            IsActive = true;
            LogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debounce.log");
        }

        public void Start()
        {
            try
            {
                IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    NativeMethods.SetThreadDesktop(hDesk);
                }
            }
            catch { }

            sinkWindow = new RawInputSinkWindow(this);
            RefreshDevices();
            InstallHook();
        }

        public void Stop()
        {
            UninstallHook();
            if (sinkWindow != null)
            {
                sinkWindow.DestroyHandle();
                sinkWindow = null;
            }
        }

        public void ResetStats()
        {
            lock (EngineLock)
            {
                TotalKeystrokes = 0;
                TotalBlocked = 0;
                foreach (var rec in KeyRecords.Values)
                {
                    rec.BounceCount = 0;
                }
            }
        }

        public void UpdateTargetHandles()
        {
            lock (EngineLock)
            {
                TargetHandles.Clear();
                foreach (var kvp in AllKeyboards)
                {
                    if (TargetPattern == "ALL" || kvp.Value.IndexOf(TargetPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TargetHandles.Add(kvp.Key);
                    }
                }
                foreach (var rec in KeyRecords.Values)
                {
                    rec.IsTargetDevice = null;
                }
            }
        }

        public void RefreshDevices()
        {
            uint nDevices = 0;
            NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref nDevices, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST)));
            if (nDevices == 0) return;

            IntPtr pList = Marshal.AllocHGlobal((int)(nDevices * Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST))));
            try
            {
                NativeMethods.GetRawInputDeviceList(pList, ref nDevices, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST)));

                Dictionary<IntPtr, string> newAll = new Dictionary<IntPtr, string>();

                int structSize = Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST));
                for (int i = 0; i < nDevices; i++)
                {
                    IntPtr entryPtr = new IntPtr(pList.ToInt64() + i * structSize);
                    NativeMethods.RAWINPUTDEVICELIST dev = (NativeMethods.RAWINPUTDEVICELIST)Marshal.PtrToStructure(entryPtr, typeof(NativeMethods.RAWINPUTDEVICELIST));

                    if (dev.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                    {
                        uint nameLen = 0;
                        NativeMethods.GetRawInputDeviceInfo(dev.hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref nameLen);
                        if (nameLen > 0)
                        {
                            IntPtr nameBuf = Marshal.AllocHGlobal((int)nameLen * 2);
                            try
                            {
                                NativeMethods.GetRawInputDeviceInfo(dev.hDevice, NativeMethods.RIDI_DEVICENAME, nameBuf, ref nameLen);
                                string devName = Marshal.PtrToStringUni(nameBuf) ?? "";
                                newAll[dev.hDevice] = devName;
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(nameBuf);
                            }
                        }
                    }
                }

                lock (EngineLock)
                {
                    AllKeyboards.Clear();
                    foreach (var kvp in newAll)
                    {
                        AllKeyboards[kvp.Key] = kvp.Value;
                    }
                }

                UpdateTargetHandles();

                if (DevicesRefreshed != null)
                {
                    DevicesRefreshed();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pList);
            }
        }

        public bool IsTargetDevice(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return false;
            lock (EngineLock)
            {
                if (TargetPattern == "ALL") return true;
                if (TargetHandles.Contains(hDevice)) return true;

                string name;
                if (AllKeyboards.TryGetValue(hDevice, out name))
                {
                    if (name.IndexOf(TargetPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TargetHandles.Add(hDevice);
                        return true;
                    }
                }
            }
            return false;
        }

        public void ProcessRawInput(IntPtr hRawInput)
        {
            uint dwSize = 0;
            NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER)));
            if (dwSize == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
            try
            {
                if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, buffer, ref dwSize, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER))) == dwSize)
                {
                    NativeMethods.RAWINPUT raw = (NativeMethods.RAWINPUT)Marshal.PtrToStructure(buffer, typeof(NativeMethods.RAWINPUT));
                    if (raw.header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                    {
                        IntPtr hDev = raw.header.hDevice;
                        ushort vKey = raw.keyboard.VKey;
                        bool isBreak = (raw.keyboard.Flags & NativeMethods.RI_KEY_BREAK) != 0;

                        bool isTarget = IsTargetDevice(hDev);
                        lock (EngineLock)
                        {
                            KeyRecord rec;
                            if (!KeyRecords.TryGetValue(vKey, out rec))
                            {
                                rec = new KeyRecord(vKey);
                                KeyRecords[vKey] = rec;
                            }
                            rec.LastDevice = hDev;
                            rec.IsTargetDevice = isTarget;
                            rec.IsDown = !isBreak;
                        }
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void InstallHook()
        {
            if (hookHandle == IntPtr.Zero)
            {
                hookDelegate = new NativeMethods.HookProc(HookCallback);
                using (Process curProcess = Process.GetCurrentProcess())
                using (ProcessModule curModule = curProcess.MainModule)
                {
                    hookHandle = NativeMethods.SetWindowsHookEx(
                        NativeMethods.WH_KEYBOARD_LL,
                        hookDelegate,
                        NativeMethods.GetModuleHandle(curModule.ModuleName),
                        0
                    );
                }
            }
        }

        private void UninstallHook()
        {
            if (hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && IsActive)
            {
                NativeMethods.KBDLLHOOKSTRUCT kb = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));
                uint vk = kb.vkCode;
                uint flags = kb.flags;

                // Never block injected inputs (software macros, on-screen keyboard, remote desktop)
                if ((flags & (NativeMethods.LLKHF_INJECTED | NativeMethods.LLKHF_LOWER_IL_INJECTED)) != 0)
                {
                    return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
                }

                int msg = wParam.ToInt32();
                bool isDown = (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN);
                bool isUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);
                double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                double thresholdSec = (double)DebounceThresholdMs / 1000.0;

                lock (EngineLock)
                {
                    TotalKeystrokes++;
                    KeyRecord rec;
                    if (!KeyRecords.TryGetValue((ushort)vk, out rec))
                    {
                        rec = new KeyRecord((ushort)vk);
                        KeyRecords[(ushort)vk] = rec;
                    }

                    // If key belongs to another device (e.g. secondary keyboard, mouse, macro pad): NEVER BLOCK!
                    if (rec.IsTargetDevice == false)
                    {
                        if (isDown) rec.LastHookDown = now;
                        else if (isUp) rec.LastHookUp = now;
                        return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
                    }

                    // Target Keyboard Debounce Logic
                    if (isDown)
                    {
                        // Check A: Release bounce (Break bounce)
                        double timeSinceUp = now - rec.LastHookUp;
                        if (rec.LastHookUp > 0 && timeSinceUp > 0.0 && timeSinceUp < thresholdSec)
                        {
                            rec.SuppressedDown = true;
                            rec.BounceCount++;
                            TotalBlocked++;
                            NotifyBounceBlocked((ushort)vk, "Release bounce", timeSinceUp * 1000.0);
                            return (IntPtr)1; // DROP BOUNCE
                        }

                        // Check B: Make bounce (contact flutter on press)
                        double timeSinceDown = now - rec.LastHookDown;
                        double timeSinceFirst = now - rec.InitialDownTime;
                        if (rec.IsDown && rec.LastHookDown > 0 && timeSinceDown > 0.0 && timeSinceDown < thresholdSec)
                        {
                            if (timeSinceFirst < 0.200) // Not typematic repeat
                            {
                                rec.SuppressedDown = true;
                                rec.BounceCount++;
                                TotalBlocked++;
                                NotifyBounceBlocked((ushort)vk, "Make bounce", timeSinceDown * 1000.0);
                                return (IntPtr)1; // DROP BOUNCE
                            }
                        }

                        // Normal KeyDown
                        rec.SuppressedDown = false;
                        if (!rec.IsDown || (now - rec.LastHookDown > 0.150))
                        {
                            rec.InitialDownTime = now;
                        }
                        rec.LastHookDown = now;
                        rec.IsDown = true;
                        return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
                    }
                    else if (isUp)
                    {
                        // Drop matching KeyUp if KeyDown was dropped as a bounce
                        if (rec.SuppressedDown)
                        {
                            rec.SuppressedDown = false;
                            return (IntPtr)1;
                        }

                        // Suppress spurious micro-release flutter on make (< 20ms)
                        double timeSinceDown = now - rec.LastHookDown;
                        if (timeSinceDown > 0.0 && timeSinceDown < Math.Min(0.020, thresholdSec * 0.4))
                        {
                            rec.SuppressedUp = true;
                            return (IntPtr)1; // Suppress contact chatter release
                        }

                        rec.SuppressedUp = false;
                        rec.LastHookUp = now;
                        rec.IsDown = false;
                        return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
                    }
                }
            }
            return NativeMethods.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        private void NotifyBounceBlocked(ushort vk, string reason, double intervalMs)
        {
            var evt = new BlockedBounceEvent
            {
                Vk = vk,
                KeyName = GetVkName(vk),
                Reason = reason,
                IntervalMs = intervalMs,
                Timestamp = DateTime.Now,
                TargetPattern = TargetPattern
            };

            try
            {
                string logLine = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [BLOCKED] Key '{1}' (VK 0x{2:X2}) {3} ({4:F1}ms) on {5}",
                    evt.Timestamp, evt.KeyName, evt.Vk, evt.Reason, evt.IntervalMs, evt.TargetPattern);
                File.AppendAllText(LogFilePath, logLine + Environment.NewLine, Encoding.UTF8);
            }
            catch { }

            if (BounceBlocked != null)
            {
                BounceBlocked(evt);
            }
        }

        private static readonly Dictionary<ushort, string> VkNames = new Dictionary<ushort, string>
        {
            { 0x08, "BACKSPACE" }, { 0x09, "TAB" }, { 0x0D, "ENTER" }, { 0x10, "SHIFT" },
            { 0x11, "CTRL" }, { 0x12, "ALT" }, { 0x13, "PAUSE" }, { 0x14, "CAPS_LOCK" },
            { 0x1B, "ESC" }, { 0x20, "SPACE" }, { 0x21, "PAGE_UP" }, { 0x22, "PAGE_DOWN" },
            { 0x23, "END" }, { 0x24, "HOME" }, { 0x25, "LEFT" }, { 0x26, "UP" },
            { 0x27, "RIGHT" }, { 0x28, "DOWN" }, { 0x2D, "INSERT" }, { 0x2E, "DELETE" },
            { 0x5B, "LWIN" }, { 0x5C, "RWIN" }, { 0x5D, "APPS" },
            { 0xBA, ";" }, { 0xBB, "=" }, { 0xBC, "," }, { 0xBD, "-" },
            { 0xBE, "." }, { 0xBF, "/" }, { 0xC0, "`" }, { 0xDB, "[" },
            { 0xDC, "\\" }, { 0xDD, "]" }, { 0xDE, "'" }
        };

        public static string GetVkName(ushort vk)
        {
            if (VkNames.ContainsKey(vk)) return VkNames[vk];
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "NUM_" + (vk - 0x60);
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            return string.Format("0x{0:X2}", vk);
        }
    }

    // ==========================================
    // 5. Clean, Professional Windows GUI Form
    // ==========================================
    public class MainForm : Form
    {
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "KeyboardDebouncer";

        private readonly DebounceEngine engine;
        private readonly DebouncerAppContext appContext;

        private ComboBox cmbDevices;
        private Button btnRefreshDevices;
        private Label lblDeviceEndpoints;
        private NumericUpDown numDebounce;
        private CheckBox chkStartWithWindows;
        private CheckBox chkMinimizeToTray;
        private Label lblStatusBadge;
        private Button btnToggleActive;
        private Button btnResetStats;
        private Label lblStatsTotal;
        private Label lblStatsBlocked;
        private Label lblTopBounced;
        private ListBox lstLog;
        private Button btnClearLog;
        private Button btnOpenLogFile;

        private System.Windows.Forms.Timer uiTimer;
        private readonly Queue<string> logQueue = new Queue<string>();
        private bool isInitializing = true;
        private bool isUpdatingDropdown = false;
        private string configPath;

        public MainForm(DebounceEngine engine, DebouncerAppContext appContext)
        {
            this.engine = engine;
            this.appContext = appContext;
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

            InitializeComponents();
            LoadConfig();

            chkStartWithWindows.Checked = IsStartWithWindows();
            PopulateDeviceDropdown();

            engine.BounceBlocked += Engine_BounceBlocked;
            engine.DevicesRefreshed += () =>
            {
                if (this.InvokeRequired)
                {
                    this.BeginInvoke(new Action(PopulateDeviceDropdown));
                }
                else
                {
                    PopulateDeviceDropdown();
                }
            };

            uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
            uiTimer.Tick += UiTimer_Tick;
            uiTimer.Start();

            isInitializing = false;
        }

        private void Engine_BounceBlocked(BlockedBounceEvent evt)
        {
            string line = string.Format("[{0:HH:mm:ss.fff}] BLOCKED '{1}' ({2}, {3:F1}ms)",
                evt.Timestamp, evt.KeyName, evt.Reason, evt.IntervalMs);

            lock (logQueue)
            {
                logQueue.Enqueue(line);
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            lblStatsTotal.Text = "Monitored Keystrokes: " + engine.TotalKeystrokes.ToString("N0");
            lblStatsBlocked.Text = "Bounces Blocked: " + engine.TotalBlocked.ToString("N0");

            lock (engine.EngineLock)
            {
                List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>();
                foreach (var kvp in engine.KeyRecords)
                {
                    if (kvp.Value.BounceCount > 0)
                    {
                        list.Add(new KeyValuePair<string, int>(DebounceEngine.GetVkName(kvp.Key), kvp.Value.BounceCount));
                    }
                }
                list.Sort((a, b) => b.Value.CompareTo(a.Value));

                if (list.Count > 0)
                {
                    StringBuilder sb = new StringBuilder("Top Bounced Keys: ");
                    for (int i = 0; i < Math.Min(4, list.Count); i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(string.Format("{0} ({1}x)", list[i].Key, list[i].Value));
                    }
                    lblTopBounced.Text = sb.ToString();
                }
                else
                {
                    lblTopBounced.Text = "Top Bounced Keys: None detected yet";
                }
            }

            // Drain log queue safely
            List<string> itemsToAdd = new List<string>();
            lock (logQueue)
            {
                while (logQueue.Count > 0)
                {
                    itemsToAdd.Add(logQueue.Dequeue());
                }
            }
            if (itemsToAdd.Count > 0)
            {
                lstLog.BeginUpdate();
                for (int i = 0; i < itemsToAdd.Count; i++)
                {
                    lstLog.Items.Insert(0, itemsToAdd[i]);
                }
                while (lstLog.Items.Count > 100)
                {
                    lstLog.Items.RemoveAt(lstLog.Items.Count - 1);
                }
                lstLog.EndUpdate();
            }
        }

        private static string GetVendorName(string vid)
        {
            switch (vid.ToUpper())
            {
                case "1A2C": return "China Resource Semico (Mechanical/USB Keyboard)";
                case "0483": return "STMicroelectronics (HID Device / Controller)";
                case "046D": return "Logitech";
                case "1532": return "Razer";
                case "045E": return "Microsoft";
                case "1B1C": return "Corsair";
                case "093A": return "PixArt Imaging";
                case "05AC": return "Apple";
                case "04F2": return "Chicony";
                case "04B3": return "IBM / Lenovo";
                case "413C": return "Dell";
                case "03F0": return "HP";
                case "04D9": return "Holtek Semiconductor";
                case "320F": return "Keychron / NuPhy";
                case "258A": return "SinoWealth Keyboard";
                case "1038": return "SteelSeries";
                default: return "Keyboard Device";
            }
        }

        public void PopulateDeviceDropdown()
        {
            if (isUpdatingDropdown) return;
            isUpdatingDropdown = true;
            try
            {
                Dictionary<string, KeyboardDeviceItem> groups = new Dictionary<string, KeyboardDeviceItem>(StringComparer.OrdinalIgnoreCase);

                lock (engine.EngineLock)
                {
                    foreach (var kvp in engine.AllKeyboards)
                    {
                        string devName = kvp.Value;
                        Match m = Regex.Match(devName, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                        if (m.Success)
                        {
                            string vid = m.Groups[1].Value.ToUpper();
                            string pid = m.Groups[2].Value.ToUpper();
                            string hwId = "VID_" + vid + "&PID_" + pid;

                            KeyboardDeviceItem item;
                            if (!groups.TryGetValue(hwId, out item))
                            {
                                string vendor = GetVendorName(vid);
                                item = new KeyboardDeviceItem
                                {
                                    HardwareId = hwId,
                                    DisplayName = string.Format("{0} ({1})", vendor, hwId),
                                    EndpointCount = 0
                                };
                                groups[hwId] = item;
                            }
                            item.EndpointCount++;
                            item.Handles.Add(kvp.Key);
                        }
                    }
                }

                cmbDevices.Items.Clear();
                int selectedIdx = 0;
                int idx = 0;
                foreach (var item in groups.Values)
                {
                    cmbDevices.Items.Add(item);
                    if (string.Equals(item.HardwareId, engine.TargetPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIdx = idx;
                    }
                    idx++;
                }

                var allItem = new KeyboardDeviceItem
                {
                    HardwareId = "ALL",
                    DisplayName = "[All Connected Keyboards - Universal Debounce]",
                    EndpointCount = engine.AllKeyboards.Count
                };
                cmbDevices.Items.Add(allItem);
                if (string.Equals("ALL", engine.TargetPattern, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIdx = cmbDevices.Items.Count - 1;
                }

                if (cmbDevices.Items.Count > 0)
                {
                    cmbDevices.SelectedIndex = selectedIdx;
                }
            }
            finally
            {
                isUpdatingDropdown = false;
            }
        }

        private void CmbDevices_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (isInitializing || isUpdatingDropdown) return;
            var item = cmbDevices.SelectedItem as KeyboardDeviceItem;
            if (item != null)
            {
                engine.TargetPattern = item.HardwareId;
                engine.UpdateTargetHandles();
                lblDeviceEndpoints.Text = string.Format("Monitored Endpoints: {0}  |  Hardware ID: {1}", item.EndpointCount, item.HardwareId);
                SaveConfig();
            }
        }

        private void InitializeComponents()
        {
            this.Text = "Keyboard Debouncer - Hardware Filter";
            this.Size = new Size(685, 780);
            this.MinimumSize = new Size(685, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
            this.BackColor = Color.FromArgb(243, 244, 246);
            this.Icon = DebouncerAppContext.CreateKeyboardIcon();
            this.AutoScaleMode = AutoScaleMode.Dpi;

            // Header Banner
            Panel pnlBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = Color.FromArgb(24, 32, 47)
            };
            Label lblHeader = new Label
            {
                Text = "Keyboard Debouncer",
                Font = new Font("Segoe UI", 14F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(18, 12),
                AutoSize = true,
                UseMnemonic = false
            };
            Label lblSubHeader = new Label
            {
                Text = "Isolates and drops switch chatter on your chosen keyboard; bypasses all other devices.",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.FromArgb(203, 213, 225),
                Location = new Point(20, 40),
                AutoSize = true,
                UseMnemonic = false
            };
            pnlBanner.Controls.Add(lblHeader);
            pnlBanner.Controls.Add(lblSubHeader);

            // Scrollable Container Panel
            Panel pnlBody = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(16)
            };

            int y = 14;
            const int cardWidth = 620;

            // ==========================================
            // CARD 1: Target Device Selection
            // ==========================================
            Panel cardDevice = CreateCardPanel(y, cardWidth, 120);
            Label lblDeviceTitle = CreateCardTitle("Target Keyboard Selection", 16, 12);
            Label lblDevicePrompt = new Label
            {
                Text = "Select the keyboard that has bouncing keys (all other devices bypass):",
                Location = new Point(16, 36),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                UseMnemonic = false
            };

            cmbDevices = new ComboBox
            {
                Location = new Point(16, 58),
                Size = new Size(470, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5F)
            };
            cmbDevices.SelectedIndexChanged += CmbDevices_SelectedIndexChanged;

            btnRefreshDevices = new Button
            {
                Text = "Refresh Devices",
                Location = new Point(494, 57),
                Size = new Size(110, 30),
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };
            btnRefreshDevices.Click += (s, e) => engine.RefreshDevices();

            lblDeviceEndpoints = new Label
            {
                Text = "Monitored Endpoints: 0  |  Hardware ID: None",
                Location = new Point(16, 94),
                AutoSize = true,
                ForeColor = Color.FromArgb(71, 85, 105),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                UseMnemonic = false
            };

            cardDevice.Controls.Add(lblDeviceTitle);
            cardDevice.Controls.Add(lblDevicePrompt);
            cardDevice.Controls.Add(cmbDevices);
            cardDevice.Controls.Add(btnRefreshDevices);
            cardDevice.Controls.Add(lblDeviceEndpoints);
            pnlBody.Controls.Add(cardDevice);

            y += 132;

            // ==========================================
            // CARD 2: Debounce Settings & Startup
            // ==========================================
            Panel cardSettings = CreateCardPanel(y, cardWidth, 150);
            Label lblSettingsTitle = CreateCardTitle("Debounce Settings & Windows Startup", 16, 12);

            Label lblThreshold = new Label
            {
                Text = "Debounce Threshold:",
                Location = new Point(16, 38),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F),
                UseMnemonic = false
            };
            numDebounce = new NumericUpDown
            {
                Location = new Point(160, 36),
                Size = new Size(65, 26),
                Minimum = 10,
                Maximum = 200,
                Value = engine.DebounceThresholdMs,
                Increment = 5,
                Font = new Font("Segoe UI", 9.5F)
            };
            numDebounce.ValueChanged += (s, e) =>
            {
                if (isInitializing) return;
                engine.DebounceThresholdMs = (int)numDebounce.Value;
                SaveConfig();
            };

            Label lblThresholdUnit = new Label
            {
                Text = "ms",
                Location = new Point(230, 38),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                UseMnemonic = false
            };

            Label lblThresholdHint = new Label
            {
                Text = "(40–60 ms recommended; drops chatter without slowing fast typing)",
                Location = new Point(260, 38),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(100, 116, 139),
                UseMnemonic = false
            };

            chkStartWithWindows = new CheckBox
            {
                Text = "Start automatically when Windows boots",
                Location = new Point(16, 68),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F)
            };
            chkStartWithWindows.CheckedChanged += (s, e) =>
            {
                if (isInitializing) return;
                SetStartWithWindows(chkStartWithWindows.Checked);
                SaveConfig();
            };

            chkMinimizeToTray = new CheckBox
            {
                Text = "Minimize to System Tray on close (keep protecting)",
                Location = new Point(310, 68),
                AutoSize = true,
                Checked = true,
                Font = new Font("Segoe UI", 9F)
            };

            Label lblTrayHint = new Label
            {
                Text = "Note: In Windows 11, new tray icons sit inside the (^) overflow arrow near your clock.",
                Location = new Point(16, 96),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                ForeColor = Color.FromArgb(71, 85, 105),
                UseMnemonic = false
            };

            Button btnTestTray = new Button
            {
                Text = "Ping Tray Notification",
                Location = new Point(460, 92),
                Size = new Size(145, 26),
                Font = new Font("Segoe UI", 8.5F),
                FlatStyle = FlatStyle.System
            };
            btnTestTray.Click += (s, e) =>
            {
                if (appContext != null)
                {
                    appContext.ShowTrayBalloon("Keyboard Debouncer Active", "Keyboard Debouncer is running! Look inside the (^) arrow near your clock.");
                }
            };

            Label lblSavedHint = new Label
            {
                Text = "Settings are saved automatically to config.json",
                Location = new Point(16, 124),
                AutoSize = true,
                Font = new Font("Segoe UI", 8F),
                ForeColor = Color.FromArgb(100, 116, 139),
                UseMnemonic = false
            };

            cardSettings.Controls.Add(lblSettingsTitle);
            cardSettings.Controls.Add(lblThreshold);
            cardSettings.Controls.Add(numDebounce);
            cardSettings.Controls.Add(lblThresholdUnit);
            cardSettings.Controls.Add(lblThresholdHint);
            cardSettings.Controls.Add(chkStartWithWindows);
            cardSettings.Controls.Add(chkMinimizeToTray);
            cardSettings.Controls.Add(lblTrayHint);
            cardSettings.Controls.Add(btnTestTray);
            cardSettings.Controls.Add(lblSavedHint);
            pnlBody.Controls.Add(cardSettings);

            y += 164;

            // ==========================================
            // CARD 3: Live Protection & Counters
            // ==========================================
            Panel cardStatus = CreateCardPanel(y, cardWidth, 130);
            Label lblStatusTitle = CreateCardTitle("Live Protection & Counters", 16, 12);

            lblStatusBadge = new Label
            {
                Text = "● ACTIVE - Protecting against key bounce",
                Location = new Point(16, 38),
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(22, 101, 52),
                UseMnemonic = false
            };

            btnToggleActive = new Button
            {
                Text = "Pause Protection",
                Location = new Point(470, 32),
                Size = new Size(134, 32),
                Font = new Font("Segoe UI", 9F),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };
            btnToggleActive.Click += (s, e) => ToggleProtection();

            lblStatsTotal = new Label
            {
                Text = "Monitored Keystrokes: 0",
                Location = new Point(16, 68),
                AutoSize = true,
                Font = new Font("Segoe UI", 9F),
                UseMnemonic = false
            };

            lblStatsBlocked = new Label
            {
                Text = "Bounces Blocked: 0",
                Location = new Point(235, 68),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 28, 28),
                UseMnemonic = false
            };

            btnResetStats = new Button
            {
                Text = "Reset Counters",
                Location = new Point(470, 70),
                Size = new Size(134, 28),
                Font = new Font("Segoe UI", 8.5F),
                BackColor = Color.White,
                FlatStyle = FlatStyle.System
            };
            btnResetStats.Click += (s, e) => engine.ResetStats();

            lblTopBounced = new Label
            {
                Text = "Top Bounced Keys: None detected yet",
                Location = new Point(16, 98),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(71, 85, 105),
                UseMnemonic = false
            };

            cardStatus.Controls.Add(lblStatusTitle);
            cardStatus.Controls.Add(lblStatusBadge);
            cardStatus.Controls.Add(btnToggleActive);
            cardStatus.Controls.Add(lblStatsTotal);
            cardStatus.Controls.Add(lblStatsBlocked);
            cardStatus.Controls.Add(btnResetStats);
            cardStatus.Controls.Add(lblTopBounced);
            pnlBody.Controls.Add(cardStatus);

            y += 142;

            // ==========================================
            // CARD 4: Real-Time Blocked Bounce Log
            // ==========================================
            Panel cardLog = CreateCardPanel(y, cardWidth, 180);
            Label lblLogTitle = CreateCardTitle("Real-Time Blocked Bounce Event Log", 16, 12);

            btnClearLog = new Button
            {
                Text = "Clear Log",
                Location = new Point(355, 10),
                Size = new Size(100, 26),
                Font = new Font("Segoe UI", 8.5F),
                FlatStyle = FlatStyle.System
            };
            btnClearLog.Click += (s, e) => lstLog.Items.Clear();

            btnOpenLogFile = new Button
            {
                Text = "Open Log File",
                Location = new Point(464, 10),
                Size = new Size(140, 26),
                Font = new Font("Segoe UI", 8.5F),
                FlatStyle = FlatStyle.System
            };
            btnOpenLogFile.Click += (s, e) =>
            {
                if (File.Exists(engine.LogFilePath))
                {
                    Process.Start("notepad.exe", engine.LogFilePath);
                }
                else
                {
                    MessageBox.Show("Log file has not been created yet.", "Debounce Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            lstLog = new ListBox
            {
                Location = new Point(16, 42),
                Size = new Size(588, 125),
                Font = new Font("Consolas", 9F),
                HorizontalScrollbar = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(250, 250, 252)
            };

            cardLog.Controls.Add(lblLogTitle);
            cardLog.Controls.Add(btnClearLog);
            cardLog.Controls.Add(btnOpenLogFile);
            cardLog.Controls.Add(lstLog);
            pnlBody.Controls.Add(cardLog);

            // In WinForms docking, adding fill first and top second makes top dock to (0,0) and fill take Y=70..end
            this.Controls.Add(pnlBody);
            this.Controls.Add(pnlBanner);

            this.FormClosing += MainForm_FormClosing;
            this.Resize += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Minimized && chkMinimizeToTray != null && chkMinimizeToTray.Checked)
                {
                    this.Hide();
                    if (appContext != null)
                    {
                        appContext.ShowTrayBalloon("Keyboard Debouncer", "Running in the background. Look inside the (^) arrow near your clock.");
                    }
                }
            };
        }

        private Panel CreateCardPanel(int y, int width, int height)
        {
            Panel p = new Panel
            {
                Location = new Point(14, y),
                Size = new Size(width, height),
                BackColor = Color.White
            };
            p.Paint += (s, e) =>
            {
                using (Pen pen = new Pen(Color.FromArgb(226, 232, 240), 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
                }
            };
            return p;
        }

        private Label CreateCardTitle(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                UseMnemonic = false
            };
        }

        public void ToggleProtection()
        {
            engine.IsActive = !engine.IsActive;
            if (engine.IsActive)
            {
                lblStatusBadge.Text = "● ACTIVE - Protecting against key bounce";
                lblStatusBadge.ForeColor = Color.FromArgb(22, 101, 52);
                btnToggleActive.Text = "Pause Protection";
            }
            else
            {
                lblStatusBadge.Text = "■ PAUSED - Protection suspended";
                lblStatusBadge.ForeColor = Color.FromArgb(185, 28, 28);
                btnToggleActive.Text = "Resume Protection";
            }
            appContext.UpdateTrayStatus(engine.IsActive);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && chkMinimizeToTray.Checked)
            {
                e.Cancel = true;
                this.Hide();
                if (appContext != null)
                {
                    appContext.ShowTrayBalloon("Keyboard Debouncer", "Running in the background. Look inside the (^) arrow near your clock.");
                }
            }
        }

        private bool IsStartWithWindows()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue(APP_NAME);
                        return val != null;
                    }
                }
            }
            catch { }
            return false;
        }

        private void SetStartWithWindows(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = Application.ExecutablePath;
                            key.SetValue(APP_NAME, "\"" + exePath + "\" --minimized");
                        }
                        else
                        {
                            key.DeleteValue(APP_NAME, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update Windows Startup settings:\n" + ex.Message, "Startup Setting Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadConfig()
        {
            if (File.Exists(configPath))
            {
                try
                {
                    string content = File.ReadAllText(configPath);
                    Match mTarget = Regex.Match(content, @"""TargetPattern""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (mTarget.Success)
                    {
                        engine.TargetPattern = mTarget.Groups[1].Value.ToUpper();
                    }

                    Match mThresh = Regex.Match(content, @"""DebounceThresholdMs""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mThresh.Success)
                    {
                        int t = int.Parse(mThresh.Groups[1].Value);
                        if (t >= 10 && t <= 200)
                        {
                            engine.DebounceThresholdMs = t;
                            numDebounce.Value = t;
                        }
                    }

                    Match mMin = Regex.Match(content, @"""MinimizeToTrayOnClose""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                    if (mMin.Success)
                    {
                        chkMinimizeToTray.Checked = bool.Parse(mMin.Groups[1].Value);
                    }
                }
                catch { }
            }
        }

        private void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine(string.Format("  \"TargetPattern\": \"{0}\",", engine.TargetPattern));
                sb.AppendLine(string.Format("  \"DebounceThresholdMs\": {0},", engine.DebounceThresholdMs));
                sb.AppendLine(string.Format("  \"StartWithWindows\": {0},", chkStartWithWindows.Checked.ToString().ToLower()));
                sb.AppendLine(string.Format("  \"MinimizeToTrayOnClose\": {0}", chkMinimizeToTray.Checked.ToString().ToLower()));
                sb.AppendLine("}");
                File.WriteAllText(configPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ==========================================
    // 6. Application Context & System Tray
    // ==========================================
    public class DebouncerAppContext : ApplicationContext
    {
        private readonly DebounceEngine engine;
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly MainForm mainForm;

        public DebouncerAppContext(bool startMinimized, string initialDevice, int? initialDebounce)
        {
            engine = new DebounceEngine(initialDevice, initialDebounce ?? 50);
            engine.Start();

            // Tray Menu
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Keyboard Debouncer", null, (s, e) => ShowMainForm());
            var mnuActive = new ToolStripMenuItem("Active (Protecting)", null, (s, e) =>
            {
                if (mainForm != null) mainForm.ToggleProtection();
            });
            mnuActive.Checked = engine.IsActive;
            trayMenu.Items.Add(mnuActive);
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

            // Tray Icon
            trayIcon = new NotifyIcon();
            trayIcon.Icon = CreateKeyboardIcon();
            trayIcon.Text = "Keyboard Debouncer - Active";
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.DoubleClick += (s, e) => ShowMainForm();

            mainForm = new MainForm(engine, this);

            if (!startMinimized)
            {
                mainForm.Show();
            }
            else
            {
                ShowTrayBalloon("Keyboard Debouncer", "Running in the background. Protecting your keyboard from bounce.");
            }
        }

        public void UpdateTrayStatus(bool isActive)
        {
            ((ToolStripMenuItem)trayMenu.Items[1]).Checked = isActive;
            trayIcon.Text = isActive ? "Keyboard Debouncer - Active" : "Keyboard Debouncer - Paused";
        }

        public void ShowTrayBalloon(string title, string text)
        {
            try
            {
                trayIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        public void ShowMainForm()
        {
            if (mainForm != null)
            {
                mainForm.Show();
                mainForm.WindowState = FormWindowState.Normal;
                mainForm.BringToFront();
                NativeMethods.SetForegroundWindow(mainForm.Handle);
            }
        }

        private void ExitApplication()
        {
            trayIcon.Visible = false;
            engine.Stop();
            Application.Exit();
        }

        public static Icon CreateKeyboardIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(icoPath))
                {
                    return new Icon(icoPath);
                }
                Icon appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null)
                {
                    return appIcon;
                }
            }
            catch { }

            // High-contrast fallback bitmap (bright cyan outline, white keys, neon green dot)
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(20, 25, 38)))
                    g.FillRectangle(bg, 1, 3, 14, 11);
                using (Pen border = new Pen(Color.FromArgb(0, 195, 255), 1))
                    g.DrawRectangle(border, 1, 3, 13, 10);
                using (SolidBrush keys = new SolidBrush(Color.FromArgb(240, 246, 255)))
                {
                    g.FillRectangle(keys, 3, 5, 2, 2);
                    g.FillRectangle(keys, 6, 5, 2, 2);
                    g.FillRectangle(keys, 9, 5, 2, 2);
                    g.FillRectangle(keys, 3, 8, 2, 2);
                    g.FillRectangle(keys, 6, 8, 2, 2);
                    g.FillRectangle(keys, 9, 8, 2, 2);
                    g.FillRectangle(keys, 3, 11, 4, 1);
                }
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(0, 230, 118)))
                    g.FillEllipse(dot, 10, 9, 5, 5);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // ==========================================
    // 7. Program Entry Point
    // ==========================================
    static class Program
    {
        private const string MUTEX_NAME = "KeyboardDebouncerApp_SingleInstance_Mutex";

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                NativeMethods.SetProcessDPIAware();
            }
            catch { }

            // Global Exception Logging
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ev.ExceptionObject.ToString());
                }
                catch { }
            };
            Application.ThreadException += (s, ev) =>
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ev.Exception.ToString());
                }
                catch { }
            };

            bool isNewInstance = false;
            using (Mutex mutex = new Mutex(true, MUTEX_NAME, out isNewInstance))
            {
                if (!isNewInstance)
                {
                    MessageBox.Show(
                        "Keyboard Debouncer is already running!\nLook for the keyboard icon in the System Tray (near the taskbar clock).",
                        "Keyboard Debouncer",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool startMinimized = false;
                string deviceArg = null;
                int? debounceArg = null;

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].ToLower();
                    if (a == "--minimized" || a == "-m" || a == "/minimized")
                    {
                        startMinimized = true;
                    }
                    else if ((a == "--device" || a == "-d") && i + 1 < args.Length)
                    {
                        deviceArg = args[++i];
                    }
                    else if ((a == "--debounce" || a == "-t") && i + 1 < args.Length)
                    {
                        int v;
                        if (int.TryParse(args[++i], out v))
                        {
                            debounceArg = v;
                        }
                    }
                }

                DebouncerAppContext appContext = new DebouncerAppContext(startMinimized, deviceArg, debounceArg);
                Application.Run(appContext);
            }
        }
    }
}
