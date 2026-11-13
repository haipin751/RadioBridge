// RadioBridge v3.0 - Simplified Turn-Based HTTP Bridge for Radio Commander (BepInEx 5, C# 5)
// Only retains 3 core capabilities:
// 1. Mission Objectives (ObjectivesController + HQController)
// 2. Radio Communication Logs & Subtitles (GiveLogTexts + SubtitleTextController)
// 3. Atomic Communication Sending & Turn-Based Busy Detection (/status & /send)
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RadioBridge
{
    [BepInPlugin("local.radiobridge", "RadioBridge", "3.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        const int Port = 8765;
        static readonly BindingFlags BF = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        HttpListener listener;
        static readonly object Lock = new object();

        // Recent subtitles buffer (captures voice lines even if not yet in radio log)
        static readonly List<string> Subtitles = new List<string>();
        static int NextSubId = 1;

        // Synchronous Main-Thread Dispatcher
        class MainTask
        {
            public Func<string> Fn;
            public string Result;
            public Exception Error;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }
        static readonly Queue<MainTask> Tasks = new Queue<MainTask>();

        void Awake()
        {
            Application.runInBackground = true;
            try
            {
                var h = new Harmony("local.radiobridge");
                var m = AccessTools.Method(AccessTools.TypeByName("Radio.SubtitleTextController"), "SetMyValues");
                if (m != null)
                    h.Patch(m, null, new HarmonyMethod(typeof(Plugin).GetMethod("OnSubtitle", BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e) { Logger.LogError("Subtitle hook failed: " + e); }

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                listener.Start();
                var t = new Thread(HttpLoop);
                t.IsBackground = true;
                t.Start();
                Logger.LogInfo("RadioBridge v3.0 listening on http://127.0.0.1:" + Port + "/");
            }
            catch (Exception e) { Logger.LogError("HTTP start failed: " + e); }
        }

        void OnDestroy()
        {
            try { if (listener != null) listener.Close(); } catch { }
        }

        void Update()
        {
            if (!Application.runInBackground) Application.runInBackground = true;

            while (true)
            {
                MainTask task = null;
                lock (Lock)
                {
                    if (Tasks.Count > 0) task = Tasks.Dequeue();
                }
                if (task == null) break;
                try { task.Result = task.Fn(); }
                catch (Exception e) { task.Error = e; }
                finally { task.Done.Set(); }
            }
        }

        static string RunOnMain(Func<string> fn, int timeoutMs)
        {
            var task = new MainTask { Fn = fn };
            lock (Lock) { Tasks.Enqueue(task); }
            if (!task.Done.WaitOne(timeoutMs))
                throw new TimeoutException("Unity main thread did not respond within " + timeoutMs + "ms");
            if (task.Error != null)
                throw task.Error;
            return task.Result;
        }

        static void OnSubtitle(float givenStartTime, float givenEndTime, string givenText)
        {
            if (string.IsNullOrEmpty(givenText)) return;
            lock (Lock)
            {
                Subtitles.Add("{\"id\":" + (NextSubId++) + ",\"text\":" + Q(givenText.Trim()) + "}");
                if (Subtitles.Count > 30) Subtitles.RemoveRange(0, Subtitles.Count - 30);
            }
        }

        // ---------------- Reflection Helpers ----------------
        static readonly Dictionary<string, FieldInfo> FC = new Dictionary<string, FieldInfo>();
        static readonly Dictionary<string, PropertyInfo> PC = new Dictionary<string, PropertyInfo>();
        static readonly Dictionary<string, MethodInfo> MC = new Dictionary<string, MethodInfo>();

        static object Inst(string typeName)
        {
            var t = AccessTools.TypeByName(typeName);
            if (t == null) return null;
            var f = t.GetField("instance", BF);
            return f == null ? null : f.GetValue(null);
        }

        static object Get(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            string key = t.FullName + "." + name;
            FieldInfo f;
            if (!FC.TryGetValue(key, out f)) { f = t.GetField(name, BF); FC[key] = f; }
            if (f != null) return f.GetValue(o);
            PropertyInfo p;
            if (!PC.TryGetValue(key, out p)) { p = t.GetProperty(name, BF); PC[key] = p; }
            return p == null ? null : p.GetValue(o, null);
        }

        static void Set(object o, string name, object val)
        {
            if (o == null) return;
            var t = o.GetType();
            string key = t.FullName + "." + name;
            FieldInfo f;
            if (!FC.TryGetValue(key, out f)) { f = t.GetField(name, BF); FC[key] = f; }
            if (f != null) { f.SetValue(o, val); return; }
            PropertyInfo p;
            if (!PC.TryGetValue(key, out p)) { p = t.GetProperty(name, BF); PC[key] = p; }
            if (p != null) p.SetValue(o, val, null);
        }

        static object Call(object o, string method, params object[] args)
        {
            if (o == null) throw new Exception("Target null for " + method);
            var t = o.GetType();
            int n = args == null ? 0 : args.Length;
            string key = t.FullName + "." + method + "/" + n;
            MethodInfo mi;
            if (!MC.TryGetValue(key, out mi))
            {
                foreach (var m in t.GetMethods(BF))
                    if (m.Name == method && m.GetParameters().Length == n) { mi = m; break; }
                MC[key] = mi;
            }
            if (mi == null) throw new MissingMethodException(t.FullName, method);
            return mi.Invoke(o, args);
        }

        static string Str(object o) { return o == null ? "" : o.ToString(); }
        static string Bool(object o) { return (o is bool && (bool)o) ? "true" : "false"; }

        // ---------------- 1. Mission Objectives ----------------
        static string BuildObjectivesJson()
        {
            var list = new List<string>();
            var oc = Inst("Radio.ObjectivesController") as Component;
            if (oc != null && oc.transform != null)
            {
                foreach (Transform child in oc.transform)
                {
                    if (child == null || child.gameObject == null || !child.gameObject.activeSelf)
                        continue;
                    var objCtrl = child.gameObject.GetComponent("ObjectiveController");
                    if (objCtrl == null) continue;
                    int id = 0;
                    try { id = Convert.ToInt32(Get(objCtrl, "objectiveId")); } catch { }
                    string txt = "";
                    try
                    {
                        var textObj = Get(objCtrl, "objectiveText");
                        if (textObj != null) txt = Str(Call(textObj, "GiveText")).Trim();
                    }
                    catch { }
                    if (txt.Length > 0)
                        list.Add("{\"id\":" + id + ",\"text\":" + Q(txt) + "}");
                }
            }

            // Fallback: also check HQController objectiveAvatars if ObjectivesController yielded nothing
            if (list.Count == 0)
            {
                try
                {
                    var hqType = AccessTools.TypeByName("Radio.HQController");
                    if (hqType != null)
                    {
                        var hq = UnityEngine.Object.FindObjectOfType(hqType);
                        var avatars = Get(hq, "objectiveAvatars") as IList;
                        if (avatars != null)
                        {
                            int idx = 1;
                            foreach (var av in avatars)
                            {
                                var uiText = Get(av, "objectiveText") as UnityEngine.UI.Text;
                                string txt = uiText != null ? uiText.text.Trim() : "";
                                if (txt.Length > 0)
                                    list.Add("{\"id\":" + (idx++) + ",\"text\":" + Q(txt) + "}");
                            }
                        }
                    }
                }
                catch { }
            }
            return "[" + string.Join(",", list.ToArray()) + "]";
        }

        // ---------------- 2. Radio Communication Logs ----------------
        static string BuildLogsJson(object r, int limit, out int totalCount)
        {
            totalCount = 0;
            if (r == null) return "[]";
            var rawList = Call(r, "GiveLogTexts") as IList;
            if (rawList == null) return "[]";
            totalCount = rawList.Count;
            int start = (limit > 0 && totalCount > limit) ? (totalCount - limit) : 0;
            var res = new List<string>();
            for (int i = start; i < totalCount; i++)
            {
                var item = rawList[i];
                string t = Str(Get(item, "message_Time")).Trim();
                string m = Str(Get(item, "message_Text")).Trim();
                res.Add("{\"index\":" + (i + 1) + ",\"time\":" + Q(t) + ",\"text\":" + Q(m) + "}");
            }
            return "[" + string.Join(",", res.ToArray()) + "]";
        }

        // ---------------- 3. Busy State & Dynamic Options ----------------
        static bool IsCommBusy(object r)
        {
            if (r == null) return false;
            if (Equals(Get(r, "onAir"), true)) return true;

            var rBuf = Get(r, "bufforPile") as IList;
            if (rBuf != null && rBuf.Count > 0) return true;

            var rOut = Get(r, "outPile") as IList;
            if (rOut != null && rOut.Count > 0) return true;

            var aether = Inst("Radio.AetherController");
            if (aether != null)
            {
                if (Equals(Get(aether, "waiting"), true) || Equals(Get(aether, "goAhead"), true))
                    return true;
                var docs = Get(aether, "currentDOCs") as IList;
                if (docs != null && docs.Count > 0) return true;
                var dpile = Get(aether, "dialogsPile") as IList;
                if (dpile != null && dpile.Count > 0) return true;

                var friendly = Get(aether, "friendlyUnits") as IList;
                if (friendly != null)
                {
                    foreach (var roc in friendly)
                    {
                        if (roc == null) continue;
                        if (Get(roc, "queuedMessage") != null) return true;
                        var uBuf = Get(roc, "bufforPile") as IList;
                        if (uBuf != null && uBuf.Count > 0) return true;
                    }
                }
            }
            return false;
        }

        static bool IsDialogOpen(object r, bool autoShow)
        {
            if (r == null) return false;
            var dp = Get(r, "dialogPanel") as GameObject;
            bool active = dp != null && dp.activeSelf;
            bool mustOpen = Equals(Get(r, "mustOpenDialogPanel"), true);
            if (!active && mustOpen && autoShow)
            {
                try
                {
                    Call(r, "ButtonUnholdOld");
                    Call(r, "ShowDialog");
                    active = dp != null && dp.activeSelf;
                }
                catch { }
            }
            return active || mustOpen;
        }

        static List<string> GetDialogOptions(object r)
        {
            var res = new List<string>();
            if (!IsDialogOpen(r, true)) return res;
            var list = Get(r, "dialogOptionAvatarControllersList") as IList;
            if (list == null) return res;
            int idx = 1;
            foreach (var a in list)
            {
                var bt = Get(a, "buttonText") as UnityEngine.UI.Text;
                string txt = bt != null ? bt.text.Trim() : "";
                if (txt.Length > 0) res.Add(idx + ": " + txt);
                idx++;
            }
            return res;
        }

        static string[] KwRaw(string kind)
        {
            var vr = Inst("Radio.VoiceRecognitionController");
            var v = Get(vr, "keywords" + kind) as string[];
            if (v != null && v.Length > 0) return v;
            try
            {
                var sim = Inst("Radio.SimController");
                if (sim != null)
                {
                    if (kind == "Units") return (string[])Call(sim, "GiveUnitVRStrings");
                    if (kind == "Groups") return (string[])Call(sim, "GiveGroupsVRStrings");
                    if (kind == "Orders") return (string[])Call(sim, "GiveOrdersVRStrings");
                    if (kind == "Params") return (string[])Call(sim, "GiveParamsVRStrings");
                }
            }
            catch { }
            return new string[0];
        }

        static string BuildStatusJson(int logLimit)
        {
            var r = Inst("Radio.RadioController");
            var tc = Inst("Radio.TimeController");
            var sb = new StringBuilder("{");
            sb.Append("\"ok\":true");
            sb.Append(",\"in_mission\":").Append(r != null ? "true" : "false");

            if (tc != null)
            {
                string dt = "";
                try { dt = ((DateTime)Get(tc, "currentDateTime")).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
                sb.Append(",\"time\":{\"datetime\":").Append(Q(dt))
                  .Append(",\"speed\":").Append(Str(Get(tc, "currentSpeed")))
                  .Append(",\"multiplier\":").Append(Str(Get(tc, "currentMulti")))
                  .Append(",\"frozen\":").Append(Bool(Get(tc, "timeFreezed"))).Append("}");
            }
            else sb.Append(",\"time\":null");

            bool busy = IsCommBusy(r);
            bool dlgOpen = IsDialogOpen(r, true);
            sb.Append(",\"comm_busy\":").Append(busy ? "true" : "false");
            sb.Append(",\"dialog_open\":").Append(dlgOpen ? "true" : "false");

            // 1. Objectives
            sb.Append(",\"objectives\":").Append(BuildObjectivesJson());

            // 2. Communication Logs
            int totalLogs;
            string logsJson = BuildLogsJson(r, logLimit, out totalLogs);
            sb.Append(",\"total_logs\":").Append(totalLogs);
            sb.Append(",\"logs\":").Append(logsJson);

            lock (Lock)
            {
                int from = Math.Max(0, Subtitles.Count - 10);
                sb.Append(",\"recent_subtitles\":[").Append(string.Join(",", Subtitles.GetRange(from, Subtitles.Count - from).ToArray())).Append("]");
            }

            // 3. Dynamic Options for Initiating Communication
            sb.Append(",\"options\":{");
            sb.Append("\"dialog_options\":").Append(Arr(GetDialogOptions(r)));
            sb.Append(",\"units\":").Append(Arr(KwRaw("Units")));
            sb.Append(",\"groups\":").Append(Arr(KwRaw("Groups")));
            sb.Append(",\"orders\":").Append(Arr(KwRaw("Orders")));
            sb.Append(",\"params\":").Append(Arr(KwRaw("Params")));
            sb.Append("}}");
            return sb.ToString();
        }

        // ---------------- 4. Atomic Command Execution ----------------
        static string CoordStr(object map, Vector3 pos)
        {
            object c = Call(map, "GiveCoordinatesByWorldPosition", pos);
            if (c == null) return "";
            return Str(Get(c, "longitude")) + "/" + Str(Get(c, "latitude"));
        }

        static string BuildMapJson()
        {
            var map = Inst("Radio.MapController");
            if (map == null)
                return "{\"ok\":false,\"error\":\"MapController not available (not in mission)\"}";

            var sb = new StringBuilder("{");
            sb.Append("\"ok\":true");

            // Map grid bounds
            int minX = 0, maxX = 0, minY = 0, maxY = 0;
            try
            {
                minX = Convert.ToInt32(Call(map, "GiveMinLongitude", false));
                maxX = Convert.ToInt32(Call(map, "GiveMaxLongitude", false));
                minY = Convert.ToInt32(Call(map, "GiveMinLatitude", false));
                maxY = Convert.ToInt32(Call(map, "GiveMaxLatitude", false));
            }
            catch { }
            sb.Append(",\"bounds\":{\"min_x\":").Append(minX)
              .Append(",\"max_x\":").Append(maxX)
              .Append(",\"min_y\":").Append(minY)
              .Append(",\"max_y\":").Append(maxY).Append("}");

            // Waypoints (landmarks/bases/etc.)
            sb.Append(",\"waypoints\":[");
            bool firstWp = true;
            try
            {
                var wcType = AccessTools.TypeByName("Radio.WaypointController");
                var wcs = wcType != null ? UnityEngine.Object.FindObjectsOfType(wcType) : null;
                if (wcs != null)
                {
                    foreach (var wc in wcs)
                    {
                        var comp = wc as Component;
                        if (comp == null || !comp.gameObject.activeInHierarchy) continue;
                        string wpType = Str(Get(wc, "waypointType"));
                        int wpInt = 0;
                        try { wpInt = Convert.ToInt32(Get(wc, "waypointInt")); } catch { }
                        string name = wpInt > 0 ? (wpType + " " + wpInt) : wpType;
                        string coord = CoordStr(map, comp.transform.position);
                        if (!firstWp) sb.Append(",");
                        firstWp = false;
                        sb.Append("{\"name\":").Append(Q(name)).Append(",\"coord\":").Append(Q(coord)).Append("}");
                    }
                }
            }
            catch { }
            sb.Append("]");

            // Objective ping coordinates
            sb.Append(",\"objectives\":[");
            bool firstObj = true;
            try
            {
                var ocType = AccessTools.TypeByName("Radio.ObjectiveController");
                var ocs = ocType != null ? UnityEngine.Object.FindObjectsOfType(ocType) : null;
                if (ocs != null)
                {
                    foreach (var oc in ocs)
                    {
                        var comp = oc as Component;
                        if (comp == null || !comp.gameObject.activeInHierarchy) continue;
                        var coords = new List<string>();
                        if (Equals(Get(oc, "pingSelfPosition"), true))
                        {
                            string c = CoordStr(map, comp.transform.position);
                            if (c.Length > 0) coords.Add(c);
                        }
                        if (Equals(Get(oc, "pingObjectsPositions"), true))
                        {
                            var arr = Get(oc, "pingPositions") as Array;
                            if (arr != null)
                            {
                                foreach (var item in arr)
                                {
                                    var go = item as GameObject;
                                    if (go != null)
                                    {
                                        string c = CoordStr(map, go.transform.position);
                                        if (c.Length > 0) coords.Add(c);
                                    }
                                }
                            }
                        }
                        if (coords.Count == 0) continue;
                        if (!firstObj) sb.Append(",");
                        firstObj = false;
                        sb.Append("{\"coords\":").Append(Arr(coords)).Append("}");
                    }
                }
            }
            catch { }
            sb.Append("]");

            // Map markers / tokens (Radio.ObjectController, e.g. VC camp?, unit tokens)
            sb.Append(",\"markers\":[");
            bool firstMarker = true;
            try
            {
                var ocType = AccessTools.TypeByName("Radio.ObjectController");
                var ocs = ocType != null ? UnityEngine.Object.FindObjectsOfType(ocType) : null;
                if (ocs != null)
                {
                    foreach (var oc in ocs)
                    {
                        var comp = oc as Component;
                        if (comp == null || !comp.gameObject.activeInHierarchy) continue;
                        string name = Str(Get(oc, "ObjectName"));
                        if (string.IsNullOrEmpty(name)) name = Str(Get(oc, "objectName"));
                        if (string.IsNullOrEmpty(name)) name = comp.gameObject.name;
                        string coord = CoordStr(map, comp.transform.position);
                        if (string.IsNullOrEmpty(coord)) continue;
                        if (!firstMarker) sb.Append(",");
                        firstMarker = false;
                        sb.Append("{\"name\":").Append(Q(name)).Append(",\"coord\":").Append(Q(coord)).Append("}");
                    }
                }
            }
            catch { }
            sb.Append("]");

            // Friendly unit coordinates
            sb.Append(",\"units\":[");
            bool firstUnit = true;
            try
            {
                var aether = Inst("Radio.AetherController");
                var fUnits = Get(aether, "friendlyUnits") as IList;
                if (fUnits != null)
                {
                    foreach (var u in fUnits)
                    {
                        var comp = u as Component;
                        if (comp == null) continue;
                        var ro = Get(u, "radioOperatorController");
                        string name = ro != null ? Str(Get(ro, "UnitRadioName")) : comp.gameObject.name;
                        string coord = CoordStr(map, comp.transform.position);
                        if (!firstUnit) sb.Append(",");
                        firstUnit = false;
                        sb.Append("{\"name\":").Append(Q(name)).Append(",\"coord\":").Append(Q(coord)).Append("}");
                    }
                }
            }
            catch { }
            sb.Append("]}");
            return sb.ToString();
        }

        static void SetSpeedInternal(object r, object tc, int speed)
        {
            if (tc == null) return;
            if (speed <= 0) Call(tc, "SetSpeed0");
            else if (speed == 1) Call(tc, "SetSpeed1");
            else
            {
                if (r != null && !Equals(Get(r, "isOn"), true)) Call(r, "SwitchOn");
                Call(tc, "SetSpeed2");
            }
        }

        static void PrepareRadioMenu(object r)
        {
            if (!Equals(Get(r, "isOn"), true)) Call(r, "SwitchOn");
            Set(r, "isHoldingButton", true);
            // RadioTryOpenPanel initializes currentShortcut before SelectMe writes shortcut levels
            Call(r, "RadioTryOpenPanel");
            Call(r, "EraseOrders");
            Call(r, "SetUnits");
        }

        static bool SelectUnit(object r, string unitSpec, out string matchedName, out List<string> available)
        {
            matchedName = "";
            available = new List<string>();
            var list = Get(r, "unitAvatarControllersList") as IList;
            if (list == null || list.Count == 0)
            {
                Call(r, "SetUnits");
                list = Get(r, "unitAvatarControllersList") as IList;
            }
            if (list == null) return false;

            int idx;
            bool isIdx = int.TryParse(unitSpec, out idx);
            int cur = 1;
            foreach (var av in list)
            {
                var roc = Get(av, "radioOperatorController");
                string radioName = Str(Get(roc, "UnitRadioName")).Trim();
                string vrName = "";
                try { vrName = Str(Get(Get(roc, "operatorLines"), "unitVoiceRecognitionName")).Trim(); } catch { }
                available.Add(vrName.Length > 0 ? vrName : radioName);

                if ((isIdx && cur == idx) ||
                    string.Equals(vrName, unitSpec, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(radioName, unitSpec, StringComparison.OrdinalIgnoreCase) ||
                    (unitSpec.Length >= 2 && (vrName.StartsWith(unitSpec, StringComparison.OrdinalIgnoreCase) || radioName.StartsWith(unitSpec, StringComparison.OrdinalIgnoreCase))))
                {
                    Call(av, "SelectMe", true);
                    matchedName = radioName.Length > 0 ? radioName : vrName;
                    return Get(r, "currentReceiverRadioOperatorController") != null;
                }
                cur++;
            }
            return false;
        }

        static bool SelectGroup(object r, string groupSpec, out string matchedName, out List<string> available)
        {
            matchedName = "";
            available = new List<string>();
            var list = Get(r, "groupAvatarControllersList") as IList;
            if (list == null) return false;

            int idx;
            bool isIdx = int.TryParse(groupSpec, out idx);
            int cur = 1;
            foreach (var av in list)
            {
                var grpObj = Get(av, "radioGroupObject");
                string desc = "";
                try { desc = Str(Call(Get(grpObj, "groupDescription"), "GiveText")).Trim(); } catch { }
                available.Add(desc);

                if ((isIdx && cur == idx) ||
                    string.Equals(desc, groupSpec, StringComparison.OrdinalIgnoreCase) ||
                    (groupSpec.Length > 0 && desc.IndexOf(groupSpec, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Call(av, "SelectMe", true);
                    matchedName = desc;
                    return Get(r, "currentRadioGroupObject") != null;
                }
                cur++;
            }
            return false;
        }

        static bool SelectOrder(object r, string orderSpec, out string matchedName, out List<string> available)
        {
            matchedName = "";
            available = new List<string>();
            var list = Get(r, "orderAvatarControllersList") as IList;
            if (list == null) return false;

            int idx;
            bool isIdx = int.TryParse(orderSpec, out idx);
            int cur = 1;
            foreach (var av in list)
            {
                var ordObj = Get(av, "radioOrderObject");
                string desc = "";
                try { desc = Str(Call(Get(ordObj, "orderDescription"), "GiveText")).Trim(); } catch { }
                available.Add(desc);

                if ((isIdx && cur == idx) ||
                    string.Equals(desc, orderSpec, StringComparison.OrdinalIgnoreCase) ||
                    (orderSpec.Length > 0 && desc.IndexOf(orderSpec, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Call(av, "SelectMe", true);
                    matchedName = desc;
                    return Get(r, "currentRadioOrderObject") != null;
                }
                cur++;
            }
            return false;
        }

        static bool SelectParam(object r, string paramSpec, out string matchedParam)
        {
            matchedParam = paramSpec;
            int n;
            if (int.TryParse(paramSpec, out n))
            {
                Call(r, "PickVoiceFromInts", n);
                return true;
            }
            var pList = Get(r, "paramAvatarControllersList") as IList;
            if (pList != null)
            {
                // Try exact keyword match first, then case-insensitive fallback from KwRaw("Params")
                foreach (var kw in KwRaw("Params"))
                {
                    if (string.Equals(kw, paramSpec, StringComparison.OrdinalIgnoreCase) ||
                        kw.IndexOf(paramSpec, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        matchedParam = kw;
                        break;
                    }
                }
                foreach (var pav in pList)
                {
                    bool ok = false;
                    try { ok = Equals(Call(pav, "RecognizeWord", matchedParam), true); } catch { }
                    if (ok) return true;
                }
            }
            // Fallback to PickVoiceFromParams
            Call(r, "PickVoiceFromParams", matchedParam);
            return true;
        }

        static string ExecuteSend(string unit, string group, string order, string param, string dialog, string speedStr)
        {
            var r = Inst("Radio.RadioController");
            var tc = Inst("Radio.TimeController");
            if (r == null) return "{\"ok\":false,\"error\":\"Not in an active mission\"}";

            // 1. Handle Dialog Response if specified
            if (!string.IsNullOrEmpty(dialog))
            {
                var opts = GetDialogOptions(r);
                if (opts.Count == 0)
                    return "{\"ok\":false,\"error\":\"No dialog panel is currently open\"}";

                int dlgIdx = -1;
                if (!int.TryParse(dialog.Trim(), out dlgIdx))
                {
                    for (int i = 0; i < opts.Count; i++)
                    {
                        if (opts[i].IndexOf(dialog.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            dlgIdx = i + 1;
                            break;
                        }
                    }
                }
                if (dlgIdx < 1 || dlgIdx > opts.Count)
                    return "{\"ok\":false,\"error\":\"Invalid dialog option: " + Escape(dialog) + "\",\"dialog_options\":" + Arr(opts) + "}";

                if (!Equals(Get(r, "isOn"), true)) Call(r, "SwitchOn");
                Call(r, "PickFromDialog", dlgIdx);
                Call(r, "SendRadioMessage", true);
                Call(r, "ButtonUnholdOld");
                // Ensure game runs at 1x so dialog audio & reply play out
                SetSpeedInternal(r, tc, 1);

                return "{\"ok\":true,\"action\":\"dialog\",\"selected\":" + Q(opts[dlgIdx - 1]) + ",\"comm_busy\":" + Bool(IsCommBusy(r)) + "}";
            }

            // 2. Handle Radio Unit Order if unit is specified
            if (!string.IsNullOrEmpty(unit))
            {
                PrepareRadioMenu(r);

                string uMatch, gMatch = "", oMatch = "";
                List<string> avail;
                if (!SelectUnit(r, unit.Trim(), out uMatch, out avail))
                {
                    Call(r, "EraseOrders");
                    Call(r, "ButtonUnholdOld");
                    return "{\"ok\":false,\"step\":\"unit\",\"error\":\"Unit not found: " + Escape(unit) + "\",\"available_units\":" + Arr(avail) + "}";
                }

                if (!string.IsNullOrEmpty(group))
                {
                    if (!SelectGroup(r, group.Trim(), out gMatch, out avail))
                    {
                        Call(r, "EraseOrders");
                        Call(r, "ButtonUnholdOld");
                        return "{\"ok\":false,\"step\":\"group\",\"error\":\"Group not found: " + Escape(group) + "\",\"available_groups\":" + Arr(avail) + "}";
                    }
                }

                if (!string.IsNullOrEmpty(order))
                {
                    if (!SelectOrder(r, order.Trim(), out oMatch, out avail))
                    {
                        Call(r, "EraseOrders");
                        Call(r, "ButtonUnholdOld");
                        return "{\"ok\":false,\"step\":\"order\",\"error\":\"Order not found: " + Escape(order) + "\",\"available_orders\":" + Arr(avail) + "}";
                    }
                }

                var matchedParams = new List<string>();
                if (!string.IsNullOrEmpty(param))
                {
                    foreach (var p in param.Split(new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string mp;
                        SelectParam(r, p.Trim(), out mp);
                        matchedParams.Add(mp);
                    }
                }

                Call(r, "SendRadioMessage", true);
                Call(r, "ButtonUnholdOld");
                // Set speed to 1x so Commander voice & unit response can complete
                SetSpeedInternal(r, tc, 1);

                string summary = uMatch + " -> " + gMatch + " -> " + oMatch + (matchedParams.Count > 0 ? (" (" + string.Join(", ", matchedParams.ToArray()) + ")") : "");
                return "{\"ok\":true,\"action\":\"order\",\"sent\":" + Q(summary) + ",\"comm_busy\":" + Bool(IsCommBusy(r)) + "}";
            }

            // 3. Handle Time Speed Change only (speed=0 pause, speed=1 play, speed=2 fast)
            if (!string.IsNullOrEmpty(speedStr))
            {
                int sp = 0;
                string s = speedStr.Trim().ToLower();
                if (s == "pause" || s == "0") sp = 0;
                else if (s == "play" || s == "normal" || s == "1") sp = 1;
                else if (s == "fast" || s == "faster" || s == "2") sp = 2;
                else int.TryParse(s, out sp);

                SetSpeedInternal(r, tc, sp);
                return "{\"ok\":true,\"action\":\"speed\",\"speed\":" + sp + ",\"comm_busy\":" + Bool(IsCommBusy(r)) + "}";
            }

            return "{\"ok\":false,\"error\":\"Missing parameters. Provide unit/group/order, dialog, or speed.\"}";
        }

        // ---------------- HTTP Server ----------------
        void HttpLoop()
        {
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception e)
                    {
                        var inner = e.InnerException ?? e;
                        Logger.LogError("HTTP error: " + inner);
                        try { Reply(ctx, 500, "{\"ok\":false,\"error\":" + Q(inner.GetType().Name + ": " + inner.Message) + "}"); } catch { }
                    }
                });
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            var rq = ctx.Request;
            string path = rq.Url.AbsolutePath.TrimEnd('/').ToLower();

            if (path == "" || path == "/help")
            {
                Reply(ctx, 200, "{\"ok\":true,\"endpoints\":[\"GET /status?limit=5\",\"GET /map\",\"POST|GET /send?unit=alpha&group=...&order=...&param=...\",\"POST|GET /send?dialog=1\",\"POST|GET /send?speed=0|1|2\"]}");
                return;
            }

            if (path == "/map")
            {
                string json = RunOnMain(() => BuildMapJson(), 5000);
                Reply(ctx, 200, json);
                return;
            }

            if (path == "/status" || path == "/state")
            {
                int limit = 5;
                if (rq.QueryString["limit"] != null) int.TryParse(rq.QueryString["limit"], out limit);
                string json = RunOnMain(() => BuildStatusJson(limit), 5000);
                Reply(ctx, 200, json);
                return;
            }

            if (path == "/send" || path == "/say")
            {
                string unit = rq.QueryString["unit"];
                string group = rq.QueryString["group"];
                string order = rq.QueryString["order"];
                string param = rq.QueryString["param"];
                string dialog = rq.QueryString["dialog"];
                string speed = rq.QueryString["speed"];

                if (rq.HasEntityBody)
                {
                    string body;
                    using (var sr = new StreamReader(rq.InputStream, Encoding.UTF8))
                        body = sr.ReadToEnd().Trim();
                    ParseBodyParams(body, ref unit, ref group, ref order, ref param, ref dialog, ref speed);
                }

                string json = RunOnMain(() => ExecuteSend(unit, group, order, param, dialog, speed), 5000);
                Reply(ctx, 200, json);
                return;
            }

            Reply(ctx, 404, "{\"ok\":false,\"error\":\"Not found. Use /status or /send\"}");
        }

        static void ParseBodyParams(string body, ref string unit, ref string group, ref string order, ref string param, ref string dialog, ref string speed)
        {
            if (string.IsNullOrEmpty(body)) return;
            // Support simple key:value tokens or query-style lines
            foreach (var raw in body.Split(new char[] { '\n', '\r', ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().Trim('[', ']', '"', '{', '}');
                if (t.Length == 0) continue;
                if (t.StartsWith("@")) t = t.Substring(1);
                string low = t.ToLower();
                if (low == "pause") { speed = "0"; continue; }
                if (low == "play" || low == "normal") { speed = "1"; continue; }
                if (low == "fast" || low == "faster") { speed = "2"; continue; }

                int sep = t.IndexOfAny(new char[] { ':', '=' });
                if (sep > 0)
                {
                    string k = t.Substring(0, sep).Trim().Trim('"').ToLower();
                    string v = t.Substring(sep + 1).Trim().Trim('"', ',');
                    if (k == "unit") unit = v;
                    else if (k == "group") group = v;
                    else if (k == "order") order = v;
                    else if (k == "param" || k == "int") param = string.IsNullOrEmpty(param) ? v : (param + "," + v);
                    else if (k == "dialog") dialog = v;
                    else if (k == "speed") speed = v;
                }
            }
        }

        static void Reply(HttpListenerContext ctx, int code, string body)
        {
            var b = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = code;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.OutputStream.Close();
        }

        // ---------------- JSON Formatting ----------------
        static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string Q(string s)
        {
            return s == null ? "null" : ("\"" + Escape(s) + "\"");
        }

        static string Arr(IEnumerable<string> items)
        {
            var list = new List<string>();
            if (items != null)
                foreach (var s in items) list.Add(Q(s));
            return "[" + string.Join(",", list.ToArray()) + "]";
        }
    }
}
