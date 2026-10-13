using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ChangeMonitorMode;

record MonitorItem(string Key, string Name) { public override string ToString() => Name; }
class MonitorIdentityException : Exception
{
    public MonitorIdentityException() : base("저장된 모니터의 식별 정보를 읽을 수 없습니다. 그래픽 어댑터 또는 연결 구성이 변경되었을 수 있습니다.") { }
}
class Layout { public byte[] Paths { get; set; } = []; public byte[] Modes { get; set; } = []; }
class Settings
{
    public string? Monitor { get; set; }
    public string? MirrorMonitor { get; set; }
    public bool Outside { get; set; }
    public Layout? Home { get; set; }
    public bool JetAuto { get; set; }
    public string JetIp { get; set; } = "";
    public string ReceiverIp { get; set; } = "";
    public int ReceiverPort { get; set; } = 1883;
    public void ClearMonitorConfiguration()
    {
        Home = null; Monitor = null; MirrorMonitor = null; Outside = false;
    }
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChangeMonitorMode", "settings.json");
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
static class Displays
{
    const int PathSize = 72, ModeSize = 64;
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] byte[] pathData, ref uint modes, [Out] byte[] modeData, IntPtr topology);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint paths, byte[]? pathData, uint modes, byte[]? modeData, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo([In, Out] byte[] request);
    static void Check(int result) { if (result != 0) throw new Win32Exception(result); }
    public static void ExtendAll() => Check(SetDisplayConfig(0, null, 0, null, 0x4 | 0x80));
    public static Layout Capture()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out uint p, out uint m));
            byte[] paths = new byte[p * PathSize], modes = new byte[m * ModeSize];
            int result = QueryDisplayConfig(2, ref p, paths, ref m, modes, IntPtr.Zero);
            if (result == 122) continue;
            Check(result);
            return new Layout { Paths = paths[..((int)p * PathSize)], Modes = modes[..((int)m * ModeSize)] };
        }
        throw new Exception("모니터 구성이 변경 중입니다. 잠시 후 다시 시도하세요.");
    }
    static byte[] Device(byte[] path)
    {
        byte[] request = new byte[420];
        BitConverter.GetBytes(2u).CopyTo(request, 0);
        BitConverter.GetBytes(420u).CopyTo(request, 4);
        Array.Copy(path, 20, request, 8, 8);
        Array.Copy(path, 28, request, 16, 4);
        Check(DisplayConfigGetDeviceInfo(request));
        return request;
    }
    static string Text(byte[] data, int offset, int size) => Encoding.Unicode.GetString(data, offset, size).TrimEnd('\0');
    public static List<MonitorItem> List(Layout layout)
    {
        var result = new List<MonitorItem>();
        for (int i = 0; i < layout.Paths.Length; i += PathSize)
        {
            var d = Device(layout.Paths[i..(i + PathSize)]);
            string key = Text(d, 164, 256), name = Text(d, 36, 128);
            if (string.IsNullOrWhiteSpace(key)) throw new MonitorIdentityException();
            result.Add(new MonitorItem(key, $"{result.Count + 1}: {(name.Length == 0 ? "모니터" : name)}"));
        }
        return result;
    }
    public static Layout BuildClone(Layout layout, int primaryIndex, int mirrorIndex)
    {
        if (primaryIndex == mirrorIndex) throw new Exception("복제 모니터는 JetKVM과 다른 모니터를 선택하세요.");
        int count = layout.Paths.Length / PathSize;
        if (primaryIndex < 0 || mirrorIndex < 0 || primaryIndex >= count || mirrorIndex >= count) throw new Exception("복제할 모니터가 없습니다.");
        byte[] primary = layout.Paths[(primaryIndex * PathSize)..((primaryIndex + 1) * PathSize)];
        byte[] mirror = layout.Paths[(mirrorIndex * PathSize)..((mirrorIndex + 1) * PathSize)];
        if (!primary.AsSpan(0, 8).SequenceEqual(mirror.AsSpan(0, 8))) throw new Exception("서로 다른 그래픽 어댑터의 화면 복제는 지원하지 않습니다.");
        // Both outputs reference JetKVM's one desktop source. Its first path
        // has priority and the source origin (0,0) makes this the primary group.
        Array.Copy(primary, 0, mirror, 0, 20);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(primary, 32);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(mirror, 32);
        byte[] modes = (byte[])layout.Modes.Clone();
        int sourceMode = (int)BitConverter.ToUInt32(primary, 12);
        if (sourceMode < 0 || sourceMode >= modes.Length / ModeSize) throw new Exception("주 화면 모드 정보를 찾을 수 없습니다.");
        Array.Clear(modes, sourceMode * ModeSize + 28, 8);
        return new Layout { Paths = [.. primary, .. mirror], Modes = modes };
    }
    public static void Apply(Layout layout, string? only = null, bool validateOnly = false, string? mirror = null)
    {
        if (layout.Paths.Length == 0 || layout.Paths.Length % PathSize != 0 || layout.Modes.Length % ModeSize != 0) throw new Exception("저장된 화면 구성이 올바르지 않습니다.");
        byte[] paths = layout.Paths;
        byte[] modes = (byte[])layout.Modes.Clone();
        if (only != null)
        {
            var monitors = List(layout);
            int index = monitors.FindIndex(x => x.Key == only);
            if (index < 0) throw new Exception("선택한 모니터가 없습니다. 집 모드에서 설정을 다시 저장하세요.");
            if (mirror != null)
            {
                if (!monitors[index].Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase)) throw new Exception("화면 복제 시 주 모니터는 JetKVM을 선택하세요.");
                var clone = BuildClone(layout, index, monitors.FindIndex(x => x.Key == mirror));
                Apply(clone, validateOnly: validateOnly); return;
            }
            paths = layout.Paths[(index * PathSize)..((index + 1) * PathSize)];
            int sourceMode = (int)BitConverter.ToUInt32(paths, 12);
            if (sourceMode >= 0 && sourceMode < modes.Length / ModeSize) Array.Clear(modes, sourceMode * ModeSize + 28, 8);
        }
        Check(SetDisplayConfig((uint)(paths.Length / PathSize), paths, (uint)(modes.Length / ModeSize), modes, 0x40 | 0x20 | 0x400));
        if (validateOnly) return;
        Check(SetDisplayConfig((uint)(paths.Length / PathSize), paths, (uint)(modes.Length / ModeSize), modes, 0x20 | 0x80 | 0x400 | 0x200));
    }

}
static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("ChangeMonitorMode") != null; } }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("ChangeMonitorMode", $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue("ChangeMonitorMode", false);
    }
}
class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Icon homeIcon = MakeIcon(false), outsideIcon = MakeIcon(true);
    readonly Settings settings;
    readonly Control commands = new();
    readonly CancellationTokenSource stopping = new();
    bool busy;
    bool startupPending = true;
    readonly SessionPolicy sessionPolicy = new();
    readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 1000 };
    JetSessionReceiver? receiver;
    string jetStatus = "자동 감지 꺼짐";
    string? lastError;
    DateTimeOffset receiverStarted;
    bool missingStatusNotified;
    DateTimeOffset retryAfter;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    public TrayApp(bool? requestedMode = null)
    {
        _ = commands.Handle;
        string? loadError = null;
        try { settings = File.Exists(Settings.FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath)) ?? new() : new(); }
        catch (Exception ex) { settings = new(); loadError = ex.Message; }
        var menu = new ContextMenuStrip();
        menu.Items.Add("집 모드", null, (_, _) => Change(false));
        menu.Items.Add("외부 모드", null, (_, _) => Change(true));
        menu.Items.Add("설정…", null, (_, _) => Configure());
        var detect = new ToolStripMenuItem("JetKVM 접속 자동 전환") { Checked = settings.JetAuto };
        detect.Click += (_, _) =>
        {
            settings.JetAuto = !settings.JetAuto; detect.Checked = settings.JetAuto;
            settings.Save(); StartReceiver();
        };
        menu.Items.Add(detect);
        menu.Items.Add("자동 감지 상태", null, (_, _) => MessageBox.Show($"{jetStatus}\nJetKVM: {settings.JetIp}\n상태 수신: {settings.ReceiverIp}:{settings.ReceiverPort}\n접속 종료 후 10초 대기", "JetKVM 자동 전환"));
        menu.Items.Add("바탕화면 바로가기 추가", null, (_, _) =>
        {
            try
            {
                using var installer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--install-shortcuts") { UseShellExecute = false, CreateNoWindow = true });
                installer!.WaitForExit();
                if (installer.ExitCode != 0) throw new Exception(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "shortcut-install-error.txt")));
                tray!.ShowBalloonTip(3000, "바로가기 추가 완료", "각 모니터에 집 모드·외부 모드 바로가기를 배치했습니다.", ToolTipIcon.Info);
            }
            catch (Exception ex) { Error(ex); }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => { Recovery.Stop(); ExitThread(); });
        tray = new NotifyIcon { Visible = true, ContextMenuStrip = menu };
        Update();
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Change(!settings.Outside); };
        _ = Listen();

        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            if (settings.Home == null || loadError != null)
            {
                timer.Stop(); timer.Dispose();
                if (loadError != null) ResetMonitorConfiguration(new Exception("설정 읽기 실패: " + loadError));
                else Configure();
                startupPending = false;
                if (settings.Home != null) Change(requestedMode ?? false);
                return;
            }
            try
            {
                bool outside = requestedMode ?? false;
                Displays.Apply(settings.Home, outside ? settings.Monitor ?? throw new Exception("외부 모니터를 선택하세요.") : null, mirror: outside ? settings.MirrorMonitor : null);
                settings.Outside = outside; settings.Save(); Update();
                timer.Stop(); timer.Dispose();
                startupPending = false;
                WriteJetStatus();
            }
            catch (Exception ex)
            {
                if (ex is Win32Exception { NativeErrorCode: 87 or 1168 } || ++attempts >= 5)
                {
                    timer.Stop(); timer.Dispose(); startupPending = false;
                    ResetMonitorConfiguration(ex);
                    if (settings.Home != null) Change(requestedMode ?? false);
                }
            }
        };
        timer.Start();
        sessionTimer.Tick += (_, _) =>
        {
            // Keep received MQTT state, but apply it only after the startup layout.
            if (!settings.JetAuto || busy || startupPending || settings.Home == null) return;
            var now = DateTimeOffset.UtcNow;
            bool? desired = sessionPolicy.Desired(now);
            if (sessionPolicy.Count == null && receiver != null)
            {
                if (now - receiverStarted >= TimeSpan.FromSeconds(30) && !missingStatusNotified)
                {
                    missingStatusNotified = true;
                    jetStatus = "접속 상태를 받지 못하고 있습니다 · 자동 전환 대기";
                    tray.ShowBalloonTip(8000, "JetKVM 자동 감지 연결 확인", "JetKVM 설정 → MQTT에서 Connected인지 확인하세요. Disconnected라면 Save & Reconnect를 누르세요.", ToolTipIcon.Warning);
                    Update();
                }
                WriteJetStatus();
            }
            if (desired == null || now < retryAfter) return;
            if (settings.Outside != desired.Value) Change(desired.Value, automatic: true);
            if (settings.Outside == desired.Value) sessionPolicy.Applied(desired.Value);
            else retryAfter = now.AddSeconds(10);
        };
        sessionTimer.Start(); StartReceiver();
    }
    void OnUi(Action action)
    {
        if (!stopping.IsCancellationRequested && !commands.IsDisposed)
        {
            try { commands.BeginInvoke(action); } catch (InvalidOperationException) { }
        }
    }
    void WriteJetStatus()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings.FilePath)!);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "jet-status.json"), JsonSerializer.Serialize(new { Enabled = settings.JetAuto, Status = jetStatus, ActiveSessions = sessionPolicy.Count, LastReceivedUtc = sessionPolicy.LastSeen, Outside = settings.Outside, MirrorEnabled = settings.MirrorMonitor != null, Executable = Environment.ProcessPath, LastError = lastError }));
        }
        catch (IOException) { }
    }
    async void StartReceiver()
    {
        receiver?.Dispose(); receiver = null; sessionPolicy.Offline();
        receiverStarted = DateTimeOffset.UtcNow; missingStatusNotified = false;
        jetStatus = settings.JetAuto ? "JetKVM 접속 상태 수신 대기" : "자동 감지 꺼짐";
        WriteJetStatus();
        if (!settings.JetAuto) return;
        JetSessionReceiver? candidate = null;
        candidate = new JetSessionReceiver(settings.JetIp, settings.ReceiverIp, settings.ReceiverPort,
            count => OnUi(() =>
            {
                if (receiver != candidate) return;
                if (count.HasValue) { sessionPolicy.Observe(count.Value, DateTimeOffset.UtcNow); missingStatusNotified = false; receiverStarted = DateTimeOffset.UtcNow; }
                else { sessionPolicy.Offline(); receiverStarted = DateTimeOffset.UtcNow; }
                WriteJetStatus();
            }), status => OnUi(() => { if (receiver == candidate) { jetStatus = status; Update(); WriteJetStatus(); } }));
        receiver = candidate;
        try { await candidate.Start(); }
        catch (Exception ex) { if (receiver == candidate) { candidate.Dispose(); receiver = null; jetStatus = "상태 수신 시작 실패: " + ex.Message; WriteJetStatus(); Error(ex); } }
    }
    async Task Listen()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Program.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                string? command = await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false);
                if (command is "home" or "outside") commands.BeginInvoke(() => Change(command == "outside"));
                await writer.WriteLineAsync(command is "home" or "outside" or "ping" ? "OK" : "INVALID").ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (ObjectDisposedException) { break; }
        }
    }
    void Update() { tray.Icon = settings.Outside ? outsideIcon : homeIcon; tray.Text = "ChangeMonitorMode · " + (settings.Outside ? "외부 모드" : "집 모드") + (settings.JetAuto ? (sessionPolicy.Count.HasValue ? " · 자동 감지" : " · 감지 대기") : ""); }
    void Error(Exception ex) { lastError = ex.Message; WriteJetStatus(); tray.ShowBalloonTip(8000, "화면 구성 적용 실패", ex.Message, ToolTipIcon.Error); }
    void ResetMonitorConfiguration(Exception reason)
    {
        timer.Stop(); startupPending = false;
        try
        {
            if (File.Exists(Settings.FilePath))
                File.Copy(Settings.FilePath, Settings.FilePath + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), false);
            settings.ClearMonitorConfiguration();
            settings.Save();
            sessionPolicy.Offline(); retryAfter = default;
            lastError = "모니터 설정을 초기화했습니다: " + reason.Message;
            Update(); WriteJetStatus();
            tray.ShowBalloonTip(8000, "모니터 설정 다시 저장", "포트 또는 모니터 구성이 변경되어 설정을 초기화했습니다. 전체 모니터를 켠 뒤 설정을 저장하세요.", ToolTipIcon.Warning);
            // Ask Windows for the available extended topology before capturing
            // the replacement home layout, rather than saving an outside clone.
            try { Displays.ExtendAll(); } catch (Exception ex) { Error(ex); }
            Configure();
        }
        catch (Exception ex) { Error(ex); }
    }
    void Change(bool outside, bool automatic = false)
    {
        if (busy) return;
        timer.Stop();
        startupPending = false;
        if (settings.Home == null || (outside && settings.Monitor == null))
        {
            if (automatic) { jetStatus = "모니터 설정이 필요합니다"; WriteJetStatus(); return; }
            Configure();
            if (settings.Home == null || (outside && settings.Monitor == null)) return;
        }
        busy = true;
        Exception? displayError = null;
        bool displayApplied = false;
        try { Displays.Apply(settings.Home, outside ? settings.Monitor : null, mirror: outside ? settings.MirrorMonitor : null); displayApplied = true; settings.Outside = outside; lastError = null; Update(); settings.Save(); WriteJetStatus(); }
        catch (Exception ex) { if (!displayApplied) displayError = ex; else Error(ex); }
        finally { busy = false; }
        if (displayError != null) ResetMonitorConfiguration(displayError);
    }
    void Configure()
    {
        if (busy) return;
        timer.Stop();
        busy = true;
        Exception? invalidConfiguration = null;
        try
        {
            Layout layout = settings.Home ?? Displays.Capture();
            var items = Displays.List(layout);
            using var form = new Form { Text = "ChangeMonitorMode 설정", ClientSize = new Size(470, 345), StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var label = new Label { Text = "외부 모드에서 사용할 Jet KVM 연결 모니터", Left = 20, Top = 20, AutoSize = true };
            var combo = new ComboBox { Left = 20, Top = 48, Width = 425, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(items.Cast<object>().ToArray());
            combo.SelectedIndex = Math.Max(0, items.FindIndex(x => settings.Monitor != null ? x.Key == settings.Monitor : x.Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase)));
            var mirrorLabel = new Label { Text = "외부 모드에서 같은 화면을 표시할 모니터", Left = 20, Top = 90, AutoSize = true };
            var mirrorCombo = new ComboBox { Left = 20, Top = 115, Width = 425, DropDownStyle = ComboBoxStyle.DropDownList };
            void RefreshMirrors(string? key)
            {
                mirrorCombo.Items.Clear(); mirrorCombo.Items.Add(new MonitorItem("", "없음 — JetKVM만 사용"));
                string? mainKey = (combo.SelectedItem as MonitorItem)?.Key;
                mirrorCombo.Items.AddRange(items.Where(x => x.Key != mainKey).Cast<object>().ToArray());
                mirrorCombo.SelectedIndex = Math.Max(0, mirrorCombo.Items.Cast<MonitorItem>().ToList().FindIndex(x => x.Key == key));
            }
            RefreshMirrors(settings.MirrorMonitor);
            combo.SelectedIndexChanged += (_, _) => RefreshMirrors((mirrorCombo.SelectedItem as MonitorItem)?.Key);
            var hint = new Label { Text = "선택 시 JetKVM이 주 화면이 되고 두 화면이 복제됩니다.\n두 화면이 공통 지원하는 해상도가 적용될 수 있습니다.", Left = 20, Top = 153, Width = 430, Height = 40 };
            var auto = new CheckBox { Text = "Windows 로그인 시 자동 실행 (집 모드로 시작)", Left = 20, Top = 205, Width = 430, Checked = Startup.Enabled };
            var capture = new Button { Text = "현재 전체 모니터 구성 다시 읽기", Left = 20, Top = 240, Width = 280, Enabled = !settings.Outside };
            capture.Click += (_, _) =>
            {
                try { layout = Displays.Capture(); items = Displays.List(layout); combo.Items.Clear(); combo.Items.AddRange(items.Cast<object>().ToArray()); combo.SelectedIndex = Math.Max(0, items.FindIndex(x => settings.Monitor != null ? x.Key == settings.Monitor : x.Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase))); RefreshMirrors(settings.MirrorMonitor); }
                catch (Exception ex) { MessageBox.Show(form, ex.Message); }
            };
            var save = new Button { Text = "저장", Left = 270, Top = 295, Width = 80 };
            var cancel = new Button { Text = "취소", Left = 365, Top = 295, Width = 80, DialogResult = DialogResult.Cancel };
            save.Click += (_, _) =>
            {
                try
                {
                    if (combo.SelectedItem is not MonitorItem selected) throw new Exception("모니터를 선택하세요.");
                    string? mirrorKey = (mirrorCombo.SelectedItem as MonitorItem)?.Key;
                    if (string.IsNullOrEmpty(mirrorKey)) mirrorKey = null;
                    Displays.Apply(layout, selected.Key, validateOnly: true, mirror: mirrorKey);
                    if (settings.Outside) Displays.Apply(layout, selected.Key, mirror: mirrorKey);
                    Startup.Set(auto.Checked);
                    settings.Home = layout; settings.Monitor = selected.Key; settings.MirrorMonitor = mirrorKey; settings.Save();
                    form.DialogResult = DialogResult.OK;
                }
                catch (Exception ex) { MessageBox.Show(form, ex.Message, "설정 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            form.Controls.AddRange([label, combo, mirrorLabel, mirrorCombo, hint, auto, capture, save, cancel]); form.AcceptButton = save; form.CancelButton = cancel; form.ShowDialog();
        }
        catch (Exception ex)
        {
            if (settings.Home != null && ex is Win32Exception or MonitorIdentityException) invalidConfiguration = ex;
            else Error(ex);
        }
        finally { busy = false; startupPending = false; }
        if (invalidConfiguration != null) ResetMonitorConfiguration(invalidConfiguration);
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    internal static Icon MakeIcon(bool outside)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var pen = new Pen(Color.White, 2.5f);
        using var brush = new SolidBrush(outside ? Color.FromArgb(245, 110, 0) : Color.FromArgb(0, 170, 65));
        if (outside)
        {
            g.FillEllipse(brush, 1, 1, 30, 30);
            g.DrawEllipse(pen, 5, 5, 22, 22);
            g.DrawEllipse(pen, 11, 5, 10, 22);
            g.DrawLine(pen, 5, 16, 27, 16);
        }
        else
        {
            g.FillPolygon(brush, [new Point(1, 14), new Point(16, 1), new Point(31, 14), new Point(27, 14), new Point(27, 31), new Point(5, 31), new Point(5, 14)]);
            g.DrawLines(pen, [new Point(7, 15), new Point(16, 7), new Point(25, 15)]);
            g.DrawRectangle(pen, 10, 15, 12, 12);
            g.DrawLine(pen, 16, 21, 16, 27);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); } finally { DestroyIcon(handle); }
    }
    protected override void ExitThreadCore() { stopping.Cancel(); receiver?.Dispose(); sessionTimer.Dispose(); timer.Dispose(); commands.Dispose(); tray.Visible = false; tray.Dispose(); homeIcon.Dispose(); outsideIcon.Dispose(); base.ExitThreadCore(); }
}
static class Shortcuts
{
    public static void Install()
    {
        string folder = AppContext.BaseDirectory;
        var placements = new List<(string Name, Screen Screen, bool Outside)>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            foreach (var screen in Screen.AllScreens.OrderBy(x => x.DeviceName))
            {
                foreach (bool outside in new[] { false, true })
                {
                    string name = (outside ? "외부 모드" : "집 모드") + (screen.Primary ? "" : " (모니터 " + screen.DeviceName.Replace(@"\\.\DISPLAY", "") + ")");
                    string iconPath = Path.Combine(folder, outside ? "outside.ico" : "home.ico");
                    using (var icon = TrayApp.MakeIcon(outside)) using (var file = File.Create(iconPath)) icon.Save(file);
                    dynamic shortcut = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk"));
                    try
                    {
                        shortcut.TargetPath = Environment.ProcessPath!;
                        shortcut.Arguments = outside ? "--outside" : "--home";
                        shortcut.WorkingDirectory = folder;
                        shortcut.IconLocation = iconPath + ",0";
                        shortcut.Description = name + "로 모니터 구성 전환";
                        shortcut.Hotkey = screen.Primary ? (outside ? "CTRL+ALT+E" : "CTRL+ALT+H") : "";
                        shortcut.Save();
                        placements.Add((name, screen, outside));
                    }
                    finally { Marshal.FinalReleaseComObject(shortcut); }
                }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        DesktopIcons.Place(placements);
    }
}
static class Program
{
    internal static string PipeName => "ChangeMonitorMode-" + WindowsIdentity.GetCurrent().User!.Value + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    static void Send(string command)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(5000);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        writer.WriteLine(command);
        if (reader.ReadLine() != "OK") throw new IOException("실행 중인 프로그램이 명령을 수락하지 않았습니다.");
    }
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--watchdog")) { Recovery.Watch(args); return; }
        if (args.Contains("--self-test")) { SelfTest.Run().GetAwaiter().GetResult(); return; }
        if (args.Contains("--probe-clone"))
        {
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath))!;
            var home = saved.Home ?? throw new Exception("집 모드 구성이 없습니다.");
            var monitors = Displays.List(home);
            var jet = monitors.First(x => x.Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase));
            var mirror = monitors.First(x => x.Key != jet.Key);
            var before = Displays.Capture();
            try
            {
                Displays.Apply(home, jet.Key, mirror: mirror.Key);
                var actual = Displays.Capture();
                var names = Displays.List(actual).Select(x => x.Name).ToArray();
                if (actual.Paths.Length != 144 || !actual.Paths.AsSpan(0, 12).SequenceEqual(actual.Paths.AsSpan(72, 12))) throw new Exception("두 화면의 실제 복제 상태를 확인하지 못했습니다.");
                int mode = (int)BitConverter.ToUInt32(actual.Paths, 12);
                if (BitConverter.ToInt32(actual.Modes, mode * 64 + 28) != 0 || BitConverter.ToInt32(actual.Modes, mode * 64 + 32) != 0) throw new Exception("복제 화면이 주 화면으로 설정되지 않았습니다.");
                if (!names[0].Contains("JetKVM", StringComparison.OrdinalIgnoreCase)) throw new Exception("JetKVM 화면 우선순위를 확인하지 못했습니다.");
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "clone-live-test.json"), JsonSerializer.Serialize(new { Result = "passed", ActiveMonitors = names, SharedPrimarySource = true, Width = BitConverter.ToInt32(actual.Modes, mode * 64 + 16), Height = BitConverter.ToInt32(actual.Modes, mode * 64 + 20) }));
            }
            finally { Displays.Apply(before); }
            return;
        }
        if (args.Contains("--validate-clones"))
        {
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath))!;
            var home = saved.Home ?? throw new Exception("집 모드 구성이 없습니다.");
            var monitors = Displays.List(home);
            var jet = monitors.First(x => x.Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase));
            var results = new List<object>();
            foreach (var monitor in monitors.Where(x => x.Key != jet.Key))
            {
                try { Displays.Apply(home, jet.Key, validateOnly: true, mirror: monitor.Key); results.Add(new { Monitor = monitor.Name, Supported = true, Error = (string?)null }); }
                catch (Exception ex) { results.Add(new { Monitor = monitor.Name, Supported = false, Error = ex.Message }); }
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "clone-validation.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        if (args.Contains("--select-jet-monitor"))
        {
            var selected = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath)) ?? throw new Exception("설정을 읽을 수 없습니다.");
            var layout = selected.Home ?? throw new Exception("집 모드 구성이 없습니다.");
            string key = Displays.List(layout).FirstOrDefault(x => x.Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase))?.Key ?? throw new Exception("JetKVM 모니터를 찾지 못했습니다.");
            Displays.Apply(layout, key, validateOnly: true);
            if (selected.Outside) Displays.Apply(layout, key, mirror: selected.MirrorMonitor);
            selected.Monitor = key; selected.Save(); return;
        }
        // A development copy must not take the singleton away from the installed
        // executable whose path is registered in Windows Firewall and startup.
        string directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? ancestor = directory;
        while (ancestor != null && !File.Exists(Path.Combine(ancestor, "jetkvm-monitor-switcher.csproj"))) ancestor = Directory.GetParent(ancestor)?.FullName;
        if (!args.Contains("--diagnose") && !args.Contains("--dev") && ancestor != null && directory.StartsWith(Path.Combine(ancestor, "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string installed = Path.Combine(ancestor, "publish", "jetkvm-monitor-switcher.exe");
            if (File.Exists(installed))
            {
                var launch = new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string argument in args) launch.ArgumentList.Add(argument);
                System.Diagnostics.Process.Start(launch); return;
            }
        }
        if (args.Contains("--enable-jet-auto"))
        {
            var settings = File.Exists(Settings.FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath))! : new Settings();
            settings.Home ??= Displays.Capture();
            settings.Monitor ??= Displays.List(settings.Home).FirstOrDefault(x => x.Name.Contains("JetKVM", StringComparison.OrdinalIgnoreCase))?.Key ?? throw new Exception("JetKVM 모니터를 찾을 수 없습니다.");
            Displays.Apply(settings.Home, settings.Monitor, validateOnly: true, mirror: settings.MirrorMonitor);
            settings.JetAuto = true; settings.Save(); return;
        }
        if (args.Contains("--install-shortcuts"))
        {
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            try { Shortcuts.Install(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shortcut-install-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--ping")) { Send("ping"); return; }
        if (args.Contains("--diagnose"))
        {
            var layout = Displays.Capture();
            Displays.Apply(layout, validateOnly: true);
            foreach (var monitor in Displays.List(layout)) Displays.Apply(layout, monitor.Key, validateOnly: true);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics.json"), JsonSerializer.Serialize(new { Validation = "Home and each single-monitor layout passed SDC_VALIDATE", PathCount = layout.Paths.Length / 72, ModeCount = layout.Modes.Length / 64, Monitors = Displays.List(layout).Select(x => x.Name) }));
            return;
        }
        using var mutex = new Mutex(true, @"Local\ChangeMonitorMode", out bool first);
        bool? requestedMode = args.Contains("--outside") ? true : args.Contains("--home") ? false : null;
        if (!first)
        {
            if (requestedMode.HasValue)
            {
                try { Send(requestedMode.Value ? "outside" : "home"); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "모드 전환 명령 전송 실패", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Recovery.Start(args.Contains("--dev"));
        if (args.Contains("--recover") && !requestedMode.HasValue)
        {
            try { requestedMode = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Settings.FilePath))?.Outside; }
            catch { }
        }
        Application.Run(new TrayApp(requestedMode));
    }
}
