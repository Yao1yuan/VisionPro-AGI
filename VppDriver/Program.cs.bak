using Cognex.VisionPro;
using Cognex.VisionPro.QuickBuild;
using Cognex.VisionPro.ToolBlock;
using Cognex.VisionPro.ToolGroup;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web; // 需要引用 System.Web.dll

namespace VppDriver
{
    class Program
    {
        private static readonly Dictionary<string, object> toolCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Type, PropertyInfo[]> typePropertiesCache = new Dictionary<Type, PropertyInfo[]>();
        // 用于深度搜索防止死循环
        private static HashSet<object> _visitedObjects = new HashSet<object>();

        private static object vppObject;
        private static string vppPath;

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 2) { PrintUsage(); return; }

            string mode = args[0].ToLower();
            vppPath = args[1];

            try
            {
                Console.WriteLine($"[System] Loading VPP: {vppPath}...");
                if (!File.Exists(vppPath)) throw new Exception("VPP file not found.");

                vppObject = CogSerializer.LoadObjectFromFile(vppPath);

                // 预建缓存
                Traverse(vppObject, (obj, name) => {
                    if (!toolCache.ContainsKey(name)) toolCache[name] = obj;
                    return false;
                });
                Console.WriteLine($"[System] VPP Loaded. Found {toolCache.Count} tools.");

                if (mode == "server")
                {
                    int port = args.Length > 2 ? int.Parse(args[2]) : 8000;
                    RunServer(port);
                }
                else
                {
                    Console.WriteLine("[Error] Only 'server' mode is supported in this build.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fatal Error] {ex.Message}");
                Environment.Exit(2);
            }
        }

        static void RunServer(int port)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            Console.WriteLine($"\n[SUCCESS] Server is running at http://localhost:{port}/");
            Console.WriteLine("[Tips] Use http://localhost:8000/list_tools to test.");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;
                string responseString = "";

                try
                {
                    string cmd = request.Url.AbsolutePath.Trim('/').ToLower();
                    var query = HttpUtility.ParseQueryString(request.Url.Query);

                    switch (cmd)
                    {
                        case "list_tools":
                            var list = toolCache.Select(k => $"- {k.Key} ({k.Value.GetType().Name})");
                            responseString = string.Join("\n", list);
                            break;

                        case "help":
                            responseString = HandleHelpRequest(query["path"]);
                            break;

                        case "get":
                            responseString = HandleGetSetRequest("get", query["tool"], query["path"], null);
                            break;

                        case "set":
                            responseString = HandleGetSetRequest("set", query["tool"], query["path"], query["value"]);
                            break;

                        case "extract":
                            responseString = TryGetScriptCode(FindToolByName(query["tool"])) ?? "No script found or script object is null.";
                            break;

                        case "inject":
                            // 使用 Base64 避免 URL 字符破坏代码内容
                            string codeInput = query["code"];
                            if (string.IsNullOrEmpty(codeInput))
                            {
                                responseString = "Error: 'code' parameter is required (Base64 encoded).";
                            }
                            else
                            {
                                string code = Encoding.UTF8.GetString(Convert.FromBase64String(codeInput));
                                if (TrySetScriptCode(FindToolByName(query["tool"]), code))
                                {
                                    CogSerializer.SaveObjectToFile(vppObject, vppPath);
                                    responseString = "Injection Successful and Saved.";
                                }
                                else responseString = "Injection Failed. Check server console for details.";
                            }
                            break;

                        case "inspect":
                            // 调试用：查看脚本对象结构
                            responseString = GetScriptStructure(query["tool"]);
                            Console.WriteLine("\n[Inspect Result]\n" + responseString);
                            break;

                        case "find_code":
                            // 调试用：暴力搜索代码位置
                            responseString = DeepSearchForCode(query["tool"]);
                            Console.WriteLine(responseString);
                            break;

                        default:
                            responseString = "Unknown command. Available: list_tools, help, get, set, extract, inject, inspect, find_code";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    responseString = $"[Error] {ex.Message}";
                    Console.WriteLine(responseString);
                }

                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "text/plain; charset=utf-8";
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
        }

        // --- 核心脚本处理逻辑 (已修复支持 Auth/VisionTool) ---

