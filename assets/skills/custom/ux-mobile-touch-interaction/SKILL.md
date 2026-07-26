---
name: ux-mobile-touch-interaction
description: Designing mobile-first touch UX: touch target sizes (min 44x44px), thumb-zone ergonomics, swipe gestures, haptic feedback triggers, and soft keyboard adaptation.
category: UX & User Experience
author: Klydis Team
version: 2.0.0
---

# UX Mobile Touch Interaction & Ergonomics

Designing for touch screens requires accounting for finger tap accuracy, thumb reach ergonomics, and mobile soft keyboard interactions.

## Key Mobile Touch Rules

1. **Minimum Touch Target Size**: Interactive buttons and links MUST measure at least $44\text{px} \times 44\text{px}$ to prevent mis-taps.
2. **Thumb-Zone Ergonomics**: Place primary action controls within comfortable thumb reach (bottom third of screen).
3. **Prevent Keyboard Occlusion**: Scroll form input fields smoothly above the soft keyboard when focused.

---

## Ergonomic Thumb-Zone Layout Blueprint

```
┌─────────────────────────────────────────┐
│ [Header / Title]        (Hard Reach)    │
├─────────────────────────────────────────┤
│                                         │
│ [Main Reading Content]  (Natural View)  │
│                                         │
├─────────────────────────────────────────┤
│ [Primary Action CTA]    (Easy Thumb)    │
│ [Bottom Navigation Bar] (Zone)          │
└─────────────────────────────────────────┘
```

---

## Verification Checklist

- [ ] All interactive buttons and link targets are at least $44\text{px}$ high/wide.
- [ ] Spacing between adjacent tap targets is at least $8\text{px}$.
- [ ] Critical action buttons reside in the comfortable lower screen region.
