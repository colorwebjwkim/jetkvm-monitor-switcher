using System.Diagnostics;

namespace JetKvmMonitorSwitcher;

// A separate process survives a crash or a forced termination of the tray app.
static class Recovery
{
    static EventWaitHandle? stop;
    public static void Stop() => stop?.Set();

    public static void Start(bool development)
    {
        string token = @"Local\jetkvm-monitor-switcher-Recovery-" + Guid.NewGuid().ToString("N");
        stop = new EventWaitHandle(false, EventResetMode.ManualReset, token);
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Stop();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-ready");
        var launch = Launch();
        launch.ArgumentList.Add("--watchdog");
        launch.ArgumentList.Add(Environment.ProcessId.ToString());
        launch.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
        launch.ArgumentList.Add(token);
        if (development) launch.ArgumentList.Add("--dev");
        using var watcher = Process.Start(launch) ?? throw new IOException("자동 재실행 감시 프로세스를 시작하지 못했습니다.");
        if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
        {
            Stop();
            throw new IOException("자동 재실행 감시 프로세스가 응답하지 않습니다.");
        }
    }

    static ProcessStartInfo Launch() => new(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = AppContext.BaseDirectory
    };

    public static void Watch(string[] args)
    {
        int index = Array.IndexOf(args, "--watchdog");
        using var stopped = EventWaitHandle.OpenExisting(args[index + 3]);
        using var ready = EventWaitHandle.OpenExisting(args[index + 3] + "-ready");
        // Verify creation time so a reused PID can never be watched by mistake.
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(int.Parse(args[index + 1]));
            if (parent.StartTime.ToUniversalTime().Ticks != long.Parse(args[index + 2])) { parent.Dispose(); parent = null; }
        }
        catch (ArgumentException) { }
        ready.Set();
        using (parent)
        {
            while (parent != null && !parent.WaitForExit(1000))
            {
                if (stopped.WaitOne(0)) return;
            }
        }
        if (stopped.WaitOne(TimeSpan.FromSeconds(3))) return;
        // Each recovered app starts its own watcher. Repeated crashes have a
        // fixed delay, and the existing tray singleton prevents duplicates.
        var launch = Launch();
        launch.ArgumentList.Add("--recover");
        if (args.Contains("--dev")) launch.ArgumentList.Add("--dev");
        using var restarted = Process.Start(launch);
    }
}
