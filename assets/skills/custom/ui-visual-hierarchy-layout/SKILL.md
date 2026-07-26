---
name: ui-visual-hierarchy-layout
description: Mastering UI visual hierarchy, grid systems, whitespace utilization, layout alignment, visual weight balance, and focal point design.
category: UI Design & Systems
author: Klydis Team
version: 2.0.0
---

# UI Visual Hierarchy & Layout Design

Visual hierarchy guides the user's eye seamlessly through an interface by manipulating size, visual weight, color contrast, whitespace, and visual order.

## Core Hierarchy Mechanisms

1. **Size & Scale**: Primary focal elements (Page Heading) must be visually larger than supporting body copy.
2. **Visual Weight & Contrast**: Bold, high-contrast CTA buttons pull immediate visual attention over subtle secondary actions.
3. **Whitespace & Proximity**: Group related elements closely together while using generous margins ($24\text{px}-64\text{px}$) to isolate visual sections.
4. **Grid Alignment**: Align elements along a disciplined 8pt grid system.

---

## 8pt Grid System Standard

```css
/* All spacing properties MUST be multiples of 8px (or 4px for micro-spacing) */
.card {
  padding: 24px;         /* 8 x 3 */
  margin-bottom: 32px;   /* 8 x 4 */
  gap: 16px;             /* 8 x 2 */
  border-radius: 12px;
}
```

---

## Verification Checklist

- [ ] Page layout has a single, unambiguous primary focal point (`<h1>` or primary CTA).
- [ ] Spacing values adhere strictly to an 8pt layout grid scale.
- [ ] Related elements are visually grouped using whitespace proximity.
