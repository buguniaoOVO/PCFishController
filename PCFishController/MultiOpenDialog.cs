using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PCFishController;

/// <summary>
/// 多开管理：查看所有 PCFish PID，选择当前由助手控制的实例，
/// 并可选择一个现有 Sandboxie 沙盒启动另一份游戏。
/// 当前连接一次控制一个 PID；切换目标后重启助手，使 Frida worker 附加到新 PID。
/// </summary>
internal sealed class MultiOpenDialog : Form
{
    private readonly string _gameDir;
    private readonly ComboBox _instances = new();
    private readonly ComboBox _boxes = new();
    private readonly Label _status = new();
    private readonly int _currentPid;

    internal int SelectedPid { get; private set; }

    internal MultiOpenDialog(string gameDir, int currentPid)
    {
        _gameDir = GameDeploy.FindGameDir(gameDir) ?? gameDir ?? "";
        _currentPid = currentPid;
        SelectedPid = currentPid;

        Text = UiLanguage.T("多开管理");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 300);
        MinimumSize = new Size(600, 280);
        BackColor = Color.FromArgb(244, 247, 251);
        ForeColor = Color.FromArgb(26, 38, 56);
        Font = new Font("Microsoft YaHei UI", 9F);

        var title = new Label
        {
            Text = UiLanguage.T("PCFish 多开管理"),
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            Bounds = new Rectangle(20, 16, 400, 32)
        };
        var hint = new Label
        {
            Text = UiLanguage.T("助手按 PID 连接。当前版本一次控制一个实例；切换所控 PID 后重启助手生效。"),
            ForeColor = Color.FromArgb(98, 115, 137),
            Bounds = new Rectangle(22, 49, 610, 26)
        };
        var instanceLabel = new Label { Text = UiLanguage.T("运行中的实例"), Bounds = new Rectangle(22, 92, 110, 28) };
        _instances.Bounds = new Rectangle(138, 88, 360, 32);
        _instances.DropDownStyle = ComboBoxStyle.DropDownList;
        var refresh = new Button { Text = UiLanguage.T("刷新列表"), Bounds = new Rectangle(508, 88, 112, 32) };
        refresh.Click += (_, _) => RefreshInstances();

        var boxLabel = new Label { Text = UiLanguage.T("Sandboxie 沙盒"), Bounds = new Rectangle(22, 142, 110, 28) };
        _boxes.Bounds = new Rectangle(138, 138, 240, 32);
        _boxes.DropDownStyle = ComboBoxStyle.DropDownList;
        var launch = new Button { Text = UiLanguage.T("启动沙盒实例"), Bounds = new Rectangle(390, 138, 160, 32) };
        launch.Click += (_, _) => LaunchSandboxed();

        _status.Bounds = new Rectangle(22, 184, 600, 28);
        _status.ForeColor = Color.FromArgb(98, 115, 137);
        var cancel = new Button { Text = UiLanguage.T("取消"), DialogResult = DialogResult.Cancel, Bounds = new Rectangle(390, 232, 104, 36) };
        var select = new Button { Text = UiLanguage.T("控制此 PID"), DialogResult = DialogResult.OK, Bounds = new Rectangle(508, 232, 112, 36) };
        select.Click += (_, _) =>
        {
            if (_instances.SelectedItem is GameInstanceInfo info)
            {
                SelectedPid = info.Pid;
                _status.Text = UiLanguage.T($"已选中 PID {SelectedPid}；关闭弹窗后重启助手切换实例。");
            }
            else
            {
                SelectedPid = 0;
                _status.Text = UiLanguage.T("已设为自动选择第一个运行实例。");
            }
        };
        CancelButton = cancel;
        AcceptButton = select;
        Controls.AddRange(new Control[] { title, hint, instanceLabel, _instances, refresh, boxLabel, _boxes, launch, _status, cancel, select });
        LoadBoxes();
        RefreshInstances();
    }

    private void RefreshInstances()
    {
        var list = GameProcess.ListInstances();
        _instances.Items.Clear();
        foreach (var item in list) _instances.Items.Add(item);
        var index = list.ToList().FindIndex(x => x.Pid == _currentPid);
        if (index < 0 && list.Count > 0) index = 0;
        _instances.SelectedIndex = index;
        _status.Text = UiLanguage.T(list.Count == 0
            ? "当前没有运行中的 PCFish 实例。"
            : $"发现 {list.Count} 个实例。选中后点击「控制此 PID」。");
    }

    private void LoadBoxes()
    {
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sandboxie.ini");
        var names = new List<string>();
        if (File.Exists(ini))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(ini), @"(?m)^\[([^\]]+)\]\s*$"))
            {
                var name = match.Groups[1].Value.Trim();
                if (name.Length == 0 || name.Equals("GlobalSettings", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("UserSettings_", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
        }
        _boxes.Items.Clear();
        _boxes.Items.AddRange(names.Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        if (_boxes.Items.Count > 0) _boxes.SelectedIndex = 0;
        else
        {
            _boxes.Items.Add(UiLanguage.T("未检测到沙盒"));
            _boxes.SelectedIndex = 0;
            _boxes.Enabled = false;
        }
    }

    private void LaunchSandboxed()
    {
        if (_boxes.SelectedItem == null || !_boxes.Enabled) return;
        var game = Path.Combine(_gameDir, "PCFish.exe");
        if (!File.Exists(game))
        {
            _status.Text = UiLanguage.T("找不到 PCFish.exe，请在设置页检查游戏目录。");
            return;
        }
        var startExe = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus", "Start.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Sandboxie-Plus", "Start.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie", "Start.exe"),
        }.FirstOrDefault(File.Exists);
        if (startExe == null)
        {
            _status.Text = UiLanguage.T("找不到 Sandboxie Start.exe。");
            return;
        }
        try
        {
            var info = new ProcessStartInfo(startExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _gameDir
            };
            info.ArgumentList.Add("/box:" + _boxes.SelectedItem);
            info.ArgumentList.Add(game);
            Process.Start(info);
            _status.Text = UiLanguage.T("已请求 Sandboxie 启动游戏；登录完成后点「刷新列表」。");
        }
        catch (Exception ex)
        {
            _status.Text = UiLanguage.T("启动沙盒实例失败：") + ex.Message;
        }
    }
}
