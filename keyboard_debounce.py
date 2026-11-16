"""
Keyboard Debounce Daemon for Windows
Filters and blocks switch bounce (key chatter) exclusively for the target keyboard,
bypassing all other input devices (e.g. secondary keyboards, mice, gamepads, virtual/macro keys).
"""

import sys
import os
import time
import json
import argparse
import ctypes
from ctypes import wintypes
import threading

# Win32 Constants
WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
WM_INPUT = 0x00FF
WM_INPUT_DEVICE_CHANGE = 0x00FE
WM_CLOSE = 0x0010
WM_QUIT = 0x0012

RIDEV_INPUTSINK = 0x00000100
RIDEV_DEVNOTIFY = 0x00002000
RIM_TYPEKEYBOARD = 1
RIM_TYPEMOUSE = 0
RIM_TYPEHID = 2

RIDI_DEVICENAME = 0x20000007
RIDI_DEVICEINFO = 0x2000000b
RID_INPUT = 0x10000003

LLKHF_EXTENDED = 0x01
LLKHF_LOWER_IL_INJECTED = 0x02
LLKHF_INJECTED = 0x10
LLKHF_ALTDOWN = 0x20
LLKHF_UP = 0x80

RI_KEY_MAKE = 0
RI_KEY_BREAK = 1
RI_KEY_E0 = 2
RI_KEY_E1 = 4

