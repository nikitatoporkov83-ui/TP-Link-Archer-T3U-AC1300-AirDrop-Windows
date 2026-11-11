// Can Windows do AirDrop's Bluetooth half? Built-in WinRT APIs only.
//
//   publish [seconds]  Broadcast Apple Continuity AirDrop (type 0x05) manufacturer
//                      data: the sender-side wake omdrop's ble-airdrop-adv.py sends
//                      (arm A, zero hashes = "no contact match", which Everyone mode
//                      accepts). A 0xFFFF (reserved test company) advert runs first
//                      as a control.
//   scan [seconds]     Listen for Apple manufacturer data and count Continuity TLV
//                      types. Open Share > AirDrop on a nearby iPhone to see 0x05.
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

const ushort Apple = 0x004C;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var mode = args.Length > 0 ? args[0] : "";
var seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
if (mode != "publish" && mode != "publish-airdrop" && mode != "scan" && mode != "radio-on" && mode != "radio-cycle") {
    Console.WriteLine("Использование: bleprobe publish|publish-airdrop|scan [секунды] либо radio-on|radio-cycle");
    return 2;
}

var adapter = await BluetoothAdapter.GetDefaultAsync();
if (adapter is null) {
    Console.WriteLine("АДАПТЕР BLUETOOTH НЕ НАЙДЕН");
    return 4;
}
var physicalRadio = await adapter.GetRadioAsync();
Console.WriteLine($"radio state={physicalRadio?.State}");
if (mode == "radio-on" || mode == "radio-cycle") {
    var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
    if (physicalRadio is null || access != Windows.Devices.Radios.RadioAccessStatus.Allowed) {
        Console.WriteLine($"radio access={access}; включите Bluetooth в параметрах Windows");
        return 1;
    }
    if (mode == "radio-cycle") {
        try {
            var off = await physicalRadio.SetStateAsync(Windows.Devices.Radios.RadioState.Off);
            Console.WriteLine($"radio disable={off}, state={physicalRadio.State}");
            if (off != Windows.Devices.Radios.RadioAccessStatus.Allowed) return 1;
            await Task.Delay(TimeSpan.FromSeconds(3));
        } finally {
            var restore = await physicalRadio.SetStateAsync(Windows.Devices.Radios.RadioState.On);
            Console.WriteLine($"radio restore={restore}, state={physicalRadio.State}");
        }
    }
    var result = await physicalRadio.SetStateAsync(Windows.Devices.Radios.RadioState.On);
    Console.WriteLine($"radio enable={result}, state={physicalRadio.State}");
    return result == Windows.Devices.Radios.RadioAccessStatus.Allowed ? 0 : 1;
}
Console.WriteLine($"adapter  LE={adapter.IsLowEnergySupported} peripheral={adapter.IsPeripheralRoleSupported} " +
                  $"central={adapter.IsCentralRoleSupported} offload={adapter.IsAdvertisementOffloadSupported} " +
                  $"extendedAdv={adapter.IsExtendedAdvertisingSupported} maxAdvLen={adapter.MaxAdvertisementDataLength}");

if (mode == "publish" || mode == "publish-airdrop") {
    // Continuity TLV: type 0x05, length 0x12, arm A body: 40 <tag 3> 00000000 03 <hashes 8> 00
    byte[] airdrop = [0x05, 0x12, 0x40, 0, 0, 0, 0, 0, 0, 0, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    bool? control = mode == "publish" ? await Publish(0xFFFF, [0x01, 0x02, 0x03], "control") : null;
    var apple = await Publish(Apple, airdrop, "airdrop");
    Console.WriteLine($"RESULT control={control?.ToString() ?? "skipped"} airdrop={apple}");
    Console.WriteLine("ПРИМЕЧАНИЕ: Started означает, что Windows приняла объявление. Передачу по радио " +
                      "проверяйте вторым сканером, например nRF Connect с фильтром компании 0x004C.");
    return apple ? 0 : 1;
}

// scan
var counts = new SortedDictionary<byte, int>();
var devices = new Dictionary<byte, HashSet<ulong>>();
var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
watcher.Received += (_, e) => {
    foreach (var md in e.Advertisement.ManufacturerData) {
        if (md.CompanyId != Apple)
            continue;
        var b = md.Data.ToArray();
        for (int i = 0; i + 1 < b.Length; i += 2 + b[i + 1]) { // Continuity TLVs: type, length, value
            lock (counts) {
                counts[b[i]] = counts.GetValueOrDefault(b[i]) + 1;
                if (!devices.TryGetValue(b[i], out var set))
                    devices[b[i]] = set = [];
                set.Add(e.BluetoothAddress);
            }
        }
    }
};
watcher.Start();
await Task.Delay(TimeSpan.FromSeconds(seconds));
var status = watcher.Status;
watcher.Stop();
Console.WriteLine($"scan     {seconds} s, watcher status {status}");
var names = new Dictionary<byte, string> {
    [0x05] = "AirDrop", [0x07] = "Сопряжение рядом", [0x09] = "Приёмник AirPlay", [0x0c] = "Handoff",
    [0x0f] = "Действие рядом", [0x10] = "Сведения рядом", [0x12] = "Find My",
};
if (counts.Count == 0)
    Console.WriteLine("Поиск: объявления Apple (0x004C) не обнаружены");
foreach (var (type, n) in counts)
    Console.WriteLine($"apple    type 0x{type:x2} {names.GetValueOrDefault(type, "?"),-18} adverts={n} devices={devices[type].Count}");
return 0;

async Task<bool> Publish(ushort company, byte[] data, string label) {
    var pub = new BluetoothLEAdvertisementPublisher();
    pub.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(company, data.AsBuffer()));
    var hex = Convert.ToHexString(data).ToLowerInvariant();
    var trail = new List<string>();
    BluetoothError error = BluetoothError.Success;
    pub.StatusChanged += (_, e) => {
        Console.WriteLine($"{label} state={e.Status} error={e.Error}");
        lock (trail) {
            trail.Add(e.Status.ToString());
            if (e.Error != BluetoothError.Success)
                error = e.Error;
        }
    };
    try {
        pub.Start();
    } catch (Exception ex) {
        Console.WriteLine($"{label,-8} company=0x{company:x4} data={hex}  ОШИБКА ЗАПУСКА: {ex.Message.Trim()}");
        return false;
    }
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    var final = pub.Status;
    pub.Stop(); // Also release a publisher waiting for a shared radio resource.
    lock (trail)
        Console.WriteLine($"{label,-8} company=0x{company:x4} data={hex}  status: {string.Join(" -> ", trail)}" +
                          (error != BluetoothError.Success ? $"  error={error}" : ""));
    return final == BluetoothLEAdvertisementPublisherStatus.Started;
}
