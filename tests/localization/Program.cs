using System.Globalization;
using System.Text.Json;

namespace Pulsar;

static class LocalizationChecks
{
    static int passed;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++;
    }

    static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        foreach (var (count, expected) in new[] { (0, "0 файлов"), (1, "1 файл"), (2, "2 файла"), (5, "5 файлов"), (11, "11 файлов"), (14, "14 файлов"), (21, "21 файл"), (22, "22 файла"), (111, "111 файлов") })
            Check(Russian.Files(count) == expected, "Неверное склонение: " + count);

        string config = Path.Combine(AppContext.BaseDirectory, "settings.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { PythonExe = "python.exe" }));
        var session = new Session();
        string? saved = null, status = null, failure = null;
        Device? discovered = null;
        session.Saved += path => saved = path;
        session.SendStatus += text => status = text;
        session.ReceiveFailed += text => failure = text;
        session.DeviceFound += device => discovered = device;

        // Проверяем реальные сообщения Python на границе между процессами.
        session.ProcessHelperLine("receiver", "Передача сохранена в C:\\Загрузки\\Пульсар\\Фото", session.Generation);
        Check(saved == "C:\\Загрузки\\Пульсар\\Фото", "Русский путь не дошёл до события сохранения");
        session.ProcessHelperLine("sender", "Подготовка файла на диске…", session.Generation);
        Check(status == "Подготовка файла…", "Не распознан русский этап подготовки");
        session.ProcessHelperLine("sender", "Ожидание телефона…", session.Generation);
        Check(status?.Contains("Подключение к iPhone") == true, "Не распознан русский этап ожидания");
        session.ProcessHelperLine("sender", "Запрос подтверждения на телефоне…", session.Generation);
        Check(status == "Подтвердите передачу на iPhone.", "Не распознан запрос подтверждения");
        session.ProcessHelperLine("receiver", "Не удалось сохранить передачу: OSError", session.Generation);
        Check(failure?.Contains("свободное место") == true, "Потеряно сообщение об ошибке сохранения");
        session.ProcessHelperLine("discovery", JsonSerializer.Serialize(new { @event = "device", name = "Тестовый iPhone", address = "fe80::123", port = 8770 }), session.Generation);
        Check(discovered?.Name == "Тестовый iPhone", "Повреждено имя устройства в JSON");
        Check(Session.FriendlyLine("receiver", "{\"received\":\"Фото.jpg\",\"bytes\":123}") == "Получено: Фото.jpg (123 байт).", "Неверное русское сообщение о файле");
        Check(Session.FriendlyLine("link", "channel matched 149 -> 36 (peer slots: 149=2 36=6, peers=1)")?.Contains("канал 36") == true, "Изменён машинный формат радиопротокола");
        Check(Session.FriendlyLine("recovery", "Объявление AirDrop по Bluetooth запущено.") != null, "Не распознано сообщение восстановления");
        File.Delete(config);
        Console.WriteLine($"Успешно: {passed} проверок локализации и обмена сообщениями.");
    }
}