LRESULT = ctypes.c_longlong
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
HOOKPROC = ctypes.WINFUNCTYPE(LRESULT, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

# Setup ctypes signatures
user32.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = LRESULT

user32.CreateWindowExW.argtypes = [
    wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
    ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
    wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID
]
user32.CreateWindowExW.restype = wintypes.HWND

user32.DestroyWindow.argtypes = [wintypes.HWND]
user32.DestroyWindow.restype = wintypes.BOOL

user32.OpenDesktopW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
user32.OpenDesktopW.restype = wintypes.HDESK

user32.OpenInputDesktop.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
user32.OpenInputDesktop.restype = wintypes.HDESK

user32.SetThreadDesktop.argtypes = [wintypes.HDESK]
user32.SetThreadDesktop.restype = wintypes.BOOL

user32.CloseDesktop.argtypes = [wintypes.HDESK]
user32.CloseDesktop.restype = wintypes.BOOL

kernel32.GetModuleHandleW.restype = wintypes.HINSTANCE

user32.SetWindowsHookExW.argtypes = [ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD]
user32.SetWindowsHookExW.restype = wintypes.HHOOK

user32.UnhookWindowsHookEx.argtypes = [wintypes.HHOOK]
user32.UnhookWindowsHookEx.restype = wintypes.BOOL

user32.CallNextHookEx.argtypes = [wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM]
user32.CallNextHookEx.restype = LRESULT

user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PostMessageW.restype = wintypes.BOOL

user32.PostThreadMessageW.argtypes = [wintypes.DWORD, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PostThreadMessageW.restype = wintypes.BOOL


# C Struct Definitions
class RAWINPUTDEVICELIST(ctypes.Structure):
    _fields_ = [
        ('hDevice', wintypes.HANDLE),
        ('dwType', wintypes.DWORD),
    ]


class RAWINPUTDEVICE(ctypes.Structure):
    _fields_ = [
        ('usUsagePage', wintypes.USHORT),
        ('usUsage', wintypes.USHORT),
        ('dwFlags', wintypes.DWORD),
        ('hwndTarget', wintypes.HWND),
    ]


class RAWINPUTHEADER(ctypes.Structure):
    _fields_ = [
        ('dwType', wintypes.DWORD),
        ('dwSize', wintypes.DWORD),
        ('hDevice', wintypes.HANDLE),
        ('wParam', wintypes.WPARAM),
    ]


class RAWKEYBOARD(ctypes.Structure):
    _fields_ = [
        ('MakeCode', wintypes.USHORT),
        ('Flags', wintypes.USHORT),
        ('Reserved', wintypes.USHORT),
        ('VKey', wintypes.USHORT),
        ('Message', wintypes.UINT),
        ('ExtraInformation', wintypes.ULONG),
    ]


class RAWINPUT_U(ctypes.Union):
    _fields_ = [
        ('keyboard', RAWKEYBOARD),
    ]


class RAWINPUT(ctypes.Structure):
    _fields_ = [
        ('header', RAWINPUTHEADER),
        ('data', RAWINPUT_U),
    ]


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ('vkCode', wintypes.DWORD),
        ('scanCode', wintypes.DWORD),
        ('flags', wintypes.DWORD),
        ('time', wintypes.DWORD),
        ('dwExtraInfo', ctypes.c_size_t),
    ]


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [
        ('cbSize', wintypes.UINT),
        ('style', wintypes.UINT),
        ('lpfnWndProc', WNDPROC),
        ('cbClsExtra', ctypes.c_int),
        ('cbWndExtra', ctypes.c_int),
        ('hInstance', wintypes.HINSTANCE),
        ('hIcon', wintypes.HICON),
        ('hCursor', wintypes.HICON),
        ('hbrBackground', wintypes.HBRUSH),
        ('lpszMenuName', wintypes.LPCWSTR),
        ('lpszClassName', wintypes.LPCWSTR),
        ('hIconSm', wintypes.HICON),
    ]


# Human-readable VK Names map
VK_NAMES = {
    0x08: "BACKSPACE", 0x09: "TAB", 0x0D: "ENTER", 0x10: "SHIFT", 0x11: "CTRL",
    0x12: "ALT", 0x13: "PAUSE", 0x14: "CAPS_LOCK", 0x1B: "ESC", 0x20: "SPACE",
    0x21: "PAGE_UP", 0x22: "PAGE_DOWN", 0x23: "END", 0x24: "HOME",
    0x25: "LEFT", 0x26: "UP", 0x27: "RIGHT", 0x28: "DOWN", 0x2D: "INSERT",
    0x2E: "DELETE", 0x5B: "LWIN", 0x5C: "RWIN", 0x5D: "APPS",
    0xBA: ";", 0xBB: "=", 0xBC: ",", 0xBD: "-", 0xBE: ".", 0xBF: "/", 0xC0: "`",
    0xDB: "[", 0xDC: "\\", 0xDD: "]", 0xDE: "'",
}
for i in range(10):
    VK_NAMES[0x30 + i] = str(i)
    VK_NAMES[0x60 + i] = f"NUM_{i}"
for i in range(26):
    VK_NAMES[0x41 + i] = chr(ord('A') + i)
for i in range(1, 25):
    VK_NAMES[0x70 + i - 1] = f"F{i}"


def get_vk_name(vk):
    return VK_NAMES.get(vk, f"0x{vk:02X}")


# Key State Record
class KeyRecord:
    def __init__(self, vk):
        self.vk = vk
        self.is_down = False
        self.last_hook_down = 0.0
        self.last_hook_up = 0.0
        self.initial_down_time = 0.0
        self.suppressed_down = False
        self.suppressed_up = False
        self.last_device = None
        self.is_target_device = None  # None: unseen, True: target, False: other device
        self.bounce_count = 0
        self.total_presses = 0


# Debounce Engine Class
class KeyboardDebouncer:
    def __init__(self, target_pattern="VID_1A2C&PID_95F4", debounce_ms=50, per_key_ms=None, log_path=None, status_path=None, verbose=False):
        self.target_pattern = target_pattern.upper()
        self.default_debounce_s = debounce_ms / 1000.0
        self.default_debounce_ms = debounce_ms
        self.per_key_ms = {k.upper(): v for k, v in (per_key_ms or {}).items()}
        self.log_path = log_path or os.path.join(os.path.dirname(os.path.abspath(__file__)), "debounce.log")
        self.status_path = status_path or os.path.join(os.path.dirname(os.path.abspath(__file__)), "status.json")
        self.verbose = verbose

        self.key_records = {}
        self.target_handles = set()
        self.all_keyboards = {}  # handle -> dev_name
        self.total_blocked = 0
        self.total_keystrokes = 0
        self.start_time = time.time()
        self.last_status_write = 0.0

        self.hook_handle = None
        self.hwnd = None
        self.thread_id = None
        self.running = False
        self._c_wndproc = None
        self._c_hookproc = None
        self.lock = threading.Lock()

    def get_threshold_for_key(self, vk):
        name = get_vk_name(vk)
        if name in self.per_key_ms:
            return self.per_key_ms[name] / 1000.0, self.per_key_ms[name]
        return self.default_debounce_s, self.default_debounce_ms

    def log(self, message):
        timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
        log_entry = f"[{timestamp}] {message}"
        if self.verbose:
            print(log_entry, flush=True)
        try:
            with open(self.log_path, "a", encoding="utf-8") as f:
                f.write(log_entry + "\n")
        except Exception:
            pass

    def update_status_file(self):
        now = time.time()
        self.last_status_write = now

        stats = {
            "pid": os.getpid(),
            "hwnd": self.hwnd or 0,
            "status": "running" if self.running else "stopped",
            "start_time": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(self.start_time)),
            "uptime_seconds": int(now - self.start_time),
            "target_pattern": self.target_pattern,
            "target_device_handles": list(self.target_handles),
            "debounce_threshold_ms": self.default_debounce_ms,
            "per_key_thresholds_ms": self.per_key_ms,
            "total_keystrokes": self.total_keystrokes,
            "total_bounces_blocked": self.total_blocked,
            "blocked_per_key": {
                get_vk_name(vk): rec.bounce_count
                for vk, rec in self.key_records.items()
                if rec.bounce_count > 0
            }
        }
        try:
            temp_path = self.status_path + ".tmp"
            with open(temp_path, "w", encoding="utf-8") as f:
                json.dump(stats, f, indent=2)
            os.replace(temp_path, self.status_path)
        except Exception:
            pass

    def refresh_devices(self, initial=False):
        n = wintypes.UINT()
        user32.GetRawInputDeviceList(None, ctypes.byref(n), ctypes.sizeof(RAWINPUTDEVICELIST))
        if n.value == 0:
            return
        devs = (RAWINPUTDEVICELIST * n.value)()
        user32.GetRawInputDeviceList(devs, ctypes.byref(n), ctypes.sizeof(RAWINPUTDEVICELIST))

        new_all = {}
        new_targets = set()
        for d in devs:
            if d.dwType == RIM_TYPEKEYBOARD:
                n_len = wintypes.UINT()
                user32.GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, None, ctypes.byref(n_len))
                if n_len.value > 0:
                    buf = ctypes.create_unicode_buffer(n_len.value)
                    user32.GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, buf, ctypes.byref(n_len))
                    name = buf.value.upper()
                    new_all[d.hDevice] = name
                    if self.target_pattern in name:
                        new_targets.add(d.hDevice)

        changed = False
        with self.lock:
            if new_targets != self.target_handles or len(new_all) != len(self.all_keyboards):
                changed = True
            self.all_keyboards = new_all
            self.target_handles = new_targets

        if initial or changed:
            self.log(f"Enumerated {len(new_all)} keyboard endpoints. Target handles matching '{self.target_pattern}': {sorted(list(new_targets))}")

    def is_target_handle(self, hDevice):
        if not hDevice:
            return False
        with self.lock:
            if hDevice in self.target_handles:
                return True
            n_len = wintypes.UINT()
            user32.GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, None, ctypes.byref(n_len))
            if n_len.value > 0:
                buf = ctypes.create_unicode_buffer(n_len.value)
                user32.GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ctypes.byref(n_len))
                name = buf.value.upper()
                self.all_keyboards[hDevice] = name
                if self.target_pattern in name:
                    self.target_handles.add(hDevice)
                    return True
        return False

    def on_raw_input(self, lparam):
        raw_size = wintypes.UINT()
        user32.GetRawInputData(lparam, RID_INPUT, None, ctypes.byref(raw_size), ctypes.sizeof(RAWINPUTHEADER))
        if raw_size.value == 0:
            return

        buf = (ctypes.c_byte * raw_size.value)()
        if user32.GetRawInputData(lparam, RID_INPUT, buf, ctypes.byref(raw_size), ctypes.sizeof(RAWINPUTHEADER)) != raw_size.value:
            return

        raw = ctypes.cast(buf, ctypes.POINTER(RAWINPUT)).contents
        if raw.header.dwType != RIM_TYPEKEYBOARD:
            return

        hDevice = raw.header.hDevice
        vk = raw.data.keyboard.VKey
        flags = raw.data.keyboard.Flags
        is_break = bool(flags & RI_KEY_BREAK)

        is_target = self.is_target_handle(hDevice)

        with self.lock:
            rec = self.key_records.get(vk)
            if rec is None:
                rec = KeyRecord(vk)
                self.key_records[vk] = rec

            rec.last_device = hDevice
            rec.is_target_device = is_target

            if not is_break:
                rec.is_down = True
            else:
                rec.is_down = False

    def hook_procedure(self, nCode, wParam, lParam):
        if nCode < 0:
            return user32.CallNextHookEx(None, nCode, wParam, lParam)

        pKb = ctypes.cast(lParam, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
        vk = pKb.vkCode
        flags = pKb.flags

        # 1. Never block injected/synthetic keystrokes (software, macros, touch keyboard)
        if flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED):
            return user32.CallNextHookEx(None, nCode, wParam, lParam)

        now = time.perf_counter()
        is_down = wParam in (WM_KEYDOWN, WM_SYSKEYDOWN)
        is_up = wParam in (WM_KEYUP, WM_SYSKEYUP)
        threshold_s, threshold_ms = self.get_threshold_for_key(vk)

        with self.lock:
            self.total_keystrokes += 1
            rec = self.key_records.get(vk)
            if rec is None:
                rec = KeyRecord(vk)
                self.key_records[vk] = rec

            # 2. If this key belongs to another device (e.g. secondary keyboard, macro pad): NEVER BLOCK!
            if rec.is_target_device is False:
                if is_down:
                    rec.last_hook_down = now
                elif is_up:
                    rec.last_hook_up = now
                return user32.CallNextHookEx(None, nCode, wParam, lParam)

            # 3. Target Keyboard Debounce Logic:
            if is_down:
                # Check A: Release bounce (Break bounce)
                # Switch was released, but contact chatters closed within < threshold
                time_since_up = now - rec.last_hook_up
                if 0.0 < time_since_up < threshold_s:
                    rec.suppressed_down = True
                    rec.bounce_count += 1
                    self.total_blocked += 1
                    key_name = get_vk_name(vk)
                    self.log(f"[BLOCKED] Bounced KeyDown: '{key_name}' (VK 0x{vk:02X}) occurred {time_since_up*1000:.1f}ms after release (threshold: {threshold_ms}ms) on target keyboard")
                    return 1  # DROP BOUNCE

                # Check B: Make bounce (rapid flutter without human release)
                time_since_down = now - rec.last_hook_down
                time_since_first = now - rec.initial_down_time
                if rec.is_down and 0.0 < time_since_down < threshold_s:
                    # Typematic autorepeat never begins under 200ms
                    if time_since_first < 0.200:
                        rec.suppressed_down = True
                        rec.bounce_count += 1
                        self.total_blocked += 1
                        key_name = get_vk_name(vk)
                        self.log(f"[BLOCKED] Bounced KeyDown: '{key_name}' (VK 0x{vk:02X}) fluttered {time_since_down*1000:.1f}ms after initial press (threshold: {threshold_ms}ms) on target keyboard")
                        return 1  # DROP BOUNCE

                # Legitimate KeyDown
                rec.suppressed_down = False
                if not rec.is_down or (now - rec.last_hook_down > 0.150):
                    rec.initial_down_time = now
                rec.last_hook_down = now
                rec.is_down = True
                rec.total_presses += 1
                return user32.CallNextHookEx(None, nCode, wParam, lParam)

            elif is_up:
                # If the preceding KeyDown was dropped as a bounce, drop this bounce's KeyUp too
                if rec.suppressed_down:
                    rec.suppressed_down = False
                    return 1

                # Spurious micro-release flutter on make: KeyUp occurring < 20ms after KeyDown
                time_since_down = now - rec.last_hook_down
                if 0.0 < time_since_down < min(0.020, threshold_s * 0.4):
                    rec.suppressed_up = True
                    return 1  # Suppress contact chatter release so key hold isn't broken

                rec.suppressed_up = False
                rec.last_hook_up = now
                rec.is_down = False
                return user32.CallNextHookEx(None, nCode, wParam, lParam)

        return user32.CallNextHookEx(None, nCode, wParam, lParam)

    def wnd_proc(self, hwnd, msg, wparam, lparam):
        if msg == WM_INPUT:
            self.on_raw_input(lparam)
            return user32.DefWindowProcW(hwnd, msg, wparam, lparam)
        elif msg == WM_INPUT_DEVICE_CHANGE:
            self.refresh_devices()
            return 0
        elif msg in (WM_CLOSE, WM_QUIT):
            self.running = False
            return 0
        return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

    def run(self):
        # Attach to the active user desktop
        hDesk = user32.OpenDesktopW("Default", 0, False, 0x01FF)
        if not hDesk:
            hDesk = user32.OpenInputDesktop(0, False, 0x01FF)
        if hDesk:
            user32.SetThreadDesktop(hDesk)

        self.thread_id = kernel32.GetCurrentThreadId()
        self.running = True

        # Initial device enumeration
        self.refresh_devices(initial=True)

        # Register Window Class
        self._c_wndproc = WNDPROC(self.wnd_proc)
        wc = WNDCLASSEXW()
        wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
        wc.lpfnWndProc = self._c_wndproc
        wc.hInstance = kernel32.GetModuleHandleW(None)
        wc.lpszClassName = "KeyboardDebounceSinkClass"
        user32.RegisterClassExW(ctypes.byref(wc))

        self.hwnd = user32.CreateWindowExW(
            0, "KeyboardDebounceSinkClass", "KeyboardDebounceSink",
            0, 0, 0, 0, 0, None, None, wc.hInstance, None
        )

        # Register Raw Input Device Sink
        rid = RAWINPUTDEVICE()
        rid.usUsagePage = 1  # Generic Desktop
        rid.usUsage = 6      # Keyboard
        rid.dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY
        rid.hwndTarget = self.hwnd

        if not user32.RegisterRawInputDevices(ctypes.byref(rid), 1, ctypes.sizeof(RAWINPUTDEVICE)):
            self.log(f"Error registering Raw Input sink: {kernel32.GetLastError()}")
            return

        # Install Low-Level Keyboard Hook
        self._c_hookproc = HOOKPROC(self.hook_procedure)
        self.hook_handle = user32.SetWindowsHookExW(
            WH_KEYBOARD_LL, self._c_hookproc, kernel32.GetModuleHandleW(None), 0
        )

        if not self.hook_handle:
            self.log(f"Error installing WH_KEYBOARD_LL hook: {kernel32.GetLastError()}")
            user32.DestroyWindow(self.hwnd)
            return

        self.log(f"Keyboard Debouncer ACTIVATED! Monitoring target keyboard '{self.target_pattern}' with debounce threshold {self.default_debounce_ms}ms.")
        self.update_status_file()

        # Message Loop
        msg = wintypes.MSG()
        last_save = time.time()
        try:
            while self.running:
                while user32.PeekMessageW(ctypes.byref(msg), 0, 0, 0, 1):
                    if msg.message == WM_QUIT:
                        self.running = False
                        break
                    user32.TranslateMessage(ctypes.byref(msg))
                    user32.DispatchMessageW(ctypes.byref(msg))

                now = time.time()
                if now - last_save >= 2.0:
                    self.update_status_file()
                    last_save = now

                time.sleep(0.005)
        finally:
            self.stop()

    def stop(self):
        self.running = False
        if self.hook_handle:
            user32.UnhookWindowsHookEx(self.hook_handle)
            self.hook_handle = None
        if self.hwnd:
            user32.DestroyWindow(self.hwnd)
            self.hwnd = None
        self.update_status_file()
        self.log("Keyboard Debouncer DEACTIVATED.")


