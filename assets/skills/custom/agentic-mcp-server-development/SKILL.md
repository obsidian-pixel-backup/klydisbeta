---
name: agentic-mcp-server-development
description: Building Model Context Protocol (MCP) servers: registering tool interfaces, exposing dynamic resources, prompt templates, SSE/stdio transports, and client connections.
category: Agentic AI & Subagents
author: Klydis Team
version: 2.0.0
---

# Agentic MCP Server Development

Model Context Protocol (MCP) provides an open standard for connecting AI agents to external context sources, tools, and services securely.

## Core Architectural Components

```
┌────────────────┐           MCP Protocol           ┌────────────────┐
│  AI Agent      │ <==============================> │  MCP Server    │
│  Client        │    Stdio / SSE Transport         │  (Tools/Docs)  │
└────────────────┘                                  └────────────────┘
```

1. **Resources**: Read-only data sources exposed to the agent (e.g., log streams, DB schemas, docs).
2. **Tools**: Executable functions callable by the agent with JSON schema parameters.
3. **Prompts**: Pre-configured prompt templates exposed by the server.

---

## MCP Server Implementation Template (TypeScript Node SDK)

```typescript
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";

const server = new Server(
  { name: "git-history-mcp", version: "1.0.0" },
  { capabilities: { tools: {} } }
);

server.setRequestHandler(ListToolsRequestSchema, async () => ({
  tools: [
    {
      name: "get_recent_commits",
      description: "Returns the last N git commit summaries.",
      inputSchema: {
        type: "object",
        properties: {
          limit: { type: "number", default: 5 }
        }
      }
    }
  ]
}));

server.setRequestHandler(CallToolRequestSchema, async (request) => {
  if (request.params.name === "get_recent_commits") {
    const limit = request.params.arguments?.limit || 5;
    // Execute git command safely...
    return { content: [{ type: "text", text: `Retrieved ${limit} commits` }] };
  }
  throw new Error("Tool not found");
});

async function run() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}
run();
```

---

## Verification Checklist

- [ ] MCP Server implements stdio or SSE transport cleanly.
- [ ] All exposed tools pass strict JSON schema input validation.
- [ ] Tool execution errors return valid MCP error response objects.
- [ ] Resource URIs adhere to standard scheme formats (`file://`, `db://`).
