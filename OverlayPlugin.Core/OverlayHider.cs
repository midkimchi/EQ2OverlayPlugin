using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Advanced_Combat_Tracker; // Added for EQ2 native ACT combat events
using RainbowMage.OverlayPlugin.MemoryProcessors.InCombat;
using RainbowMage.OverlayPlugin.NetworkProcessors;
using static RainbowMage.OverlayPlugin.MemoryProcessors.InCombat.LineInCombat;

namespace RainbowMage.OverlayPlugin
{
    public interface IOverlayHider : IDisposable
    {
        void UpdateOverlays();
    }

    // ==========================================
    // ORIGINAL FFXIV HIDER
    // ==========================================
    class FFXIVOverlayHider : IOverlayHider
    {
        private bool gameActive = true;
        private bool inCutscene = false;
        private bool inCombat = false;
        private IPluginConfig config;
        private ILogger logger;
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "CA2213:Disposable fields should be disposed",
            Justification = "main is disposed of by TinyIoCContainer")]
        private PluginMain main;
        private FFXIVRepository repository;
        private int ffxivPid = -1;
        private Timer focusTimer;
        private bool _disposed;

        public FFXIVOverlayHider(TinyIoCContainer container)
        {
            this.config = container.Resolve<IPluginConfig>();
            this.logger = container.Resolve<ILogger>();
            this.main = container.Resolve<PluginMain>();
            this.repository = container.Resolve<FFXIVRepository>();

            container.Resolve<NativeMethods>().ActiveWindowChanged += ActiveWindowChangedHandler;
            container.Resolve<NetworkParser>().OnOnlineStatusChanged += OnlineStatusChanged;
            LineInCombat lineInCombat;
            if (container.TryResolve(out lineInCombat))
            {
                lineInCombat.OnInCombatChanged += CombatStatusChanged;
            }

            try
            {
                repository.RegisterProcessChangedHandler(UpdateFFXIVProcess);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, "Failed to register process watcher for FFXIV; this is only an issue if you're playing FFXIV. As a consequence, OverlayPlugin won't be able to hide overlays if you're not in-game.");
                logger.Log(LogLevel.Error, "Details: " + ex.ToString());
            }

            focusTimer = new Timer();
            focusTimer.Tick += (o, e) => ActiveWindowChangedHandler(this, IntPtr.Zero);
            focusTimer.Interval = 10000;  // 10 seconds
            focusTimer.Start();
        }

        private void UpdateFFXIVProcess(Process p)
        {
            if (p != null)
            {
                ffxivPid = p.Id;
            }
            else
            {
                ffxivPid = -1;
            }
        }

        public void UpdateOverlays()
        {
            if (!config.HideOverlaysWhenNotActive)
                gameActive = true;

            if (!config.HideOverlayDuringCutscene)
                inCutscene = false;

            try
            {
                foreach (var overlay in main.Overlays)
                {
                    if (overlay.Config.IsVisible)
                    {
                        overlay.Visible = gameActive && !inCutscene && (!overlay.Config.HideOutOfCombat || inCombat);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, $"OverlayHider: Failed to update overlays: {ex}");
            }
        }

        private void ActiveWindowChangedHandler(object sender, IntPtr changedWindow)
        {
            if (!config.HideOverlaysWhenNotActive) return;
            try
            {
                try
                {
                    NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);

                    if (pid == 0)
                        return;

                    if (ffxivPid != -1)
                    {
                        gameActive = pid == ffxivPid || pid == Process.GetCurrentProcess().Id;
                    }
                    else
                    {
                        var exePath = Process.GetProcessById((int)pid).MainModule.FileName;
                        var fileName = Path.GetFileName(exePath.ToString());
                        gameActive = (fileName == "ffxiv.exe" || fileName == "ffxiv_dx11.exe" ||
                                        exePath.ToString() == Process.GetCurrentProcess().MainModule.FileName);
                    }
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Ignore access denied errors
                    if (ex.ErrorCode == -2147467259)  // 0x80004005
                    {
                        gameActive = false;
                    }
                    else
                    {
                        logger.Log(LogLevel.Error, "XivWindowWatcher: {0}", ex.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, "XivWindowWatcher: {0}", ex.ToString());
            }

            UpdateOverlays();
        }

        private void OnlineStatusChanged(object sender, OnlineStatusChangedArgs e)
        {
            if (!config.HideOverlayDuringCutscene || e.Target != repository.GetPlayerID()) return;

            inCutscene = e.Status == 15;
            UpdateOverlays();
        }

        private void CombatStatusChanged(object sender, InCombatArgs args)
        {
            inCombat = args.InGameCombat;

            if (args.InGameCombatChanged)
            {
                UpdateOverlays();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    focusTimer?.Stop();
                    focusTimer?.Dispose();
                    focusTimer = null;
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    // ==========================================
    // EQ2 HIDER
    // ==========================================
    class EQ2OverlayHider : IOverlayHider
    {
        private bool gameActive = true;
        private bool inCombat = false;
        private IPluginConfig config;
        private ILogger logger;
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage",
            "CA2213:Disposable fields should be disposed",
            Justification = "main is disposed of by TinyIoCContainer")]
        private PluginMain main;
        private Timer focusTimer;
        private bool _disposed;

        public EQ2OverlayHider(TinyIoCContainer container)
        {
            this.config = container.Resolve<IPluginConfig>();
            this.logger = container.Resolve<ILogger>();
            this.main = container.Resolve<PluginMain>();

            container.Resolve<NativeMethods>().ActiveWindowChanged += ActiveWindowChangedHandler;

            // Bind to ACT's native combat events for EQ2 combat tracking
            if (ActGlobals.oFormActMain != null)
            {
                ActGlobals.oFormActMain.OnCombatStart += Act_OnCombatStart;
                ActGlobals.oFormActMain.OnCombatEnd += Act_OnCombatEnd;
            }

            focusTimer = new Timer();
            focusTimer.Tick += (o, e) => ActiveWindowChangedHandler(this, IntPtr.Zero);
            focusTimer.Interval = 10000;  // 10 seconds
            focusTimer.Start();
        }

        public void UpdateOverlays()
        {
            if (!config.HideOverlaysWhenNotActive)
                gameActive = true;

            try
            {
                foreach (var overlay in main.Overlays)
                {
                    if (overlay.Config.IsVisible)
                    {
                        overlay.Visible = gameActive && (!overlay.Config.HideOutOfCombat || inCombat);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, $"EQ2OverlayHider: Failed to update overlays: {ex}");
            }
        }

        private void ActiveWindowChangedHandler(object sender, IntPtr changedWindow)
        {
            if (!config.HideOverlaysWhenNotActive) return;
            try
            {
                try
                {
                    NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);

                    if (pid == 0)
                        return;

                    var exePath = Process.GetProcessById((int)pid).MainModule.FileName;
                    var fileName = Path.GetFileName(exePath.ToString());
                    
                    // Check if the focused window is EverQuest 2 or ACT itself
                    gameActive = (fileName.Equals("EverQuest2.exe", StringComparison.OrdinalIgnoreCase) ||
                                  exePath.ToString() == Process.GetCurrentProcess().MainModule.FileName);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // Ignore access denied errors
                    if (ex.ErrorCode == -2147467259)  // 0x80004005
                    {
                        gameActive = false;
                    }
                    else
                    {
                        logger.Log(LogLevel.Error, "EQ2WindowWatcher: {0}", ex.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, "EQ2WindowWatcher: {0}", ex.ToString());
            }

            UpdateOverlays();
        }

        private void Act_OnCombatStart(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            inCombat = true;
            UpdateOverlays();
        }

        private void Act_OnCombatEnd(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            inCombat = false;
            UpdateOverlays();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Clean up ACT event listeners to prevent memory leaks
                    if (ActGlobals.oFormActMain != null)
                    {
                        ActGlobals.oFormActMain.OnCombatStart -= Act_OnCombatStart;
                        ActGlobals.oFormActMain.OnCombatEnd -= Act_OnCombatEnd;
                    }

                    focusTimer?.Stop();
                    focusTimer?.Dispose();
                    focusTimer = null;
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
