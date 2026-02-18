# VisionPro Automation Suite

This project provides a comprehensive suite for programmatically interacting with Cognex VisionPro `.vpp` files. It is designed as a **Skill for Claude Code**, transforming Claude into an industrial vision expert capable of tuning parameters, debugging logic, and running tests directly through natural language.

## Components Structure

The project is organized to provide both the "Hands" (C# Driver) and the "Brain" (Python Skill & References) for the agent.

```text
.
├── VppDriver/                  # [The Hands] C# Source Code
│   ├── VppDriver.sln           # Visual Studio Solution
│   ├── Program.cs              # Main entry point
│   └── bin/Release/            # Output directory for VppDriver.exe
│
└── claude_skill/               # [The Brain] Skill Package for Claude
    └── visionpro-expert/
        ├── scripts/
        │   └── vpp_controller.py   # Python Bridge (Calls the .exe)
        │
        ├── references/             # <--- ESSENTIAL KNOWLEDGE BASE
        │   ├── common_properties.md # Shortcut map for property paths
        │   └── tuning_heuristics.md # Expert SOPs for troubleshooting
        │
        └── SKILL.md                # System prompt and instructions
```

## Setup & Installation

### 1. Build the Driver (The Hands)
Before using the skill, you must compile the C# backend.

1. Open `VppDriver/VppDriver.sln` in Visual Studio.
2. Build the solution (Release mode recommended).
3. Ensure `VppDriver.exe` is generated in `VppDriver/bin/Release/`.

### 2. Install the Skill (The Brain)
To enable Claude to use these tools, register the skill folder.

1. Locate your Claude Code skills directory (typically `~/.claude/skills` or check your config).
2. Copy the entire `claude_skill/visionpro-expert` folder into that directory.
3. **Configuration**: Open `claude_skill/visionpro-expert/scripts/vpp_controller.py` and ensure the `DRIVER_EXE` path points to the absolute path of the `VppDriver.exe` you built in Step 1.

## Domain Knowledge (References)

This skill comes with built-in reference files in the `references/` directory. You do not need to memorize them, but Claude will use them to make smarter decisions.

- **`common_properties.md`**:
  - Contains a mapping of common names to deep VisionPro property paths (e.g., "Blob Threshold" -> `RunParams.SegmentationParams.HardFixedThreshold`).
  - **Effect**: Allows you to say "Change threshold" instead of typing the full path.

- **`tuning_heuristics.md`**:
  - Contains expert rules for solving vision problems (e.g., "If image is dark and blob count is 0, try lowering threshold by 20%").
  - **Effect**: Enables Claude to act as a Consultant, not just a command executor.

## How to Use

Once installed, interact entirely through the Claude Code CLI.

### Step 1: Start Claude Code
Open your terminal and launch the Claude CLI:
```bash
claude
```

### Step 2: Load the Skill
Tell Claude to load the VisionPro expert skill (if it hasn't auto-loaded):
```plaintext
/skill visionpro-expert
```

### Step 3: Interact with Natural Language

**Example 1: Exploration (Using Reflection)**
> "I have a file named `test.vpp`. Can you inspect `CogBlobTool1`? Use the help tool to list its properties and tell me the current Threshold Mode."

**Example 2: Smart Tuning (Using Heuristics)**
> "The blob tool in `test.vpp` isn't detecting the defect in `fail.bmp`. Please consult your `tuning_heuristics` to analyze the problem. If you need to change the Polarity or Threshold, do so, and verify the fix."

**Example 3: Logic Injection (Using Code Gen)**
> "Extract the script from `MainToolBlock`. I want to add C# logic that sets the result to 'Reject' if the score is below 0.85. Please modify the code and inject it back."

## Troubleshooting

- **"VppDriver.exe not found"**:
  - Check `vpp_controller.py`. The `DRIVER_EXE` variable must be an absolute path or correctly relative to the script execution context.

- **Claude guesses wrong property names**:
  - Remind Claude: "Please check `references/common_properties.md` or use the help tool first."

- **Permission Errors**:
  - Ensure the terminal has Read/Write access to the `.vpp` file and Execute access to the `.exe`.
