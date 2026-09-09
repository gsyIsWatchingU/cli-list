using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CliListApp
{
    public sealed class CommandItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public string Action { get; set; }
        public string Executable { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public bool CloseAfterLaunch { get; set; }
    }

    public sealed class CommandUsage
    {
        public int Count { get; set; }
        public string LastUsedUtc { get; set; }
    }

    internal sealed class UsageTracker
    {
        private readonly string usagePath;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private Dictionary<string, CommandUsage> usages;

        public UsageTracker(string usagePath)
        {
            this.usagePath = usagePath;
            usages = Load();
        }

        public CommandUsage Get(CommandItem command)
        {
            CommandUsage usage;
            return usages.TryGetValue(GetKey(command), out usage) ? usage : new CommandUsage();
        }

        public void Record(CommandItem command)
        {
            string key = GetKey(command);
            CommandUsage usage;
            if (!usages.TryGetValue(key, out usage))
            {
                usage = new CommandUsage();
                usages[key] = usage;
            }

            usage.Count++;
            usage.LastUsedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            try
            {
                Save();
            }
            catch
            {
                // 统计失败不应阻止 CLI 本身启动；当前会话仍保留内存计数。
            }
        }

        public static string GetKey(CommandItem command)
        {
            return string.IsNullOrWhiteSpace(command.Id) ? command.Name.Trim() : command.Id.Trim();
        }

        private Dictionary<string, CommandUsage> Load()
        {
            if (!File.Exists(usagePath))
            {
                return new Dictionary<string, CommandUsage>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                string json = File.ReadAllText(usagePath);
                Dictionary<string, CommandUsage> loaded = serializer.Deserialize<Dictionary<string, CommandUsage>>(json);
                return loaded == null
                    ? new Dictionary<string, CommandUsage>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, CommandUsage>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, CommandUsage>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Save()
        {
            string temporaryPath = usagePath + ".tmp";
            File.WriteAllText(temporaryPath, serializer.Serialize(usages));
            File.Copy(temporaryPath, usagePath, true);
            File.Delete(temporaryPath);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool validateOnly = args.Any(value => string.Equals(value, "--validate", StringComparison.OrdinalIgnoreCase));
            string screenshotPath = GetOptionValue(args, "--screenshot");
            string browserScreenshotPath = GetOptionValue(args, "--screenshot-browser");
            string previewFilePath = GetOptionValue(args, "--preview-file");
            bool automatedMode = validateOnly || !string.IsNullOrWhiteSpace(screenshotPath) || !string.IsNullOrWhiteSpace(browserScreenshotPath);

            try
            {
                string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string configPath = Path.Combine(appDirectory, "commands.json");
                string usagePath = Path.Combine(appDirectory, "usage.json");
                List<CommandItem> commands = LoadCommands(configPath);

                if (validateOnly)
                {
                    Environment.ExitCode = 0;
                    return;
                }

                string suppliedContext = GetContextArgument(args);
                string contextPath = ResolveContextDirectory(suppliedContext);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                if (!string.IsNullOrWhiteSpace(browserScreenshotPath))
                {
                    var browserForm = new DirectoryBrowserForm(appDirectory, contextPath);
                    if (!string.IsNullOrWhiteSpace(previewFilePath))
                    {
                        browserForm.SelectFileForPreview(previewFilePath);
                    }
                    RenderScreenshot(browserForm, browserScreenshotPath);
                    return;
                }

                var form = new MainForm(appDirectory, configPath, contextPath, commands, new UsageTracker(usagePath));

                if (!string.IsNullOrWhiteSpace(screenshotPath))
                {
                    RenderScreenshot(form, screenshotPath);
                    return;
                }

                Application.Run(form);
            }
            catch (Exception exception)
            {
                Environment.ExitCode = 1;
                if (automatedMode)
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cli-list-error.log"), exception.ToString());
                }
                else
                {
                    MessageBox.Show(
                        "CLI List 无法启动：\n\n" + exception.Message,
                        "CLI List",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
        }

        private static string GetOptionValue(string[] args, string optionName)
        {
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static string GetContextArgument(string[] args)
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--validate", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(args[index], "--screenshot", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (string.Equals(args[index], "--screenshot-browser", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (string.Equals(args[index], "--preview-file", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                return args[index];
            }

            return null;
        }

        private static void RenderScreenshot(Form form, string outputPath)
        {
            string fullOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.Show();
            Application.DoEvents();

            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(fullOutputPath, ImageFormat.Png);
            }

            form.Close();
        }

        private static List<CommandItem> LoadCommands(string configPath)
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException("未找到命令配置文件。", configPath);
            }

            string json = File.ReadAllText(configPath);
            var serializer = new JavaScriptSerializer();
            List<CommandItem> commands = serializer.Deserialize<List<CommandItem>>(json);

            if (commands == null || commands.Count == 0)
            {
                throw new InvalidDataException("commands.json 至少需要一个命令。 ");
            }

            var commandKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CommandItem command in commands)
            {
                bool isBuiltInAction = string.Equals(command.Action, "BrowsePowerShell", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(command.Name) || (!isBuiltInAction && string.IsNullOrWhiteSpace(command.Executable)))
                {
                    throw new InvalidDataException("每个命令都必须包含 Name，并提供 Executable 或受支持的 Action。 ");
                }

                if (!commandKeys.Add(UsageTracker.GetKey(command)))
                {
                    throw new InvalidDataException("每个命令的 Id 必须唯一；未设置 Id 时 Name 必须唯一。 ");
                }

                command.Tags = (command.Tags ?? new List<string>())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return commands;
        }

        private static string ResolveContextDirectory(string suppliedContext)
        {
            string fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(suppliedContext))
            {
                return fallback;
            }

            string expanded = Environment.ExpandEnvironmentVariables(suppliedContext.Trim('"'));
            if (Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }

            if (File.Exists(expanded))
            {
                return Path.GetDirectoryName(Path.GetFullPath(expanded));
            }

            return fallback;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly string appDirectory;
        private readonly string configPath;
        private readonly string contextPath;
        private readonly IList<CommandItem> commands;
        private readonly UsageTracker usageTracker;
        private readonly Color background = Color.FromArgb(244, 245, 239);
        private readonly Color surface = Color.FromArgb(255, 255, 252);
        private readonly Color surfaceHover = Color.FromArgb(226, 235, 224);
        private readonly Color textPrimary = Color.FromArgb(23, 28, 24);
        private readonly Color textSecondary = Color.FromArgb(91, 102, 94);
        private readonly Color accent = Color.FromArgb(151, 179, 155);
        private readonly Color border = Color.FromArgb(23, 28, 24);
        private TextBox searchBox;
        private ComboBox tagFilter;
        private ComboBox sortBox;
        private Label summaryLabel;
        private FlowLayoutPanel commandList;

        public MainForm(string appDirectory, string configPath, string contextPath, IList<CommandItem> commands, UsageTracker usageTracker)
        {
            this.appDirectory = appDirectory;
            this.configPath = configPath;
            this.contextPath = contextPath;
            this.commands = commands;
            this.usageTracker = usageTracker;

            Text = "CLI List";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(660, 620);
            Size = new Size(720, 760);
            BackColor = background;
            ForeColor = textPrimary;
            Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;

            string iconPath = Path.Combine(appDirectory, "cli-list.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(24, 22, 24, 20)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));

            root.Controls.Add(CreateHeader(), 0, 0);
            root.Controls.Add(CreateFilters(), 0, 1);
            root.Controls.Add(CreateCommandList(), 0, 2);
            root.Controls.Add(CreateFooter(), 0, 3);
            Controls.Add(root);

            KeyPreview = true;
            KeyDown += HandleShortcut;
            RefreshCommandList();
        }

        private Control CreateHeader()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = background };
            var title = new Label
            {
                Text = "CLI LIST_",
                ForeColor = textPrimary,
                Font = new Font("Consolas", 22F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(0, 0)
            };
            var subtitle = new Label
            {
                Text = "[ COMMAND DECK ]  选择命令并立即执行",
                ForeColor = textSecondary,
                Font = new Font("Consolas", 9.5F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(2, 43)
            };
            var context = new Label
            {
                Text = "PATH  >  " + contextPath,
                ForeColor = textSecondary,
                Font = new Font("Consolas", 8.5F, FontStyle.Regular),
                AutoEllipsis = true,
                Location = new Point(2, 72),
                Width = 620,
                Height = 20,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
            };

            var divider = new Panel
            {
                BackColor = border,
                Height = 2,
                Dock = DockStyle.Bottom
            };

            panel.Controls.Add(title);
            panel.Controls.Add(subtitle);
            panel.Controls.Add(context);
            panel.Controls.Add(divider);
            return panel;
        }

        private Control CreateFilters()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(0, 12, 0, 8)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132F));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));

            searchBox = new TextBox
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Margin = Padding.Empty,
                Font = new Font("Microsoft YaHei UI", 9F),
                BorderStyle = BorderStyle.None,
                BackColor = surface,
                ForeColor = textPrimary
            };
            searchBox.TextChanged += (sender, eventArgs) => RefreshCommandList();

            tagFilter = CreateFilterComboBox();
            tagFilter.Items.Add("全部标签");
            foreach (string tag in commands.SelectMany(command => command.Tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag))
            {
                tagFilter.Items.Add("# " + tag);
            }
            tagFilter.SelectedIndex = 0;
            tagFilter.SelectedIndexChanged += (sender, eventArgs) => RefreshCommandList();

            sortBox = CreateFilterComboBox();
            sortBox.Items.AddRange(new object[] { "默认排序", "使用最多", "最近使用" });
            sortBox.SelectedIndex = 0;
            sortBox.SelectedIndexChanged += (sender, eventArgs) => RefreshCommandList();

            summaryLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = textSecondary,
                Font = new Font("Consolas", 8.5F),
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(2, 2, 0, 0)
            };

            panel.Controls.Add(CreateFilterFrame(searchBox, new Padding(0, 0, 8, 0)), 0, 0);
            panel.Controls.Add(CreateFilterFrame(tagFilter, new Padding(0, 0, 8, 0)), 1, 0);
            panel.Controls.Add(CreateFilterFrame(sortBox, Padding.Empty), 2, 0);
            panel.Controls.Add(summaryLabel, 0, 1);
            panel.SetColumnSpan(summaryLabel, 3);
            return panel;
        }

        private ComboBox CreateFilterComboBox()
        {
            var comboBox = new ComboBox
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed,
                // 下拉框总高度约为 ItemHeight + 6；20px 可完整落在 28px 内容区内，
                // 避免原生控件覆盖外层 2px 底边框。
                ItemHeight = 20,
                Font = new Font("Microsoft YaHei UI", 9F),
                BackColor = surface,
                ForeColor = textPrimary
            };
            comboBox.DrawItem += DrawFilterItem;
            return comboBox;
        }

        private Control CreateFilterFrame(Control control, Padding margin)
        {
            var frame = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = margin,
                Padding = new Padding(2),
                BackColor = border
            };
            control.Dock = DockStyle.Fill;
            control.Margin = Padding.Empty;
            control.Enter += (sender, eventArgs) => frame.BackColor = accent;
            control.Leave += (sender, eventArgs) => frame.BackColor = border;
            frame.Controls.Add(control);
            return frame;
        }

        private void DrawFilterItem(object sender, DrawItemEventArgs eventArgs)
        {
            var comboBox = sender as ComboBox;
            if (comboBox == null || eventArgs.Index < 0)
            {
                return;
            }

            bool selected = (eventArgs.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (var brush = new SolidBrush(selected ? accent : surface))
            {
                eventArgs.Graphics.FillRectangle(brush, eventArgs.Bounds);
            }

            TextRenderer.DrawText(
                eventArgs.Graphics,
                comboBox.Items[eventArgs.Index].ToString(),
                comboBox.Font,
                new Rectangle(eventArgs.Bounds.X + 8, eventArgs.Bounds.Y, Math.Max(0, eventArgs.Bounds.Width - 10), eventArgs.Bounds.Height),
                textPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            );

            if ((eventArgs.State & DrawItemState.Focus) == DrawItemState.Focus)
            {
                eventArgs.DrawFocusRectangle();
            }
        }

        private Control CreateCommandList()
        {
            commandList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 14, 6, 8)
            };

            commandList.SizeChanged += (sender, eventArgs) => ResizeCommandCards();
            return commandList;
        }

        private void RefreshCommandList()
        {
            if (commandList == null || searchBox == null || tagFilter == null || sortBox == null)
            {
                return;
            }

            string query = searchBox.Text.Trim();
            string selectedTag = tagFilter.SelectedIndex <= 0 ? null : tagFilter.SelectedItem.ToString().Substring(2);
            IEnumerable<CommandItem> filtered = commands.Where(command => MatchesSearch(command, query) &&
                (selectedTag == null || command.Tags.Contains(selectedTag, StringComparer.OrdinalIgnoreCase)));

            if (sortBox.SelectedIndex == 1)
            {
                filtered = filtered.OrderByDescending(command => usageTracker.Get(command).Count).ThenBy(command => command.Name);
            }
            else if (sortBox.SelectedIndex == 2)
            {
                filtered = filtered.OrderByDescending(command => ParseLastUsed(usageTracker.Get(command).LastUsedUtc)).ThenBy(command => command.Name);
            }

            List<CommandItem> visibleCommands = filtered.ToList();
            commandList.SuspendLayout();
            commandList.Controls.Clear();

            int commandIndex = 1;
            foreach (CommandItem command in visibleCommands)
            {
                CommandUsage usage = usageTracker.Get(command);
                string tagText = command.Tags.Count == 0 ? "# 未分类" : string.Join("  ", command.Tags.Select(tag => "# " + tag));
                string usageText = "RUN " + usage.Count + " 次";
                DateTime lastUsed = ParseLastUsed(usage.LastUsedUtc);
                if (lastUsed != DateTime.MinValue)
                {
                    usageText += "  ·  LAST " + lastUsed.ToLocalTime().ToString("MM-dd HH:mm");
                }

                var button = new Button
                {
                    Tag = command,
                    Text = "[" + commandIndex.ToString("00") + "]  " + command.Name + Environment.NewLine +
                           "      " + (command.Description ?? string.Empty) + Environment.NewLine +
                           "      " + tagText + "    " + usageText,
                    Height = 88,
                    Width = 608,
                    Margin = new Padding(0, 0, 0, 12),
                    Padding = new Padding(15, 0, 14, 0),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = textPrimary,
                    BackColor = surface,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Consolas", 10F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    UseVisualStyleBackColor = false
                };
                button.FlatAppearance.BorderColor = border;
                button.FlatAppearance.BorderSize = 2;
                button.FlatAppearance.MouseOverBackColor = surfaceHover;
                button.FlatAppearance.MouseDownBackColor = accent;
                button.MouseEnter += (sender, eventArgs) => ((Button)sender).BackColor = surfaceHover;
                button.MouseLeave += (sender, eventArgs) => ((Button)sender).BackColor = surface;
                button.Click += LaunchCommand;
                commandList.Controls.Add(button);
                commandIndex++;
            }

            if (visibleCommands.Count == 0)
            {
                commandList.Controls.Add(new Label
                {
                    Text = "> 未找到匹配的 CLI\n  请调整关键词或标签筛选。",
                    Width = 608,
                    Height = 72,
                    Padding = new Padding(14, 16, 14, 0),
                    Margin = Padding.Empty,
                    BackColor = surface,
                    ForeColor = textSecondary,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Microsoft YaHei UI", 9.5F)
                });
            }

            // FlowLayoutPanel 在自动滚动时不会稳定保留最后一个控件的尾部 Margin。
            // 使用独立占位控件扩展滚动范围，避免末项下边框紧贴底部操作栏。
            commandList.Controls.Add(new Panel
            {
                Height = 12,
                Width = 608,
                Margin = Padding.Empty,
                BackColor = background
            });
            summaryLabel.Text = "SEARCH  名称 / 描述 / 标签    CLI  " + visibleCommands.Count + " / " + commands.Count +
                                "    TOTAL RUN  " + commands.Sum(command => usageTracker.Get(command).Count);
            commandList.ResumeLayout();
            ResizeCommandCards();
        }

        private void ResizeCommandCards()
        {
            if (commandList == null)
            {
                return;
            }

            int width = Math.Max(360, commandList.ClientSize.Width - 14);
            foreach (Control control in commandList.Controls)
            {
                control.Width = width;
            }
        }

        private static bool MatchesSearch(CommandItem command, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            return (command.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (command.Description ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   command.Tags.Any(tag => tag.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static DateTime ParseLastUsed(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed) ? parsed : DateTime.MinValue;
        }

        private void HandleShortcut(object sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.Control && eventArgs.KeyCode == Keys.F)
            {
                searchBox.Focus();
                searchBox.SelectAll();
                eventArgs.SuppressKeyPress = true;
            }
            else if (eventArgs.KeyCode == Keys.Escape && !string.IsNullOrEmpty(searchBox.Text))
            {
                searchBox.Clear();
                eventArgs.SuppressKeyPress = true;
            }
        }

        private Control CreateFooter()
        {
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 12, 0, 0)
            };

            var closeButton = CreateFooterButton("关闭");
            closeButton.Click += (sender, eventArgs) => Close();

            var folderButton = CreateFooterButton("打开配置目录");
            folderButton.Click += (sender, eventArgs) => Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + appDirectory.TrimEnd(Path.DirectorySeparatorChar) + "\"",
                UseShellExecute = true
            });

            var editButton = CreateFooterButton("编辑命令");
            editButton.Click += (sender, eventArgs) => Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + configPath + "\"",
                UseShellExecute = true
            });

            footer.Controls.Add(closeButton);
            footer.Controls.Add(folderButton);
            footer.Controls.Add(editButton);
            return footer;
        }

        private Button CreateFooterButton(string text)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 36,
                Margin = new Padding(8, 0, 0, 0),
                Padding = new Padding(12, 0, 12, 0),
                ForeColor = textPrimary,
                BackColor = surface,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Consolas", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.MouseOverBackColor = accent;
            button.FlatAppearance.MouseDownBackColor = surfaceHover;
            return button;
        }

        private void LaunchCommand(object sender, EventArgs eventArgs)
        {
            var button = sender as Button;
            var command = button == null ? null : button.Tag as CommandItem;
            if (command == null)
            {
                return;
            }

            try
            {
                if (string.Equals(command.Action, "BrowsePowerShell", StringComparison.OrdinalIgnoreCase))
                {
                    usageTracker.Record(command);
                    RefreshCommandList();
                    using (var browser = new DirectoryBrowserForm(appDirectory, contextPath))
                    {
                        if (browser.ShowDialog(this) == DialogResult.OK)
                        {
                            Close();
                        }
                    }
                    return;
                }

                string executable = ResolveExecutable(command.Executable);
                string workingDirectory = ResolveWorkingDirectory(command.WorkingDirectory);
                string arguments = Environment.ExpandEnvironmentVariables(command.Arguments ?? string.Empty)
                    .Replace("{context}", contextPath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                usageTracker.Record(command);
                RefreshCommandList();

                if (command.CloseAfterLaunch)
                {
                    Close();
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "命令执行失败：\n\n" + exception.Message,
                    command.Name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }

        private string ResolveExecutable(string configuredPath)
        {
            string expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            if (Path.IsPathRooted(expanded))
            {
                return expanded;
            }

            if (expanded.Contains("\\") || expanded.Contains("/"))
            {
                return Path.GetFullPath(Path.Combine(appDirectory, expanded));
            }

            return expanded;
        }

        private string ResolveWorkingDirectory(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath) || configuredPath == "{context}")
            {
                return contextPath;
            }

            string expanded = Environment.ExpandEnvironmentVariables(configuredPath)
                .Replace("{context}", contextPath);
            if (!Path.IsPathRooted(expanded))
            {
                expanded = Path.GetFullPath(Path.Combine(appDirectory, expanded));
            }

            return Directory.Exists(expanded) ? expanded : contextPath;
        }
    }

    internal sealed class DirectoryBrowserForm : Form
    {
        private readonly string appDirectory;
        private readonly Color background = Color.FromArgb(244, 245, 239);
        private readonly Color surface = Color.FromArgb(255, 255, 252);
        private readonly Color surfaceHover = Color.FromArgb(226, 235, 224);
        private readonly Color border = Color.FromArgb(23, 28, 24);
        private readonly Color textPrimary = Color.FromArgb(23, 28, 24);
        private readonly Color textSecondary = Color.FromArgb(91, 102, 94);
        private readonly Color accent = Color.FromArgb(151, 179, 155);
        private readonly HashSet<string> textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".json", ".yaml", ".yml", ".toml", ".ini", ".log", ".env",
            ".xml", ".html", ".css", ".scss", ".js", ".jsx", ".ts", ".tsx", ".vue",
            ".py", ".cs", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs", ".sql",
            ".ps1", ".cmd", ".bat", ".sh"
        };
        private readonly HashSet<string> imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico"
        };

        private TextBox pathBox;
        private ListView fileList;
        private Label previewTitle;
        private Panel textPreviewHost;
        private Label textPreview;
        private PictureBox imagePreview;
        private Label previewMessage;
        private Label statusLabel;
        private string currentDirectory;

        public DirectoryBrowserForm(string appDirectory, string initialDirectory)
        {
            this.appDirectory = appDirectory;

            Text = "目录 PowerShell";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(860, 560);
            Size = new Size(1060, 700);
            BackColor = background;
            ForeColor = textPrimary;
            Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;

            string iconPath = Path.Combine(appDirectory, "cli-list.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }

            Controls.Add(CreateLayout());
            FormClosed += (sender, eventArgs) => DisposePreviewImage();
            LoadDirectory(initialDirectory);
        }

        private Control CreateLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(20)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            root.Controls.Add(CreateToolbar(), 0, 0);
            root.Controls.Add(CreateContent(), 0, 1);
            root.Controls.Add(CreateFooter(), 0, 2);
            return root;
        }

        private Control CreateToolbar()
        {
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 8)
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78F));

            var upButton = CreateButton("↑", 34);
            upButton.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            upButton.Click += (sender, eventArgs) =>
            {
                DirectoryInfo parent = Directory.GetParent(currentDirectory);
                if (parent != null)
                {
                    LoadDirectory(parent.FullName);
                }
            };

            pathBox = new TextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = textPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 10F),
                Margin = new Padding(6, 5, 8, 5)
            };
            pathBox.KeyDown += (sender, eventArgs) =>
            {
                if (eventArgs.KeyCode == Keys.Enter)
                {
                    LoadDirectory(pathBox.Text);
                    eventArgs.SuppressKeyPress = true;
                }
            };

            var chooseButton = CreateButton("[选择]", 34);
            chooseButton.Click += (sender, eventArgs) => ChooseDirectory();

            var refreshButton = CreateButton("[刷新]", 34);
            refreshButton.Click += (sender, eventArgs) => LoadDirectory(currentDirectory);

            toolbar.Controls.Add(upButton, 0, 0);
            toolbar.Controls.Add(pathBox, 1, 0);
            toolbar.Controls.Add(chooseButton, 2, 0);
            toolbar.Controls.Add(refreshButton, 3, 0);
            return toolbar;
        }

        private Control CreateContent()
        {
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 420,
                SplitterWidth = 2,
                BackColor = border
            };

            fileList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                BackColor = Color.White,
                ForeColor = textPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9F),
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            fileList.Columns.Add("名称", 180);
            fileList.Columns.Add("类型", 60);
            fileList.Columns.Add("大小", 65, HorizontalAlignment.Right);
            fileList.Columns.Add("修改时间", 105);
            fileList.SelectedIndexChanged += (sender, eventArgs) => PreviewSelection();
            fileList.DoubleClick += (sender, eventArgs) => OpenSelectedItem();
            split.Panel1.Padding = new Padding(0, 0, 4, 0);
            split.Panel1.Controls.Add(fileList);

            var previewLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = surface,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12)
            };
            previewLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            previewTitle = new Label
            {
                Text = "[ PREVIEW ]  文件预览",
                Dock = DockStyle.Fill,
                ForeColor = textPrimary,
                BackColor = accent,
                Font = new Font("Consolas", 10F, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            textPreviewHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                Visible = false
            };
            textPreview = new Label
            {
                AutoSize = true,
                BackColor = Color.White,
                ForeColor = textPrimary,
                Font = new Font("Consolas", 9.5F),
                Padding = new Padding(10),
                MaximumSize = new Size(900, 0)
            };
            textPreviewHost.Controls.Add(textPreview);
            textPreviewHost.SizeChanged += (sender, eventArgs) =>
            {
                textPreview.MaximumSize = new Size(Math.Max(240, textPreviewHost.ClientSize.Width - 24), 0);
            };
            imagePreview = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                SizeMode = PictureBoxSizeMode.Zoom,
                Visible = false
            };
            previewMessage = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = textSecondary,
                BackColor = Color.White,
                Text = "选择文件后在此预览",
                TextAlign = ContentAlignment.MiddleCenter
            };

            previewHost.Controls.Add(textPreviewHost);
            previewHost.Controls.Add(imagePreview);
            previewHost.Controls.Add(previewMessage);
            previewLayout.Controls.Add(previewTitle, 0, 0);
            previewLayout.Controls.Add(previewHost, 0, 1);
            split.Panel2.Padding = new Padding(4, 0, 0, 0);
            split.Panel2.Controls.Add(previewLayout);
            return split;
        }

        private Control CreateFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 10, 0, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));

            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = textSecondary,
                Font = new Font("Consolas", 8.5F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                AutoSize = false,
                Padding = new Padding(4, 0, 0, 4)
            };

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = background
            };
            var openButton = CreateButton("[运行] 在此打开 PowerShell", 38);
            openButton.AutoSize = true;
            openButton.Padding = new Padding(12, 0, 12, 0);
            openButton.BackColor = accent;
            openButton.Click += (sender, eventArgs) => OpenPowerShell();

            var cancelButton = CreateButton("[取消]", 38);
            cancelButton.Width = 70;
            cancelButton.Click += (sender, eventArgs) => Close();

            actions.Controls.Add(openButton);
            actions.Controls.Add(cancelButton);
            footer.Controls.Add(statusLabel, 0, 0);
            footer.Controls.Add(actions, 1, 0);
            return footer;
        }

        private Button CreateButton(string text, int height)
        {
            var button = new Button
            {
                Text = text,
                Height = height,
                Dock = DockStyle.Fill,
                Margin = new Padding(4),
                ForeColor = textPrimary,
                BackColor = surface,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Consolas", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.MouseOverBackColor = surfaceHover;
            button.FlatAppearance.MouseDownBackColor = accent;
            button.MouseEnter += (sender, eventArgs) => ((Button)sender).BackColor = surfaceHover;
            button.MouseLeave += (sender, eventArgs) =>
            {
                var hoveredButton = (Button)sender;
                hoveredButton.BackColor = hoveredButton.Text == "[运行] 在此打开 PowerShell"
                    ? accent
                    : surface;
            };
            return button;
        }

        private void ChooseDirectory()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择要打开 PowerShell 的目录",
                SelectedPath = currentDirectory,
                ShowNewFolderButton = true
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    LoadDirectory(dialog.SelectedPath);
                }
            }
        }

        private void LoadDirectory(string path)
        {
            try
            {
                string expanded = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim('"'));
                if (!Directory.Exists(expanded))
                {
                    throw new DirectoryNotFoundException("目录不存在：" + expanded);
                }

                var directory = new DirectoryInfo(Path.GetFullPath(expanded));
                FileSystemInfo[] entries = directory.GetFileSystemInfos();
                currentDirectory = directory.FullName;
                pathBox.Text = currentDirectory;
                statusLabel.Text = string.Empty;

                fileList.BeginUpdate();
                fileList.Items.Clear();

                if (directory.Parent != null)
                {
                    var parentItem = new ListViewItem("..") { Tag = directory.Parent };
                    parentItem.SubItems.Add("上一级");
                    parentItem.SubItems.Add(string.Empty);
                    parentItem.SubItems.Add(string.Empty);
                    fileList.Items.Add(parentItem);
                }

                foreach (DirectoryInfo childDirectory in entries.OfType<DirectoryInfo>().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(childDirectory.Name) { Tag = childDirectory };
                    item.SubItems.Add("文件夹");
                    item.SubItems.Add(string.Empty);
                    item.SubItems.Add(childDirectory.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                    fileList.Items.Add(item);
                }

                foreach (FileInfo file in entries.OfType<FileInfo>().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(file.Name) { Tag = file };
                    item.SubItems.Add(string.IsNullOrEmpty(file.Extension) ? "文件" : file.Extension.TrimStart('.').ToUpperInvariant());
                    item.SubItems.Add(FormatSize(file.Length));
                    item.SubItems.Add(file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                    fileList.Items.Add(item);
                }

                fileList.EndUpdate();
                ShowPreviewMessage("选择文件后在此预览");
            }
            catch (Exception exception)
            {
                if (fileList != null)
                {
                    fileList.EndUpdate();
                }
                MessageBox.Show(exception.Message, "无法打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenSelectedItem()
        {
            if (fileList.SelectedItems.Count == 0)
            {
                return;
            }

            var directory = fileList.SelectedItems[0].Tag as DirectoryInfo;
            if (directory != null)
            {
                LoadDirectory(directory.FullName);
            }
        }

        private void PreviewSelection()
        {
            if (fileList.SelectedItems.Count == 0)
            {
                ShowPreviewMessage("选择文件后在此预览");
                return;
            }

            FileSystemInfo selected = fileList.SelectedItems[0].Tag as FileSystemInfo;
            if (selected == null)
            {
                return;
            }

            previewTitle.Text = selected.Name;
            var directory = selected as DirectoryInfo;
            if (directory != null)
            {
                ShowPreviewMessage("文件夹\n\n" + directory.FullName + "\n\n双击进入该目录");
                return;
            }

            var file = selected as FileInfo;
            if (file == null)
            {
                return;
            }

            try
            {
                if (imageExtensions.Contains(file.Extension))
                {
                    ShowImagePreview(file.FullName);
                }
                else if (textExtensions.Contains(file.Extension) || file.Length == 0)
                {
                    ShowTextPreview(file.FullName, file.Length);
                }
                else
                {
                    ShowPreviewMessage(
                        "暂不支持预览此文件类型\n\n路径：" + file.FullName +
                        "\n大小：" + FormatSize(file.Length) +
                        "\n修改时间：" + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    );
                }
            }
            catch (Exception exception)
            {
                ShowPreviewMessage("预览失败：\n\n" + exception.Message);
            }
        }

        internal void SelectFileForPreview(string filePath)
        {
            string expanded = Environment.ExpandEnvironmentVariables(filePath ?? string.Empty);
            if (!File.Exists(expanded))
            {
                throw new FileNotFoundException("预览测试文件不存在。", expanded);
            }

            string fullPath = Path.GetFullPath(expanded);
            LoadDirectory(Path.GetDirectoryName(fullPath));
            foreach (ListViewItem item in fileList.Items)
            {
                var file = item.Tag as FileInfo;
                if (file != null && string.Equals(file.FullName, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    item.Selected = true;
                    item.Focused = true;
                    PreviewSelection();
                    return;
                }
            }

            throw new InvalidOperationException("文件已存在，但未出现在目录列表中。 ");
        }

        private void ShowTextPreview(string path, long fileLength)
        {
            DisposePreviewImage();
            const int maxCharacters = 50000;
            char[] buffer = new char[maxCharacters];
            int count;
            using (var reader = new StreamReader(path, true))
            {
                count = reader.Read(buffer, 0, buffer.Length);
            }

            string content = new string(buffer, 0, count);
            if (fileLength > 200000 || count == maxCharacters)
            {
                content += "\n\n—— 仅预览前 50,000 个字符 ——";
            }
            textPreview.Text = content;
            textPreviewHost.Visible = true;
            textPreviewHost.BringToFront();
            imagePreview.Visible = false;
            previewMessage.Visible = false;
        }

        private void ShowImagePreview(string path)
        {
            DisposePreviewImage();
            using (Image source = Image.FromFile(path))
            {
                imagePreview.Image = new Bitmap(source);
            }
            imagePreview.Visible = true;
            imagePreview.BringToFront();
            textPreviewHost.Visible = false;
            previewMessage.Visible = false;
        }

        private void ShowPreviewMessage(string message)
        {
            DisposePreviewImage();
            previewMessage.Text = message;
            previewMessage.Visible = true;
            previewMessage.BringToFront();
            textPreviewHost.Visible = false;
            imagePreview.Visible = false;
        }

        private void DisposePreviewImage()
        {
            if (imagePreview != null && imagePreview.Image != null)
            {
                Image oldImage = imagePreview.Image;
                imagePreview.Image = null;
                oldImage.Dispose();
            }
        }

        private void OpenPowerShell()
        {
            try
            {
                string executable = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe");
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-NoExit",
                    WorkingDirectory = currentDirectory,
                    UseShellExecute = true
                });
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show("PowerShell 启动失败：\n\n" + exception.Message, "目录 PowerShell", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? size.ToString("0") + " " + units[unit] : size.ToString("0.##") + " " + units[unit];
        }
    }
}
