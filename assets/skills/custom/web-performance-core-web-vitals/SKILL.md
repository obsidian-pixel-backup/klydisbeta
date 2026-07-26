---
name: web-performance-core-web-vitals
description: Optimizing web performance and Core Web Vitals: LCP (Largest Contentful Paint), INP (Interaction to Next Paint), CLS (Cumulative Layout Shift), and bundle optimization.
category: Web & Full-Stack Architecture
author: Klydis Team
version: 2.0.0
---

# Web Performance & Core Web Vitals

Optimizing Core Web Vitals directly improves user retention, conversion rates, and search engine ranking.

## Core Web Vitals Thresholds (Google Standard)

| Metric | Full Name | Good Threshold | Target Area |
| :--- | :--- | :--- | :--- |
| **LCP** | Largest Contentful Paint | $\le 2.5\text{s}$ | Hero image loading, SSR response speed |
| **INP** | Interaction to Next Paint | $\le 200\text{ms}$ | Long JS tasks, main thread blocking |
| **CLS** | Cumulative Layout Shift | $\le 0.10$ | Un-sized images, dynamic font loading |

---

## Optimization Techniques Blueprint

### 1. Fixing Cumulative Layout Shift (CLS)
Always reserve layout dimensions for images, video elements, and ads:

```html
<!-- BAD: Unsized image causes layout shift when loaded -->
<img src="hero.jpg" alt="Hero Banner" />

<!-- GOOD: Explicit aspect-ratio or dimensions -->
<img src="hero.jpg" alt="Hero Banner" width="1200" height="600" style="aspect-ratio: 2/1;" />
```

### 2. Code Splitting & Dynamic Imports
Split large third-party bundles to improve initial JS execution time:

```typescript
// React dynamic import for heavy modal component
import dynamic from 'next/dynamic';

const HeavyChartModal = dynamic(() => import('@/components/HeavyChartModal'), {
  ssr: false,
  loading: () => <p>Loading chart visualization...</p>
});
```

---

## Verification Checklist

- [ ] Hero images feature `priority` or `preload` tags to optimize LCP.
- [ ] Images have explicit `width` and `height` attributes to prevent CLS.
- [ ] Bundle size analyzer verifies initial JS payload stays under 100KB gzipped.
- [ ] Fonts use `font-display: swap` or local preload strategy.
