using Cognex.VisionPro;
using Cognex.VisionPro.QuickBuild;
using Cognex.VisionPro.ToolBlock;
using Cognex.VisionPro.ToolGroup;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace VppDriverMcp
{
    // MCP JSON-RPC 请求结构
    public class JsonRpcRequest
    {
        public string jsonrpc { get; set; }
        public object id { get; set; }
        public string method { get; set; }
        public JToken @params { get; set; }
    }

    class Program
    {
        // --- 全局状态 ---
        private static readonly Dictionary<string, object> toolCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Type, PropertyInfo[]> typePropertiesCache = new Dictionary<Type, PropertyInfo[]>();
        private static object vppObject;
        private static string vppPath;
        private static TextWriter _claudeChannel;

        [STAThread]
        static void Main(string[] args)
        {
            // 1. 接管 Stdout
            _claudeChannel = Console.Out;
            // 2. 屏蔽 VisionPro 日志
            Console.SetOut(Console.Error);

            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);

            try
            {
                if (args.Length > 0 && File.Exists(args[0]))
                    LoadVppFile(args[0]);

                RunMcpLoop();
            }
            catch (Exception ex)
            {
                Log($"[FATAL CRASH] {ex}");
            }
        }

        static void RunMcpLoop()
        {
            while (true)
            {
                string line = Console.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var request = JsonConvert.DeserializeObject<JsonRpcRequest>(line);
                    if (request == null) continue;

                    object result = null;
                    bool isError = false;
                    string errorMsg = "";

                    try
                    {
                        if (request.method == "initialize")
                        {
                            result = new
                            {
                                protocolVersion = "2024-11-05",
                                capabilities = new { tools = new { listChanged = true } },
                                serverInfo = new { name = "visionpro-vpp-driver", version = "9.2.0" }
                            };
                        }
                        else if (request.method == "tools/list") result = new { tools = GetMcpTools() };
                        else if (request.method == "tools/call") result = HandleToolCall(request.@params);
                        else if (request.method == "ping") result = new { };
                    }
                    catch (Exception ex)
                    {
                        isError = true;
                        errorMsg = ex.Message;
                    }

                    if (request.id != null)
                    {
                        string jsonRes;
                        if (isError)
                            jsonRes = JsonConvert.SerializeObject(new { jsonrpc = "2.0", id = request.id, error = new { code = -32603, message = errorMsg } }, Formatting.None);
                        else
                            jsonRes = JsonConvert.SerializeObject(new { jsonrpc = "2.0", id = request.id, result = result }, Formatting.None);

                        _claudeChannel.WriteLine(jsonRes);
                        _claudeChannel.Flush();
                    }
                }
                catch (Exception ex) { Log($"[Loop Error] {ex.Message}"); }
            }
        }

        static List<object> GetMcpTools()
        {
            return new List<object>
            {
                new { name = "vpp_load_file", description = "Load VPP file.", inputSchema = new { type = "object", properties = new { file_path = new { type = "string" } }, required = new[] { "file_path" } } },
                new { name = "vpp_list_tools", description = "List all tools.", inputSchema = new { type = "object", properties = new { } } },
                new { name = "vpp_get_property", description = "Get value or inspect object structure (use path='.' for root).", inputSchema = new { type = "object", properties = new { tool_name = new { type = "string" }, path = new { type = "string" } }, required = new[] { "tool_name", "path" } } },
                new { name = "vpp_set_property", description = "Set value.", inputSchema = new { type = "object", properties = new { tool_name = new { type = "string" }, path = new { type = "string" }, value = new { type = "string" } }, required = new[] { "tool_name", "path", "value" } } },
                new { name = "vpp_extract_script", description = "Extract C# script.", inputSchema = new { type = "object", properties = new { tool_name = new { type = "string" } }, required = new[] { "tool_name" } } },
                new { name = "vpp_inject_script", description = "Inject C# script.", inputSchema = new { type = "object", properties = new { tool_name = new { type = "string" }, code = new { type = "string" } }, required = new[] { "tool_name", "code" } } }
            };
        }

        static object HandleToolCall(JToken paramsToken)
        {
            string name = paramsToken["name"]?.ToString();
            JObject args = paramsToken["arguments"] as JObject;
            string output = "";
            bool isErr = false;

            try
            {
                string tName = args?["tool_name"]?.ToString();
                string path = args?["path"]?.ToString();
                string val = args?["value"]?.ToString();
                string code = args?["code"]?.ToString();

                switch (name)
                {
                    case "vpp_load_file": output = LoadVppFile(args?["file_path"]?.ToString()); break;
                    case "vpp_list_tools": output = toolCache.Count == 0 ? "No tools." : string.Join("\n", toolCache.Select(k => $"- {k.Key} ({k.Value.GetType().Name})")); break;
                    case "vpp_get_property":
                        if (path == ".") path = "";
                        output = HandleGetSetRequest("get", tName, path, null);
                        break;
                    case "vpp_set_property": output = HandleGetSetRequest("set", tName, path, val); break;
                    case "vpp_extract_script": output = TryGetScriptCode(FindToolByName(tName)) ?? "No script."; break;
                    case "vpp_inject_script":
                        if (TrySetScriptCode(FindToolByName(tName), code)) { CogSerializer.SaveObjectToFile(vppObject, vppPath); output = "Success & Saved."; }
                        else { output = "Failed."; isErr = true; }
                        break;
                    default: output = "Unknown tool."; isErr = true; break;
                }
            }
            catch (Exception ex) { output = ex.Message; isErr = true; }
            return new { content = new[] { new { type = "text", text = output } }, isError = isErr };
        }

        // --- 核心逻辑 ---

        static string HandleGetSetRequest(string mode, string toolName, string path, string val)
        {
            object tool = FindToolByName(toolName);
            if (tool == null) return "Error: Tool not found.";

            if (!TryResolveProperty(tool, path, out object targetObj, out PropertyInfo prop))
                return $"Error: Path '{path}' not found.";

            if (mode == "get")
            {
                object result = (prop != null) ? prop.GetValue(targetObj) : targetObj;
                if (result == null) return "null";

                Type t = result.GetType();
                if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
                {
                    return result.ToString();
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"[Structure: {t.Name}]");
                    sb.AppendLine(new string('-', 60));
                    sb.AppendLine($"{"Property Name",-35} | {"Type",-15} | {"Value/Detail"}");
                    sb.AppendLine(new string('-', 60));
                    AppendObjectStructure(sb, result, t, "", 0, 1);
                    return sb.ToString();
                }
            }
            else // set
            {
                if (prop == null || !prop.CanWrite) return "Error: Property is read-only or not a property.";
                try
                {
                    object safeVal = prop.PropertyType.IsEnum ? Enum.Parse(prop.PropertyType, val, true) : Convert.ChangeType(val, prop.PropertyType);
                    prop.SetValue(targetObj, safeVal);
                    CogSerializer.SaveObjectToFile(vppObject, vppPath);
                    return $"Success: Set to {safeVal}";
                }
                catch (Exception ex) { return $"Set Error: {ex.Message}"; }
            }
        }

        // --- 修复后的万能解析逻辑 (支持 IList 和 IEnumerable) ---
        static bool TryResolveProperty(object root, string path, out object targetObj, out PropertyInfo targetProp)
        {
            targetObj = root; targetProp = null;
            if (string.IsNullOrWhiteSpace(path)) return true;

            string[] parts = path.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            object current = root;
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (current == null) return false;

                // 索引逻辑 [0]
                if (part.Contains("[") && part.EndsWith("]"))
                {
                    try
                    {
                        int open = part.IndexOf("[");
                        string name = part.Substring(0, open);
                        int idx = int.Parse(part.Substring(open + 1, part.Length - open - 2));

                        // 1. 先找属性 (忽略大小写)
                        PropertyInfo pColl = current.GetType().GetProperty(name, flags);
                        if (pColl == null) return false;

                        object coll = pColl.GetValue(current);
                        if (coll == null) return false;

                        object foundItem = null;
                        bool itemFound = false;

                        // 2. 尝试 IList (标准列表)
                        if (coll is IList list)
                        {
                            if (idx < list.Count) { foundItem = list[idx]; itemFound = true; }
                        }
                        // 3. 尝试 IEnumerable (兼容 VisionPro 奇怪集合)
                        else if (coll is IEnumerable en)
                        {
                            int count = 0;
                            foreach (var item in en)
                            {
                                if (count == idx) { foundItem = item; itemFound = true; break; }
                                count++;
                            }
                        }

                        if (itemFound)
                        {
                            current = foundItem;
                            if (i == parts.Length - 1)
                            {
                                targetObj = current;
                                targetProp = null; // 是对象本身
                                return true;
                            }
                        }
                        else return false;
                    }
                    catch { return false; }
                }
                else // 普通属性
                {
                    PropertyInfo p = current.GetType().GetProperty(part, flags);
                    if (p == null) return false;

                    if (i == parts.Length - 1)
                    {
                        targetObj = current;
                        targetProp = p;
                        return true;
                    }
                    current = p.GetValue(current);
                }
            }
            targetObj = current;
            return true;
        }

        static void AppendObjectStructure(StringBuilder sb, object instance, Type type, string prefix, int currentDepth, int maxDepth)
        {
            if (currentDepth > maxDepth || instance == null) return;
            var props = GetCachedProperties(type);

            foreach (var p in props)
            {
                string pName = p.Name;
                if (pName.EndsWith("Changed") || pName.Contains("StateFlags") || pName == "Tag") continue;
                if (p.GetIndexParameters().Length > 0) continue;

                string fullPath = string.IsNullOrEmpty(prefix) ? pName : $"{prefix}.{pName}";
                string detail = "";

                try
                {
                    object val = p.GetValue(instance);
                    if (val == null) detail = "null";
                    else if (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)) detail = val.ToString();
                    else if (val is IList list) detail = $"[List Count={list.Count}]";
                    else detail = $"<{p.PropertyType.Name}>";
                }
                catch { detail = "<Error>"; }

                sb.AppendLine($"{fullPath,-35} | {p.PropertyType.Name,-15} | {detail}");

                if (currentDepth < maxDepth)
                {
                    if (pName == "RunParams" || pName == "Pattern" || pName == "Operator" || pName == "Operators")
                    {
                        try
                        {
                            object sub = p.GetValue(instance);
                            AppendObjectStructure(sb, sub, p.PropertyType, fullPath, currentDepth + 1, maxDepth);
                        }
                        catch { }
                    }
                }
            }
        }

        private static PropertyInfo[] GetCachedProperties(Type type)
        {
            if (!typePropertiesCache.TryGetValue(type, out var props))
            {
                props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                typePropertiesCache[type] = props;
            }
            return props;
        }

        static string LoadVppFile(string path)
        {
            if (!File.Exists(path)) return "File not found.";
            try
            {
                vppObject = CogSerializer.LoadObjectFromFile(path);
                vppPath = path;
                toolCache.Clear();
                Traverse(vppObject, (obj, name) => { if (!toolCache.ContainsKey(name)) toolCache[name] = obj; return false; });
                return $"Loaded {Path.GetFileName(path)}. Found {toolCache.Count} tools.";
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        static bool Traverse(object obj, Func<object, string, bool> action)
        {
            if (obj == null) return false;
            string name = "Unnamed";
            try
            {
                if (obj is ICogTool ct) name = ct.Name;
                else if (obj is CogJob cj) name = cj.Name;
                else { var p = obj.GetType().GetProperty("Name"); if (p != null) name = p.GetValue(obj)?.ToString() ?? "Unnamed"; }
            }
            catch { }
            if (action(obj, name)) return true;
            if (obj is CogJobManager m) for (int i = 0; i < m.JobCount; i++) Traverse(m.Job(i), action);
            else if (obj is CogJob j) Traverse(j.VisionTool, action);
            else if (obj is CogToolGroup g && g.Tools != null) foreach (ICogTool t in g.Tools) Traverse(t, action);
            else if (obj is CogToolBlock b && b.Tools != null) foreach (ICogTool t in b.Tools) Traverse(t, action);
            else if (obj is IEnumerable en && !(obj is string)) foreach (var item in en) if (item is ICogTool || item is CogJob) Traverse(item, action);
            return false;
        }

        static string TryGetScriptCode(object host)
        {
            if (host == null) return null;
            object s = (host is CogToolBlock tb) ? tb.Script : (host is CogJob j ? (j.VisionTool?.GetType().GetProperty("Script")?.GetValue(j.VisionTool) ?? j.JobScript) : null);
            if (s == null) return null;
            foreach (var n in new[] { "UserSource", "Auth", "Text", "SourceCode" })
            {
                var p = s.GetType().GetProperty(n);
                if (p != null) { var v = p.GetValue(s) as string; if (!string.IsNullOrEmpty(v)) return v; }
            }
            return null;
        }

        static bool TrySetScriptCode(object host, string code)
        {
            if (host == null) return false;
            object s = (host is CogToolBlock tb) ? tb.Script : (host is CogJob j ? (j.VisionTool?.GetType().GetProperty("Script")?.GetValue(j.VisionTool) ?? j.JobScript) : null);
            if (s == null) return false;
            foreach (var n in new[] { "UserSource", "Auth", "Text", "SourceCode" })
            {
                var p = s.GetType().GetProperty(n);
                if (p != null && p.CanWrite) { p.SetValue(s, code); s.GetType().GetMethod("Compile")?.Invoke(s, null); return true; }
            }
            return false;
        }

        static object FindToolByName(string name) => (name != null && toolCache.TryGetValue(name, out object t)) ? t : null;
        static void Log(string m) => Console.Error.WriteLine(m);
    }
}