# CLI File Paths
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PID_FILE = os.path.join(SCRIPT_DIR, "debounce.pid")
STATUS_FILE = os.path.join(SCRIPT_DIR, "status.json")
LOG_FILE = os.path.join(SCRIPT_DIR, "debounce.log")
CONFIG_FILE = os.path.join(SCRIPT_DIR, "config.json")


def load_config():
    default_config = {
        "target_device_pattern": "VID_1A2C&PID_95F4",
        "default_debounce_ms": 50,
        "per_key_thresholds_ms": {}
    }
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                cfg = json.load(f)
                default_config.update(cfg)
        except Exception:
            pass
    return default_config


def save_config(cfg):
    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
    except Exception:
        pass


def list_devices():
    n = wintypes.UINT()
    user32.GetRawInputDeviceList(None, ctypes.byref(n), ctypes.sizeof(RAWINPUTDEVICELIST))
    if n.value == 0:
        print("No raw input devices found.")
        return
    devs = (RAWINPUTDEVICELIST * n.value)()
    user32.GetRawInputDeviceList(devs, ctypes.byref(n), ctypes.sizeof(RAWINPUTDEVICELIST))

    cfg = load_config()
    target_pattern = cfg.get("target_device_pattern", "VID_1A2C&PID_95F4").upper()

    type_map = {RIM_TYPEMOUSE: "Mouse", RIM_TYPEKEYBOARD: "Keyboard", RIM_TYPEHID: "HID/Other"}
    print("\nConnected Input Devices on Windows:")
    print("=" * 90)
    for d in devs:
        n_len = wintypes.UINT()
        user32.GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, None, ctypes.byref(n_len))
        name = ""
        if n_len.value > 0:
            buf = ctypes.create_unicode_buffer(n_len.value)
            user32.GetRawInputDeviceInfoW(d.hDevice, RIDI_DEVICENAME, buf, ctypes.byref(n_len))
            name = buf.value

        t_str = type_map.get(d.dwType, f"Type {d.dwType}")
        is_target = target_pattern in name.upper()
        marker = " <== [TARGET KEYBOARD DEBOUNCED]" if is_target else "     [BYPASSED - NOT TOUCHED]"
        print(f"Handle: {d.hDevice:<8} | Type: {t_str:<10} | {marker} | Name: {name}")
    print("=" * 90)


