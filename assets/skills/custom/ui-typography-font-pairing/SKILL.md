---
name: ui-typography-font-pairing
description: Mastering UI typography: dynamic type scale calculation, fluid typography (`clamp()`), font pairing rules, line-height ratios, and letter-spacing.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Typography & Font Pairing Systems

Typography establishes structure, legibility, and tone in digital interfaces.

## Dynamic Modular Type Scale

Calculate font sizes using a $1.25$ Major Third modular scale ratio:

| Scale Step | Token | Size (px) | Line Height | Letter Spacing |
| :--- | :--- | :--- | :--- | :--- |
| **Display Header** | `font-display` | $48\text{px}$ | $1.1$ | $-0.02\text{em}$ |
| **Heading 1** | `font-h1` | $36\text{px}$ | $1.2$ | $-0.015\text{em}$ |
| **Heading 2** | `font-h2` | $28\text{px}$ | $1.25$ | $-0.01\text{em}$ |
| **Heading 3** | `font-h3` | $22\text{px}$ | $1.3$ | $0\text{em}$ |
| **Body Regular** | `font-body` | $16\text{px}$ | $1.5$ | $0\text{em}$ |
| **Caption / Small**| `font-small` | $13\text{px}$ | $1.4$ | $+0.01\text{em}$ |

---

## Modern Fluid Typography CSS (`clamp()`)

```css
h1 {
  /* Fluid font scaling: min 32px, preferred 4vw, max 56px */
  font-size: clamp(2rem, 4vw + 1rem, 3.5rem);
  line-height: 1.15;
  letter-spacing: -0.02em;
  font-weight: 700;
}
```

---

## Verification Checklist

- [ ] Body line-height is set between $1.4 - 1.6$ for maximum legibility.
- [ ] Heading typography uses slightly tight tracking (`letter-spacing: -0.01em`).
- [ ] Font pairings combine a clean sans-serif (Inter, Roboto) with an expressive display font.
