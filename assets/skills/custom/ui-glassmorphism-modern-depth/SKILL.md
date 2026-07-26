---
name: ui-glassmorphism-modern-depth
description: Designing glassmorphism UI depth, backdrop filters, translucent layering, subtle borders, ambient shadows, and modern visual elevation.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Glassmorphism & Modern Depth Systems

Glassmorphism and modern elevation systems create visual hierarchy by layering translucent frosted glass cards over dynamic backgrounds.

## Glassmorphism Core CSS Recipe

```css
.glass-card {
  /* 1. Translucent background fill */
  background: rgba(15, 23, 42, 0.65);

  /* 2. Backdrop blur filter for frosted effect */
  backdrop-filter: blur(16px) saturate(180%);
  -webkit-backdrop-filter: blur(16px) saturate(180%);

  /* 3. Subtle highlight border */
  border: 1px solid rgba(255, 255, 255, 0.12);

  /* 4. Ambient depth shadow */
  box-shadow: 0 20px 40px -15px rgba(0, 0, 0, 0.5),
              inset 0 1px 0 0 rgba(255, 255, 255, 0.1);
  
  border-radius: 16px;
}
```

---

## Depth & Elevation Layering Scale

```css
:root {
  --elevation-flat: none;
  --elevation-low: 0 2px 8px -2px rgba(0, 0, 0, 0.2);
  --elevation-medium: 0 12px 24px -6px rgba(0, 0, 0, 0.3);
  --elevation-high: 0 24px 48px -12px rgba(0, 0, 0, 0.5);
}
```

---

## Verification Checklist

- [ ] Backdrop blur filters fall back gracefully on browsers lacking filter support.
- [ ] Translucent glass cards maintain sufficient text readability and contrast.
- [ ] Layered card borders use subtle opacity highlights (`rgba(255,255,255,0.12)`).