def is_pid_running(pid):
    if pid <= 0:
        return False
    hProcess = kernel32.OpenProcess(0x1000, False, pid)
    if hProcess:
        kernel32.CloseHandle(hProcess)
        return True
    return False


def get_running_pid():
    if os.path.exists(PID_FILE):
        try:
            with open(PID_FILE, "r") as f:
                pid = int(f.read().strip())
                if is_pid_running(pid):
                    return pid
        except Exception:
            pass
    return None


def cmd_start(args):
    running_pid = get_running_pid()
    if running_pid:
        print(f"Debounce daemon is already running (PID: {running_pid}).")
        return

    cfg = load_config()
    target = args.device or cfg.get("target_device_pattern", "VID_1A2C&PID_95F4")
    debounce_val = args.debounce if args.debounce is not None else cfg.get("default_debounce_ms", 50)
    per_key = cfg.get("per_key_thresholds_ms", {})

    if args.foreground:
        print(f"Starting Keyboard Debouncer in foreground...")
        print(f"  Target Pattern:     {target}")
        print(f"  Debounce Threshold: {debounce_val} ms")
        print("Press Ctrl+C to stop.\n")
        with open(PID_FILE, "w") as f:
            f.write(str(os.getpid()))
        try:
            debouncer = KeyboardDebouncer(
                target_pattern=target,
                debounce_ms=debounce_val,
                per_key_ms=per_key,
                log_path=LOG_FILE,
                status_path=STATUS_FILE,
                verbose=True
            )
            debouncer.run()
        except KeyboardInterrupt:
            print("\nStopping...")
        finally:
            if os.path.exists(PID_FILE):
                os.remove(PID_FILE)
    else:
        python_exe = sys.executable
        script_path = os.path.abspath(__file__)
        cmd = [python_exe, script_path, "_daemon", "--device", target, "--debounce", str(debounce_val)]
        
        flags = 0x00000008 | 0x00000200 | 0x08000000
        import subprocess
        proc = subprocess.Popen(
            cmd,
            creationflags=flags,
            close_fds=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            stdin=subprocess.DEVNULL
        )
        with open(PID_FILE, "w") as f:
            f.write(str(proc.pid))

        time.sleep(0.5)
        if is_pid_running(proc.pid):
            print(f"[SUCCESS] Keyboard Debouncer successfully activated in background!")
            print(f"  PID:                {proc.pid}")
            print(f"  Target Keyboard:    {target}")
            print(f"  Debounce Threshold: {debounce_val} ms")
            print(f"  Log File:           {LOG_FILE}")
            print(f"  Status File:        {STATUS_FILE}")
            print(f"\nRun 'python keyboard_debounce.py status' to monitor blocked bounces.")
        else:
            print("[ERROR] Failed to start background daemon. Check debounce.log for details.")