        static string TryGetScriptCode(object host)
        {
            if (host == null) return "Error: Host object is null.";

            try
            {
                // 策略 A: 针对 CogJob
                // 优先路径: CogJob -> VisionTool -> Script -> Auth (根据你的 deep_search 结果)
                if (host is CogJob job)
                {
                    // 1. 尝试从 VisionTool 获取
                    object visionTool = job.VisionTool;
                    if (visionTool != null)
                    {
                        PropertyInfo pScript = visionTool.GetType().GetProperty("Script");
                        if (pScript != null)
                        {
                            object scriptObj = pScript.GetValue(visionTool);
                            if (scriptObj != null)
                            {
                                string code = ReadCodeFromScriptObj(scriptObj);
                                if (code != null) return code;
                            }
                        }
                    }

                    // 2. 尝试从 JobScript 获取 (标准路径兜底)
                    if (job.JobScript != null)
                    {
                        string code = ReadCodeFromScriptObj(job.JobScript);
                        if (code != null) return code;
                    }
                }

                // 策略 B: 针对 CogToolBlock
                else if (host is CogToolBlock toolBlock)
                {
                    if (toolBlock.Script != null)
                    {
                        return ReadCodeFromScriptObj(toolBlock.Script);
                    }
                }
            }
            catch (Exception ex)
            {
                return $"[Exception] Extract failed: {ex.Message}";
            }

            return null;
        }

        static bool TrySetScriptCode(object host, string code)
        {
            if (host == null) return false;

            try
            {
                object scriptObj = null;

                // --- 步骤 1: 定位 Script 对象 ---
                if (host is CogJob job)
                {
                    // CogJob 策略: 优先 VisionTool.Script, 其次 JobScript
                    if (job.VisionTool != null)
                    {
                        PropertyInfo pScript = job.VisionTool.GetType().GetProperty("Script");
                        if (pScript != null) scriptObj = pScript.GetValue(job.VisionTool);
                    }
                    if (scriptObj == null && job.JobScript != null) scriptObj = job.JobScript;
                }
                else if (host is CogToolBlock toolBlock)
                {
                    // ToolBlock 策略: 直接取 Script
                    scriptObj = toolBlock.Script;
                }

                if (scriptObj == null)
                {
                    Console.WriteLine("[Error] Target object has no Script object.");
                    return false;
                }

                // --- 步骤 2: 写入代码 (遍历白名单) ---
                bool written = false;
                Type t = scriptObj.GetType();

                // 优先级列表与读取保持一致
                string[] candidates = { "UserSource", "Auth", "Text", "SourceCode" };

                foreach (var propName in candidates)
                {
                    PropertyInfo p = t.GetProperty(propName);
                    if (p != null && p.CanWrite)
                    {
                        p.SetValue(scriptObj, code);
                        Console.WriteLine($"[Success] Code injected into property: '{propName}'");
                        written = true;
                        break; // 只要成功写入一个属性，通常就够了
                    }
                }

                if (!written)
                {
                    Console.WriteLine($"[Error] Could not find any writable property among: {string.Join(", ", candidates)}");
                    return false;
                }

                // --- 步骤 3: 编译 (兼容 Compile / Build) ---
                MethodInfo mCompile = t.GetMethod("Compile", Type.EmptyTypes) ?? t.GetMethod("Build", Type.EmptyTypes);

                if (mCompile != null)
                {
                    mCompile.Invoke(scriptObj, null);
                    Console.WriteLine($"[Success] Script compiled using method: '{mCompile.Name}()'");
                }
                else
                {
                    Console.WriteLine("[Warning] Code injected but no 'Compile' or 'Build' method found. Save VPP to apply.");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Exception] Injection failed: {ex.Message}");
                return false;
            }
        }

        // 辅助：读取代码文本
        // 辅助：通用读取代码逻辑 (万能版)
        static string ReadCodeFromScriptObj(object scriptObj)
        {
            if (scriptObj == null) return null;
            Type t = scriptObj.GetType();

            // 优先级列表：UserSource (ToolBlock常见) -> Auth (Job常见) -> Text (标准) -> SourceCode (旧版)
            string[] candidates = { "UserSource", "Auth", "Text", "SourceCode" };

            foreach (var propName in candidates)
            {
                PropertyInfo p = t.GetProperty(propName);
                if (p != null && p.CanRead)
                {
                    string val = p.GetValue(scriptObj) as string;
                    // 只有当属性存在且内容不为空时，才算成功
                    if (!string.IsNullOrEmpty(val))
                    {
                        // Console.WriteLine($"[Debug] Read code from '{propName}'"); // 调试用
                        return val;
                    }
                }
            }

            return null; // 所有属性都试过了，全是空的
        }

        // --- 调试与搜索逻辑 ---

