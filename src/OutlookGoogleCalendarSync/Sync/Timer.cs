﻿using log4net;
using System;
using System.Windows.Forms;

namespace OutlookGoogleCalendarSync.Sync {
    public class SyncTimer : Timer {
        private static readonly ILog log = LogManager.GetLogger(typeof(SyncTimer));
        private object owningProfile;
        
        public DateTime LastSyncDate { internal get; set; }

        private DateTime? nextSyncDate;
        public DateTime? NextSyncDate {
            get { return nextSyncDate; }
            set {
                nextSyncDate = value;
                if (nextSyncDate != null) {
                    DateTime theDate = (DateTime)nextSyncDate;
                    var profile = owningProfile as SettingsStore.Calendar;
                    NextSyncDateText = theDate.ToLongDateString() + " @ " + theDate.ToLongTimeString() + (profile.OutlookPush ? " + Push" : "");
                    log.Info("Next sync scheduled for " + NextSyncDateText);
                }
            }
        }

        private String nextSyncDateText;
        public String NextSyncDateText {
            get { return nextSyncDateText; }
            set {
                nextSyncDateText = value;
                var profile = owningProfile as SettingsStore.Calendar;
                if (Forms.Main.Instance.ActiveCalendarProfile._ProfileName == profile._ProfileName)
                    Forms.Main.Instance.NextSyncVal = value;
            }
        }

        public SyncTimer(Object owningProfile) {
            this.owningProfile = owningProfile;
            this.Tag = "AutoSyncTimer";
            this.Tick += new EventHandler(ogcsTimer_Tick);
            this.Interval = int.MaxValue;

            SetNextSync();
        }

        private void ogcsTimer_Tick(object sender, EventArgs e) {
            if (Forms.ErrorReporting.Instance.Visible) return;

            log.Debug("Scheduled sync triggered.");

            if (!Sync.Engine.Instance.SyncingNow) {
                Sync.Engine.Instance.ActiveProfile = this.owningProfile;
                if (Settings.GetProfileType(this.owningProfile) == Settings.ProfileType.Calendar)
                    Forms.Main.Instance.NotificationTray.ShowBubbleInfo("Autosyncing calendars: " + (Engine.Instance.ActiveProfile as SettingsStore.Calendar).SyncDirection.Name + "...");
                Forms.Main.Instance.Sync_Click(sender, null);
            } else {
                log.Debug("Busy syncing already. Rescheduled for 5 mins time.");
                SetNextSync(5, fromNow: true);
            }
        }

        private int getResyncInterval() {
            var profile = (this.owningProfile as SettingsStore.Calendar);
            int min = profile.SyncInterval;
            if (profile.SyncIntervalUnit == "Hours") {
                min *= 60;
            }
            return min;
        }

        /// <summary>Configure the next sync according to configured schedule in Settings.</summary>
        public void SetNextSync() {
            SetNextSync(getResyncInterval());
        }

        /// <summary>Configure the next sync that override any configured schedule in Settings.</summary>
        /// <param name="delayMins">Number of minutes to next sync</param>
        /// <param name="fromNow">From now or since last successful sync</param>
        /// <param name="calculateInterval">Calculate milliseconds to next sync and activate timer</param>
        public void SetNextSync(int delayMins, Boolean fromNow = false, Boolean calculateInterval = true) {
            int syncInterval = 0;
            if (owningProfile is SettingsStore.Calendar) {
                syncInterval = Settings.GetCalendarProfile(owningProfile).SyncInterval;
            }            
            
            if (syncInterval != 0) {
                DateTime now = DateTime.Now;
                this.nextSyncDate = fromNow ? now.AddMinutes(delayMins) : LastSyncDate.AddMinutes(delayMins);
                if (calculateInterval) CalculateInterval();
                else this.NextSyncDate = this.nextSyncDate;
            } else {
                this.NextSyncDateText = "Inactive";
                Activate(false);
                log.Info("Schedule disabled.");
            }
        }

        public void CalculateInterval() {
            if ((owningProfile as SettingsStore.Calendar).SyncInterval == 0) return;

            DateTime now = DateTime.Now;
            double interval = ((DateTime)this.nextSyncDate - now).TotalMinutes;

            if (this.Interval != (interval * 60000)) {
                Activate(false);
                if (interval < 0) {
                    log.Debug("Moving past sync into imminent future.");
                    this.Interval = 1 * 60000;
                } else if (interval == 0)
                    this.Interval = 1000;
                else
                    this.Interval = (int)Math.Min((interval * 60000), int.MaxValue);
                this.NextSyncDate = now.AddMilliseconds(this.Interval);
            }
            Activate(true);
        }

        public void Activate(Boolean activate) {
            if (Forms.Main.Instance.InvokeRequired) {
                log.Error("Attempted to " + (activate ? "" : "de") + "activate " + this.Tag + " from non-GUI thread will not work.");
                return;
            }

            if (activate && !this.Enabled) this.Start();
            else if (!activate && this.Enabled) this.Stop();
        }

