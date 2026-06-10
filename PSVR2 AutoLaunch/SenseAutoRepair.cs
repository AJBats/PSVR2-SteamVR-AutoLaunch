using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using static PSVR2_AutoLaunch.BluetoothNative;

namespace PSVR2_AutoLaunch
{
    internal enum SenseSide { Left, Right }

    // Auto re-pair flow for PSVR2 Sense controllers.
    //
    // Trigger: a Sense is plugged in over USB, then unplugged.
    // Action : after unplug, watch ~60 s for the controller to advertise in pair
    //          mode (user long-presses the pair combo). Only when pair mode is
    //          actually observed do we drop the old Windows pairing and silently
    //          re-authenticate.
    //
    // Deferring the unpair until pair mode is seen keeps plug-in-to-charge
    // non-destructive: entering pair mode on the controller does not require the
    // Windows pairing record to be gone first, so nothing is removed until the
    // user has proven intent with the long-press. If the controller instead just
    // reconnects over BT with its existing pairing after unplug, the watch ends
    // quietly.
    internal class SenseAutoRepair
    {
        public const string VendorIdSony    = "054C";
        public const string ProductIdSenseL = "0E45";
        public const string ProductIdSenseR = "0E46";

        // Windows shows these as "PlayStation VR2 Sense Controller (L)" / "(R)" in the
        // Bluetooth stack (confirmed via HKLM\SYSTEM\...\BTHPORT\Parameters\Devices).
        // Match a stable substring of that name.
        private const string BtNamePrefix = "VR2 Sense";

        private static readonly TimeSpan WatchTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ScanGap     = TimeSpan.FromSeconds(2);
        private const byte InquiryTimeoutMultiplier = 4; // 4 * 1.28 s ~= 5 s per inquiry pass
        private const byte ConfirmTimeoutMultiplier = 2; // short pass to confirm a first lastSeen bump quickly

        // Per-side state. The flow has two phases:
        //   PendingUnplug -- USB plug-in seen, waiting for user to unplug.
        //   Watching      -- USB removal seen, scanner running, looking for pair mode
        //                    for up to WatchTimeout.
        private enum Phase { PendingUnplug, Watching }
        private class SideState
        {
            public Phase Phase;
            public DateTime Deadline;    // only meaningful when Phase == Watching
            public bool RemovedBond;     // we already dropped the old Windows pairing this flow
            public int WatchId;          // identifies this watch; scopes lastSeen streaks to it
        }

        // Per-address stLastSeen tracking for the scanner's pair-mode detection.
        // WatchId scopes the streak to a single watch: the scanner thread (and this
        // record) can outlive a watch when the other side is still active, and a
        // stale streak carried into a later watch would collapse the two-bump
        // requirement to a single bump.
        private class SeenRecord
        {
            public DateTime LastSeen;
            public int Streak;  // bumps observed within the current watch
            public int WatchId; // the watch these observations belong to
        }

        private readonly NotifyIcon trayIcon;
        private readonly object gate = new object();
        private readonly Dictionary<SenseSide, SideState> states = new Dictionary<SenseSide, SideState>();
        private Thread scannerThread;
        private CancellationTokenSource scannerCts;
        private volatile bool enabled = true;
        private int watchCounter; // guarded by gate

        // Non-null only while our own BluetoothAuthenticateDeviceEx call is in
        // flight (guarded by gate). The auth callback accepts nothing else.
        private ulong? pendingAuthAddr;

        // Auth callback: kept as a field so the delegate isn't GC'd while pinned native-side.
        private readonly PFN_AUTHENTICATION_CALLBACK_EX authCallbackDelegate;
        private IntPtr authRegHandle = IntPtr.Zero;

        public SenseAutoRepair(NotifyIcon trayIcon)
        {
            this.trayIcon = trayIcon;
            authCallbackDelegate = OnAuthCallback;
            uint regErr = BluetoothRegisterForAuthenticationEx(IntPtr.Zero, out authRegHandle, authCallbackDelegate, IntPtr.Zero);
            if (regErr != 0)
                Log.Write("auth callback registration FAILED err=" + regErr + " - pairings will show the Windows SSP prompt");
        }