def cmd_daemon(args):
    with open(PID_FILE, "w") as f:
        f.write(str(os.getpid()))
    try:
        cfg = load_config()
        per_key = cfg.get("per_key_thresholds_ms", {})
        debouncer = KeyboardDebouncer(
            target_pattern=args.device,
            debounce_ms=args.debounce,
            per_key_ms=per_key,
            log_path=LOG_FILE,
            status_path=STATUS_FILE,
            verbose=False
        )
        debouncer.run()
    finally:
        if os.path.exists(PID_FILE):
            try:
                os.remove(PID_FILE)
            except Exception:
                pass


def cmd_stop():
    pid = get_running_pid()
    if not pid:
        print("Debounce daemon is not currently running.")
        if os.path.exists(PID_FILE):
            os.remove(PID_FILE)
        return

    print(f"Stopping debounce daemon (PID: {pid})...")
    # Try graceful window close first
    if os.path.exists(STATUS_FILE):
        try:
            with open(STATUS_FILE, "r") as f:
                data = json.load(f)
            hwnd = data.get("hwnd", 0)
            if hwnd:
                user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
                for _ in range(20):
                    time.sleep(0.1)
                    if not is_pid_running(pid):
                        break
        except Exception:
            pass

    # If still running, terminate process
    if is_pid_running(pid):
        hProcess = kernel32.OpenProcess(0x0001, False, pid)
        if hProcess:
            kernel32.TerminateProcess(hProcess, 0)
            kernel32.CloseHandle(hProcess)

    time.sleep(0.3)
    if os.path.exists(PID_FILE):
        try:
            os.remove(PID_FILE)
        except Exception:
            pass
    print("[SUCCESS] Debounce daemon stopped.")


