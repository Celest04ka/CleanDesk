// CleanDesk — a tiny tray app that hides and shows the Windows taskbar and desktop icons
// with global hotkeys. Settings live in CleanDesk.ini next to the exe.
//
// How it works:
//  * Taskbar. Hiding turns on the regular "Automatically hide the taskbar" option, so maximized
//    windows get the whole screen, and also hides the taskbar window itself (SW_HIDE), so it does
//    not slide out when the mouse touches the screen edge. While the taskbar is hidden, a watchdog
//    hides it again 4 times a second if Explorer brings it back (explorer restart, new monitor,
//    Win key). Showing the taskbar or exiting restores the auto-hide option to what it was.
//    That original value is also kept in the registry (HKCU\Software\CleanDesk) while the taskbar
//    is hidden, so after a crash the next launch puts everything back.
//  * Desktop icons. Explorer gets the same command as "View > Show desktop icons" in the desktop
//    context menu, so this is a normal Windows setting that stays as is after CleanDesk exits.
//  * CleanDesk.ini is checked once a second; saved changes apply without a restart.
//
// Build: build.ps1 (uses csc from .NET Framework 4.x, which ships with Windows 10/11).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("CleanDesk")]
[assembly: AssemblyProduct("CleanDesk")]
[assembly: AssemblyDescription("Hide the taskbar and desktop icons with a hotkey")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace CleanDesk
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool firstInstance;
            using (var mutex = new Mutex(true, @"Local\CleanDesk", out firstInstance))
            {
                if (!firstInstance)
                {
                    Config.Load();   // picks the UI language
                    MessageBox.Show(string.Format(Strings.Current.AlreadyRunning, Config.FileName),
                        "CleanDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var app = new TrayApp())
                {
                    app.Start();
                    Application.Run(app);
                }
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        const int TaskbarHotkeyId = 1;
        const int IconsHotkeyId = 2;
        const int WatchdogMs = 250;

        readonly MessageWindow window = new MessageWindow();
        readonly NotifyIcon trayIcon = new NotifyIcon();
        readonly ToolStripMenuItem taskbarItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem iconsItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem settingsItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem autostartItem = new ToolStripMenuItem();
        readonly ToolStripMenuItem exitItem = new ToolStripMenuItem();
        readonly System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer configWatcher = new System.Windows.Forms.Timer();
        Config config;
        DateTime configTime;
        bool hidden;
        int stateBeforeHide;
        int ticks;

        static Strings L { get { return Strings.Current; } }

        public void Start()
        {
            Recovery.RestoreIfNeeded();

            window.HotkeyPressed += OnHotkey;
            window.SessionEnding += delegate { if (hidden) ShowTaskbar(); };

            taskbarItem.Click += delegate { ToggleTaskbar(); };
            iconsItem.Click += delegate { ToggleIcons(); };
            settingsItem.Click += delegate { OpenConfig(); };
            autostartItem.Click += delegate { ToggleAutostart(); };
            exitItem.Click += delegate { Exit(); };
            var menu = new ContextMenuStrip();
            menu.Items.AddRange(new ToolStripItem[] {
                taskbarItem, iconsItem, new ToolStripSeparator(),
                settingsItem, autostartItem, new ToolStripSeparator(),
                exitItem });
            menu.Opening += delegate { UpdateMenu(); };

            trayIcon.Icon = LoadIcon();
            trayIcon.ContextMenuStrip = menu;
            trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleTaskbar(); };
            trayIcon.Visible = true;

            watchdog.Interval = WatchdogMs;
            watchdog.Tick += delegate { KeepHidden(); };

            ApplyConfig(false);
            configWatcher.Interval = 1000;
            configWatcher.Tick += delegate { if (Config.LastWriteTime() != configTime) ApplyConfig(true); };
            configWatcher.Start();
        }

        // ---------- settings and hotkeys ----------

        void ApplyConfig(bool announce)
        {
            config = Config.Load();
            configTime = Config.LastWriteTime();
            var problems = new List<string>(config.Errors);

            try { Autostart.Apply(config.StartWithWindows); }
            catch (Exception e) { problems.Add(string.Format(L.AutostartFailed, e.Message)); }

            Native.UnregisterHotKey(window.Handle, TaskbarHotkeyId);
            Native.UnregisterHotKey(window.Handle, IconsHotkeyId);

            bool taskbarOk = Register(TaskbarHotkeyId, config.Taskbar, "Taskbar", problems);
            if (config.DesktopIcons != null && config.DesktopIcons.SameAs(config.Taskbar))
                problems.Add(string.Format(L.SameAs, "DesktopIcons", config.DesktopIcons.Text, "Taskbar"));
            else
                Register(IconsHotkeyId, config.DesktopIcons, "DesktopIcons", problems);

            // Without a working hotkey a hidden taskbar can't be brought back (the tray icon is hidden too).
            if (hidden && !taskbarOk) ShowTaskbar();

            string summary = L.TaskbarLong + ": " + Hotkey.Describe(config.Taskbar) +
                             "\n" + L.IconsLong + ": " + Hotkey.Describe(config.DesktopIcons);
            trayIcon.Text = Limit("CleanDesk\n" + L.TaskbarShort + ": " + Hotkey.Describe(config.Taskbar) +
                                  "\n" + L.IconsShort + ": " + Hotkey.Describe(config.DesktopIcons), 63);

            if (problems.Count > 0)
                Notify(L.SettingsProblem, string.Join("\n", problems.ToArray()) + "\n\n" + summary, ToolTipIcon.Warning);
            else if (announce)
                Notify(L.SettingsUpdated, summary, ToolTipIcon.Info);
        }

        bool Register(int id, Hotkey hotkey, string name, List<string> problems)
        {
            if (hotkey == null) return false;
            if (Native.RegisterHotKey(window.Handle, id, hotkey.Modifiers | Native.MOD_NOREPEAT, (uint)hotkey.Key))
                return true;
            problems.Add(string.Format(L.Taken, name, hotkey.Text));
            return false;
        }

        void OnHotkey(int id)
        {
            if (id == TaskbarHotkeyId) ToggleTaskbar();
            else if (id == IconsHotkeyId) ToggleIcons();
        }

        void OpenConfig()
        {
            if (!File.Exists(Config.FilePath)) ApplyConfig(false);
            try { Process.Start("notepad.exe", "\"" + Config.FilePath + "\""); }
            catch (Exception e) { Notify(string.Format(L.CantOpen, Config.FileName), e.Message, ToolTipIcon.Warning); }
        }

        // The ini file stays the single source of truth: the menu item just rewrites its line.
        void ToggleAutostart()
        {
            try { Config.SetValue(Config.FilePath, "StartWithWindows", config.StartWithWindows ? "no" : "yes"); }
            catch (Exception e)
            {
                Notify(string.Format(L.WriteFailed, Config.FileName), e.Message, ToolTipIcon.Warning);
                return;
            }
            ApplyConfig(false);
        }

        // ---------- taskbar ----------

        void ToggleTaskbar()
        {
            if (hidden) ShowTaskbar(); else HideTaskbar();
        }

        void HideTaskbar()
        {
            stateBeforeHide = Taskbar.GetState();
            Recovery.Remember(stateBeforeHide);
            hidden = true;
            Taskbar.HideWindows();
            Taskbar.SetState(stateBeforeHide | Native.ABS_AUTOHIDE);
            Taskbar.HideWindows();
            ticks = 0;
            watchdog.Start();
        }

        void ShowTaskbar()
        {
            watchdog.Stop();
            hidden = false;
            Taskbar.ShowWindows();
            Taskbar.SetState(stateBeforeHide);
            Recovery.Forget();
        }

        void KeepHidden()
        {
            if (!hidden) return;
            Taskbar.HideWindows();
            // Auto-hide may have been turned off (explorer restart, Settings) — that would leave an empty strip.
            if (++ticks % 8 == 0 && (Taskbar.GetState() & Native.ABS_AUTOHIDE) == 0)
                Taskbar.SetState(stateBeforeHide | Native.ABS_AUTOHIDE);
        }

        // ---------- desktop icons ----------

        void ToggleIcons()
        {
            if (!DesktopIcons.Toggle())
                Notify(L.IconsLong, L.IconsNotFound, ToolTipIcon.Warning);
        }

        // ---------- tray ----------

        void UpdateMenu()
        {
            taskbarItem.Text = hidden ? L.ShowTaskbar : L.HideTaskbar;
            taskbarItem.ShortcutKeyDisplayString = config.Taskbar == null ? "" : config.Taskbar.Text;
            iconsItem.Text = DesktopIcons.AreVisible() ? L.HideIcons : L.ShowIcons;
            iconsItem.ShortcutKeyDisplayString = config.DesktopIcons == null ? "" : config.DesktopIcons.Text;
            settingsItem.Text = L.OpenSettings;
            autostartItem.Text = L.StartWithWindows;
            autostartItem.Checked = config.StartWithWindows;
            exitItem.Text = L.Exit;
        }

        void Notify(string title, string text, ToolTipIcon icon)
        {
            trayIcon.ShowBalloonTip(icon == ToolTipIcon.Warning ? 10000 : 3000,
                Limit("CleanDesk: " + title, 63), Limit(text, 255), icon);
        }

        static string Limit(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        void Exit()
        {
            if (hidden) ShowTaskbar();
            trayIcon.Visible = false;
            ExitThread();
        }

        static Icon LoadIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CleanDesk.ico"))
                    if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (hidden) ShowTaskbar();
                configWatcher.Dispose();
                watchdog.Dispose();
                trayIcon.Dispose();
                Native.UnregisterHotKey(window.Handle, TaskbarHotkeyId);
                Native.UnregisterHotKey(window.Handle, IconsHotkeyId);
                window.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // CleanDesk.ini: "Name = value" lines; comments start with ;
    sealed class Config
    {
        public const string FileName = "CleanDesk.ini";
        public const string DefaultTaskbar = "Ctrl+Alt+L";
        public const string DefaultDesktopIcons = "Ctrl+Alt+K";

        public Hotkey Taskbar;
        public Hotkey DesktopIcons;
        public bool StartWithWindows;
        public readonly List<string> Errors = new List<string>();

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        public static DateTime LastWriteTime()
        {
            try { return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        // Also sets Strings.Current. Creates the file with defaults if it is missing.
        public static Config Load()
        {
            Strings.Current = Strings.Detect();
            var config = new Config();
            string[] lines;
            try
            {
                if (!File.Exists(FilePath)) File.WriteAllText(FilePath, Strings.Current.DefaultIni, Encoding.UTF8);
                lines = File.ReadAllLines(FilePath);
            }
            catch (Exception e)
            {
                lines = new string[0];
                config.Errors.Add(string.Format(Strings.Current.ReadFailed, FileName, e.Message));
            }
            config.Parse(lines);
            return config;
        }

        void Parse(string[] lines)
        {
            string error;
            Hotkey.TryParse(DefaultTaskbar, out Taskbar, out error);
            Hotkey.TryParse(DefaultDesktopIcons, out DesktopIcons, out error);

            // Language goes first, so that every message below uses it.
            var entries = new List<Tuple<int, string, string>>();
            string badLanguage = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;

                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    entries.Add(Tuple.Create(i + 1, line, (string)null));
                    continue;
                }
                string name = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (name.Equals("Language", StringComparison.OrdinalIgnoreCase))
                {
                    Strings strings = Strings.ForSetting(value);
                    if (strings != null) Strings.Current = strings; else badLanguage = value;
                }
                else entries.Add(Tuple.Create(i + 1, name, value));
            }

            Strings L = Strings.Current;
            if (badLanguage != null) Errors.Add(string.Format(L.BadLanguage, badLanguage));

            foreach (var entry in entries)
            {
                int lineNumber = entry.Item1;
                string name = entry.Item2, value = entry.Item3;
                if (value == null)
                {
                    Errors.Add(string.Format(L.NoEquals, lineNumber, name));
                    continue;
                }

                if (name.Equals("Taskbar", StringComparison.OrdinalIgnoreCase)) Taskbar = ParseHotkey(name, value);
                else if (name.Equals("DesktopIcons", StringComparison.OrdinalIgnoreCase)) DesktopIcons = ParseHotkey(name, value);
                else if (name.Equals("StartWithWindows", StringComparison.OrdinalIgnoreCase))
                {
                    bool? yes = ParseYesNo(value);
                    if (yes.HasValue) StartWithWindows = yes.Value;
                    else Errors.Add(string.Format(L.BadYesNo, name, value));
                }
                else Errors.Add(string.Format(L.UnknownSetting, lineNumber, name));
            }
        }

        Hotkey ParseHotkey(string name, string value)
        {
            Hotkey hotkey;
            string error;
            if (!Hotkey.TryParse(value, out hotkey, out error))
                Errors.Add(name + " = " + value + ": " + error + ".");
            return hotkey;
        }

        static bool? ParseYesNo(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "yes": case "true": case "on": case "1": case "да": return true;
                case "no": case "false": case "off": case "0": case "нет": return false;
                default: return null;
            }
        }

        // Replaces the "name = ..." line (or appends one), keeping comments and everything else.
        public static void SetValue(string path, string name, string value)
        {
            var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path) : new string[0]);
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == ';' || eq < 0) continue;
                if (!line.Substring(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = name + " = " + value;
                found = true;
            }
            if (!found) lines.Add(name + " = " + value);
            File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
        }
    }

    sealed class Hotkey
    {
        public uint Modifiers;
        public Keys Key;
        public string Text;

        // A Russian letter means the Latin letter on the same physical key.
        const string RuLayout = "йцукенгшщзхъфывапролджэячсмитьбюё";
        const string EnLayout = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";

        static readonly Dictionary<string, Keys> Aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            { "Esc", Keys.Escape }, { "Enter", Keys.Return }, { "Del", Keys.Delete }, { "Ins", Keys.Insert },
            { "PgUp", Keys.PageUp }, { "PgDn", Keys.PageDown }, { "Backspace", Keys.Back }, { "PrtSc", Keys.PrintScreen },
            { "Plus", Keys.Oemplus }, { "Minus", Keys.OemMinus },
            { "-", Keys.OemMinus }, { "=", Keys.Oemplus }, { "[", Keys.OemOpenBrackets }, { "]", Keys.OemCloseBrackets },
            { ";", Keys.OemSemicolon }, { "'", Keys.OemQuotes }, { ",", Keys.Oemcomma }, { ".", Keys.OemPeriod },
            { "/", Keys.OemQuestion }, { "\\", Keys.OemPipe }, { "`", Keys.Oemtilde }, { "~", Keys.Oemtilde },
        };

        public bool SameAs(Hotkey other)
        {
            return other != null && other.Modifiers == Modifiers && other.Key == Key;
        }

        public static string Describe(Hotkey hotkey)
        {
            return hotkey == null ? Strings.Current.Off : hotkey.Text;
        }

        // Empty or "none" turns the action off: hotkey = null and the result is true.
        public static bool TryParse(string text, out Hotkey hotkey, out string error)
        {
            Strings L = Strings.Current;
            hotkey = null;
            error = null;
            text = text.Trim();
            if (text.Length == 0 || text.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;

            uint modifiers = 0;
            Keys key = Keys.None;
            string keyText = null;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                uint modifier = ParseModifier(part);
                if (modifier != 0) { modifiers |= modifier; continue; }
                if (part.Length == 0) { error = L.EmptyPart; return false; }
                if (key != Keys.None) { error = string.Format(L.TwoKeys, keyText, part); return false; }
                if (!TryParseKey(part, out key, out keyText)) { error = string.Format(L.UnknownKey, part); return false; }
            }
            if (key == Keys.None) { error = L.OnlyModifiers; return false; }
            if ((modifiers & ~Native.MOD_SHIFT) == 0 && IsTypingKey(key)) { error = L.TypingKey; return false; }

            var sb = new StringBuilder();
            if ((modifiers & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((modifiers & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((modifiers & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((modifiers & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(keyText);

            hotkey = new Hotkey { Modifiers = modifiers, Key = key, Text = sb.ToString() };
            return true;
        }

        static uint ParseModifier(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "ctrl": case "control": case "ctl": return Native.MOD_CONTROL;
                case "alt": return Native.MOD_ALT;
                case "shift": return Native.MOD_SHIFT;
                case "win": case "windows": return Native.MOD_WIN;
                default: return 0;
            }
        }

        static bool TryParseKey(string s, out Keys key, out string display)
        {
            key = Keys.None;
            if (s.Length == 1)
            {
                char c = char.ToLowerInvariant(s[0]);
                int ru = RuLayout.IndexOf(c);
                if (ru >= 0) c = EnLayout[ru];
                display = char.ToUpperInvariant(c).ToString();
                if (c >= 'a' && c <= 'z') { key = Keys.A + (c - 'a'); return true; }
                if (c >= '0' && c <= '9') { key = Keys.D0 + (c - '0'); return true; }
                return Aliases.TryGetValue(c.ToString(), out key);
            }

            display = char.ToUpperInvariant(s[0]) + s.Substring(1);
            if (Aliases.TryGetValue(s, out key)) return true;
            if (!char.IsLetter(s[0]) || !Enum.TryParse(s, true, out key)) return false;
            if (!Enum.IsDefined(typeof(Keys), key) || (int)key < 8 || (int)key > 0xFE) return false;   // mouse buttons, flags
            switch (key)
            {
                case Keys.ShiftKey: case Keys.LShiftKey: case Keys.RShiftKey:
                case Keys.ControlKey: case Keys.LControlKey: case Keys.RControlKey:
                case Keys.Menu: case Keys.LMenu: case Keys.RMenu:
                case Keys.LWin: case Keys.RWin:
                    return false;
            }
            return true;
        }

        static bool IsTypingKey(Keys key)
        {
            int k = (int)key;
            return (key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9)
                || key == Keys.Space || key == Keys.Return || key == Keys.Back || key == Keys.Tab || key == Keys.Escape
                || (k >= 0xBA && k <= 0xC0) || (k >= 0xDB && k <= 0xDF) || k == 0xE2;
        }
    }

    // Writes or removes the HKCU ...\Run entry, so the current exe starts when the user signs in.
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "CleanDesk";

        public static void Apply(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                string command = "\"" + Application.ExecutablePath + "\"";
                if (enabled)
                {
                    if (!command.Equals(key.GetValue(ValueName) as string)) key.SetValue(ValueName, command);
                }
                else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
            }
        }
    }

    // Invisible window that receives hotkeys and the "user is signing out" message.
    sealed class MessageWindow : NativeWindow, IDisposable
    {
        public event Action<int> HotkeyPressed;
        public event EventHandler SessionEnding;

        public MessageWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && HotkeyPressed != null) HotkeyPressed(m.WParam.ToInt32());
            else if (m.Msg == Native.WM_QUERYENDSESSION && SessionEnding != null) SessionEnding(this, EventArgs.Empty);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    static class Taskbar
    {
        // The main taskbar and the taskbars on other monitors.
        static IEnumerable<IntPtr> Windows()
        {
            IntPtr main = Native.FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) yield return main;
            IntPtr w = IntPtr.Zero;
            while ((w = Native.FindWindowEx(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                yield return w;
        }

        public static void HideWindows()
        {
            foreach (IntPtr w in Windows())
                if (Native.IsWindowVisible(w)) Native.ShowWindowAsync(w, Native.SW_HIDE);
        }

        public static void ShowWindows()
        {
            foreach (IntPtr w in Windows())
                if (!Native.IsWindowVisible(w)) Native.ShowWindowAsync(w, Native.SW_SHOWNA);
        }

        public static int GetState()
        {
            Native.APPBARDATA abd = NewData();
            return (int)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd).ToUInt32();
        }

        public static void SetState(int state)
        {
            Native.APPBARDATA abd = NewData();
            abd.lParam = (IntPtr)state;
            Native.SHAppBarMessage(Native.ABM_SETSTATE, ref abd);
        }

        static Native.APPBARDATA NewData()
        {
            var abd = new Native.APPBARDATA();
            abd.cbSize = (uint)Marshal.SizeOf(typeof(Native.APPBARDATA));
            abd.hWnd = Native.FindWindow("Shell_TrayWnd", null);
            return abd;
        }
    }

    static class DesktopIcons
    {
        // The command behind "View > Show desktop icons".
        const int ToggleCommand = 0x7402;

        // The icon view (SHELLDLL_DefView) lives in Progman, or in one of the WorkerW windows after a wallpaper change.
        static IntPtr FindDefView()
        {
            IntPtr view = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
            IntPtr worker = IntPtr.Zero;
            while ((worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                view = Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view != IntPtr.Zero) return view;
            }
            return IntPtr.Zero;
        }

        public static bool AreVisible()
        {
            IntPtr view = FindDefView();
            IntPtr list = view == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            return list != IntPtr.Zero && Native.IsWindowVisible(list);
        }

        public static bool Toggle()
        {
            IntPtr view = FindDefView();
            return view != IntPtr.Zero && Native.PostMessage(view, Native.WM_COMMAND, (IntPtr)ToggleCommand, IntPtr.Zero);
        }
    }

    // If CleanDesk is killed while the taskbar is hidden, the next launch puts everything back.
    static class Recovery
    {
        const string KeyPath = @"Software\CleanDesk";
        const string ValueName = "StateBeforeHide";

        public static void Remember(int state)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                key.SetValue(ValueName, state, RegistryValueKind.DWord);
        }

        public static void Forget()
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        }

        public static void RestoreIfNeeded()
        {
            object saved;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                saved = key == null ? null : key.GetValue(ValueName);
            if (!(saved is int)) return;
            Taskbar.ShowWindows();
            Taskbar.SetState((int)saved);
            Forget();
        }
    }

    // UI text. The language comes from Windows unless CleanDesk.ini sets Language = en / ru.
    sealed class Strings
    {
        public string AlreadyRunning;
        public string HideTaskbar, ShowTaskbar, HideIcons, ShowIcons, OpenSettings, StartWithWindows, Exit;
        public string TaskbarShort, IconsShort, TaskbarLong, IconsLong, Off;
        public string SettingsUpdated, SettingsProblem, CantOpen, WriteFailed, IconsNotFound, AutostartFailed;
        public string Taken, SameAs, NoEquals, UnknownSetting, ReadFailed, BadLanguage, BadYesNo;
        public string EmptyPart, TwoKeys, UnknownKey, OnlyModifiers, TypingKey;
        public string DefaultIni;

        public static readonly Strings En = new Strings
        {
            AlreadyRunning = "CleanDesk is already running, its icon is in the tray.\n\nHotkeys are set in {0} next to the program.",
            HideTaskbar = "Hide taskbar",
            ShowTaskbar = "Show taskbar",
            HideIcons = "Hide desktop icons",
            ShowIcons = "Show desktop icons",
            OpenSettings = "Settings…",
            StartWithWindows = "Start with Windows",
            Exit = "Exit",
            TaskbarShort = "Taskbar",
            IconsShort = "Icons",
            TaskbarLong = "Taskbar",
            IconsLong = "Desktop icons",
            Off = "off",
            SettingsUpdated = "settings updated",
            SettingsProblem = "settings problem",
            CantOpen = "couldn't open {0}",
            WriteFailed = "couldn't save {0}",
            IconsNotFound = "Couldn't find the Explorer desktop.",
            AutostartFailed = "Couldn't change autostart: {0}",
            Taken = "{0} = {1}: this combination is already taken by Windows or another program.",
            SameAs = "{0} = {1}: same combination as {2}.",
            NoEquals = "Line {0} \"{1}\": missing =.",
            UnknownSetting = "Line {0}: unknown setting \"{1}\" (known: Language, StartWithWindows, Taskbar, DesktopIcons).",
            ReadFailed = "Couldn't read {0} ({1}), using the default hotkeys.",
            BadLanguage = "Language = {0}: use auto, en or ru.",
            BadYesNo = "{0} = {1}: use yes or no.",
            EmptyPart = "empty part between pluses (write the plus key as Plus)",
            TwoKeys = "two regular keys ({0} and {1}), only one is allowed",
            UnknownKey = "unknown key \"{0}\"",
            OnlyModifiers = "only modifiers, no regular key",
            TypingKey = "without Ctrl, Alt or Win this key would stop working while typing",
            DefaultIni =
                "; CleanDesk settings. Changes apply by themselves a second after you save this file.\r\n" +
                "\r\n" +
                "; Interface language: auto (same as Windows), en or ru\r\n" +
                "Language = auto\r\n" +
                "\r\n" +
                "; Start CleanDesk when you sign in to Windows: yes or no\r\n" +
                "StartWithWindows = no\r\n" +
                "\r\n" +
                "; Hotkeys: modifiers and a key joined with +, e.g. Ctrl+Alt+L, Ctrl+Shift+F1, Win+Alt+H, F9.\r\n" +
                ";   Modifiers: Ctrl, Alt, Shift, Win.\r\n" +
                ";   Keys: A-Z, 0-9, F1-F24, Space, Enter, Tab, Esc, Backspace, Insert, Delete, Home, End,\r\n" +
                ";         PageUp, PageDown, Up, Down, Left, Right, NumPad0-NumPad9, Pause, PrtSc,\r\n" +
                ";         and - = [ ] ; ' , . / \\ ` (write the plus key as Plus).\r\n" +
                ";   Write none to turn an action off.\r\n" +
                "\r\n" +
                "; Hide / show the taskbar\r\n" +
                "Taskbar = " + Config.DefaultTaskbar + "\r\n" +
                "\r\n" +
                "; Hide / show desktop icons\r\n" +
                "DesktopIcons = " + Config.DefaultDesktopIcons + "\r\n",
        };

        public static readonly Strings Ru = new Strings
        {
            AlreadyRunning = "CleanDesk уже запущен, его значок в трее.\n\nГорячие клавиши настраиваются в {0} рядом с программой.",
            HideTaskbar = "Скрыть панель задач",
            ShowTaskbar = "Показать панель задач",
            HideIcons = "Скрыть значки рабочего стола",
            ShowIcons = "Показать значки рабочего стола",
            OpenSettings = "Настройки…",
            StartWithWindows = "Запускать вместе с Windows",
            Exit = "Выход",
            TaskbarShort = "Панель",
            IconsShort = "Значки",
            TaskbarLong = "Панель задач",
            IconsLong = "Значки рабочего стола",
            Off = "выкл.",
            SettingsUpdated = "настройки обновлены",
            SettingsProblem = "проблема с настройками",
            CantOpen = "не открылся {0}",
            WriteFailed = "не сохранился {0}",
            IconsNotFound = "Не нашёл рабочий стол Проводника.",
            AutostartFailed = "Не получилось настроить автозапуск: {0}",
            Taken = "{0} = {1}: эту комбинацию уже заняла Windows или другая программа.",
            SameAs = "{0} = {1}: та же комбинация, что у {2}.",
            NoEquals = "Строка {0} «{1}»: нет знака =.",
            UnknownSetting = "Строка {0}: неизвестная настройка «{1}» (есть Language, StartWithWindows, Taskbar, DesktopIcons).",
            ReadFailed = "Не прочитался {0} ({1}), взяты клавиши по умолчанию.",
            BadLanguage = "Language = {0}: можно auto, en или ru.",
            BadYesNo = "{0} = {1}: можно yes или no.",
            EmptyPart = "пустое место между плюсами (сам плюс пиши словом Plus)",
            TwoKeys = "две обычные клавиши ({0} и {1}), нужна одна",
            UnknownKey = "не знаю клавишу «{0}»",
            OnlyModifiers = "одни модификаторы, нет обычной клавиши",
            TypingKey = "без Ctrl, Alt или Win эта клавиша перестанет работать при наборе текста",
            DefaultIni =
                "; Настройки CleanDesk. После сохранения файла изменения применятся сами (в течение секунды).\r\n" +
                "\r\n" +
                "; Язык программы: auto (как в Windows), en или ru\r\n" +
                "Language = auto\r\n" +
                "\r\n" +
                "; Запускать CleanDesk вместе с Windows: yes или no\r\n" +
                "StartWithWindows = no\r\n" +
                "\r\n" +
                "; Горячие клавиши: модификаторы и клавиша через +, например Ctrl+Alt+L, Ctrl+Shift+F1, Win+Alt+H, F9.\r\n" +
                ";   Модификаторы: Ctrl, Alt, Shift, Win.\r\n" +
                ";   Клавиши: A-Z, 0-9, F1-F24, Space, Enter, Tab, Esc, Backspace, Insert, Delete, Home, End,\r\n" +
                ";            PageUp, PageDown, Up, Down, Left, Right, NumPad0-NumPad9, Pause, PrtSc,\r\n" +
                ";            знаки - = [ ] ; ' , . / \\ ` (сам плюс пиши словом Plus).\r\n" +
                ";   Букву можно писать и по-русски: Д = L, это та же клавиша на клавиатуре.\r\n" +
                ";   Чтобы отключить действие, напиши none.\r\n" +
                "\r\n" +
                "; Скрыть / показать панель задач\r\n" +
                "Taskbar = " + Config.DefaultTaskbar + "\r\n" +
                "\r\n" +
                "; Скрыть / показать значки рабочего стола\r\n" +
                "DesktopIcons = " + Config.DefaultDesktopIcons + "\r\n",
        };

        // Declared after En and Ru: static fields are initialized in textual order.
        public static Strings Current = Detect();

        public static Strings Detect()
        {
            return (Native.GetUserDefaultUILanguage() & 0x3FF) == 0x19 ? Ru : En;   // 0x19 = LANG_RUSSIAN
        }

        // "auto", "en" or "ru"; null for anything else.
        public static Strings ForSetting(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "": case "auto": return Detect();
                case "en": case "english": return En;
                case "ru": case "russian": case "русский": return Ru;
                default: return null;
            }
        }
    }

    static class Native
    {
        public const int WM_QUERYENDSESSION = 0x0011;
        public const int WM_COMMAND = 0x0111;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        public const int SW_HIDE = 0, SW_SHOWNA = 8;
        public const uint ABM_GETSTATE = 0x4, ABM_SETSTATE = 0xA;
        public const int ABS_AUTOHIDE = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        public static extern ushort GetUserDefaultUILanguage();
    }
}
