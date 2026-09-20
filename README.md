# LocalAIApp

An enterprise-grade, high-performance Windows desktop application prototype engineered for **Edge AI / Localized Intelligence** workloads. Built using `.NET 10 (WPF)`, this application provides an optimized desktop client interface that natively interfaces with a localized **Ollama RESTful server** running the **Microsoft Phi-3 (3.8B) Light Weight/Small Language Model (SLM)**. 

The core architecture is heavily optimized under strict system-level compute budgets and memory constraints, utilizing low-level network pipeline throttling and defensive programming to deliver real-time, production-ready AI streaming without exhausting critical host hardware resources.

---

## 🚀 Architectural Breakthroughs & Technical Highlights

This application addresses the common engineering pitfalls of Edge AI desktop clients (e.g., thermal throttling, high Time-to-First-Token latency, pipeline truncation, and zombie background server workloads) through meticulous system-level optimizations:

### 1. Time-to-First-Token (TTFT) Latency Tuning (`num_ctx` Optimization)
*   **The Pitfall**: Running localized SLMs on consumer hardware (such as laptops) with bloated default context windows drastically increases memory footprints and computational overhead, leading to sluggish initial token rendering and poor user experience.
*   **The Solution**: Explicitly injected core parameters into the inference request body by setting `num_ctx = 2048`. This optimizes the memory footprint to fit specialized display/color management domain queries perfectly, **reducing TTFT by several folds** and delivering near-instantaneous typography visualization.

### 2. Defensive Text Generation & Truncation Control (`num_predict`)
*   **The Pitfall**: Localized SLMs operating without discrete GPU acceleration or under heavy context pressure are prone to token hallucination, infinite loop rendering, or encoding corruption, which eventually triggers network socket timeout exceptions.
*   **The Solution**: The primary request uses `temperature = 0.0` (greedy decoding), so output is fully deterministic, reproducible, and auditable. If a stream ends abnormally — no completion signal received, and none of the known abort branches were triggered (see Section 8 below) — the request is automatically retried once with `temperature = 0.7`. Because greedy decoding is fully deterministic, retrying at the same temperature would only reproduce the exact same failure; introducing randomness gives the model a chance to follow a different generation path and avoid the token sequence that triggered the original failure.

### 3. High-Throughput Asynchronous Zero-Buffer Streaming
*   **The Solution**: Bypassed unstable, high-level third-party AI frameworks (eliminating runtime API protocol mismatch defects) to build a raw HTTP connection pool using `.NET 10 HttpClient` driven by `HttpCompletionOption.ResponseHeadersRead`. Paired with an optimized asynchronous line-by-line streaming buffer reader loop (`ReadLineAsync`), this design prevents Thread Starvation, delivering a seamless, lag-free localized typewriter rendering effect. The underlying stream can still fail for reasons outside the client's control (see Section 8), which the retry and defense layers below are designed to catch.

### 4. Deterministic Lifecycle Governance & Compute Guardrails (`CancellationToken`)
*   **The Pitfall**: Abruptly terminating the UI window during an active AI text stream often disconnects the HTTP socket but leaves the underlying inference process (`llama-server.exe`) running as an orphaned background entity, causing continuous CPU/GPU spikes, battery depletion, and thermal overhead.
*   **The Solution**: A `CancellationTokenSource` is bound directly into the WPF `Closed` lifecycle event. Closing the window immediately triggers cancellation, which severs the underlying HTTP stream connection right away, preventing the background inference request from continuing as an orphaned process.

### 5. Fluent State-Machine Asynchronous UI UX
*   **The Solution**: Employed thread-safe non-blocking state machine mechanisms. During the exact latency gap between dispatching the payload and parsing the first HTTP header, the engine dynamically flashes the WPF ProgressBar via `IsIndeterminate="True"`. The animation gracefully hides the moment the first streaming byte arrives, eliminating operational uncertainty and creating a smooth, responsive desktop UX.

### 6. Token Budget Calibration Against Empirical Ground Truth
*   **The Pitfall**: The token-budget circuit breaker (`ValidateTokenBudget`) estimates prompt size by running SharpToken's `cl100k_base` encoding over the fully assembled `finalPrompt` (including chat-template tags). `cl100k_base` is not Phi-3's native tokenizer, so this estimate is only an approximation and can systematically diverge from what the model actually consumes.
*   **The Solution**: Across 15 empirical samples (short prompts, long inputs, pure Chinese, pure English, and mixed Chinese/English), the ratio between Ollama's actual `prompt_eval_count` and the SharpToken estimate was measured to fall consistently between 1.1930 and 1.2140 (mean 1.2033, standard deviation 0.48%). A calibration factor of 1.22 — slightly above the observed maximum — is applied to the raw estimate before comparing it against the context budget, so the circuit breaker's safety margin is grounded in measured behavior rather than a theoretical token count.

