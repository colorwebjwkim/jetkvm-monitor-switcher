using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace JetKvmMonitorSwitcher;

static class SelfTest
{
    public static async Task Run()
    {
        var now = DateTimeOffset.UtcNow;
        var p = new SessionPolicy();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var resetSettings = new Settings { Home = new Layout(), Monitor = "old-port", MirrorMonitor = "old-mirror", Outside = true, JetAuto = true, JetIp = "192.0.2.10", ReceiverIp = "192.0.2.11", ReceiverPort = 1885 };
        resetSettings.ClearMonitorConfiguration();
        Check(resetSettings.Home == null && resetSettings.Monitor == null && resetSettings.MirrorMonitor == null && !resetSettings.Outside, "Monitor reset kept stale configuration");
        Check(resetSettings.JetAuto && resetSettings.JetIp == "192.0.2.10" && resetSettings.ReceiverIp == "192.0.2.11" && resetSettings.ReceiverPort == 1885, "Monitor reset changed MQTT configuration");
        var originalPaths = new byte[216];
        var originalModes = new byte[192];
        for (int i = 0; i < 3; i++)
        {
            BitConverter.GetBytes(7L).CopyTo(originalPaths, i * 72);
            BitConverter.GetBytes((uint)(i + 10)).CopyTo(originalPaths, i * 72 + 8);
            BitConverter.GetBytes((uint)i).CopyTo(originalPaths, i * 72 + 12);
            BitConverter.GetBytes((uint)(i + 20)).CopyTo(originalPaths, i * 72 + 28);
            BitConverter.GetBytes(100 + i).CopyTo(originalModes, i * 64 + 28);
        }
        var original = new Layout { Paths = originalPaths, Modes = originalModes };
        var livePaths = (byte[])originalPaths.Clone();
        for (int i = 0; i < 3; i++)
        {
            BitConverter.GetBytes(99L).CopyTo(livePaths, i * 72);
            BitConverter.GetBytes(99L).CopyTo(livePaths, i * 72 + 20);
            BitConverter.GetBytes(uint.MaxValue).CopyTo(originalPaths, i * 72 + 32);
        }
        var rebound = Displays.Rebind(original, new Layout { Paths = livePaths, MonitorKeys = ["a", "b", "c"] }, ["a", "b", "c"]);
        Check(BitConverter.ToInt64(rebound.Paths, 0) == 99 && BitConverter.ToInt64(rebound.Modes, 8) == 99, "Reboot adapter identity not rebound");
        Check(BitConverter.ToInt64(original.Paths, 0) == 7 && BitConverter.ToInt32(rebound.Modes, 28) == 100, "Rebind changed home layout");
        bool missingRejected = false;
        try { Displays.Rebind(original, new Layout { Paths = livePaths, MonitorKeys = ["a", "b", "other"] }, ["a", "b", "c"]); } catch (MonitorIdentityException) { missingRejected = true; }
        Check(missingRejected, "Missing monitor silently replaced");
        var clone = Displays.BuildClone(original, 2, 0);
        Check(clone.Paths.Length == 144, "Clone enabled unexpected monitors");
        Check(BitConverter.ToUInt32(clone.Paths, 28) == 22 && BitConverter.ToUInt32(clone.Paths, 100) == 20, "JetKVM path lost priority or target identity");
        Check(clone.Paths.AsSpan(0, 20).SequenceEqual(clone.Paths.AsSpan(72, 20)), "Outputs do not share one source");
        Check(BitConverter.ToInt32(clone.Modes, 156) == 0 && BitConverter.ToInt32(clone.Modes, 160) == 0, "Clone desktop is not primary");
        Check(BitConverter.ToInt32(original.Modes, 156) == 102 && BitConverter.ToUInt32(original.Paths, 8) == 10, "Home layout was mutated");
        bool rejectedSame = false;
        try { Displays.BuildClone(original, 1, 1); } catch { rejectedSame = true; }
        Check(rejectedSame, "Same output clone accepted");
        Check(p.Desired(now) == null, "Unknown state changed mode");
        p.Observe(1, now); Check(p.Desired(now) == true, "Connect did not request outside");
        p.Applied(true); p.Observe(2, now.AddSeconds(1)); Check(p.Desired(now.AddSeconds(1)) == null, "Repeated connected count changed mode");
        p.Observe(0, now.AddSeconds(2)); Check(p.Desired(now.AddSeconds(11)) == null, "Home too early");
        Check(p.Desired(now.AddSeconds(12)) == false, "Home countdown failed");
        p.Observe(1, now.AddSeconds(13)); Check(p.Desired(now.AddSeconds(13)) == true, "Reconnect did not cancel home");
        p.Observe(0, now.AddSeconds(14)); p.Offline(); Check(p.Desired(now.AddSeconds(30)) == null, "Offline treated as zero");
        p.Observe(1, now); Check(p.Desired(now.AddSeconds(46)) == null, "Stale state changed mode");
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = new JetSessionReceiver("127.0.0.1", "127.0.0.1", 18884, count => { if (count.HasValue) received.TrySetResult(count.Value); else offline.TrySetResult(); }, _ => { });
        await receiver.Start();
        using var client = new MqttFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", 18884).Build());
        var subscription = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("jetkvm/test/#").Build());
        Check(subscription.Items.First().ResultCode != MqttClientSubscribeResultCode.GrantedQoS0, "Subscriptions allowed");
        await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("jetkvm/test/sessions/state").WithPayload("{\"active_sessions\":1}").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        Check(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)) == 1, "MQTT receiver failed");
        await client.DisconnectAsync(); await offline.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.json"), "{\"result\":\"passed\",\"reset\":\"stale monitor configuration cleared, MQTT settings preserved\",\"clone\":\"priority, shared primary source, home preservation, duplicate rejection\",\"policy\":\"connect, countdown, reconnect, offline, stale\",\"mqtt\":\"publish received, subscriptions rejected, disconnect detected\"}");
    }
}
