using System.Net;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace JetKvmMonitorSwitcher;

// All policy calls run on the UI thread. Unknown/offline data never means zero sessions.
sealed class SessionPolicy
{
    public int? Count { get; private set; }
    public DateTimeOffset? LastSeen { get; private set; }
    DateTimeOffset? zeroSince;
    bool? handled;
    public void Observe(int count, DateTimeOffset now)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (Count != count && (Count == null || (Count > 0) != (count > 0))) handled = null;
        zeroSince = count == 0 ? zeroSince ?? now : null;
        Count = count; LastSeen = now;
    }
    public void Offline() { Count = null; LastSeen = null; zeroSince = null; handled = null; }
    public bool? Desired(DateTimeOffset now)
    {
        if (LastSeen == null || now - LastSeen > TimeSpan.FromSeconds(45)) { Offline(); return null; }
        bool? desired = Count > 0 ? true : zeroSince != null && now - zeroSince >= TimeSpan.FromSeconds(10) ? false : null;
        return desired != handled ? desired : null;
    }
    public void Applied(bool outside) => handled = outside;
}
sealed class JetSessionReceiver : IDisposable
{
    MQTTnet.Server.MqttServer? server;
    readonly Action<int?> update;
    readonly Action<string> status;
    readonly string deviceIp, bindIp;
    readonly int port;
    public JetSessionReceiver(string deviceIp, string bindIp, int port, Action<int?> update, Action<string> status)
    {
        this.deviceIp = deviceIp; this.bindIp = bindIp; this.port = port; this.update = update; this.status = status;
    }
    public async Task Start()
    {
        var address = ValidateConfiguration(deviceIp, bindIp, port);
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(address).WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).WithDefaultEndpointPort(port).Build();
        server = new MqttFactory().CreateMqttServer(options);
        server.ValidatingConnectionAsync += args =>
        {
            // Only this JetKVM may connect. No subscriptions/control traffic are permitted.
            string endpoint = args.Endpoint;
            if (!endpoint.StartsWith(deviceIp + ":", StringComparison.Ordinal) && endpoint != deviceIp) args.ReasonCode = MqttConnectReasonCode.NotAuthorized;
            status(args.ReasonCode == MqttConnectReasonCode.Success ? "JetKVM 상태 전송 연결됨" : "허용되지 않은 장치 접속 거절");
            return Task.CompletedTask;
        };
        server.InterceptingSubscriptionAsync += args => { args.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized; return Task.CompletedTask; };
        server.InterceptingPublishAsync += args =>
        {
            args.ProcessPublish = false; // Consume status locally; never relay or retain it.
            try
            {
                if (args.ApplicationMessage.Topic.EndsWith("/sessions/state", StringComparison.Ordinal))
                {
                    using var json = JsonDocument.Parse(args.ApplicationMessage.PayloadSegment);
                    if (!json.RootElement.TryGetProperty("active_sessions", out var value) || !value.TryGetInt32(out int count) || count < 0) throw new FormatException();
                    update(count); status("접속 " + count + "명");
                }
                else if (args.ApplicationMessage.Topic.EndsWith("/status", StringComparison.Ordinal))
                {
                    using var json = JsonDocument.Parse(args.ApplicationMessage.PayloadSegment);
                    if (json.RootElement.TryGetProperty("online", out var online) && online.ValueKind == JsonValueKind.False) { update(null); status("JetKVM 연결 끊김 · 모드 유지"); }
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { update(null); status("상태 정보 오류 · 모드 유지"); }
            return Task.CompletedTask;
        };
        server.ClientDisconnectedAsync += args => { update(null); status("상태 수신 끊김 · 모드 유지"); return Task.CompletedTask; };
        await server.StartAsync();
        status("JetKVM 접속 상태 수신 대기");
    }
    internal static IPAddress ValidateConfiguration(string deviceIp, string bindIp, int port)
    {
        static IPAddress Parse(string value, string name)
        {
            if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
                throw new ArgumentException($"{name}에 올바른 IPv4 주소를 입력하세요. settings.json의 JetIp와 ReceiverIp를 확인하세요.");
            return address;
        }
        Parse(deviceIp, "JetKVM IP(JetIp)");
        var address = Parse(bindIp, "PC 수신 IP(ReceiverIp)");
        if (port is < 1 or > 65535) throw new ArgumentException("MQTT 수신 포트(ReceiverPort)는 1~65535여야 합니다.");
        return address;
    }
    public void Dispose() { server?.Dispose(); server = null; }
}
