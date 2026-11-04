using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using AgShell.Core;
using AgShell.Protocol;
using Microsoft.Win32;

namespace AgShell.App
{
    /// <summary>agterm's Help menu installers, for the portable zip and for running them again: PATH, hooks, skill.</summary>
    public partial class MainWindow
    {
        /// <summary>
        /// Puts agshell's folder on the user's PATH, as the installer does, so agshellctl works in new terminals, drops other
        /// folders holding agshellctl.exe so an older copy is not found first, and reports in a modal as agterm does.
        /// </summary>
        private void InstallCommandLineTool()
        {
            string folder = AppContext.BaseDirectory.TrimEnd('\\');
            UserPathChange change;
            List<string> machineRemoved;
            try
            {
                // The system PATH comes first, so an older agshellctl there would still win; agterm asks for admin rights when
                // its link needs them, and a dismissed prompt changes and shows nothing.
                switch (RemoveFromMachinePath(folder, out machineRemoved))
                {
                    case MachinePathResult.Cancelled:
                        return;
                    case MachinePathResult.Failed failed:
                        ShowInstallResult(true, Strings.T("Не удалось установить"), failed.Message);
                        return;
                }

                using RegistryKey environment = Registry.CurrentUser.OpenSubKey("Environment", writable: true) ?? throw new IOException(@"HKEY_CURRENT_USER\Environment cannot be opened");
                string path = environment.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
                change = UserPathInstall.Apply(path, folder, Environment.ExpandEnvironmentVariables, HoldsAgShellctl);
                if (change.Path != path)
                {
                    environment.SetValue("Path", change.Path, RegistryValueKind.ExpandString);
                    SendMessageTimeout(new IntPtr(0xFFFF), 0x1A, IntPtr.Zero, "Environment", 0x2, 1000, out _);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException)
            {
                ShowInstallResult(true, Strings.T("Не удалось установить"), ex.Message);
                return;
            }

            string text = (change.AlreadyPresent ? Strings.T("agshellctl уже в PATH пользователя: {0}.", folder) : Strings.T("agshellctl добавлен в PATH пользователя: {0}.", folder))
                + " " + Strings.T("Откройте новый терминал и запустите «agshellctl --help».");
            if (change.Removed.Count + machineRemoved.Count > 0)
            {
                text += "\n\n" + Strings.T("Из PATH убраны другие папки с agshellctl.exe:") + "\n" + string.Join("\n", machineRemoved.Concat(change.Removed).Select(entry => "  • " + entry));
            }

            ShowInstallResult(false, Strings.T("Инструмент командной строки установлен"), text);
        }

        private abstract record MachinePathResult
        {
            public sealed record Done : MachinePathResult;

            public sealed record Cancelled : MachinePathResult;

            public sealed record Failed(string Message) : MachinePathResult;
        }

        private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

        /// <summary>Drops the other agshellctl folders from the system PATH through one elevated write, when it has any.</summary>
        private static MachinePathResult RemoveFromMachinePath(string folder, out List<string> removed)
        {
            removed = new List<string>();
            string path;
            using (RegistryKey? environment = Registry.LocalMachine.OpenSubKey(MachineEnvironment))
            {
                path = environment?.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
            }

            UserPathChange change = UserPathInstall.Without(path, folder, Environment.ExpandEnvironmentVariables, HoldsAgShellctl);
            if (change.Removed.Count == 0)
            {
                return new MachinePathResult.Done();
            }

            string script = "Set-ItemProperty -LiteralPath 'HKLM:\\" + MachineEnvironment + "' -Name Path -Type ExpandString -Value '" + change.Path.Replace("'", "''") + "'";
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using Process process = Process.Start(start) ?? throw new IOException("powershell.exe did not start");
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    return new MachinePathResult.Failed(Strings.T("Системный PATH не изменён (код {0}).", process.ExitCode));
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return new MachinePathResult.Cancelled();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new MachinePathResult.Failed(ex.Message);
            }

            removed.AddRange(change.Removed);
            SendMessageTimeout(new IntPtr(0xFFFF), 0x1A, IntPtr.Zero, "Environment", 0x2, 1000, out _);
            return new MachinePathResult.Done();
        }

