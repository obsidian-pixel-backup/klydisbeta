---
name: micro-frontend-architecture
description: Architecting Micro-Frontends: Module Federation, iframe isolation, single-spa framework, cross-application communication, and independent deployment.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Micro-Frontend Architecture

Micro-frontends decompose monolithic frontend web applications into independently deployable micro-apps owned by autonomous domain teams.

## Micro-Frontend Integration Patterns

- **Module Federation (Webpack 5 / Vite)**: Dynamic runtime sharing of JavaScript modules across separate builds.
- **Web Components (Custom Elements)**: Framework-agnostic UI encapsulation using Shadow DOM.
- **Single-SPA**: Orchestration framework routing URLs to distinct micro-frontend bundles.

---

## Webpack Module Federation Blueprint (`webpack.config.js`)

```javascript
// Host App Webpack Config
const ModuleFederationPlugin = require("webpack/lib/container/ModuleFederationPlugin");

module.exports = {
  plugins: [
    new ModuleFederationPlugin({
      name: "host_app",
      remotes: {
        checkout_app: "checkout_app@https://checkout.example.com/remoteEntry.js",
      },
      shared: { react: { singleton: true }, "react-dom": { singleton: true } },
    }),
  ],
};
```

---

## Verification Checklist

- [ ] Micro-frontends can be built and deployed independently without rebuilding host app.
- [ ] Shared dependencies (React, Vue) are configured as singletons to prevent loading duplicates.
- [ ] Global styling CSS rules are scoped to prevent bleeding across micro-apps.
- [ ] Cross-app communication relies on browser CustomEvents or window postMessage.
