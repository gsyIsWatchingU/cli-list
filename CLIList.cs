using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("CLI List")]
[assembly: AssemblyProduct("CLI List")]
[assembly: AssemblyVersion("0.3.2")]
[assembly: AssemblyFileVersion("0.3.2")]

namespace CliListApp
{
    internal static class AppInfo
    {
        public static string Version
        {
            get
            {
                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "0.0.0" : version.ToString(3);
            }
        }

        public static string DisplayName
        {
            get { return "CLI List v" + Version; }
        }
    }

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
        public bool Disabled { get; set; }
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
        internal static readonly string ResidentMutexName = @"Local\CliListApp.Resident." + Environment.UserName;
        internal static readonly string ResidentPipeName = "CliListApp.Resident." + Environment.UserName;

        [STAThread]
        private static void Main(string[] args)
        {
            bool validateOnly = args.Any(value => string.Equals(value, "--validate", StringComparison.OrdinalIgnoreCase));
            bool residentRequested = args.Any(value => string.Equals(value, "--resident", StringComparison.OrdinalIgnoreCase));
            string screenshotPath = GetOptionValue(args, "--screenshot");
            string aiEditorScreenshotPath = GetOptionValue(args, "--screenshot-ai-editor");
            string browserScreenshotPath = GetOptionValue(args, "--screenshot-browser");
            string browserPickerScreenshotPath = GetOptionValue(args, "--screenshot-browser-picker");
            string ideCliPickerScreenshotPath = GetOptionValue(args, "--screenshot-ide-cli-picker");
            string previewFilePath = GetOptionValue(args, "--preview-file");
            string aiPatchPath = GetOptionValue(args, "--apply-ai-patch");
            bool automatedMode = validateOnly || !string.IsNullOrWhiteSpace(screenshotPath) ||
                !string.IsNullOrWhiteSpace(aiEditorScreenshotPath) || !string.IsNullOrWhiteSpace(aiPatchPath) ||
                !string.IsNullOrWhiteSpace(browserScreenshotPath) || !string.IsNullOrWhiteSpace(browserPickerScreenshotPath) ||
                !string.IsNullOrWhiteSpace(ideCliPickerScreenshotPath);

            try
            {
                string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string configPath = Path.Combine(appDirectory, "commands.json");
                string usagePath = Path.Combine(appDirectory, "usage.json");
                AiCommandPatchService.MigrateLegacyIdeCliCommand(configPath, GetLocalConfigPath(configPath));

                if (validateOnly)
                {
                    LoadCommands(configPath);
                    Environment.ExitCode = 0;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(aiPatchPath))
                {
                    string localConfigPath = GetLocalConfigPath(configPath);
                    string fingerprint = AiCommandPatchService.ComputeFingerprint(configPath, localConfigPath);
                    AiCommandPatchPlan plan = AiCommandPatchService.Prepare(
                        File.ReadAllText(Path.GetFullPath(aiPatchPath)),
                        configPath,
                        localConfigPath,
                        fingerprint
                    );
                    AiCommandPatchService.Apply(plan, configPath, localConfigPath);
                    Environment.ExitCode = 0;
                    return;
                }

                string suppliedContext = GetContextArgument(args);
                string contextPath = ResolveContextDirectory(suppliedContext);

                if (Installer.TryEnsureInstalled(appDirectory, suppliedContext, residentRequested))
                {
                    return;
                }

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

                if (!string.IsNullOrWhiteSpace(browserPickerScreenshotPath))
                {
                    var pickerForm = new BrowserPickerForm(appDirectory, contextPath);
                    RenderScreenshot(pickerForm, browserPickerScreenshotPath);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(ideCliPickerScreenshotPath))
                {
                    var pickerForm = new IdeCliPickerForm(appDirectory, contextPath);
                    RenderScreenshot(pickerForm, ideCliPickerScreenshotPath);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(screenshotPath))
                {
                    var form = CreateMainForm(appDirectory, configPath, usagePath, contextPath);
                    RenderScreenshot(form, screenshotPath);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(aiEditorScreenshotPath))
                {
                    var form = new AiCommandEditorForm(configPath, GetLocalConfigPath(configPath));
                    RenderScreenshot(form, aiEditorScreenshotPath);
                    return;
                }

                bool ownsMutex;
                using (var instanceMutex = new Mutex(true, ResidentMutexName, out ownsMutex))
                {
                    if (!ownsMutex)
                    {
                        if (!ResidentApplicationContext.TryForwardToResident(contextPath, !residentRequested, 1800))
                        {
                            MessageBox.Show(
                                "CLI List 已在运行，但暂时无法连接到驻留进程。请稍后重试。",
                                "CLI List",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning
                            );
                        }
                        return;
                    }

                    try
                    {
                        using (var resident = new ResidentApplicationContext(
                            appDirectory,
                            configPath,
                            usagePath,
                            contextPath,
                            !residentRequested
                        ))
                        {
                            Application.Run(resident);
                        }
                    }
                    finally
                    {
                        instanceMutex.ReleaseMutex();
                    }
                }
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

                if (string.Equals(args[index], "--resident", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(args[index], "--screenshot", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (string.Equals(args[index], "--screenshot-ai-editor", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[index], "--apply-ai-patch", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (string.Equals(args[index], "--screenshot-browser", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (string.Equals(args[index], "--screenshot-browser-picker", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    continue;
                }

                if (string.Equals(args[index], "--screenshot-ide-cli-picker", StringComparison.OrdinalIgnoreCase))
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
                bitmap.Save(fullOutputPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            form.Close();
        }

        internal static MainForm CreateMainForm(string appDirectory, string configPath, string usagePath, string contextPath)
        {
            string localConfigPath = GetLocalConfigPath(configPath);
            return new MainForm(
                appDirectory,
                localConfigPath,
                contextPath,
                LoadCommands(configPath),
                new UsageTracker(usagePath)
            );
        }

        internal static List<CommandItem> LoadCommands(string configPath)
        {
            List<CommandItem> commands = ReadCommands(configPath, false);
            ValidateCommands(commands, false, Path.GetFileName(configPath));

            string localConfigPath = GetLocalConfigPath(configPath);
            if (!File.Exists(localConfigPath))
            {
                return commands;
            }

            List<CommandItem> localCommands = ReadCommands(localConfigPath, true);
            ValidateCommands(localCommands, true, Path.GetFileName(localConfigPath));

            // 提取本地配置中的排序顺序（按出现顺序）
            var localOrder = new List<string>();
            var localSettings = new Dictionary<string, CommandItem>(StringComparer.OrdinalIgnoreCase);
            foreach (CommandItem localCommand in localCommands)
            {
                string key = GetCommandKey(localCommand);
                localOrder.Add(key);
                localSettings[key] = localCommand;
            }

            // 应用本地配置的修改（覆盖/新增/禁用）
            foreach (CommandItem localCommand in localCommands)
            {
                string localKey = GetCommandKey(localCommand);
                int existingIndex = commands.FindIndex(command => string.Equals(
                    GetCommandKey(command),
                    localKey,
                    StringComparison.OrdinalIgnoreCase
                ));

                if (localCommand.Disabled)
                {
                    if (existingIndex >= 0)
                    {
                        commands.RemoveAt(existingIndex);
                    }
                    continue;
                }

                if (existingIndex >= 0)
                {
                    commands[existingIndex] = localCommand;
                }
                else
                {
                    commands.Add(localCommand);
                }
            }

            // 如果本地配置定义了排序顺序，按该顺序重排
            if (localOrder.Count > 0)
            {
                var ordered = new List<CommandItem>();
                var remaining = new List<CommandItem>(commands);

                // 按本地排序顺序排列
                foreach (string key in localOrder)
                {
                    int index = remaining.FindIndex(c => string.Equals(GetCommandKey(c), key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0 && !remaining[index].Disabled)
                    {
                        ordered.Add(remaining[index]);
                        remaining.RemoveAt(index);
                    }
                }

                // 剩余未在排序中的命令追加到末尾
                ordered.AddRange(remaining.Where(c => !c.Disabled));
                commands = ordered;
            }

            ValidateCommands(commands, false, "合并后的命令配置");
            return commands;
        }

        internal static string GetLocalConfigPath(string configPath)
        {
            return Path.Combine(Path.GetDirectoryName(configPath), "commands.local.json");
        }

        internal static void EnsureLocalConfigFile(string localConfigPath)
        {
            if (File.Exists(localConfigPath))
            {
                return;
            }

            string directoryPath = Path.GetDirectoryName(localConfigPath);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            File.WriteAllText(localConfigPath, "[]" + Environment.NewLine, new UTF8Encoding(false));
        }

        private static List<CommandItem> ReadCommands(string configPath, bool allowEmpty)
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException("未找到命令配置文件。", configPath);
            }

            string json = File.ReadAllText(configPath);
            var serializer = new JavaScriptSerializer();
            List<CommandItem> commands = serializer.Deserialize<List<CommandItem>>(json);

            if (commands == null || (!allowEmpty && commands.Count == 0))
            {
                throw new InvalidDataException("commands.json 至少需要一个命令。 ");
            }

            return commands ?? new List<CommandItem>();
        }

        private static void ValidateCommands(List<CommandItem> commands, bool allowDisabled, string sourceName)
        {
            if (!allowDisabled && commands.Count == 0)
            {
                throw new InvalidDataException(sourceName + " 至少需要一个可用命令。 ");
            }

            var commandKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CommandItem command in commands)
            {
                if (command == null)
                {
                    throw new InvalidDataException(sourceName + " 不能包含空命令。 ");
                }

                if (command.Disabled)
                {
                    if (!allowDisabled)
                    {
                        throw new InvalidDataException(sourceName + " 不允许包含 Disabled 命令。 ");
                    }
                    if (string.IsNullOrWhiteSpace(command.Id) && string.IsNullOrWhiteSpace(command.Name))
                    {
                        throw new InvalidDataException("禁用命令必须提供 Id 或 Name。 ");
                    }
                }

                bool isBuiltInAction = string.Equals(command.Action, "BrowsePowerShell", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(command.Action, "OpenBrowser", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(command.Action, "ChooseIdeOrCli", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(command.Action, "CheckUpdate", StringComparison.OrdinalIgnoreCase);
                if (!command.Disabled && (string.IsNullOrWhiteSpace(command.Name) || (!isBuiltInAction && string.IsNullOrWhiteSpace(command.Executable))))
                {
                    throw new InvalidDataException("每个命令都必须包含 Name，并提供 Executable 或受支持的 Action。 ");
                }

                if (!commandKeys.Add(GetCommandKey(command)))
                {
                    throw new InvalidDataException(sourceName + " 中的命令 Id 必须唯一；未设置 Id 时 Name 必须唯一。 ");
                }

                command.Tags = (command.Tags ?? new List<string>())
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Select(tag => tag.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private static string GetCommandKey(CommandItem command)
        {
            return string.IsNullOrWhiteSpace(command.Id) ? command.Name.Trim() : command.Id.Trim();
        }

        internal static string ResolveContextDirectory(string suppliedContext)
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

    internal static class UpdateManager
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/gsyIsWatchingU/cli-list/releases/latest";
        private static readonly string[] ProtectedFileNames =
        {
            "commands.json",
            "commands.local.json",
            "commands.local.json.ai-last.bak",
            "commands.local.json.pre-ide-cli-picker.bak",
            "commands.pre-shared-migration.json",
            "usage.json"
        };

        public static void BeginSilentCheck(Action<string> onAvailable)
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                    Dictionary<string, object> release = ReadLatestRelease();
                    string tagName = GetRequiredString(release, "tag_name", "Release 缺少版本号。");
                    Version currentVersion;
                    Version latestVersion;
                    if (!Regex.IsMatch(tagName, @"^v\d+\.\d+\.\d+$") ||
                        !Version.TryParse(AppInfo.Version, out currentVersion) ||
                        !Version.TryParse(tagName.Substring(1), out latestVersion) ||
                        latestVersion <= currentVersion)
                    {
                        return;
                    }

                    if (onAvailable != null)
                    {
                        onAvailable(tagName);
                    }
                }
                catch
                {
                    // 启动检查不得打断用户；网络恢复后，下次启动会再次检查。
                }
            });
        }

        public static void CheckForUpdate(IWin32Window owner, string appDirectory)
        {
            string workDirectory = null;
            string stageDirectory = null;
            bool handedOff = false;

            try
            {
                string installDirectory = Path.GetFullPath(appDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string sourceMarkerPath = Path.Combine(installDirectory, ".source-repository");
                bool isSourceInstall = File.Exists(sourceMarkerPath);

                if (!isSourceInstall && !Installer.IsProductInstallDirectory(installDirectory))
                {
                    throw new InvalidOperationException("请从已安装的 CLI List 中检查更新，不能直接更新源码目录或临时解压目录。");
                }

                System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072;
                Dictionary<string, object> release = ReadLatestRelease();
                string tagName = GetRequiredString(release, "tag_name", "Release 缺少版本号。");
                if (!Regex.IsMatch(tagName, @"^v\d+\.\d+\.\d+$"))
                {
                    throw new InvalidDataException("GitHub Release 版本号格式无效：" + tagName);
                }

                Version currentVersion;
                Version latestVersion;
                if (!Version.TryParse(AppInfo.Version, out currentVersion) ||
                    !Version.TryParse(tagName.Substring(1), out latestVersion))
                {
                    throw new InvalidDataException("无法比较当前版本与 GitHub Release 版本。");
                }

                if (latestVersion <= currentVersion)
                {
                    MessageBox.Show(
                        owner,
                        "当前已是最新版本 v" + AppInfo.Version + "。",
                        "检查更新",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                    return;
                }

                DialogResult confirm = MessageBox.Show(
                    owner,
                    "发现新版本 " + tagName + "（当前 v" + AppInfo.Version + "）。" +
                    Environment.NewLine + Environment.NewLine +
                    "是否立即更新？程序会自动关闭，完成后重新启动。",
                    "CLI List 更新",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                string helperSourcePath = Path.Combine(installDirectory, "update-helper.ps1");
                if (!File.Exists(helperSourcePath))
                {
                    throw new FileNotFoundException("缺少更新组件，请重新下载安装最新版本。", helperSourcePath);
                }

                workDirectory = Path.Combine(Path.GetTempPath(), "cli-list-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDirectory);
                string helperPath = Path.Combine(workDirectory, "update-helper.ps1");
                File.Copy(helperSourcePath, helperPath, true);

                if (isSourceInstall)
                {
                    string sourceDirectory = File.ReadAllText(sourceMarkerPath).Trim().TrimStart('\uFEFF');
                    if (string.IsNullOrWhiteSpace(sourceDirectory) ||
                        (!Directory.Exists(Path.Combine(sourceDirectory, ".git")) &&
                         !File.Exists(Path.Combine(sourceDirectory, ".git"))))
                    {
                        throw new InvalidDataException("源码安装标记无效，请重新运行 install.ps1。");
                    }

                    StartUpdateHelper(
                        helperPath,
                        "Source",
                        installDirectory,
                        null,
                        null,
                        sourceDirectory,
                        workDirectory
                    );
                }
                else
                {
                    Dictionary<string, object> asset = FindReleaseAsset(release, tagName);
                    string downloadUrl = GetRequiredString(asset, "browser_download_url", "Release 安装包缺少下载地址。");
                    object sizeValue;
                    long expectedSize = asset.TryGetValue("size", out sizeValue)
                        ? Convert.ToInt64(sizeValue, CultureInfo.InvariantCulture)
                        : 0;
                    if (expectedSize < 0 || expectedSize > 100 * 1024 * 1024)
                    {
                        throw new InvalidDataException("Release 安装包大小异常。");
                    }

                    string expectedHash = GetExpectedHash(asset, downloadUrl, workDirectory);

                    string zipPath = Path.Combine(workDirectory, "update.zip");
                    using (var client = CreateWebClient())
                    {
                        client.DownloadFile(downloadUrl, zipPath);
                    }

                    long actualSize = new FileInfo(zipPath).Length;
                    if (actualSize <= 0 || actualSize > 100 * 1024 * 1024 ||
                        (expectedSize > 0 && actualSize != expectedSize))
                    {
                        throw new InvalidDataException("更新包下载不完整，当前版本未做任何修改。");
                    }

                    string actualHash = ComputeSha256(zipPath);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("更新包校验失败，当前版本未做任何修改。");
                    }

                    string updateId = Guid.NewGuid().ToString("N");
                    stageDirectory = installDirectory + ".__stage." + updateId;
                    string backupDirectory = installDirectory + ".__backup";
                    ExtractZipSafely(zipPath, stageDirectory);
                    PreserveUserFiles(installDirectory, stageDirectory);
                    ValidateStagedRelease(stageDirectory, latestVersion);

                    StartUpdateHelper(
                        helperPath,
                        "Release",
                        installDirectory,
                        stageDirectory,
                        backupDirectory,
                        null,
                        workDirectory
                    );
                }

                handedOff = true;
                Application.Exit();
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    owner,
                    "检查更新失败：" + Environment.NewLine + Environment.NewLine + exception.Message,
                    "CLI List 更新",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
            finally
            {
                if (!handedOff)
                {
                    TryDeleteDirectory(stageDirectory);
                    TryDeleteDirectory(workDirectory);
                }
            }
        }

        private static Dictionary<string, object> ReadLatestRelease()
        {
            try
            {
                string json;
                using (var client = CreateWebClient())
                {
                    json = client.DownloadString(LatestReleaseUrl);
                }

                var serializer = new JavaScriptSerializer();
                Dictionary<string, object> release = serializer.Deserialize<Dictionary<string, object>>(json);
                if (release == null)
                {
                    throw new InvalidDataException("GitHub Release 返回内容无效。");
                }
                return release;
            }
            catch (System.Net.WebException)
            {
                return ReadLatestReleaseWithoutApi();
            }
        }

        private static Dictionary<string, object> ReadLatestReleaseWithoutApi()
        {
            var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                "https://github.com/gsyIsWatchingU/cli-list/releases/latest"
            );
            request.AllowAutoRedirect = true;
            request.UserAgent = "cli-list/" + AppInfo.Version;
            request.Timeout = 60000;

            string tagName;
            using (var response = (System.Net.HttpWebResponse)request.GetResponse())
            {
                string marker = "/releases/tag/";
                string path = response.ResponseUri.AbsolutePath;
                int markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    throw new InvalidDataException("无法从 GitHub Release 页面识别最新版本。");
                }
                tagName = Uri.UnescapeDataString(path.Substring(markerIndex + marker.Length)).Trim('/');
            }

            string assetName = "cli-list-" + tagName + ".zip";
            string downloadUrl = "https://github.com/gsyIsWatchingU/cli-list/releases/download/" +
                Uri.EscapeDataString(tagName) + "/" + Uri.EscapeDataString(assetName);
            var asset = new Dictionary<string, object>
            {
                { "name", assetName },
                { "state", "uploaded" },
                { "size", 0 },
                { "browser_download_url", downloadUrl }
            };
            return new Dictionary<string, object>
            {
                { "tag_name", tagName },
                { "assets", new System.Collections.ArrayList { asset } }
            };
        }

        private static TimeoutWebClient CreateWebClient()
        {
            var client = new TimeoutWebClient(60000);
            client.Headers.Add("User-Agent", "cli-list/" + AppInfo.Version);
            client.Headers.Add("Accept", "application/vnd.github+json");
            client.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        private static Dictionary<string, object> FindReleaseAsset(Dictionary<string, object> release, string tagName)
        {
            object assetsValue;
            var assets = release.TryGetValue("assets", out assetsValue)
                ? assetsValue as System.Collections.ArrayList
                : null;
            if (assets == null)
            {
                throw new InvalidDataException("GitHub Release 缺少安装包列表。");
            }

            string expectedName = "cli-list-" + tagName + ".zip";
            var matches = new List<Dictionary<string, object>>();
            foreach (object item in assets)
            {
                var asset = item as Dictionary<string, object>;
                if (asset == null)
                {
                    continue;
                }

                string name;
                object nameValue;
                object stateValue;
                if (asset.TryGetValue("name", out nameValue) &&
                    asset.TryGetValue("state", out stateValue))
                {
                    name = Convert.ToString(nameValue, CultureInfo.InvariantCulture);
                    string state = Convert.ToString(stateValue, CultureInfo.InvariantCulture);
                    if (string.Equals(name, expectedName, StringComparison.Ordinal) &&
                        string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(asset);
                    }
                }
            }

            if (matches.Count != 1)
            {
                throw new InvalidDataException("Release 中未找到唯一的安装包：" + expectedName);
            }
            return matches[0];
        }

        private static string GetRequiredString(Dictionary<string, object> values, string key, string errorMessage)
        {
            object value;
            string result = values.TryGetValue(key, out value)
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidDataException(errorMessage);
            }
            return result;
        }

        private static string ComputeSha256(string filePath)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(filePath))
            {
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string GetExpectedHash(
            Dictionary<string, object> asset,
            string downloadUrl,
            string workDirectory)
        {
            object digestValue;
            string digest = asset.TryGetValue("digest", out digestValue)
                ? Convert.ToString(digestValue, CultureInfo.InvariantCulture)
                : null;
            if (!string.IsNullOrWhiteSpace(digest) &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(digest.Substring("sha256:".Length), "^[a-fA-F0-9]{64}$"))
            {
                return digest.Substring("sha256:".Length);
            }

            string checksumPath = Path.Combine(workDirectory, "update.zip.sha256");
            using (var client = CreateWebClient())
            {
                client.DownloadFile(downloadUrl + ".sha256", checksumPath);
            }
            string[] checksumParts = File.ReadAllText(checksumPath)
                .Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string checksum = checksumParts.Length > 0 ? checksumParts[0] : string.Empty;
            if (!Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$"))
            {
                throw new InvalidDataException("Release 安装包的 SHA-256 校验文件无效。");
            }
            return checksum;
        }

        private static void ExtractZipSafely(string zipPath, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            string destinationRoot = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            int fileCount = 0;
            long totalSize = 0;

            using (FileStream stream = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entry.FullName));
                    if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("更新包包含不安全的文件路径。");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    fileCount++;
                    totalSize += entry.Length;
                    if (fileCount > 1000 || totalSize > 200 * 1024 * 1024)
                    {
                        throw new InvalidDataException("更新包解压内容超出安全限制。");
                    }

                    string parentDirectory = Path.GetDirectoryName(destinationPath);
                    if (!Directory.Exists(parentDirectory))
                    {
                        Directory.CreateDirectory(parentDirectory);
                    }
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static void PreserveUserFiles(string installDirectory, string stageDirectory)
        {
            foreach (string fileName in ProtectedFileNames)
            {
                string sourcePath = Path.Combine(installDirectory, fileName);
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, Path.Combine(stageDirectory, fileName), true);
                }
            }
        }

        private static void ValidateStagedRelease(string stageDirectory, Version expectedVersion)
        {
            foreach (string fileName in new[] { "CLIList.exe", "commands.json", "cli-list.ico", "update-helper.ps1" })
            {
                if (!File.Exists(Path.Combine(stageDirectory, fileName)))
                {
                    throw new InvalidDataException("更新包缺少必要文件：" + fileName);
                }
            }

            string executablePath = Path.Combine(stageDirectory, "CLIList.exe");
            Version packageVersion = AssemblyName.GetAssemblyName(executablePath).Version;
            if (packageVersion == null || packageVersion.ToString(3) != expectedVersion.ToString(3))
            {
                throw new InvalidDataException("更新包内程序版本与 Release 版本不一致。");
            }

            using (Process validation = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--validate",
                WorkingDirectory = stageDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                if (validation == null || !validation.WaitForExit(30000))
                {
                    if (validation != null)
                    {
                        validation.Kill();
                    }
                    throw new InvalidDataException("更新包验证超时。");
                }
                if (validation.ExitCode != 0)
                {
                    throw new InvalidDataException("更新包验证失败，退出码：" + validation.ExitCode);
                }
            }
        }

        private static void StartUpdateHelper(
            string helperPath,
            string mode,
            string installDirectory,
            string stageDirectory,
            string backupDirectory,
            string sourceDirectory,
            string workDirectory)
        {
            var arguments = new StringBuilder();
            arguments.Append("-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ").Append(QuoteArgument(helperPath));
            arguments.Append(" -Mode ").Append(mode);
            arguments.Append(" -CurrentDirectory ").Append(QuoteArgument(installDirectory));
            arguments.Append(" -CurrentProcessId ").Append(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            arguments.Append(" -WorkDirectory ").Append(QuoteArgument(workDirectory));
            if (!string.IsNullOrWhiteSpace(stageDirectory))
            {
                arguments.Append(" -StageDirectory ").Append(QuoteArgument(stageDirectory));
            }
            if (!string.IsNullOrWhiteSpace(backupDirectory))
            {
                arguments.Append(" -BackupDirectory ").Append(QuoteArgument(backupDirectory));
            }
            if (!string.IsNullOrWhiteSpace(sourceDirectory))
            {
                arguments.Append(" -SourceDirectory ").Append(QuoteArgument(sourceDirectory));
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments.ToString(),
                WorkingDirectory = workDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void TryDeleteDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                }
            }
            catch
            {
                // 清理失败不覆盖原始更新错误。
            }
        }

        private sealed class TimeoutWebClient : System.Net.WebClient
        {
            private readonly int timeoutMilliseconds;

            public TimeoutWebClient(int timeoutMilliseconds)
            {
                this.timeoutMilliseconds = timeoutMilliseconds;
            }

            protected override System.Net.WebRequest GetWebRequest(Uri address)
            {
                System.Net.WebRequest request = base.GetWebRequest(address);
                request.Timeout = timeoutMilliseconds;
                return request;
            }
        }
    }

    internal sealed class ResidentApplicationContext : ApplicationContext
    {
        private const int ShowWindowRestore = 9;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, IntPtr processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, bool attach);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr windowHandle);

        private readonly string appDirectory;
        private readonly string configPath;
        private readonly string usagePath;
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly ToolStripMenuItem updateItem;
        private readonly Control dispatcher;
        private readonly GlobalHotKeyWindow hotKeyWindow;
        private readonly Thread pipeThread;
        private volatile bool isExiting;
        private MainForm mainForm;
        private string currentContext;
        private string availableUpdateTag;

        public ResidentApplicationContext(
            string appDirectory,
            string configPath,
            string usagePath,
            string initialContext,
            bool initiallyVisible
        )
        {
            this.appDirectory = appDirectory;
            this.configPath = configPath;
            this.usagePath = usagePath;
            currentContext = Program.ResolveContextDirectory(initialContext);

            dispatcher = new Control();
            IntPtr dispatcherHandle = dispatcher.Handle;

            var openItem = new ToolStripMenuItem("打开 CLI List    Ctrl+Alt+Space");
            openItem.Click += (sender, eventArgs) => ShowCommandPanel(null, false);
            var versionItem = new ToolStripMenuItem(AppInfo.DisplayName);
            versionItem.Enabled = false;
            updateItem = new ToolStripMenuItem("检查更新…");
            updateItem.Click += (sender, eventArgs) => UpdateManager.CheckForUpdate(null, appDirectory);
            var uninstallItem = new ToolStripMenuItem("卸载 CLI List…");
            uninstallItem.Click += (sender, eventArgs) => UninstallFromTray();
            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (sender, eventArgs) => ExitThread();

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add(openItem);
            trayMenu.Items.Add(versionItem);
            trayMenu.Items.Add(updateItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(uninstallItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(exitItem);

            string iconPath = Path.Combine(appDirectory, "cli-list.ico");
            trayIcon = new NotifyIcon
            {
                Text = "CLI List · Ctrl+Alt+Space",
                Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (sender, eventArgs) => ShowCommandPanel(null, false);

            hotKeyWindow = new GlobalHotKeyWindow(() => ShowCommandPanel(null, true));
            if (!hotKeyWindow.Registered)
            {
                trayIcon.ShowBalloonTip(
                    4000,
                    "CLI List 快捷键不可用",
                    "Ctrl+Alt+Space 已被其他程序占用，可通过托盘图标打开。",
                    ToolTipIcon.Warning
                );
            }

            pipeThread = new Thread(ListenForContexts)
            {
                IsBackground = true,
                Name = "CLI List context pipe"
            };
            pipeThread.Start();

            if (initiallyVisible)
            {
                ShowCommandPanel(initialContext, false);
            }

            UpdateManager.BeginSilentCheck(tagName =>
            {
                if (isExiting || dispatcher.IsDisposed)
                {
                    return;
                }
                dispatcher.BeginInvoke((MethodInvoker)(() => ShowAvailableUpdate(tagName)));
            });
        }

        private void ShowAvailableUpdate(string tagName)
        {
            availableUpdateTag = tagName;
            updateItem.Text = "↑ 更新到 " + tagName;
            updateItem.Visible = true;
            if (mainForm != null && !mainForm.IsDisposed)
            {
                mainForm.ShowAvailableUpdate(tagName);
            }
        }

        internal static bool TryForwardToResident(string contextPath, bool showPanel, int timeoutMilliseconds)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", Program.ResidentPipeName, PipeDirection.Out))
                {
                    client.Connect(timeoutMilliseconds);
                    string encodedContext = Convert.ToBase64String(Encoding.UTF8.GetBytes(contextPath ?? string.Empty));
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.AutoFlush = true;
                        writer.WriteLine((showPanel ? "1|" : "0|") + encodedContext);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ListenForContexts()
        {
            while (!isExiting)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(
                        Program.ResidentPipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None
                    ))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8))
                        {
                            string message = reader.ReadLine();
                            if (isExiting || string.IsNullOrWhiteSpace(message))
                            {
                                continue;
                            }

                            int separatorIndex = message.IndexOf('|');
                            bool showPanel = separatorIndex > 0 && message.Substring(0, separatorIndex) == "1";
                            string encodedContext = separatorIndex >= 0 ? message.Substring(separatorIndex + 1) : string.Empty;
                            string contextPath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedContext));
                            if (showPanel)
                            {
                                dispatcher.BeginInvoke((MethodInvoker)(() => ShowCommandPanel(contextPath, false)));
                            }
                        }
                    }
                }
                catch
                {
                    if (!isExiting)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private void ShowCommandPanel(string requestedContext, bool toggle)
        {
            if (!string.IsNullOrWhiteSpace(requestedContext))
            {
                currentContext = Program.ResolveContextDirectory(requestedContext);
            }
            string contextPath = currentContext;
            if (mainForm != null && !mainForm.IsDisposed &&
                !string.Equals(mainForm.ContextPath, contextPath, StringComparison.OrdinalIgnoreCase))
            {
                MainForm previousForm = mainForm;
                mainForm = null;
                previousForm.Close();
                previousForm.Dispose();
            }

            if (mainForm == null || mainForm.IsDisposed)
            {
                try
                {
                    mainForm = Program.CreateMainForm(appDirectory, configPath, usagePath, contextPath);
                    mainForm.FormClosed += (sender, eventArgs) => mainForm = null;
                    if (!string.IsNullOrWhiteSpace(availableUpdateTag))
                    {
                        mainForm.ShowAvailableUpdate(availableUpdateTag);
                    }
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        "CLI List 无法打开：\n\n" + exception.Message,
                        "CLI List",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return;
                }
            }

            if (toggle && mainForm.Visible)
            {
                mainForm.Hide();
                return;
            }

            if (!mainForm.Visible)
            {
                mainForm.Show();
            }
            if (mainForm.WindowState == FormWindowState.Minimized)
            {
                mainForm.WindowState = FormWindowState.Normal;
            }
            ActivateCommandPanel(mainForm);
        }

        private static void ActivateCommandPanel(Form form)
        {
            IntPtr formHandle = form.Handle;
            IntPtr foregroundHandle = GetForegroundWindow();
            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = foregroundHandle == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundHandle, IntPtr.Zero);
            bool attached = false;

            try
            {
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                ShowWindowAsync(formHandle, ShowWindowRestore);
                BringWindowToTop(formHandle);
                SetForegroundWindow(formHandle);
                SetFocus(formHandle);
                form.Activate();
                form.BringToFront();
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }

        private void UninstallFromTray()
        {
            DialogResult result = MessageBox.Show(
                "确定要卸载 CLI List 吗？" + Environment.NewLine + Environment.NewLine +
                "将移除右键菜单、开机启动和桌面快捷方式，并删除安装目录中的程序文件。",
                "卸载 CLI List",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                Installer.Uninstall();
            }
            catch (Exception exception)
            {
                MessageBox.Show("卸载失败：\n\n" + exception.Message, "卸载 CLI List", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            isExiting = true;
            TryForwardToResident(string.Empty, false, 200);
            if (pipeThread != null && pipeThread.IsAlive)
            {
                pipeThread.Join(500);
            }

            if (mainForm != null && !mainForm.IsDisposed)
            {
                MainForm closingForm = mainForm;
                mainForm = null;
                closingForm.Close();
                closingForm.Dispose();
            }

            hotKeyWindow.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
            dispatcher.Dispose();
            base.ExitThreadCore();
        }
    }

    internal sealed class GlobalHotKeyWindow : NativeWindow, IDisposable
    {
        private const int HotKeyId = 0x434C;
        private const int WmHotKey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private readonly Action onPressed;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

        public GlobalHotKeyWindow(Action onPressed)
        {
            this.onPressed = onPressed;
            CreateHandle(new CreateParams());
            Registered = RegisterHotKey(Handle, HotKeyId, ModControl | ModAlt | ModNoRepeat, (uint)Keys.Space);
        }

        public bool Registered { get; private set; }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotKey && message.WParam.ToInt32() == HotKeyId)
            {
                onPressed();
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Registered)
            {
                UnregisterHotKey(Handle, HotKeyId);
                Registered = false;
            }
            DestroyHandle();
        }
    }

    internal sealed class AiCommandPatchPlan
    {
        public string Fingerprint { get; set; }
        public List<CommandItem> LocalCommands { get; set; }
        public List<AiCommandPatchPreviewItem> PreviewItems { get; set; }

        public List<string> PreviewLines
        {
            get { return PreviewItems.Select(item => item.ToPreviewLine()).ToList(); }
        }
    }

    internal sealed class AiCommandVersion
    {
        public string SavedAtUtc { get; set; }
        public string Summary { get; set; }
        public List<CommandItem> Commands { get; set; }
    }

    internal sealed class AiCommandVersionInfo
    {
        public string FilePath { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public string Summary { get; set; }
        public int CommandCount { get; set; }
    }

    internal sealed class AiCommandPatchPreviewItem
    {
        public string OperationLabel { get; set; }
        public string Detail { get; set; }
        public string FixedName { get; set; }
        public CommandItem EditableCommand { get; set; }

        public bool CanEditName
        {
            get { return EditableCommand != null; }
        }

        public string Name
        {
            get { return CanEditName ? EditableCommand.Name : FixedName; }
        }

        public string ToPreviewLine()
        {
            string line = OperationLabel + "：“" + Name + "”";
            return string.IsNullOrWhiteSpace(Detail) ? line : line + " · " + Detail;
        }
    }

    internal static class AiCommandPatchService
    {
        internal const int UserRequestMaxLength = 300;
        internal const int AiResponseMaxLength = 20000;
        internal const string IdeCliPickerPresetId = "ide-cli-picker";
        internal const int MaxVersions = 5;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly HashSet<string> EditableFields = new HashSet<string>(new[]
        {
            "Name", "Description", "Tags", "Executable", "Arguments", "WorkingDirectory", "CloseAfterLaunch"
        }, StringComparer.Ordinal);
        private static readonly HashSet<string> PresetEditableFields = new HashSet<string>(new[]
        {
            "Name", "Description", "Tags"
        }, StringComparer.Ordinal);

        public static string ComputeFingerprint(string sharedConfigPath, string localConfigPath)
        {
            string shared = File.Exists(sharedConfigPath) ? File.ReadAllText(sharedConfigPath) : string.Empty;
            string local = File.Exists(localConfigPath) ? File.ReadAllText(localConfigPath) : "<missing>";
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(shared + "\n--LOCAL--\n" + local));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static bool MigrateLegacyIdeCliCommand(string sharedConfigPath, string localConfigPath)
        {
            if (!File.Exists(localConfigPath))
            {
                return false;
            }

            string originalJson = File.ReadAllText(localConfigPath);
            List<CommandItem> commands = ReadLocalCommands(localConfigPath);
            bool changed = false;
            foreach (CommandItem command in commands)
            {
                if (!IsMissingLegacyIdeCliCommand(command, Path.GetDirectoryName(localConfigPath)))
                {
                    continue;
                }

                command.Action = "ChooseIdeOrCli";
                command.Executable = null;
                command.Arguments = string.Empty;
                command.WorkingDirectory = string.Empty;
                command.CloseAfterLaunch = false;
                changed = true;
            }
            if (!changed)
            {
                return false;
            }

            ValidateProspective(sharedConfigPath, commands);
            string migrationBackupPath = localConfigPath + ".pre-ide-cli-picker.bak";
            if (!File.Exists(migrationBackupPath))
            {
                File.WriteAllText(migrationBackupPath, originalJson, new UTF8Encoding(false));
            }
            SaveLocalCommands(localConfigPath, commands, false);
            return true;
        }

        public static string BuildPrompt(string request, string sharedConfigPath)
        {
            if (string.IsNullOrWhiteSpace(request))
            {
                throw new InvalidDataException("请先描述要新增、修改或隐藏哪些命令。");
            }
            if (request.Trim().Length > UserRequestMaxLength)
            {
                throw new InvalidDataException("需求描述不能超过 " + UserRequestMaxLength + " 字。");
            }

            List<CommandItem> commands = Program.LoadCommands(sharedConfigPath)
                .Where(command => !string.Equals(command.Action, "CheckUpdate", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var visibleCommands = new List<Dictionary<string, object>>();
            foreach (CommandItem command in commands)
            {
                var item = new Dictionary<string, object>();
                item["Id"] = command.Id;
                item["Name"] = Redact(command.Name);
                item["Description"] = Redact(command.Description);
                item["Tags"] = command.Tags ?? new List<string>();
                if (!string.IsNullOrWhiteSpace(command.Action))
                {
                    item["Action"] = command.Action;
                }
                else
                {
                    item["Executable"] = Redact(command.Executable);
                    item["Arguments"] = Redact(command.Arguments);
                    item["WorkingDirectory"] = Redact(command.WorkingDirectory);
                    item["CloseAfterLaunch"] = command.CloseAfterLaunch;
                }
                visibleCommands.Add(item);
            }

            var prompt = new StringBuilder();
            prompt.AppendLine("你是 CLI List 的命令编辑助手。请把用户需求转换成最小增量，不要返回完整配置。");
            prompt.AppendLine();
            prompt.AppendLine("用户需求：");
            prompt.AppendLine(request.Trim());
            prompt.AppendLine();
            prompt.AppendLine("当前命令（仅用于定位，可能已隐藏敏感片段）：");
            prompt.AppendLine(Serializer.Serialize(visibleCommands));
            prompt.AppendLine();
            prompt.AppendLine("只输出一个 JSON 对象，不要解释、不要 Markdown。格式必须是：");
            prompt.AppendLine("{\"version\":1,\"ops\":[{\"op\":\"add\",\"fields\":{...}},{\"op\":\"add\",\"preset\":\"ide-cli-picker\",\"fields\":{...}},{\"op\":\"update\",\"id\":\"现有Id\",\"fields\":{...}},{\"op\":\"delete\",\"id\":\"现有Id\"}]}");
            prompt.AppendLine("规则：只保留必要操作，最多 10 条；add 不要提供 Id；update/delete 必须使用现有 Id；delete 表示从面板隐藏；不要修改 Id、Action、Disabled。");
            prompt.AppendLine("普通 add 的 fields 只允许 Name、Description、Tags、Executable、Arguments、WorkingDirectory、CloseAfterLaunch，并且必须包含 Name 和 Executable。内置 Action 命令只能修改 Name、Description、Tags。");
            prompt.AppendLine("当用户要新增“选择 IDE 或 CLI”能力时，必须使用 preset=ide-cli-picker；其 fields 只允许 Name、Description、Tags。不要引用或虚构本机 .vbs、.ps1、.cmd、.bat 文件。");
            return prompt.ToString().Trim();
        }

        public static AiCommandPatchPlan Prepare(
            string response,
            string sharedConfigPath,
            string localConfigPath,
            string expectedFingerprint)
        {
            if ((response ?? string.Empty).Length > AiResponseMaxLength)
            {
                throw new InvalidDataException("AI 返回内容不能超过 " + AiResponseMaxLength + " 字。");
            }
            string currentFingerprint = ComputeFingerprint(sharedConfigPath, localConfigPath);
            if (!string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("命令配置已发生变化，请返回第一步重新复制提示词。");
            }

            Dictionary<string, object> root = ParseRoot(response);
            EnsureKeys(root, new[] { "version", "ops" }, "根对象");
            if (Convert.ToInt32(root["version"], CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException("只支持 version=1 的变更指令。");
            }

            List<object> operations = AsList(root["ops"], "ops");
            if (operations.Count < 1 || operations.Count > 10)
            {
                throw new InvalidDataException("每次必须包含 1 到 10 条增量操作。");
            }

            List<CommandItem> merged = Program.LoadCommands(sharedConfigPath);
            List<CommandItem> local = ReadLocalCommands(localConfigPath);
            var preview = new List<AiCommandPatchPreviewItem>();
            var touchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object operationValue in operations)
            {
                var operation = operationValue as Dictionary<string, object>;
                if (operation == null)
                {
                    throw new InvalidDataException("ops 中的每一项都必须是对象。");
                }

                string operationName = RequiredString(operation, "op", 20).ToLowerInvariant();
                if (operationName == "add")
                {
                    object presetValue;
                    string presetId = operation.TryGetValue("preset", out presetValue) ? presetValue as string : null;
                    bool usesPreset = !string.IsNullOrWhiteSpace(presetId);
                    EnsureKeys(operation, usesPreset ? new[] { "op", "preset", "fields" } : new[] { "op", "fields" }, "add 操作");
                    Dictionary<string, object> fields = RequiredFields(operation);
                    if (usesPreset)
                    {
                        if (!string.Equals(presetId, IdeCliPickerPresetId, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("不支持的内置能力 preset：" + presetId);
                        }
                        ValidatePresetFieldKeys(fields);
                        if (merged.Any(command => string.Equals(command.Action, "ChooseIdeOrCli", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidDataException("“选择 IDE 或 CLI”命令已经存在，无需重复新增。");
                        }
                    }
                    else
                    {
                        ValidateFieldKeys(fields);
                        if (!fields.ContainsKey("Name") || !fields.ContainsKey("Executable"))
                        {
                            throw new InvalidDataException("普通 add 必须包含 Name 和 Executable。");
                        }
                    }

                    string id;
                    do
                    {
                        id = "local-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                    }
                    while (merged.Any(command => string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                           local.Any(command => string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase)));

                    var added = usesPreset
                        ? new CommandItem
                        {
                            Id = id,
                            Name = "选择 IDE 或 CLI",
                            Description = "选择 Codex、WorkBuddy、TRAE 或 Claude Code",
                            Tags = new List<string> { "开发", "工具" },
                            Action = "ChooseIdeOrCli",
                            CloseAfterLaunch = false
                        }
                        : new CommandItem
                        {
                            Id = id,
                            Tags = new List<string>(),
                            Arguments = string.Empty,
                            WorkingDirectory = "{context}",
                            CloseAfterLaunch = true
                        };
                    ApplyFields(added, fields);
                    if (!usesPreset)
                    {
                        ValidateGeneratedCommandReferences(added, sharedConfigPath);
                    }
                    local.Add(added);
                    merged.Add(added);
                    preview.Add(new AiCommandPatchPreviewItem
                    {
                        OperationLabel = "新增",
                        Detail = usesPreset ? "内置 IDE/CLI 选择器，名称可编辑" : "名称可编辑，其他配置保持不变",
                        EditableCommand = added
                    });
                    continue;
                }

                if (operationName != "update" && operationName != "delete")
                {
                    throw new InvalidDataException("op 只允许 add、update 或 delete。");
                }

                string idValue = RequiredString(operation, "id", 160);
                if (!touchedIds.Add(idValue))
                {
                    throw new InvalidDataException("同一个 Id 每次只能修改一次：" + idValue);
                }
                CommandItem target = merged.FirstOrDefault(command =>
                    string.Equals(command.Id, idValue, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    throw new InvalidDataException("找不到要修改的命令 Id：" + idValue);
                }

                int localIndex = local.FindIndex(command =>
                    string.Equals(command.Id, idValue, StringComparison.OrdinalIgnoreCase));
                if (operationName == "delete")
                {
                    EnsureKeys(operation, new[] { "op", "id" }, "delete 操作");
                    var disabled = new CommandItem { Id = target.Id, Disabled = true };
                    if (localIndex >= 0)
                    {
                        local[localIndex] = disabled;
                    }
                    else
                    {
                        local.Add(disabled);
                    }
                    merged.Remove(target);
                    preview.Add(new AiCommandPatchPreviewItem
                    {
                        OperationLabel = "隐藏",
                        Detail = "将从命令面板隐藏",
                        FixedName = target.Name
                    });
                    continue;
                }

                EnsureKeys(operation, new[] { "op", "id", "fields" }, "update 操作");
                Dictionary<string, object> updateFields = RequiredFields(operation);
                ValidateFieldKeys(updateFields);
                if (updateFields.Count == 0)
                {
                    throw new InvalidDataException("update 的 fields 不能为空。");
                }
                if (!string.IsNullOrWhiteSpace(target.Action) && updateFields.Keys.Any(key =>
                    key != "Name" && key != "Description" && key != "Tags"))
                {
                    throw new InvalidDataException("内置命令只能修改 Name、Description、Tags。");
                }

                CommandItem updated = Clone(target);
                ApplyFields(updated, updateFields);
                if (updateFields.Keys.Any(key => key == "Executable" || key == "Arguments" || key == "WorkingDirectory"))
                {
                    ValidateGeneratedCommandReferences(updated, sharedConfigPath);
                }
                if (localIndex >= 0)
                {
                    local[localIndex] = updated;
                }
                else
                {
                    local.Add(updated);
                }
                int mergedIndex = merged.IndexOf(target);
                merged[mergedIndex] = updated;
                preview.Add(new AiCommandPatchPreviewItem
                {
                    OperationLabel = "修改",
                    Detail = "修改字段：" + string.Join("、", updateFields.Keys.Select(FieldDisplayName)),
                    EditableCommand = updated
                });
            }

            ValidateProspective(sharedConfigPath, local);
            return new AiCommandPatchPlan
            {
                Fingerprint = expectedFingerprint,
                LocalCommands = local,
                PreviewItems = preview
            };
        }

        public static void UpdatePreviewNames(AiCommandPatchPlan plan, IDictionary<int, string> names)
        {
            if (plan == null)
            {
                throw new ArgumentNullException("plan");
            }
            if (names == null)
            {
                throw new ArgumentNullException("names");
            }

            List<int> editableIndexes = plan.PreviewItems
                .Select((item, index) => new { Item = item, Index = index })
                .Where(value => value.Item.CanEditName)
                .Select(value => value.Index)
                .ToList();
            if (names.Count != editableIndexes.Count || editableIndexes.Any(index => !names.ContainsKey(index)))
            {
                throw new InvalidDataException("名称编辑项与本次增量修改不一致，请重新预览。");
            }

            var normalizedNames = new Dictionary<int, string>();
            foreach (int index in editableIndexes)
            {
                normalizedNames[index] = ValueString(names[index], "名称", 80);
            }
            foreach (KeyValuePair<int, string> pair in normalizedNames)
            {
                plan.PreviewItems[pair.Key].EditableCommand.Name = pair.Value;
            }
        }

        public static void Apply(AiCommandPatchPlan plan, string sharedConfigPath, string localConfigPath)
        {
            if (plan == null)
            {
                throw new ArgumentNullException("plan");
            }
            if (!string.Equals(ComputeFingerprint(sharedConfigPath, localConfigPath), plan.Fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("预览后命令配置发生了变化，本次修改未应用。");
            }

            ValidateProspective(sharedConfigPath, plan.LocalCommands);
            SaveLocalCommands(localConfigPath, plan.LocalCommands, true);
        }

        public static bool CanUndo(string localConfigPath)
        {
            return File.Exists(GetBackupPath(localConfigPath));
        }

        public static void Undo(string sharedConfigPath, string localConfigPath)
        {
            string backupPath = GetBackupPath(localConfigPath);
            if (!File.Exists(backupPath))
            {
                throw new InvalidOperationException("没有可恢复的上一次 AI 修改。");
            }
            List<CommandItem> previous = Serializer.Deserialize<List<CommandItem>>(File.ReadAllText(backupPath));
            previous = previous ?? new List<CommandItem>();
            ValidateProspective(sharedConfigPath, previous);
            SaveLocalCommands(localConfigPath, previous, false);
            File.Delete(backupPath);
        }

        private static Dictionary<string, object> ParseRoot(string response)
        {
            string json = (response ?? string.Empty).Trim();
            Match fenced = Regex.Match(json, @"^```(?:json)?\s*(\{[\s\S]*\})\s*```$", RegexOptions.IgnoreCase);
            if (fenced.Success)
            {
                json = fenced.Groups[1].Value;
            }
            else if (!json.StartsWith("{", StringComparison.Ordinal) || !json.EndsWith("}", StringComparison.Ordinal))
            {
                throw new InvalidDataException("AI 返回内容必须是纯 JSON 对象，不能包含解释文字。");
            }

            Dictionary<string, object> root;
            try
            {
                root = Serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("无法解析 AI 返回的 JSON：" + exception.Message);
            }
            if (root == null)
            {
                throw new InvalidDataException("AI 返回内容必须是 JSON 对象。");
            }
            return root;
        }

        private static List<CommandItem> ReadLocalCommands(string localConfigPath)
        {
            if (!File.Exists(localConfigPath))
            {
                return new List<CommandItem>();
            }
            List<CommandItem> commands = Serializer.Deserialize<List<CommandItem>>(File.ReadAllText(localConfigPath));
            return commands ?? new List<CommandItem>();
        }

        private static void ValidateProspective(string sharedConfigPath, List<CommandItem> localCommands)
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), "cli-list-ai-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                string tempSharedPath = Path.Combine(tempDirectory, "commands.json");
                File.Copy(sharedConfigPath, tempSharedPath, true);
                File.WriteAllText(
                    Path.Combine(tempDirectory, "commands.local.json"),
                    Serializer.Serialize(localCommands),
                    new UTF8Encoding(false)
                );
                Program.LoadCommands(tempSharedPath);
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
        }

        private static void SaveLocalCommands(string localConfigPath, List<CommandItem> commands, bool createBackup)
        {
            string directory = Path.GetDirectoryName(localConfigPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (createBackup)
            {
                string previous = File.Exists(localConfigPath) ? File.ReadAllText(localConfigPath) : "[]";
                File.WriteAllText(GetBackupPath(localConfigPath), previous, new UTF8Encoding(false));
            }

            string temporaryPath = localConfigPath + ".ai-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporaryPath, Serializer.Serialize(commands), new UTF8Encoding(false));
            try
            {
                if (File.Exists(localConfigPath))
                {
                    try
                    {
                        File.Replace(temporaryPath, localConfigPath, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporaryPath, localConfigPath, true);
                        File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                        File.Copy(temporaryPath, localConfigPath, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, localConfigPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static string GetBackupPath(string localConfigPath)
        {
            return localConfigPath + ".ai-last.bak";
        }

        private static Dictionary<string, object> RequiredFields(Dictionary<string, object> operation)
        {
            object value;
            var fields = operation.TryGetValue("fields", out value) ? value as Dictionary<string, object> : null;
            if (fields == null)
            {
                throw new InvalidDataException("fields 必须是对象。");
            }
            return fields;
        }

        private static void ValidateFieldKeys(Dictionary<string, object> fields)
        {
            string unknown = fields.Keys.FirstOrDefault(key => !EditableFields.Contains(key));
            if (unknown != null)
            {
                throw new InvalidDataException("fields 不允许包含字段：" + unknown);
            }
        }

        private static void ValidatePresetFieldKeys(Dictionary<string, object> fields)
        {
            string unknown = fields.Keys.FirstOrDefault(key => !PresetEditableFields.Contains(key));
            if (unknown != null)
            {
                throw new InvalidDataException("preset fields 不允许包含字段：" + unknown);
            }
        }

        private static void ValidateGeneratedCommandReferences(CommandItem command, string sharedConfigPath)
        {
            string appDirectory = Path.GetDirectoryName(sharedConfigPath);
            string executable = ExpandKnownPath(command.Executable, appDirectory);
            if (!string.IsNullOrWhiteSpace(executable) && Path.IsPathRooted(executable) && !File.Exists(executable))
            {
                throw new InvalidDataException("AI 提议的程序不存在，因此没有应用本次修改：" + executable);
            }

            string arguments = command.Arguments ?? string.Empty;
            var references = new List<string>();
            foreach (Match match in Regex.Matches(arguments, "\"(?<path>[^\"]+\\.(?:vbs|ps1|cmd|bat))\"", RegexOptions.IgnoreCase))
            {
                references.Add(match.Groups["path"].Value);
            }
            foreach (Match match in Regex.Matches(arguments, @"(?<path>(?:%[^%]+%|[A-Za-z]:\\|\{appdir\})[^\s\x22]*\.(?:vbs|ps1|cmd|bat))", RegexOptions.IgnoreCase))
            {
                references.Add(match.Groups["path"].Value);
            }

            foreach (string reference in references.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string expanded = ExpandKnownPath(reference, appDirectory);
                if (string.IsNullOrWhiteSpace(expanded) || expanded.IndexOf("{context}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                if (!Path.IsPathRooted(expanded))
                {
                    expanded = Path.GetFullPath(Path.Combine(appDirectory, expanded));
                }
                if (!File.Exists(expanded))
                {
                    string suffix = expanded.EndsWith("ide-cli-launcher.vbs", StringComparison.OrdinalIgnoreCase)
                        ? "。请使用 preset=ide-cli-picker 创建内置选择器"
                        : string.Empty;
                    throw new InvalidDataException("AI 提议引用的本机文件不存在，因此没有应用本次修改：" + expanded + suffix);
                }
            }
        }

        private static bool IsMissingLegacyIdeCliCommand(CommandItem command, string appDirectory)
        {
            if (command == null || !string.IsNullOrWhiteSpace(command.Action) ||
                string.IsNullOrWhiteSpace(command.Arguments) ||
                command.Arguments.IndexOf("ide-cli-launcher.vbs", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            Match match = Regex.Match(command.Arguments, "\"(?<path>[^\"]*ide-cli-launcher\\.vbs)\"", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return true;
            }
            string path = ExpandKnownPath(match.Groups["path"].Value, appDirectory);
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path);
        }

        private static string ExpandKnownPath(string value, string appDirectory)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return Environment.ExpandEnvironmentVariables(value)
                .Replace("{appdir:q}", appDirectory)
                .Replace("{appdir}", appDirectory)
                .Trim().Trim('"');
        }

        private static void ApplyFields(CommandItem command, Dictionary<string, object> fields)
        {
            foreach (KeyValuePair<string, object> pair in fields)
            {
                if (pair.Key == "Name") command.Name = ValueString(pair.Value, "Name", 80);
                else if (pair.Key == "Description") command.Description = ValueString(pair.Value, "Description", 300);
                else if (pair.Key == "Executable") command.Executable = ValueString(pair.Value, "Executable", 2048);
                else if (pair.Key == "Arguments") command.Arguments = ValueString(pair.Value, "Arguments", 4096, true);
                else if (pair.Key == "WorkingDirectory") command.WorkingDirectory = ValueString(pair.Value, "WorkingDirectory", 2048, true);
                else if (pair.Key == "CloseAfterLaunch")
                {
                    if (!(pair.Value is bool)) throw new InvalidDataException("CloseAfterLaunch 必须是布尔值。");
                    command.CloseAfterLaunch = (bool)pair.Value;
                }
                else if (pair.Key == "Tags")
                {
                    List<object> values = AsList(pair.Value, "Tags");
                    if (values.Count > 12) throw new InvalidDataException("Tags 最多 12 个。");
                    command.Tags = values.Select(value => ValueString(value, "Tags", 30)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            if (command.Tags == null) command.Tags = new List<string>();
        }

        private static CommandItem Clone(CommandItem source)
        {
            return new CommandItem
            {
                Id = source.Id,
                Name = source.Name,
                Description = source.Description,
                Tags = source.Tags == null ? new List<string>() : new List<string>(source.Tags),
                Action = source.Action,
                Executable = source.Executable,
                Arguments = source.Arguments,
                WorkingDirectory = source.WorkingDirectory,
                CloseAfterLaunch = source.CloseAfterLaunch,
                Disabled = source.Disabled
            };
        }

        private static string RequiredString(Dictionary<string, object> values, string key, int maxLength)
        {
            object value;
            if (!values.TryGetValue(key, out value))
            {
                throw new InvalidDataException("缺少字段：" + key);
            }
            return ValueString(value, key, maxLength);
        }

        private static string ValueString(object value, string name, int maxLength, bool allowEmpty)
        {
            string text = value as string;
            if (text == null || (!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > maxLength)
            {
                throw new InvalidDataException(name + " 必须是长度不超过 " + maxLength + " 的字符串。");
            }
            return text.Trim();
        }

        private static string ValueString(object value, string name, int maxLength)
        {
            return ValueString(value, name, maxLength, false);
        }

        private static List<object> AsList(object value, string name)
        {
            var array = value as object[];
            if (array != null) return array.ToList();
            var list = value as System.Collections.ArrayList;
            if (list != null) return list.Cast<object>().ToList();
            throw new InvalidDataException(name + " 必须是数组。");
        }

        private static void EnsureKeys(Dictionary<string, object> values, string[] allowed, string name)
        {
            var allowedKeys = new HashSet<string>(allowed, StringComparer.Ordinal);
            string unknown = values.Keys.FirstOrDefault(key => !allowedKeys.Contains(key));
            if (unknown != null)
            {
                throw new InvalidDataException(name + " 不允许包含字段：" + unknown);
            }
            string missing = allowed.FirstOrDefault(key => !values.ContainsKey(key));
            if (missing != null)
            {
                throw new InvalidDataException(name + " 缺少字段：" + missing);
            }
        }

        private static string FieldDisplayName(string field)
        {
            var names = new Dictionary<string, string>
            {
                { "Name", "名称" }, { "Description", "说明" }, { "Tags", "标签" },
                { "Executable", "程序" }, { "Arguments", "参数" },
                { "WorkingDirectory", "工作目录" }, { "CloseAfterLaunch", "启动后关闭" }
            };
            string result;
            return names.TryGetValue(field, out result) ? result : field;
        }

        private static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string redacted = Regex.Replace(
                value,
                "(?i)(api[_-]?key|token|secret|password)(\\s*[:=]\\s*)([^\\s\\\"']+)",
                "$1$2***"
            );
            return Regex.Replace(redacted, "(?i)Bearer\\s+[^\\s\\\"']+", "Bearer ***");
        }
    }

    internal sealed class AiCommandEditorForm : Form
    {
        private readonly string sharedConfigPath;
        private readonly string localConfigPath;
        private readonly Label stepLabel;
        private readonly Label instructionLabel;
        private readonly TextBox inputBox;
        private readonly FlowLayoutPanel previewList;
        private readonly Dictionary<int, TextBox> previewNameEditors;
        private readonly List<Control> previewCards;
        private readonly Label characterLimitLabel;
        private readonly Button primaryButton;
        private readonly Button backButton;
        private readonly Button undoButton;
        private int step = 1;
        private string originalRequest;
        private string copiedFingerprint;
        private AiCommandPatchPlan plan;

        public AiCommandEditorForm(string sharedConfigPath, string localConfigPath)
        {
            this.sharedConfigPath = sharedConfigPath;
            this.localConfigPath = localConfigPath;

            Text = "AI 修改命令";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 610);
            MinimumSize = new Size(620, 520);
            BackColor = Color.FromArgb(244, 245, 239);
            ForeColor = Color.FromArgb(23, 28, 24);
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(24, 20, 24, 20),
                BackColor = BackColor
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));

            stepLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 17F, FontStyle.Bold),
                ForeColor = ForeColor
            };
            instructionLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 9.5F),
                ForeColor = Color.FromArgb(91, 102, 94)
            };
            inputBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                AcceptsTab = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(255, 255, 252),
                ForeColor = ForeColor,
                Font = new Font("Microsoft YaHei UI", 10F)
            };
            inputBox.TextChanged += (sender, eventArgs) => UpdateCharacterLimit();
            previewNameEditors = new Dictionary<int, TextBox>();
            previewCards = new List<Control>();
            previewList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = BackColor,
                Padding = new Padding(0, 0, 4, 0),
                Visible = false
            };
            previewList.ClientSizeChanged += (sender, eventArgs) => ResizePreviewCards();
            characterLimitLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(91, 102, 94),
                Font = new Font("Microsoft YaHei UI", 8.5F)
            };
            var inputPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                BackColor = BackColor
            };
            inputPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            inputPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            var editorHost = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = BackColor
            };
            editorHost.Controls.Add(inputBox);
            editorHost.Controls.Add(previewList);
            inputPanel.Controls.Add(editorHost, 0, 0);
            inputPanel.Controls.Add(characterLimitLabel, 0, 1);
            var safetyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "只修改个人 commands.local.json；应用前会严格校验并保留一次恢复备份。提示词会隐藏常见密钥，但发送前仍请自行确认。",
                ForeColor = Color.FromArgb(91, 102, 94),
                Font = new Font("Microsoft YaHei UI", 8.5F),
                Padding = new Padding(0, 10, 0, 0)
            };

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0)
            };
            primaryButton = CreateButton("复制给 AI", true);
            primaryButton.Click += PrimaryButton_Click;
            backButton = CreateButton("上一步", false);
            backButton.Click += BackButton_Click;
            undoButton = CreateButton("恢复上次修改", false);
            undoButton.Click += UndoButton_Click;
            footer.Controls.Add(primaryButton);
            footer.Controls.Add(backButton);
            footer.Controls.Add(undoButton);

            root.Controls.Add(stepLabel, 0, 0);
            root.Controls.Add(instructionLabel, 0, 1);
            root.Controls.Add(inputPanel, 0, 2);
            root.Controls.Add(safetyLabel, 0, 3);
            root.Controls.Add(footer, 0, 4);
            Controls.Add(root);
            AcceptButton = primaryButton;
            UpdateStep();
        }

        private static Button CreateButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 34,
                Padding = new Padding(12, 0, 12, 0),
                Margin = new Padding(8, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(151, 179, 155) : Color.FromArgb(255, 255, 252),
                ForeColor = Color.FromArgb(23, 28, 24),
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(23, 28, 24);
            button.FlatAppearance.BorderSize = 2;
            return button;
        }

        private void PrimaryButton_Click(object sender, EventArgs eventArgs)
        {
            try
            {
                if (step == 1)
                {
                    originalRequest = inputBox.Text.Trim();
                    copiedFingerprint = AiCommandPatchService.ComputeFingerprint(sharedConfigPath, localConfigPath);
                    string prompt = AiCommandPatchService.BuildPrompt(originalRequest, sharedConfigPath);
                    Clipboard.SetText(prompt);
                    inputBox.Clear();
                    step = 2;
                    UpdateStep();
                    return;
                }

                if (step == 2)
                {
                    string response = inputBox.Text.Trim();
                    if (response.Length == 0 && Clipboard.ContainsText())
                    {
                        response = Clipboard.GetText().Trim();
                    }
                    plan = AiCommandPatchService.Prepare(
                        response,
                        sharedConfigPath,
                        localConfigPath,
                        copiedFingerprint
                    );
                    step = 3;
                    BuildPreviewEditor();
                    UpdateStep();
                    return;
                }

                var editedNames = new Dictionary<int, string>();
                foreach (KeyValuePair<int, TextBox> editor in previewNameEditors)
                {
                    editedNames[editor.Key] = editor.Value.Text;
                }
                AiCommandPatchService.UpdatePreviewNames(plan, editedNames);
                AiCommandPatchService.Apply(plan, sharedConfigPath, localConfigPath);
                MessageBox.Show(this, "命令已更新。", "AI 修改命令", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法继续", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BackButton_Click(object sender, EventArgs eventArgs)
        {
            if (step == 3)
            {
                step = 2;
                inputBox.Clear();
            }
            else if (step == 2)
            {
                step = 1;
                inputBox.Text = originalRequest ?? string.Empty;
            }
            UpdateStep();
        }

        private void UndoButton_Click(object sender, EventArgs eventArgs)
        {
            try
            {
                AiCommandPatchService.Undo(sharedConfigPath, localConfigPath);
                MessageBox.Show(this, "已恢复到上一次 AI 修改前。", "AI 修改命令", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateStep()
        {
            backButton.Visible = step > 1;
            undoButton.Visible = step == 1 && AiCommandPatchService.CanUndo(localConfigPath);
            inputBox.Visible = step < 3;
            previewList.Visible = step == 3;
            if (step == 3)
            {
                previewList.BringToFront();
            }
            else
            {
                inputBox.BringToFront();
            }
            inputBox.MaxLength = step == 1
                ? AiCommandPatchService.UserRequestMaxLength
                : AiCommandPatchService.AiResponseMaxLength;
            characterLimitLabel.Visible = step < 3;
            if (step == 1)
            {
                stepLabel.Text = "1 / 3  描述你要改什么";
                instructionLabel.Text = "用自然语言描述即可：" + Environment.NewLine +
                    "例如：新增 Cursor 命令；隐藏 VS Code；修改 PowerShell 标签。";
                primaryButton.Text = "复制给 AI";
            }
            else if (step == 2)
            {
                stepLabel.Text = "2 / 3  粘贴 AI 返回内容";
                instructionLabel.Text = "提示词已复制。发给任意大模型，再把它返回的 JSON 粘贴到下方；留空时会直接读取剪贴板。";
                primaryButton.Text = "解析并预览";
            }
            else
            {
                stepLabel.Text = "3 / 3  确认增量修改";
                instructionLabel.Text = "可直接优化命令名称；程序、参数等配置保持不变。确认后一次应用全部操作。";
                primaryButton.Text = "应用 " + plan.PreviewItems.Count.ToString(CultureInfo.InvariantCulture) + " 项修改";
            }
            UpdateCharacterLimit();
            if (step == 3 && previewNameEditors.Count > 0)
            {
                TextBox firstEditor = previewNameEditors.OrderBy(editor => editor.Key).First().Value;
                firstEditor.Focus();
                firstEditor.SelectAll();
            }
            else
            {
                inputBox.Focus();
            }
        }

        private void BuildPreviewEditor()
        {
            previewList.SuspendLayout();
            foreach (Control control in previewList.Controls.Cast<Control>().ToList())
            {
                control.Dispose();
            }
            previewList.Controls.Clear();
            previewNameEditors.Clear();
            previewCards.Clear();

            for (int index = 0; index < plan.PreviewItems.Count; index++)
            {
                AiCommandPatchPreviewItem item = plan.PreviewItems[index];
                var card = new TableLayoutPanel
                {
                    Height = 68,
                    ColumnCount = 2,
                    RowCount = 2,
                    Margin = new Padding(0, 0, 0, 8),
                    Padding = new Padding(10, 6, 10, 6),
                    BackColor = Color.FromArgb(255, 255, 252)
                };
                card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
                card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
                card.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));

                var operationLabel = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + item.OperationLabel,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                    ForeColor = ForeColor
                };
                Control nameControl;
                if (item.CanEditName)
                {
                    var nameEditor = new TextBox
                    {
                        Dock = DockStyle.Fill,
                        Text = item.Name,
                        MaxLength = 80,
                        BorderStyle = BorderStyle.FixedSingle,
                        BackColor = Color.White,
                        ForeColor = ForeColor,
                        Font = new Font("Microsoft YaHei UI", 9.5F),
                        Margin = new Padding(0, 3, 0, 1)
                    };
                    previewNameEditors[index] = nameEditor;
                    nameControl = nameEditor;
                }
                else
                {
                    nameControl = new Label
                    {
                        Dock = DockStyle.Fill,
                        Text = item.Name,
                        TextAlign = ContentAlignment.MiddleLeft,
                        ForeColor = ForeColor,
                        Font = new Font("Microsoft YaHei UI", 9.5F),
                        Padding = new Padding(1, 0, 0, 0)
                    };
                }
                var detailLabel = new Label
                {
                    Dock = DockStyle.Fill,
                    Text = item.Detail,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.FromArgb(91, 102, 94),
                    Font = new Font("Microsoft YaHei UI", 8.5F)
                };

                card.Controls.Add(operationLabel, 0, 0);
                card.SetRowSpan(operationLabel, 2);
                card.Controls.Add(nameControl, 1, 0);
                card.Controls.Add(detailLabel, 1, 1);
                previewCards.Add(card);
                previewList.Controls.Add(card);
            }
            ResizePreviewCards();
            previewList.ResumeLayout();
        }

        private void ResizePreviewCards()
        {
            int width = Math.Max(320, previewList.ClientSize.Width - previewList.Padding.Horizontal -
                SystemInformation.VerticalScrollBarWidth - 4);
            foreach (Control card in previewCards)
            {
                card.Width = width;
            }
        }

        private void UpdateCharacterLimit()
        {
            int limit = step == 1
                ? AiCommandPatchService.UserRequestMaxLength
                : AiCommandPatchService.AiResponseMaxLength;
            characterLimitLabel.Text = inputBox.TextLength.ToString("N0", CultureInfo.InvariantCulture) +
                " / " + limit.ToString("N0", CultureInfo.InvariantCulture) + " 字";
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly string appDirectory;
        private readonly string configPath;
        private readonly string contextPath;
        private List<CommandItem> commands;
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
        private Panel commandListHost;
        private VScrollBar commandScrollBar;
        private Button updateButton;
        private bool scrolling;

        // 置顶命令列表（pins.json，最新的在最前）
        private readonly List<string> pinnedKeys;

        // 拖动排序相关字段
        private Control dragSourceHandle;
        private Point dragStartPoint;
        private bool isDragging;
        private int dragInsertIndex = -1;
        private Panel dragIndicator;


        internal string ContextPath { get { return contextPath; } }

        public MainForm(string appDirectory, string configPath, string contextPath, IList<CommandItem> commands, UsageTracker usageTracker)
        {
            this.appDirectory = appDirectory;
            this.configPath = configPath;
            this.contextPath = contextPath;
            // 应用保存的自定义排序
            var orderedCommands = LoadCustomOrder(commands.Where(IsVisibleCommand).ToList());
            this.commands = orderedCommands;
            this.pinnedKeys = LoadPinnedKeys();
            this.usageTracker = usageTracker;

            Text = AppInfo.DisplayName;
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

        // 命令列表 = 自绘滚动条（右） + FlowLayoutPanel（填充剩余宽度），用 TableLayoutPanel
        // 左右分栏。之所以不用 FlowLayoutPanel.AutoScroll：WinForms 在 DPI 缩放或主题启用时
        // 会改用视觉样式滚动条，其绘制完全不走控件窗口，会显现为一条空白竖槽（右侧那条白条）。
        private Control CreateCommandList()
        {
            commandList = new FlowLayoutPanel
            {
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Location = Point.Empty,
                BackColor = background,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,
                Padding = new Padding(0, 14, 0, 8),
                AllowDrop = true
            };

            commandList.SizeChanged += (sender, eventArgs) => OnCommandListSizeChanged();
            commandList.DragOver += CommandList_DragOver;
            commandList.DragDrop += CommandList_DragDrop;
            commandList.DragLeave += (sender, eventArgs) => HideDragIndicator();
            commandList.MouseWheel += CommandList_MouseWheel;

            commandScrollBar = CreateCommandScrollBar();

            commandListHost = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = background,
                AutoScroll = false
            };
            commandListHost.Controls.Add(commandList);

            // Drag indicator overlays the host panel and never enters the FlowLayoutPanel flow,
            // otherwise every DragOver would remove/re-add it and reflow the whole list (lag).
            dragIndicator = new Panel
            {
                Height = 3,
                Width = 360,
                BackColor = accent,
                Visible = false,
                Margin = Padding.Empty
            };
            commandListHost.Controls.Add(dragIndicator);

            EnableDoubleBuffering(commandList);
            EnableDoubleBuffering(commandListHost);
            commandListHost.SizeChanged += (sender, eventArgs) => ResizeCommandCards();
            commandListHost.MouseWheel += CommandList_MouseWheel;

            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SystemInformation.VerticalScrollBarWidth + 2F));
            host.Controls.Add(commandListHost, 0, 0);
            host.Controls.Add(commandScrollBar, 1, 0);
            return host;
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

            // 置顶命令始终排在最前（按置顶顺序，最新置顶在最前），其余保持当前排序
            List<CommandItem> pinnedCommands = visibleCommands.Where(IsPinned).ToList();
            List<CommandItem> unpinnedCommands = visibleCommands.Where(command => !IsPinned(command)).ToList();
            var orderedVisible = new List<CommandItem>();
            var remainingPinned = new List<CommandItem>(pinnedCommands);
            foreach (string key in pinnedKeys)
            {
                int index = remainingPinned.FindIndex(c => string.Equals(UsageTracker.GetKey(c), key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    orderedVisible.Add(remainingPinned[index]);
                    remainingPinned.RemoveAt(index);
                }
            }
            orderedVisible.AddRange(remainingPinned);
            orderedVisible.AddRange(unpinnedCommands);
            visibleCommands = orderedVisible;

            commandList.SuspendLayout();
            commandList.Controls.Clear();


            bool allowDrag = sortBox.SelectedIndex == 0 && string.IsNullOrEmpty(query) && selectedTag == null;

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

                // 放置顺序必须是「先内容、后手柄」：两者都是 Dock 填充，WinForms 按逆 z 序
                // 排布填充控件，先加入者先占满整行，手柄会被挤到卡片之外（表现为手柄消失）。
                var button = new Button
                {
                    Tag = command,
                    Text = "  [" + command.Id + "]  " + command.Name + Environment.NewLine +
                           "      " + (command.Description ?? string.Empty) + Environment.NewLine +
                           "      " + tagText + "    " + usageText,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty,
                    Padding = new Padding(10, 0, 14, 0),
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = textPrimary,
                    BackColor = surface,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Consolas", 10F, FontStyle.Bold),
                    Cursor = allowDrag ? Cursors.SizeAll : Cursors.Hand,
                    UseVisualStyleBackColor = false
                };
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.MouseOverBackColor = surfaceHover;
                button.FlatAppearance.MouseDownBackColor = accent;
                button.Click += LaunchCommand;

                // 左侧拖动手柄
                var dragHandle = new Label
                {
                    Text = "⋮⋮",
                    Dock = DockStyle.Left,
                    Width = 26,
                    Font = new Font("Consolas", 12F, FontStyle.Bold),
                    ForeColor = textSecondary,
                    TextAlign = ContentAlignment.MiddleCenter,
                    BackColor = Color.FromArgb(240, 240, 235),
                    Cursor = allowDrag ? Cursors.SizeAll : Cursors.Default
                };

                // 一键置顶按钮（Dock.Right，加入顺序最后、最先参与停靠，位于卡片右侧）
                bool isPinned = IsPinned(command);
                CommandItem pinTarget = command;
                var pinButton = new Button
                {
                    Tag = command,
                    Text = isPinned ? "已置顶" : "置顶",
                    Dock = DockStyle.Right,
                    Width = 62,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = isPinned ? accent : Color.FromArgb(240, 240, 235),
                    ForeColor = textPrimary,
                    Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                    Cursor = Cursors.Hand,
                    TextAlign = ContentAlignment.MiddleCenter,
                    UseVisualStyleBackColor = false
                };
                pinButton.FlatAppearance.BorderSize = 0;
                pinButton.FlatAppearance.MouseOverBackColor = isPinned ? accent : surfaceHover;
                pinButton.FlatAppearance.MouseDownBackColor = accent;
                pinButton.Click += (sender, e) => TogglePin(pinTarget);
                pinButton.MouseWheel += ForwardMouseWheel;

                // 创建命令卡片容器（带拖动手柄和置顶按钮）
                var cardPanel = new Panel
                {
                    Height = 88,
                    Width = CommandCardWidth(),
                    Margin = new Padding(0, 0, 0, 12),
                    BackColor = surface,
                    BorderStyle = BorderStyle.FixedSingle,
                    Padding = new Padding(0)
                };

                // 手柄悬停效果
                dragHandle.MouseEnter += (sender, e) => dragHandle.BackColor = surfaceHover;
                dragHandle.MouseLeave += (sender, e) => dragHandle.BackColor = Color.FromArgb(240, 240, 235);

                // 启用拖动排序（只在手柄上）
                if (allowDrag)
                {
                    dragHandle.MouseDown += CommandButton_MouseDown;
                    dragHandle.MouseMove += CommandButton_MouseMove;
                    dragHandle.GiveFeedback += CommandButton_GiveFeedback;
                    dragHandle.AllowDrop = true;
                    dragHandle.DragOver += (s, e) => CommandList_DragOver(s, e);
                    dragHandle.DragDrop += (s, e) => CommandList_DragDrop(s, e);

                    // 整张卡片都可按住拖动（手柄只是视觉提示），5px 死区保证单击仍触发启动。
                    button.MouseDown += CommandButton_MouseDown;
                    button.MouseMove += CommandButton_MouseMove;
                    cardPanel.MouseDown += CommandButton_MouseDown;
                    cardPanel.MouseMove += CommandButton_MouseMove;
                }

                cardPanel.Controls.Add(button);
                cardPanel.Controls.Add(dragHandle);
                cardPanel.Controls.Add(pinButton);

                // 滚轮落在卡片上时转发给列表滚动条，否则鼠标停在卡片区域滚不动。
                cardPanel.MouseWheel += ForwardMouseWheel;
                button.MouseWheel += ForwardMouseWheel;
                dragHandle.MouseWheel += ForwardMouseWheel;

                commandList.Controls.Add(cardPanel);
            }

            if (visibleCommands.Count == 0)
            {
                commandList.Controls.Add(new Label
                {
                    Text = "> 未找到匹配的 CLI\n  请调整关键词或标签筛选。",
                    Width = CommandCardWidth(),
                    Height = 72,
                    Padding = new Padding(14, 16, 14, 0),
                    Margin = Padding.Empty,
                    BackColor = surface,
                    ForeColor = textSecondary,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Microsoft YaHei UI", 9.5F)
                });
            }

            // 末项与底部操作栏之间留白，避免下边框紧贴。
            commandList.Controls.Add(new Panel
            {
                Height = 12,
                Width = CommandCardWidth(),
                Margin = Padding.Empty,
                BackColor = background
            });
            summaryLabel.Text = "SEARCH  名称 / 描述 / 标签    CLI  " + visibleCommands.Count + " / " + commands.Count +
                                "    TOTAL RUN  " + commands.Sum(command => usageTracker.Get(command).Count);
            commandList.ResumeLayout();
            UpdateCommandListHeight();
            ResizeCommandCards();
        }

        // ============== 拖动排序实现 ==============

        private void CommandButton_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragSourceHandle = sender as Control;
                dragStartPoint = e.Location;
                isDragging = false;
            }
        }

        private void CommandButton_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragSourceHandle == null || e.Button != MouseButtons.Left)
            {
                return;
            }

            // 移动足够距离才开始拖动，避免误触
            if (!isDragging)
            {
                if (Math.Abs(e.X - dragStartPoint.X) < 5 && Math.Abs(e.Y - dragStartPoint.Y) < 5)
                {
                    return;
                }
                isDragging = true;
                // 拖动期间卡片会被改成无边框并加上内边距，控件尺寸随之变化（会从 88 掉到 3），
                // 所以命中判断所需的高度与布局坐标必须在改样式之前先缓存下来。
                CacheCommandCardLayout();
                // 高亮整个卡片
                var cardPanel = dragSourceHandle.Parent as Panel;
                if (cardPanel != null)
                {
                    cardPanel.BackColor = Color.FromArgb(200, 215, 198);
                    cardPanel.BorderStyle = BorderStyle.None;
                    cardPanel.Padding = new Padding(2);
                    cardPanel.Height = 88;   // 维持卡片高度，避免拖拽时列表整体跳动
                }
            }

            // 执行拖放操作
            DragDropEffects effect = dragSourceHandle.DoDragDrop(dragSourceHandle, DragDropEffects.Move);

            // 拖动结束后恢复样式
            if (effect == DragDropEffects.Move)
            {
                var cardPanel = dragSourceHandle.Parent as Panel;
                if (cardPanel != null)
                {
                    cardPanel.BackColor = surface;
                    cardPanel.BorderStyle = BorderStyle.FixedSingle;
                    cardPanel.Padding = new Padding(0);
                    cardPanel.Height = 88;
                }
            }

            dragSourceHandle = null;
            isDragging = false;
            HideDragIndicator();
        }

        private static void EnableDoubleBuffering(Control control)
        {
            var prop = typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop != null) { prop.SetValue(control, true, null); }
        }

        private void CommandList_DragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.Move;

            // 计算插入位置
            Point pt = commandList.PointToClient(new Point(e.X, e.Y));
            dragInsertIndex = GetInsertIndex(pt.Y);
            ShowDragIndicator(dragInsertIndex);        }

        private void CommandList_DragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(Control)))
            {
                return;
            }

            Control sourceHandle = (Control)e.Data.GetData(typeof(Control));
            Panel sourceCard = sourceHandle.Parent as Panel;
            Point pt = commandList.PointToClient(new Point(e.X, e.Y));
            int targetIndex = GetInsertIndex(pt.Y);

            // 获取源命令在当前列表中的索引
            int sourceIndex = -1;
            int cardIndex = 0;
            foreach (Control ctrl in commandList.Controls)
            {
                if (ctrl is Panel && ((Panel)ctrl).Controls.Count >= 2)
                {
                    // 先加入的是内容按钮，Tag 是 CommandItem
                    var btn = ((Panel)ctrl).Controls[0] as Button;
                    if (btn != null && btn.Tag is CommandItem)
                    {
                        if (ctrl == sourceCard)
                        {
                            sourceIndex = cardIndex;
                            break;
                        }
                        cardIndex++;
                    }
                }
            }

            if (sourceIndex < 0 || sourceIndex == targetIndex)
            {
                HideDragIndicator();
                return;
            }

            // 调整目标索引（如果源在目标之前，需要减1）
            if (sourceIndex < targetIndex)
            {
                targetIndex--;
            }

            // 执行移动
            MoveCommand(sourceIndex, targetIndex);
            HideDragIndicator();
        }

        private void CommandButton_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = true;
        }

        // 拖动前统一记录各卡片的布局 Top：列表本身会被滚动条整体上移（负数 Top），
        // 命中判断必须用卡片的列表内坐标，不能直接用控件的 Top。
        private readonly List<int> commandCardTops = new List<int>();
        private readonly List<int> commandCardHeights = new List<int>();

        private void CacheCommandCardLayout()
        {
            commandCardTops.Clear();
            commandCardHeights.Clear();

            if (commandList == null)
            {
                return;
            }

            int y = commandList.Padding.Top;
            foreach (Control ctrl in commandList.Controls)
            {
                if (ctrl == dragIndicator) continue;
                if (!(ctrl is Panel) || ((Panel)ctrl).BorderStyle != BorderStyle.FixedSingle) continue;
                if (((Panel)ctrl).Controls.Count < 2) continue;

                commandCardTops.Add(y);
                commandCardHeights.Add(ctrl.Height);
                y += ctrl.Height + ctrl.Margin.Bottom;
            }
        }

        private int GetInsertIndex(double y)
        {
            for (int index = 0; index < commandCardTops.Count; index++)
            {
                if (y < commandCardTops[index] + commandCardHeights[index] / 2.0)
                {
                    return index;
                }
            }

            return commandCardTops.Count;
        }

        private void ShowDragIndicator(int index)
        {
            if (dragIndicator == null) return;

            // Insert Y in list-local coords; out of range falls back to below the last card.
            int y = commandList.Padding.Top;
            if (commandCardTops.Count > 0)
            {
                if (index < commandCardTops.Count)
                {
                    y = commandCardTops[index];
                }
                else
                {
                    y = commandCardTops[commandCardTops.Count - 1] + commandCardHeights[commandCardHeights.Count - 1];
                }
            }
            // commandList scrolls via a negative Top, so host Y = list-local y + commandList.Top.
            int hostY = y - 2 + commandList.Top;
            int indicatorWidth = CommandCardWidth();
            if (dragIndicator.Visible && dragIndicator.Top == hostY && dragIndicator.Width == indicatorWidth)
            {
                return;
            }
            dragIndicator.Location = new Point(0, hostY);
            dragIndicator.Width = indicatorWidth;
            dragIndicator.Visible = true;
            dragIndicator.BringToFront();
        }

        private void HideDragIndicator()
        {
            dragInsertIndex = -1;
            if (dragIndicator != null && dragIndicator.Parent != null)
            {
                dragIndicator.Visible = false;
            }
            commandCardTops.Clear();
            commandCardHeights.Clear();
        }

        private void MoveCommand(int sourceIndex, int targetIndex)
        {
            if (sourceIndex == targetIndex) return;

            // 从 commands 列表中移动项
            var cmdList = commands.ToList();
            CommandItem item = cmdList[sourceIndex];
            cmdList.RemoveAt(sourceIndex);
            cmdList.Insert(targetIndex, item);

            // 更新 commands 列表
            for (int i = 0; i < cmdList.Count; i++)
            {
                if (i < commands.Count)
                {
                    commands[i] = cmdList[i];
                }
            }

            SaveCustomOrder(cmdList);
            RefreshCommandList();
        }

        private void SaveCustomOrder(List<CommandItem> orderedCommands)
        {
            try
            {
                // 单独保存排序到 order.json，不修改命令配置
                string orderPath = Path.Combine(Path.GetDirectoryName(configPath), "order.json");

                // 只保存命令 Id 的顺序列表
                List<string> orderIds = orderedCommands
                    .Select(cmd => UsageTracker.GetKey(cmd))
                    .ToList();

                string json = new JavaScriptSerializer().Serialize(orderIds);
                File.WriteAllText(orderPath, json, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存排序失败：" + ex.Message, "CLI List", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private List<CommandItem> LoadCustomOrder(List<CommandItem> defaultCommands)
        {
            try
            {
                string orderPath = Path.Combine(Path.GetDirectoryName(configPath), "order.json");
                if (!File.Exists(orderPath))
                {
                    return defaultCommands;
                }

                string json = File.ReadAllText(orderPath);
                List<string> orderIds = new JavaScriptSerializer().Deserialize<List<string>>(json);
                if (orderIds == null || orderIds.Count == 0)
                {
                    return defaultCommands;
                }

                var ordered = new List<CommandItem>();
                var remaining = new List<CommandItem>(defaultCommands);

                // 按 order.json 的顺序排列
                foreach (string key in orderIds)
                {
                    int index = remaining.FindIndex(c =>
                        string.Equals(UsageTracker.GetKey(c), key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        ordered.Add(remaining[index]);
                        remaining.RemoveAt(index);
                    }
                }

                // 追加新增的命令
                ordered.AddRange(remaining);
                return ordered;
            }
            catch
            {
                return defaultCommands;
            }
        }

        // ============== 一键置顶实现 ==============

        private string GetPinsPath()
        {
            return Path.Combine(Path.GetDirectoryName(configPath), "pins.json");
        }

        private List<string> LoadPinnedKeys()
        {
            try
            {
                string pinsPath = GetPinsPath();
                if (!File.Exists(pinsPath))
                {
                    return new List<string>();
                }

                string json = File.ReadAllText(pinsPath);
                List<string> keys = new JavaScriptSerializer().Deserialize<List<string>>(json);
                return keys == null ? new List<string>() : keys;
            }
            catch
            {
                return new List<string>();
            }
        }

        private void SavePins()
        {
            try
            {
                string pinsPath = GetPinsPath();
                string json = new JavaScriptSerializer().Serialize(pinnedKeys);
                string temporaryPath = pinsPath + ".tmp";
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                File.Copy(temporaryPath, pinsPath, true);
                File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存置顶失败：" + ex.Message, "CLI List", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool IsPinned(CommandItem command)
        {
            string key = UsageTracker.GetKey(command);
            return pinnedKeys.Any(pinned => string.Equals(pinned, key, StringComparison.OrdinalIgnoreCase));
        }

        private void TogglePin(CommandItem command)
        {
            string key = UsageTracker.GetKey(command);
            int existingIndex = pinnedKeys.FindIndex(pinned => string.Equals(pinned, key, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                pinnedKeys.RemoveAt(existingIndex);
            }
            else
            {
                // 最新置顶的排在最前
                pinnedKeys.Insert(0, key);
            }
            SavePins();
            RefreshCommandList();
        }

        // ============== 拖动排序结束 ==============

        // FlowLayoutPanel 每次改高度都会触发 SizeChanged，若直接在里面重新量高度会互相递归，
        // 因此只在「宽度变化」时重排卡片与滚动条。
        // 卡片按「扣掉滚动条之后的可用宽度」排布，滚动条才不会盖住卡片的右边框。
        private int CommandCardWidth()
        {
            return Math.Max(360, _lastViewWidth - SystemInformation.VerticalScrollBarWidth);
        }

        private void OnCommandListSizeChanged()
        {
            if (commandList == null || commandListHost == null)
            {
                return;
            }

            if (commandList.Width == _lastCommandListWidth)
            {
                return;
            }

            _lastCommandListWidth = commandList.Width;
            ResizeCommandCards();
        }

        private int _lastCommandListWidth = -1;

        private void ResizeCommandCards()
        {
            if (commandList == null)
            {
                return;
            }

            int viewWidth = commandListHost == null ? Math.Max(360, commandList.ClientSize.Width) : commandListHost.ClientSize.Width;
            int viewHeight = commandListHost == null ? 0 : commandListHost.ClientSize.Height;

            int containerWidth;
            if (viewWidth > 0)
            {
                containerWidth = Math.Max(360, viewWidth - SystemInformation.VerticalScrollBarWidth);
                _lastViewWidth = viewWidth;
                commandList.Width = viewWidth;
            }
            else
            {
                // 首次布局完成前取不到宿主宽度，沿用上一次的实测值，避免卡片被压成 360。
                viewWidth = _lastViewWidth;
                containerWidth = Math.Max(360, viewWidth - SystemInformation.VerticalScrollBarWidth);
            }

            foreach (Control control in commandList.Controls)
            {
                // 卡片按「扣掉滚动条之后的可用宽度」排布，滚动条才不会盖住卡片的右边框。
                bool isCommandCard = control is Panel && ((Panel)control).BorderStyle == BorderStyle.FixedSingle;
                control.Width = isCommandCard ? containerWidth : viewWidth;
            }

            UpdateCommandScrollBar(containerWidth, viewHeight);
        }

        private int _lastViewWidth = 560;

        // 命令列表改用「外层 Panel 承载 + 自绘滚动条」后，滚动范围由自身高度决定：
        // FlowLayoutPanel 内容随项增减，高度按子项实测高度累加；外层 Panel 只做视口。
        private void UpdateCommandListHeight()
        {
            if (commandList == null)
            {
                return;
            }

            int contentHeight = commandList.Padding.Top + commandList.Padding.Bottom;
            foreach (Control control in commandList.Controls)
            {
                if (control == dragIndicator)
                {
                    continue; // 指示线绝对定位，不占流式布局高度。
                }
                contentHeight += control.Height + control.Margin.Bottom + control.Margin.Top;
            }

            int minimumHeight = commandListHost == null ? 0 : commandListHost.ClientSize.Height;
            commandList.AutoSize = false;
            commandList.Height = Math.Max(1, Math.Max(contentHeight, minimumHeight));
        }

        private void UpdateCommandScrollBar()
        {
            UpdateCommandScrollBar(
                Math.Max(360, (commandListHost == null ? 0 : commandListHost.ClientSize.Width) - SystemInformation.VerticalScrollBarWidth),
                commandListHost == null ? 0 : commandListHost.ClientSize.Height
            );
        }

        private void UpdateCommandScrollBar(int viewportWidth, int viewportHeight)
        {
            if (commandScrollBar == null || commandListHost == null)
            {
                return;
            }

            int contentHeight = commandList.Height;
            int maxScroll = Math.Max(0, contentHeight - viewportHeight);
            int largeChange = Math.Max(1, viewportHeight);

            scrolling = true;
            try
            {
                commandScrollBar.LargeChange = largeChange;
                commandScrollBar.SmallChange = 30;
                // WinForms 允许用户拖到的实际上限是 Maximum - LargeChange + 1。
                // 因此 Maximum 必须包含视口跨度，否则滑块会在距离列表底部约一个视口处提前停下。
                commandScrollBar.Maximum = maxScroll + largeChange - 1;
                // 内容变短时把位置夹回合法范围，否则滑块会画出可视区之外。
                commandScrollBar.Value = Math.Min(commandScrollBar.Value, maxScroll);
                commandList.Top = -commandScrollBar.Value;
            }
            finally
            {
                scrolling = false;
            }

            PlaceCommandScrollBar();
            UpdateCommandScrollBarVisibility();
        }

        private void PlaceCommandScrollBar()
        {
            if (commandScrollBar == null || commandScrollBar.Parent == null)
            {
                return;
            }

            int right = commandScrollBar.Parent.ClientSize.Width - commandScrollBar.Width;
            int trackHeight = Math.Max(60, commandScrollBar.Parent.ClientSize.Height - 16);
            commandScrollBar.Bounds = new Rectangle(right, 8, commandScrollBar.Width, trackHeight);
        }

        private void UpdateCommandScrollBarVisibility()
        {
            if (commandScrollBar == null || commandListHost == null)
            {
                return;
            }

            commandScrollBar.Visible = commandList.Height > commandListHost.ClientSize.Height;
        }

        private VScrollBar CreateCommandScrollBar()
        {
            var bar = new DrawerScrollBar
            {
                Minimum = 0,
                Maximum = 0,
                SmallChange = 30,
                Width = SystemInformation.VerticalScrollBarWidth,
                TabStop = false,
                Visible = false
            };
            bar.ValueChanged += (sender, eventArgs) => ScrollCommandList(bar.Value);
            return bar;
        }

        // 自绘滚动条：WinForms 原生的 VScrollBar 无法按应用配色改（必须注册非主题子控件，
        // 代价是滑块被画成刺眼的纯白，也就是那条白条）。这里自己画轨道与滑块，
        // 保持与卡片一致的浅色墨线语言。
        private sealed class DrawerScrollBar : VScrollBar
        {
            private readonly Color trackColor = Color.FromArgb(237, 239, 232);
            private readonly Color thumbColor = Color.FromArgb(196, 205, 196);
            private readonly Color thumbHoverColor = Color.FromArgb(151, 179, 155);

            private bool hovering;
            private bool draggingThumb;
            private int dragOffset;

            public DrawerScrollBar()
            {
                SetStyle(
                    ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
                    true
                );
            }

            protected override void OnMouseEnter(EventArgs eventArgs)
            {
                hovering = true;
                Invalidate();
                base.OnMouseEnter(eventArgs);
            }

            protected override void OnMouseLeave(EventArgs eventArgs)
            {
                hovering = false;
                Invalidate();
                base.OnMouseLeave(eventArgs);
            }

            protected override void OnMouseDown(MouseEventArgs eventArgs)
            {
                base.OnMouseDown(eventArgs);
                if (eventArgs.Button != MouseButtons.Left)
                {
                    return;
                }

                Rectangle thumb = GetThumbRectangle();
                if (thumb.Contains(eventArgs.Location))
                {
                    draggingThumb = true;
                    dragOffset = eventArgs.Y - thumb.Top;
                    Capture = true;
                }
                else
                {
                    SetInteractiveValue(Value + (eventArgs.Y < thumb.Top ? -LargeChange : LargeChange));
                }
            }

            protected override void OnMouseMove(MouseEventArgs eventArgs)
            {
                base.OnMouseMove(eventArgs);
                if (!draggingThumb)
                {
                    return;
                }

                Rectangle thumb = GetThumbRectangle();
                int travel = Math.Max(1, Height - thumb.Height);
                int thumbTop = Math.Max(0, Math.Min(travel, eventArgs.Y - dragOffset));
                int maxScroll = InteractiveMaximum();
                SetInteractiveValue((int)Math.Round((double)thumbTop / travel * maxScroll));
            }

            protected override void OnMouseUp(MouseEventArgs eventArgs)
            {
                if (eventArgs.Button == MouseButtons.Left)
                {
                    draggingThumb = false;
                    Capture = false;
                }
                base.OnMouseUp(eventArgs);
            }

            protected override void OnMouseCaptureChanged(EventArgs eventArgs)
            {
                if (!Capture)
                {
                    draggingThumb = false;
                }
                base.OnMouseCaptureChanged(eventArgs);
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                Graphics graphics = eventArgs.Graphics;
                graphics.Clear(Parent == null ? trackColor : Parent.BackColor);

                var track = new Rectangle(0, 0, Width, Height);
                using (var trackBrush = new SolidBrush(trackColor))
                {
                    graphics.FillRectangle(trackBrush, track);
                }

                Rectangle thumb = GetThumbRectangle();
                using (var thumbBrush = new SolidBrush(hovering ? thumbHoverColor : thumbColor))
                {
                    graphics.FillRectangle(thumbBrush, thumb);
                }
            }

            private Rectangle GetThumbRectangle()
            {
                int channelHeight = Math.Max(1, Height);
                int minimumThumb = 28;
                int visible = Math.Max(1, LargeChange);
                int maxScroll = Math.Max(0, Maximum - LargeChange + 1);
                int range = visible + maxScroll;
                int thumbHeight = Math.Max(minimumThumb, (int)Math.Round((double)channelHeight * visible / range));
                thumbHeight = Math.Min(thumbHeight, channelHeight);

                int scrollable = Math.Max(1, channelHeight - thumbHeight);
                int positionRange = Math.Max(1, maxScroll);
                int offset = (int)Math.Round((double)Math.Min(Value, positionRange) / positionRange * scrollable);

                return new Rectangle(1, Math.Min(offset, channelHeight - thumbHeight), Math.Max(1, Width - 2), thumbHeight);
            }

            private int InteractiveMaximum()
            {
                return Math.Max(Minimum, Maximum - LargeChange + 1);
            }

            private void SetInteractiveValue(int value)
            {
                Value = Math.Max(Minimum, Math.Min(InteractiveMaximum(), value));
                Invalidate();
            }
        }

        private void ScrollCommandList(int value)
        {
            if (commandList == null || scrolling)
            {
                return;
            }

            scrolling = true;
            try
            {
                commandList.Top = -value;
            }
            finally
            {
                scrolling = false;
            }
        }

        private void CommandList_MouseWheel(object sender, MouseEventArgs eventArgs)
        {
            ScrollCommandListBy(-eventArgs.Delta / 120 * 60);
        }

        // 滚轮落在卡片按钮/手柄上时，也转发给同一个滚动条，否则鼠标停在卡片上滚不动。
        private void ForwardMouseWheel(object sender, MouseEventArgs eventArgs)
        {
            ScrollCommandListBy(-eventArgs.Delta / 120 * 60);
        }

        private void ScrollCommandListBy(int delta)
        {
            if (commandScrollBar == null || !commandScrollBar.Visible)
            {
                return;
            }

            int minimum = commandScrollBar.Minimum;
            int maximum = Math.Max(minimum, commandScrollBar.Maximum - commandScrollBar.LargeChange + 1);
            int next = Math.Max(minimum, Math.Min(maximum, commandScrollBar.Value + delta));
            if (next == commandScrollBar.Value)
            {
                return;
            }

            commandScrollBar.Value = next;
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

            var aiEditButton = CreateFooterButton("增/删/改命令");
            aiEditButton.Click += (sender, eventArgs) =>
            {
                using (var editor = new AiCommandEditorForm(
                    Path.Combine(appDirectory, "commands.json"),
                    configPath
                ))
                {
                    if (editor.ShowDialog(this) == DialogResult.OK)
                    {
                        ReloadCommands();
                    }
                }
            };

            updateButton = CreateFooterButton("检查更新");
            updateButton.Click += (sender, eventArgs) => UpdateManager.CheckForUpdate(this, appDirectory);

            footer.Controls.Add(closeButton);
            footer.Controls.Add(aiEditButton);
            footer.Controls.Add(updateButton);
            return footer;
        }

        internal void ShowAvailableUpdate(string tagName)
        {
            updateButton.Text = "↑ 更新 " + tagName;
            updateButton.Visible = true;
        }

        private static bool IsVisibleCommand(CommandItem command)
        {
            return !string.Equals(command.Action, "CheckUpdate", StringComparison.OrdinalIgnoreCase);
        }

        private void ReloadCommands()
        {
            commands = LoadCustomOrder(Program.LoadCommands(Path.Combine(appDirectory, "commands.json"))
                .Where(IsVisibleCommand)
                .ToList());

            tagFilter.BeginUpdate();
            try
            {
                tagFilter.Items.Clear();
                tagFilter.Items.Add("全部标签");
                foreach (string tag in commands.SelectMany(command => command.Tags)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag))
                {
                    tagFilter.Items.Add("# " + tag);
                }
                tagFilter.SelectedIndex = 0;
            }
            finally
            {
                tagFilter.EndUpdate();
            }
            RefreshCommandList();
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
                        browser.ShowDialog(this);
                    }
                    return;
                }

                if (string.Equals(command.Action, "OpenBrowser", StringComparison.OrdinalIgnoreCase))
                {
                    usageTracker.Record(command);
                    RefreshCommandList();
                    using (var picker = new BrowserPickerForm(appDirectory, contextPath))
                    {
                        picker.ShowDialog(this);
                    }
                    return;
                }

                if (string.Equals(command.Action, "ChooseIdeOrCli", StringComparison.OrdinalIgnoreCase))
                {
                    using (var picker = new IdeCliPickerForm(appDirectory, contextPath))
                    {
                        if (picker.ShowDialog(this) == DialogResult.OK)
                        {
                            usageTracker.Record(command);
                            RefreshCommandList();
                        }
                    }
                    return;
                }

                if (string.Equals(command.Action, "CheckUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    usageTracker.Record(command);
                    RefreshCommandList();
                    UpdateManager.CheckForUpdate(this, appDirectory);
                    return;
                }

                string executable = ResolveExecutable(command.Executable);
                string workingDirectory = ResolveWorkingDirectory(command.WorkingDirectory);
                string arguments = Environment.ExpandEnvironmentVariables(command.Arguments ?? string.Empty)
                    .Replace("{appdir:q}", QuoteContextPath(appDirectory))
                    .Replace("{appdir}", appDirectory)
                    .Replace("{context:q}", QuoteContextPath(contextPath))
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

        private static string QuoteContextPath(string contextPath)
        {
            return "\"" + contextPath.Replace("\"", "\\\"") + "\"";
        }
    }

    internal enum IdeCliLaunchKind
    {
        ShellTarget,
        AppModelId,
        TerminalCli
    }

    internal sealed class IdeCliToolInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string TargetPath { get; set; }
        public string DetectionSource { get; set; }
        public IdeCliLaunchKind LaunchKind { get; set; }

        public bool IsAvailable
        {
            get { return !string.IsNullOrWhiteSpace(TargetPath); }
        }
    }

    internal static class IdeCliToolLocator
    {
        private const string CodexPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";

        public static List<IdeCliToolInfo> DetectTools()
        {
            List<string> shortcutRoots = GetShortcutRoots();
            string codexPackageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                CodexPackageFamily
            );
            bool hasCodexApp = Directory.Exists(codexPackageDirectory);
            string workBuddyShortcut = FindShortcut(shortcutRoots, new[] { "WorkBuddy.lnk" });
            string traeShortcut = FindShortcut(shortcutRoots, new[] { "Trae CN.lnk", "TRAE.lnk", "Trae.lnk" });
            string claudePath = FindExecutableOnPath("claude", GetPathDirectories(), GetPathExtensions());

            return new List<IdeCliToolInfo>
            {
                new IdeCliToolInfo
                {
                    Id = "codex",
                    Name = "Codex",
                    Description = "打开 Codex 桌面应用",
                    TargetPath = hasCodexApp ? "shell:AppsFolder\\" + CodexPackageFamily + "!App" : null,
                    DetectionSource = hasCodexApp ? "Windows 应用" : "未检测到 Codex 桌面应用",
                    LaunchKind = IdeCliLaunchKind.AppModelId
                },
                new IdeCliToolInfo
                {
                    Id = "workbuddy",
                    Name = "WorkBuddy",
                    Description = "打开 WorkBuddy 桌面应用",
                    TargetPath = workBuddyShortcut,
                    DetectionSource = workBuddyShortcut == null ? "未检测到 WorkBuddy" : "开始菜单",
                    LaunchKind = IdeCliLaunchKind.ShellTarget
                },
                new IdeCliToolInfo
                {
                    Id = "trae",
                    Name = "TRAE",
                    Description = "打开 TRAE 桌面应用",
                    TargetPath = traeShortcut,
                    DetectionSource = traeShortcut == null ? "未检测到 TRAE" : "开始菜单",
                    LaunchKind = IdeCliLaunchKind.ShellTarget
                },
                new IdeCliToolInfo
                {
                    Id = "claude",
                    Name = "Claude Code",
                    Description = "选择目录后在终端启动 Claude Code",
                    TargetPath = claudePath,
                    DetectionSource = claudePath == null ? "未在 PATH 中检测到 claude" : claudePath,
                    LaunchKind = IdeCliLaunchKind.TerminalCli
                }
            };
        }

        internal static string FindShortcut(IEnumerable<string> roots, IEnumerable<string> candidateNames)
        {
            var names = new HashSet<string>(candidateNames, StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string match = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                        .FirstOrDefault(path => names.Contains(Path.GetFileName(path)));
                    if (!string.IsNullOrWhiteSpace(match))
                    {
                        return match;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
            return null;
        }

        internal static string FindExecutableOnPath(string commandName, IEnumerable<string> directories, string pathExtensions)
        {
            string[] extensions = (pathExtensions ?? ".EXE;.CMD;.BAT;.COM")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string directory in directories.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string expandedDirectory = Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"'));
                if (!Directory.Exists(expandedDirectory))
                {
                    continue;
                }
                foreach (string extension in extensions)
                {
                    string candidate = Path.Combine(expandedDirectory, commandName + extension.ToLowerInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                    candidate = Path.Combine(expandedDirectory, commandName + extension.ToUpperInvariant());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            return null;
        }

        internal static ProcessStartInfo CreateTerminalStartInfo(string executablePath, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new FileNotFoundException("未找到 CLI 程序。", executablePath);
            }
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException("要打开的目录不存在：" + workingDirectory);
            }

            string commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandProcessor) || !File.Exists(commandProcessor))
            {
                commandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            }
            return new ProcessStartInfo
            {
                FileName = commandProcessor,
                Arguments = "/d /s /k \"\"" + executablePath.Replace("\"", "\"\"") + "\"\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
        }

        private static List<string> GetShortcutRoots()
        {
            return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            }.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        }

        private static List<string> GetPathDirectories()
        {
            var values = new List<string>();
            foreach (EnvironmentVariableTarget target in new[]
            {
                EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.User,
                EnvironmentVariableTarget.Machine
            })
            {
                try
                {
                    string path = Environment.GetEnvironmentVariable("PATH", target);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        values.AddRange(path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                }
                catch (System.Security.SecurityException)
                {
                }
            }
            return values;
        }

        private static string GetPathExtensions()
        {
            string value = Environment.GetEnvironmentVariable("PATHEXT");
            return string.IsNullOrWhiteSpace(value) ? ".COM;.EXE;.BAT;.CMD" : value;
        }
    }

    internal sealed class IdeCliPickerForm : Form
    {
        private readonly string contextPath;
        private readonly Color background = Color.FromArgb(244, 245, 239);
        private readonly Color surface = Color.FromArgb(255, 255, 252);
        private readonly Color surfaceHover = Color.FromArgb(226, 235, 224);
        private readonly Color border = Color.FromArgb(23, 28, 24);
        private readonly Color textPrimary = Color.FromArgb(23, 28, 24);
        private readonly Color textSecondary = Color.FromArgb(91, 102, 94);
        private readonly Color accent = Color.FromArgb(151, 179, 155);
        private Label statusLabel;

        public IdeCliPickerForm(string appDirectory, string contextPath)
        {
            this.contextPath = contextPath;
            Text = "选择 IDE 或 CLI";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 480);
            Size = new Size(700, 560);
            BackColor = background;
            ForeColor = textPrimary;
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;

            string iconPath = Path.Combine(appDirectory, "cli-list.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
            Controls.Add(CreateLayout(IdeCliToolLocator.DetectTools()));
        }

        private Control CreateLayout(List<IdeCliToolInfo> tools)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(20)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 0, 8)
            };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            header.Controls.Add(new Label
            {
                Text = "[ DEV ]  选择 IDE 或 CLI",
                Dock = DockStyle.Fill,
                ForeColor = textPrimary,
                BackColor = accent,
                Font = new Font("Consolas", 11F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            }, 0, 0);
            header.Controls.Add(new Label
            {
                Text = "当前目录： " + (string.IsNullOrWhiteSpace(contextPath) ? "（无）" : contextPath),
                Dock = DockStyle.Fill,
                ForeColor = textSecondary,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(2, 0, 0, 0)
            }, 0, 1);

            var list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = background,
                Padding = new Padding(0, 4, 8, 4)
            };
            foreach (IdeCliToolInfo tool in tools)
            {
                list.Controls.Add(CreateToolButton(tool));
            }

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 10, 0, 6)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = textSecondary,
                Font = new Font("Microsoft YaHei UI", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(4, 0, 0, 4),
                Text = "检测到 " + tools.Count(tool => tool.IsAvailable).ToString(CultureInfo.InvariantCulture) +
                    " / " + tools.Count.ToString(CultureInfo.InvariantCulture) + " 个工具。"
            };
            var closeButton = CreateFooterButton("关闭");
            closeButton.Click += (sender, eventArgs) => Close();
            footer.Controls.Add(statusLabel, 0, 0);
            footer.Controls.Add(closeButton, 1, 0);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(list, 0, 1);
            root.Controls.Add(footer, 0, 2);
            return root;
        }

        private Button CreateToolButton(IdeCliToolInfo tool)
        {
            string availability = tool.IsAvailable ? "已检测到" : tool.DetectionSource;
            var button = new Button
            {
                Tag = tool,
                Width = 600,
                Height = 76,
                Margin = new Padding(2, 3, 2, 5),
                FlatStyle = FlatStyle.Flat,
                BackColor = surface,
                ForeColor = tool.IsAvailable ? textPrimary : textSecondary,
                Text = tool.Name + "  ·  " + availability + Environment.NewLine + tool.Description,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                Cursor = tool.IsAvailable ? Cursors.Hand : Cursors.Default,
                Enabled = tool.IsAvailable,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.MouseOverBackColor = surfaceHover;
            button.Click += (sender, eventArgs) => LaunchTool(tool);
            return button;
        }

        private Button CreateFooterButton(string text)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Height = 38,
                Margin = new Padding(4),
                ForeColor = textPrimary,
                BackColor = surface,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 2;
            return button;
        }

        private void LaunchTool(IdeCliToolInfo tool)
        {
            try
            {
                if (tool.LaunchKind == IdeCliLaunchKind.TerminalCli)
                {
                    using (var dialog = new FolderBrowserDialog
                    {
                        Description = "选择要用 Claude Code 打开的目录",
                        SelectedPath = Directory.Exists(contextPath) ? contextPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ShowNewFolderButton = true
                    })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK)
                        {
                            statusLabel.Text = "已取消选择目录。";
                            return;
                        }
                        Process.Start(IdeCliToolLocator.CreateTerminalStartInfo(tool.TargetPath, dialog.SelectedPath));
                    }
                }
                else if (tool.LaunchKind == IdeCliLaunchKind.AppModelId)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                        Arguments = tool.TargetPath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tool.TargetPath,
                        UseShellExecute = true
                    });
                }
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                statusLabel.Text = "启动失败：" + exception.Message;
            }
        }
    }

    internal sealed class BrowserPickerForm : Form
    {
        private readonly Color background = Color.FromArgb(244, 245, 239);
        private readonly Color surface = Color.FromArgb(255, 255, 252);
        private readonly Color surfaceHover = Color.FromArgb(226, 235, 224);
        private readonly Color border = Color.FromArgb(23, 28, 24);
        private readonly Color textPrimary = Color.FromArgb(23, 28, 24);
        private readonly Color textSecondary = Color.FromArgb(91, 102, 94);
        private readonly Color accent = Color.FromArgb(151, 179, 155);

        private Label statusLabel;

        public BrowserPickerForm(string appDirectory, string contextPath)
        {
            Text = "打开浏览器";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(560, 420);
            Size = new Size(680, 560);
            BackColor = background;
            ForeColor = textPrimary;
            Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;

            string iconPath = Path.Combine(appDirectory, "cli-list.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }

            Controls.Add(CreateLayout(contextPath));
        }

        private Control CreateLayout(string contextPath)
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
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            root.Controls.Add(CreateHeader(contextPath), 0, 0);
            root.Controls.Add(CreateBrowserList(), 0, 1);
            root.Controls.Add(CreateFooter(), 0, 2);
            return root;
        }

        private Control CreateHeader(string contextPath)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0, 0, 0, 8)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var title = new Label
            {
                Text = "[ BROWSER ]  选择要打开的浏览器",
                Dock = DockStyle.Fill,
                ForeColor = textPrimary,
                BackColor = accent,
                Font = new Font("Consolas", 11F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            };

            var contextLabel = new Label
            {
                Text = "当前目录： " + (string.IsNullOrWhiteSpace(contextPath) ? "（无）" : contextPath),
                Dock = DockStyle.Fill,
                ForeColor = textSecondary,
                Font = new Font("Consolas", 8.5F),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(2, 0, 0, 0)
            };

            header.Controls.Add(title, 0, 0);
            header.Controls.Add(contextLabel, 0, 1);
            return header;
        }

        private Control CreateBrowserList()
        {
            List<BrowserInfo> browsers = DetectBrowsers();

            var listPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = background,
                Padding = new Padding(0, 4, 8, 4)
            };

            if (browsers.Count == 0)
            {
                listPanel.Controls.Add(new Label
                {
                    Text = "未检测到已安装的浏览器。",
                    AutoSize = true,
                    ForeColor = textSecondary,
                    Font = new Font("Consolas", 10F),
                    Padding = new Padding(8, 16, 8, 8)
                });
                return listPanel;
            }

            foreach (BrowserInfo browser in browsers)
            {
                listPanel.Controls.Add(CreateBrowserButton(browser));
            }
            return listPanel;
        }

        private Button CreateBrowserButton(BrowserInfo browser)
        {
            var button = new Button
            {
                Tag = browser,
                Width = 560,
                Height = 62,
                Margin = new Padding(2, 3, 2, 3),
                FlatStyle = FlatStyle.Flat,
                BackColor = surface,
                ForeColor = textPrimary,
                Text = browser.Name + "\r\n" + browser.ExecutablePath,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = 2;
            button.FlatAppearance.MouseOverBackColor = surfaceHover;
            button.MouseEnter += (sender, eventArgs) => ((Button)sender).BackColor = surfaceHover;
            button.MouseLeave += (sender, eventArgs) => ((Button)sender).BackColor = surface;
            button.Click += (sender, eventArgs) => LaunchBrowser(browser);
            return button;
        }

        private Control CreateFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 10, 0, 6)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));

            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = textSecondary,
                Font = new Font("Consolas", 8.5F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                AutoSize = false,
                Padding = new Padding(4, 0, 0, 4),
                Text = "检测到 " + DetectBrowsers().Count + " 个浏览器，点击即可启动。"
            };

            var cancelButton = new Button
            {
                Text = "[关闭]",
                Dock = DockStyle.Fill,
                Height = 38,
                Margin = new Padding(4),
                ForeColor = textPrimary,
                BackColor = surface,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Consolas", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            cancelButton.FlatAppearance.BorderColor = border;
            cancelButton.FlatAppearance.BorderSize = 2;
            cancelButton.FlatAppearance.MouseOverBackColor = surfaceHover;
            cancelButton.FlatAppearance.MouseDownBackColor = accent;
            cancelButton.Click += (sender, eventArgs) => Close();

            footer.Controls.Add(statusLabel, 0, 0);
            footer.Controls.Add(cancelButton, 1, 0);
            return footer;
        }

        private void LaunchBrowser(BrowserInfo browser)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browser.ExecutablePath,
                    UseShellExecute = true
                });
                Close();
            }
            catch (Exception exception)
            {
                statusLabel.Text = "启动失败：" + exception.Message;
            }
        }

        internal static List<BrowserInfo> DetectBrowsers()
        {
            var browsers = new Dictionary<string, BrowserInfo>(StringComparer.OrdinalIgnoreCase);

            AddRegistryBrowsers(browsers, Registry.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet");
            AddRegistryBrowsers(browsers, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet");
            AddRegistryBrowsers(browsers, Registry.CurrentUser, @"SOFTWARE\Clients\StartMenuInternet");

            AddCandidate(browsers, "Microsoft Edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"));
            AddCandidate(browsers, "Microsoft Edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"));
            AddCandidate(browsers, "Google Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"));
            AddCandidate(browsers, "Google Chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"));
            AddCandidate(browsers, "Mozilla Firefox", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Mozilla Firefox\firefox.exe"));
            AddCandidate(browsers, "Mozilla Firefox", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Mozilla Firefox\firefox.exe"));
            AddCandidate(browsers, "Opera", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Opera\opera.exe"));
            AddCandidate(browsers, "Brave", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"BraveSoftware\Brave-Browser\Application\brave.exe"));
            AddCandidate(browsers, "360 安全浏览器", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"360\360se6\Application\360se.exe"));
            AddCandidate(browsers, "360 极速浏览器", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"360\360Chrome\Chrome\Application\360chrome.exe"));
            AddCandidate(browsers, "QQ 浏览器", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Tencent\QQBrowser\QQBrowser.exe"));
            AddCandidate(browsers, "搜狗高速浏览器", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"SogouExplorer\SogouExplorer.exe"));

            return browsers.Values
                .Where(browser => File.Exists(browser.ExecutablePath))
                .OrderBy(browser => browser.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddRegistryBrowsers(Dictionary<string, BrowserInfo> browsers, RegistryKey root, string subPath)
        {
            using (RegistryKey clientsKey = root.OpenSubKey(subPath))
            {
                if (clientsKey == null)
                {
                    return;
                }

                foreach (string browserKeyName in clientsKey.GetSubKeyNames())
                {
                    using (RegistryKey browserKey = clientsKey.OpenSubKey(browserKeyName))
                    {
                        if (browserKey == null)
                        {
                            continue;
                        }

                        string command = null;
                        using (RegistryKey commandKey = browserKey.OpenSubKey(@"shell\open\command"))
                        {
                            if (commandKey != null)
                            {
                                command = commandKey.GetValue(null) as string;
                            }
                        }

                        string exePath = ParseExecutablePath(command);
                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                        {
                            continue;
                        }

                        AddUnique(browsers, GetBrowserDisplayName(browserKey, browserKeyName), exePath);
                    }
                }
            }
        }

        private static string GetBrowserDisplayName(RegistryKey browserKey, string fallbackName)
        {
            string displayName = browserKey.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@", StringComparison.Ordinal))
            {
                return displayName;
            }

            int dashIndex = fallbackName.LastIndexOf('-');
            if (dashIndex > 0 && dashIndex < fallbackName.Length - 1 && IsHexSuffix(fallbackName, dashIndex + 1))
            {
                return fallbackName.Substring(0, dashIndex);
            }
            return fallbackName;
        }

        private static bool IsHexSuffix(string text, int startIndex)
        {
            for (int index = startIndex; index < text.Length; index++)
            {
                if (!Uri.IsHexDigit(text[index]))
                {
                    return false;
                }
            }
            return text.Length - startIndex == 8 || text.Length - startIndex == 16;
        }

        private static string ParseExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            string trimmed = command.Trim();
            if (trimmed.StartsWith("\"", StringComparison.Ordinal))
            {
                int endQuote = trimmed.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    return Environment.ExpandEnvironmentVariables(trimmed.Substring(1, endQuote - 1));
                }
            }

            int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex < 0)
            {
                return null;
            }
            int start = trimmed.LastIndexOf(' ', exeIndex);
            int end = trimmed.IndexOf(' ', exeIndex);
            if (end < 0)
            {
                end = trimmed.Length;
            }
            return Environment.ExpandEnvironmentVariables(trimmed.Substring(start + 1, end - start - 1).Trim('"'));
        }

        private static void AddCandidate(Dictionary<string, BrowserInfo> browsers, string name, string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                AddUnique(browsers, name, path);
            }
        }

        private static void AddUnique(Dictionary<string, BrowserInfo> browsers, string name, string exePath)
        {
            string fullPath = Path.GetFullPath(exePath);
            if (!browsers.ContainsKey(fullPath))
            {
                browsers[fullPath] = new BrowserInfo { Name = name, ExecutablePath = fullPath };
            }
        }

        internal sealed class BrowserInfo
        {
            public string Name { get; set; }
            public string ExecutablePath { get; set; }
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
            var previewFile = new FileInfo(path);
            if (previewFile.Length > 20L * 1024 * 1024)
            {
                ShowPreviewMessage("图片超过 20MB，已跳过预览。\n\n路径：" + path);
                return;
            }
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

    internal static class Installer
    {
        private const string RegistryKeyPath = @"Software\CliListApp";
        private const string InstalledValueName = "InstalledPath";

        private static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CLIList"
        );

        private static readonly string[] ContextMenuPaths = new[]
        {
            @"Software\Classes\Directory\Background\shell\CLIList",
            @"Software\Classes\Directory\shell\CLIList",
            @"Software\Classes\DesktopBackground\Shell\CLIList",
            @"Software\Classes\*\shell\CLIList",
            @"Software\Classes\Drive\shell\CLIList"
        };

        // 返回 true 表示引导流程已处理（已完成安装并转交新进程），调用方应直接退出当前进程。
        public static bool TryEnsureInstalled(string appDirectory, string contextArgument, bool residentRequested)
        {
            // 后台驻留模式（开机启动/托盘重启）不弹安装引导。
            if (residentRequested || IsInstalled())
            {
                return false;
            }

            using (var form = new BootstrapperForm(InstallDirectory))
            {
                if (form.ShowDialog() != DialogResult.Yes)
                {
                    return false;
                }
            }

            try
            {
                Install(appDirectory);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "安装失败：\n\n" + exception.Message,
                    "CLI List",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return false;
            }

            string installedExecutable = Path.Combine(InstallDirectory, "CLIList.exe");
            var launchArguments = new List<string>();
            if (residentRequested)
            {
                launchArguments.Add("--resident");
            }
            else if (!string.IsNullOrWhiteSpace(contextArgument))
            {
                launchArguments.Add("\"" + contextArgument.Trim('"') + "\"");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installedExecutable,
                Arguments = string.Join(" ", launchArguments),
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = true
            });
            return true;
        }

        public static bool IsInstalled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string installedPath = key.GetValue(InstalledValueName) as string;
                    return !string.IsNullOrWhiteSpace(installedPath) && File.Exists(installedPath);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsProductInstallDirectory(string directoryPath)
        {
            string expected = Path.GetFullPath(InstallDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string actual = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        public static void Install(string sourceDirectory)
        {
            Directory.CreateDirectory(InstallDirectory);

            foreach (string fileName in new[] { "CLIList.exe", "cli-list.ico", "cli-list.svg", "commands.json", "minimize-all.vbs", "update-helper.ps1" })
            {
                string sourcePath = Path.Combine(sourceDirectory, fileName);
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, Path.Combine(InstallDirectory, fileName), true);
                }
            }

            string executablePath = Path.Combine(InstallDirectory, "CLIList.exe");
            string iconPath = Path.Combine(InstallDirectory, "cli-list.ico");

            foreach (string contextPath in ContextMenuPaths)
            {
                using (RegistryKey shellKey = Registry.CurrentUser.CreateSubKey(contextPath))
                {
                    if (shellKey == null)
                    {
                        continue;
                    }

                    shellKey.SetValue(null, "CLI List");
                    if (File.Exists(iconPath))
                    {
                        shellKey.SetValue("Icon", iconPath);
                    }

                    bool isBackground = contextPath.Contains(@"Directory\Background") || contextPath.Contains("DesktopBackground");
                    string placeholder = isBackground ? "%V" : "%1";
                    using (RegistryKey commandKey = shellKey.CreateSubKey("command"))
                    {
                        if (commandKey != null)
                        {
                            commandKey.SetValue(null, "\"" + executablePath + "\" \"" + placeholder + "\"");
                        }
                    }
                }
            }

            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CLI List Resident.lnk"),
                executablePath,
                "--resident",
                "启动 CLI List 托盘与全局快捷键"
            );
            CreateShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CLI List.lnk"),
                executablePath,
                string.Empty,
                "打开 CLI List 命令面板"
            );

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath))
            {
                if (key != null)
                {
                    key.SetValue(InstalledValueName, executablePath);
                }
            }
        }

        public static void Uninstall()
        {
            foreach (string contextPath in ContextMenuPaths)
            {
                Registry.CurrentUser.DeleteSubKeyTree(contextPath, false);
            }

            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CLI List Resident.lnk"));
            RemoveShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CLI List.lnk"));

            Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, false);

            try
            {
                if (Directory.Exists(InstallDirectory))
                {
                    Directory.Delete(InstallDirectory, true);
                }
            }
            catch
            {
                // 安装目录可能正被占用，留给用户手动清理。
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return;
                }

                object shell = Activator.CreateInstance(shellType);
                object shortcut = shell.GetType().InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath }
                );
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetPath) });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath + ",0" });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch
            {
                // 快捷方式创建失败不阻止安装主体完成。
            }
        }

        private static void RemoveShortcut(string shortcutPath)
        {
            try
            {
                if (File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class BootstrapperForm : Form
    {
        private readonly Color background = Color.FromArgb(244, 245, 239);
        private readonly Color surface = Color.FromArgb(255, 255, 252);
        private readonly Color surfaceHover = Color.FromArgb(226, 235, 224);
        private readonly Color textPrimary = Color.FromArgb(23, 28, 24);
        private readonly Color textSecondary = Color.FromArgb(91, 102, 94);
        private readonly Color accent = Color.FromArgb(151, 179, 155);
        private readonly Color border = Color.FromArgb(23, 28, 24);

        public BootstrapperForm(string installDirectory)
        {
            Text = "安装 CLI List";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(540, 340);
            BackColor = background;
            ForeColor = textPrimary;
            Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(28, 24, 28, 20)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));

            var title = new Label
            {
                Text = "CLI LIST 安装",
                ForeColor = textPrimary,
                Font = new Font("Consolas", 18F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            var body = new Label
            {
                Text = "这是 CLI List 首次运行。\n\n" +
                       "将安装到：" + installDirectory + "\n\n" +
                       "• 在桌面、文件夹和磁盘的右键菜单添加 “CLI List”\n" +
                       "• 注册全局快捷键 Ctrl + Alt + Space\n" +
                       "• 开机自动驻留系统托盘\n" +
                       "• 创建桌面快捷方式\n\n" +
                       "无需管理员权限，可随时从托盘菜单卸载。",
                ForeColor = textSecondary,
                Font = new Font("Microsoft YaHei UI", 9F),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft
            };

            var bodyPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty
            };
            bodyPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bodyPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            bodyPanel.Controls.Add(title, 0, 0);
            bodyPanel.Controls.Add(body, 0, 1);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = background
            };
            var installButton = CreateActionButton("[ 安装 ]", 96);
            installButton.Click += (sender, eventArgs) => { DialogResult = DialogResult.Yes; Close(); };
            var skipButton = CreateActionButton("[ 仅本次运行 ]", 132);
            skipButton.Click += (sender, eventArgs) => { DialogResult = DialogResult.No; Close(); };
            actions.Controls.Add(installButton);
            actions.Controls.Add(skipButton);

            root.Controls.Add(bodyPanel, 0, 0);
            root.Controls.Add(actions, 0, 1);
            Controls.Add(root);
        }

        private Button CreateActionButton(string text, int width)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 38,
                Margin = new Padding(8, 0, 0, 0),
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
    }
}







