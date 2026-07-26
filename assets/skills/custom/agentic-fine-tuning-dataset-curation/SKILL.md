---
name: agentic-fine-tuning-dataset-curation
description: Curating high-quality instruction tuning datasets for coding agents: multi-turn trajectory formatting, synthetic data generation, quality filtering, and deduplication.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic Fine-Tuning Dataset Curation

Building specialized LLMs for agentic software development requires high-precision instruction, tool-calling, and multi-turn trajectory dataset curation.

## Core Dataset Formatting Standards

1. **ShareGPT / ChatML Multi-Turn Schema**: Structure interactions into clean `system`, `user`, `assistant`, and `tool` role turns.
2. **Quality Filtering Rubrics**: Remove trajectories with broken syntaxes, repetitive loops, incomplete tool calls, or low-quality code edits.
3. **MinHash De-duplication**: Eliminate near-identical code snippets and prompt variations to prevent model overfitting.

---

## Instruction Trajectory Format Blueprint (JSONL)

```json
{
  "messages": [
    {
      "role": "system",
      "content": "You are Antigravity, an AI pair programmer. Use provided tools to edit files."
    },
    {
      "role": "user",
      "content": "Fix typo in src/utils.py line 12."
    },
    {
      "role": "assistant",
      "tool_calls": [
        {
          "id": "call_123",
          "type": "function",
          "function": {
            "name": "replace_file_content",
            "arguments": "{"TargetFile": "src/utils.py", "StartLine": 12, "EndLine": 12, "TargetContent": "teh", "ReplacementContent": "the"}"
          }
        }
      ]
    },
    {
      "role": "tool",
      "tool_call_id": "call_123",
      "content": "Successfully updated src/utils.py"
    },
    {
      "role": "assistant",
      "content": "Fixed the typo in `src/utils.py`."
    }
  ]
}
```

---

## Data Curation Quality Filters

- **Syntactic Validity**: Filter out trajectories where generated code fails parser checks (`ast.parse`).
- **Tool Integrity**: Ensure every `tool_calls` entry is answered by a corresponding `tool` result block.
- **Length Normalization**: Truncate oversized trajectories (>16k tokens) while preserving initial task prompt and final diff.

---

## Verification Checklist

- [ ] All JSONL samples conform to standard multi-turn ChatML / OpenAI schema.
- [ ] Syntactic validation passes for 100% of embedded code blocks.
- [ ] MinHash deduplication removes near-duplicate trajectories at threshold $>0.85$.
- [ ] Secret tokens, passwords, and personal identifiable information (PII) are completely redacted.