        /// <summary>
        /// Registers the AgShell event log provider for this copy of agshell (wevtutil im with its manifest and resources),
        /// as the installer does; it needs administrator rights, asked through UAC, and a dismissed prompt changes and shows
        /// nothing, as agterm's dismissed admin prompt.
        /// </summary>
        private void RegisterEventLog()
        {
            string manifest = Path.Combine(AppContext.BaseDirectory, "AgShell.man");
            string resources = Path.Combine(AppContext.BaseDirectory, "AgShell.EventLog.dll");
            foreach (string file in new[] { manifest, resources })
            {
                if (!File.Exists(file))
                {
                    ShowInstallResult(true, Strings.T("Не удалось зарегистрировать журнал событий"), Strings.T("Рядом с agshell.exe нет {0}.", Path.GetFileName(file)));
                    return;
                }
            }

            string script = EventLogRegistration.RegisterScript(manifest, resources);
            ProcessStartInfo start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using Process process = Process.Start(start) ?? throw new IOException("powershell.exe did not start");
                process.WaitForExit();
                Log.Refresh();
                if (process.ExitCode != 0)
                {
                    ShowInstallResult(true, Strings.T("Не удалось зарегистрировать журнал событий"), Strings.T("Регистрация завершилась с кодом {0}.", process.ExitCode));
                    return;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                ShowInstallResult(true, Strings.T("Не удалось зарегистрировать журнал событий"), ex.Message);
                return;
            }

            ShowInstallResult(false, Strings.T("Журнал событий зарегистрирован"), Strings.T("События AgShell пишутся в журнал Windows: «Просмотр событий» ▸ «Журналы приложений и служб» ▸ AgShell ▸ Operational."));
        }

        private const int ErrorCancelled = 1223;

        private static bool HoldsAgShellctl(string folder)
        {
            return Path.IsPathFullyQualified(folder) && File.Exists(Path.Combine(folder, UserPathInstall.ToolName));
        }