        public Boolean Running() {
            return this.Enabled;
        }

        public String Status() {
            var profile = (owningProfile as SettingsStore.Calendar);
            if (this.Running()) return NextSyncDateText;
            else if (profile.OgcsPushTimer != null && profile.OgcsPushTimer.Running()) return "Push Sync Active";
            else return "Inactive";
        }
    }


    public class PushSyncTimer : Timer {
        private static readonly ILog log = LogManager.GetLogger(typeof(PushSyncTimer));
        private object owningProfile;
        private DateTime lastRunTime;
        private Int32 lastRunItemCount;
        private Int16 failures = 0;
        private static PushSyncTimer instance;
        public static PushSyncTimer Instance(Object owningProfile) {
            if (instance == null) {
                instance = new PushSyncTimer(owningProfile);
            }
            return instance;
        }

        private PushSyncTimer(Object owningProfile) {
            this.owningProfile = owningProfile;
            ResetLastRun();
            this.Tag = "PushTimer";
            this.Interval = 2 * 60000;
            this.Tick += new EventHandler(ogcsPushTimer_Tick);
        }

        /// <summary>
        /// Recalculate item count as of now.
        /// </summary>
        public void ResetLastRun() {
            this.lastRunTime = DateTime.Now;
            try {
                log.Fine("Updating calendar item count following Push Sync.");
                this.lastRunItemCount = OutlookOgcs.Calendar.Instance.GetCalendarEntriesInRange(this.owningProfile as SettingsStore.Calendar, true).Count;
            } catch (System.Exception ex) {
                OGCSexception.Analyse("Failed to update item count following a Push Sync.", ex);
            }
        }

        private void ogcsPushTimer_Tick(object sender, EventArgs e) {
            if (Forms.ErrorReporting.Instance.Visible) return;
            log.Fine("Push sync triggered.");

            try {
                if (OutlookOgcs.Calendar.Instance.IOutlook.NoGUIexists()) return;

                System.Collections.Generic.List<Microsoft.Office.Interop.Outlook.AppointmentItem> items = OutlookOgcs.Calendar.Instance.GetCalendarEntriesInRange((SettingsStore.Calendar)this.owningProfile, true);

                if (items.Count < this.lastRunItemCount || items.FindAll(x => x.LastModificationTime > this.lastRunTime).Count > 0) {
                    log.Debug("Changes found for Push sync.");
                    Forms.Main.Instance.NotificationTray.ShowBubbleInfo("Autosyncing calendars: " + (this.owningProfile as SettingsStore.Calendar).SyncDirection.Name + "...");
                    if (!Sync.Engine.Instance.SyncingNow) {
                        Forms.Main.Instance.Sync_Click(sender, null);
                    } else {
                        log.Debug("Busy syncing already. No need to push.");
                    }
                } else {
                    log.Fine("No changes found.");
                }
                failures = 0;
            } catch (System.Exception ex) {
                failures++;
                log.Warn("Push Sync failed " + failures + " times to check for changed items.");

                String hResult = OGCSexception.GetErrorCode(ex);
                if ((ex is System.InvalidCastException && hResult == "0x80004002" && ex.Message.Contains("0x800706BA")) || //The RPC server is unavailable
                    (ex is System.Runtime.InteropServices.COMException && (
                        ex.Message.Contains("0x80010108(RPC_E_DISCONNECTED)") || //The object invoked has disconnected from its clients
                        hResult == "0x800706BE" || //The remote procedure call failed
                        hResult == "0x800706BA")) //The RPC server is unavailable
                    ) {
                    OGCSexception.Analyse(OGCSexception.LogAsFail(ex));
                    try {
                        OutlookOgcs.Calendar.Instance.Reset();
                    } catch (System.Exception ex2) {
                        OGCSexception.Analyse("Failed resetting Outlook connection.", ex2);
                    }
                } else
                    OGCSexception.Analyse(ex);
                if (failures == 10)
                    Forms.Main.Instance.Console.UpdateWithError("Push Sync is failing.", ex, notifyBubble: true);
            }
        }

        public void Activate(Boolean activate) {
            SettingsStore.Calendar profile = this.owningProfile as SettingsStore.Calendar;
            if (activate && !this.Enabled) {
                ResetLastRun();
                this.Start();
                if (profile.SyncInterval == 0 && profile._ProfileName == Forms.Main.Instance.ActiveCalendarProfile._ProfileName) 
                    Forms.Main.Instance.NextSyncVal = "Push Sync Active";
            } else if (!activate && this.Enabled) {
                this.Stop();
                profile.OgcsTimer.SetNextSync();
            }
        }
        public Boolean Running() {
            return this.Enabled;
        }
    }
}
