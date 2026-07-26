---
name: agentic-rag-pipeline-architecture
description: Architecting retrieval-augmented generation (RAG) systems for codebases: chunking strategies, hybrid search (BM25 + vector embeddings), re-ranking, and context synthesis.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic RAG Pipeline Architecture

Retrieval-Augmented Generation (RAG) enables coding agents to search through large repositories, documentation sets, and historical logs without overloading context limits.

## Core Architectural Components

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│ Code & Markdown │───>│ AST/Semantic     │───>│ Vector + BM25   │
│ Ingestion       │    │ Chunking         │    │ Hybrid Index    │
└─────────────────┘    └──────────────────┘    └─────────────────┘
                                                        │
┌─────────────────┐    ┌──────────────────┐             ▼
│ Prompt Context  │<───│ Cross-Encoder    │<───[ High-Recall    ]
│ Synthesis       │    │ Re-Ranker        │    [ Candidate Retrieval]
└─────────────────┘    └──────────────────┘
```

---

## 1. Codebase Chunking Strategies

- **AST-Aware Code Chunking**: Split code at class, function, or module boundaries rather than arbitrary token counts.
- **Overlapping Markdown Chunking**: For documentation, use 500-token chunks with 50-token overlap, preserving heading context headers.
- **Metadata Tagging**: Attach metadata attributes (`file_path`, `language`, `start_line`, `end_line`, `git_commit`) to every chunk vector.

### AST Chunking Example (Python / Tree-Sitter)
```python
def chunk_python_code(code_str: str, file_path: str):
    tree = parser.parse(bytes(code_str, "utf8"))
    chunks = []
    for node in tree.root_node.children:
        if node.type in ["function_definition", "class_definition"]:
            snippet = code_str[node.start_byte:node.end_byte]
            chunks.append({
                "file": file_path,
                "type": node.type,
                "start_line": node.start_point[0] + 1,
                "end_line": node.end_point[0] + 1,
                "content": snippet
            })
    return chunks
```

---

## 2. Hybrid Search & Re-Ranking

Combine dense vector embeddings (semantic search) with sparse BM25 search (exact symbol/identifier matching) using Reciprocal Rank Fusion (RRF):

$$RRF\_Score(d) = \sum_{m \in M} rac{1}{k + r_m(d)}$$

Where $k pprox 60$, and $r_m(d)$ is the rank of document $d$ in retrieval method $m$.

Follow hybrid retrieval with a Cross-Encoder Re-Ranker (e.g., BGE-Reranker) to extract top 5-10 highest precision context windows.

---

## Common Pitfalls & Solutions

- **Lost in the Middle**: LLMs ignore context in the middle of giant prompts $
ightarrow$ Place top-ranked chunks at the extreme top and bottom of the context window.
- **Stale Index**: Vector index out of sync with workspace modifications $
ightarrow$ Implement real-time file watcher index updates for modified files.

---

## Verification & Evaluation Checklist

- [ ] Chunking logic preserves complete function definitions and docstrings.
- [ ] Exact symbol searches (e.g., `process_payment_v2`) successfully return exact matches using sparse BM25.
- [ ] Re-ranker filters out irrelevant boilerplates (e.g., imports, package locks).
- [ ] Retrieved contexts include file path and line numbers for precise citation.