        /// <summary>agterm's result alert: a modal over the window, a warning when something failed or was skipped.</summary>
        private void ShowInstallResult(bool problem, string title, string text)
        {
            MessageBox.Show(this, title + "\n\n" + text, "AgShell", MessageBoxButton.OK, problem ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>agshellctl install hooks' exit code when OpenCode is set up but its version needs --opencode.</summary>
        private const int OpenCodeVersionNeeded = 2;

        /// <summary>Runs agshellctl install hooks|skill and reports what it says.</summary>
        private async Task RunInstallerAsync(string what)
        {
            (string Output, string Errors, int ExitCode)? run = await RunAgShellctlAsync(what, what == "skill" ? "install skill --json" : "install " + what);
            if (run is null)
            {
                return;
            }

            (string output, string errors, int exitCode) = run.Value;
            if (what == "hooks" && exitCode == OpenCodeVersionNeeded)
            {
                // agterm's chooser for a version the login shell does not tell; the run again installs nothing twice.
                run = await RunAgShellctlAsync(what, "install hooks --opencode " + ChooseOpenCodeVersion());
                if (run is null)
                {
                    return;
                }

                (output, errors, exitCode) = run.Value;
            }

            if (what == "skill")
            {
                ShowSkillReport(output, errors);
                return;
            }

            // agterm's result alert: what each agent got, a warning when a step failed.
            ShowInstallResult(exitCode != 0, exitCode == 0 ? Strings.T("Хуки статусов агентов установлены") : Strings.T("Хуки статусов агентов установлены — с предупреждением"), (output + errors).Trim());
        }

        /// <summary>agshellctl's output through agterm's capture, or null once the failure to run it is reported.</summary>
        private async Task<(string Output, string Errors, int ExitCode)?> RunAgShellctlAsync(string what, string arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "agshellctl.exe"), arguments)
            {
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            string? failure;
            try
            {
                ProcessOutput? output = await ProcessOutputCapture.RunAsync(start, null);
                if (output is { ExitCode: int exitCode })
                {
                    return (output.Stdout, output.Stderr, exitCode);
                }

                failure = Strings.T("Вывод agshellctl не дочитан: поток держит другой процесс.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                failure = ex.Message;
            }

            ShowInstallResult(true, what == "skill" ? Strings.T("Не удалось запустить agshellctl") : Strings.T("Не удалось установить"), failure);

            return null;
        }

        /// <summary>agterm's makeOpenCodeVersionAlert: skip OpenCode, or the version the user runs. Answers --opencode's value.</summary>
        private string ChooseOpenCodeVersion()
        {
            string answer = "skip";
            Window dialog = new Window
            {
                Owner = this,
                Title = "AgShell",
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            StackPanel content = new StackPanel { Margin = new Thickness(24, 18, 24, 16), MaxWidth = 460 };
            content.Children.Add(new TextBlock { Text = Strings.T("Выберите версию OpenCode"), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            content.Children.Add(new TextBlock
            {
                Text = Strings.T("Не удалось определить версию OpenCode в шелле входа (opencode --version). Выберите версию, которой пользуетесь, или пропустите OpenCode и установите остальные хуки."),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            foreach ((string label, string value) in new[] { (Strings.T("Пропустить OpenCode"), "skip"), ("OpenCode v1", "v1"), ("OpenCode v2", "v2") })
            {
                Button button = new Button { Content = label, MinWidth = 90, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), IsCancel = value == "skip", IsDefault = value == "skip" };
                button.Click += (sender, args) =>
                {
                    answer = value;
                    dialog.Close();
                };
                buttons.Children.Add(button);
            }

            content.Children.Add(buttons);
            dialog.Content = content;
            dialog.ShowDialog();
            return answer;
        }

        /// <summary>agterm's skill alert, built from the report agshellctl install skill --json prints.</summary>
        private void ShowSkillReport(string output, string errors)
        {
            JsonObject? report;
            try
            {
                report = JsonNode.Parse(output) as JsonObject;
            }
            catch (JsonException)
            {
                report = null;
            }

            if (report is null || report["error"] is JsonNode)
            {
                string message = report?["error"]?.GetValue<string>() ?? (output + errors).Trim();
                ShowInstallResult(true, Strings.T("Не удалось установить"), message);
                return;
            }

            List<string> installed = Entries(report["installed"], "path");
            List<string> skipped = Entries(report["skipped"], "reason");
            List<string> lines = new List<string>();
            if (installed.Count == 0)
            {
                lines.Add(Strings.T("Скилл не установлен."));
            }
            else
            {
                lines.Add(Strings.T("Установлен для:"));
                lines.AddRange(installed);
                lines.Add(string.Empty);
                lines.Add(Strings.T("Агент берёт его сам, когда вы просите его управлять AgShell. Управляет он через agshellctl, так что поставьте и его, если ещё не поставили: палитра действий ▸ «Добавить agshellctl в PATH»."));
            }

            if (skipped.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add(Strings.T("Пропущено:"));
                lines.AddRange(skipped);
            }

            if (report["freshSkillsDirectory"]?.GetValue<bool>() == true)
            {
                lines.Add(string.Empty);
                lines.Add(Strings.T("Каталог скиллов создан впервые — перезапустите работающие сессии агентов, чтобы они подхватили новый скилл."));
            }

            bool problem = installed.Count == 0 || skipped.Count > 0;
            ShowInstallResult(problem, installed.Count == 0 ? Strings.T("Скилл не установлен") : Strings.T("Скилл для агентов установлен"), string.Join("\n", lines));
        }

        /// <summary>The report's lines «• agent (place): detail»; a foreign skill's reason comes in the interface language.</summary>
        private static List<string> Entries(JsonNode? node, string detail)
        {
            List<string> lines = new List<string>();
            foreach (JsonObject entry in (node as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                string text = entry["foreign"]?.GetValue<bool>() == true
                    ? Strings.T("здесь уже есть другой скилл «agshell» — он не тронут")
                    : entry[detail]?.GetValue<string>() ?? string.Empty;
                lines.Add("  • " + entry["agent"]?.GetValue<string>() + " (" + entry["place"]?.GetValue<string>() + "): " + text);
            }

            return lines;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
