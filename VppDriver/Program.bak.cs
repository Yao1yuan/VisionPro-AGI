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
    // 用于反序列化请求的类（仅用于读取，不用于响应）
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
        // 防止递归死循环
        private static HashSet<object> _visitedObjects = new HashSet<object>();

        private static object vppObject;
        private static string vppPath;
        private static TextWriter _realStdout;

        // --- 入口点 ---
        [STAThread]
        static void Main(string[] args)
        {
            // 1. 备份真正的 Stdout，用于发送 JSON-RPC 响应
            var stdout = Console.Out;
            _realStdout = stdout;
            // 2. 将全局 Console.Out 重定向到 Stderr (日志通道)
            // 这样即使 Cognex 或其他库调用了 Console.Write，也不会破坏协议
            Console.SetOut(Console.Error);

            // 1. 强制无 BOM UTF-8，防止乱码
            var utf8NoBom = new UTF8Encoding(false);
            Console.InputEncoding = utf8NoBom;
            Console.OutputEncoding = utf8NoBom;

            try
            {
                // 如果启动参数带了路径，尝试加载
                if (args.Length > 0)
                {
                    string path = args[0];
                    if (File.Exists(path))
                    {
                        LoadVppFile(path);
                    }
                    else
                    {
                        Log($"[Warning] File not found at startup: {path}");
                    }
                }
                else
                {
                    Log("[System] Started without VPP file. Waiting for vpp_load_file command...");
                }

                // 进入 MCP 循环
                RunMcpLoop();
            }
            catch (Exception ex)
            {
                Log($"[FATAL CRASH] {ex}");
            }
        }

        // --- MCP 核心循环 ---
        static void RunMcpLoop()
        {

            // 务必使用忽略 Null 的设置，但我们在响应时会使用匿名对象双重保险
            var jsonSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None
            };

            while (true)
            {
                try
                {
                    string line = Console.ReadLine();
                    if (line == null) break; // 管道关闭
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Log($"[Received] {line}"); // 调试时可开启

                    var request = JsonConvert.DeserializeObject<JsonRpcRequest>(line);
                    if (request == null) continue;

                    object responseResult = null;
                    object responseError = null;

                    try
                    {
                        if (request.method == "initialize")
                        {
                            responseResult = new
                            {
                                protocolVersion = "2024-11-05",
                                capabilities = new { tools = new { } },
                                serverInfo = new { name = "visionpro-vpp-driver", version = "2.1.0" }
                            };
                        }
                        else if (request.method == "notifications/initialized")
                        {
                            // 客户端已初始化，无需操作
                        }
                        else if (request.method == "tools/list")
                        {
                            responseResult = new { tools = GetMcpTools() };
                        }
                        else if (request.method == "tools/call")
                        {
                            responseResult = HandleToolCall(request.@params);
                        }
                        else if (request.method == "ping")
                        {
                            responseResult = new { };
                        }
                        else
                        {
                            // 未知方法，忽略
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Error processing {request.method}] {ex.Message}");
                        responseError = new { code = -32603, message = ex.Message };
                    }

                    // 发送响应 (仅当 id 存在时)
                    if (request.id != null)
                    {


                        // 【关键】使用匿名对象构建响应，确保绝对没有 extra fields
                        if (responseError != null)
                        {
                            var errObj = new
                            {
                                jsonrpc = "2.0",
                                id = request.id,
                                error = responseError
                            };
                            jsonResponse = JsonConvert.SerializeObject(errObj, Formatting.None);

                        }
                        else
                        {
                            // 即使 result 是 null，也必须包含 result 字段
                            var successObj = new
                            {
                                jsonrpc = "2.0",
                                id = request.id,
                                result = responseResult
                            };
                            jsonResponse = JsonConvert.SerializeObject(successObj, Formatting.None);
                        }
                        // 关键：写向真正的 stdout
                        _realStdout.WriteLine(jsonResponse);
                        _realStdout.Flush();

                        Console.WriteLine(jsonResponse);
                        Console.Out.Flush(); // 强制刷新
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Loop Error] {ex.Message}");
                }
            }
        }

        // --- MCP 工具定义 ---
        static List<object> GetMcpTools()
        {
            return new List<object>
            {
                new {
                    name = "vpp_load_file",
                    description = "Load a .vpp file into memory. Call this first.",
                    inputSchema = new {
                        type = "object",
                        properties = new { file_path = new { type = "string" } },
                        required = new [] { "file_path" }
                    }
                },
                new {
                    name = "vpp_list_tools",
                    description = "List all tools and objects in the loaded VPP.",
                    inputSchema = new { type = "object", properties = new { } }
                },
                new {
                    name = "vpp_get_property",
                    description = "Get a property value from a tool (e.g. RunParams.ContrastThreshold).",
                    inputSchema = new {
                        type = "object",
                        properties = new { tool_name = new { type = "string" }, path = new { type = "string" } },
                        required = new [] { "tool_name" }
                    }
                },
                new {
                    name = "vpp_set_property",
                    description = "Set a property value. Handles Enums and basic types automatically.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            tool_name = new { type = "string" },
                            path = new { type = "string" },
                            value = new { type = "string" }
                        },
                        required = new [] { "tool_name", "path", "value" }
                    }
                },
                new {
                    name = "vpp_extract_script",
                    description = "Extract C# script code from a ToolBlock or CogJob.",
                    inputSchema = new {
                        type = "object",
                        properties = new { tool_name = new { type = "string" } },
                        required = new [] { "tool_name" }
                    }
                },
                new {
                    name = "vpp_inject_script",
                    description = "Inject C# code into a tool and recompile it. The VPP file will be auto-saved.",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            tool_name = new { type = "string" },
                            code = new { type = "string", description = "The full C# code content" }
                        },
                        required = new [] { "tool_name", "code" }
                    }
                },
                new {
                    name = "vpp_inspect_object",
                    description = "Deep inspect the C# type structure (Properties/Methods) of a tool's script object.",
                    inputSchema = new {
                        type = "object",
                        properties = new { tool_name = new { type = "string" } },
                        required = new [] { "tool_name" }
                    }
                },
                new {
                    name = "vpp_find_code",
                    description = "Recursively search the entire object tree for hidden scripts or code strings.",
                    inputSchema = new {
                        type = "object",
                        properties = new { tool_name = new { type = "string" } },
                        required = new [] { "tool_name" }
                    }
                }
            };
        }

        static object HandleToolCall(JToken paramsToken)
        {
            string name = paramsToken["name"]?.ToString();
            JObject args = paramsToken["arguments"] as JObject;

            string toolName = args?["tool_name"]?.ToString();
            string path = args?["path"]?.ToString();
            string val = args?["value"]?.ToString();
            string code = args?["code"]?.ToString();
            string filePath = args?["file_path"]?.ToString();

            string outputText = "";
            bool isError = false;

            try
            {
                switch (name)
                {
                    case "vpp_load_file":
                        outputText = LoadVppFile(filePath);
                        break;

                    case "vpp_list_tools":
                        if (toolCache.Count == 0) outputText = "No tools loaded.";
                        else outputText = string.Join("\n", toolCache.Select(k => $"{k.Key} ({k.Value.GetType().Name})"));
                        break;

                    case "vpp_get_property":
                        outputText = HandleGetSetRequest("get", toolName, path, null);
                        break;

                    case "vpp_set_property":
                        outputText = HandleGetSetRequest("set", toolName, path, val);
                        break;

                    case "vpp_extract_script":
                        outputText = TryGetScriptCode(FindToolByName(toolName)) ?? "No script found.";
                        break;

                    case "vpp_inject_script":
                        if (TrySetScriptCode(FindToolByName(toolName), code))
                        {
                            CogSerializer.SaveObjectToFile(vppObject, vppPath);
                            outputText = "Injection Successful and VPP Saved.";
                        }
                        else
                        {
                            outputText = "Injection Failed. Check logs.";
                            isError = true;
                        }
                        break;

                    case "vpp_inspect_object":
                        outputText = GetScriptStructure(toolName);
                        break;

                    case "vpp_find_code":
                        outputText = DeepSearchForCode(toolName);
                        break;

                    default:
                        outputText = $"Unknown tool: {name}";
                        isError = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                isError = true;
                outputText = $"Error executing {name}: {ex.Message}";
                Log($"[Exception] {ex}");
            }

            return new
            {
                content = new[] { new { type = "text", text = outputText } },
                isError = isError
            };
        }

        // --- 核心业务逻辑 (从 Server 版本移植并优化) ---

        static string LoadVppFile(string path)
        {
            if (!File.Exists(path)) return $"File not found: {path}";
            try
            {
                Log($"[System] Loading VPP: {path}");
                vppObject = CogSerializer.LoadObjectFromFile(path);
                vppPath = path;

                toolCache.Clear();
                Traverse(vppObject, (obj, tName) => {
                    if (!toolCache.ContainsKey(tName)) toolCache[tName] = obj;
                    return false;
                });
                Log($"[System] Loaded {toolCache.Count} tools.");
                return $"Successfully loaded {Path.GetFileName(path)}. Found {toolCache.Count} tools.";
            }
            catch (Exception ex)
            {
                Log($"[Load Error] {ex.Message}");
                return $"Failed to load file: {ex.Message}";
            }
        }

        static string HandleGetSetRequest(string mode, string toolName, string path, string val)
        {
            if (vppObject == null) return "Error: No VPP file loaded.";

            object tool = FindToolByName(toolName);
            if (tool == null) return $"Error: Tool '{toolName}' not found.";

            if (string.IsNullOrEmpty(path)) return tool.ToString();

            if (!TryResolveProperty(tool, path, out object parent, out PropertyInfo prop))
                return $"Error: Path '{path}' not found on '{toolName}'.";

            if (mode == "get")
            {
                object result = (prop != null) ? prop.GetValue(parent) : parent;
                return result?.ToString() ?? "null";
            }
            else // set
            {
                if (prop == null || !prop.CanWrite) return "Error: Property is read-only or not found.";
                try
                {
                    object safeVal;
                    if (prop.PropertyType.IsEnum)
                        safeVal = Enum.Parse(prop.PropertyType, val, true);
                    else
                        safeVal = Convert.ChangeType(val, prop.PropertyType);

                    prop.SetValue(parent, safeVal);
                    CogSerializer.SaveObjectToFile(vppObject, vppPath);
                    return $"Success: Set {path} to {safeVal}";
                }
                catch (Exception ex)
                {
                    return $"Error setting value: {ex.Message}";
                }
            }
        }

        // --- 脚本处理逻辑 (最强版本) ---

        static string TryGetScriptCode(object host)
        {
            if (host == null) return "Error: Host object is null.";
            try
            {
                if (host is CogJob job)
                {
                    // 1. 优先尝试 VisionTool.Script
                    if (job.VisionTool != null)
                    {
                        PropertyInfo pScript = job.VisionTool.GetType().GetProperty("Script");
                        if (pScript != null)
                        {
                            object scriptObj = pScript.GetValue(job.VisionTool);
                            if (scriptObj != null)
                            {
                                string code = ReadCodeFromScriptObj(scriptObj);
                                if (code != null) return code;
                            }
                        }
                    }
                    // 2. 尝试 JobScript
                    if (job.JobScript != null)
                    {
                        string code = ReadCodeFromScriptObj(job.JobScript);
                        if (code != null) return code;
                    }
                }
                else if (host is CogToolBlock toolBlock)
                {
                    if (toolBlock.Script != null)
                    {
                        return ReadCodeFromScriptObj(toolBlock.Script);
                    }
                }
            }
            catch (Exception ex) { return $"[Exception] Extract failed: {ex.Message}"; }
            return null;
        }

        static bool TrySetScriptCode(object host, string code)
        {
            if (host == null) return false;
            try
            {
                object scriptObj = null;

                // 1. 定位脚本对象
                if (host is CogJob job)
                {
                    if (job.VisionTool != null)
                    {
                        PropertyInfo pScript = job.VisionTool.GetType().GetProperty("Script");
                        if (pScript != null) scriptObj = pScript.GetValue(job.VisionTool);
                    }
                    if (scriptObj == null && job.JobScript != null) scriptObj = job.JobScript;
                }
                else if (host is CogToolBlock toolBlock)
                {
                    scriptObj = toolBlock.Script;
                }

                if (scriptObj == null)
                {
                    Log("[Error] Target object has no Script object.");
                    return false;
                }

                // 2. 写入代码 (遍历白名单属性)
                Type t = scriptObj.GetType();
                string[] candidates = { "UserSource", "Auth", "Text", "SourceCode" };
                bool written = false;

                foreach (var propName in candidates)
                {
                    PropertyInfo p = t.GetProperty(propName);
                    if (p != null && p.CanWrite)
                    {
                        p.SetValue(scriptObj, code);
                        Log($"[Success] Code injected into property: '{propName}'");
                        written = true;
                        break;
                    }
                }

                if (!written)
                {
                    Log($"[Error] Could not find any writable property among: {string.Join(", ", candidates)}");
                    return false;
                }

                // 3. 编译
                MethodInfo mCompile = t.GetMethod("Compile", Type.EmptyTypes) ?? t.GetMethod("Build", Type.EmptyTypes);
                if (mCompile != null)
                {
                    try
                    {
                        mCompile.Invoke(scriptObj, null);
                        Log($"[Success] Script compiled using method: '{mCompile.Name}()'");
                    }
                    catch (Exception ex)
                    {
                        Log($"[Compile Error] {ex.InnerException?.Message ?? ex.Message}");
                        // 注入成功但编译失败，仍返回 true，因为代码已经写进去了，用户可以在 IDE 里修
                    }
                }
                else
                {
                    Log("[Warning] Code injected but no 'Compile' method found.");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"[Exception] SetScript failed: {ex.Message}");
                return false;
            }
        }

        static string ReadCodeFromScriptObj(object scriptObj)
        {
            if (scriptObj == null) return null;
            Type t = scriptObj.GetType();
            string[] candidates = { "UserSource", "Auth", "Text", "SourceCode" };
            foreach (var propName in candidates)
            {
                PropertyInfo p = t.GetProperty(propName);
                if (p != null && p.CanRead)
                {
                    string val = p.GetValue(scriptObj) as string;
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            return null;
        }

        // --- 深度搜索与结构分析 ---

        static string DeepSearchForCode(string toolName)
        {
            object root = FindToolByName(toolName);
            if (root == null) return "Tool not found.";

            _visitedObjects.Clear();
            var found = RecursiveSearch(root, toolName, 0, 6);

            if (found.HasValue)
            {
                return $"[FOUND CODE]\nLocation: {found.Value.Path}\n\n[Preview]:\n{found.Value.Content.Substring(0, Math.Min(200, found.Value.Content.Length))}...";
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

        // --- 辅助方法 ---

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
                    try
                    {
                        int open = part.IndexOf("[");
                        string name = part.Substring(0, open);
                        int idx = int.Parse(part.Substring(open + 1, part.Length - open - 2));

                        PropertyInfo pColl = current.GetType().GetProperty(name);
                        if (pColl == null) return false;
                        object coll = pColl.GetValue(current);

                        if (coll is IList list && idx < list.Count)
                            current = list[idx];
                        else
                            return false;
                    }
                    catch { return false; }
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
                ausing Cognex.VisionPro;
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
        class Program
        {
            // --- 全局状态 ---
            private static readonly Dictionary<string, object> toolCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            private static object vppObject;
            private static string vppPath;

            // --- 日志与通信 ---
            // 日志文件强制写在桌面，方便调试
            private static string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "vpp_mcp_debug.log");
            private static StreamWriter _claudeChannel;

            [STAThread] // VisionPro 必须
            static void Main(string[] args)
            {
                try
                {
                    // 初始化日志
                    File.WriteAllText(logFilePath, $"[Start] PID: {System.Diagnostics.Process.GetCurrentProcess().Id}\n");

                    // 1. 强制无 BOM UTF-8
                    Console.InputEncoding = new UTF8Encoding(false);
                    Console.OutputEncoding = new UTF8Encoding(false);

                    // 2. 建立给 Claude 的专用通道 (Stdout)
                    var originalOut = Console.OpenStandardOutput();
                    _claudeChannel = new StreamWriter(originalOut, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                    // 3. 把所有杂乱日志重定向到 Stderr (Claude 看到会忽略，不会报错)
                    Console.SetOut(Console.Error);

                    Log("[System] Initialized. Waiting for Claude...");

                    // 如果启动参数带了路径，记录一下，但不立即加载（防止超时）
                    if (args.Length > 0)
                    {
                        Log($"[Info] Pending file path: {args[0]}");
                    }

                    RunMcpLoop();
                }
                catch (Exception ex)
                {
                    Log($"[FATAL] {ex}");
                }
            }

            static void RunMcpLoop()
            {
                var input = Console.OpenStandardInput();
                var reader = new StreamReader(input, new UTF8Encoding(false));

                while (true)
                {
                    try
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        Log($"[Received] {line}");

                        var request = JsonConvert.DeserializeObject<JObject>(line);
                        JObject response = new JObject();
                        response["jsonrpc"] = "2.0";
                        response["id"] = request["id"];

                        string method = request["method"]?.ToString();

                        // --- 处理请求 ---
                        if (method == "initialize")
                        {
                            response["result"] = JObject.FromObject(new
                            {
                                protocolVersion = "2024-11-05",
                                capabilities = new { tools = new { } },
                                serverInfo = new { name = "visionpro-driver", version = "2.1.0" }
                            });
                        }
                        else if (method == "tools/list")
                        {
                            // 【关键】这里返回真正的工具定义！
                            response["result"] = new JObject { ["tools"] = JToken.FromObject(GetMcpTools()) };
                        }
                        else if (method == "tools/call")
                        {
                            // 执行具体逻辑
                            var result = HandleToolCall(request["params"]);
                            response["result"] = JToken.FromObject(result);
                        }
                        else if (method == "ping")
                        {
                            response["result"] = new JObject();
                        }
                        else
                        {
                            if (request["id"] == null) continue; // 通知不回
                            response["result"] = new JObject();
                        }

                        // --- 发送响应 ---
                        if (request["id"] != null)
                        {
                            string jsonString = response.ToString(Formatting.None);
                            lock (_claudeChannel)
                            {
                                _claudeChannel.WriteLine(jsonString);
                                _claudeChannel.Flush();
                            }
                            Log($"[Sent] {jsonString}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Loop Error] {ex.Message}");
                    }
                }
            }

            // --- MCP 工具定义 (Claude 会读取这里) ---
            static List<object> GetMcpTools()
            {
                return new List<object>
            {
                new {
                    name = "vpp_load_file",
                    description = "Load a VisionPro .vpp file. You MUST call this tool first before accessing other tools.",
                    inputSchema = new {
                        type = "object",
                        properties = new { file_path = new { type = "string", description = "The absolute path to the .vpp file" } },
                        required = new [] { "file_path" }
                    }
                },
                new {
                    name = "vpp_list_tools",
                    description = "List all VisionPro tools contained in the currently loaded VPP file.",
                    inputSchema = new { type = "object", properties = new { } }
                },
                new {
                    name = "vpp_get_property",
                    description = "Get a specific property value from a tool (e.g. RunParams.ContrastThreshold).",
                    inputSchema = new {
                        type = "object",
                        properties = new {
                            tool_name = new { type = "string", description = "The name of the tool (e.g. CogBlobTool1)" },
                            path = new { type = "string", description = "Property path (e.g. RunParams.ContrastThreshold)" }
                        },
                        required = new [] { "tool_name" }
                    }
                }
            };
            }

            static object HandleToolCall(JToken paramsToken)
            {
                string name = paramsToken["name"]?.ToString();
                JObject args = paramsToken["arguments"] as JObject;
                string outputText = "";
                bool isError = false;

                try
                {
                    if (name == "vpp_load_file")
                    {
                        string path = args?["file_path"]?.ToString();
                        outputText = LoadVppFile(path);
                    }
                    else if (name == "vpp_list_tools")
                    {
                        if (toolCache.Count == 0) outputText = "No tools loaded. Please call 'vpp_load_file' first.";
                        else outputText = string.Join("\n", toolCache.Keys.Select(k => $"{k} ({toolCache[k].GetType().Name})"));
                    }
                    else if (name == "vpp_get_property")
                    {
                        string tName = args?["tool_name"]?.ToString();
                        string path = args?["path"]?.ToString();
                        outputText = HandleGetProperty(tName, path);
                    }
                    else
                    {
                        outputText = $"Unknown tool: {name}";
                        isError = true;
                    }
                }
                catch (Exception ex)
                {
                    isError = true;
                    outputText = $"Error: {ex.Message}";
                    Log($"[Tool Error] {ex}");
                }

                return new { content = new[] { new { type = "text", text = outputText } }, isError = isError };
            }

            // --- 业务逻辑 ---

            static string LoadVppFile(string path)
            {
                if (!File.Exists(path)) return $"File not found: {path}";
                try
                {
                    Log($"Loading VPP: {path}...");
                    vppObject = CogSerializer.LoadObjectFromFile(path);
                    vppPath = path;

                    toolCache.Clear();
                    Traverse(vppObject, (obj, tName) => {
                        if (!toolCache.ContainsKey(tName)) toolCache[tName] = obj;
                        return false;
                    });

                    string msg = $"Success. Loaded {toolCache.Count} tools from {path}.";
                    Log(msg);
                    return msg;
                }
                catch (Exception ex)
                {
                    return $"Failed to load: {ex.Message}";
                }
            }

            static string HandleGetProperty(string toolName, string path)
            {
                if (!toolCache.TryGetValue(toolName, out object tool)) return "Tool not found.";
                if (string.IsNullOrEmpty(path)) return tool.ToString();

                if (TryResolveProperty(tool, path, out object target, out PropertyInfo prop))
                {
                    object val = prop != null ? prop.GetValue(target) : target;
                    return val?.ToString() ?? "null";
                }
                return "Property path not found.";
            }

            static bool Traverse(object obj, Func<object, string, bool> action)
            {
                if (obj == null) return false;
                string name = "Unnamed";
                try
                {
                    if (obj is ICogTool ct) name = ct.Name;
                    else if (obj is CogJob cj) name = cj.Name;
                }
                catch { }

                if (action(obj, name)) return true;

                if (obj is CogJobManager manager)
                {
                    for (int i = 0; i < manager.JobCount; i++) Traverse(manager.Job(i), action);
                }
                else if (obj is CogJob job)
                {
                    if (job.VisionTool != null) Traverse(job.VisionTool, action);
                }
                else if (obj is CogToolGroup group)
                {
                    if (group.Tools != null)
                        foreach (ICogTool tool in group.Tools) Traverse(tool, action);
                }
                // 增加 ToolBlock 支持
                else if (obj is CogToolBlock block)
                {
                    if (block.Tools != null)
                        foreach (ICogTool tool in block.Tools) Traverse(tool, action);
                }
                return false;
            }

            static bool TryResolveProperty(object root, string path, out object targetObj, out PropertyInfo targetProp)
            {
                targetObj = root; targetProp = null;
                string[] parts = path.Split('.');
                object current = root;

                foreach (var part in parts)
                {
                    if (current == null) return false;
                    PropertyInfo p = current.GetType().GetProperty(part);
                    if (p == null) return false;
                    targetObj = current; targetProp = p;
                    current = p.GetValue(current);
                }
                return true;
            }

            static void Log(string msg)
            {
                string time = DateTime.Now.ToString("HH:mm:ss.fff");
                string fullMsg = $"{time} {msg}";
                Console.WriteLine(fullMsg); // 写 Stderr
                try { File.AppendAllText(logFilePath, fullMsg + "\n"); } catch { } // 写文件
            }
        }
    }
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

        static void Log(string message)
{
    // 日志只写到 Stderr，绝不写到 Stdout，否则会破坏 MCP 协议
    Console.Error.WriteLine(message);
}
    }
}