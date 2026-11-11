using System.Security.Principal;
using System.Diagnostics;
using System.Globalization;

namespace Pulsar;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        ApplicationConfiguration.Initialize();
        if (args.Contains("--configure-driver") || args.Contains("--restore-driver")) {
            var setup = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Setup-Devices.ps1"), "-Action", args.Contains("--restore-driver") ? "Restore" : "Configure", "-Root", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) }) setup.ArgumentList.Add(arg);
            using var process = Process.Start(setup)!; process.WaitForExit();
            MessageBox.Show(process.ExitCode == 0 ? "Настройка адаптера завершена." : "Не удалось настроить адаптер. Закройте Пульсар и проверьте C:\\ProgramData\\Pulsar\\setup.log.", "Настройка Пульсар");
            return process.ExitCode;
        }
        if (args.Contains("--self-test")) return Checks.Run();
        if (args.Contains("--recovery-test")) return Checks.Recovery().GetAwaiter().GetResult();
        if (args.Contains("--lifecycle-soak-test")) return Checks.Lifecycle(10).GetAwaiter().GetResult();
        if (args.Contains("--lifecycle-test")) return Checks.Lifecycle().GetAwaiter().GetResult();
        using var single = new Mutex(true, "Local\\Pulsar.Desktop", out bool first);
        if (!first && !args.Contains("--render")) { MessageBox.Show("Пульсар уже запущен.", "Пульсар"); return 0; }
        if (!args.Contains("--render") && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        { MessageBox.Show("Откройте Pulsar.exe и подтвердите запрос администратора Windows для работы с awdl0.", "Пульсар"); return 1; }
        using var form = new MainForm(preview: args.Contains("--render"));
        if (args.Contains("--render"))
        {
            form.Show(); Application.DoEvents();
            bool HasHistory(Control control) => control is RecentRow || control.Controls.Cast<Control>().Any(HasHistory);
            if (HasHistory(form)) throw new IOException("В общедоступных иллюстрациях не должно быть истории передач.");
            void Save(string name)
            {
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, name));
            }
            Save("preview.png");
            if (args.Contains("--demo"))
            {
                form.Demo(Device.Validate("iPhone 14 Pro Max", "fe80::1", 8770));
                Save("preview-devices.png");
                form.Demo(null);
                Save("preview-receive.png");
                form.DemoProgress(); Save("preview-progress.png");
                foreach (var (dialog, name) in new (Form, string)[] {
                    (new ConsentDialog(new Ask("demo", "Пример iPhone", ["Фото 1.HEIC", "Фото 2.HEIC"], 3_400_000)), "preview-consent.png"),
                    (new ProfileDialog(new Session()), "preview-profile.png") })
                {
                    dialog.StartPosition = FormStartPosition.CenterScreen; dialog.Show(); Application.DoEvents();
                    using var shot = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(shot, new Rectangle(Point.Empty, dialog.Size));
                    shot.Save(Path.Combine(AppContext.BaseDirectory, name)); dialog.Dispose();
                }
            }
            form.Close();
            for (int i = 0; i < 5; i++) Application.DoEvents();
            if (form.Visible) throw new IOException("Закрытие окна не завершилось.");
            return 0;
        }
        Application.Run(form);
        return 0;
    }
}
