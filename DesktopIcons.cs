using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ChangeMonitorMode;

static class DesktopIcons
{
    delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] struct Point32 { public int X, Y; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point32 point);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point32 point);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static IntPtr Message(IntPtr window, uint message, int index, IntPtr data)
    {
        if (SendMessageTimeout(window, message, (IntPtr)index, data, 2, 2000, out var result) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }
    public static void Place(List<(string Name, Screen Screen, bool Outside)> shortcuts)
    {
        if (!Environment.Is64BitProcess) throw new Exception("바탕화면 배치는 64비트 실행 파일에서 지원합니다.");
        IntPtr list = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) list = FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            return list == IntPtr.Zero;
        }, IntPtr.Zero);
        if (list == IntPtr.Zero) throw new Exception("Windows 바탕화면 아이콘 창을 찾을 수 없습니다.");
        int style = GetWindowLong(list, -16);
        if ((style & 0x100) != 0) SetWindowLong(list, -16, style & ~0x100);
        GetWindowThreadProcessId(list, out uint pid);
        var process = OpenProcess(0x38, false, pid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var memory = VirtualAllocEx(process, IntPtr.Zero, 2048, 0x3000, 4);
        try
        {
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            void Write(byte[] bytes) { if (!WriteProcessMemory(process, memory, bytes, (nuint)bytes.Length, out var count) || count != (nuint)bytes.Length) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            byte[] Read(IntPtr address, int size) { var bytes = new byte[size]; if (!ReadProcessMemory(process, address, bytes, (nuint)size, out var count) || count != (nuint)size) throw new Win32Exception(Marshal.GetLastWin32Error()); return bytes; }
            Dictionary<string, int> indices = new();
            for (int attempt = 0; attempt < 30; attempt++)
            {
                indices.Clear();
                int total = (int)Message(list, 0x1004, 0, IntPtr.Zero);
                for (int index = 0; index < total; index++)
                {
                    byte[] item = new byte[88];
                    BitConverter.GetBytes((memory + 128).ToInt64()).CopyTo(item, 24);
                    BitConverter.GetBytes(512).CopyTo(item, 32);
                    Write(item);
                    Message(list, 0x1073, index, memory);
                    string name = Encoding.Unicode.GetString(Read(memory + 128, 1024)).Split('\0')[0];
                    indices[name] = index;
                }
                if (shortcuts.All(x => indices.ContainsKey(x.Name) || indices.ContainsKey(x.Name + ".lnk"))) break;
                Thread.Sleep(100);
            }
            var report = new List<object>();
            foreach (var shortcut in shortcuts)
            {
                if (!indices.TryGetValue(shortcut.Name, out int index) && !indices.TryGetValue(shortcut.Name + ".lnk", out index)) throw new Exception("바탕화면 바로가기를 찾지 못했습니다: " + shortcut.Name);
                var area = shortcut.Screen.WorkingArea;
                var point = new Point32 { X = area.Right - (shortcut.Outside ? 120 : 240), Y = area.Top + 100 };
                ScreenToClient(list, ref point);
                byte[] position = new byte[8]; BitConverter.GetBytes(point.X).CopyTo(position, 0); BitConverter.GetBytes(point.Y).CopyTo(position, 4);
                Write(position); Message(list, 0x1031, index, memory);
                Message(list, 0x1010, index, memory);
                var actual = Read(memory, 8);
                var actualPoint = new Point32 { X = BitConverter.ToInt32(actual, 0), Y = BitConverter.ToInt32(actual, 4) };
                ClientToScreen(list, ref actualPoint);
                if (!shortcut.Screen.Bounds.Contains(actualPoint.X, actualPoint.Y)) throw new Exception($"해당 모니터에 아이콘을 배치하지 못했습니다: {shortcut.Name}, bounds={shortcut.Screen.Bounds}, requested={point.X},{point.Y}, actual={actualPoint.X},{actualPoint.Y}");
                report.Add(new { shortcut.Name, Monitor = shortcut.Screen.DeviceName, X = actualPoint.X, Y = actualPoint.Y });
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "desktop-shortcuts.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { if (memory != IntPtr.Zero) VirtualFreeEx(process, memory, 0, 0x8000); CloseHandle(process); }
    }
}
