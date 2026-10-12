using NeoSync.Core.Device;
using NeoSync.Core.Ipc;
using NeoSync.Core.Protocol;
using NeoSync.Core.Usb;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NeoSync.App;

internal sealed partial class MainForm : Form
{
    private NeoDevice? _device;
    private List<NeoApplet> _applets = [];
    private uint _usageUsedBytes;
    private uint _usageFreeBytes;
    private bool _usageHasData;

    // Created on the UI thread so its hidden message-only window shares the form's message
    // pump - ConnectAsync (a background thread) awaits its Tasks, which is thread-safe.
    private readonly DeviceArrivalWatcher _deviceWatcher = new();

    public MainForm()
    {
        InitializeComponent();
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void btnConnect_Click(object sender, EventArgs e)
    {
        if (_device != null)
        {
            // Connected → disconnect and reboot Neo to keyboard mode
            btnConnect.Enabled  = false;
            _device.Restart();
            _device.Dispose();
            _device = null;
            _applets.Clear();
            treeFiles.Nodes.Clear();
            btnDownload.Enabled  = false;
            btnUpload.Enabled    = false;
            btnDelete.Enabled    = false;
            btnQuickSync.PrimaryEnabled = false;
            btnConnect.Text      = "Connect";
            HideUsageBar();
            SetStatus("Disconnecting\u2026", busy: true);

            // BD04 sometimes lands in a transient failed-start state when the Neo
            // reboots back into keyboard mode - same issue as on Connect, fixed the
            // same way (a soft device restart, not a reinstall).
            Task.Run(FixBd04AfterDisconnect).ContinueWith(_ =>
            {
                Invoke(() => { SetStatus("Disconnected."); btnConnect.Enabled = true; });
            });
            return;
        }

        // Disconnected → start connecting
        SetStatus("Connecting\u2026", busy: true);
        btnConnect.Enabled   = false;
        btnDownload.Enabled  = false;
        btnUpload.Enabled    = false;
        btnDelete.Enabled    = false;
        btnQuickSync.PrimaryEnabled = false;
        treeFiles.Nodes.Clear();
        _applets.Clear();
        _device?.Dispose();
        _device = null;
        HideUsageBar();

        Task.Run(ConnectAsync).ContinueWith(t =>
        {
            if (t.Exception != null)
                Invoke(() => SetStatus($"Error: {t.Exception.InnerException?.Message ?? t.Exception.Message}"));
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void btnDownload_Click(object sender, EventArgs e)
    {
        if (_device is null) return;
        if (treeFiles.SelectedNode?.Tag is not (NeoApplet applet, int fileIndex)) return;

        NeoFileAttributes? attr = _device.GetFileAttributes(applet, fileIndex);

        // Re-fetch the real file name - the tree label now has a size suffix appended for display.
        string? realName = attr?.FileName;
        string defaultName = string.IsNullOrEmpty(realName) ? treeFiles.SelectedNode.Text : realName;

        using var dlg = new SaveFileDialog
        {
            Title      = "Save file from Neo",
            Filter     = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "txt",
            FileName   = defaultName
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        SetStatus("Downloading…");
        btnDownload.Enabled = false;

        Task.Run(() => DownloadFile(applet, fileIndex, dlg.FileName))
            .ContinueWith(_ => Invoke(() =>
            {
                btnDownload.Enabled = treeFiles.SelectedNode != null;
            }));
    }

    private void btnUpload_Click(object sender, EventArgs e)
    {
        if (_device is null) return;
        if (treeFiles.SelectedNode?.Tag is not (NeoApplet applet, int fileIndex)) return;

        using var dlg = new OpenFileDialog
        {
            Title  = "Upload file to Neo",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dlg.ShowDialog() != DialogResult.OK) return;

        SetStatus("Uploading…");
        btnUpload.Enabled = false;

        Task.Run(() => UploadFile(applet, fileIndex, dlg.FileName))
            .ContinueWith(_ => Invoke(() =>
            {
                btnUpload.Enabled = treeFiles.SelectedNode != null;
            }));
    }

    private void btnDelete_Click(object sender, EventArgs e)
    {
        if (_device is null) return;
        if (treeFiles.SelectedNode?.Tag is not (NeoApplet applet, int fileIndex)) return;

        string? preview = GetSelectedFilePreviewText();
        string previewLine = string.IsNullOrEmpty(preview) ? "(no preview available)" : preview;

        var confirm = MessageBox.Show(this,
            $"Delete file {fileIndex} on the Neo?\r\n\r\nPreview: {previewLine}\r\n\r\nThis cannot be undone.",
            "NeoSync - Delete File", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        SetStatus("Deleting…");
        btnDelete.Enabled = false;

        Task.Run(() => DeleteFile(applet, fileIndex))
            .ContinueWith(_ => Invoke(() =>
            {
                btnDelete.Enabled = treeFiles.SelectedNode != null;
            }));
    }

    /// <summary>Returns the preview text already shown in the tree for the selected file,
    /// without a fresh device read.</summary>
    private string? GetSelectedFilePreviewText()
    {
        TreeNode? node = treeFiles.SelectedNode;
        if (node is null) return null;
        return node.Nodes.Count > 0 ? node.Nodes[0].Text : node.Text;
    }

    private void treeFiles_AfterSelect(object sender, TreeViewEventArgs e)
    {
        bool fileSelected = e.Node?.Tag is (NeoApplet, int);
        btnDownload.Enabled = fileSelected;
        btnUpload.Enabled   = fileSelected;
        btnDelete.Enabled   = fileSelected;
    }

    private void btnQuickSync_Click(object sender, EventArgs e)
    {
        if (_device is null) return;

        SetStatus("Quick Sync starting…");
        btnQuickSync.PrimaryEnabled = false;

        Task.Run(QuickSync).ContinueWith(t =>
        {
            Invoke(() =>
            {
                btnQuickSync.PrimaryEnabled = _device is not null;
                if (t.Exception != null)
                    SetStatus($"Quick Sync failed: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
            });
        });
    }

    private void OpenQuickSyncFolder()
    {
        string folder = QuickSyncSettings.GetFolder();
        Directory.CreateDirectory(folder);
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the folder:\r\n{ex.Message}",
                "NeoSync - Open Quick Sync Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ChangeQuickSyncFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description         = "Choose the folder Quick Sync should save files to",
            SelectedPath        = QuickSyncSettings.GetFolder(),
            ShowNewFolderButton = true
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        QuickSyncSettings.SetFolder(dlg.SelectedPath);
        MessageBox.Show(this, $"Quick Sync will now save to:\r\n{dlg.SelectedPath}",
            "NeoSync - Quick Sync", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Backs up whatever's currently in the Quick Sync folder into a timestamped Archive
    /// subfolder, then downloads every non-empty AlphaWord file into it fresh.
    /// </summary>
    private void QuickSync()
    {
        string folder = QuickSyncSettings.GetFolder();
        Directory.CreateDirectory(folder);

        string[] existing = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
        if (existing.Length > 0)
        {
            string archiveDir = Path.Combine(folder, "Archive", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(archiveDir);
            foreach (string file in existing)
                File.Move(file, Path.Combine(archiveDir, Path.GetFileName(file)), overwrite: true);
            Invoke(() => SetStatus($"Backed up {existing.Length} existing file(s)…"));
        }

        NeoApplet? alphaWord = _applets.FirstOrDefault(a => a.IsAlphaWord);
        if (alphaWord is null)
        {
            Invoke(() => SetStatus("Quick Sync: no AlphaWord files found."));
            return;
        }

        int fileCount = alphaWord.FileCount > 0
            ? alphaWord.FileCount
            : (_device?.GetAppletFileCount(alphaWord) ?? 0);

        int synced = 0;
        for (int i = 1; i <= fileCount; i++)
        {
            NeoFileAttributes? attr = _device?.GetFileAttributes(alphaWord, i);
            if (attr is null || attr.AllocSize <= attr.MinSize) continue; // skip empty files

            byte[]? data = _device?.ReadFile(alphaWord, i);
            if (data is null || data.Length == 0) continue;

            try
            {
                string text = ConvertAlphaWordBytes(data);
                string baseName = string.IsNullOrEmpty(attr.FileName) ? $"File {i}" : attr.FileName;
                string destPath = Path.Combine(folder, SanitizeFileName(baseName) + ".txt");
                File.WriteAllText(destPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                synced++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }

        Invoke(() => SetStatus($"Quick Sync complete: {synced} file(s) saved to {folder}."));
    }

    /// <summary>File names come from the device and shouldn't be trusted to build a
    /// path directly -- strip anything that isn't valid in a Windows file name.</summary>
    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ── Connection flow ───────────────────────────────────────────────────────

    /// <summary>
    /// After the Neo reboots back into keyboard mode, BD04 sometimes lands in a
    /// transient failed-start state purely from Windows' own driver re-selection -
    /// a soft device restart (not a reinstall) reliably clears it. Runs on a
    /// background thread; polls briefly since the physical re-enumeration isn't instant.
    /// </summary>
    private static void FixBd04AfterDisconnect()
    {
        for (int i = 0; i < 40; i++) // up to ~12s for the Neo to physically re-enumerate
        {
            Thread.Sleep(300);

            // Confirm a LIVE instance actually exists before trusting its driver kind -
            // NeoDriverState.Detect() falls back to stale ghost data (from before the
            // reboot) when nothing is currently present, which was causing this loop to
            // wrongly conclude "already healthy" before the device had even come back.
            string? bd04Id = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD04");
            if (bd04Id is null || !NeoUsbDiag.GetDevicePresence(bd04Id).Present) continue;

            // CM_PROB_FAILED_START doesn't always show up the instant the device becomes
            // present - it can appear a moment later as Windows finishes starting the
            // driver. A single immediate check here was missing that and declaring victory
            // too early (confirmed via a real repro: Repair NeoSync showed BD04 failed to
            // start right after a Disconnect that had already reported "Disconnected."
            // fine). Give it a short settle window instead of trusting the first snapshot.
            NeoDriverKind hid = NeoDriverState.Detect().Hid;
            for (int settle = 0; settle < 10 && hid != NeoDriverKind.DriverError; settle++) // ~3s
            {
                Thread.Sleep(300);
                hid = NeoDriverState.Detect().Hid;
            }

            if (hid == NeoDriverKind.DriverError)
            {
                // A single restart doesn't always clear it - retry once more if needed.
                for (int attempt = 1; attempt <= 2 && hid == NeoDriverKind.DriverError; attempt++)
                {
                    NeoSyncHelperClient.Send($"RESTART_BD01 {bd04Id}");
                    // The restart can take 10+ seconds to fully settle - keep polling
                    // rather than assuming one restart call is instantly effective.
                    for (int j = 0; j < 60; j++)
                    {
                        Thread.Sleep(200);
                        hid = NeoDriverState.Detect().Hid;
                        if (hid != NeoDriverKind.DriverError) break;
                    }
                }
            }

            return; // present and either healthy or we've done what we can - stop polling
        }
    }

    private void ConnectAsync()
    {
        // Check driver state before attempting anything.
        var driverState = NeoDriverState.Detect();
        if (!driverState.HidReady)
        {
            Invoke(() =>
            {
                SetStatus($"Driver setup needed - BD04: {driverState.Hid}, BD01: {driverState.Comms}");
                using var dlg = new DriverSetupForm(driverState);
                dlg.ShowDialog(this);
                btnConnect.Text    = "Connect";
                btnConnect.Enabled = true;
            });
            return;
        }

        string logPath = Path.Combine(Path.GetTempPath(), "neosync_diag.txt");
        DateTime switchTime = DateTime.Now;

        // A cold BD04 (not yet WinUSB) needs its driver force-installed before anything else
        // can happen - that install+bind is the slow part (several seconds of real OS work),
        // not the mode switch itself. Do it here as an event-driven wait (arm the watcher,
        // then ask the service to install) instead of leaving it to SWITCH's own internal
        // WaitForWinUsb poll, so the UI can reflect it as its own distinct step and the
        // subsequent SWITCH call finds BD04 already ready and skips straight to sending bytes.
        if (driverState.Hid != NeoDriverKind.WinUsb)
        {
            Invoke(() => SetStatus("Installing WinUSB driver for the Neo\u2026", busy: true));
            Task<bool> bd04ArrivalTask = _deviceWatcher.WaitForArrivalAsync("vid_081e&pid_bd04", 15_000);
            string installResult = NeoSyncHelperClient.Send("INSTALL_BD04");
            DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Service INSTALL_BD04 \u2192 {installResult}\n");
            // Don't block on the arrival event here - in practice it often never fires for
            // this transition (observed 0-for-3 in real testing: a device recovering from a
            // failed driver start in-place isn't a fresh PnP arrival), so waiting on it was
            // just adding dead time before the code that actually works (below) ever got to
            // run. Log it if/when it resolves, but don't gate on it.
            _ = bd04ArrivalTask.ContinueWith(t =>
                DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BD04 WinUSB arrival event observed={t.Result}\n"),
                TaskScheduler.Default);
        }

        // Arm the arrival watcher BEFORE calling SWITCH so a BD01 arrival event firing while
        // the (up to ~30s) pipe call is in flight can't be missed - the actual presence check
        // below then has a real OS-event-backed answer instead of re-polling the registry
        // blind for something the service already confirmed before returning "OK".
        Task<bool> bd01ArrivalTask = _deviceWatcher.WaitForArrivalAsync("vid_081e&pid_bd01", 30_000);

        // Step 1: mode switch via NeoSyncHelper service (LocalSystem; handles all driver ops).
        Invoke(() => SetStatus("Switching to transfer mode\u2026", busy: true));
        string switchResult = NeoSyncHelperClient.Send("SWITCH");
        bool switched = switchResult == "OK";
        string switchDiag = switchResult.StartsWith("FAIL:") ? switchResult[5..] : switchResult;
        DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Service SWITCH \u2192 {switchResult}\n");

        if (switched)
        {
            // Locate BD01 instance in the PnP registry once for per-iteration diagnostics.
            string? bd01Id = NeoUsbDiag.FindVidPidInstanceId("VID_081E&PID_BD01");
            DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BD01 instanceId={bd01Id ?? "(not in registry)"}\n");

            // The service only returns OK once it has confirmed BD01 present, so this should
            // already be signaled or resolve near-instantly in theory - but in practice the
            // arrival event has been observed to never fire for this transition (BD01 shows
            // up already in CM_PROB_FAILED_START, and its recovery to STARTED is an in-place
            // driver-start retry, not a fresh device arrival). Don't block the poll loop below
            // on it - just log it asynchronously if it ever resolves.
            _ = bd01ArrivalTask.ContinueWith(t =>
                DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] BD01 arrival event observed={t.Result}\n"),
                TaskScheduler.Default);

            // Poll for up to 25 seconds; open immediately once the interface appears. This
            // remaining loop is no longer "has it arrived at all" (the watcher above already
            // answered that) - it's specifically for the handle-open settle window plus the
            // CM_PROB_FAILED_START self-heal below, both of which need periodic rechecking
            // regardless of arrival events.
            const int maxWaitMs = 25_000;
            const int pollMs    = 300;
            int elapsed = 0;
            NeoUsbTransport? earlyTransport = null;
            bool restartAttempted = false;

            while (elapsed < maxWaitMs)
            {
                earlyTransport = NeoUsbTransport.TryOpen(out string pollDiag);

                // Full diagnostic snapshot every poll iteration so we can see the exact state timeline.
                string cmInfo  = "cm=n/a";
                string cfgInfo = "cfg=n/a";
                string presInfo = "present=? phantom=?";
                if (bd01Id is not null)
                {
                    var (cmStatus, cmProblem) = NeoUsbDiag.GetDevNodeStatus(bd01Id);
                    uint cfgFlags = NeoUsbDiag.GetVidPidConfigFlags("VID_081E&PID_BD01");
                    cmInfo   = NeoUsbDiag.FormatCmStatus(cmStatus, cmProblem);
                    cfgInfo  = NeoUsbDiag.FormatCfgFlags(cfgFlags);
                    var (present, phantom) = NeoUsbDiag.GetDevicePresence(bd01Id);
                    presInfo = $"present={present} phantom={phantom}";

                    if ((cmStatus & 0x400u) != 0 && cmProblem == 10) // HAS_PROBLEM|FAILED_START
                    {
                        if (!restartAttempted)
                        {
                            restartAttempted = true;
                            NeoSyncHelperClient.Send($"RESTART_BD01 {bd01Id}");
                        }
                        else
                        {
                            // Restart didn't help - likely a competing driver (NeoManager etc.) won.
                            NeoSyncHelperClient.Send("FORCE_BD01");
                            restartAttempted = false; // allow one more restart after force-install
                        }
                    }
                }
                DiagnosticsSettings.AppendLog(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] BD01 t={elapsed}ms: {presInfo} {cmInfo} {cfgInfo} diag={pollDiag}\n");

                if (earlyTransport is not null) break;

                Invoke(() => SetStatus("Waiting for comms device\u2026", busy: true));
                Thread.Sleep(pollMs);
                elapsed += pollMs;
            }

            if (earlyTransport is not null)
            {
                DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TryOpen=True (after switch, {elapsed}ms)\n");
                _device = new NeoDevice(earlyTransport);
                Invoke(() => SetStatus("Enumerating files\u2026", busy: true));
                _applets = _device.EnumerateApplets();
                uint earlyAvailRam = _device.GetAvailableRam();
                Invoke(() => PopulateTree(_applets, earlyAvailRam));
                return;
            }

            DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Timeout after {maxWaitMs}ms\n");
            AppendPnpEvents(logPath, switchTime);
            Invoke(() => { SetStatus("BD01 did not respond. Click Connect again first; if it keeps failing, unplug and replug the Neo."); btnConnect.Text = "Connect"; btnConnect.Enabled = true; });
            return;
        }
        else
        {
            // Service already cleaned up BD04. Check if BD01 is already present from a prior session.
            bool alreadyPresent = NeoHidSwitcher.IsCommsDevicePresent();
            if (!alreadyPresent)
            {
                Invoke(() =>
                {
                    SetStatus($"Could not switch the Neo to transfer mode - {switchDiag}");
                    btnConnect.Text    = "Connect";
                    btnConnect.Enabled = true;
                });
                return;
            }
        }

        // Step 2: open bulk transport (device was already in comms mode).
        // Poll briefly - WinUSB may have just finished loading after a fresh install.
        const int step2MaxMs = 10_000;
        const int step2PollMs = 300;
        int step2Elapsed = 0;
        NeoUsbTransport? transport = null;
        string tryOpenDiag = string.Empty;

        while (step2Elapsed < step2MaxMs)
        {
            transport = NeoUsbTransport.TryOpen(out tryOpenDiag);
            if (transport is not null) break;
            Invoke(() => SetStatus("Opening device\u2026", busy: true));
            Thread.Sleep(step2PollMs);
            step2Elapsed += step2PollMs;
        }

        DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TryOpen={transport is not null} - {tryOpenDiag}\n");
        if (transport is null)
        {
            Invoke(() =>
            {
                SetStatus($"Neo not found. [Switch: {switchDiag}]");
                btnConnect.Text    = "Connect";
                btnConnect.Enabled = true;
            });
            return;
        }

        _device = new NeoDevice(transport);

        // Step 3: enumerate applets
        Invoke(() => SetStatus("Enumerating files\u2026", busy: true));
        _applets = _device.EnumerateApplets();
        uint availRam = _device.GetAvailableRam();

        Invoke(() => PopulateTree(_applets, availRam));
    }

    // ── File transfer helpers ─────────────────────────────────────────────────

    private void DownloadFile(NeoApplet applet, int fileIndex, string destPath)
    {
        byte[]? data = _device?.ReadFile(applet, fileIndex);
        if (data is null || data.Length == 0)
        {
            Invoke(() => SetStatus("Download failed or file is empty."));
            return;
        }

        try
        {
            // AlphaWord stores text in Mac Roman encoding with 0x0D (CR) line endings.
            // Strip trailing null padding, decode as Mac Roman, normalise to Windows CRLF.
            string text = ConvertAlphaWordBytes(data);

            // Write directly to the user's chosen path - no temp files
            File.WriteAllText(destPath, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Invoke(() => SetStatus($"Saved to {destPath}"));
        }
        catch (Exception ex)
        {
            // e.g. destination file open/locked in another app, read-only, or permission denied.
            Invoke(() => SetStatus($"Could not save file: {ex.Message}"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Converts raw AlphaWord file bytes to a clean Unicode string.
    /// Ported from ASAlphaWordFile::exportText() - Copyright (c) 2008-2013 tSoniq.
    /// </summary>
    private static string ConvertAlphaWordBytes(byte[] data)
    {
        // Strip trailing null/pad bytes
        int length = data.Length;
        while (length > 0 && (data[length - 1] == 0x00 || data[length - 1] == 0xa7)) length--;
        if (length == 0) return string.Empty;

        // Neo character encoding is roughly CP1252 (Windows-1252)
        System.Text.Encoding neoEncoding;
        try { neoEncoding = System.Text.Encoding.GetEncoding(1252); }
        catch { neoEncoding = System.Text.Encoding.Latin1; }

        var sb = new System.Text.StringBuilder(length);
        int i = 0;

        while (i < length)
        {
            int code = data[i++];

            switch (code)
            {
                // ── Discard codes ──────────────────────────────────────────
                case 0xa4: continue;   // unused
                case 0xa7: continue;   // pad byte
                case 0x8f: continue;   // line-break hint (non-printing)
                case 0xa8: continue;   // unknown non-printing
                case 0xa9: continue;   // unknown (hides line)

                // ── Line-breaking space → regular space ────────────────────
                case 0x81:
                case 0xa1:
                    sb.Append(' ');
                    continue;

                // ── Line-breaking tab ──────────────────────────────────────
                case 0x8d:
                case 0xa3:
                    sb.Append('\t');
                    continue;

                // ── Line-breaking hyphen ───────────────────────────────────
                case 0xad:
                    sb.Append('-');
                    continue;

                // ── End of paragraph ──────────────────────────────────────
                case 0x0d:
                    sb.Append("\r\n");
                    continue;

                // ── Tab ───────────────────────────────────────────────────
                case 0x09:
                    sb.Append('\t');
                    continue;

                // ── Escape sequence { 0xb0, code, 0xb0 } ─────────────────
                case 0xb0:
                    if (i < length)
                    {
                        int escaped = data[i++];
                        if (i < length && data[i] == 0xb0) i++; // skip closing marker
                        AppendNeoChar(sb, escaped, neoEncoding);
                    }
                    continue;
            }

            // Skip other unknown control codes in the 0xa1–0xbf range
            if (code >= 0xa1 && code <= 0xbf) continue;

            AppendNeoChar(sb, code, neoEncoding);
        }

        return sb.ToString();
    }

    private static void AppendNeoChar(System.Text.StringBuilder sb, int code, System.Text.Encoding enc)
    {
        if (code < 0x20) return;                    // skip remaining control codes
        if (code <= 0x7e) { sb.Append((char)code); return; }
        // 0x80–0xFF: decode via CP1252
        sb.Append(enc.GetString([(byte)code]));
    }

    private void UploadFile(NeoApplet applet, int fileIndex, string srcPath)
    {
        // Read source as UTF-8 text
        string text;
        try { text = File.ReadAllText(srcPath, new System.Text.UTF8Encoding(false)); }
        catch (Exception ex)
        {
            Invoke(() => SetStatus($"Could not read file: {ex.Message}"));
            return;
        }

        // Reject obviously binary content (null bytes are not valid in plain text)
        if (text.Contains('\0'))
        {
            Invoke(() => SetStatus("Upload cancelled: file appears to be binary, not plain text."));
            return;
        }

        byte[] data = EncodeAlphaWordText(text);

        // Check against the device's actual available RAM (keep 1 KB buffer, matching Mac source)
        uint availRam = _device?.GetAvailableRam() ?? 0;
        if (availRam > 0 && data.Length + 1024 > availRam)
        {
            Invoke(() => SetStatus(
                $"Upload cancelled: encoded size {data.Length:N0} B exceeds available RAM {availRam:N0} B."));
            CryptographicOperations.ZeroMemory(data);
            return;
        }
        try
        {
            bool ok = _device?.WriteFile(applet, fileIndex, data) ?? false;
            Invoke(() => SetStatus(ok ? "Upload complete." : "Upload failed."));
            if (ok)
            {
                uint availRamAfter = _device?.GetAvailableRam() ?? 0;
                Invoke(() => PopulateTree(_applets, availRamAfter));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Converts a plain Unicode string to AlphaWord file bytes.
    /// Mirrors the encoding used by ASAlphaWordFile::importText.
    /// The Neo regenerates display break-hints on its own so we omit them here.
    /// </summary>
    private static byte[] EncodeAlphaWordText(string text)
    {
        const int minFileSize = 512;

        // Normalise to CR-only line endings (AlphaWord paragraph marker = 0x0D)
        text = text.Replace("\r\n", "\r").Replace("\n", "\r");

        // CP1252 for extended characters (Neo encoding is "roughly CP1252")
        System.Text.Encoding cp1252;
        try { cp1252 = System.Text.Encoding.GetEncoding(1252); }
        catch { cp1252 = System.Text.Encoding.Latin1; }

        var result = new List<byte>(Math.Max(text.Length + 64, minFileSize));

        foreach (char c in text)
        {
            if (c == '\r')        { result.Add(0x0D); continue; }
            if (c == '\t')        { result.Add(0x09); continue; }
            if (c == '\n')        { result.Add(0x0D); continue; } // treat stray LF as paragraph

            if (c < 0x20)         continue;   // skip other control chars
            if (c <= 0x7E)        { result.Add((byte)c); continue; }

            // Extended character: encode via CP1252
            byte[] enc;
            try { enc = cp1252.GetBytes(c.ToString()); }
            catch { continue; }

            if (enc.Length == 1)
            {
                byte b = enc[0];
                // Codes 0xa1–0xbf are AlphaWord control codes - wrap in escape {0xb0, b, 0xb0}
                if (b >= 0xa1 && b <= 0xbf)
                    { result.Add(0xb0); result.Add(b); result.Add(0xb0); }
                else
                    result.Add(b);
            }
        }

        // Pad to minimum file size with the AlphaWord pad byte
        while (result.Count < minFileSize)
            result.Add(0xa7);

        return result.ToArray();
    }

    /// <summary>Deletes a file's content by writing an empty (pad-only) buffer -
    /// AlphaWord treats a file as empty once its AllocSize drops to MinSize.</summary>
    private void DeleteFile(NeoApplet applet, int fileIndex)
    {
        byte[] data = EncodeAlphaWordText(string.Empty);
        try
        {
            bool ok = _device?.WriteFile(applet, fileIndex, data) ?? false;
            Invoke(() => SetStatus(ok ? "File deleted." : "Delete failed."));
            if (ok)
            {
                uint availRam = _device?.GetAvailableRam() ?? 0;
                Invoke(() => PopulateTree(_applets, availRam));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>Appends Windows PnP/System event log entries since <paramref name="after"/> to the diag log.</summary>
    private static void AppendPnpEvents(string logPath, DateTime after)
    {
        var events = QueryRecentPnpEvents(after);
        DiagnosticsSettings.AppendLog(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PnP events since switch ({events.Count}):\n");
        foreach (string ev in events)
            DiagnosticsSettings.AppendLog(logPath, ev + "\n");
    }

    private static List<string> QueryRecentPnpEvents(DateTime after)
    {
        var results = new List<string>();
        string afterXml = after.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss") + ".000Z";
        // XPath time filter - keeps the scan fast even on large logs.
        string xpathFilter = $"*[System[TimeCreated[@SystemTime>='{afterXml}']]]";
        string[] channels  = ["System", "Microsoft-Windows-Kernel-PnP/Configuration"];
        foreach (string channel in channels)
        {
            try
            {
                var eq = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                    channel, System.Diagnostics.Eventing.Reader.PathType.LogName, xpathFilter);
                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(eq);
                System.Diagnostics.Eventing.Reader.EventRecord? rec;
                int count = 0;
                while ((rec = reader.ReadEvent()) is not null && count < 50)
                {
                    using (rec)
                    {
                        string msg = "";
                        try { msg = rec.FormatDescription() ?? ""; } catch { }

                        // These channels capture ALL system PnP activity, not just the Neo's --
                        // only keep entries that actually mention our device/driver, so an
                        // unrelated USB device (or anything else) plugged in during the same
                        // window doesn't end up in a diagnostic dump.
                        bool relevant = msg.Contains("081E", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("BD01", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("BD04", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("AlphaSmart", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("NeoSync", StringComparison.OrdinalIgnoreCase)
                            || msg.Contains("WinUSB", StringComparison.OrdinalIgnoreCase);
                        if (!relevant) continue;

                        if (msg.Length > 300) msg = msg[..300] + "…";
                        msg = msg.Replace('\r', ' ').Replace('\n', ' ');
                        results.Add($"  [{channel}] {rec.TimeCreated:yyyy-MM-dd HH:mm:ss.fff} {rec.ProviderName} Id={rec.Id}: {msg}");
                    }
                    count++;
                }
            }
            catch (Exception ex)
            {
                results.Add($"  [{channel}] query failed: {ex.Message}");
            }
        }
        return results;
    }

    private void CopyDiagnostics()
    {
        var state = NeoDriverState.Detect();
        string dump = NeoUsbDiag.BuildDumpString(state);

        // Append recent events (last 5 minutes) to the snapshot.
        var events = QueryRecentPnpEvents(DateTime.Now.AddMinutes(-5));
        var sb = new System.Text.StringBuilder(dump);
        sb.AppendLine($"PnP events (last 5 min): {events.Count}");
        foreach (string ev in events) sb.AppendLine(ev);
        dump = sb.ToString();

        // This is an explicit, one-off user action (distinct from the always-on/off
        // background logging toggle) -- clicking the button is itself the consent.
        string logPath = Path.Combine(Path.GetTempPath(), "neosync_diag.txt");
        DiagnosticsSettings.AppendLogAlways(logPath, dump);
        try { Clipboard.SetText(dump); } catch { }
        MessageBox.Show(this,
            "Diagnostic info copied to clipboard -- paste it into your bug report or support email.\r\n\r\n" +
            "This never includes the contents of any file on the Neo -- only USB/driver state, " +
            "timing, and command names.\r\n\r\n" +
            $"Also saved to:\n{logPath}",
            "NeoSync - Copy Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void PopulateTree(List<NeoApplet> applets, uint? availRam = null)
    {
        treeFiles.BeginUpdate();
        treeFiles.Nodes.Clear();
        uint alphaWordUsedBytes = 0;

        foreach (var applet in applets)
        {
            // Default: show only the AlphaWord applet (user text files).
            // "Show all applets" checkbox reveals system/font/other applets.
            bool isAlphaWord = applet.IsAlphaWord;
            if (!isAlphaWord && !mnuShowSystem.Checked) continue;
            var appletNode = new TreeNode(applet.Name);

            // The applet header's FileCount is not always reliable.
            // Fall back to GET_USED_SPACE for applets that report 0 in the header.
            int fileCount = applet.FileCount > 0
                ? applet.FileCount
                : (_device?.GetAppletFileCount(applet) ?? 0);

            for (int i = 1; i <= fileCount; i++)
            {
                NeoFileAttributes? attr = _device?.GetFileAttributes(applet, i);
                string label = attr?.FileName is { Length: > 0 } name ? name : $"File {i}";
                bool hasContent = attr is not null && attr.AllocSize > attr.MinSize;
                // AlphaWord is always enumerated regardless of the show-all-applets toggle,
                // so this total is a stable measure of the device's user-content usage.
                if (isAlphaWord && attr is not null) alphaWordUsedBytes += attr.AllocSize;
                if (attr is not null)
                {
                    // MinSize is a constant minimum block allocation (always 512 in practice),
                    // not the file's actual content size -- AllocSize is the one that varies
                    // per file, and equals MinSize exactly when the file has no content.
                    label += hasContent ? $"  ({FormatFileSize(attr.AllocSize)})" : "  (empty)";
                }
                var fileNode = new TreeNode(label) { Tag = (applet, i) };
                appletNode.Nodes.Add(fileNode);

                // AlphaWord-only: other applets' files aren't plain text, so a text preview
                // wouldn't be meaningful. Every file still gets a child node (blank if no
                // preview) so empty and non-empty files take up the same vertical space.
                string? preview = isAlphaWord && hasContent ? GetFilePreview(applet, i) : null;
                var previewNode = new TreeNode(preview ?? string.Empty)
                {
                    Tag         = (applet, i), // clicking the preview line selects its file too
                    ForeColor   = SystemColors.GrayText,
                    NodeFont    = new Font(treeFiles.Font, FontStyle.Italic)
                };
                fileNode.Nodes.Add(previewNode);
                fileNode.Expand();
            }

            appletNode.Expand();
            treeFiles.Nodes.Add(appletNode);
        }

        treeFiles.EndUpdate();

        bool hasFiles = applets.Any(a => a.FileCount > 0);
        SetStatus(hasFiles ? "Ready." : "Connected - no files found.");
        btnConnect.Text      = "Disconnect";
        btnConnect.Enabled   = true;
        btnQuickSync.PrimaryEnabled = true;
        if (availRam.HasValue)
            UpdateUsageBar(alphaWordUsedBytes, availRam.Value);
    }

    private static string FormatFileSize(uint bytes)
        => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";

    /// <summary>Reads a file's content and returns a short first-words preview for the tree
    /// label. Returns null if the file couldn't be read.</summary>
    private string? GetFilePreview(NeoApplet applet, int fileIndex, int maxWords = 8)
    {
        byte[]? data = _device?.ReadFile(applet, fileIndex);
        if (data is null || data.Length == 0) return null;

        try
        {
            string text = ConvertAlphaWordBytes(data);
            string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return null;

            string shortPreview = string.Join(' ', words.Take(maxWords));
            if (words.Length > maxWords) shortPreview += " …";

            return shortPreview;
        }
        catch
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private void SetStatus(string text, bool busy = false)
    {
        lblStatus.Text = text;
        lblStatus.ToolTipText = text; // full message on hover in case it's too long to fit
        progStatus.Visible = busy;
    }

    private void UpdateUsageBar(uint usedBytes, uint freeBytes)
    {
        _usageUsedBytes = usedBytes;
        _usageFreeBytes = freeBytes;
        _usageHasData   = true;
        pnlUsageBar.Visible = true;
        pnlUsageBar.Invalidate();
    }

    private void HideUsageBar()
    {
        _usageHasData = false;
        pnlUsageBar.Visible = false;
    }

    private void pnlUsageBar_Paint(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle rect = pnlUsageBar.ClientRectangle;

        using (var bg = new SolidBrush(SystemColors.ControlLight))
            g.FillRectangle(bg, rect);

        if (_usageHasData)
        {
            uint total = _usageUsedBytes + _usageFreeBytes;
            double fraction = total > 0 ? (double)_usageUsedBytes / total : 0;
            int fillWidth = (int)(rect.Width * fraction);
            using (var fill = new SolidBrush(Color.SteelBlue))
                g.FillRectangle(fill, new Rectangle(0, 0, fillWidth, rect.Height));

            int percentUsed = total > 0 ? (int)Math.Round(_usageUsedBytes * 100.0 / total) : 0;
            string text = $"{percentUsed}% used \u2014 {FormatFileSize(_usageFreeBytes)} free of {FormatFileSize(total)}";
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, pnlUsageBar.Font, SystemBrushes.ControlText, rect, sf);
        }

        using var border = new Pen(SystemColors.ControlDark);
        g.DrawRectangle(border, 0, 0, rect.Width - 1, rect.Height - 1);
    }
}