### 7. Dynamic Tool Discovery via Reflection-Based Plugin Attributes
*   **The Pitfall**: The system prompt's list of callable tools (e.g., which hardware categories the model may query via WMI) was originally hand-written as fixed text, requiring a manual edit to `MainWindow.xaml.cs` every time a new tool or category was added.
*   **The Solution**: `AiPluginAttribute` now carries an optional `TagTemplate` (e.g. `"[[CALL_WMI:{0}]]"`) and `ValidValues` array alongside its existing `MethodName`/`Description`. `ToolDiscoveryService` uses reflection to scan an assembly, or a specific set of types, for methods marked `[AiPlugin]`, and renders the system prompt's tool list directly from what it finds — expanding `TagTemplate`/`ValidValues` into one line per valid value when both are present, or falling back to `Description` alone for tools that don't need a sub-category. Adding a new tool now only requires annotating its method; no prompt-assembly code needs to change.

### 8. Multi-Layer Defense Against Small Language Model Hallucination
*   **The Pitfall**: Phi-3 (3.8B) occasionally produces unpredictable output when handling ambiguous or complex input — reciting the system prompt's own structure back, inventing protocol tags that were never defined, or supplying parameters outside any tool's valid range.
*   **The Solution**: Three checks run in sequence against each accumulated response, in the order `StreamOnceAsync` actually evaluates them, followed by one loop-level guard that sits outside that sequence. Each corresponds to a specific case reproduced during testing, and each is logged to `stream_parse_debug.log` for later analysis:
    1.  **System-prompt leakage detection**: evaluated first, before any protocol parsing. The response is checked for markers from the system prompt's own section headers (e.g. `[核心規則]`, `[範例對齊]`). Their presence indicates the model is reciting the instructions themselves rather than answering the question, and the response is aborted immediately.
    2.  **Unknown protocol detection**: a regular expression (`\[\[([A-Z_]+):([^\]]*)\]\]`) matches any double-bracket protocol-shaped output; only the `CALL_WMI` protocol name is allowed through. Any protocol name the model invents on its own (e.g. the observed `[[DRIVERLOADED:SSD]]`) is safely intercepted before any tool is invoked.
    3.  **Category whitelist validation**: even when the protocol name is correct, the parsed category is checked against the `ValidValues` list defined on the corresponding `AiPluginAttribute` (guarding against cases like `[[CALL_WMI:COLOR_REPAIR]]`, where the protocol format is valid but the parameter itself is hallucinated).
    4.  **Read-timeout protection** *(loop-level, not part of the sequence above)*: each line read from the stream is bounded by an 8-second timeout. This guards the read loop itself rather than the parsed response content, preventing a silently hung connection from blocking the pipeline indefinitely.
*   **Root Cause Story**: One case of silent mid-stream truncation was traced by ruling out client-side cancellation logic, JSON parsing, protocol mis-triggering, and server cold-start timing, then isolating the Ollama HTTP API directly with `curl`, bypassing the application layer entirely. The truncation originated inside Ollama's chat-template parsing pipeline (`common_chat_peg_parse`), which aborts the generation task when the model emits an invalid UTF-8 byte sequence — producing a silent stream cutoff under an HTTP 200 status. This finding motivated the read-timeout and retry design described above.

---

## 🛠️ System Components & Architecture Environment

*   **Runtime UI Architecture**: .NET 10.0-windows (WPF Core Desktop Platform)
*   **Local AI Inference Engine**: Ollama for Windows (RESTful Localhost Listening on `http://localhost:11434`)
*   **Deployed Model Family**: Microsoft Phi-3 (3.8B Lightweight / Small Language Model)
*   **System Prompt Configuration**:
    > *"You are a senior software architect specializing in monitor color calibration, ICC Profile mapping, and low-level hardware control. Please answer the user's questions in concise, traditional Chinese."*

---

## 📂 Getting Started & Local Execution

### 1. Launch the Localized SLM Engine
Ensure you have the native Ollama environment installed on your Windows machine. Initialize the core model through your command prompt (CMD/PowerShell):
```bash
ollama run phi3
```

### 2. Clone & Compile the Repository
```bash
# Clone the repository
git clone https://github.com
cd LocalAIApp

# Purge cache and restore dependencies
dotnet clean
dotnet restore

# Run the high-performance desktop client
dotnet run
```