using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulsar;

sealed class BluetoothBusyException() : IOException("Windows заблокировала объявление по Bluetooth. Восстановление может ненадолго переподключить аксессуары.");

record Ask(string Id, string Sender, string[] Files, long Bytes);

record Device(string Name, string Address, int Port)
{
    public override string ToString() => Name;
    public static Device Validate(string name, string address, int port)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Не указано имя устройства.");
        if (!IPAddress.TryParse(address, out var ip) || !ip.IsIPv6LinkLocal || port is < 1 or > 65535)
            throw new ArgumentException("Недопустимый адрес устройства AirDrop.");
        return new Device(name.Length > 100 ? name[..100] : name, ip.ToString().Split('%')[0], port);
    }
}

sealed class Session
{
    public event Action<string>? Log;
    public event Action? Changed;
    public event Action<Device>? DeviceFound;
    public event Action<string>? DeviceRemoved;
    public event Action<Ask>? AskReceived;
    public event Action<string>? AskExpired, Saved;
    public event Action<string, long, long>? Progress;
    public event Action<long, long>? SendProgress;
    public event Action<string>? SendStatus;
    public event Action<string>? ReceiveFailed;
    public readonly string Base = AppContext.BaseDirectory;
    public readonly string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Пульсар");
    public string LogDirectory { get; private set; } = "";
    public string Status { get; private set; } = "Готов к работе";
    public bool Running { get; private set; }
    public bool Working { get; private set; }
    public string Python { get; }
    public string Address { get; private set; } = "";
    public string Sid { get; private set; } = "";
    public int IfIndex { get; private set; }
    readonly SemaphoreSlim gate = new(1, 1);
    readonly List<Process> owned = [];
    readonly HashSet<string> candidates = [];
    readonly object sync = new();
    CancellationTokenSource cancel = new();
    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource linkReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string stopFile = "";
    Process? link, bridge, advertiser, receiver;
    Task? recovery;
    TaskCompletionSource<bool>? bluetoothReady;
    bool advertising;
    string? publisherStatusLog;
    readonly SemaphoreSlim bluetoothGate = new(1, 1);
    int generation;
    public int Generation => generation;
    string? senderError;
    bool sessionWriteFailed;
    string startupStage = "настройка";
    Task? activeScan;

