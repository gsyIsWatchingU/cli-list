using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CliListApp
{
    public sealed class CommandItem
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Action { get; set; }
        public string Executable { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public bool CloseAfterLaunch { get; set; }
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

                var form = new MainForm(appDirectory, configPath, contextPath, commands);

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

            foreach (CommandItem command in commands)
            {
                bool isBuiltInAction = string.Equals(command.Action, "BrowsePowerShell", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(command.Name) || (!isBuiltInAction && string.IsNullOrWhiteSpace(command.Executable)))
                {
                    throw new InvalidDataException("每个命令都必须包含 Name，并提供 Executable 或受支持的 Action。 ");
                }
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
        private readonly Color background = Color.FromArgb(244, 245, 239);
        private readonly Color surface = Color.FromArgb(255, 255, 252);
        private readonly Color surfaceHover = Color.FromArgb(226, 235, 224);
        private readonly Color textPrimary = Color.FromArgb(23, 28, 24);
        private readonly Color textSecondary = Color.FromArgb(91, 102, 94);
        private readonly Color accent = Color.FromArgb(151, 179, 155);
        private readonly Color border = Color.FromArgb(23, 28, 24);

        public MainForm(string appDirectory, string configPath, string contextPath, IList<CommandItem> commands)
        {
            this.appDirectory = appDirectory;
            this.configPath = configPath;
            this.contextPath = contextPath;

            Text = "CLI List";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(600, 560);
            Size = new Size(660, 700);
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
                RowCount = 3,
                Padding = new Padding(24, 22, 24, 20)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));

            root.Controls.Add(CreateHeader(), 0, 0);
            root.Controls.Add(CreateCommandList(commands), 0, 1);
            root.Controls.Add(CreateFooter(), 0, 2);
            Controls.Add(root);
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
                Width = 560,
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

        private Control CreateCommandList(IList<CommandItem> commands)
        {
            var list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(0, 14, 6, 8)
            };

            int commandIndex = 1;
            foreach (CommandItem command in commands)
            {
                var button = new Button
                {
                    Tag = command,
                    Text = "[" + commandIndex.ToString("00") + "]  " + command.Name + Environment.NewLine +
                           "      " + (command.Description ?? string.Empty),
                    Height = 74,
                    Width = 548,
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
                list.Controls.Add(button);
                commandIndex++;
            }

            list.SizeChanged += (sender, eventArgs) =>
            {
                int width = Math.Max(320, list.ClientSize.Width - 14);
                foreach (Control control in list.Controls)
                {
                    control.Width = width;
                }
            };

            return list;
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