def cmd_status():
    pid = get_running_pid()
    if pid:
        print(f"Debounce Daemon Status: ACTIVE (PID: {pid})")
    else:
        print("Debounce Daemon Status: STOPPED")

    if os.path.exists(STATUS_FILE):
        try:
            with open(STATUS_FILE, "r") as f:
                data = json.load(f)
            print("\nStatistics:")
            print(f"  Target Device Pattern:   {data.get('target_pattern')}")
            print(f"  Debounce Threshold:      {data.get('debounce_threshold_ms')} ms")
            if data.get("per_key_thresholds_ms"):
                print(f"  Per-Key Overrides:       {data.get('per_key_thresholds_ms')}")
            print(f"  Started At:              {data.get('start_time')}")
            print(f"  Uptime:                  {data.get('uptime_seconds')} seconds")
            print(f"  Total Keystrokes:        {data.get('total_keystrokes')}")
            print(f"  Total Bounces Blocked:   {data.get('total_bounces_blocked')}")
            blocked = data.get("blocked_per_key", {})
            if blocked:
                print("\nBlocked Bounces by Key:")
                for k, v in sorted(blocked.items(), key=lambda x: -x[1]):
                    print(f"    Key '{k}': {v} bounce(s) blocked")
            else:
                print("  No bounces detected yet.")
        except Exception as e:
            print(f"Error reading status file: {e}")
    else:
        print("No status history found.")


