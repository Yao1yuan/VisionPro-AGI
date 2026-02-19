import os
from bs4 import BeautifulSoup
from google import genai
from google.genai import types

# 1. 初始化 Gemini 客户端 (请替换为你的 Gemini API Key)
# 强烈建议将 API Key 存入环境变量 GEMINI_API_KEY 中，这样 client = genai.Client() 会自动读取
API_KEY = "AIzaSyAXGcE45rEXN8i2MSfUdaxGGDnLtwbTMDc"
client = genai.Client(api_key=API_KEY)

# 2. 文件夹路径设置
INPUT_DIR = r"C:\VPro\html"              # 存放你解压出来的 VisionPro HTML 文件的目录
OUTPUT_DIR = r"./reference/tools"      # 输出 Agent 专用 Markdown 的目录

os.makedirs(OUTPUT_DIR, exist_ok=True)

# 3. 专为 Gemini 设计的超强 System Prompt
SYSTEM_INSTRUCTION = """
You are a Principal Machine Vision Architect for Cognex VisionPro. 
Your task is to read the raw, messy official documentation of a VisionPro Tool and distill it into a pure, hard-core API cheat sheet for an autonomous AI Agent.

CRITICAL RULES:
1. NO GUI / NO HUMAN ACTIONS: The Agent uses a C# MCP driver. Completely delete any mentions of "Edit Control", "Graphics Tab", "Click", "Drag", "Buttons", or visual colors (like "green box").
2. EXTRACT I/O: Identify the exact C# property names for inputs (e.g., InputImage, Region) and outputs (e.g., Results).
3. EXTRACT RUNPARAMS: List the crucial properties inside `RunParams` that control the algorithm. MUST include exact Enum names (e.g., `CogBlobPolarityConstants.DarkOnLight`).
4. TROUBLESHOOTING: Add a brief section on what parameters to tweak if the tool fails to find a result.
5. FORMAT: Output MUST be clean Markdown. Use headings: 1. Description & Workflow, 2. I/O Interfaces, 3. Key Tuning Parameters, 4. Diagnostics & Troubleshooting.
"""

def process_html_with_gemini(html_file_path):
    filename = os.path.basename(html_file_path)
    tool_name = filename.replace(".htm", "").replace(".html", "")
    
    # 仅处理以 Cog 开头的工具文件
    if not filename.startswith("Cog") or not "Tool" in filename:
        return

    print(f"[{tool_name}] 正在读取与清洗...")

    # 读取并剥离 HTML 标签，提取纯文本
    try:
        with open(html_file_path, 'r', encoding='utf-8', errors='ignore') as f:
            soup = BeautifulSoup(f, 'html.parser')
            raw_text = soup.get_text(separator='\n', strip=True)
    except Exception as e:
        print(f"[{tool_name}] 读取文件失败: {e}")
        return

    print(f"[{tool_name}] 正在调用 Gemini 进行知识提纯...")

    try:
        # 调用 Gemini 模型 (推荐使用 gemini-2.5-flash，速度极快且极度聪明，性价比最高)
        response = client.models.generate_content(
            model='gemini-3-flash-preview',
            contents=f"Please distill the following VisionPro documentation for {tool_name}:\n\n{raw_text}",
            config=types.GenerateContentConfig(
                system_instruction=SYSTEM_INSTRUCTION,
                temperature=0.1, # 低温度，保证 API 名称的绝对精确，拒绝幻觉
            )
        )
        
        md_content = response.text

        # 稍微清理一下大模型可能带上的 ```markdown 标记
        if md_content.startswith("```markdown"):
            md_content = md_content[11:]
        if md_content.endswith("```"):
            md_content = md_content[:-3]
        md_content = md_content.strip()

        # 写入文件
        output_path = os.path.join(OUTPUT_DIR, f"{tool_name}.md")
        with open(output_path, 'w', encoding='utf-8') as f:
            f.write(md_content)
            
        print(f"✅ [成功] 已生成完美小抄: {output_path}")

    except Exception as e:
        print(f"❌ [{tool_name}] 提纯失败: {e}")

# 4. 遍历所有文件并执行
def main():
    if not os.path.exists(INPUT_DIR):
        print(f"错误: 找不到输入目录 {INPUT_DIR}。请先将 HTML 文件放进去。")
        return

    files_to_process = [f for f in os.listdir(INPUT_DIR) if f.endswith((".htm", ".html"))]
    print(f"共找到 {len(files_to_process)} 个 HTML 文件，准备开始提炼...\n")

    for file in files_to_process:
        process_html_with_gemini(os.path.join(INPUT_DIR, file))
        
    print("\n🎉 所有 VisionPro 秘籍已全部生成完毕！快让 Agent 读读看吧！")

if __name__ == "__main__":
    main()