        // Called by Windows when an SSP pairing event needs confirmation. Returns true
        // if we handled it (Windows skips its own UI), false to let Windows handle it.
        // We accept only the authentication we initiated ourselves, identified by the
        // address armed around the BluetoothAuthenticateDeviceEx call. Inbound pairing
        // attempts - including devices merely advertising a Sense-like name - fall
        // through to the default Windows flow.
        private bool OnAuthCallback(IntPtr pvParam, ref BLUETOOTH_AUTHENTICATION_CALLBACK_PARAMS p)
        {
            lock (gate)
            {
                if (pendingAuthAddr == null || p.deviceInfo.Address.ullLong != pendingAuthAddr.Value)
                    return false;
            }

            // For numeric-comparison/passkey methods Windows may validate the echoed
            // value; the rest of the union stays zeroed (Just Works path ignores it).
            var union = new byte[32];
            if (p.authenticationMethod == BLUETOOTH_AUTHENTICATION_METHOD_NUMERIC_COMPARISON
                || p.authenticationMethod == BLUETOOTH_AUTHENTICATION_METHOD_PASSKEY_NOTIFICATION)
            {
                Array.Copy(BitConverter.GetBytes(p.Numeric_Value_Or_Passkey), union, 4);
            }

            var response = new BLUETOOTH_AUTHENTICATE_RESPONSE
            {
                bthAddressRemote = p.deviceInfo.Address,
                authMethod = p.authenticationMethod,
                union_pinInfo_oobInfo_etc = union,
                negativeResponse = 0, // accept
            };

            uint sendErr = BluetoothSendAuthenticationResponseEx(IntPtr.Zero, ref response);
            if (sendErr != 0)
                Log.Write("auth response send FAILED err=" + sendErr + " method=" + p.authenticationMethod);
            return true;
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                bool wasEnabled;
                lock (gate)
                {
                    wasEnabled = enabled;
                    enabled = value;
                    if (wasEnabled && !value)
                    {
                        states.Clear();
                        scannerCts?.Cancel();
                    }
                }
            }
        }

        // Returns true and sets `side` when `pnpDeviceId` is a PSVR2 Sense USB path.
        // Expected form: "USB\VID_054C&PID_0E45\..." (case may vary).
        public static bool TryParseSenseSide(string pnpDeviceId, out SenseSide side)
        {
            if (string.IsNullOrEmpty(pnpDeviceId)) { side = default(SenseSide); return false; }
            string upper = pnpDeviceId.ToUpperInvariant();
            if (upper.IndexOf("VID_" + VendorIdSony, StringComparison.Ordinal) < 0)
            {
                side = default(SenseSide);
                return false;
            }
            if (upper.IndexOf("PID_" + ProductIdSenseL, StringComparison.Ordinal) >= 0) { side = SenseSide.Left;  return true; }
            if (upper.IndexOf("PID_" + ProductIdSenseR, StringComparison.Ordinal) >= 0) { side = SenseSide.Right; return true; }
            side = default(SenseSide);
            return false;
        }

        public void OnUsbArrival(SenseSide side)
        {
            // WMI fires plug events once per USB interface (~4 per plug for a Sense
            // composite device). We deduplicate inside the lock so only the first
            // arrival in a chain shows the toast. Re-plug after an unplug (state was
            // Watching) is treated as a new arrival.
            lock (gate)
            {
                if (!enabled) return;
                states.TryGetValue(side, out SideState existingState);
                if (existingState != null && existingState.Phase == Phase.PendingUnplug)
                {
                    return; // duplicate WMI event for the same in-progress plug-in
                }
                states[side] = new SideState
                {
                    Phase = Phase.PendingUnplug,
                    Deadline = DateTime.MaxValue, // sentinel; only set when transitioning to Watching
                    // A replug during a watch is the natural retry gesture; if that
                    // watch already removed the old bond, the fact must survive so
                    // the eventual timeout toast stays honest.
                    RemovedBond = existingState != null && existingState.RemovedBond,
                };
            }

            // No toast: arrival takes no action (charging is benign), so there is
            // nothing to tell the user until they unplug.
            Log.Write("usb arrival " + Label(side));
        }