        static string DeepSearchForCode(string toolName)
        {
            object root = FindToolByName(toolName);
            if (root == null) return "Tool not found.";

            _visitedObjects.Clear();
            StringBuilder resultLog = new StringBuilder();
            resultLog.AppendLine($"[Start Search] Root: {toolName} ({root.GetType().Name})");

            var found = RecursiveSearch(root, toolName, 0, 6);

            if (found.HasValue)
            {
                return $"[FOUND!]\nPath: {found.Value.Path}\n\n[Preview Start]:\n{found.Value.Content.Substring(0, Math.Min(200, found.Value.Content.Length))}...";
            }
            else
            {
                return $"[NOT FOUND] Checked 6 levels deep.";
            }
        }

        static (string Path, string Content)? RecursiveSearch(object obj, string currentPath, int depth, int maxDepth)
        {
            if (obj == null || depth > maxDepth) return null;
            if (_visitedObjects.Contains(obj)) return null;
            _visitedObjects.Add(obj);

            Type type = obj.GetType();

            // 检查是不是代码字符串
            if (obj is string strVal)
            {
                if (strVal.Length > 50 && (strVal.Contains("using System") || strVal.Contains("public class")))
                    return (currentPath, strVal);
                return null;
            }

            if (type.IsPrimitive || type.IsEnum || (type.Namespace != null && type.Namespace.StartsWith("System") && type != typeof(object)))
                return null;

            PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var p in props)
            {
                if (p.GetIndexParameters().Length > 0 || p.Name == "Parent" || p.Name == "TopLevelControl") continue;

                try
                {
                    object val = p.GetValue(obj);
                    if (val == null) continue;

                    string nextPath = $"{currentPath}.{p.Name}";

                    // 快速检查字符串属性
                    if (val is string s)
                    {
                        if (s.Length > 50 && (s.Contains("using System") || s.Contains("public class")))
                            return (nextPath, s);
                    }
                    else
                    {
                        var result = RecursiveSearch(val, nextPath, depth + 1, maxDepth);
                        if (result != null) return result;
                    }
                }
                catch { }
            }
            return null;
        }

        static string GetScriptStructure(string toolName)
        {
            object host = FindToolByName(toolName);
            if (host == null) return $"Error: Tool '{toolName}' not found.";

            StringBuilder sb = new StringBuilder();
            object scriptObj = null;

            // 尝试获取各种 Script 对象
            if (host is CogToolBlock tb) scriptObj = tb.Script;
            else if (host is CogJob job)
            {
                if (job.VisionTool != null)
                {
                    var p = job.VisionTool.GetType().GetProperty("Script");
                    if (p != null) scriptObj = p.GetValue(job.VisionTool);
                }
                if (scriptObj == null) scriptObj = job.JobScript;
            }

            if (scriptObj == null) return "Error: Script object is NULL.";

            Type t = scriptObj.GetType();
            sb.AppendLine($"[Script Type] {t.FullName}");
            sb.AppendLine("--- PROPERTIES ---");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                sb.AppendLine($"{p.Name,-20} | {p.PropertyType.Name}");
            }
            sb.AppendLine("--- METHODS ---");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.DeclaringType != typeof(object) && !m.IsSpecialName)
                    sb.AppendLine($"{m.Name,-20} | Returns: {m.ReturnType.Name}");
            }

            return sb.ToString();
        }

        // --- 通用属性处理逻辑 (原逻辑) ---

        static string HandleHelpRequest(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return "Error: Missing 'path' parameter.";
            string toolName = fullPath.Split('.')[0];
            object tool = FindToolByName(toolName);
            if (tool == null) return $"Error: Tool '{toolName}' not found.";

            string propPath = fullPath.Contains(".") ? fullPath.Substring(toolName.Length + 1) : "";
            if (!TryResolveProperty(tool, propPath, out object targetObj, out PropertyInfo targetProp))
                return $"Error: Cannot resolve path '{propPath}'";

            object finalObj = (targetProp != null) ? targetProp.GetValue(targetObj) : targetObj;
            if (finalObj == null) return "Target object is null.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[Help] Target: {fullPath} ({finalObj.GetType().Name})");
            sb.AppendLine(new string('-', 80));
            sb.AppendLine($"{"Property Path",-45} | {"Type",-15} | {"Access"} | {"Detail"}");
            sb.AppendLine(new string('-', 80));

            AppendObjectStructure(sb, finalObj, finalObj.GetType(), "", 0, 2);
            return sb.ToString();
        }

        static string HandleGetSetRequest(string mode, string toolName, string path, string val)
        {
            object tool = FindToolByName(toolName);
            if (tool == null) return "Error: Tool not found.";
            if (!TryResolveProperty(tool, path, out object parent, out PropertyInfo prop)) return "Error: Path not found.";

            if (mode == "get")
            {
                object result = (prop != null) ? prop.GetValue(parent) : parent;
                return result?.ToString() ?? "null";
            }
            else // set
            {
                if (prop == null || !prop.CanWrite) return "Error: Property is read-only or not found.";
                object safeVal = prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, val, true) : Convert.ChangeType(val, prop.PropertyType);
                prop.SetValue(parent, safeVal);
                CogSerializer.SaveObjectToFile(vppObject, vppPath);
                return $"Success: Set to {safeVal}";
            }
        }

        static void AppendObjectStructure(StringBuilder sb, object instance, Type type, string prefix, int currentDepth, int maxDepth)
        {
            if (currentDepth > maxDepth) return;
            var props = GetCachedProperties(type);

            foreach (var p in props)
            {
                string pName = p.Name;
                if (pName.EndsWith("Changed") || pName.Contains("StateFlags") || pName == "Tag") continue;

                string fullPath = string.IsNullOrEmpty(prefix) ? pName : $"{prefix}.{pName}";
                string access = (p.CanRead ? "R" : "") + (p.CanWrite ? "W" : "");
                string typeName = p.PropertyType.Name;
                string detail = "";

                bool isBasic = p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string);
                if (isBasic && instance != null && p.CanRead)
                {
                    try { detail = $"= {p.GetValue(instance)}"; } catch { }
                }

                sb.AppendLine($"{fullPath,-45} | {typeName,-15} | {access,-6} | {detail}");

                if (!isBasic && currentDepth < maxDepth)
                {
                    if (typeName.EndsWith("Params") || pName == "RunParams" || pName == "Pattern")
                    {
                        object sub = (instance != null && p.CanRead) ? p.GetValue(instance) : null;
                        AppendObjectStructure(sb, sub, p.PropertyType, fullPath, currentDepth + 1, maxDepth);
                    }
                }
            }
        }

        static bool TryResolveProperty(object root, string path, out object targetObj, out PropertyInfo targetProp)
        {
            targetObj = root; targetProp = null;
            if (string.IsNullOrEmpty(path)) return true;

            string[] parts = path.Split('.');
            object current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (current == null) return false;

                if (part.Contains("[") && part.EndsWith("]"))
                {
                    int open = part.IndexOf("[");
                    string name = part.Substring(0, open);
                    int idx = int.Parse(part.Substring(open + 1, part.Length - open - 2));

                    PropertyInfo pColl = current.GetType().GetProperty(name);
                    if (pColl == null) return false;
                    object coll = pColl.GetValue(current);
                    if (coll is IList list && idx < list.Count) current = list[idx];
                    else return false;
                }
                else
                {
                    PropertyInfo p = current.GetType().GetProperty(part);
                    if (p == null) return false;
                    if (i == parts.Length - 1) { targetObj = current; targetProp = p; return true; }
                    current = p.GetValue(current);
                }
            }
            targetObj = current;
            return true;
        }

        private static PropertyInfo[] GetCachedProperties(Type type)
        {
            if (!typePropertiesCache.TryGetValue
                (type, out var props))
            {
                props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                typePropertiesCache[type] = props;
            }
            return props;
        }

        static object FindToolByName(string name) => (name != null && toolCache.TryGetValue(name, out object t)) ? t : null;

        static bool Traverse(object obj, Func<object, string, bool> action)
        {
            if (obj == null) return false;
            string name = "Unnamed";
            try
            {
                if (obj is ICogTool ct) name = ct.Name;
                else if (obj is CogJob cj) name = cj.Name;
                else if (obj is CogJobManager) name = "JobManager";
                else
                {
                    PropertyInfo pName = obj.GetType().GetProperty("Name");
                    if (pName != null) name = pName.GetValue(obj) as string ?? "Unnamed";
                }
            }
            catch { }

            if (action(obj, name)) return true;

            if (obj is CogJobManager manager)
            {
                for (int i = 0; i < manager.JobCount; i++)
                    if (Traverse(manager.Job(i), action)) return true;
            }
            else if (obj is CogJob job)
            {
                if (job.VisionTool != null)
                    if (Traverse(job.VisionTool, action)) return true;
            }
            else if (obj is CogToolGroup group)
            {
                if (group.Tools != null)
                    foreach (ICogTool tool in group.Tools)
                        if (Traverse(tool, action)) return true;
            }
            else if (obj is IEnumerable enumerable && !(obj is string))
            {
                foreach (var item in enumerable)
                    if ((item is ICogTool || item is CogJob) && Traverse(item, action)) return true;
            }

            return false;
        }

        static void PrintUsage()
        {
            Console.WriteLine("VppDriver v8.0 Server Mode (Enhanced)");
            Console.WriteLine("Usage: VppDriver.exe server <vpp_path> [port]");
        }
    }
}