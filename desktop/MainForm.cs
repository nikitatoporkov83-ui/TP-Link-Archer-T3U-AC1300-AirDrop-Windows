using System.Diagnostics;

namespace Pulsar;

sealed class MainForm : Form
{
    readonly Session session;
    readonly Label adapter;
    readonly Label stateText = new(), sendHint = new(), emptyText = new(), recvTitle = new(), recvDetail = new(), steps = new(), noRecent = new();
    readonly Dot dot = new();
    readonly AppIcon radar = new() { Corner = 32 };
    readonly Switch toggle = new();
    readonly Bar bar = new() { Visible = false };
    readonly System.Windows.Forms.Timer flashTimer = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer stallTimer = new() { Interval = 5000 };
    DateTime lastProgress;
    string? incoming, flash;
    long inReceived, inTotal;
    readonly Pill sendTab = new(), receiveTab = new(), turnOn = new(), showAll = new();
    readonly IconButton refresh = new(), edit = new(), gear = new(), minimize = new(), close = new(), help = new();
    readonly DropZone drop = new();
    readonly Panel sendPage = new(), receivePage = new(), list = new(), empty = new(), recent = new();
    readonly TextBox activity = new();
    readonly ActivityWindow activityWindow;
    readonly ContextMenuStrip menu = new() { Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) };
    readonly ToolStripMenuItem recoverItem = new("Восстановить Bluetooth…"), stopItem = new("Выключить приём");
    readonly ToolStripMenuItem fasterItem = new("Совместимая отправка (12 Мбит/с)") { CheckOnClick = true };
    readonly ToolStripMenuItem matchItem = new("Использовать канал Wi-Fi iPhone") { CheckOnClick = true };
    static readonly TimeSpan LostGrace = TimeSpan.FromSeconds(60);
    readonly List<DeviceRow> rows = [];
    readonly HashSet<string> known = [];
    ConsentDialog? consent;
    CancellationTokenSource? sendCancel;
    DeviceRow? sendingRow;
    string? selectedFile;
    DateTime endsAt;
    bool busy, closing, closed, wasRunning, demo;

    public MainForm(bool preview = false)
    {
        demo = preview;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Пульсар"; FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(480, 720);
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("Pulsar.Icon")!) Icon = new Icon(iconStream);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.UI(10); DoubleBuffered = true;

        // Header and window chrome.
        var icon = new AppIcon { Bounds = new Rectangle(24, 20, 56, 56) };
        var title = new Label { Text = "Пульсар", Font = new Font("Segoe UI", 16, FontStyle.Bold), Bounds = new Rectangle(94, 18, 220, 34) };
        dot.Bounds = new Rectangle(96, 59, 8, 8);
        stateText.Bounds = new Rectangle(108, 52, 210, 22); stateText.Font = Theme.UI(9.5f); stateText.ForeColor = Theme.Secondary; stateText.AutoEllipsis = true;
        Glyph(edit, "", "Изменить имя и значок AirDrop", new Rectangle(320, 26, 34, 34));
        Glyph(gear, "", "Настройки", new Rectangle(358, 26, 34, 34));
        Glyph(minimize, "", "Свернуть", new Rectangle(396, 26, 34, 34));
        Glyph(close, "", "Закрыть", new Rectangle(430, 26, 34, 34));
        minimize.Font = close.Font = Theme.Icons(10);
        Controls.AddRange([icon, title, dot, stateText, edit, gear, minimize, close]);
        foreach (var c in new Control[] { this, icon, title, stateText }) Theme.Drag(this, c);

        var tabs = new Surface { Bounds = new Rectangle(24, 96, 432, 54), Radius = 16 };
        Tab(sendTab, "Отправить", "", new Rectangle(4, 4, 212, 46)); Tab(receiveTab, "Получить", "", new Rectangle(216, 4, 212, 46));
        tabs.Controls.AddRange([sendTab, receiveTab]); Controls.Add(tabs);

        // Send page.
        sendPage.Bounds = receivePage.Bounds = new Rectangle(0, 166, 480, 490);
        drop.Bounds = new Rectangle(24, 0, 432, 172); drop.AccessibleName = "Выбрать файл для отправки";
        ResetDrop();
        var nearby = new Label { Text = "Устройства Apple рядом", Font = Theme.Semibold(11), Bounds = new Rectangle(28, 190, 300, 26), TextAlign = ContentAlignment.MiddleLeft };
        Glyph(refresh, "", "Найти устройства рядом", new Rectangle(422, 186, 34, 34)); refresh.Ink = Theme.Blue;
        list.Bounds = empty.Bounds = new Rectangle(24, 226, 432, 236); list.AutoScroll = true; Theme.DarkScroll(list);
        emptyText.Bounds = new Rectangle(16, 34, 400, 64); emptyText.TextAlign = ContentAlignment.MiddleCenter; emptyText.ForeColor = Theme.Secondary; emptyText.Font = Theme.UI(9.5f);
        turnOn.Text = "Включить AirDrop"; turnOn.Bounds = new Rectangle(126, 112, 180, 44);
        empty.Controls.AddRange([emptyText, turnOn]);
        sendHint.Bounds = new Rectangle(28, 464, 424, 24); sendHint.ForeColor = Theme.Secondary; sendHint.Font = Theme.UI(9); sendHint.AutoEllipsis = true;
        sendPage.Controls.AddRange([drop, nearby, refresh, empty, list, sendHint]);

        // Receive page.
        var card = new Surface { Bounds = new Rectangle(24, 0, 432, 112), Radius = 18 };
        radar.Bounds = new Rectangle(18, 22, 64, 64);
        recvTitle.Bounds = new Rectangle(98, 22, 250, 30); recvTitle.Font = Theme.Semibold(14);
        recvDetail.Bounds = new Rectangle(98, 52, 250, 40); recvDetail.ForeColor = Theme.Secondary; recvDetail.Font = Theme.UI(9.5f);
        toggle.Bounds = new Rectangle(360, 40, 54, 32); toggle.AccessibleName = "Приём файлов";
        bar.Bounds = new Rectangle(98, 84, 316, 6);
        card.Controls.AddRange([radar, recvTitle, recvDetail, toggle, bar]);
        steps.Bounds = new Rectangle(28, 120, 424, 44); steps.ForeColor = Theme.Secondary; steps.Font = Theme.UI(9.5f);
        var recentTitle = new Label { Text = "Недавние файлы", Font = Theme.Semibold(11), Bounds = new Rectangle(28, 176, 200, 26), TextAlign = ContentAlignment.MiddleLeft };
        showAll.Text = "Все файлы"; showAll.Bounds = new Rectangle(366, 174, 90, 30); showAll.Fill = Theme.Raised; showAll.Ink = Theme.Text; showAll.Radius = 10; showAll.Font = Theme.UI(9);
        recent.Bounds = new Rectangle(24, 212, 432, 272);
        noRecent.Text = "Здесь появятся полученные файлы."; noRecent.Bounds = new Rectangle(0, 40, 432, 40); noRecent.TextAlign = ContentAlignment.MiddleCenter; noRecent.ForeColor = Theme.Secondary; noRecent.Font = Theme.UI(9.5f);
        recent.Controls.Add(noRecent);
        receivePage.Controls.AddRange([card, steps, recentTitle, showAll, recent]);
        Controls.AddRange([sendPage, receivePage]);

        // Footer.
        var rule = new Panel { Bounds = new Rectangle(24, 664, 432, 1), BackColor = Color.FromArgb(38, 45, 60) };
        var antenna = new Label { Text = "", Font = Theme.Icons(12), ForeColor = Theme.Secondary, Bounds = new Rectangle(24, 676, 28, 32), TextAlign = ContentAlignment.MiddleCenter };
        adapter = new Label { Text = "Адаптер TP-Link Archer T3U", Font = Theme.UI(9.5f), ForeColor = Theme.Secondary, Bounds = new Rectangle(56, 676, 300, 32), TextAlign = ContentAlignment.MiddleLeft };
        Glyph(help, "", "Справка", new Rectangle(422, 675, 34, 34));
        Controls.AddRange([rule, antenna, adapter, help]);

        var profileItem = new ToolStripMenuItem("Имя и значок…"); var openFiles = new ToolStripMenuItem("Полученные файлы");
        var exportLogs = new ToolStripMenuItem("Экспорт диагностики…"); var openLogs = new ToolStripMenuItem("Открыть журналы"); var activityItem = new ToolStripMenuItem("Журнал событий");
        menu.Items.AddRange([profileItem, new ToolStripSeparator(), openFiles, activityItem, openLogs, exportLogs, new ToolStripSeparator(), matchItem, fasterItem, recoverItem, stopItem]);
        foreach (ToolStripItem item in menu.Items) item.ForeColor = Theme.Text;

        activity.Multiline = true; activity.ReadOnly = true; activity.ScrollBars = ScrollBars.Vertical; activity.BorderStyle = BorderStyle.None;
        activity.ForeColor = Theme.Text; activity.Font = Theme.UI(9); activity.AccessibleName = "События передачи файлов"; Theme.DarkScroll(activity);
        activityWindow = new ActivityWindow(activity);
        ResumeLayout();

        try { session = new Session(); }
        catch (Exception e) { throw new IOException("Настройка Пульсар не завершена. Сначала выполните tools/package-desktop.ps1.", e); }
        fasterItem.Checked = session.CompatibleSending;
        fasterItem.Click += (_, _) => {
            try {
                session.SetCompatibleSending(fasterItem.Checked);
                sendHint.Text = fasterItem.Checked ? "Включена совместимая отправка (12 Мбит/с). Скорость снижена." : "Включена обычная скорость отправки (54 Мбит/с).";
            } catch (Exception e) { fasterItem.Checked = session.CompatibleSending; MessageBox.Show(this, e.Message, "Пульсар"); }
        };
        matchItem.Checked = session.MatchChannel;
        matchItem.Click += (_, _) => {
            try {
                session.SetMatchChannel(matchItem.Checked);
                sendHint.Text = matchItem.Checked ? "Адаптер будет следовать за iPhone между каналами 149 и 36." : "Адаптер будет оставаться на канале 149.";
            } catch (Exception e) { matchItem.Checked = session.MatchChannel; MessageBox.Show(this, e.Message, "Пульсар"); }
        };
        session.Log += line => UI(() => Append(line)); session.Changed += () => UI(UpdateControls);
        session.DeviceFound += device => {
            int owner = session.Generation;
            UI(() => { if (session.Running && session.Generation == owner) AddDevice(device); });
        };
        session.DeviceRemoved += address => {
            int owner = session.Generation;
            UI(() => { if (session.Generation == owner) RemoveDevice(address); });
        };
        session.SendProgress += (sent, total) => UI(() => { if (sendingRow != null) sendHint.Text = $"Отправка на {sendingRow.Device.Name} · {Bytes(sent)} из {Bytes(total)}"; });
        session.SendStatus += text => UI(() => { if (sendingRow != null) sendHint.Text = text; });
        session.AskReceived += ask => UI(() => ShowAsk(ask));
        session.AskExpired += id => UI(() => { if (consent?.Id == id) consent.Close(); });
        session.Saved += _ => UI(() => { Chime.Play(); Finish("Получено: " + (incoming ?? "файл")); RefreshRecent(); });
        session.ReceiveFailed += reason => UI(() => { if (incoming != null) Finish(reason); });
        session.Progress += (name, received, total) => UI(() => { lastProgress = DateTime.Now; incoming = name; inReceived = received; inTotal = total; flash = null; UpdateControls(); });
        flashTimer.Tick += (_, _) => { flashTimer.Stop(); flash = null; UpdateControls(); };
        // The sender may cancel after approval without telling us; the receiver gives up after 120 s.
        stallTimer.Tick += (_, _) => {
            if (incoming != null && !demo && DateTime.Now - lastProgress > TimeSpan.FromSeconds(130)) Finish("Передача не завершена");
            foreach (var row in rows.Where(r => r != sendingRow && DateTime.Now - r.LostAt > LostGrace).ToArray()) DropRow(row);
        };
        stallTimer.Start();

        sendTab.Click += (_, _) => ShowTab(true); receiveTab.Click += (_, _) => ShowTab(false);
        gear.Click += (_, _) => menu.Show(gear, new Point(0, gear.Height));
        edit.Click += (_, _) => EditProfile(); profileItem.Click += (_, _) => EditProfile();
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        close.Click += (_, _) => Close();
        help.Click += (_, _) => MessageBox.Show(this,
            "Получение: включите приём. На iPhone выберите Поделиться → AirDrop → " + session.Name + ", затем подтвердите запрос на этом компьютере.\n\n" +
            "Отправка: разблокируйте iPhone, включите AirDrop «Для всех на 10 минут», обновите список и нажмите «Отправить» рядом с устройством. Один файл до 1 ГиБ.\n\n" +
            "Если устройство не появилось, попробуйте восстановление Bluetooth в настройках.", "Справка Пульсар");
        toggle.Click += async (_, _) => await Toggle();
        turnOn.Click += async (_, _) => await Toggle();
        refresh.Click += async (_, _) => await Work(async () => { ClearDevices(); sendHint.Text = "Поиск устройств рядом…"; await session.WakeAsync(); if (sendCancel == null) sendHint.Text = rows.Count > 0 ? "Нажмите «Отправить» рядом с устройством." : "Устройств пока нет. Обновите режим «Для всех» на iPhone или восстановите Bluetooth."; });
        drop.Click += (_, _) => Choose();
        drop.FilesDropped += paths =>
        {
            var file = paths.FirstOrDefault(File.Exists);
            if (file == null) { sendHint.Text = "Перетащите отдельный файл вместо папки."; return; }
            SetFile(file);
            if (paths.Length > 1) sendHint.Text = "Можно отправить один файл. Выбран: " + Path.GetFileName(file) + ".";
        };
        showAll.Click += (_, _) => OpenDownloads(); openFiles.Click += (_, _) => OpenDownloads();
        openLogs.Click += (_, _) => { if (session.LogDirectory.Length > 0) Open(session.LogDirectory); else Append("Включите приём, чтобы создать журнал сеанса."); };
        exportLogs.Click += (_, _) => ExportLogs();
        activityItem.Click += (_, _) => { if (!activityWindow.Visible) activityWindow.Show(this); activityWindow.Activate(); };
        stopItem.Click += async (_, _) => await Toggle();
        recoverItem.Click += async (_, _) => await Work(async () => { await RecoverPrompt(); });
        FormClosing += async (_, e) =>
        {
            if (closed) return;
            e.Cancel = true;
            if (closing) return;
            if (e.CloseReason == CloseReason.UserClosing && !ConfirmInterrupt("Закрытие Пульсар")) return;
            closing = true; Enabled = false;
            await Task.Yield();
            try { sendCancel?.Cancel(); await session.StopAsync(); await session.WaitForRecoveryAsync(); await session.StopAsync(); }
            catch (Exception error) { MessageBox.Show(error.Message, "Завершение работы"); }
            closed = true; Close();
        };
        Append("Подключите поддерживаемый адаптер TP-Link и включите приём. Входящие файлы требуют вашего подтверждения.");
        ShowTab(true);
        RefreshRecent();
        UpdateControls();
    }

    protected override CreateParams CreateParams
    {
        // WS_MINIMIZEBOX lets the taskbar button minimize/restore the borderless window.
        get { var p = base.CreateParams; p.Style |= 0x20000; return p; }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.Chrome(this); }

    static void Glyph(IconButton button, string glyph, string name, Rectangle bounds) { button.Glyph = glyph; button.AccessibleName = name; button.Bounds = bounds; }
    static void Tab(Pill tab, string text, string glyph, Rectangle bounds) { tab.Text = text; tab.Glyph = glyph; tab.Bounds = bounds; tab.Radius = 13; tab.Font = Theme.UI(11); }

    void ShowTab(bool send)
    {
        sendPage.Visible = send; receivePage.Visible = !send;
        foreach (var (tab, on) in new[] { (sendTab, send), (receiveTab, !send) })
        { tab.Fill = on ? Theme.Blue : Theme.Surface; tab.Ink = on ? Color.White : Theme.Secondary; tab.Invalidate(); }
    }
    void ShowAsk(Ask ask)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate(); Chime.Play();
        bool accept;
        using (var dialog = consent = new ConsentDialog(ask)) accept = dialog.ShowDialog(this) == DialogResult.OK;
        consent = null;
        session.Answer(ask.Id, accept);
        if (accept) { lastProgress = DateTime.Now; incoming = ask.Files.Length == 1 ? ask.Files[0] : Russian.Files(ask.Files.Length); inReceived = 0; inTotal = ask.Bytes; flash = null; ShowTab(false); UpdateControls(); }
        Append((accept ? "Принято: " : "Отклонено: ") + (ask.Files.Length == 1 ? ask.Files[0] : Russian.Files(ask.Files.Length)) + " от " + ask.Sender + ".");
    }
    void Finish(string message) { incoming = null; flash = message; flashTimer.Stop(); flashTimer.Start(); UpdateControls(); }
    static string Bytes(long bytes) => bytes < 1 << 20 ? $"{bytes / 1024.0:0.#} КБ" : $"{bytes / 1048576.0:0.#} МБ";
    void EditProfile()
    {
        using var dialog = new ProfileDialog(session);
        if (dialog.ShowDialog(this) == DialogResult.OK) Append("Новое имя AirDrop: " + session.Name + (session.Running ? ". Выключите и снова включите приём, чтобы применить его." : "."));
    }
    void RefreshRecent()
    {
        foreach (var row in recent.Controls.OfType<RecentRow>().ToArray()) row.Dispose();
        if (demo) { noRecent.Visible = true; return; }
        var files = Directory.Exists(session.Downloads)
            ? new DirectoryInfo(session.Downloads).EnumerateDirectories().Where(d => !d.Name.StartsWith(".incoming-"))
                .SelectMany(d => d.EnumerateFiles()).OrderByDescending(f => f.LastWriteTimeUtc).Take(4).ToArray()
            : [];
        float k = this.K();
        for (int i = 0; i < files.Length; i++)
        {
            var row = new RecentRow(files[i]) { Location = new Point(0, i * 68) };
            row.Scale(new SizeF(k, k)); // created after the form's DPI scaling pass
            recent.Controls.Add(row);
        }
        noRecent.Visible = files.Length == 0;
    }
    void ResetDrop() { drop.Glyph = ""; drop.GlyphColor = Theme.Secondary; drop.Title = "Перетащите файл сюда"; drop.Sub = "или нажмите для выбора · до 1 ГиБ"; drop.Invalidate(); }
    void Choose()
    {
        using var dialog = new OpenFileDialog { Title = "Отправка файла (до 1 ГиБ)", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) SetFile(dialog.FileName);
    }
    void SetFile(string path)
    {
        selectedFile = path;
        long bytes = new FileInfo(path).Length;
        drop.GlyphColor = Theme.Blue; drop.Title = Path.GetFileName(path);
        drop.Sub = (bytes < 1 << 20 ? $"{bytes / 1024.0:0.#} КБ" : $"{bytes / 1048576.0:0.#} МБ") + " · нажмите для замены";
        drop.Invalidate(); UpdateControls();
    }
    async Task Toggle()
    {
        if (session.Running && !ConfirmInterrupt("Выключение приёма")) return;
        await Work(ToggleCore);
    }
    async Task ToggleCore()
    {
        bool stopping = session.Running;
        sendCancel?.Cancel();
        ClearDevices();
        if (stopping) await session.StopAsync(); else await session.StartAsync();
    }
    // Sending runs outside Work so the rest of the window stays usable; the row's button becomes Cancel.
    async Task SendTo(DeviceRow row)
    {
        if (sendCancel != null) { if (row == sendingRow) sendCancel.Cancel(); return; }
        if (selectedFile == null) { Choose(); if (selectedFile == null) return; }
        string file = selectedFile, name = row.Device.Name;
        sendCancel = new CancellationTokenSource(); sendingRow = row; UpdateControls();
        try
        {
            if (row.LostAt != null || DateTime.UtcNow - row.VerifiedAt > TimeSpan.FromSeconds(10))
            {
                var fresh = await Reacquire(row, sendCancel.Token);
                if (fresh == null) { sendHint.Text = "Не удалось связаться с " + name + ". Разблокируйте устройство, включите AirDrop «Для всех на 10 минут» и повторите."; return; }
                sendingRow = row = fresh; UpdateControls();
            }
            sendHint.Text = "Ожидаем, пока " + name + " примет " + Path.GetFileName(file) + "…";
            await session.SendAsync(row.Device, file, sendCancel.Token);
            sendHint.Text = "Устройство " + name + " приняло " + Path.GetFileName(file) + ".";
            Chime.Play();
        }
        catch (BluetoothBusyException) { await RecoverPrompt(); }
        catch (OperationCanceledException) { sendHint.Text = "Отправка отменена."; Append("Отправка на " + name + " отменена."); }
        catch (Exception e) { sendHint.Text = e.Message; Append(e.Message); }
        finally { sendCancel.Dispose(); sendCancel = null; sendingRow = null; if (!IsDisposed) UpdateControls(); }
    }
    void ExportLogs()
    {
        if (session.LogDirectory.Length == 0) { Append("Включите приём, чтобы собрать диагностику сеанса."); return; }
        using var dialog = new SaveFileDialog { Title = "Экспорт диагностических журналов", Filter = "Архив диагностики (*.zip)|*.zip",
            FileName = "Pulsar-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { DiagnosticExport.Write(session.LogDirectory, dialog.FileName); Append("Диагностика сохранена: " + dialog.FileName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Не удалось экспортировать диагностику. Выберите другое имя файла.\n\n" + e.Message, "Пульсар"); }
    }

    void OpenDownloads() { Directory.CreateDirectory(session.Downloads); Open(session.Downloads); }

    void UI(Action action) { if (IsDisposed || Disposing) return; if (InvokeRequired) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } else action(); }
    void Append(string text) { if (activity.TextLength > 24000) activity.Text = activity.Text[^12000..]; activity.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); }
    void UpdateControls()
    {
        bool running = session.Running, working = session.Working;
        adapter.Text = $"T3U · {(session.MatchChannel ? "Авто" : "149")} · {(session.CompatibleSending ? 12 : 54)} Мбит/с PHY";
        fasterItem.Enabled = matchItem.Enabled = !running && !working && !busy;
        if (running && !wasRunning) endsAt = DateTime.Now.AddHours(1);
        wasRunning = running;
        dot.Color = running ? Theme.Green : working ? Theme.Orange : Theme.Off; dot.Invalidate();
        string shown = running ? session.ActiveName : session.Name;
        stateText.Text = running ? "Видно как " + shown : session.Status;
        radar.On = running; radar.Invalidate();
        recvTitle.Text = running ? "Приём включён" : working ? session.Status : "Приём выключен";
        recvDetail.Text = running ? $"Видно как {shown} до {endsAt:HH:mm}" : "Включите приём AirDrop и подключите адаптер TP-Link.";
        if (!running && !demo) incoming = null;
        bar.Visible = incoming != null; recvDetail.Height = (int)((incoming != null ? 26 : 40) * this.K());
        if (incoming != null)
        {
            // Received bytes are the compressed archive, so hold at 99% until the file is saved.
            double done = inTotal > 0 ? Math.Min(0.99, (double)inReceived / inTotal) : 0;
            recvTitle.Text = "Получение…";
            recvDetail.Text = $"{incoming} · {Bytes(Math.Min(inReceived, inTotal))} из {Bytes(inTotal)}";
            bar.Value = done; bar.Invalidate();
            stateText.Text = $"Получение: {incoming} · {done:P0}"; dot.Color = Theme.Blue;
        }
        else if (flash != null && running) recvTitle.Text = flash;
        steps.Text = "На iPhone: Поделиться → AirDrop → " + shown + ", затем подтвердите запрос здесь. Если компьютера нет в списке, включите на iPhone «Для всех на 10 минут».";
        toggle.On = running; toggle.Enabled = stopItem.Enabled = running ? !working : !busy && !working; toggle.Invalidate();
        stopItem.Visible = running;
        turnOn.Enabled = !busy && !working; turnOn.Visible = !running;
        refresh.Enabled = recoverItem.Enabled = !busy && running && sendCancel == null;
        empty.Visible = rows.Count == 0; list.Visible = rows.Count > 0;
        emptyText.Text = running ? "Поиск устройств…\nРазблокируйте iPhone, включите «Для всех на 10 минут» и обновите список."
            : "AirDrop выключен.\nВключите его, чтобы найти устройства Apple рядом.";
        for (int i = 0; i < rows.Count; i++)
        {
            var send = rows[i].Send; bool mine = rows[i] == sendingRow;
            send.Text = mine ? "Отмена" : "Отправить";
            // Let a verified device accept immediately while the remaining scan continues.
            send.Enabled = running && (mine || ((!busy || session.Scanning) && sendingRow == null));
            send.Fill = mine ? Color.FromArgb(64, 32, 38) : i == 0 && selectedFile != null && sendingRow == null ? Theme.Blue : Theme.Raised;
            send.Ink = mine ? Theme.Red : Theme.Text; send.Invalidate();
        }
        foreach (Control c in new Control[] { turnOn, refresh }) c.Invalidate();
    }
    async Task<bool> RecoverPrompt()
    {
        if (MessageBox.Show(this, "Windows заблокировала сигнал AirDrop по Bluetooth. Восстановить его?\n\nСначала будут временно приостановлены службы подключённых устройств Windows. Если этого окажется недостаточно, Bluetooth выключится и включится; аксессуары ненадолго потеряют связь. Затем исходные состояния служб будут восстановлены.", "Восстановление Bluetooth", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        try {
            sendHint.Text = "Восстановление Bluetooth и служб…";
            await session.RecoverAsync();
            if (sendCancel == null) sendHint.Text = rows.Count > 0 ? "Нажмите «Отправить» рядом с устройством." : "Восстановление завершено. Обновите режим «Для всех» на iPhone.";
            return true;
        } catch (Exception e) { Append(e.Message); MessageBox.Show(this, e.Message, "Пульсар"); return false; }
    }
    async Task Work(Func<Task> action)
    {
        busy = true; UpdateControls();
        try { await action(); }
        catch (BluetoothBusyException) { await RecoverPrompt(); }
        catch (OperationCanceledException) { Append("Действие отменено."); }
        catch (Exception e) { Append(e.Message); MessageBox.Show(this, e.Message, "Пульсар", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { busy = false; if (!IsDisposed) UpdateControls(); }
    }
    void AddDevice(Device device)
    {
        if (!known.Add(device.Address + ":" + device.Port))
        {
            foreach (var r in rows.Where(r => r.Device.Address == device.Address && r.Device.Port == device.Port)) r.Verified();
            return;
        }
        var row = new DeviceRow(device) { Location = new Point(0, rows.Count * 80) };
        row.Scale(new SizeF(this.K(), this.K())); // created after the form's DPI scaling pass
        row.Send.Click += async (_, _) => await SendTo(row);
        rows.Add(row); list.Controls.Add(row);
        Append("Найдено: " + device.Name);
        UpdateControls();
    }
    // The radio forgets an idle phone within seconds; keep its row (marked) so Send can wake it.
    void RemoveDevice(string address)
    {
        foreach (var row in rows.Where(row => row.Device.Address == address)) row.SetLost(true);
    }
    void DropRow(DeviceRow row)
    {
        known.Remove(row.Device.Address + ":" + row.Device.Port);
        list.Controls.Remove(row); rows.Remove(row); row.Dispose();
        for (int i = 0; i < rows.Count; i++) rows[i].Top = (int)(i * 80 * this.K());
        UpdateControls();
    }
    // Wake an out-of-range phone and wait for it to answer again; returns the row to send to, or null.
    async Task<DeviceRow?> Reacquire(DeviceRow row, CancellationToken token)
    {
        string name = row.Device.Name;
        sendHint.Text = "Подключение к " + name + "… оставьте устройство разблокированным с режимом AirDrop «Для всех».";
        if (session.Scanning) await session.ScanAsync().WaitAsync(token);
        var requested = DateTime.UtcNow;
        Task scan = session.WakeAsync();
        DeviceRow? Fresh() => rows.FirstOrDefault(r => r.VerifiedAt >= requested && r.Device.Address == row.Device.Address && r.Device.Port == row.Device.Port);
        while (!scan.IsCompleted && Fresh() == null) await Task.WhenAny(scan, Task.Delay(250, token));
        token.ThrowIfCancellationRequested();
        if (scan.IsFaulted) await scan; // surfaces BluetoothBusyException for the recovery prompt
        return Fresh();
    }
    bool Transferring => !demo && (incoming != null || sendCancel != null);
    bool ConfirmInterrupt(string action) => !Transferring || MessageBox.Show(this,
        $"Сейчас идёт передача файла. Действие «{action}» прервёт её.\n\nПродолжить?", "Пульсар", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    void ClearDevices()
    {
        known.Clear();
        foreach (var row in rows) row.Dispose();
        rows.Clear(); UpdateControls();
    }
    // Preview-only: populate a device row, or switch to the Receive tab.
    internal void Demo(Device? device) { if (device != null) AddDevice(device); else ShowTab(false); }
    internal void DemoProgress() { demo = true; incoming = "Пример видео.mp4"; inReceived = 3_100_000; inTotal = 6_846_469; UpdateControls(); }
    internal void DemoAsk() => BeginInvoke(() => ShowAsk(new Ask("demo", "Пример iPhone", ["Фото 1.HEIC", "Фото 2.HEIC"], 3_400_000)));
    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
