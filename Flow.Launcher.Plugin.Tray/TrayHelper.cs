using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Flow.Launcher.Plugin.Tray;

internal class TrayIconInfo
{
    public IntPtr OwnerHwnd { get; set; }
    public uint NotifyIconId { get; set; }
    public string Tooltip { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public AutomationElement? AutomationElement { get; set; }
}

internal static class TrayHelper
{
    public static readonly List<string> DiagLog = [];
    private static void Diag(string msg) => DiagLog.Add(msg);

    #region P/Invoke
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll")]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TBBUTTON
    {
        public int iBitmap, idCommand;
        public byte fsState, fsStyle;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] bReserved;
        public ulong dwData;
        public long iString;
    }

    private const uint PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_OPERATION = 0x0008;
    private const uint MEM_COMMIT = 0x1000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;
    private const uint TB_BUTTONCOUNT = 0x0418, TB_GETBUTTON = 0x0417;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // Works for all processes including protected ones unlike Process.MainModule
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x000;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    #endregion

    // Find all direct/indirect child windows whose class name matches a given string
    private static List<IntPtr> FindChildrenByClass(IntPtr parent, string className)
    {
        var found = new List<IntPtr>();
        EnumChildProc cb = (hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, 256);
            if (sb.ToString().Equals(className, StringComparison.OrdinalIgnoreCase))
                found.Add(hwnd);
            return true;
        };
        EnumChildWindows(parent, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return found;
    }

    private static string GetExecutablePath(int pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (hProc == IntPtr.Zero) return string.Empty;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(hProc, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        finally { CloseHandle(hProc); }
    }

    private static string GetExecutablePath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return GetExecutablePath((int)pid);
    }