def cmd_log():
    if not os.path.exists(LOG_FILE):
        print("No log file found.")
        return
    with open(LOG_FILE, "r", encoding="utf-8") as f:
        lines = f.readlines()
    print(f"Recent Debounce Logs (last 30 entries of {len(lines)}):")
    print("-" * 90)
    for line in lines[-30:]:
        print(line.rstrip())
    print("-" * 90)


def cmd_test(args):
    print("=" * 80)
    print("KEYBOARD DEBOUNCE REAL-TIME TEST MODE")
    print(f"Target Keyboard Pattern: {args.device}")
    print(f"Debounce Threshold:      {args.debounce} ms")
    print("Type any keys on your keyboard. Spurious bounces will be displayed in real time.")
    print("Press Ctrl+C to exit test mode.")
    print("=" * 80 + "\n")

    debouncer = KeyboardDebouncer(
        target_pattern=args.device,
        debounce_ms=args.debounce,
        log_path=LOG_FILE,
        status_path=STATUS_FILE,
        verbose=True
    )
    try:
        debouncer.run()
    except KeyboardInterrupt:
        print("\nExiting test mode.")


def main():
    parser = argparse.ArgumentParser(description="Keyboard Debounce Tool for Windows")
    subparsers = parser.add_subparsers(dest="command", help="Subcommand to execute")

    # start
    p_start = subparsers.add_parser("start", help="Start the debounce daemon")
    p_start.add_argument("--device", default=None, help="Target keyboard VID/PID or name substring (default from config or VID_1A2C&PID_95F4)")
    p_start.add_argument("--debounce", type=int, default=None, help="Debounce threshold in milliseconds (default: 50)")
    p_start.add_argument("--foreground", action="store_true", help="Run in foreground instead of background")

    # _daemon internal
    p_daemon = subparsers.add_parser("_daemon", help=argparse.SUPPRESS)
    p_daemon.add_argument("--device", default="VID_1A2C&PID_95F4")
    p_daemon.add_argument("--debounce", type=int, default=50)

    # stop
    subparsers.add_parser("stop", help="Stop the running debounce daemon")

    # status
    subparsers.add_parser("status", help="Check debounce daemon status and statistics")

    # log
    subparsers.add_parser("log", help="Display recent blocked bounces from log file")

    # devices
    subparsers.add_parser("devices", help="List all connected keyboard and input devices")

    # test
    p_test = subparsers.add_parser("test", help="Interactive test mode with live console feedback")
    p_test.add_argument("--device", default="VID_1A2C&PID_95F4", help="Target keyboard VID/PID")
    p_test.add_argument("--debounce", type=int, default=50, help="Debounce threshold in ms")

    args = parser.parse_args()

    if args.command == "devices":
        list_devices()
    elif args.command == "start":
        cmd_start(args)
    elif args.command == "_daemon":
        cmd_daemon(args)
    elif args.command == "stop":
        cmd_stop()
    elif args.command == "status":
        cmd_status()
    elif args.command == "log":
        cmd_log()
    elif args.command == "test":
        cmd_test(args)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