        public void OnUsbDeparture(SenseSide side)
        {
            if (!Enabled) return;

            bool startScanner = false;
            lock (gate)
            {
                if (!states.TryGetValue(side, out SideState state)) return; // never saw a plug-in for this side
                if (state.Phase == Phase.Watching) return; // already watching

                state.Phase = Phase.Watching;
                state.Deadline = DateTime.UtcNow + WatchTimeout;
                state.WatchId = ++watchCounter;
                startScanner = true;
            }

            if (startScanner)
            {
                Log.Write("usb departure " + Label(side) + ", watching for pair mode");
                ShowToast(side,
                    "Re-pair " + SideName(side) + " Sense controller?",
                    "Hold " + PairCombo(side) + " until the lights pulse blue - or ignore if it's already paired. (60 s)",
                    ToolTipIcon.Info);
                EnsureScannerRunning();
            }
        }

        public void Shutdown()
        {
            CancellationTokenSource cts;
            Thread thread;
            bool threadAlive;
            lock (gate)
            {
                states.Clear();
                cts = scannerCts;
                scannerCts = null;
                thread = scannerThread;
                threadAlive = thread != null && thread.IsAlive;
                cts?.Cancel();
            }

            // Give an in-flight pairing attempt a chance to finish before we pull
            // the auth callback registration out from under it. Bounded: longer
            // than one authenticate attempt, short enough not to hang app exit.
            if (threadAlive)
                thread.Join(TimeSpan.FromSeconds(8));

            // If the scanner thread is still draining, it disposes the CTS itself on
            // exit (it is no longer the registered one). Otherwise it's ours to free.
            if (cts != null && !threadAlive)
                cts.Dispose();

            if (authRegHandle != IntPtr.Zero)
            {
                BluetoothUnregisterAuthentication(authRegHandle);
                authRegHandle = IntPtr.Zero;
            }
        }

        private void EnsureScannerRunning()
        {
            lock (gate)
            {
                // A live thread whose token is already cancelled (feature was toggled
                // off and back on mid-pass) will exit without scanning again - treat
                // it as not running, or new Watching states would be stranded with no
                // scanner and no timeout.
                bool oldAlive  = scannerThread != null && scannerThread.IsAlive;
                bool oldUsable = oldAlive && scannerCts != null && !scannerCts.IsCancellationRequested;
                if (oldUsable) return;

                // Previous scanner exited: reclaim its CTS handle here. If it is still
                // draining (alive but cancelled), it disposes its own CTS on exit once
                // it sees it has been replaced.
                if (!oldAlive)
                    scannerCts?.Dispose();

                var cts = new CancellationTokenSource();
                scannerCts = cts;
                scannerThread = new Thread(() => ScannerLoop(cts))
                {
                    IsBackground = true,
                    Name = "SenseAutoRepair-Scanner",
                };
                scannerThread.Start();
            }
        }

        private void ScannerLoop(CancellationTokenSource cts)
        {
            try
            {
                ScannerLoopBody(cts.Token);
            }
            finally
            {
                bool stillRegistered;
                lock (gate)
                {
                    stillRegistered = ReferenceEquals(scannerCts, cts);
                    // Clear ownership of scannerThread only when this thread is the
                    // one currently registered. Avoids racing with EnsureScannerRunning
                    // when it has already replaced us.
                    if (scannerThread == Thread.CurrentThread)
                        scannerThread = null;
                }
                // Replaced or shut down: nobody else will free this CTS. Disposing
                // here is safe - the token is no longer in use past this point.
                if (!stillRegistered)
                    cts.Dispose();
            }
        }

