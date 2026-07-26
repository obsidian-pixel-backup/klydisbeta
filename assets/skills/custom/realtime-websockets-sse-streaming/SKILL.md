---
name: realtime-websockets-sse-streaming
description: Architecting real-time web applications: WebSockets, Server-Sent Events (SSE), HTTP streaming, auto-reconnection handling, and heartbeat protocols.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Real-Time Web: WebSockets & Server-Sent Events (SSE)

Real-time applications stream live updates (chat, AI response streaming, notification feeds) between server and clients efficiently.

## Protocol Comparison

- **WebSockets (`ws://`, `wss://`)**: Full-duplex, bi-directional socket communication over single TCP connection. Best for multi-user chat, collaborative whiteboards.
- **Server-Sent Events (SSE)**: Monodirectional server-to-client HTTP streaming protocol. Best for LLM token streaming, live sports scores, progress indicators.

---

## Server-Sent Events (SSE) Implementation Blueprint

### Node.js SSE Express Route
```typescript
import { Request, Response } from 'express';

export function streamAIResponse(req: Request, res: Response) {
  res.setHeader('Content-Type', 'text/event-stream');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'keep-alive');

  let tokenCount = 0;
  const interval = setInterval(() => {
    tokenCount++;
    res.write(`data: ${JSON.stringify({ token: `chunk_${tokenCount}` })}\n\n`);
    if (tokenCount >= 5) {
      res.write('data: [DONE]\n\n');
      clearInterval(interval);
      res.end();
    }
  }, 300);

  req.on('close', () => clearInterval(interval));
}
```

### Client SSE Consumer (JavaScript)
```typescript
const eventSource = new EventSource('/api/stream');

eventSource.onmessage = (event) => {
  if (event.data === '[DONE]') {
    eventSource.close();
    return;
  }
  const payload = JSON.parse(event.data);
  console.log('Received chunk:', payload.token);
};
```

---

## Verification Checklist

- [ ] WebSocket connections implement ping/pong heartbeat messages to detect dropped sockets.
- [ ] SSE endpoints send explicit `text/event-stream` headers and handle client disconnect events.
- [ ] Real-time clients implement exponential backoff auto-reconnection logic.
- [ ] Sensitive socket channels require authorization tokens upon handshake.
