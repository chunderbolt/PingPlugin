﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PingPlugin.PingTrackers
{
    public class Win32APIPingTracker : IPingTracker
    {
        private readonly CancellationTokenSource tokenSource;
        private readonly int pid;
        private readonly PingConfiguration config;

        public bool Reset { get; set; }
        public double AverageRTT { get; set; }
        public IPAddress SeAddress { get; set; }
        public long SeAddressRaw { get; set; }
        public WinError LastError { get; set; }
        public ulong LastRTT { get; set; }
        public Queue<float> RTTTimes { get; set; }

        public Win32APIPingTracker(PingConfiguration config)
        {
            this.tokenSource = new CancellationTokenSource();
            this.pid = Process.GetProcessesByName("ffxiv_dx11")[0].Id;
            this.config = config;

            UpdateSeAddress();

            RTTTimes = new Queue<float>(this.config.PingQueueSize);

            Task.Run(() => PingLoop(this.tokenSource.Token));
            Task.Run(() => CheckAddressLoop(this.tokenSource.Token));
        }

        private void NextRTTCalculation(ulong nextRTT)
        {
            lock (RTTTimes)
            {
                RTTTimes.Enqueue(nextRTT);

                while (RTTTimes.Count > this.config.PingQueueSize)
                    RTTTimes.Dequeue();
            }
            CalcAverage();

            LastRTT = nextRTT;
        }

        private void CalcAverage() => AverageRTT = RTTTimes.Average();

        private void ResetRTT() => RTTTimes = new Queue<float>();

        /*
         * This might be done instead of using the game packets for two reasons (if the first reason proves invalid, the old stuff is committed to roll back).
         * First, there doesn't seem to be a good pair of game packets to use, since the only packets that provide a good indication of latency are the
         * actor cast and skill packet pairs. Casts work well, but skills can obviously only be used on valid targets, making that method moot outside of
         * combat (maybe sensor fusion? seems overcomplicated). Ping packets seem to have little correlation to your actual ping, as their dTimes
         * tend to vary dramatically between exchanges. It's entirely possible that this so-called ping packet is really just a keepalive. There's a number of
         * actions that don't send responses, as well, including movement and chat. Besides all these, there are some other good pairs such as search info
         * settings, weapon draws/sheaths, etc., that could give a very accurate representation of latency, but they're also all actions that are only performed
         * rarely, even in aggregate.
         *
         * So, what's the other reason for doing it this way? No need to update opcodes ever lol.
         */
        private async Task PingLoop(CancellationToken token)
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();
                var rtt = GetAddressLastRTT(SeAddressRaw);
                LastError = (WinError)Marshal.GetLastWin32Error();
                if (LastError == WinError.NO_ERROR)
                    NextRTTCalculation(rtt);
                await Task.Delay(3000, token);
            }
        }

        private async Task CheckAddressLoop(CancellationToken token)
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();
                var lastAddress = SeAddress;
                UpdateSeAddress();
                if (!lastAddress.Equals(SeAddress))
                {
                    Reset = true;
                    ResetRTT();
                }
                else
                {
                    Reset = false;
                }
                await Task.Delay(10000, token); // It's probably not that expensive, but it's not like the address is constantly changing, either.
            }
        }

        private void UpdateSeAddress()
        {
            SeAddressRaw = GetProcessHighestPortAddress(this.pid);
            SeAddress = new IPAddress(SeAddressRaw);
        }

        [DllImport("OSBindings.dll", EntryPoint = "?GetProcessHighestPortAddress@@YAKH@Z")]
        private static extern long GetProcessHighestPortAddress(int pid);

        [DllImport("OSBindings.dll", EntryPoint = "?GetAddressLastRTT@@YAKK@Z", SetLastError = true)]
        private static extern ulong GetAddressLastRTT(long address);

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.tokenSource.Cancel();
                this.tokenSource.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