        private void ScannerLoopBody(CancellationToken token)
        {
            // stLastSeen tracking per remembered device address. A remembered Sense
            // bumps stLastSeen when it answers an inquiry - but also on ordinary
            // connection activity, so a single bump is ambiguous (a controller
            // reconnecting on its own bumps it once, just before fConnected goes
            // true). A controller in pair mode answers *every* inquiry pass, so we
            // require bumps on two consecutive passes before treating it as pair
            // mode. Change detection (rather than comparing against wall-clock time)
            // sidesteps the UTC-vs-local ambiguity of SYSTEMTIME.
            var lastSeenTracker = new Dictionary<ulong, SeenRecord>();

            // After a first bump, the next pass is a short, gap-less confirmation
            // inquiry so a genuine pair-mode controller is confirmed in ~3 s instead
            // of waiting out a full pass.
            bool confirmRound = false;

            while (!token.IsCancellationRequested)
            {
                // Expire any Watching deadlines that have elapsed and snapshot the
                // currently-watching sides. Sides in PendingUnplug aren't scanned.
                var expired = new List<KeyValuePair<SenseSide, bool>>(); // side, RemovedBond
                List<SenseSide> activeSides = new List<SenseSide>();
                var watchIds = new Dictionary<SenseSide, int>();
                lock (gate)
                {
                    foreach (var kv in states)
                    {
                        if (kv.Value.Phase != Phase.Watching) continue;
                        if (DateTime.UtcNow >= kv.Value.Deadline)
                        {
                            expired.Add(new KeyValuePair<SenseSide, bool>(kv.Key, kv.Value.RemovedBond));
                        }
                        else
                        {
                            activeSides.Add(kv.Key);
                            watchIds[kv.Key] = kv.Value.WatchId;
                        }
                    }
                    foreach (var kv in expired)
                        states.Remove(kv.Key);
                }

                foreach (var kv in expired)
                {
                    if (kv.Value)
                    {
                        // We already removed the old pairing but never completed the
                        // new one - the only outcome that warrants a warning.
                        ShowToast(kv.Key,
                            "Pairing didn't finish",
                            "The " + SideName(kv.Key) + " controller's old pairing was removed. Plug it in and unplug again, then hold " + PairCombo(kv.Key) + ".",
                            ToolTipIcon.Warning);
                    }
                    else
                    {
                        // Benign expiry (nothing was touched): just withdraw the
                        // "Re-pair?" prompt instead of adding more noise.
                        Log.Write("watch expired " + Label(kv.Key) + ", nothing touched");
                        DismissToast(kv.Key);
                    }
                }

                if (activeSides.Count == 0)
                {
                    // Exit decision must be atomic with deregistration: a new watch
                    // can be armed between our snapshot above and here, in which case
                    // EnsureScannerRunning would see this thread alive and not start
                    // a replacement - keep looping instead. Otherwise unregister
                    // ourselves under the same lock so a concurrent arm starts fresh.
                    lock (gate)
                    {
                        bool anyWatching = false;
                        foreach (var kv in states)
                        {
                            if (kv.Value.Phase == Phase.Watching) { anyWatching = true; break; }
                        }
                        if (!anyWatching)
                        {
                            if (scannerThread == Thread.CurrentThread)
                                scannerThread = null;
                            return;
                        }
                    }
                    continue;
                }

                // Inquiry-scan for BT devices. Blocks ~5 s. Unpaired Senses in pair
                // mode come back as unknown devices; remembered ones come back whether
                // in range or not, so pair mode is detected via the stLastSeen
                // baseline. fConnected means the controller reconnected on its own
                // with its existing pairing.
                var found = new Dictionary<SenseSide, BLUETOOTH_DEVICE_INFO>();
                var reconnected = new HashSet<SenseSide>();
                bool bumpArmed = false;
                foreach (var d in EnumerateDevices(
                    authenticated: true, remembered: true, unknown: true, connected: true,
                    issueInquiry: true,
                    timeoutMultiplier: confirmRound ? ConfirmTimeoutMultiplier : InquiryTimeoutMultiplier))
                {
                    if (d.szName == null) continue;
                    if (d.szName.IndexOf(BtNamePrefix, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    SenseSide side;
                    if (d.szName.EndsWith("(L)", StringComparison.Ordinal))
                        side = SenseSide.Left;
                    else if (d.szName.EndsWith("(R)", StringComparison.Ordinal))
                        side = SenseSide.Right;
                    else
                        continue;

                    if (d.fConnected)
                    {
                        // "Reconnected" only counts with an actual bond. A controller
                        // whose pairing is gone shows fConnected=true transiently while
                        // its doomed reconnect attempt is being rejected - that is not
                        // success.
                        if (d.fAuthenticated)
                            reconnected.Add(side);
                        continue;
                    }

                    if (!d.fRemembered && !d.fAuthenticated)
                    {
                        // Unknown devices are only returned when they answered this
                        // very inquiry: discoverable now, so in pair mode.
                        found[side] = d;
                        continue;
                    }

                    // Only track remembered devices for sides actually being watched,
                    // and scope all observations to the current watch: a record (or
                    // streak) from an earlier watch must never count toward this one.
                    if (!watchIds.TryGetValue(side, out int watchId)) continue;

                    DateTime lastSeen = SystemTimeToDateTime(d.stLastSeen);
                    ulong addr = d.Address.ullLong;
                    if (!lastSeenTracker.TryGetValue(addr, out SeenRecord rec) || rec.WatchId != watchId)
                    {
                        // First sighting within this watch just sets the baseline.
                        lastSeenTracker[addr] = new SeenRecord { LastSeen = lastSeen, WatchId = watchId };
                    }
                    else if (lastSeen != rec.LastSeen)
                    {
                        rec.LastSeen = lastSeen;
                        rec.Streak++;
                        if (rec.Streak >= 2)
                        {
                            found[side] = d;
                        }
                        else
                        {
                            bumpArmed = true;
                            Log.Write("lastSeen bump #1 for " + Label(side) + " - running short confirmation pass");
                        }
                    }
                    else if (!confirmRound)
                    {
                        // An isolated bump on a full pass was connection noise,
                        // not pair mode. A short confirmation pass can simply miss
                        // an inquiry response, so it doesn't reset the streak.
                        rec.Streak = 0;
                    }
                }

                if (token.IsCancellationRequested) return;

                foreach (var side in activeSides)
                {
                    if (reconnected.Contains(side))
                    {
                        bool wasWatching;
                        lock (gate)
                        {
                            wasWatching = states.TryGetValue(side, out SideState st) && st.Phase == Phase.Watching;
                            if (wasWatching) states.Remove(side);
                        }
                        if (wasWatching)
                        {
                            // Still paired, nothing happened: withdraw the "Re-pair?"
                            // prompt silently.
                            Log.Write("reconnected with existing bond " + Label(side) + ", watch ended");
                            DismissToast(side);
                        }
                        continue;
                    }

                    if (!found.TryGetValue(side, out BLUETOOTH_DEVICE_INFO deviceInfo)) continue;

                    // Side may have been cleared by Enabled = false or moved back to
                    // PendingUnplug by a fresh USB plug-in in the gap.
                    lock (gate)
                    {
                        if (!states.TryGetValue(side, out SideState st) || st.Phase != Phase.Watching)
                            continue;
                    }

                    // Pair mode confirmed - the user long-pressed the combo. Only now
                    // is it safe to drop the old pairing record and pair fresh. If the
                    // authenticate below fails, the device is no longer remembered and
                    // the next pass retries it through the unknown-device path.
                    Log.Write("pair mode detected " + Label(side)
                        + " addr=" + AddressToString(deviceInfo.Address)
                        + " remembered=" + deviceInfo.fRemembered);
                    if (deviceInfo.fRemembered || deviceInfo.fAuthenticated)
                    {
                        // Last line of defense before the one destructive step: a
                        // controller that is connected (or connecting) right now is
                        // reconnecting normally, not in pair mode - the enumeration
                        // snapshot may be seconds stale, so re-query fresh state.
                        if (TryGetDeviceInfo(deviceInfo.Address, out BLUETOOTH_DEVICE_INFO fresh) && fresh.fConnected)
                        {
                            Log.Write("skip unpair " + Label(side) + ": device is connected - reconnect in progress, not pair mode");
                            continue;
                        }

                        var addr = deviceInfo.Address;
                        uint removeErr = BluetoothRemoveDevice(ref addr);
                        Log.Write("removed old pairing " + Label(side) + " result=" + removeErr);
                        if (removeErr != 0)
                        {
                            // Pairing on top of a record we failed to remove invites a
                            // false success (authenticate would report 259 against the
                            // stale bond). Leave the watch in place and retry next pass.
                            continue;
                        }
                        lock (gate)
                        {
                            if (states.TryGetValue(side, out SideState st2)) st2.RemovedBond = true;
                        }

                        // The struct still carries pre-removal state, and the
                        // authenticate call consults it (observed: an instant 259
                        // "already authenticated" when these flags are stale).
                        // Reflect reality: the record is gone now.
                        deviceInfo.fAuthenticated = false;
                        deviceInfo.fRemembered = false;
                        deviceInfo.fConnected = false;
                    }

                    uint err = 0;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        lock (gate) { pendingAuthAddr = deviceInfo.Address.ullLong; }
                        try
                        {
                            err = BluetoothAuthenticateDeviceEx(
                                IntPtr.Zero, IntPtr.Zero, ref deviceInfo, IntPtr.Zero, MITMProtectionNotRequired);
                        }
                        finally
                        {
                            lock (gate) { pendingAuthAddr = null; }
                        }

                        Log.Write("authenticate " + Label(side) + " result=" + err);
                        if (err != 259) break;

                        // 259 = ERROR_NO_MORE_ITEMS: "device is already authenticated".
                        // Either the bond genuinely exists (e.g. something else paired
                        // it) - confirm with a fresh query and call it success - or the
                        // stack's view is stale right after our removal: settle briefly
                        // and retry within this pass instead of burning a full one.
                        if (TryGetDeviceInfo(deviceInfo.Address, out BLUETOOTH_DEVICE_INFO postAuth)
                            && postAuth.fAuthenticated)
                        {
                            Log.Write("authenticate " + Label(side) + " reported already-authenticated and bond confirmed - treating as success");
                            err = 0;
                            break;
                        }
                        if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) return;
                        Log.Write("authenticate " + Label(side) + " stale already-authenticated, retrying");
                    }

                    if (err == 0)
                    {
                        // Bind the HID profile so Windows actually treats this as a gamepad.
                        // Without this, the device appears under "Other devices" and won't
                        // stay connected.
                        var hidGuid = HidServiceClassGuid;
                        uint svcErr = BluetoothSetServiceState(
                            IntPtr.Zero, ref deviceInfo, ref hidGuid, BLUETOOTH_SERVICE_ENABLE);
                        if (svcErr != 0)
                            Log.Write("HID service enable " + Label(side) + " FAILED err=" + svcErr + " - device may not stay connected");

                        lock (gate) { states.Remove(side); }
                        ShowToast(side,
                            SideName(side) + " Sense controller paired",
                            null,
                            ToolTipIcon.Info);
                    }
                    // err != 0: leave in watch state, retry on the next pass.
                }

                // One short confirmation pass per bump, started without the usual
                // gap; otherwise pause briefly before the next full pass.
                confirmRound = bumpArmed;
                if (!confirmRound)
                {
                    if (token.WaitHandle.WaitOne(ScanGap)) return;
                }
            }
        }

