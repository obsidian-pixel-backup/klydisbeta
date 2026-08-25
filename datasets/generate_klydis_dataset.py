import json
import random
import argparse
from datetime import datetime

MODES = ["conversation", "task", "autonomous"]
TOOL_PAIRS = [
    ("read_file", lambda i: {"path": f"/workspace/file_{i}.txt"}),
    ("write_file", lambda i: {"path": f"/workspace/output_{i}.txt","content": f"Generated content {i}"}),
    ("apply_patch", lambda i: {"path": f"/workspace/patch_{i}.diff","hunks": ["@@ -1 +1 @@\n-old\n+new"]}),
    ("run_command", lambda i: {"command": "dotnet test", "working_dir": "/workspace", "timeout_seconds": 120}),
    ("index_folder_rag", lambda i: {"path": f"/workspace/docs_{i}", "collection_name": f"docs_{i}"}),
    ("search_rag", lambda i: {"collection_name": f"docs_{i%10}", "query": "token budgeting", "top_k": 3}),
    ("store_memory", lambda i: {"key": f"pref_{i%5}", "value": "concise"}),
    ("recall_lessons", lambda i: {"query": "test timeout"}),
]

INTENTS = [
    "small-talk fallback",
    "research with web retrieval",
    "index a folder for RAG",
    "build a landing page",
    "apply patch to file",
    "edit file safely",
    "run unit tests and use results as evidence",
    "completion gating: plan must be empty",
    "RAG search for a snippet",
    "store and retrieve memory",
    "activate a skill for a task",
    "create custom tool",
    "list capabilities and enforce policy",
    "web crawl with stealth fallback",
    "create a plan step",
    "plan re-evaluation after stagnation",
    "list RAG collections",
    "safe-mode approval gate example",
    "record a lesson for future sessions",
    "recall lessons for context",
    "manage background process",
    "search skills and show details",
    "queue a steering message",
    "list models and load one",
    "speculative decoding disabled",
    "show the workbench state",
    "hardware-aware offload planning explanation",
    "handle tool output large offload",
]

TAGS_POOL = ["mode:autonomous","mode:task","mode:conversation","tool:file","tool:rag","skill","memory","verification","policy","failure","repair","evidence","rag","skills","process","web","test"]


def make_example(i):
    mode = random.choice(MODES)
    difficulty = random.choice(["easy","medium","hard"])
    intent = random.choice(INTENTS)
    user_message = ""
    context = None
    tool_call = None
    expected_post_state = {"tools_called": []}
    expected_action = None
    expected_output = None

    if mode == "conversation":
        user_message = random.choice(["good evening","what's the weather","explain token budgeting","how do I use Klydis?"])
        expected_action = "classify:Conversation; no tools; short friendly reply"
        expected_output = "Good evening — how can I help you with Klydis today?"
    elif mode == "task":
        intent = random.choice(INTENTS)
        user_message = f"Please do task {i}: {intent}"
        # 60% chance of calling a tool
        if random.random() < 0.6:
            name, args_fn = random.choice(TOOL_PAIRS)
            tool_call = {"name": name, "args": args_fn(i)}
            expected_action = f"classify:Task; call tool: {name}"
            expected_post_state = {"tools_called": [name]}
            expected_output = f"Called {name} with args"
        else:
            expected_action = "classify:Task; produce concise answer; no durable task created"
            expected_output = "Summary result"
    else:  # autonomous
        user_message = f"Autonomously: perform work unit {i} — implement feature {i%50}"
        # ensure autonomous outputs produce actions
        name, args_fn = random.choice(TOOL_PAIRS)
        tool_call = {"name": name, "args": args_fn(i)}
        expected_action = f"classify:Autonomous; create/continue durable task; execute {name}"
        expected_post_state = {"task_created": True, "plan_seeded": ["inspect","design","implement","verify"], "tools_called": [name]}
        expected_output = "Executing step and recording file change"

    tags = random.sample(TAGS_POOL, k=min(4, len(TAGS_POOL)))

    example = {
        "id": f"ex-{i:05d}",
        "mode": mode,
        "difficulty": difficulty,
        "tags": tags,
        "intent": intent,
        "user_message": user_message,
        "context": context,
        "expected_action": expected_action,
        "tool_call": tool_call,
        "expected_post_state": expected_post_state,
        "expected_output": expected_output,
        "notes": f"Auto-generated example {i}"
    }
    return example


def generate(output_path, count):
    with open(output_path, "w", encoding="utf-8") as f:
        for i in range(1, count+1):
            ex = make_example(i)
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Generate Klydis training JSONL dataset")
    parser.add_argument("--output", "-o", dest="output", default="datasets/klydis_training_10000.jsonl")
    parser.add_argument("--count", "-c", dest="count", type=int, default=10000)
    args = parser.parse_args()
    print(f"Generating {args.count} examples to {args.output} at {datetime.utcnow().isoformat()}Z")
    generate(args.output, args.count)
    print("Done. Compute SHA256 checksum if desired.")
