# KlydisBeta

**A high-performance, self-contained LLM inference engine & chat platform for Windows.**

[![Official Website](https://img.shields.io/badge/Website-klydis.co-00F5A0?style=for-the-badge&logo=globe&logoColor=0D0D0D)](https://klydis.co)
[![Documentation](https://img.shields.io/badge/Docs-docs.klydis.com-00E6A8?style=for-the-badge&logo=gitbook&logoColor=0D0D0D)](https://docs.klydis.com)
[![Framework](https://img.shields.io/badge/.NET-10.0-7015E6?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-0078D4?style=for-the-badge&logo=windows&logoColor=white)](https://microsoft.com/windows)
[![CUDA](https://img.shields.io/badge/GPU_Acceleration-CUDA_12-76B900?style=for-the-badge&logo=nvidia&logoColor=white)](https://nvidia.com)

---

## 🚀 Overview

**KlydisBeta** is a modern, dark-themed WPF desktop application engineered for zero-latency, private Large Language Model (LLM) execution directly on local Windows hardware. Powered by **.NET 10** and a vendored fork of **LLamaSharp** (see `patches/README.md`), Klydis operates with an **in-process inference engine**, eliminating the overhead, network leaks, and IPC latency of external local servers (like Ollama or LM Studio).

Klydis is part of the [klydis.co](https://klydis.co) ecosystem, combining deep hardware awareness with advanced RAG, agentic skill orchestration, and non-blocking asynchronous VRAM lifecycle management.

---

## ✨ Features at a Glance

- ⚡ **In-Process Inference Engine**: Directly loads and executes `.gguf` format models inside the application process using LLamaSharp.
- 🏎️ **Hardware Acceleration**: Native support for NVIDIA CUDA 12, AVX2, and AVX-512 vector instruction sets for maximum generation throughput.
- 🔄 **Async Non-Blocking VRAM Management**: Off-thread native memory disposal (`INativeResourceDisposer`) preventing UI freezes during rapid model switching.
- 📊 **Real-Time Hardware & VRAM Telemetry**: Embedded profiler tracking live CPU core usage, System RAM, NVIDIA VRAM allocation, and real-time generation speed (*tokens/sec*).
- 📚 **Local RAG (Retrieval-Augmented Generation)**: Index local documents (PDF, TXT, Markdown, Code) into vector stores with SQLite persistence for context-aware querying.
- 🛠️ **Agentic Skill Orchestration**: Create, manage, and execute custom prompt skills, system personas, and automated workflow routines.
- 🎨 **Mint & Dark Modern UI**: Built with custom XAML design system featuring Obsidian, Midnight, and Ocean backgrounds paired with Forest Mint, Fluorescent Cyan, and Amber accent themes.
- 💾 **Prefix-Cached Multi-Turn Chat**: Uses `InteractiveExecutor` with native KV-cache prefix reuse (exact + partial), fast in-place context resets (`llama_kv_cache_seq_rm`), and SQLite persistence to keep multi-turn conversations fast without cache corruption.

---

## 🌐 Ecosystem & Resources

Explore official resources and documentation from [klydis.co](https://klydis.co):

- 🏠 **Official Website**: [https://klydis.co](https://klydis.co)
- 📖 **Documentation Portal**: [https://docs.klydis.com](https://docs.klydis.com)
- 💡 **Prompt Engineering Guide**: [https://docs.klydis.com/en/docs/build-with-klydis/prompt-engineering/overview](https://docs.klydis.com/en/docs/build-with-klydis/prompt-engineering/overview)
- 🛠️ **Support & Help Center**: [https://support.klydis.com](https://support.klydis.com)

---

## 🛠️ Onboarding & Setup Guide

### Prerequisites
- **Operating System**: Windows 10 / 11 (64-bit)
- **SDK**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher
- **GPU Acceleration (Optional)**: NVIDIA GPU with CUDA 12 drivers installed. *(Automatic CPU fallback available via AVX2/AVX-512)*.

---

### 📥 Installation & Running

#### Method 1: Using the Launcher Script (Recommended)
Simply double-click or execute the included launcher script from PowerShell / CMD:
```cmd
.\Start-Klydis.bat
```
*This launches Klydis with development environment settings and diagnostic logging enabled.*

#### Method 2: Command Line (.NET CLI)
```bash
# 1. Clone the repository
git clone https://github.com/obsidian-pixel-backup/klydisbeta.git
cd klydisbeta

# 2. Build the solution
dotnet build

# 3. Launch the application
dotnet run --project src/Klydis.App/Klydis.App.csproj
```

#### Method 3: Visual Studio 2022 / Rider
1. Open `KlydisBeta.sln` in Visual Studio 2022 (v17.12+) or JetBrains Rider.
2. Ensure `Klydis.App` is set as the Startup Project.
3. Press `F5` to build and run.

---

## 🧠 Model Onboarding & Management

Klydis supports any GGUF quantized model (e.g., Llama 3, Mistral 7B, Phi-3, Qwen 2.5, DeepSeek-R1 distills).

### Step-by-Step Model Setup:
1. **Download a GGUF Model**: Download `.gguf` files from [Hugging Face](https://huggingface.co/models?search=gguf).
2. **Place Model Files**: Save your downloaded `.gguf` models into `.klydis/models` inside your user directory or the project directory.
3. **Select Model in App**:
   - Open **KlydisBeta**.
   - Navigate to the **Model Library** tab in the sidebar.
   - Click **Refresh** to discover new GGUF models.
   - Click **Load Model** on your desired model card.
4. **Configure Hardware Offloading**:
   - Adjust GPU VRAM layer offload sliders in **Settings** based on your GPU's VRAM capacity.
   - Monitor real-time VRAM allocation in the **System Profiler** status bar.

---

## 🏗️ Solution Architecture

KlydisBeta is structured into modular layers designed for high reliability and clean separation of concerns:

```
KlydisBeta/
├── src/
│   ├── Klydis.Core/             # Core Engine & Infrastructure Layer
│   │   ├── Inference/           # LLamaSharp wrapper, ModelPool & async disposal
│   │   ├── RAG/                 # Vector embeddings, SQLite store & document indexer
│   │   ├── Hardware/            # Windows WMI & NVIDIA NVML GPU telemetry
│   │   ├── Skills/              # Prompt skill packs & workflow execution
│   │   ├── Chat/                # Conversation state & session stores
│   │   └── Memory/              # SQLite database context orchestrator
│   │
│   └── Klydis.App/              # WPF UI Presentation Layer (MVVM)
│       ├── Views/               # ChatView, ModelLibraryView, RagView, SystemMonitorView
│       ├── ViewModels/          # ChatViewModel, ModelLibraryViewModel, MainViewModel
│       ├── Themes/              # Dark mode XAML themes (Obsidian, Midnight, Forest Mint)
│       └── Helpers/             # Converters & UI thread dispatchers
│
└── tests/
    └── Klydis.Core.Tests/       # 440+ unit & empirical stress tests
```

---

## 🎹 Keyboard Shortcuts

| Shortcut | Action |
| --- | --- |
| `Enter` / `Ctrl + Enter` | Send Chat Message |
| `Esc` | Stop Active Generation |
| `Ctrl + Shift + U` | Open Model Context Window |
| `Ctrl + R` | Refresh Model Library |
| `Tab` | Switch Active Sidebar Tabs |

---

## 🧪 Testing & Quality Assurance

Klydis features a comprehensive automated test suite ensuring model synchronization, zero UI thread blocking during VRAM cleanup, and thread-safe cancellation:

```bash
# Run all unit and empirical stress tests
dotnet test tests/Klydis.Core.Tests/Klydis.Core.Tests.csproj
```

---

## 🤝 Contributing

Contributions, bug reports, and feature requests are welcome!
- Feel free to open an issue on the [GitHub Issues](https://github.com/obsidian-pixel-backup/klydisbeta/issues) page.
- For documentation updates or prompting guides, visit [docs.klydis.com](https://docs.klydis.com).

---

<div align="center">
  <sub>Built with ❤️ by the Klydis team. Powered by C#, .NET 10, and a vendored LLamaSharp fork.</sub>
  <br>
  <sub>Official Website: <a href="https://klydis.co">klydis.co</a></sub>
</div>