        // One toast slot per side: a new message for the same side replaces the
        // previous one in place instead of colliding with the other side's.
        private static string ToastTag(SenseSide side)
        {
            return side == SenseSide.Left ? "sense-left" : "sense-right";
        }

        private void ShowToast(SenseSide side, string title, string body, ToolTipIcon fallbackIcon)
        {
            string tag = ToastTag(side);
            try
            {
                Toasts.Show(tag, title, body);
                Log.Write("toast [" + tag + "] " + title + (string.IsNullOrEmpty(body) ? "" : " | " + body));
            }
            catch (Exception ex)
            {
                // WinRT toasts unavailable (old OS, broken registration) - fall back
                // to a balloon tip on the tray icon.
                Log.Write("toast [" + tag + "] FAILED (" + ex.GetType().Name + ": " + ex.Message + "), falling back to balloon: " + title);
                try
                {
                    trayIcon.BalloonTipTitle = title;
                    trayIcon.BalloonTipText  = string.IsNullOrEmpty(body) ? title : body;
                    trayIcon.BalloonTipIcon  = fallbackIcon;
                    trayIcon.ShowBalloonTip(7000);
                }
                catch
                {
                    // tray icon being disposed during shutdown - swallow
                }
            }
        }

        // Withdraw the side's current toast (if any) without showing a new one.
        private static void DismissToast(SenseSide side)
        {
            try
            {
                Toasts.Remove(ToastTag(side));
            }
            catch
            {
                // toast history unavailable - nothing to clean up
            }
        }

        // Pair-mode combo differs per side on the PSVR2 Sense.
        private static string PairCombo(SenseSide s)
        {
            return s == SenseSide.Left ? "PS + Create" : "PS + Options";
        }

        private static string Label(SenseSide s)
        {
            return s == SenseSide.Left ? "(L)" : "(R)";
        }

        private static string SideName(SenseSide s)
        {
            return s == SenseSide.Left ? "Left" : "Right";
        }
    }
}
