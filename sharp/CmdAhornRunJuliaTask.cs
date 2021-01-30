﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Olympus {
    public unsafe class CmdAhornRunJuliaTask : Cmd<string, bool?, IEnumerator> {

        public static readonly Regex EscapeCmdRegex = new Regex("\u001B....|\\^\\[\\[.25.|\\^\\[\\[2K|\\^M");
        public static readonly Regex EscapeDashRegex = new Regex(@"─+");

        public override bool LogRun => false;

        public override IEnumerator Run(string script, bool? localDepot) {
            string tmpFilename = null;
            try {
                using (ManualResetEvent timeout = new ManualResetEvent(false))
                using (Process process = AhornHelper.NewJulia(out tmpFilename, script, localDepot)) {
                    process.Start();

                    bool dead = false;
                    int timeoutThreadID = 0;
                    int lineID = 0;
                    WaitHandle[] timeoutHandle = new WaitHandle[] { timeout };
                    Thread killer = null;

                    for (string line; (line = process.StandardOutput.ReadLine()) != null;) {
                        if (line.StartsWith("#OLYMPUS# ")) {
                            line = line.Substring("#OLYMPUS# ".Length);
                            if (line == "TIMEOUT START") {
                                if (killer == null) {
                                    timeoutThreadID++;
                                    timeout.Reset();
                                    killer = new Thread(() => {
                                        int timeoutThreadIDCurrent = timeoutThreadID;
                                        int lineIDCurrent = lineID;
                                        try {
                                            while (!dead && timeoutThreadID == timeoutThreadIDCurrent) {
                                                int waited = WaitHandle.WaitAny(timeoutHandle, 10 * 60 * 1000);
                                                timeout.Reset();
                                                if (waited == WaitHandle.WaitTimeout && !dead && timeoutThreadID == timeoutThreadIDCurrent && lineID == lineIDCurrent) {
                                                    dead = true;
                                                    process.Kill();
                                                    return;
                                                }
                                                lineIDCurrent = lineID;
                                            }
                                        } catch {
                                        }
                                    }) {
                                        Name = $"Olympus Julia watchdog thread {process}",
                                        IsBackground = true
                                    };
                                    killer.Start();
                                }

                            } else if (line == "TIMEOUT END") {
                                timeoutThreadID++;
                                killer = null;
                                timeout.Set();

                            } else {
                                process.Kill();
                                throw new Exception("Unexpected #OLYMPUS# command:" + line);
                            }
                        } else {
                            lineID++;
                            timeout.Set();
                            yield return Status(Escape(line, out bool update), false, "", update);
                        }
                    }

                    process.WaitForExit();
                    dead = true;
                    timeout.Set();
                    killer?.Join();
                    if (process.ExitCode != 0 || dead)
                        throw new Exception("Julia encountered a fatal error:" + process.StandardError.ReadToEnd());
                }
            } finally {
                if (!string.IsNullOrEmpty(tmpFilename) && File.Exists(tmpFilename))
                    File.Delete(tmpFilename);
            }
        }

        public static string Escape(string line, out bool update) {
            line = EscapeCmdRegex.Replace(line, "");
            line = EscapeDashRegex.Replace(line, "-");
            update = line.StartsWith("#") && line.EndsWith("%");
            return line;
        }

    }
}
