---
name: design-patterns-gof
description: Implementing classic Gang of Four (GoF) structural, creational, and behavioral design patterns in modern TypeScript, Python, and C#.
category: Development & Architecture
author: Klydis Team
version: 2.0.0
---

# Gang of Four (GoF) Design Patterns

Design patterns provide battle-tested solutions to common object-oriented and structural software architecture challenges.

## Pattern Classification Summary

| Category | Patterns | Use Case |
| :--- | :--- | :--- |
| **Creational** | Factory Method, Abstract Factory, Builder, Singleton, Prototype | Decoupling object creation logic from usage |
| **Structural** | Adapter, Decorator, Facade, Composite, Proxy, Strategy | Organizing relationships between classes and objects |
| **Behavioral** | Observer, Command, State, Chain of Responsibility, Strategy | Managing communication and state flows between objects |

---

## Essential Patterns & Code Blueprints

### 1. Builder Pattern (Creational)
Construct complex objects step-by-step with clean validation.

```typescript
class HTTPRequest {
  constructor(
    public readonly url: string,
    public readonly method: string,
    public readonly headers: Record<string, string>,
    public readonly body?: string
  ) {}
}

class HTTPRequestBuilder {
  private url: string = "";
  private method: string = "GET";
  private headers: Record<string, string> = {};
  private body?: string;

  setURL(url: string): this { this.url = url; return this; }
  setMethod(method: string): this { this.method = method; return this; }
  setHeader(key: string, value: string): this { this.headers[key] = value; return this; }
  setBody(body: string): this { this.body = body; return this; }

  build(): HTTPRequest {
    if (!this.url) throw new Error("URL is required");
    return new HTTPRequest(this.url, this.method, this.headers, this.body);
  }
}
```

### 2. Strategy Pattern (Behavioral)
Swap algorithms at runtime without changing caller code.

```typescript
interface PaymentStrategy {
  pay(amount: number): Promise<boolean>;
}

class StripePayment implements PaymentStrategy {
  async pay(amount: number): Promise<boolean> {
    console.log(`Processing $${amount} via Stripe API...`);
    return true;
  }
}

class PayPalPayment implements PaymentStrategy {
  async pay(amount: number): Promise<boolean> {
    console.log(`Processing $${amount} via PayPal API...`);
    return true;
  }
}
```

---

## Verification Checklist

- [ ] Choose patterns that simplify architecture rather than over-engineering simple problems.
- [ ] Patterns are implemented idiomatically for the target language (e.g., functions over class singletons in JS/Python).
- [ ] Concrete pattern implementations are hidden behind clear interface definitions.
- [ ] Unit tests verify behavioral behavior for all strategy/adapter variants.