    // When the button is hosted by explorer.exe, try to match the tooltip against running processes
    private static string FindExeByTooltip(string tooltip)
    {
        tooltip = tooltip.Replace("\r\n", " ").Replace("\n", " ").Trim();
        var bestPath = string.Empty;
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        string exePath = GetExecutablePath(proc.Id);
                        if (string.IsNullOrEmpty(exePath)) continue;

                        // Match on process name
                        string procName = proc.ProcessName;
                        if (tooltip.Contains(procName, StringComparison.OrdinalIgnoreCase) ||
                            procName.Contains(tooltip, StringComparison.OrdinalIgnoreCase))
                            return exePath;

                        // Match on FileVersionInfo FileDescription / ProductName
                        try
                        {
                            var fvi = FileVersionInfo.GetVersionInfo(exePath);
                            string desc = fvi.FileDescription ?? string.Empty;
                            string prod = fvi.ProductName ?? string.Empty;
                            if ((!string.IsNullOrEmpty(desc) &&
                                    (tooltip.Contains(desc, StringComparison.OrdinalIgnoreCase) ||
                                     desc.Contains(tooltip, StringComparison.OrdinalIgnoreCase))) ||
                                (!string.IsNullOrEmpty(prod) &&
                                    (tooltip.Contains(prod, StringComparison.OrdinalIgnoreCase) ||
                                     prod.Contains(tooltip, StringComparison.OrdinalIgnoreCase))))
                            {
                                bestPath = exePath; // keep looking for an exact match but remember this
                            }
                        }
                        catch { }
                    }
                    catch { }
                }
            }
        }
        catch { }
        return bestPath;
    }

    private static string GetOrCreateIconCache(string exePath, string cacheDir)
    {
        if (string.IsNullOrEmpty(exePath)) return string.Empty;
        string key = Convert.ToBase64String(Encoding.UTF8.GetBytes(exePath))
            .Replace("/", "_").Replace("+", "-").Replace("=", "");
        string iconPath = Path.Combine(cacheDir, key + ".png");
        if (File.Exists(iconPath)) return iconPath;
        try
        {
            // Primary: ExtractAssociatedIcon (works for classic Win32 apps)
            Bitmap? bmp = null;
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null) bmp = icon.ToBitmap();
            }
            catch { }

            // Fallback: SHGetFileInfo (works for UWP/MSIX/packaged apps like new Teams)
            if (bmp == null)
            {
                var shfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(exePath, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
                if (res != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                {
                    try { using var icon = Icon.FromHandle(shfi.hIcon); bmp = icon.ToBitmap(); }
                    finally { DestroyIcon(shfi.hIcon); }
                }
            }

            if (bmp == null) return string.Empty;
            using (bmp) bmp.Save(iconPath, ImageFormat.Png);
            return iconPath;
        }
        catch { return string.Empty; }
    }

    // Recursively walk UIA tree using TreeWalker (required for XAML Islands)
    // Since we scope to InputSite windows under TrayNotifyWnd, accept any named non-root element
    private static void WalkUIA(AutomationElement elem, int depth, List<AutomationElement> candidates, ref int logged)
    {
        if (depth > 12) return;
        try
        {
            // Name can be empty for some tray icons (e.g. JetBrains Toolbox) — fall back to HelpText/AutomationId
            string name = elem.Current.Name;
            if (string.IsNullOrWhiteSpace(name)) name = elem.Current.HelpText;
            if (string.IsNullOrWhiteSpace(name)) name = elem.Current.AutomationId;
            string ct = elem.Current.ControlType.ProgrammaticName;
            string cls = elem.Current.ClassName;
            if (logged < 60)
            {
                Diag($"{new string(' ', depth * 2)}ct={ct.Replace("ControlType.", "")} cls='{cls}' name='{name}'");
                logged++;
            }
            // Accept any named element at depth > 0 (root is the InputSite host itself)
            if (depth > 0 && !string.IsNullOrWhiteSpace(name))
                candidates.Add(elem);

            var child = TreeWalker.RawViewWalker.GetFirstChild(elem);
            while (child != null)
            {
                WalkUIA(child, depth + 1, candidates, ref logged);
                child = TreeWalker.RawViewWalker.GetNextSibling(child);
            }
        }
        catch { }
    }

    // Windows 11: walk UIA from a specific set of InputSite HWNDs (the XAML island host windows)
    private static void CollectViaUIA(List<IntPtr> inputSiteHwnds, string iconCacheDir, List<TrayIconInfo> results, HashSet<string> seen)
    {
        if (inputSiteHwnds.Count == 0) { Diag("CollectViaUIA: no InputSite windows"); return; }
        Exception? threadEx = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var candidates = new List<AutomationElement>();
                int logged = 0;
                foreach (var hwnd in inputSiteHwnds)
                {
                    Diag($"UIA TreeWalker from InputSite hwnd={hwnd}");
                    try
                    {
                        var root = AutomationElement.FromHandle(hwnd);
                        WalkUIA(root, 0, candidates, ref logged);
                    }
                    catch (Exception ex) { Diag($"  FromHandle failed: {ex.Message}"); }
                }
                Diag($"Total candidates: {candidates.Count}");

                foreach (var btn in candidates)
                {
                    try
                    {
                        string name = btn.Current.Name;
                        if (string.IsNullOrWhiteSpace(name)) name = btn.Current.HelpText;
                        if (string.IsNullOrWhiteSpace(name)) name = btn.Current.AutomationId;
                        name = name.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
                        if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                        int pid = btn.Current.ProcessId;
                        string exePath = GetExecutablePath(pid);
                        if (string.IsNullOrEmpty(exePath) ||
                            Path.GetFileNameWithoutExtension(exePath).Equals("explorer", StringComparison.OrdinalIgnoreCase))
                        {
                            string matched = FindExeByTooltip(name);
                            if (!string.IsNullOrEmpty(matched)) exePath = matched;
                        }
                        // Last resort: use explorer.exe so we at least have an icon
                        if (string.IsNullOrEmpty(exePath))
                            exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                        results.Add(new TrayIconInfo
                        {
                            OwnerHwnd = btn.Current.NativeWindowHandle != 0
                                ? (IntPtr)btn.Current.NativeWindowHandle
                                : inputSiteHwnds[0],
                            Tooltip = name,
                            ExecutablePath = exePath,
                            IconPath = GetOrCreateIconCache(exePath, iconCacheDir),
                            AutomationElement = btn
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { threadEx = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(10000);
        if (threadEx != null) Diag($"UIA error: {threadEx.Message}");
    }

    // Windows 10: enumerate via ToolbarWindow32 cross-process memory read
    private static void CollectViaToolbar(IntPtr toolbar, string iconCacheDir, List<TrayIconInfo> results, HashSet<string> seen)
    {
        if (toolbar == IntPtr.Zero) { Diag("Toolbar Zero, skip"); return; }
        int count = (int)SendMessage(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
        Diag($"TB_BUTTONCOUNT: {count}");
        if (count <= 0) return;

        GetWindowThreadProcessId(toolbar, out uint processId);
        IntPtr hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, processId);
        if (hProcess == IntPtr.Zero) { Diag("OpenProcess failed"); return; }

        int tbSize = Marshal.SizeOf<TBBUTTON>();
        IntPtr remoteBuf = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)tbSize, MEM_COMMIT, PAGE_READWRITE);
        try
        {
            for (int i = 0; i < count; i++)
            {
                SendMessage(toolbar, TB_GETBUTTON, (IntPtr)i, remoteBuf);
                byte[] buf = new byte[tbSize];
                if (!ReadProcessMemory(hProcess, remoteBuf, buf, tbSize, out _)) continue;

                GCHandle gh = GCHandle.Alloc(buf, GCHandleType.Pinned);
                var btn = Marshal.PtrToStructure<TBBUTTON>(gh.AddrOfPinnedObject());
                gh.Free();

                if (btn.idCommand == 0 || btn.dwData == 0) continue;

                string tooltip = string.Empty;
                if (btn.iString > 0xFFFF)
                {
                    byte[] strBuf = new byte[512];
                    if (ReadProcessMemory(hProcess, (IntPtr)btn.iString, strBuf, strBuf.Length, out _))
                        tooltip = Encoding.Unicode.GetString(strBuf).Split('\0')[0];
                }

                byte[] trayData = new byte[16];
                if (!ReadProcessMemory(hProcess, (IntPtr)(long)btn.dwData, trayData, trayData.Length, out _)) continue;

                IntPtr ownerHwnd = (IntPtr)BitConverter.ToInt64(trayData, 0);
                uint notifyIconId = BitConverter.ToUInt32(trayData, 8);
                if (ownerHwnd == IntPtr.Zero) continue;

                string exePath = GetExecutablePath(ownerHwnd);
                if (string.IsNullOrEmpty(tooltip)) tooltip = Path.GetFileNameWithoutExtension(exePath);
                if (string.IsNullOrEmpty(tooltip) || !seen.Add(tooltip)) continue;

                results.Add(new TrayIconInfo
                {
                    OwnerHwnd = ownerHwnd,
                    NotifyIconId = notifyIconId,
                    Tooltip = tooltip,
                    ExecutablePath = exePath,
                    IconPath = GetOrCreateIconCache(exePath, iconCacheDir)
                });
            }
        }
        finally { VirtualFreeEx(hProcess, remoteBuf, 0, MEM_RELEASE); CloseHandle(hProcess); }
    }

    public static List<TrayIconInfo> EnumerateAll(string iconCacheDir)
    {
        DiagLog.Clear();
        var results = new List<TrayIconInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IntPtr shellTray = FindWindow("Shell_TrayWnd", null);
        IntPtr notify = FindWindowEx(shellTray, IntPtr.Zero, "TrayNotifyWnd", null);
        IntPtr pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        Diag($"Shell_TrayWnd={shellTray} TrayNotifyWnd={notify} SysPager={pager}");

        if (pager != IntPtr.Zero)
        {
            // Windows 10 path: ToolbarWindow32 cross-process
            Diag("Path: Windows 10 (ToolbarWindow32)");
            CollectViaToolbar(FindWindowEx(pager, IntPtr.Zero, "ToolbarWindow32", null), iconCacheDir, results, seen);
            IntPtr overflow10 = FindWindow("NotifyIconOverflowWindow", null);
            CollectViaToolbar(FindWindowEx(overflow10, IntPtr.Zero, "ToolbarWindow32", null), iconCacheDir, results, seen);
        }
        else
        {
            // Windows 11 path: target Windows.UI.Input.InputSite.WindowClass under TrayNotifyWnd
            Diag("Path: Windows 11 (UI Automation on InputSite)");

            // Visible tray icons: InputSite windows under TrayNotifyWnd
            var mainInputSites = FindChildrenByClass(notify, "Windows.UI.Input.InputSite.WindowClass");
            Diag($"InputSite windows under TrayNotifyWnd: {mainInputSites.Count}");
            CollectViaUIA(mainInputSites, iconCacheDir, results, seen);

            // Overflow tray icons
            IntPtr overflow11 = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
            Diag($"OverflowXamlIsland={overflow11}");
            var overflowInputSites = FindChildrenByClass(overflow11, "Windows.UI.Input.InputSite.WindowClass");
            Diag($"InputSite windows under overflow: {overflowInputSites.Count}");
            CollectViaUIA(overflowInputSites, iconCacheDir, results, seen);
        }

        Diag($"Total results: {results.Count}");
        return results;
    }

    public static void SimulateClick(TrayIconInfo info, bool rightClick)
    {
        // Try UI Automation InvokePattern first (Win11)
        if (info.AutomationElement != null)
        {
            try
            {
                if (!rightClick && info.AutomationElement.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                { ((InvokePattern)p).Invoke(); return; }
            }
            catch { }
        }

        // Fallback: hardware mouse simulation via Shell_NotifyIconGetRect (Win10)
        var nii = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = info.OwnerHwnd,
            uID = info.NotifyIconId
        };
        if (Shell_NotifyIconGetRect(ref nii, out RECT rect) != 0) return;

        int cx = (rect.left + rect.right) / 2, cy = (rect.top + rect.bottom) / 2;
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int nx = (int)((long)(cx - vx) * 65536 / vw);
        int ny = (int)((long)(cy - vy) * 65536 / vh);
        uint abs = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        uint dn = rightClick ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        uint up = rightClick ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;

        INPUT[] inputs = [
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | abs } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = dn | abs } },
            new INPUT { type = 0, mi = new MOUSEINPUT { dx = nx, dy = ny, dwFlags = up | abs } },
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