    internal static bool TryAppendLog(string path, string text)
    {
        try { File.AppendAllText(path, text, Encoding.UTF8); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    void ReportWriteFailure()
    {
        if (sessionWriteFailed) return;
        sessionWriteFailed = true;
        Log?.Invoke("Не удалось записать файлы сеанса. Проверьте свободное место и права на папку; диагностические журналы могут быть неполными.");
    }

    void Diagnostic(string kind, object detail)
    {
        if (LogDirectory.Length == 0) return;
        lock (sync) {
            if (!TryAppendLog(Path.Combine(LogDirectory, "diagnostics.jsonl"), JsonSerializer.Serialize(new {
                utc = DateTimeOffset.UtcNow, tick_ms = Environment.TickCount64, kind, detail
            }) + Environment.NewLine)) ReportWriteFailure();
        }
    }

    void StartupStage(string stage)
    {
        startupStage = stage;
        Diagnostic("startup-stage", new { stage });
    }

    internal static async Task AwaitReadyAsync(Task readiness, Process child, string stage, TimeSpan timeout, CancellationToken token)
    {
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task exited = child.WaitForExitAsync(ended.Token);
        Task deadline = Task.Delay(timeout, ended.Token);
        try {
            await Task.WhenAny(readiness, exited, deadline);
            token.ThrowIfCancellationRequested();
            if (child.HasExited) throw new IOException($"{stage}: вспомогательный процесс завершился с кодом {child.ExitCode}.");
            if (!readiness.IsCompleted) throw new TimeoutException($"{stage}: процесс не сообщил о готовности за {timeout.TotalSeconds:0} с.");
            await readiness;
        } finally { ended.Cancel(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WaitNamedPipe(string name, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, StringBuilder path, uint length, uint flags);

    public static string PhysicalDirectory(string directory)
    {
        // Packaged launchers can redirect AppData while an elevated PowerShell helper sees the real path.
        using var marker = new FileStream(Path.Combine(directory, ".path-" + Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.DeleteOnClose);
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(marker.SafeFileHandle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
            throw new IOException("Не удалось определить папку журналов сеанса.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        string resolved = path.ToString();
        if (resolved.StartsWith(@"\\?\UNC\")) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\")) resolved = resolved[4..];
        return Path.GetDirectoryName(resolved)!;
    }

    public Session()
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Base, "settings.json")))!;
        Python = Path.GetFullPath(config["PythonExe"], Base);
        try
        {
            var profile = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(new[] { ProfileFile, LegacyPulseItProfileFile, LegacyProfileFile }.FirstOrDefault(File.Exists) ?? ProfileFile))!;
            Name = CleanName(profile["name"]);
            if (Models.Any(m => m.Model == profile["model"])) Model = profile["model"];
            CompatibleSending = profile.GetValueOrDefault("compatibleSending") == "true";
            MatchChannel = profile.GetValueOrDefault("matchChannel") == "true";
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or ArgumentException) { }
    }

    public static (string Address, string Sid) Identity(byte[] mac)
    {
        if (mac.Length != 6 || (mac[0] & 1) != 0) throw new ArgumentException("Для awdl0 нужен шестибайтовый индивидуальный MAC-адрес.");
        byte[] ip = [0xfe, 0x80, 0, 0, 0, 0, 0, 0, (byte)(mac[0] ^ 2), mac[1], mac[2], 0xff, 0xfe, mac[3], mac[4], mac[5]];
        return (new IPAddress(ip).ToString(), Convert.ToHexString(mac).ToLowerInvariant());
    }

    public static string? PeerAddress(string line)
    {
        var match = Regex.Match(line, @"^peer added ([0-9a-fA-F:]+) ");
        if (!match.Success) return null;
        try { return Identity(match.Groups[1].Value.Split(':').Select(x => Convert.ToByte(x, 16)).ToArray()).Address; }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { return null; }
    }

    void SetStatus(string status) { Status = status; Changed?.Invoke(); }
    void Line(string source, string line)
    {
        lock (sync)
        {
            if (LogDirectory.Length > 0 && !TryAppendLog(Path.Combine(LogDirectory, source + ".log"), line + Environment.NewLine)) ReportWriteFailure();
            var peer = source == "link" ? PeerAddress(line) : null;
            bool peersChanged = peer != null && candidates.Add(peer);
            if (source == "link" && peer != null) Diagnostic("peer-heard", new { peer });
            if (source == "link" && line.StartsWith("peer removed ")) {
                var removed = PeerAddress("peer added " + line["peer removed ".Length..]);
                if (removed != null) {
                    peersChanged |= candidates.Remove(removed);
                    Diagnostic("peer-expired", new { removed });
                    DeviceRemoved?.Invoke(removed);
                }
            }
            if (peersChanged && Scanning) WriteCandidates();
        }
        if (line.StartsWith("metrics ") || line.StartsWith("startup step=")) Diagnostic(source + "-metrics", new { line });
        if (source == "bluetooth") {
            if (line.Contains("state=Started error=Success")) { advertising = true; bluetoothReady?.TrySetResult(true); }
            if (line.Contains("error=ResourceInUse") || line.Contains("state=Aborted")) { advertising = false; bluetoothReady?.TrySetResult(false); }
        }
        if (source == "receiver" && line.Contains(" принимает на [")) ready.TrySetResult();
        if (source == "link" && line.StartsWith("service_port=")) linkReady.TrySetResult();
        if (source == "receiver" && line.StartsWith("Передача сохранена в ")) Saved?.Invoke(line["Передача сохранена в ".Length..]);
        if (source == "receiver") {
            if (line.StartsWith("Не удалось сохранить передачу")) ReceiveFailed?.Invoke("Windows не удалось сохранить файл. Проверьте свободное место и права на папку.");
            else if (line.StartsWith("Некорректная передача отклонена")) ReceiveFailed?.Invoke("Передача отклонена. Подробности в журналах.");
            else if (line.StartsWith("Ошибка соединения передачи")) ReceiveFailed?.Invoke("Соединение прервано до завершения передачи.");
        }
        if (source == "sender") {
            if (line.StartsWith("Подготовка файла")) SendStatus?.Invoke("Подготовка файла…");
            else if (line.StartsWith("Ожидание телефона")) SendStatus?.Invoke("Подключение к iPhone. Оставьте его разблокированным с режимом «Для всех»…");
            else if (line.StartsWith("Запрос подтверждения")) SendStatus?.Invoke("Подтвердите передачу на iPhone.");
        }
        if (line.StartsWith("{\"event\":"))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                switch (root.GetProperty("event").GetString())
                {
                    case "device" when source == "discovery":
                        var found = Device.Validate(root.GetProperty("name").GetString()!, root.GetProperty("address").GetString()!, root.GetProperty("port").GetInt32());
                        Diagnostic("device-verified", found);
                        DeviceFound?.Invoke(found);
                        break;
                    case "ask" when source == "receiver":
                        AskReceived?.Invoke(new Ask(root.GetProperty("id").GetString()!, root.GetProperty("sender").GetString()!,
                            root.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToArray(), root.GetProperty("bytes").GetInt64()));
                        break;
                    case "progress" when source == "receiver":
                        Progress?.Invoke(root.GetProperty("name").GetString()!, root.GetProperty("received").GetInt64(), root.GetProperty("total").GetInt64());
                        break;
                    case "send-progress" when source == "sender":
                        SendProgress?.Invoke(root.GetProperty("sent").GetInt64(), root.GetProperty("total").GetInt64());
                        break;
                    case "send-trace" when source == "sender":
                        if (root.GetProperty("phase").GetString() == "http-body-written" && root.GetProperty("endpoint").GetString() == "/Upload")
                            SendStatus?.Invoke("Завершение передачи на iPhone…");
                        break;
                    case "send-error" when source == "sender":
                        string? message = root.GetProperty("message").GetString();
                        if (!string.IsNullOrWhiteSpace(message)) senderError = new string(message.Where(c => !char.IsControl(c)).Take(300).ToArray());
                        break;
                    case "ask-expired" when source == "receiver":
                        AskExpired?.Invoke(root.GetProperty("id").GetString()!);
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { Log?.Invoke("Некорректное сообщение вспомогательного процесса пропущено."); }
        }
        var friendly = FriendlyLine(source, line);
        if (friendly != null) Log?.Invoke(friendly);
    }

    public static string? FriendlyLine(string source, string line)
    {
        if (source == "receiver" && line.StartsWith("{\"received\":")) {
            try {
                using var json = JsonDocument.Parse(line);
                return $"Получено: {json.RootElement.GetProperty("received").GetString()} ({json.RootElement.GetProperty("bytes").GetInt64():N0} байт).";
            } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return "Файл получен. Откройте «Полученные файлы»."; }
        }
        if (source == "receiver" && line.Contains(" принимает на [")) return "Готов к приёму файлов с iPhone.";
        if (source == "receiver" && line.Contains("POST /Ask") && line.Contains("200")) return "Входящая передача принята.";
        if (source == "receiver" && line.Contains("POST /Ask") && line.Contains("403")) return "Входящая передача отклонена.";
        if (line.StartsWith("Некорректная передача отклонена")) return "Входящая передача отклонена. Подробности в журналах.";
        if (source == "sender" && line.StartsWith("Запрос подтверждения")) return "Подтвердите передачу на принимающем устройстве.";
        if (source == "discovery" && line.StartsWith("Поиск завершён")) return line;
        if (source == "link" && line.StartsWith("channel match reverted ")) return "Канал 36 не помог; в этом сеансе адаптер вернулся на канал 149.";
        if (source == "link" && line.StartsWith("channel matched ")) {
            var to = line.Split(' ').ElementAtOrDefault(4);
            return to == "149" ? "Для поиска устройств адаптер вернулся на канал 149." : $"Адаптер переключён на канал {to}, совпадающий с Wi-Fi iPhone.";
        }
        if (line.Contains("state=Started error=Success")) return "Сигнал обнаружения Bluetooth передаётся.";
        if (line.Contains("error=ResourceInUse")) return "Объявление Bluetooth занято. Если iPhone не виден, запустите восстановление.";
        if (source == "recovery" && line.StartsWith("Восстановлено")) return "Служба подключённых устройств восстановлена.";
        if (source == "recovery" && line.StartsWith("Объявление AirDrop по Bluetooth запущено")) return line;
        if (source == "recovery" && line.StartsWith("Объявление по-прежнему заблокировано")) return "Блокировка сохраняется; перезапускаем Bluetooth.";
        if (line.StartsWith("Traceback")) return "Действие завершилось с ошибкой. Подробности в журналах.";
        return null;
    }

    Process Launch(string executable, IEnumerable<string> args, string source, bool critical = false, bool input = false)
    {
        var utf8 = new UTF8Encoding(false);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Base,
            StandardOutputEncoding = utf8, StandardErrorEncoding = utf8, RedirectStandardInput = input, StandardInputEncoding = input ? utf8 : null };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        int session = generation;
        process.OutputDataReceived += (_, e) => { if (e.Data != null) ProcessHelperLine(source, e.Data, session); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) ProcessHelperLine(source, e.Data, session); };
        process.Exited += (_, _) => { if (session == generation) Diagnostic("helper-exit", new { source, pid = process.Id, code = process.ExitCode }); };
        if (critical) process.Exited += (_, _) => { if (Running && session == generation) _ = StopAsync("Сеанс завершён. Проверьте журналы или запустите его снова."); };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        Diagnostic("helper-start", new { source, pid = process.Id, executable });
        lock (sync) owned.Add(process);
        return process;
    }

    internal void ProcessHelperLine(string source, string line, int ownerGeneration)
    {
        lock (sync) { if (ownerGeneration == generation) Line(source, line); }
    }

    string Engine(string file) => Path.Combine(Base, "engine", file);
    string Script(string file) => Path.Combine(Base, "airdrop", file);
    string[] Common() => ["--address", Address, "--ifindex", IfIndex.ToString(), "--sid", Sid];
    string[] Profile() => ["--name", Name, "--model", Model];
    // The app is elevated: ignore PYTHON* variables and the user site directory, which any user process can set.
    static readonly string[] PythonFlags = ["-E", "-s", "-u", "-X", "utf8"];

    // AirDrop identity shown to Apple devices. ReceiverModelName picks the device icon they draw.
    public static readonly (string Label, string Model)[] Models =
        [("MacBook", "MacBookPro18,3"), ("iMac", "iMac21,1"), ("Mac mini", "Macmini9,1"), ("Mac Studio", "Mac13,1")];
    public string Name { get; private set; } = "Этот компьютер";
    public string ActiveName { get; private set; } = "Этот компьютер"; // the name the running receiver advertises
    public string Model { get; private set; } = "MacBookPro18,3";
    // Unicast data (mostly TCP ACKs while receiving) goes out at 54 Mbit/s; at 12 Mbit/s the
    // ACKs took most of the phone's time on our channel. Compatible sending falls back to 12.
    public bool CompatibleSending { get; private set; }
    // Retune between 149 and 36 to follow where the phone actually spends its AWDL slots.
    public bool MatchChannel { get; private set; }
    static string ProfileFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulsar", "profile.json");
    // Profile saved before the rename from PulseDrop; read until the first save under the new name.
    static string LegacyPulseItProfileFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseIt", "profile.json");
    static string LegacyProfileFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseDrop", "profile.json");
    public static string CleanName(string name) => new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim() is var clean && clean.Length is >= 1 and <= 40 ? clean : throw new ArgumentException("Имя должно содержать от 1 до 40 символов.");
    public void SaveProfile(string name, string model)
    {
        if (!Models.Any(m => m.Model == model)) throw new ArgumentException("Неизвестный значок устройства.");
        Name = CleanName(name); Model = model;
        Directory.CreateDirectory(Path.GetDirectoryName(ProfileFile)!);
        File.WriteAllText(ProfileFile, JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = Name, ["model"] = Model, ["compatibleSending"] = CompatibleSending ? "true" : "false", ["matchChannel"] = MatchChannel ? "true" : "false" }));
        Changed?.Invoke();
    }

    public void Answer(string id, bool accept)
    {
        var json = JsonSerializer.Serialize(new { id, accept });
        lock (sync) try { if (receiver is { HasExited: false }) { receiver.StandardInput.WriteLine(json); receiver.StandardInput.Flush(); } } catch (IOException) { }
    }

    public void SetCompatibleSending(bool enabled)
    {
        if (Running || Working) throw new InvalidOperationException("Выключите приём перед изменением скорости адаптера.");
        bool previous = CompatibleSending;
        CompatibleSending = enabled;
        try { SaveProfile(Name, Model); }
        catch { CompatibleSending = previous; throw; }
    }

    public void SetMatchChannel(bool enabled)
    {
        if (Running || Working) throw new InvalidOperationException("Выключите приём перед изменением настроек адаптера.");
        bool previous = MatchChannel;
        MatchChannel = enabled;
        try { SaveProfile(Name, Model); }
        catch { MatchChannel = previous; throw; }
    }

    [SupportedOSPlatform("windows")]
    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (Running) return;
            Working = true; SetStatus("Проверка настройки…");
            cancel.Dispose(); cancel = new();
            lock (sync) { ++generation; candidates.Clear(); sessionWriteFailed = false; }
            LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulsar", "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(LogDirectory);
            LogDirectory = PhysicalDirectory(LogDirectory);
            StartupStage("настройка");
            foreach (var file in new[] { Python, Engine("awdl_link.exe"), Engine("devourer-bridge.exe"), Engine("bleprobe\\bleprobe.exe"), Script("service.py") })
                if (!File.Exists(file)) throw new IOException("Отсутствует компонент: " + file);
            if (Process.GetProcessesByName("awdl_link").Length > 0 || Process.GetProcessesByName("devourer-bridge").Length > 0)
                throw new IOException("Другой сеанс AWDL уже работает. Сначала остановите его.");
            var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.Name.Equals("awdl0", StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("Интерфейс awdl0 не найден. Сначала настройте TAP-адаптер.");
            (Address, Sid) = Identity(adapter.GetPhysicalAddress().GetAddressBytes());
            IfIndex = adapter.GetIPProperties().GetIPv6Properties()!.Index;
            Diagnostic("session", new { version = typeof(Session).Assembly.GetName().Version?.ToString(), Address, IfIndex, MatchChannel, CompatibleSending, python = Python });
            StartupStage("канал радиомоста");
            stopFile = Path.Combine(LogDirectory, "stop.request");
            string pipe = @"\\.\pipe\pulsar-" + Guid.NewGuid().ToString("N");
            ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            linkReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SetStatus("Запуск адаптера TP-Link…");
            bridge = Launch(Engine("devourer-bridge.exe"), [pipe], "radio", true);
            bool available = false;
            for (int i = 0; i < 60; i++)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (bridge.HasExited) throw new IOException("Не удалось запустить радиомост. Проверьте его журнал.");
                if (WaitNamedPipe(pipe, 50)) { available = true; break; }
                await Task.Delay(100, cancel.Token);
            }
            if (!available) throw new IOException("Радиомост не сообщил о готовности.");
            StartupStage("инициализация радио");
            link = Launch(Engine("awdl_link.exe"), [pipe, "149", "3600", "--fixed", .. (MatchChannel ? new[] { "--match-channel" } : []), "--data-rate", CompatibleSending ? "12" : "54", "--tap", "awdl0", "--probe", "--service-port", "8771", "--stop-file", stopFile], "link", true);
            await AwaitReadyAsync(linkReady.Task, link, startupStage, TimeSpan.FromSeconds(55), cancel.Token);
            StartupStage("IPv6-адрес TAP");
            bool addressReady = false;
            for (int i = 0; i < 200; i++)
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (link.HasExited) throw new IOException("Не удалось открыть USB-адаптер. Проверьте подключение TP-Link и драйвер WinUSB.");
                adapter = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.Name.Equals("awdl0", StringComparison.OrdinalIgnoreCase));
                if (adapter.OperationalStatus == OperationalStatus.Up && adapter.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString().Split('%')[0] == Address && a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred))
                { addressReady = true; break; }
                await Task.Delay(100, cancel.Token);
            }
            if (!addressReady) throw new IOException("Интерфейс awdl0 не получил IPv6-адрес.");
            SetStatus("Запуск приёмника…");
            StartupStage("HTTPS-приёмник");
            var receiver = this.receiver = Launch(Python, [.. PythonFlags, Script("service.py"), .. Common(), .. Profile(), "--seconds", "3600", "--ui-consent"], "receiver", true, input: true);
            await AwaitReadyAsync(ready.Task, receiver, startupStage, TimeSpan.FromSeconds(20), cancel.Token);
            if (receiver.HasExited || link.HasExited || bridge.HasExited) throw new IOException("Сеанс остановился во время запуска. Проверьте журналы.");
            Launch(Python, [.. PythonFlags, Script("announce.py"), .. Common(), "--seconds", "3600"], "announcer", true);
            StartupStage("ready");
            Running = true; Working = false;
            ActiveName = Name;
            SetStatus("Приём под именем " + Name);
            Log?.Invoke("На iPhone: Поделиться > AirDrop > " + Name + ". Подтверждайте входящие файлы на этом компьютере.");
        }
        catch (OperationCanceledException) { await CleanupAsync(); SetStatus("Запуск отменён"); throw; }
        catch (Exception error) {
            Diagnostic("startup-failed", new { stage = startupStage, error = error.ToString() });
            await CleanupAsync(); SetStatus("Ошибка запуска: " + startupStage);
            throw new IOException($"Ошибка запуска на этапе «{startupStage}»: {error.Message} Журналы: {LogDirectory}", error);
        }
        finally { Working = false; Changed?.Invoke(); gate.Release(); }
    }

    public async Task StopAsync(string status = "Stopped")
    {
        cancel.Cancel(); await gate.WaitAsync();
        try { await CleanupAsync(); SetStatus(status); }
        finally { gate.Release(); }
    }

    async Task CleanupAsync()
    {
        Running = false; Working = true; Changed?.Invoke();
        if (stopFile.Length > 0) await File.WriteAllTextAsync(stopFile, "Пульсар запросил завершение работы");
        foreach (var p in new[] { link, bridge })
            if (p != null && !p.HasExited) { try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); } catch (TimeoutException) { p.Kill(true); } }
        Process[] children; lock (sync) children = owned.ToArray();
        foreach (var p in children)
        {
            if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(); }
            p.WaitForExit(); p.Dispose();
        }
        if (activeScan != null) {
            try { await activeScan; } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException) { }
            activeScan = null;
        }
        Scanning = false;
        lock (sync) owned.Clear();
        advertising = false; publisherStatusLog = null;
        link = bridge = advertiser = receiver = null; stopFile = ""; Working = false;
    }

    internal async Task<int> ExitAsync(Process process, CancellationToken extra = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, extra);
        var token = linked.Token;
        try {
            await process.WaitForExitAsync(token);
            token.ThrowIfCancellationRequested();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (InvalidOperationException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        catch (OperationCanceledException) {
            try {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                process.WaitForExit(); // Drain output before another operation can reuse its UI/error state.
            } catch (InvalidOperationException) { }
            throw;
        }
    }

    public Task ScanAsync()
    {
        lock (sync) {
            if (activeScan is { IsCompleted: false }) return activeScan;
            return activeScan = ScanCoreAsync();
        }
    }

    async Task ScanCoreAsync()
    {
        if (!Running) throw new InvalidOperationException("Сначала включите приём.");
        Scanning = true; Changed?.Invoke();
        try {
        lock (sync) WriteCandidates();
        List<string> args = [.. PythonFlags, Script("desktop_discover.py"), .. Common(), "--seconds", "30", "--candidates-file", Path.Combine(LogDirectory, "candidates.json")];
        var process = Launch(Python, args, "discovery");
        if (await ExitAsync(process) != 0) throw new IOException("Ошибка поиска устройств. Проверьте журналы.");
        Diagnostic("scan-complete", new { generation });
        } finally { Scanning = false; Changed?.Invoke(); }
    }

    public bool Scanning { get; private set; }

    void WriteCandidates()
    {
        try { File.WriteAllText(Path.Combine(LogDirectory, "candidates.json"), JsonSerializer.Serialize(candidates.Take(32))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ReportWriteFailure(); }
    }

    internal static bool PublisherLogStarted(string path)
    {
        try {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            source.Seek(Math.Max(0, source.Length - 8192), SeekOrigin.Begin);
            using var reader = new StreamReader(source, Encoding.UTF8);
            string tail = reader.ReadToEnd();
            int state = tail.LastIndexOf("airdrop state=", StringComparison.Ordinal);
            return state >= 0 && tail.AsSpan(state).StartsWith("airdrop state=Started error=Success", StringComparison.Ordinal);
        } catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    async Task EnsureAdvertiserAsync()
    {
        await bluetoothGate.WaitAsync(cancel.Token);
        try {
            if (advertiser != null && !advertiser.HasExited && advertising) {
                if (publisherStatusLog == null || PublisherLogStarted(publisherStatusLog)) return;
                advertising = false;
                Diagnostic("advertising-lost", new { reason = "retained publisher no longer reports Started" });
            }
            publisherStatusLog = null;
            for (int attempt = 0; attempt < 2; attempt++) {
                if (advertiser != null && !advertiser.HasExited) { advertiser.Kill(true); await advertiser.WaitForExitAsync(); }
                advertising = false;
                bluetoothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                advertiser = Launch(Engine("bleprobe\\bleprobe.exe"), ["publish-airdrop", "3600"], "bluetooth");
                try { if (await bluetoothReady.Task.WaitAsync(TimeSpan.FromSeconds(5), cancel.Token)) return; }
                catch (TimeoutException) { }
                await Task.Delay(500, cancel.Token);
            }
            throw new BluetoothBusyException();
        } finally { bluetoothGate.Release(); }
    }
    public async Task WakeAsync()
    {
        if (!Running) throw new InvalidOperationException("Сначала включите приём.");
        await EnsureAdvertiserAsync();
        await ScanAsync();
    }

    public async Task SendAsync(Device device, string file, CancellationToken token = default)
    {
        if (!Running) throw new InvalidOperationException("Сначала включите приём.");
        Device.Validate(device.Name, device.Address, device.Port);
        if (!File.Exists(file) || new FileInfo(file).Length > 1024L * 1024 * 1024) throw new IOException("Выберите файл размером до 1 ГиБ.");
        await EnsureAdvertiserAsync();
        token.ThrowIfCancellationRequested();
        senderError = null;
        var process = Launch(Python, [.. PythonFlags, Script("send.py"), file, "--peer", device.Address, "--port", device.Port.ToString(), .. Common(), .. Profile()], "sender");
        if (await ExitAsync(process, token) != 0) throw new IOException(senderError ?? "Передача завершилась с ошибкой. Подробности в журналах.");
        Log?.Invoke("Файл принят устройством " + device.Name + ". Проверьте, открывается ли он на телефоне.");
    }

    public Task RecoverAsync()
    {
        if (!Running) throw new InvalidOperationException("Сначала включите приём.");
        return recovery = RecoverCoreAsync();
    }
    async Task RecoverCoreAsync()
    {
        if (advertiser != null && !advertiser.HasExited) { advertiser.Kill(true); await advertiser.WaitForExitAsync(); }
        advertiser = null;
        // Do not put this process in the kill-on-stop list: its finally block restores services.
        // A result file avoids inheritable stdout pipes being held open by the long-lived publisher.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Base, "RecoverBluetooth.ps1"), "-BleExe", Engine("bleprobe\\bleprobe.exe"), "-LogDirectory", LogDirectory }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch (TimeoutException)
        {
            // Its finally block restores services; a hang there needs the user to check them.
            process.Kill(true);
            throw new IOException("Восстановление Bluetooth не завершилось за 2 минуты. Проверьте recovery.log и службы Bluetooth и подключённых устройств.");
        }
        string log = Path.Combine(LogDirectory, "recovery.log");
        if (File.Exists(log)) foreach (var line in File.ReadAllLines(log)) {
            var friendly = FriendlyLine("recovery", line);
            if (friendly != null) Log?.Invoke(friendly);
        }
        if (process.ExitCode != 0) throw new IOException("Не удалось восстановить Bluetooth. Состояние восстановления служб указано в recovery.log.");
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(LogDirectory, "recovery-result.json")));
        if (!result.RootElement.GetProperty("services_restored").GetBoolean()) throw new IOException("Службы Bluetooth не восстановлены.");
        int pid = result.RootElement.GetProperty("publisher_pid").GetInt32();
        var child = Process.GetProcessById(pid);
        if (!string.Equals(child.MainModule?.FileName, Engine("bleprobe\\bleprobe.exe"), StringComparison.OrdinalIgnoreCase)) {
            child.Dispose();
            throw new IOException("Восстановление вернуло неожиданный процесс.");
        }
        bool adopted = false;
        try {
            string statusPath = result.RootElement.GetProperty("publisher_log").GetString()!;
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(statusPath)), Path.GetFullPath(LogDirectory), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(statusPath).StartsWith("recovery-bluetooth", StringComparison.Ordinal)
                || !PublisherLogStarted(statusPath)) throw new IOException("Процесс объявления Bluetooth больше не сообщает о запуске. Проверьте его журналы.");
            lock (sync) {
                if (Running) { advertiser = child; advertising = true; publisherStatusLog = statusPath; owned.Add(child); adopted = true; }
            }
        } finally {
            if (!adopted) {
                try { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
                finally { child.Dispose(); }
            }
        }
        if (Running) await ScanAsync();
    }
    public Task WaitForRecoveryAsync() => recovery ?? Task.CompletedTask;
}
